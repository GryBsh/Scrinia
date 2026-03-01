using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Core;

/// <summary>
/// Instance-based <see cref="IMemoryStore"/> backed by the filesystem.
/// Each instance is scoped to a workspace root directory.
///
/// Naming convention:
///   "subject"              → local scope:   {workspace}/.scrinia/store/subject.nmp2
///   "topic:subject"        → local topic:   {workspace}/.scrinia/topics/topic/subject.nmp2
///   "~subject"             → ephemeral:     in-memory only (dies with instance)
/// </summary>
public sealed partial class FileMemoryStore : IMemoryStore
{
    private readonly string _workspaceRoot;
    private readonly ConcurrentDictionary<string, EphemeralEntry> _ephemeralStore = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _indexLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions;

    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(IndexFile))]
    private partial class FileStoreJsonContext : JsonSerializerContext;

    public FileMemoryStore(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = FileStoreJsonContext.Default,
        };
    }

    // ── Naming ───────────────────────────────────────────────────────────────

    public (string Scope, string Subject) ParseQualifiedName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be empty.", nameof(name));

        int colonIdx = name.IndexOf(':');
        if (colonIdx < 0)
            return ("local", SanitizeName(name.Trim()));

        string topic = name[..colonIdx].Trim();
        string subject = name[(colonIdx + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(topic))
            throw new ArgumentException($"Topic part must not be empty in '{name}'.", nameof(name));
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException($"Subject part must not be empty in '{name}'.", nameof(name));

        return ($"local-topic:{SanitizeName(topic)}", SanitizeName(subject));
    }

    public string FormatQualifiedName(string scope, string subject)
    {
        if (scope.StartsWith("local-topic:", StringComparison.Ordinal))
            return $"{scope["local-topic:".Length..]}:{subject}";
        return subject;
    }

    public bool IsEphemeral(string name) =>
        !string.IsNullOrEmpty(name) && name[0] == '~';

    public string SanitizeName(string name)
    {
        // Strip directory separators and path traversal sequences first
        string safe = name.Replace("..", "").Replace('/', '_').Replace('\\', '_');

        // Remove remaining invalid filename characters
        char[] invalid = Path.GetInvalidFileNameChars();
        safe = string.Concat(safe.Select(c => invalid.Contains(c) ? '_' : c));

        // Final safety: extract only the filename component (blocks any residual path)
        safe = Path.GetFileName(safe);

        if (string.IsNullOrWhiteSpace(safe))
            throw new ArgumentException($"Name '{name}' is invalid after sanitization.", nameof(name));

        return safe;
    }

    // ── Ephemeral memory ─────────────────────────────────────────────────────

    [Obsolete("Use MemoryNaming.StripEphemeralPrefix instead.")]
    public static string StripEphemeralPrefix(string name) =>
        MemoryNaming.StripEphemeralPrefix(name);

    public void RememberEphemeral(string key, EphemeralEntry entry) =>
        _ephemeralStore[key] = entry;

    public bool ForgetEphemeral(string key) =>
        _ephemeralStore.TryRemove(key, out _);

    public EphemeralEntry? GetEphemeral(string key) =>
        _ephemeralStore.TryGetValue(key, out var entry) ? entry : null;

    public List<ScopedArtifact> ListEphemeral()
    {
        var result = new List<ScopedArtifact>();
        foreach (var kvp in _ephemeralStore)
        {
            var e = kvp.Value;
            var artifactEntry = new ArtifactEntry(
                Name: e.Name,
                Uri: "",
                OriginalBytes: e.OriginalBytes,
                ChunkCount: e.ChunkCount,
                CreatedAt: e.CreatedAt,
                Description: e.Description,
                Tags: e.Tags,
                ContentPreview: e.ContentPreview,
                Keywords: e.Keywords,
                TermFrequencies: e.TermFrequencies,
                UpdatedAt: e.UpdatedAt,
                ChunkEntries: e.ChunkEntries);
            result.Add(new ScopedArtifact("ephemeral", artifactEntry));
        }
        return result;
    }

    // ── Paths ────────────────────────────────────────────────────────────────

    public string GetStoreDirForScope(string scope)
    {
        if (scope == "local")
            return Path.Combine(_workspaceRoot, ".scrinia", "store");

        if (scope.StartsWith("local-topic:", StringComparison.Ordinal))
        {
            string topic = scope["local-topic:".Length..];
            return Path.Combine(_workspaceRoot, ".scrinia", "topics", topic);
        }

        throw new ArgumentException($"Unknown scope: {scope}");
    }

    public string ArtifactPath(string name, string scope = "local") =>
        Path.Combine(GetStoreDirForScope(scope), SanitizeName(name) + ".nmp2");

    public string ArtifactUri(string name, string scope = "local") =>
        $"file://{ArtifactPath(name, scope)}";

    public string FindArtifactPath(string subject, string normalizedScope) =>
        ArtifactPath(subject, normalizedScope);

    // ── Scope helpers ────────────────────────────────────────────────────────

    internal IReadOnlyList<string> NormalizeScopeFilters(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return ["local"];

        string s = token.Trim();
        if (s.Equals("local", StringComparison.OrdinalIgnoreCase)) return ["local"];
        // Ephemeral is handled separately in ListScoped — exclude from filesystem scopes
        if (s.Equals("ephemeral", StringComparison.OrdinalIgnoreCase)) return [];
        if (s.StartsWith("local-topic:", StringComparison.OrdinalIgnoreCase)) return [s];

        return [$"local-topic:{SanitizeName(s)}"];
    }

    public IReadOnlyList<string> ResolveReadScopes(string? scopes = null)
    {
        if (!string.IsNullOrWhiteSpace(scopes))
        {
            return scopes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(NormalizeScopeFilters)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var ordered = new List<string> { "local" };
        ordered.AddRange(DiscoverTopics());
        return ordered.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // ── CRUD ─────────────────────────────────────────────────────────────────

    private SemaphoreSlim GetIndexLock(string scope) =>
        _indexLocks.GetOrAdd(scope, _ => new SemaphoreSlim(1, 1));

    public List<ArtifactEntry> LoadIndex(string scope = "local")
    {
        var lk = GetIndexLock(scope);
        lk.Wait();
        try
        {
            return LoadIndexUnsafe(scope);
        }
        finally
        {
            lk.Release();
        }
    }

    private List<ArtifactEntry> LoadIndexUnsafe(string scope)
    {
        string storeDir = GetStoreDirForScope(scope);
        string indexPath = Path.Combine(storeDir, "index.json");
        if (!File.Exists(indexPath)) return [];

        try
        {
            string json = File.ReadAllText(indexPath);
            var idx = JsonSerializer.Deserialize<IndexFile>(json, _jsonOptions);
            return idx?.Entries ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void SaveIndex(List<ArtifactEntry> entries, string scope = "local")
    {
        var lk = GetIndexLock(scope);
        lk.Wait();
        try
        {
            SaveIndexUnsafe(entries, scope);
        }
        finally
        {
            lk.Release();
        }
    }

    private void SaveIndexUnsafe(List<ArtifactEntry> entries, string scope)
    {
        string storeDir = GetStoreDirForScope(scope);
        Directory.CreateDirectory(storeDir);

        var idx = new IndexFile { Entries = entries };
        string json = JsonSerializer.Serialize(idx, _jsonOptions);

        string indexPath = Path.Combine(storeDir, "index.json");
        string tmp = indexPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, indexPath, overwrite: true);
    }

    public void Upsert(ArtifactEntry entry, string scope = "local")
    {
        var lk = GetIndexLock(scope);
        lk.Wait();
        try
        {
            List<ArtifactEntry> entries = LoadIndexUnsafe(scope);
            int idx = entries.FindIndex(e => e.Name == entry.Name);
            if (idx >= 0)
                entries[idx] = entry;
            else
                entries.Add(entry);
            SaveIndexUnsafe(entries, scope);
        }
        finally
        {
            lk.Release();
        }
    }

    public bool Remove(string name, string scope = "local")
    {
        var lk = GetIndexLock(scope);
        lk.Wait();
        try
        {
            List<ArtifactEntry> entries = LoadIndexUnsafe(scope);
            int before = entries.Count;
            entries.RemoveAll(e => e.Name == name);
            if (entries.Count == before) return false;
            SaveIndexUnsafe(entries, scope);
            return true;
        }
        finally
        {
            lk.Release();
        }
    }

    // ── Artifact file I/O ────────────────────────────────────────────────────

    public async Task WriteArtifactAsync(string subject, string scope, string artifactText, CancellationToken ct = default)
    {
        string path = ArtifactPath(subject, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, artifactText, ct);
    }

    public async Task<string> ReadArtifactAsync(string subject, string scope, CancellationToken ct = default)
    {
        string path = ArtifactPath(subject, scope);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Artifact not found: {subject} in scope {scope}", path);
        return await File.ReadAllTextAsync(path, ct);
    }

    public bool DeleteArtifact(string subject, string scope)
    {
        string path = ArtifactPath(subject, scope);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    // ── Resolve ──────────────────────────────────────────────────────────────

    public async Task<string> ResolveArtifactAsync(string nameOrArtifact, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nameOrArtifact))
            throw new ArgumentException("Input must not be empty.", nameof(nameOrArtifact));

        // 1. Inline artifact
        if (nameOrArtifact.TrimStart().StartsWith("NMP/2 ", StringComparison.Ordinal))
            return nameOrArtifact;

        // 2. file:// URI (backward compat)
        if (nameOrArtifact.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            string filePath = nameOrArtifact[7..];
            return await File.ReadAllTextAsync(filePath, ct);
        }

        // 3. Ephemeral memory (~name)
        if (IsEphemeral(nameOrArtifact))
        {
            string key = MemoryNaming.StripEphemeralPrefix(nameOrArtifact);
            var entry = GetEphemeral(key);
            if (entry is null)
                throw new FileNotFoundException($"Ephemeral memory '~{key}' not found.");
            return entry.Artifact;
        }

        // 4. Qualified name resolution
        var (scope, subject) = ParseQualifiedName(nameOrArtifact);
        string path = FindArtifactPath(subject, scope);

        if (!File.Exists(path))
            throw new FileNotFoundException($"Memory '{nameOrArtifact}' not found.", path);

        return await File.ReadAllTextAsync(path, ct);
    }

    // ── Listing & Search ─────────────────────────────────────────────────────

    public List<ScopedArtifact> ListScoped(string? scopes = null)
    {
        var result = new List<ScopedArtifact>();

        bool includeEphemeral = string.IsNullOrWhiteSpace(scopes);
        if (!includeEphemeral && scopes is not null)
        {
            includeEphemeral = scopes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(s => s.Trim().Equals("ephemeral", StringComparison.OrdinalIgnoreCase));
        }

        if (includeEphemeral)
            result.AddRange(ListEphemeral());

        foreach (string scope in ResolveReadScopes(scopes))
        {
            foreach (var entry in LoadIndex(scope))
                result.Add(new ScopedArtifact(scope, entry));
        }
        return result;
    }

    public IReadOnlyList<SearchResult> SearchAll(string query, string? scopes = null, int limit = 20)
        => SearchAll(query, scopes, limit, supplementalScores: null);

    public IReadOnlyList<SearchResult> SearchAll(string query, string? scopes, int limit,
        IReadOnlyDictionary<string, double>? supplementalScores)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var searcher = new WeightedFieldScorer();
        var candidates = ListScoped(scopes);
        var topics = GatherTopicInfos(scopes);
        return searcher.SearchAll(query, candidates, topics, limit, supplementalScores);
    }

    // ── Copy & Archive ───────────────────────────────────────────────────────

    public bool CopyMemory(string sourceName, string destinationName, bool overwrite, out string message)
    {
        bool srcEphemeral = IsEphemeral(sourceName);
        bool dstEphemeral = IsEphemeral(destinationName);
        string srcKey = srcEphemeral ? MemoryNaming.StripEphemeralPrefix(sourceName) : sourceName;
        string dstKey = dstEphemeral ? MemoryNaming.StripEphemeralPrefix(destinationName) : destinationName;

        // ── Ephemeral source ─────────────────────────────────────────────
        if (srcEphemeral)
        {
            var entry = GetEphemeral(srcKey);
            if (entry is null)
            {
                message = $"Error: source memory '{sourceName}' was not found.";
                return false;
            }

            if (dstEphemeral)
            {
                if (srcKey.Equals(dstKey, StringComparison.OrdinalIgnoreCase))
                {
                    message = "Error: source and destination are the same.";
                    return false;
                }
                if (!overwrite && GetEphemeral(dstKey) is not null)
                {
                    message = $"Error: destination memory '{destinationName}' already exists. Set overwrite=true to replace it.";
                    return false;
                }
                RememberEphemeral(dstKey, entry with { Name = dstKey });
                message = $"Copied '{sourceName}' to '{destinationName}'.";
                return true;
            }

            // Ephemeral → Persistent (promotion)
            var (dstScope, dstSubject) = ParseQualifiedName(dstKey);
            string destPath = ArtifactPath(dstSubject, dstScope);
            if (File.Exists(destPath) && !overwrite)
            {
                message = $"Error: destination memory '{destinationName}' already exists. Set overwrite=true to replace it.";
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.WriteAllText(destPath, entry.Artifact);

            var destEntry = new ArtifactEntry(
                Name: dstSubject,
                Uri: ArtifactUri(dstSubject, dstScope),
                OriginalBytes: entry.OriginalBytes,
                ChunkCount: entry.ChunkCount,
                CreatedAt: DateTimeOffset.UtcNow,
                Description: entry.Description,
                Tags: entry.Tags,
                ContentPreview: entry.ContentPreview,
                ChunkEntries: entry.ChunkEntries);
            Upsert(destEntry, dstScope);
            message = $"Copied '{sourceName}' to '{destinationName}'.";
            return true;
        }

        // ── Persistent source ────────────────────────────────────────────
        var (srcScope, srcSubject) = ParseQualifiedName(srcKey);

        if (!dstEphemeral)
        {
            var (dstScopeCheck, dstSubjectCheck) = ParseQualifiedName(dstKey);
            if (srcScope.Equals(dstScopeCheck, StringComparison.OrdinalIgnoreCase)
                && srcSubject.Equals(dstSubjectCheck, StringComparison.OrdinalIgnoreCase))
            {
                message = "Error: source and destination are the same.";
                return false;
            }
        }

        string sourcePath = FindArtifactPath(srcSubject, srcScope);
        if (!File.Exists(sourcePath))
        {
            message = $"Error: source memory '{sourceName}' was not found.";
            return false;
        }

        if (dstEphemeral)
        {
            if (!overwrite && GetEphemeral(dstKey) is not null)
            {
                message = $"Error: destination memory '{destinationName}' already exists. Set overwrite=true to replace it.";
                return false;
            }

            string artifact = File.ReadAllText(sourcePath);
            ArtifactEntry? srcEntry = LoadIndex(srcScope).FirstOrDefault(e => e.Name == srcSubject);

            var ephEntry = new EphemeralEntry(
                Name: dstKey,
                Artifact: artifact,
                OriginalBytes: srcEntry?.OriginalBytes ?? 0,
                ChunkCount: srcEntry?.ChunkCount ?? Nmp2ChunkedEncoder.GetChunkCount(artifact),
                CreatedAt: DateTimeOffset.UtcNow,
                Description: srcEntry?.Description ?? $"Loaded from {sourceName}",
                Tags: srcEntry?.Tags,
                ContentPreview: srcEntry?.ContentPreview,
                ChunkEntries: srcEntry?.ChunkEntries);
            RememberEphemeral(dstKey, ephEntry);
            message = $"Copied '{sourceName}' to '{destinationName}'.";
            return true;
        }

        // Persistent → Persistent
        var (persistDstScope, persistDstSubject) = ParseQualifiedName(dstKey);
        string destPathP = ArtifactPath(persistDstSubject, persistDstScope);
        if (File.Exists(destPathP) && !overwrite)
        {
            message = $"Error: destination memory '{destinationName}' already exists. Set overwrite=true to replace it.";
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destPathP)!);
        File.Copy(sourcePath, destPathP, overwrite);

        ArtifactEntry? sourceEntry = LoadIndex(srcScope).FirstOrDefault(e => e.Name == srcSubject);
        ArtifactEntry destinationEntry;

        if (sourceEntry is not null)
        {
            destinationEntry = sourceEntry with
            {
                Name = persistDstSubject,
                Uri = ArtifactUri(persistDstSubject, persistDstScope),
                CreatedAt = DateTimeOffset.UtcNow
            };
        }
        else
        {
            string artifactText = File.ReadAllText(destPathP);
            int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifactText);
            long originalBytes = new Nmp2Strategy().Decode(artifactText).LongLength;

            destinationEntry = new ArtifactEntry(
                Name: persistDstSubject,
                Uri: ArtifactUri(persistDstSubject, persistDstScope),
                OriginalBytes: originalBytes,
                ChunkCount: chunkCount,
                CreatedAt: DateTimeOffset.UtcNow,
                Description: $"Copied from {sourceName}");
        }

        Upsert(destinationEntry, persistDstScope);
        message = $"Copied '{sourceName}' to '{destinationName}'.";
        return true;
    }

    public void ArchiveVersion(string subject, string scope = "local")
    {
        string currentPath = ArtifactPath(subject, scope);
        if (!File.Exists(currentPath))
            return;

        string storeDir = GetStoreDirForScope(scope);
        string versionsDir = Path.Combine(storeDir, "versions");
        Directory.CreateDirectory(versionsDir);

        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        string archiveName = $"{SanitizeName(subject)}_{timestamp}.nmp2";
        string archivePath = Path.Combine(versionsDir, archiveName);

        File.Copy(currentPath, archivePath, overwrite: true);
    }

    // ── Content utility ──────────────────────────────────────────────────────

    public string GenerateContentPreview(string content, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(content)) return "";
        string preview = content[..Math.Min(maxLength, content.Length)];
        return preview.Replace('\n', ' ').Replace('\r', ' ').Trim();
    }

    // ── Topic discovery ──────────────────────────────────────────────────────

    public string[] DiscoverTopics()
    {
        string localTopicsRoot = Path.Combine(_workspaceRoot, ".scrinia", "topics");
        if (!Directory.Exists(localTopicsRoot))
            return [];

        return Directory.GetDirectories(localTopicsRoot)
            .Select(d => $"local-topic:{Path.GetFileName(d)}")
            .ToArray();
    }

    public List<TopicInfo> GatherTopicInfos(string? scopes = null)
    {
        var topics = new List<TopicInfo>();
        foreach (string scope in ResolveReadScopes(scopes))
        {
            if (!scope.StartsWith("local-topic:", StringComparison.Ordinal))
                continue;

            string topicName = scope["local-topic:".Length..];
            var entries = LoadIndex(scope);
            if (entries.Count == 0) continue;
            topics.Add(new TopicInfo(
                Scope: scope,
                TopicName: topicName,
                EntryCount: entries.Count,
                Description: $"{topicName} ({entries.Count} {(entries.Count == 1 ? "entry" : "entries")})",
                Tags: null,
                EntryNames: entries.Select(e => e.Name).ToArray()));
        }
        return topics;
    }

    // ── Export/Import helpers ─────────────────────────────────────────────────

    public List<(string Name, string FilePath)> ListTopicArtifacts(string topicScope)
    {
        var result = new List<(string, string)>();
        var entries = LoadIndex(topicScope);
        string storeDir = GetStoreDirForScope(topicScope);
        foreach (var entry in entries)
        {
            string filePath = Path.Combine(storeDir, SanitizeName(entry.Name) + ".nmp2");
            if (File.Exists(filePath))
                result.Add((entry.Name, filePath));
        }
        return result;
    }

    public void ImportTopicEntries(string topicScope, List<ArtifactEntry> entries,
        Dictionary<string, string> artifactContents, bool overwrite)
    {
        string storeDir = GetStoreDirForScope(topicScope);
        Directory.CreateDirectory(storeDir);

        var existingEntries = LoadIndex(topicScope);

        foreach (var entry in entries)
        {
            bool exists = existingEntries.Any(e => e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
            if (exists && !overwrite)
                continue;

            if (artifactContents.TryGetValue(entry.Name, out string? content))
            {
                string filePath = Path.Combine(storeDir, SanitizeName(entry.Name) + ".nmp2");
                File.WriteAllText(filePath, content);
            }

            var updatedEntry = entry with
            {
                Uri = ArtifactUri(entry.Name, topicScope),
                CreatedAt = DateTimeOffset.UtcNow
            };

            int idx = existingEntries.FindIndex(e => e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                existingEntries[idx] = updatedEntry;
            else
                existingEntries.Add(updatedEntry);
        }

        SaveIndex(existingEntries, topicScope);
    }

    // ── Display helpers ──────────────────────────────────────────────────────

    [Obsolete("Use MemoryNaming.FormatScopeLabel instead.")]
    public static string FormatScopeLabel(string scope) =>
        MemoryNaming.FormatScopeLabel(scope);

    public static string NameFromUri(string uri)
    {
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return uri;
        string path = uri[7..];
        return Path.GetFileNameWithoutExtension(path);
    }
}
