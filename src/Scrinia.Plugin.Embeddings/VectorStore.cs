using System.Collections.Concurrent;
using Scrinia.Plugin.Embeddings.Models;

namespace Scrinia.Plugin.Embeddings;

/// <summary>
/// Per-scope binary vector storage.
///
/// File format (SVF1):
///   [magic "SVF1" 4B] [dimensions uint16] [count uint32]
///   then count entries: [nameLen uint16] [nameUtf8] [chunkIndex int32 (-1 = null)] [vector float32[dims]]
///
/// Ephemeral scope vectors are stored in-memory only.
/// Persistent scopes write to {baseDir}/{scope}/vectors.bin with atomic writes (.tmp → rename).
/// </summary>
public sealed class VectorStore : IDisposable
{
    private static readonly byte[] Magic = "SVF1"u8.ToArray();
    private readonly string _baseDir;
    private readonly ConcurrentDictionary<string, List<VectorEntry>> _scopeVectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _scopeLocks = new(StringComparer.OrdinalIgnoreCase);

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

            // Remove existing entry with same name+chunk
            vectors.RemoveAll(v => v.Name == name && v.ChunkIndex == chunkIndex);
            vectors.Add(new VectorEntry(name, chunkIndex, vector));

            // Persist if not ephemeral
            if (!scope.Equals("ephemeral", StringComparison.OrdinalIgnoreCase))
                await SaveToDiskAsync(scope, vectors, ct);
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

            int removed = vectors.RemoveAll(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed > 0 && !scope.Equals("ephemeral", StringComparison.OrdinalIgnoreCase))
                await SaveToDiskAsync(scope, vectors, ct);
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

    private List<VectorEntry> LoadFromDisk(string scope)
    {
        string path = GetFilePath(scope);
        if (!File.Exists(path))
            return [];

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs);

            // Validate magic
            byte[] magic = reader.ReadBytes(4);
            if (!magic.AsSpan().SequenceEqual(Magic))
                return [];

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
        catch
        {
            return [];
        }
    }

    private async Task SaveToDiskAsync(string scope, List<VectorEntry> vectors, CancellationToken ct)
    {
        string path = GetFilePath(scope);
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";

        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var writer = new BinaryWriter(fs))
        {
            writer.Write(Magic);

            ushort dims = vectors.Count > 0 ? (ushort)vectors[0].Vector.Length : (ushort)0;
            writer.Write(dims);
            writer.Write((uint)vectors.Count);

            foreach (var entry in vectors)
            {
                byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(entry.Name);
                writer.Write((ushort)nameBytes.Length);
                writer.Write(nameBytes);
                writer.Write(entry.ChunkIndex ?? -1);

                foreach (float f in entry.Vector)
                    writer.Write(f);
            }
        }

        File.Move(tmp, path, overwrite: true);
    }

    public void Dispose()
    {
        foreach (var lk in _scopeLocks.Values)
            lk.Dispose();
    }
}
