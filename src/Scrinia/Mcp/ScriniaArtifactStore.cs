using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Mcp;

/// <summary>
/// Manages persistent and ephemeral NMP/2 artifact stores across local and topic scopes.
///
/// Naming convention:
///   "subject"              → local scope:   &lt;workspace&gt;/.scrinia/store/subject.nmp2
///   "topic:subject"        → local topic:   &lt;workspace&gt;/.scrinia/topics/topic/subject.nmp2
///   "~subject"             → ephemeral:     in-memory only (dies with process)
/// </summary>
internal static partial class ScriniaArtifactStore
{
    private static readonly object _configLock = new();
    private static string _configuredWorkspaceRoot = Directory.GetCurrentDirectory();

    // AsyncLocal overrides keep tests isolated when running in parallel.
    private static readonly AsyncLocal<string?> _storeDirOverride = new();
    private static readonly AsyncLocal<string?> _workspaceRootOverride = new();

    // Ephemeral in-memory store — lives for the process lifetime only.
    // AsyncLocal override allows test isolation.
    private static readonly ConcurrentDictionary<string, EphemeralEntry> _globalEphemeralStore = new(StringComparer.OrdinalIgnoreCase);
    private static readonly AsyncLocal<ConcurrentDictionary<string, EphemeralEntry>?> _ephemeralStoreOverride = new();

    private static ConcurrentDictionary<string, EphemeralEntry> EphemeralStore =>
        _ephemeralStoreOverride.Value ?? _globalEphemeralStore;

    public static string StoreDir => GetStoreDirForScope("local");

