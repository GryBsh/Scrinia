using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Resilience;
using Scrinia.Core.Search;

namespace Scrinia;

/// <summary>
/// <see cref="IMemoryStore"/> implementation that proxies to a Scrinia.Server REST API.
/// Ephemeral storage stays client-side. Naming/parsing uses shared logic.
/// </summary>
public sealed partial class HttpMemoryStore : IMemoryStore, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _store;
    private readonly string _tempDir;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly RetryOptions _retryOptions;

    /// <summary>
    /// Sends a synchronous HTTP request with retry + circuit breaker.
    /// Content is buffered before the retry loop so each attempt gets a fresh HttpContent.
    /// </summary>
    private HttpResponseMessage SendSync(HttpRequestMessage request)
    {
        // Buffer content before the retry loop so each attempt gets fresh HttpContent (SEC-028/QAL-016)
        byte[]? contentBytes = null;
        string? contentType = null;
        if (request.Content is not null)
        {
            contentBytes = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            contentType = request.Content.Headers.ContentType?.ToString();
        }

        try
        {
            _circuitBreaker.EnsureClosed();
        }
        catch (CircuitBreakerOpenException)
        {
            // Degrade gracefully — let callers check IsSuccessStatusCode (OPS-1)
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        }

        HttpResponseMessage response;
        try
        {
            response = RetryPolicy.Execute(
                () =>
                {
                    var clone = new HttpRequestMessage(request.Method, request.RequestUri);
                    if (contentBytes is not null)
                    {
                        clone.Content = new ByteArrayContent(contentBytes);
                        if (contentType is not null)
                        {
                            clone.Content.Headers.Remove("Content-Type");
                            clone.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                        }
                    }
                    return _http.Send(clone, HttpCompletionOption.ResponseContentRead);
                },
                resp => TransientDetector.IsTransient(resp),
                _retryOptions);
        }
        catch (Exception)
        {
            // Network failure after retries exhausted — record for circuit breaker
            _circuitBreaker.RecordFailure();
            throw;
        }

        // Fail closed against a server that doesn't speak this client's version contract, before
        // any status-code-based handling below gets a chance to treat its response as trustworthy
        // (see RemoteApiVersionGuard — this covers the synchronous send path; the async path is
        // covered at each of its two call sites below).
        //
        // Every caller of SendSync does `using var resp = SendSync(req)`, so before this guard
        // existed, the response always reached that `using`. A throw from here skips the handoff,
        // so it must dispose the response itself first — otherwise every call against a
        // misversioned server leaks the response and its content stream.
        try
        {
            RemoteApiVersionGuard.Validate(response);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        if (response.IsSuccessStatusCode)
            _circuitBreaker.RecordSuccess();
        else if (TransientDetector.IsTransient(response))
            _circuitBreaker.RecordFailure();
        return response;
    }
    private const int MaxEphemeralEntries = 1000;
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
    [JsonSerializable(typeof(SearchApiRequest))]
    [JsonSerializable(typeof(AppendApiRequest))]
    [JsonSerializable(typeof(AppendApiResponse))]
    [JsonSerializable(typeof(CopyApiRequest))]
    [JsonSerializable(typeof(ChunkApiResponse))]
    private partial class HttpStoreJsonContext : JsonSerializerContext;

    // Internal DTOs for the REST API
    internal sealed record StoreApiRequest(string[] Content, string Name, string? Description = null,
        Dictionary<string, string[]>? Attributes = null, string[]? Keywords = null);
    internal sealed record StoreApiResponse(string Name, string QualifiedName, int ChunkCount, long OriginalBytes, string Message);
    internal sealed record ShowApiResponse(string Name, string Content, int ChunkCount, long OriginalBytes);
    internal sealed record ListApiResponse(ListApiItem[] Memories, int Total);
    internal sealed record ListApiItem(string Name, string QualifiedName, string Scope, int ChunkCount,
        long OriginalBytes, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, string Description,
        Dictionary<string, string[]>? Attributes);
    internal sealed record SearchApiResponse(SearchApiItem[] Results);
    internal sealed record SearchApiItem(string Type, string Name, double Score, string? Description,
        int? ChunkIndex = null, int? TotalChunks = null,
        // Null for a "topic" item — a topic is a scope label, not a memory, and has no real byte
        // count or creation time. Present for "entry" and "chunk" items.
        long? OriginalBytes = null, DateTimeOffset? CreatedAt = null);
    internal sealed record SearchApiRequest(string Q, string? Scopes, int Limit, string? ExcludeTopics,
        string? PathPrefix, Dictionary<string, string[]>? AttributeFilter);
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

    public HttpMemoryStore(HttpClient http, string store = "default", TimeSpan? timeout = null,
        CircuitBreaker? circuitBreaker = null, RetryOptions? retryOptions = null)
    {
        _http = http;
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(30);
        _store = store;
        _circuitBreaker = circuitBreaker ?? new CircuitBreaker();
        _retryOptions = retryOptions ?? new RetryOptions();
        CircuitBreakerRegistry.Register($"http-memory-store:{_store}", _circuitBreaker);
        // Use a temp dir for the helper — we only use it for naming/parsing
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _localHelper = new FileMemoryStore(_tempDir);
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

    public void RememberEphemeral(string key, EphemeralEntry entry)
    {
        _ephemeral[key] = entry;

        if (_ephemeral.Count > MaxEphemeralEntries)
        {
            // Evict oldest by CreatedAt
            var oldest = _ephemeral.OrderBy(e => e.Value.CreatedAt).First();
            _ephemeral.TryRemove(oldest.Key, out _);
        }
    }

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
                ContentPreview: e.ContentPreview,
                Keywords: e.Keywords,
                TermFrequencies: e.TermFrequencies,
                UpdatedAt: e.UpdatedAt,
                ChunkEntries: e.ChunkEntries,
                Attributes: e.Attributes);
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

        // file:// URIs are not supported in remote mode — the server handles its own file resolution
        if (nameOrArtifact.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("file:// URIs are not supported in remote mode.");

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
        _circuitBreaker.EnsureClosed();
        var resp = await RetryPolicy.ExecuteAsync(
            () => _http.GetAsync($"{BaseUrl}/memories/{encoded}", ct),
            r => TransientDetector.IsTransient(r),
            _retryOptions, logger: null, ct);
        // Fail closed on the async send path too — see the sync-path comment in SendSync above.
        RemoteApiVersionGuard.Validate(resp);
        if (resp.IsSuccessStatusCode)
            _circuitBreaker.RecordSuccess();
        else
        {
            if (TransientDetector.IsTransient(resp)) _circuitBreaker.RecordFailure();
            throw new FileNotFoundException($"Memory '{nameOrArtifact}' not found.");
        }

        var show = await resp.Content.ReadFromJsonAsync(HttpStoreJsonContext.Default.ShowApiResponse, ct);
        if (show is null)
            throw new FileNotFoundException($"Memory '{nameOrArtifact}' not found.");

        // Re-encode the decoded content back to NMP/2 for callers that need it
        return Nmp2ChunkedEncoder.Encode(show.Content);
    }

    /// <summary>
    /// Proxies to the same listing endpoint <see cref="ListScoped(string?)"/> already uses,
    /// filtered to <paramref name="scope"/>.
    ///
    /// <para><b>This is a blocking network round trip, deliberately — and for a scope with more
    /// entries than the server's page size, more than one.</b> <c>LoadIndex</c> is declared
    /// synchronous on <see cref="IMemoryStore"/> (mirroring <see cref="FileMemoryStore"/>'s free,
    /// in-process cache read), so there is no async seam to hide the cost behind — every call
    /// here really does block the calling thread on <see cref="SendSync"/>, once per page. That
    /// trade was chosen over the alternative of overriding <c>TryUpdateEntry</c> to PATCH
    /// server-side: that needs a brand-new endpoint and DTO and would still leave every other
    /// caller — the async entity-typing sink, the maintenance sink, and every MCP tool's "does
    /// this entry already exist" lookup — reading an empty (or, pre-pagination-fix, truncated)
    /// list. Proxying here fixes the read for all of them in one change. See the cost audit in
    /// docs/superpowers/sdd/2026-08-10-remote-write-correctness/task-6-report.md for which
    /// production call sites can actually reach an <see cref="HttpMemoryStore"/> and confirmation
    /// that none of them call this in a loop themselves (this method's own internal paging loop is
    /// a different thing — one logical call, not a caller looping over <c>LoadIndex</c>).</para>
    /// </summary>
    public IReadOnlyList<ArtifactEntry> LoadIndex(string scope = "local")
    {
        var result = new List<ArtifactEntry>();
        int offset = 0;
        // 1000 is MaxListLimit server-side (MemoryEndpoints.cs) — the largest page the server
        // will honour, so paging at this size minimizes round trips for a large scope. The
        // server's default page size (200) is what silently truncated LoadIndex before this loop
        // existed: a caller who asked for "the scope" got the first page and no signal that more
        // existed. The loop below keeps requesting pages until it has collected every entry the
        // server reports via ListApiResponse.Total, so a scope of any size comes back whole.
        const int pageSize = 1000;

        while (true)
        {
            string url = $"{BaseUrl}/memories?scopes={Uri.EscapeDataString(scope)}&offset={offset}&limit={pageSize}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = SendSync(req);

            if (!resp.IsSuccessStatusCode)
            {
                // Returning [] here would look exactly like "this scope has no entries" — the
                // same lie LoadIndex told unconditionally before this fix, just gated behind a
                // failed request instead of always. A caller (TryUpdateEntry's default
                // implementation among them) must not conclude "not found" from a server that is
                // unreachable or erroring; that is a materially different condition and losing
                // the distinction is how the original defect happened. Throwing matches
                // WriteArtifactAsync's existing throw-on-failure contract elsewhere in this type.
                // The status code is included in the message specifically so a 403 here reads as
                // a permission problem (this endpoint requires the "search" permission, distinct
                // from "store") rather than an opaque server fault.
                throw new HttpRequestException(
                    $"Remote store listing failed for scope '{scope}': {(int)resp.StatusCode} {resp.StatusCode}.",
                    inner: null,
                    statusCode: resp.StatusCode);
            }

            using var stream = resp.Content.ReadAsStream();
            var list = JsonSerializer.Deserialize(stream, HttpStoreJsonContext.Default.ListApiResponse);
            if (list?.Memories is null || list.Memories.Length == 0)
                break;

            foreach (var item in list.Memories)
            {
                result.Add(new ArtifactEntry(
                    Name: item.Name,
                    Uri: "",
                    OriginalBytes: item.OriginalBytes,
                    ChunkCount: item.ChunkCount,
                    CreatedAt: item.CreatedAt,
                    ContentPreview: item.Description,
                    UpdatedAt: item.UpdatedAt,
                    Attributes: item.Attributes));
            }

            offset += list.Memories.Length;
            if (offset >= list.Total)
                break;
        }

        return result;
    }

    public void Upsert(ArtifactEntry entry, string scope = "local")
    {
        // In remote mode, store operations go through WriteArtifactAsync/StoreAsync
        // Upsert is managed server-side
    }

    /// <summary>
    /// Overrides <see cref="IMemoryStore"/>'s default implementation, which ends with
    /// <c>Upsert(updated, scope)</c>. <see cref="Upsert"/> is a deliberate no-op here — see its
    /// comment — because persistent writes go through <see cref="WriteArtifactAsync"/> instead.
    ///
    /// <para>Now that <see cref="LoadIndex"/> proxies to the real listing endpoint rather than
    /// unconditionally returning <c>[]</c>, the inherited default would find the entry, apply the
    /// caller's mutation, hand it to the no-op <c>Upsert</c>, and still return <c>true</c> —
    /// reporting a successful write that never reached the server. That is a contract violation
    /// regardless of who currently reads it: <see cref="IMemoryStore.TryUpdateEntry"/>'s own doc
    /// promises the return value means the mutation was persisted, and <c>true</c> here would
    /// make that promise false. To be precise about what this fixes today rather than overstate
    /// it: the one production caller, <c>EntityTypingEventHandler.PromoteAsync</c>, calls this
    /// as a discarded statement and does not branch on the result or retry either way — so
    /// <c>true</c> vs. <c>false</c> is currently unobservable in production, and this override is
    /// a correctness fix for the contract itself (for whichever future caller does read the
    /// result — the interface's own doc already promises one exists), not a fix for an observed
    /// live bug.</para>
    ///
    /// <para>The correct fix is a dedicated attributes-only PATCH endpoint, so a metadata-only
    /// mutation does not have to replay the whole artifact through <c>WriteArtifactAsync</c>
    /// (which would also trigger a spurious version-archive on every mutation — unlike
    /// <c>FileMemoryStore.TryUpdateEntry</c>'s atomic, sidecar-only write, this store has no
    /// remote equivalent). That endpoint and DTO are out of scope for the change that added this
    /// override — seeded remotely, LoadIndex used to make the "not found" case lie in one
    /// direction (found nothing existing); without this override, fixing LoadIndex would only
    /// move the lie to the other, worse direction. Until the endpoint exists, this fails closed:
    /// a mutation that would have applied is refused, not silently dropped while reporting
    /// success.</para>
    /// </summary>
    public bool TryUpdateEntry(string subject, string scope, Func<ArtifactEntry, ArtifactEntry?> mutate)
    {
        var current = LoadIndex(scope).FirstOrDefault(e => SanitizeName(e.Name) == SanitizeName(subject));
        if (current is null) return false;
        // From here to the end of the method, this is deliberately dead work: `updated` and the
        // `null` branch below can only ever produce `return false`, same as skipping straight to
        // `return false` after the LoadIndex check above. It is kept anyway — spending the
        // LoadIndex round trip above and running `mutate` for real, not stubbing it out — as
        // documentation-by-code: it proves this override honours the same "find, then apply the
        // caller's mutation" contract the default implementation does, up to the point where a
        // remote store has no channel left to persist the result. Do NOT delete this and jump
        // straight to `return false`: that would silently reintroduce ambiguity about whether a
        // "not found" and a "found but cannot persist" case are being told apart, which is the
        // exact distinction the LoadIndex fix exists to preserve for every other caller.
        var updated = mutate(current);
        if (updated is null) return false;
        return false;
    }

    public bool Remove(string name, string scope = "local")
    {
        string encoded = Uri.EscapeDataString(FormatQualifiedName(scope, name));
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/memories/{encoded}");
        using var resp = SendSync(req);
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

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = SendSync(req);
        if (resp.IsSuccessStatusCode)
        {
            using var stream = resp.Content.ReadAsStream();
            var list = JsonSerializer.Deserialize(stream, HttpStoreJsonContext.Default.ListApiResponse);
            if (list?.Memories is not null)
            {
                foreach (var item in list.Memories)
                {
                    // Reconstruct scope from the qualified name
                    string scope = item.Scope switch
                    {
                        "local" => "local",
                        "ephemeral" => "ephemeral",
                        _ => $"local:{item.Scope}"
                    };

                    var entry = new ArtifactEntry(
                        Name: item.Name,
                        Uri: "",
                        OriginalBytes: item.OriginalBytes,
                        ChunkCount: item.ChunkCount,
                        CreatedAt: item.CreatedAt,
                        ContentPreview: item.Description,
                        UpdatedAt: item.UpdatedAt,
                        Attributes: item.Attributes);
                    result.Add(new ScopedArtifact(scope, entry));
                }
            }
        }

        return result;
    }

    // query.Candidates is ignored — the server materializes its own candidates remotely.
    public IReadOnlyList<SearchResult> SearchAll(SearchQuery query)
        => SearchRemote(query.Query, query.Scopes, query.Limit, query.ExcludeTopics,
            query.PathPrefix, query.AttributeFilter);

    /// <summary>
    /// The one remote search path. GETs when every filter fits a query string; POSTs when an
    /// attribute filter is present, because that is a dictionary of arrays.
    /// <para><b>Supplemental and entity-graph scores are deliberately ignored.</b> They are keyed
    /// by local scope+name and computed by client-side plugins, while a remote store scores
    /// server-side. Passing them over the wire would require the server to trust client scores.
    /// Whether the pipeline should skip computing them for a remote store is a store-capability
    /// question and belongs to the F3 interface split.</para>
    /// </summary>
    private List<SearchResult> SearchRemote(
        string query, string? scopes, int limit, string? excludeTopics,
        string? pathPrefix, IReadOnlyDictionary<string, string[]>? attributeFilter)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        using var request = attributeFilter is { Count: > 0 }
            ? BuildSearchPost(query, scopes, limit, excludeTopics, pathPrefix, attributeFilter)
            : BuildSearchGet(query, scopes, limit, excludeTopics, pathPrefix);

        using var resp = SendSync(request);
        if (!resp.IsSuccessStatusCode)
            return [];

        using var stream = resp.Content.ReadAsStream();
        var search = JsonSerializer.Deserialize(stream, HttpStoreJsonContext.Default.SearchApiResponse);
        if (search?.Results is null)
            return [];

        var results = new List<SearchResult>();
        foreach (var item in search.Results)
        {
            // Reconstruct the result type. An entry/chunk's wire name is always a v3 path
            // (FormatQualifiedName's output, e.g. "/goal/G-1/widget-0") — it never contains
            // ':', so a Contains(':') guard here is dead code that silently hardcodes every
            // remote result's scope to "local". ParseQualifiedName must run unconditionally.
            var (scope, subject) = ParseQualifiedName(item.Name);

            // artifactEntry is built lazily (only inside the entry/chunk branches below) rather
            // than once up front: a "topic" item has no real ArtifactEntry to build (it is a
            // scope label, not a memory) and never reaches this constructor, so there is no
            // topic-shaped value to invent here.
            //
            // NOTE: two of ArtifactEntry's four constructor arguments are still fabricated —
            // Uri: "" (making remote:// dereferenceable is a separate capability decision) and
            // ChunkCount: 1 (wrong for any multi-chunk memory; needs its own wire field). Only
            // OriginalBytes and CreatedAt were in scope for this fix.

            if (item.Type == "chunk" && item.ChunkIndex.HasValue)
            {
                var artifactEntry = BuildArtifactEntry(item, subject);
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
                results.Add(new TopicResult(MemoryNaming.ParseScopeLabel(item.Name), item.Name, item.Description ?? "", 0, null, item.Score));
            }
            else
            {
                var artifactEntry = BuildArtifactEntry(item, subject);
                results.Add(new EntryResult(
                    new ScopedArtifact(scope, artifactEntry),
                    item.Score));
            }
        }

        return results;
    }

    /// <summary>
    /// Builds the <see cref="ArtifactEntry"/> for an "entry" or "chunk" search item — never called
    /// for a "topic" item, which has no ArtifactEntry to build.
    ///
    /// <c>item.OriginalBytes</c>/<c>item.CreatedAt</c> are nullable on the wire DTO only because it
    /// is shared across all three result types; a same-version server always populates both for
    /// "entry" and "chunk" (only "topic" omits them — a genuine absent-value case, not a version-
    /// skew one). <c>.Value</c> below intentionally throws rather than silently substituting a
    /// fabricated byte count or "now" as the creation time if that contract is ever violated —
    /// reinstating either fallback is the exact defect this method exists to fix.
    /// </summary>
    private static ArtifactEntry BuildArtifactEntry(SearchApiItem item, string subject) => new(
        Name: subject,
        Uri: "",
        OriginalBytes: item.OriginalBytes!.Value,
        ChunkCount: 1,
        CreatedAt: item.CreatedAt!.Value,
        ContentPreview: item.Description ?? "");

    private HttpRequestMessage BuildSearchGet(
        string query, string? scopes, int limit, string? excludeTopics, string? pathPrefix)
    {
        string url = $"{BaseUrl}/search?q={Uri.EscapeDataString(query)}&limit={limit}";
        if (!string.IsNullOrWhiteSpace(scopes))
            url += $"&scopes={Uri.EscapeDataString(scopes)}";
        if (!string.IsNullOrWhiteSpace(excludeTopics))
            url += $"&excludeTopics={Uri.EscapeDataString(excludeTopics)}";
        if (!string.IsNullOrWhiteSpace(pathPrefix))
            url += $"&pathPrefix={Uri.EscapeDataString(pathPrefix)}";
        return new HttpRequestMessage(HttpMethod.Get, url);
    }

    private HttpRequestMessage BuildSearchPost(
        string query, string? scopes, int limit, string? excludeTopics,
        string? pathPrefix, IReadOnlyDictionary<string, string[]> attributeFilter)
    {
        // No comparer here: JSON serialization never consults a dictionary's comparer, and
        // an OrdinalIgnoreCase comparer throws ArgumentException on a caller-supplied filter
        // carrying both "Topic" and "topic" (e.g. from case-variant callers or a merged
        // filter). Local mode tolerates that fine — SearchResultFilter.MatchesAttributes
        // lowercases each filter key as it iterates — so a comparer here only introduces a
        // local-crashes-vs-remote-works divergence for no benefit.
        var body = new SearchApiRequest(
            query, scopes, limit, excludeTopics, pathPrefix,
            attributeFilter.ToDictionary(kv => kv.Key, kv => kv.Value));
        string json = JsonSerializer.Serialize(body, HttpStoreJsonContext.Default.SearchApiRequest);
        return new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/search")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
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
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/memories/{encoded}/copy") { Content = content };
        using var resp = SendSync(httpReq);

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

    // ── Addressing ──────────────────────────────────────────────────────────

    public string ArtifactUri(string name, string scope = "local") =>
        $"remote://{_store}/{scope}/{SanitizeName(name)}";

    // ── Artifact file I/O via REST API ──────────────────────────────────────

    public async Task WriteArtifactAsync(string subject, string scope, string artifactText,
        ArtifactWriteMetadata? meta = null, CancellationToken ct = default)
    {
        // Decode the NMP/2 artifact back to text, then store via the API
        byte[] bytes = Nmp2Strategy.Instance.Decode(artifactText);
        string text = System.Text.Encoding.UTF8.GetString(bytes);
        string qualifiedName = FormatQualifiedName(scope, subject);
        // Keywords travel alongside Attributes, not instead of them. The map handed to this write is
        // the caller's PRE-AttributeMigration.Forward map; the server re-runs Forward, which re-folds
        // recognized "key:value" keywords into attributes. Send the map without the keywords and
        // Forward has nothing to re-fold, so that class of agent-supplied keyword is lost silently.
        // ToDictionary: ArtifactWriteMetadata.Attributes is IReadOnlyDictionary and the
        // source-generated serializer needs the concrete Dictionary.
        var req = new StoreApiRequest(
            [text],
            qualifiedName,
            Description: meta?.Description,
            Attributes: meta?.Attributes?.ToDictionary(kv => kv.Key, kv => kv.Value),
            Keywords: meta?.Keywords);
        _circuitBreaker.EnsureClosed();
        var resp = await RetryPolicy.ExecuteAsync(
            () =>
            {
                // Create fresh content per attempt — HttpContent is consumed after send (SEC-029/QAL-017)
                var content = JsonContent.Create(req, HttpStoreJsonContext.Default.StoreApiRequest);
                return _http.PostAsync($"{BaseUrl}/memories", content, ct);
            },
            r => TransientDetector.IsTransient(r),
            _retryOptions, logger: null, ct);
        // Fail closed on this second async send path too — see the sync-path comment in SendSync.
        RemoteApiVersionGuard.Validate(resp);
        if (resp.IsSuccessStatusCode)
        {
            _circuitBreaker.RecordSuccess();
        }
        else
        {
            // A reachable server that answered with a failure status must not look like a
            // successful store to callers (matches FileMemoryStore's throw-on-failure contract).
            if (TransientDetector.IsTransient(resp))
                _circuitBreaker.RecordFailure();
            throw new HttpRequestException(
                $"Remote store write failed for '{qualifiedName}': {(int)resp.StatusCode} {resp.StatusCode}.",
                inner: null,
                statusCode: resp.StatusCode);
        }
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
        using var delReq = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/memories/{encoded}");
        using var resp = SendSync(delReq);
        return resp.IsSuccessStatusCode;
    }

    // ── Topic discovery ─────────────────────────────────────────────────────

    public string[] DiscoverTopics()
    {
        // Discover from listing
        var items = ListScoped(null);
        return items
            .Where(i => i.Scope.StartsWith("local:", StringComparison.Ordinal))
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
            .Where(i => i.Scope.StartsWith("local:", StringComparison.Ordinal))
            .GroupBy(i => i.Scope);

        foreach (var g in grouped)
        {
            string topicName = g.Key["local:".Length..];
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

    public void Dispose()
    {
        _localHelper.Dispose();
        _http.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
