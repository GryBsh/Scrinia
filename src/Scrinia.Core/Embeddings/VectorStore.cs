using System.Collections.Concurrent;
using Scrinia.Core.Embeddings.Models;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// Per-scope binary vector storage with append-only SVF2 format.
///
/// SVF1 (legacy, read-only):
///   [magic "SVF1" 4B] [dimensions uint16] [count uint32]
///   then count entries: [nameLen uint16] [nameUtf8] [chunkIndex int32 (-1 = null)] [vector float32[dims]]
///
/// SVF2 (append-only, current):
///   [magic "SVF2" 4B] [dimensions uint16]
///   then appendable entries:
///     [op byte: 0=add, 1=delete] [nameLen uint16] [nameUtf8] [chunkIndex int32 (-1 = null)]
///     (for add only: [vector float32[dims]])
///   Compaction triggered when deletes exceed 20% of total operations.
///
/// Ephemeral scope vectors are stored in-memory only.
/// Persistent scopes write to {baseDir}/{scope}/vectors.bin with atomic writes for full rewrites
/// and direct append for single-entry upserts.
/// </summary>
public sealed class VectorStore : IDisposable
{
    private static readonly byte[] MagicSvf1 = "SVF1"u8.ToArray();
    private static readonly byte[] MagicSvf2 = "SVF2"u8.ToArray();
    private readonly string _baseDir;
    private readonly ConcurrentDictionary<string, List<VectorEntry>> _scopeVectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _scopeLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _scopeDeleteCount = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _scopeOpCount = new(StringComparer.OrdinalIgnoreCase);

    public VectorStore(string baseDir)
    {
        _baseDir = baseDir;
    }

    private SemaphoreSlim GetLock(string scope) =>
        _scopeLocks.GetOrAdd(scope, _ => new SemaphoreSlim(1, 1));

    /// <summary>Loads vectors for a scope (from disk if persistent, from cache if already loaded).</summary>
    public IReadOnlyList<VectorEntry> GetVectors(string scope)
    {
        if (_scopeVectors.TryGetValue(scope, out var cached))
            return cached;

        var lk = GetLock(scope);
        lk.Wait();
        try
        {
            // Double-check after lock
            if (_scopeVectors.TryGetValue(scope, out cached))
                return cached;

            var loaded = LoadFromDisk(scope);
            _scopeVectors[scope] = loaded;
            return loaded;
        }
        finally
        {
            lk.Release();
        }
    }

    /// <summary>Adds or updates vectors for a named memory in a scope.</summary>
    public async Task UpsertAsync(string scope, string name, int? chunkIndex, float[] vector, CancellationToken ct = default)
    {
        var lk = GetLock(scope);
        await lk.WaitAsync(ct);
        try
        {
            var vectors = _scopeVectors.GetOrAdd(scope, _ => LoadFromDisk(scope));

            // Check if we're replacing an existing entry
            bool hadExisting = vectors.RemoveAll(v => v.Name == name && v.ChunkIndex == chunkIndex) > 0;
            vectors.Add(new VectorEntry(name, chunkIndex, vector));

            // Persist if not ephemeral
            if (!scope.Equals("ephemeral", StringComparison.OrdinalIgnoreCase))
            {
                string path = GetFilePath(scope);
                if (File.Exists(path) && IsSvf2(path))
                {
                    // SVF2: append operations instead of full rewrite
                    if (hadExisting)
                        await AppendDeleteOpAsync(path, name, chunkIndex, ct);
                    await AppendAddOpAsync(path, name, chunkIndex, vector, ct);

                    // Track ops for compaction
                    int ops = _scopeOpCount.AddOrUpdate(scope, 1, (_, v) => v + 1);
                    int deletes = hadExisting
                        ? _scopeDeleteCount.AddOrUpdate(scope, 1, (_, v) => v + 1)
                        : _scopeDeleteCount.GetOrAdd(scope, 0);

                    if (ops > 0 && (double)deletes / ops > 0.2 && deletes >= 10)
                        await CompactAsync(scope, vectors, ct);
                }
                else
                {
                    // First write or migration from SVF1: full rewrite as SVF2
                    await SaveAsSvf2Async(scope, vectors, ct);
                }
            }
        }
        finally
        {
            lk.Release();
        }
    }

    /// <summary>Removes all vectors for a named memory in a scope.</summary>
    public async Task RemoveAsync(string scope, string name, CancellationToken ct = default)
    {
        var lk = GetLock(scope);
        await lk.WaitAsync(ct);
        try
        {
            if (!_scopeVectors.TryGetValue(scope, out var vectors))
                return;

            var removed = vectors.Where(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            int removedCount = vectors.RemoveAll(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (removedCount > 0 && !scope.Equals("ephemeral", StringComparison.OrdinalIgnoreCase))
            {
                string path = GetFilePath(scope);
                if (File.Exists(path) && IsSvf2(path))
                {
                    foreach (var entry in removed)
                        await AppendDeleteOpAsync(path, entry.Name, entry.ChunkIndex, ct);

                    _scopeDeleteCount.AddOrUpdate(scope, removedCount, (_, v) => v + removedCount);
                    _scopeOpCount.AddOrUpdate(scope, removedCount, (_, v) => v + removedCount);
                }
                else
                {
                    await SaveAsSvf2Async(scope, vectors, ct);
                }
            }
        }
        finally
        {
            lk.Release();
        }
    }

    /// <summary>Returns the count of vectors across all loaded scopes.</summary>
    public int TotalVectorCount() =>
        _scopeVectors.Values.Sum(v => v.Count);

    private string GetFilePath(string scope)
    {
        string safeScope = scope.Replace(':', '_').Replace('/', '_').Replace('\\', '_');
        return Path.Combine(_baseDir, safeScope, "vectors.bin");
    }

    private static bool IsSvf2(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] magic = new byte[4];
            return fs.Read(magic, 0, 4) == 4 && magic.AsSpan().SequenceEqual(MagicSvf2);
        }
        catch { return false; }
    }

