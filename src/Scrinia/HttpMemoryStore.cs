using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia;

/// <summary>
/// <see cref="IMemoryStore"/> implementation that proxies to a Scrinia.Server REST API.
/// Ephemeral storage stays client-side. Naming/parsing uses shared logic.
/// </summary>
public sealed partial class HttpMemoryStore : IMemoryStore
{
    private readonly HttpClient _http;
    private readonly string _store;
    private readonly ConcurrentDictionary<string, EphemeralEntry> _ephemeral = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileMemoryStore _localHelper;

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(StoreApiRequest))]
    [JsonSerializable(typeof(StoreApiResponse))]
    [JsonSerializable(typeof(ShowApiResponse))]
    [JsonSerializable(typeof(ListApiResponse))]
    [JsonSerializable(typeof(ListApiItem))]
    [JsonSerializable(typeof(SearchApiResponse))]
    [JsonSerializable(typeof(SearchApiItem))]
    [JsonSerializable(typeof(AppendApiRequest))]
    [JsonSerializable(typeof(AppendApiResponse))]
    [JsonSerializable(typeof(CopyApiRequest))]
    [JsonSerializable(typeof(ChunkApiResponse))]
    private partial class HttpStoreJsonContext : JsonSerializerContext;

    // Internal DTOs for the REST API
    internal sealed record StoreApiRequest(string[] Content, string Name, string? Description = null,
        string[]? Tags = null, string[]? Keywords = null, string? ReviewAfter = null, string? ReviewWhen = null);
    internal sealed record StoreApiResponse(string Name, string QualifiedName, int ChunkCount, long OriginalBytes, string Message);
    internal sealed record ShowApiResponse(string Name, string Content, int ChunkCount, long OriginalBytes);
    internal sealed record ListApiResponse(ListApiItem[] Memories, int Total);
    internal sealed record ListApiItem(string Name, string QualifiedName, string Scope, int ChunkCount,
        long OriginalBytes, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, string Description, string[]? Tags);
    internal sealed record SearchApiResponse(SearchApiItem[] Results);
    internal sealed record SearchApiItem(string Type, string Name, double Score, string? Description,
        int? ChunkIndex = null, int? TotalChunks = null);
    internal sealed record AppendApiRequest(string Content);
    internal sealed record AppendApiResponse(string Name, int ChunkCount, long OriginalBytes, string Message);
    internal sealed record CopyApiRequest(string Destination, bool Overwrite = false);
    internal sealed record ChunkApiResponse(string Content, int ChunkIndex, int TotalChunks);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = HttpStoreJsonContext.Default,
    };

    public HttpMemoryStore(HttpClient http, string store = "default")
    {
        _http = http;
        _store = store;
        // Use a temp dir for the helper — we only use it for naming/parsing
        string tempRoot = Path.Combine(Path.GetTempPath(), $"scrinia-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        _localHelper = new FileMemoryStore(tempRoot);
    }

    private string BaseUrl => $"/api/v1/stores/{Uri.EscapeDataString(_store)}";

    // ── Naming (delegated to local helper — pure string ops) ────────────────

    public (string Scope, string Subject) ParseQualifiedName(string name) =>
        _localHelper.ParseQualifiedName(name);

    public string FormatQualifiedName(string scope, string subject) =>
        _localHelper.FormatQualifiedName(scope, subject);

    public bool IsEphemeral(string name) =>
        _localHelper.IsEphemeral(name);

    public string SanitizeName(string name) =>
        _localHelper.SanitizeName(name);

    // ── Ephemeral (stays client-side) ───────────────────────────────────────

    public void RememberEphemeral(string key, EphemeralEntry entry) =>
        _ephemeral[key] = entry;

    public bool ForgetEphemeral(string key) =>
        _ephemeral.TryRemove(key, out _);

    public EphemeralEntry? GetEphemeral(string key) =>
        _ephemeral.TryGetValue(key, out var entry) ? entry : null;

    private List<ScopedArtifact> ListEphemeral()
    {
        var result = new List<ScopedArtifact>();
        foreach (var kvp in _ephemeral)
        {
            var e = kvp.Value;
            var artifactEntry = new ArtifactEntry(
                Name: $"~{e.Name}",
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

    // ── CRUD via REST API ───────────────────────────────────────────────────

    public async Task<string> ResolveArtifactAsync(string nameOrArtifact, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nameOrArtifact))
            throw new ArgumentException("Input must not be empty.", nameof(nameOrArtifact));

        // Inline artifact
        if (nameOrArtifact.TrimStart().StartsWith("NMP/2 ", StringComparison.Ordinal))
            return nameOrArtifact;

        // file:// URI
        if (nameOrArtifact.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            string filePath = nameOrArtifact[7..];
            return await File.ReadAllTextAsync(filePath, ct);
        }

        // Ephemeral
        if (IsEphemeral(nameOrArtifact))
        {
            string key = MemoryNaming.StripEphemeralPrefix(nameOrArtifact);
            var entry = GetEphemeral(key);
            if (entry is null)
                throw new FileNotFoundException($"Ephemeral memory '~{key}' not found.");
            return entry.Artifact;
        }

        // Remote lookup via Show endpoint
        string encoded = Uri.EscapeDataString(nameOrArtifact);
        var resp = await _http.GetAsync($"{BaseUrl}/memories/{encoded}", ct);
        if (!resp.IsSuccessStatusCode)
            throw new FileNotFoundException($"Memory '{nameOrArtifact}' not found.");

        var show = await resp.Content.ReadFromJsonAsync(HttpStoreJsonContext.Default.ShowApiResponse, ct);
        if (show is null)
            throw new FileNotFoundException($"Memory '{nameOrArtifact}' not found.");

        // Re-encode the decoded content back to NMP/2 for callers that need it
        return Nmp2ChunkedEncoder.Encode(show.Content);
    }

    public List<ArtifactEntry> LoadIndex(string scope = "local") =>
        []; // Not directly supported in remote mode; use ListScoped

    public void SaveIndex(List<ArtifactEntry> entries, string scope = "local") =>
        throw new NotSupportedException("SaveIndex is not supported in remote mode.");

    public void Upsert(ArtifactEntry entry, string scope = "local")
    {
        // In remote mode, store operations go through WriteArtifactAsync/StoreAsync
        // Upsert is managed server-side
    }

    public bool Remove(string name, string scope = "local")
    {
        string encoded = Uri.EscapeDataString(FormatQualifiedName(scope, name));
        var resp = _http.DeleteAsync($"{BaseUrl}/memories/{encoded}").GetAwaiter().GetResult();
        return resp.IsSuccessStatusCode;
    }

    // ── Listing & Search ────────────────────────────────────────────────────

    public List<ScopedArtifact> ListScoped(string? scopes = null)
    {
        var result = new List<ScopedArtifact>();

        // Include ephemeral
        bool includeEphemeral = string.IsNullOrWhiteSpace(scopes);
        if (!includeEphemeral && scopes is not null)
        {
            includeEphemeral = scopes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(s => s.Trim().Equals("ephemeral", StringComparison.OrdinalIgnoreCase));
        }
        if (includeEphemeral)
            result.AddRange(ListEphemeral());

        // Fetch from server
        string url = scopes is not null
            ? $"{BaseUrl}/memories?scopes={Uri.EscapeDataString(scopes)}"
            : $"{BaseUrl}/memories";

        var resp = _http.GetAsync(url).GetAwaiter().GetResult();
        if (resp.IsSuccessStatusCode)
        {
            var list = resp.Content.ReadFromJsonAsync(HttpStoreJsonContext.Default.ListApiResponse).GetAwaiter().GetResult();
            if (list?.Memories is not null)
            {
                foreach (var item in list.Memories)
                {
                    // Reconstruct scope from the qualified name
                    string scope = item.Scope switch
                    {
                        "local" => "local",
                        "ephemeral" => "ephemeral",
                        _ => $"local-topic:{item.Scope}"
                    };

                    var entry = new ArtifactEntry(
                        Name: item.Name,
                        Uri: "",
                        OriginalBytes: item.OriginalBytes,
                        ChunkCount: item.ChunkCount,
                        CreatedAt: item.CreatedAt,
                        Description: item.Description,
                        Tags: item.Tags,
                        UpdatedAt: item.UpdatedAt);
                    result.Add(new ScopedArtifact(scope, entry));
                }
            }
        }

        return result;
    }

    public IReadOnlyList<SearchResult> SearchAll(string query, string? scopes = null, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        string url = $"{BaseUrl}/search?q={Uri.EscapeDataString(query)}&limit={limit}";
        if (scopes is not null)
            url += $"&scopes={Uri.EscapeDataString(scopes)}";

        var resp = _http.GetAsync(url).GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
            return [];

        var search = resp.Content.ReadFromJsonAsync(HttpStoreJsonContext.Default.SearchApiResponse).GetAwaiter().GetResult();
        if (search?.Results is null)
            return [];

        var results = new List<SearchResult>();
        foreach (var item in search.Results)
        {
            // Reconstruct the result type
            string scope = "local";
            string subject = item.Name;

            if (item.Name.Contains(':'))
            {
                var (s, subj) = ParseQualifiedName(item.Name);
                scope = s;
                subject = subj;
            }

            var artifactEntry = new ArtifactEntry(
                Name: subject,
                Uri: "",
                OriginalBytes: 0,
                ChunkCount: 1,
                CreatedAt: DateTimeOffset.UtcNow,
                Description: item.Description ?? "");

            if (item.Type == "chunk" && item.ChunkIndex.HasValue)
            {
                var chunkEntry = new ChunkEntry(
                    item.ChunkIndex.Value,
                    item.Description,
                    null, null);
                results.Add(new ChunkEntryResult(
                    new ScopedArtifact(scope, artifactEntry),
                    chunkEntry,
                    item.TotalChunks ?? 1,
                    item.Score));
            }
            else if (item.Type == "topic")
            {
                results.Add(new TopicResult(scope, item.Name, item.Description ?? "", 0, null, item.Score));
            }
            else
            {
                results.Add(new EntryResult(
                    new ScopedArtifact(scope, artifactEntry),
                    item.Score));
            }
        }

        return results;
    }

    // ── Copy & Archive ──────────────────────────────────────────────────────

    public bool CopyMemory(string src, string dst, bool overwrite, out string message)
    {
        bool srcEphemeral = IsEphemeral(src);
        bool dstEphemeral = IsEphemeral(dst);

        // Handle ephemeral-only copies locally
        if (srcEphemeral && dstEphemeral)
        {
            string srcKey = MemoryNaming.StripEphemeralPrefix(src);
            string dstKey = MemoryNaming.StripEphemeralPrefix(dst);
            var entry = GetEphemeral(srcKey);
            if (entry is null)
            {
                message = $"Error: source memory '{src}' was not found.";
                return false;
            }
            if (!overwrite && GetEphemeral(dstKey) is not null)
            {
                message = $"Error: destination memory '{dst}' already exists. Set overwrite=true to replace it.";
                return false;
            }
            RememberEphemeral(dstKey, entry with { Name = dstKey });
            message = $"Copied '{src}' to '{dst}'.";
            return true;
        }

        // For any persistent copy, use the server's copy endpoint
        string encoded = Uri.EscapeDataString(srcEphemeral ? MemoryNaming.StripEphemeralPrefix(src) : src);
        var req = new CopyApiRequest(dst, overwrite);
        var content = JsonContent.Create(req, HttpStoreJsonContext.Default.CopyApiRequest);
        var resp = _http.PostAsync($"{BaseUrl}/memories/{encoded}/copy", content).GetAwaiter().GetResult();

        if (resp.IsSuccessStatusCode)
        {
            message = $"Copied '{src}' to '{dst}'.";
            return true;
        }

        message = $"Error copying '{src}' to '{dst}': {resp.StatusCode}";
        return false;
    }

    public void ArchiveVersion(string subject, string scope = "local")
    {
        // No-op in remote mode — server archives internally
    }

    // ── Paths (not supported in remote mode) ────────────────────────────────

    public string ArtifactPath(string name, string scope = "local") =>
        throw new NotSupportedException("ArtifactPath is not supported in remote mode.");

    public string ArtifactUri(string name, string scope = "local") =>
        $"remote://{_store}/{scope}/{SanitizeName(name)}";

    public string FindArtifactPath(string subject, string normalizedScope) =>
        throw new NotSupportedException("FindArtifactPath is not supported in remote mode.");

    public string GetStoreDirForScope(string scope) =>
        throw new NotSupportedException("GetStoreDirForScope is not supported in remote mode.");

    // ── Content utility ─────────────────────────────────────────────────────

    public string GenerateContentPreview(string content, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(content)) return "";
        string preview = content[..Math.Min(maxLength, content.Length)];
        return preview.Replace('\n', ' ').Replace('\r', ' ').Trim();
    }

    // ── Artifact file I/O via REST API ──────────────────────────────────────

    public async Task WriteArtifactAsync(string subject, string scope, string artifactText, CancellationToken ct = default)
    {
        // Decode the NMP/2 artifact back to text, then store via the API
        byte[] bytes = new Nmp2Strategy().Decode(artifactText);
        string text = System.Text.Encoding.UTF8.GetString(bytes);
        string qualifiedName = FormatQualifiedName(scope, subject);
        var req = new StoreApiRequest([text], qualifiedName);
        var content = JsonContent.Create(req, HttpStoreJsonContext.Default.StoreApiRequest);
        await _http.PostAsync($"{BaseUrl}/memories", content, ct);
    }

    public async Task<string> ReadArtifactAsync(string subject, string scope, CancellationToken ct = default)
    {
        string qualifiedName = FormatQualifiedName(scope, subject);
        return await ResolveArtifactAsync(qualifiedName, ct);
    }

    public bool DeleteArtifact(string subject, string scope)
    {
        string qualifiedName = FormatQualifiedName(scope, subject);
        string encoded = Uri.EscapeDataString(qualifiedName);
        var resp = _http.DeleteAsync($"{BaseUrl}/memories/{encoded}").GetAwaiter().GetResult();
        return resp.IsSuccessStatusCode;
    }

    // ── Topic discovery ─────────────────────────────────────────────────────

    public string[] DiscoverTopics()
    {
        // Discover from listing
        var items = ListScoped(null);
        return items
            .Where(i => i.Scope.StartsWith("local-topic:", StringComparison.Ordinal))
            .Select(i => i.Scope)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public List<TopicInfo> GatherTopicInfos(string? scopes = null)
    {
        // Gather from listing
        var items = ListScoped(scopes);
        var topics = new List<TopicInfo>();
        var grouped = items
            .Where(i => i.Scope.StartsWith("local-topic:", StringComparison.Ordinal))
            .GroupBy(i => i.Scope);

        foreach (var g in grouped)
        {
            string topicName = g.Key["local-topic:".Length..];
            topics.Add(new TopicInfo(
                Scope: g.Key,
                TopicName: topicName,
                EntryCount: g.Count(),
                Description: $"{topicName} ({g.Count()} {(g.Count() == 1 ? "entry" : "entries")})",
                Tags: null,
                EntryNames: g.Select(e => e.Entry.Name).ToArray()));
        }

        return topics;
    }

    // ── Export/Import helpers ────────────────────────────────────────────────

    public List<(string Name, string FilePath)> ListTopicArtifacts(string topicScope) =>
        throw new NotSupportedException("ListTopicArtifacts is not supported in remote mode. Use export endpoint.");

    public void ImportTopicEntries(string topicScope, List<ArtifactEntry> entries,
        Dictionary<string, string> artifactContents, bool overwrite) =>
        throw new NotSupportedException("ImportTopicEntries is not supported in remote mode. Use import endpoint.");

    public IReadOnlyList<string> ResolveReadScopes(string? scopes = null)
    {
        if (!string.IsNullOrWhiteSpace(scopes))
        {
            return scopes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var ordered = new List<string> { "local" };
        ordered.AddRange(DiscoverTopics());
        return ordered.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