    /// <summary>Configures scope behavior for the running MCP server process.</summary>
    public static void Configure(string workspaceRoot)
    {
        lock (_configLock)
        {
            _configuredWorkspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot)
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(workspaceRoot);
        }
    }

    internal static void OverrideStoreDir(string? path) => _storeDirOverride.Value = path;
    internal static void OverrideWorkspaceRoot(string? path) => _workspaceRootOverride.Value = path;
    internal static void OverrideEphemeralStore(ConcurrentDictionary<string, EphemeralEntry>? store) =>
        _ephemeralStoreOverride.Value = store;

    // ── Ephemeral memory ─────────────────────────────────────────────────────

    /// <summary>Returns true if the name starts with '~', indicating ephemeral storage.</summary>
    public static bool IsEphemeral(string name) =>
        !string.IsNullOrEmpty(name) && name[0] == '~';

    /// <summary>Strips the '~' prefix from an ephemeral name.</summary>
    [Obsolete("Use MemoryNaming.StripEphemeralPrefix instead.")]
    public static string StripEphemeralPrefix(string name) =>
        MemoryNaming.StripEphemeralPrefix(name);

    public static void RememberEphemeral(string key, EphemeralEntry entry) =>
        EphemeralStore[key] = entry;

    public static bool ForgetEphemeral(string key) =>
        EphemeralStore.TryRemove(key, out _);

    public static EphemeralEntry? GetEphemeral(string key) =>
        EphemeralStore.TryGetValue(key, out var entry) ? entry : null;

    public static List<ScopedArtifact> ListEphemeral()
    {
        var result = new List<ScopedArtifact>();
        foreach (var kvp in EphemeralStore)
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

    internal static void ClearEphemeral() => EphemeralStore.Clear();

    // ── Naming and paths ─────────────────────────────────────────────────────

    /// <summary>
    /// Parses a qualified name into an internal scope string and a sanitized subject name.
    /// "subject" → ("local", subject), "topic:subject" → ("local-topic:topic", subject).
    /// </summary>
    public static (string Scope, string Subject) ParseQualifiedName(string name)
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

    /// <summary>
    /// Normalizes a scope filter token into one or more internal scope strings.
    /// Bare topic names map to local-topic.
    /// </summary>
    internal static IReadOnlyList<string> NormalizeScopeFilters(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return ["local"];

        string s = token.Trim();
        if (s.Equals("local", StringComparison.OrdinalIgnoreCase)) return ["local"];
        if (s.Equals("ephemeral", StringComparison.OrdinalIgnoreCase)) return ["ephemeral"];

        // Already-qualified scope names pass through
        if (s.StartsWith("local-topic:", StringComparison.OrdinalIgnoreCase)) return [s];

        // Bare topic name → local topic
        return [$"local-topic:{SanitizeName(s)}"];
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = StoreJsonContext.Default,
    };

    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(IndexFile))]
    private partial class StoreJsonContext : JsonSerializerContext;

    /// <summary>The resolved workspace root path (respects test overrides).</summary>
    internal static string WorkspaceRootPath => WorkspaceRoot;

    private static string WorkspaceRoot
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_workspaceRootOverride.Value))
                return Path.GetFullPath(_workspaceRootOverride.Value!);
            lock (_configLock)
                return _configuredWorkspaceRoot;
        }
    }

    public static string GetStoreDirForScope(string scope)
    {
        if (scope == "local")
        {
            if (!string.IsNullOrWhiteSpace(_storeDirOverride.Value))
                return Path.GetFullPath(_storeDirOverride.Value!);
            return Path.Combine(WorkspaceRoot, ".scrinia", "store");
        }

        if (scope.StartsWith("local-topic:", StringComparison.Ordinal))
        {
            string topic = scope["local-topic:".Length..];
            return Path.Combine(WorkspaceRoot, ".scrinia", "topics", topic);
        }

        throw new ArgumentException($"Unknown scope: {scope}");
    }

    /// <summary>
    /// Discovers all topic directories under the local workspace.
    /// Returns scope strings like "local-topic:api".
    /// </summary>
    public static string[] DiscoverTopics()
    {
        string localTopicsRoot = Path.Combine(WorkspaceRoot, ".scrinia", "topics");
        if (!Directory.Exists(localTopicsRoot))
            return [];

        return Directory.GetDirectories(localTopicsRoot)
            .Select(d => $"local-topic:{Path.GetFileName(d)}")
            .ToArray();
    }

    /// <summary>
    /// Gathers metadata about each topic for use in search scoring.
    /// </summary>
    public static List<TopicInfo> GatherTopicInfos(string? scopes = null)
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

    public static IReadOnlyList<string> ResolveReadScopes(string? scopes = null)
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

    public static List<ArtifactEntry> LoadIndex(string scope = "local")
    {
        string storeDir = GetStoreDirForScope(scope);
        return LoadIndexFrom(storeDir);
    }

    private static List<ArtifactEntry> LoadIndexFrom(string storeDir)
    {
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

    public static void SaveIndex(List<ArtifactEntry> entries, string scope = "local")
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

    public static void Upsert(ArtifactEntry entry, string scope = "local")
    {
        List<ArtifactEntry> entries = LoadIndex(scope);
        int idx = entries.FindIndex(e => e.Name == entry.Name);
        if (idx >= 0)
            entries[idx] = entry;
        else
            entries.Add(entry);
        SaveIndex(entries, scope);
    }

    public static bool Remove(string name, string scope = "local")
    {
        List<ArtifactEntry> entries = LoadIndex(scope);
        int before = entries.Count;
        entries.RemoveAll(e => e.Name == name);
        if (entries.Count == before) return false;
        SaveIndex(entries, scope);
        return true;
    }

    public static List<ScopedArtifact> ListScoped(string? scopes = null)
    {
        var result = new List<ScopedArtifact>();

        // Include ephemeral entries when no scope filter or when "ephemeral" is requested
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

    public static List<ScopedArtifact> Find(string query, string? scopes = null, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var searcher = new WeightedFieldScorer();
        var candidates = ListScoped(scopes);
        var results = searcher.Search(query, candidates, limit);
        return results.Select(r => r.Item).ToList();
    }

    public static IReadOnlyList<ScoredArtifact> Search(string query, string? scopes = null, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var searcher = new WeightedFieldScorer();
        return searcher.Search(query, ListScoped(scopes), limit);
    }

    public static IReadOnlyList<SearchResult> SearchAll(string query, string? scopes = null, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var searcher = new WeightedFieldScorer();
        var candidates = ListScoped(scopes);
        var topics = GatherTopicInfos(scopes);
        return searcher.SearchAll(query, candidates, topics, limit);
    }

    public static bool CopyMemory(string sourceName, string destinationName, bool overwrite, out string message)
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
            // Persistent → Ephemeral (load into memory for fast access)
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

    public static string GenerateContentPreview(string content, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(content)) return "";
        string preview = content[..Math.Min(maxLength, content.Length)];
        return preview.Replace('\n', ' ').Replace('\r', ' ').Trim();
    }

    public static string SanitizeName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }

    public static string NameFromUri(string uri)
    {
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return uri;

        string path = uri[7..];
        return Path.GetFileNameWithoutExtension(path);
    }

    public static string ArtifactPath(string name, string scope = "local") =>
        Path.Combine(GetStoreDirForScope(scope), SanitizeName(name) + ".nmp2");

    public static string ArtifactUri(string name, string scope = "local") =>
        $"file://{ArtifactPath(name, scope)}";

    /// <summary>
    /// Finds the artifact file path for a subject within a scope.
    /// Returns the canonical path (may or may not exist on disk).
    /// </summary>
    public static string FindArtifactPath(string subject, string normalizedScope)
    {
        return ArtifactPath(subject, normalizedScope);
    }

    /// <summary>
    /// Formats a qualified name from scope and subject.
    /// local → subject, local-topic:t → t:subject
    /// </summary>
    public static string FormatQualifiedName(string scope, string subject)
    {
        if (scope.StartsWith("local-topic:", StringComparison.Ordinal))
            return $"{scope["local-topic:".Length..]}:{subject}";
        return subject;
    }

    /// <summary>
    /// Returns a human-friendly display label for an internal scope string.
    /// local → "local", local-topic:api → "api", ephemeral → "ephemeral"
    /// </summary>
    [Obsolete("Use MemoryNaming.FormatScopeLabel instead.")]
    public static string FormatScopeLabel(string scope) =>
        MemoryNaming.FormatScopeLabel(scope);

    /// <summary>
    /// Returns the NMP/2 artifact text for a given memory name or inline artifact.
    /// Resolution order:
    ///   1. If starts with "NMP/2 " → return as-is (inline artifact)
    ///   2. If starts with "file://" → read file (backward compat, silent)
    ///   3. If starts with "~" → look up in ephemeral store
    ///   4. Parse as qualified name → look up file in appropriate scope → read
    ///   5. Throw if not found
    /// </summary>
    public static async Task<string> ResolveArtifactAsync(string nameOrArtifact, CancellationToken ct = default)
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

    // ── Version history ────────────────────────────────────────────────────────

    /// <summary>
    /// Moves the current .nmp2 artifact to a versions/ subdirectory with a timestamp suffix.
    /// No-op if the file doesn't exist.
    /// </summary>
    public static void ArchiveVersion(string subject, string scope = "local")
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

    // ── Export/Import helpers ─────────────────────────────────────────────────

    /// <summary>Returns all artifact file paths for a topic scope.</summary>
    public static List<(string Name, string FilePath)> ListTopicArtifacts(string topicScope)
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

    /// <summary>Imports entries and artifacts into a topic scope from raw data.</summary>
    public static void ImportTopicEntries(string topicScope, List<ArtifactEntry> entries,
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

            // Write artifact file
            if (artifactContents.TryGetValue(entry.Name, out string? content))
            {
                string filePath = Path.Combine(storeDir, SanitizeName(entry.Name) + ".nmp2");
                File.WriteAllText(filePath, content);
            }

            // Update index
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
}