    /// <summary>Loads vectors from SVF1 or SVF2 format.</summary>
    internal List<VectorEntry> LoadFromDisk(string scope)
    {
        string path = GetFilePath(scope);
        if (!File.Exists(path))
            return [];

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs);

            byte[] magic = reader.ReadBytes(4);

            if (magic.AsSpan().SequenceEqual(MagicSvf1))
                return LoadSvf1(reader);

            if (magic.AsSpan().SequenceEqual(MagicSvf2))
                return LoadSvf2(reader);

            return [];
        }
        catch
        {
            return [];
        }
    }

    private static List<VectorEntry> LoadSvf1(BinaryReader reader)
    {
        ushort dims = reader.ReadUInt16();
        uint count = reader.ReadUInt32();

        var entries = new List<VectorEntry>((int)count);
        for (uint i = 0; i < count; i++)
        {
            ushort nameLen = reader.ReadUInt16();
            string name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(nameLen));
            int chunkIdx = reader.ReadInt32();
            int? chunkIndex = chunkIdx == -1 ? null : chunkIdx;

            float[] vector = new float[dims];
            for (int d = 0; d < dims; d++)
                vector[d] = reader.ReadSingle();

            entries.Add(new VectorEntry(name, chunkIndex, vector));
        }

        return entries;
    }

    private static List<VectorEntry> LoadSvf2(BinaryReader reader)
    {
        ushort dims = reader.ReadUInt16();
        var entries = new Dictionary<string, VectorEntry>(StringComparer.OrdinalIgnoreCase);

        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            byte op = reader.ReadByte();
            ushort nameLen = reader.ReadUInt16();
            string name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(nameLen));
            int chunkIdx = reader.ReadInt32();
            int? chunkIndex = chunkIdx == -1 ? null : chunkIdx;
            string key = $"{name}|{chunkIdx}";

            if (op == 0) // Add
            {
                float[] vector = new float[dims];
                for (int d = 0; d < dims; d++)
                    vector[d] = reader.ReadSingle();
                entries[key] = new VectorEntry(name, chunkIndex, vector);
            }
            else // Delete
            {
                entries.Remove(key);
            }
        }

        return entries.Values.ToList();
    }

    /// <summary>Full rewrite in SVF2 format (used for initial write and compaction).</summary>
    private async Task SaveAsSvf2Async(string scope, List<VectorEntry> vectors, CancellationToken ct)
    {
        string path = GetFilePath(scope);
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        string tmp = $"{path}.{Environment.ProcessId}.tmp";

        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var writer = new BinaryWriter(fs))
        {
            writer.Write(MagicSvf2);
            ushort dims = vectors.Count > 0 ? (ushort)vectors[0].Vector.Length : (ushort)0;
            writer.Write(dims);

            foreach (var entry in vectors)
            {
                writer.Write((byte)0); // Add op
                byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(entry.Name);
                writer.Write((ushort)nameBytes.Length);
                writer.Write(nameBytes);
                writer.Write(entry.ChunkIndex ?? -1);
                foreach (float f in entry.Vector)
                    writer.Write(f);
            }
        }

        File.Move(tmp, path, overwrite: true);

        _scopeDeleteCount[scope] = 0;
        _scopeOpCount[scope] = vectors.Count;
    }

    /// <summary>Appends a single add operation to an SVF2 file.</summary>
    private static async Task AppendAddOpAsync(string path, string name, int? chunkIndex, float[] vector, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
        await using var writer = new BinaryWriter(fs);

        writer.Write((byte)0); // Add
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        writer.Write((ushort)nameBytes.Length);
        writer.Write(nameBytes);
        writer.Write(chunkIndex ?? -1);
        foreach (float f in vector)
            writer.Write(f);
    }

    /// <summary>Appends a single delete operation to an SVF2 file.</summary>
    private static async Task AppendDeleteOpAsync(string path, string name, int? chunkIndex, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
        await using var writer = new BinaryWriter(fs);

        writer.Write((byte)1); // Delete
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        writer.Write((ushort)nameBytes.Length);
        writer.Write(nameBytes);
        writer.Write(chunkIndex ?? -1);
    }

    /// <summary>Compacts an SVF2 file by rewriting only live entries.</summary>
    private async Task CompactAsync(string scope, List<VectorEntry> vectors, CancellationToken ct)
    {
        await SaveAsSvf2Async(scope, vectors, ct);
    }

    public void Dispose()
    {
        foreach (var lk in _scopeLocks.Values)
            lk.Dispose();
    }
}
