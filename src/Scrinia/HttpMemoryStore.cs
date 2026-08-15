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
        // (see RemoteApiVersionGuard — this covers the synchronous send path; the asynchronous
        // paths are covered by ResolveArtifactAsync's own Validate call and by PostCheckedAsync,
        // which every asynchronous write goes through).
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
    [JsonSerializable(typeof(MemoryEntry))]
    [JsonSerializable(typeof(ChunkEntryDto))]
    [JsonSerializable(typeof(SearchApiResponse))]
    [JsonSerializable(typeof(SearchApiItem))]
    [JsonSerializable(typeof(SearchApiRequest))]
    [JsonSerializable(typeof(AppendApiRequest))]
    [JsonSerializable(typeof(AppendApiResponse))]
    [JsonSerializable(typeof(CopyApiRequest))]
    [JsonSerializable(typeof(CopyFromClientApiRequest))]
    [JsonSerializable(typeof(CarriedEntryDto))]
    [JsonSerializable(typeof(ChunkApiResponse))]
    private partial class HttpStoreJsonContext : JsonSerializerContext;

    /// <summary>
    /// Serializes a PATCH body. Identical to <see cref="HttpStoreJsonContext"/> except that it
    /// WRITES nulls — deliberately, and it is the only reason this second context exists.
    /// <para><c>PATCH /memories/{name}</c> requires a complete entry and checks for the PRESENCE of
    /// the <c>description</c>, <c>attributes</c> and <c>keywords</c> keys, rejecting a body that
    /// omits any of them with a 400: a missing key binds exactly like an explicit "clear this
    /// field", and confusing the two silently erases curated metadata. Under
    /// <c>WhenWritingNull</c> an entry that genuinely has no attributes would serialize without the
    /// key and be refused — an explicit <c>null</c> is what the contract wants.</para>
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(MemoryEntry))]
    private partial class PatchBodyJsonContext : JsonSerializerContext;

    // Internal DTOs for the REST API
    internal sealed record StoreApiRequest(string[] Content, string Name, string? Description = null,
        Dictionary<string, string[]>? Attributes = null, string[]? Keywords = null);
    /// <summary>Mirrors <c>Scrinia.Server.Models.StoreResponse</c>: the complete entry the SERVER
    /// derived, plus its message. The name and the two counts are read off <c>Entry</c> — they were
    /// deleted from the response rather than kept alongside it, because a derivable field that can
    /// disagree with its payload is the defect this contract removes.</summary>
    internal sealed record StoreApiResponse(MemoryEntry Entry, string Message);
    /// <summary><c>ChunkCount</c> is deleted, not retained: it is <c>Content.Length</c>.</summary>
    internal sealed record ShowApiResponse(string Name, string[] Content, long OriginalBytes);
    internal sealed record ListApiResponse(MemoryEntry[] Memories, int Total);
    /// <summary>The one complete entry shape mirrored from <c>Scrinia.Server.Models.MemoryEntry</c>
    /// — always complete; a null field means genuinely absent, never "not requested".</summary>
    internal sealed record MemoryEntry(
        string Name, string QualifiedName, string Scope,
        int ChunkCount, long OriginalBytes,
        DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt,
        string Description,
        Dictionary<string, string[]>? Attributes,
        string[]? Keywords,
        Dictionary<string, int>? TermFrequencies,
        ChunkEntryDto[]? ChunkEntries,
        Dictionary<string, string>? FileHashes,
        /// <summary>Which of <see cref="Keywords"/> the caller supplied — additive and optional on
        /// this wire, so an older server simply omits it and it reads as null (UNKNOWN).</summary>
        string[]? AgentKeywords = null);
    internal sealed record ChunkEntryDto(
        int ChunkIndex, string? ContentPreview, string[]? Keywords,
        Dictionary<string, int>? TermFrequencies);
    internal sealed record SearchApiResponse(SearchApiItem[] Results);
    internal sealed record SearchApiItem(string Type, string Name, double Score, string? Description,
        int? ChunkIndex = null, int? TotalChunks = null,
        // Null for a "topic" item — a topic is a scope label, not a memory, and has no real byte
        // count or creation time. Present for "entry" and "chunk" items.
        long? OriginalBytes = null, DateTimeOffset? CreatedAt = null);
    internal sealed record SearchApiRequest(string Q, string? Scopes, int Limit, string? ExcludeTopics,
        string? PathPrefix, Dictionary<string, string[]>? AttributeFilter);
    /// <summary>Mirrors <c>Scrinia.Server.Models.AppendRequest</c>. <c>Content</c> is a single
    /// string because an append writes exactly one chunk (<see cref="IMemoryStore.AppendAsync"/>);
    /// the other three used to be absent from the wire entirely, which silently dropped them.</summary>
    internal sealed record AppendApiRequest(string Content, string? Description = null,
        Dictionary<string, string[]>? Attributes = null, string[]? Keywords = null);
    /// <inheritdoc cref="StoreApiResponse"/>
    internal sealed record AppendApiResponse(MemoryEntry Entry, string Message);
    internal sealed record CopyApiRequest(string Destination, bool Overwrite = false);
    /// <summary>The metadata a client-side source carries to a server-owned destination: exactly the
    /// fields the server can neither derive from the artifact nor compute from the destination name.
    /// Mirrors <c>Scrinia.Server.Models.CarriedEntry</c>. <c>OriginalBytes</c> is deliberately absent
    /// (RULED 2026-08-14): the server computes it from <c>Content</c> itself, so carrying it here
    /// would let the request disagree with its own artifact.</summary>
    internal sealed record CarriedEntryDto(
        DateTimeOffset? UpdatedAt,
        string? Description,
        Dictionary<string, string[]>? Attributes,
        string[]? Keywords,
        Dictionary<string, int>? TermFrequencies,
        ChunkEntryDto[]? ChunkEntries,
        Dictionary<string, string>? FileHashes,
        string[]? AgentKeywords = null);
    /// <summary>Body of <c>POST /memories/{name}/copy-from-client</c>, where <c>{name}</c> is the
    /// DESTINATION — unlike <c>/copy</c>, where it is the source. See <see cref="CopyMemory"/> for why
    /// the source cannot be named in the path.</summary>
    internal sealed record CopyFromClientApiRequest(
        string[] Content, CarriedEntryDto Entry, bool Overwrite = false);
    internal sealed record ChunkApiResponse(string Content, int ChunkIndex, int TotalChunks);

    /// <summary>
    /// The one place a wire <see cref="MemoryEntry"/> becomes an <see cref="ArtifactEntry"/> — used
    /// by the listing reads and by the write surface, so a field the server sends cannot be read
    /// back in one place and dropped in another.
    ///
    /// <para><b>Every derived field is read, deliberately.</b> The two hand-written copies this
    /// replaces built the entry from name/bytes/count/createdAt/description/updatedAt/attributes/
    /// keywords and silently discarded <c>TermFrequencies</c>, <c>ChunkEntries</c> and
    /// <c>FileHashes</c> — which the server puts on the wire for every entry of every page. That is
    /// not a cosmetic loss: <c>TermFrequencies</c> is what BM25 scores against, <c>ChunkEntries</c>
    /// is what an append must not destroy, and <c>FileHashes</c> is the drift baseline
    /// <c>list(mode:"drift")</c> reads.</para>
    ///
    /// <para><c>Uri</c> is the one field with nothing to read: the wire carries no address, and a
    /// dereferenceable <c>remote://</c> scheme is a ruled-but-unbuilt addressing design (see the
    /// note at <see cref="BuildArtifactEntry"/>, which owns that decision). It stays the empty
    /// placeholder both copies already used rather than being invented here.</para>
    /// </summary>
    private static ArtifactEntry ToArtifactEntry(MemoryEntry item) => new(
        Name: item.Name,
        Uri: "",
        OriginalBytes: item.OriginalBytes,
        ChunkCount: item.ChunkCount,
        CreatedAt: item.CreatedAt,
        ContentPreview: item.Description,
        Keywords: item.Keywords,
        TermFrequencies: item.TermFrequencies,
        UpdatedAt: item.UpdatedAt,
        ChunkEntries: item.ChunkEntries?
            .Select(c => new ChunkEntry(c.ChunkIndex, c.ContentPreview, c.Keywords, c.TermFrequencies))
            .ToArray(),
        FileHashes: item.FileHashes,
        Attributes: item.Attributes,
        // Absent from an older server's response, which deserializes to null — and null already
        // means UNKNOWN, so a client talking to one degrades to the documented fallback rather
        // than to a wrong answer.
        AgentKeywords: item.AgentKeywords);

    public HttpMemoryStore(HttpClient http, string store = "default", TimeSpan? timeout = null,
        CircuitBreaker? circuitBreaker = null, RetryOptions? retryOptions = null,
        string? workspaceRoot = null)
    {
        _http = http;
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(30);
        _store = store;
        _circuitBreaker = circuitBreaker ?? new CircuitBreaker();
        _retryOptions = retryOptions ?? new RetryOptions();
        CircuitBreakerRegistry.Register($"http-memory-store:{_store}", _circuitBreaker);
        // The helper store serves two purposes: the pure string operations (naming, parsing,
        // sanitizing) that a remote store answers locally, and — see
        // WriteEphemeralInProcessAsync — ephemeral writes, which are addressed to this process and
        // must be derived by a real store rather than sent to one.
        //
        // Its root is the WORKSPACE root when the caller supplies one, and that matters: an
        // ephemeral write is derived by FileMemoryStore.StoreAsync, which hashes `file` attribute
        // values (MemoryDerivation.ForStore's fileHasher) BEFORE it branches on ephemerality. A
        // throwaway root resolves every reference to nothing, so the memory's drift baseline
        // records each referenced file as unhashable. That was invisible only while EphemeralEntry
        // had no FileHashes member to store the wrong answer in. The comment that used to sit here
        // claimed the write "returns before any path is resolved" — it does not, and that claim is
        // how this stayed hidden.
        //
        // The temp directory remains the fallback for callers with no workspace (tests, and any
        // construction that predates this parameter), and nothing is ever written under it. Created
        // only when it will actually be used: a caller that supplies workspaceRoot should not leave
        // an empty directory behind for every instance it constructs.
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia-http-{Guid.NewGuid():N}");
        if (workspaceRoot is null)
            Directory.CreateDirectory(_tempDir);
        _localHelper = new FileMemoryStore(workspaceRoot ?? _tempDir);
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

    /// <inheritdoc/>
    /// <remarks>
    /// The same compare-and-swap as <c>FileMemoryStore</c>'s, over this store's own map, and it
    /// makes no request: a <c>~name</c> addresses THIS process here too, so an ephemeral metadata
    /// update has nothing to ask the server. That is what lets <c>link</c> on a <c>~name</c> behave
    /// identically in local and remote mode rather than through a second mechanism.
    ///
    /// <para>No fact-store invalidation tail, unlike the local twin: this store does not override
    /// <c>GetEntityFactStore</c>, so it takes the interface default, which rebuilds from
    /// <c>ListScoped(null)</c> on every call and caches nothing to invalidate.</para>
    /// </remarks>
    public bool TryUpdateEphemeral(string key, Func<EphemeralEntry, EphemeralEntry?> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        while (true)
        {
            if (!_ephemeral.TryGetValue(key, out var current)) return false;

            var updated = mutate(current);
            if (updated is null) return false;

            if (_ephemeral.TryUpdate(key, updated, current)) return true;
        }
    }

    private List<ScopedArtifact> ListEphemeral()
    {
        var result = new List<ScopedArtifact>();
        foreach (var kvp in _ephemeral)
        {
            // EphemeralEntry.ToArtifactEntry, the one projection every ephemeral read shares. This
            // site used to write its own, and got two things wrong that the shared one fixes by
            // construction:
            //
            // - It prefixed the tilde onto Name (`$"~{e.Name}"`), disagreeing with every other
            //   ephemeral Name projection: FileMemoryStore.ListEphemeral, PersistEphemeral (which
            //   is what actually answers this store's own ephemeral WRITE response, through
            //   _localHelper), and the server's MemoryEntry.From all treat ArtifactEntry.Name as
            //   BARE and add the `~` only when building a caller-facing address. A prefixed Name
            //   here meant a remote store's write response and its own list response disagreed
            //   about the same memory, and every consumer that correctly adds its own `~` for
            //   Scope == "ephemeral" — the CLI, SearchResultRenderer, MemoryTools list — doubled
            //   it to "~~name" for any candidate from here.
            // - It dropped AgentKeywords, so one memory reported its keyword provenance
            //   differently depending on which read reached it.
            //
            // Uri comes back empty, as it did: an ephemeral artifact has no backing file. That is
            // not the remote-path placeholder described at BuildArtifactEntry — these entries come
            // from the in-process _ephemeral dictionary, not the server.
            result.Add(new ScopedArtifact("ephemeral", kvp.Value.ToArtifactEntry()));
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

        // Re-encode the decoded chunks back to NMP/2 for callers that need it
        return Nmp2ChunkedEncoder.EncodeChunks(show.Content);
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
    /// trade was chosen over the alternative of serving only <c>TryUpdateEntry</c> from a
    /// server-side PATCH: that endpoint exists now and <see cref="TryUpdateEntry"/> does use it,
    /// but it would still have left every OTHER caller — the async entity-typing sink, the
    /// maintenance sink, and every MCP tool's "does this entry already exist" lookup — reading an
    /// empty (or, pre-pagination-fix, truncated) list. Proxying here fixes the read for all of
    /// them in one change.</para>
    ///
    /// <para>The call sites that can reach this store, and their cost: <c>MemoryTools.Core</c>'s
    /// remember and append existing-entry lookups, <c>MemoryTools.Lifecycle</c>'s compact and
    /// restore lookups, <c>BundleFormatService</c>'s per-topic export read,
    /// <c>EntityTypingEventHandler.PromoteAsync</c>, and <c>MaintenanceEventSink</c>. Each spends
    /// one logical call — none of them loops over <c>LoadIndex</c> per entry, so the round trips
    /// are bounded by the number of tool invocations, not by corpus size.
    /// <see cref="TryUpdateEntry"/> no longer reads through here at all: it fetches the single
    /// entry it is about to write, which is one small response instead of a whole scope.
    /// <c>MemoryOrchestrator</c>'s two call sites are server-side and always hold a
    /// <c>FileMemoryStore</c>. (This method's own internal paging loop is a different thing — one
    /// logical call, several pages.)</para>
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
                result.Add(ToArtifactEntry(item));

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
    /// Overrides <see cref="IMemoryStore"/>'s default implementation (LoadIndex → mutate →
    /// <c>Upsert</c>), which cannot work here: <see cref="Upsert"/> is a deliberate no-op remotely,
    /// so the default would find the entry, apply the mutation, discard it into a method that
    /// persists nothing, and still return <c>true</c>. This one uses the real metadata channel
    /// instead: <c>GET /memories/{name}/entry</c>, the caller's mutation, then
    /// <c>PATCH /memories/{name}</c>.
    ///
    /// <para><b>The body is a COMPLETE entry, and its <c>UpdatedAt</c> is the stored one.</b> The
    /// PATCH contract rejects a body missing <c>description</c>, <c>attributes</c> or
    /// <c>keywords</c> (an incomplete body would silently erase curated metadata), and compares the
    /// body's <c>UpdatedAt</c> against the stored value as an optimistic-concurrency token. So the
    /// value sent is the one this method just read, NOT whatever timestamp the caller's mutation
    /// stamped on its copy — sending the mutated one makes every PATCH a guaranteed 409.</para>
    ///
    /// <para><b>Return value.</b> <c>true</c> only on a 200, i.e. the server persisted it.
    /// <c>false</c> for the three definite refusals: 404 (gone between the read and the write),
    /// 409 (a concurrent writer won), and 400 — which a mutation that CHANGES <c>Keywords</c> earns,
    /// because keywords are re-derived from content and PATCH has no content to derive against;
    /// that change has to go through <see cref="StoreAsync"/>/<see cref="AppendAsync"/>. Anything
    /// else (403, 5xx, unreachable) throws rather than returning <c>false</c>: a caller must not
    /// read "the server is broken" as "the mutation was refused", the same distinction
    /// <see cref="LoadIndex"/> throws to preserve — and this method's own read used to BE a
    /// <c>LoadIndex</c> call, so losing that guarantee here is how a fixed path quietly regresses.
    /// The "found" versus "not found" distinction the previous, deliberately-dead implementation
    /// protected is kept the same way it was: <paramref name="mutate"/> runs if and only if the
    /// entry exists.</para>
    /// </summary>
    public bool TryUpdateEntry(string subject, string scope, Func<ArtifactEntry, ArtifactEntry?> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        string qualifiedName = FormatQualifiedName(scope, subject);
        string encoded = Uri.EscapeDataString(qualifiedName);

        MemoryEntry? stored;
        using (var getReq = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/memories/{encoded}/entry"))
        using (var getResp = SendSync(getReq))
        {
            if (getResp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return false;
            if (!getResp.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Remote entry read failed for '{qualifiedName}': " +
                    $"{(int)getResp.StatusCode} {getResp.StatusCode}.",
                    inner: null,
                    statusCode: getResp.StatusCode);

            using var stream = getResp.Content.ReadAsStream();
            stored = JsonSerializer.Deserialize(stream, HttpStoreJsonContext.Default.MemoryEntry);
        }

        if (stored is null)
            throw new HttpRequestException(
                $"Remote entry read for '{qualifiedName}' returned a 2xx with no entry body.");

        var updated = mutate(ToArtifactEntry(stored));
        if (updated is null) return false;

        // The complete entry back, with the fields the caller owns taken from the mutation and the
        // rest echoed from what was read. The server ignores every store-derived field on the body
        // (ChunkCount, OriginalBytes, CreatedAt, TermFrequencies, ChunkEntries, FileHashes) — they
        // are sent because the contract is a complete entry, not because they can change anything.
        var body = stored with
        {
            Description = AttributeReaders.PreviewText(updated.ContentPreview),
            Attributes = updated.Attributes,
            Keywords = updated.Keywords,
            UpdatedAt = stored.UpdatedAt,
        };

        using var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"{BaseUrl}/memories/{encoded}")
        {
            // PatchBodyJsonContext, not HttpStoreJsonContext: the latter omits null properties, and
            // the PATCH contract rejects a body whose "attributes" or "keywords" KEY is missing —
            // an omitted key is indistinguishable from an explicit "clear this field", so the server
            // refuses it rather than guess. A null-omitting serializer would turn every entry with
            // no attributes into a 400.
            Content = new StringContent(
                JsonSerializer.Serialize(body, PatchBodyJsonContext.Default.MemoryEntry),
                System.Text.Encoding.UTF8, "application/json"),
        };
        using var patchResp = SendSync(patchReq);

        if (patchResp.IsSuccessStatusCode) return true;

        return patchResp.StatusCode switch
        {
            System.Net.HttpStatusCode.NotFound => false,
            System.Net.HttpStatusCode.Conflict => false,
            System.Net.HttpStatusCode.BadRequest => false,
            _ => throw new HttpRequestException(
                $"Remote entry update failed for '{qualifiedName}': " +
                $"{(int)patchResp.StatusCode} {patchResp.StatusCode}.",
                inner: null,
                statusCode: patchResp.StatusCode),
        };
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
        // The `scopes is not null` test here selects WHICH URL is built — filtered or unfiltered —
        // rather than whether an empty filter parameter gets appended to one. That makes it a
        // different question from the sibling check that was changed to a whitespace test, and the
        // two are not an inconsistency to reconcile. An empty-but-non-null `scopes` produces
        // `?scopes=`, which `FileMemoryStore.ResolveReadScopes` treats identically to an absent
        // parameter (its `IsNullOrWhiteSpace` gate is false for both) — both fall through to the
        // same default branch and return every discovered scope. Empty and null converge on the
        // same server behaviour, so the client's distinction between them is harmless.
        string url = scopes is not null
            ? $"{BaseUrl}/memories?scopes={Uri.EscapeDataString(scopes)}"
            : $"{BaseUrl}/memories";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = SendSync(req);

        // Same endpoint, same permission, so the same honesty as LoadIndex above: a non-2xx here
        // used to fall through to `return result`, which for the common case (no ephemeral entries)
        // is an empty list — indistinguishable from "this store has nothing in it". A 403 from the
        // "search" permission gate made list() answer "No memories stored." for a store full of
        // them. Throwing keeps "cannot read the listing" apart from "the listing is empty".
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Remote store listing failed{(scopes is null ? "" : $" for scopes '{scopes}'")}: " +
                $"{(int)resp.StatusCode} {resp.StatusCode}.",
                inner: null,
                statusCode: resp.StatusCode);

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

                result.Add(new ScopedArtifact(scope, ToArtifactEntry(item)));
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
            // Uri and ChunkCount are placeholders on the remote path — see the note at
            // BuildArtifactEntry for the two capability decisions that own them.

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
    ///
    /// <c>Uri: ""</c> and <c>ChunkCount: 1</c> are placeholders, not defects, and are not tracked
    /// separately from the two capability decisions that own them. A dereferenceable
    /// <c>remote://</c> scheme is an addressing design (RULED 2026-08-10: build it); carrying real
    /// chunk structure across the wire is a wire-parity design (RULED 2026-08-10: send the
    /// chunks). Until those land, remote reads flatten to a single chunk and the URI has nothing
    /// to resolve against. Both are ruled decisions awaiting their own specs — not backlog, not
    /// yet designed.
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

    /// <summary>
    /// Four-way dispatch on the ephemerality of <paramref name="src"/> and <paramref name="dst"/>.
    /// Ephemeral storage stays client-side, so only the two ALL-persistent-touching combinations
    /// (promotion, demotion) ever leave the process; the other two are answered from
    /// <see cref="_ephemeral"/> or the plain server copy endpoint, exactly as before.
    ///
    /// <para><b>Ruling 2(a) — <c>~a</c> → <c>~a</c>:</b> <see cref="FileMemoryStore"/>'s ephemeral
    /// branch refuses a same-name copy ("source and destination are the same"); this branch used
    /// to accept it. CLOSED, not pinned: the check is a single case-insensitive comparison against
    /// keys this method already has in hand, the branch never leaves the process (so there is no
    /// wire-compatibility reason to tolerate the gap), and nothing elsewhere in this file documents
    /// the omission as a deliberate remote/local divergence — it reads as one that was simply never
    /// ported, not a decision.</para>
    ///
    /// <para><b>Ruling 2(b) — <c>ErrorCodes.Internal</c> for a version-skew refusal:</b> a 404 from
    /// <c>copy-from-client</c> means the caller's server predates the route, not that anything
    /// broke — but <see cref="Scrinia.Mcp.MemoryTools.Copy"/> is the only production caller and it
    /// wraps EVERY <c>false</c> return from this method in <c>ErrorCodes.Internal</c>, unconditionally,
    /// via the same one line it uses for every other failure this method can produce. This method
    /// has no error-code channel — its contract is <c>bool</c> + <c>out string message</c> — and
    /// giving it one would mean widening <see cref="IMemoryStore.CopyMemory"/> across every
    /// implementation (<see cref="FileMemoryStore"/> included) and its one caller, which is a
    /// bigger surface than "the client uses the copy-from-client member Tasks 1-3 built" and is not
    /// something this task's file list (<c>HttpMemoryStore.cs</c> plus its tests) reaches. PINNED:
    /// the label stays <c>Internal</c>: the message text below says explicitly that the server
    /// lacks the route rather than describing an internal fault, so the caller-facing string is
    /// honest even though the machine-readable code is not yet.</para>
    /// </summary>
    public bool CopyMemory(string src, string dst, bool overwrite, out string message)
    {
        bool srcEphemeral = IsEphemeral(src);
        bool dstEphemeral = IsEphemeral(dst);

        // ~a → ~b: unchanged except for the ruling-2(a) same-name guard below — client-local, no
        // wire traffic either way.
        if (srcEphemeral && dstEphemeral)
        {
            string srcKey = MemoryNaming.StripEphemeralPrefix(src);
            string dstKey = MemoryNaming.StripEphemeralPrefix(dst);

            // Existence is checked BEFORE same-name, matching FileMemoryStore's ephemeral branch
            // ordering exactly: a missing source is "not found", not "source and destination are
            // the same", even when src and dst happen to be spelled identically. Reversing this
            // order (as an earlier draft did) reopens, for the one-name-typed-twice case, the exact
            // divergence-between-stores class ruling 2(a) exists to close.
            var entry = GetEphemeral(srcKey);
            if (entry is null)
            {
                message = $"Error: source memory '{src}' was not found.";
                return false;
            }

            if (srcKey.Equals(dstKey, StringComparison.OrdinalIgnoreCase))
            {
                message = "Error: source and destination are the same.";
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

        // ~a → b (promotion): the local ephemeral is the only source of truth. If it is absent
        // there is nothing to ask the server about — sending a bare name and letting the server
        // resolve it is exactly how the old path found an unrelated same-named persistent memory.
        if (srcEphemeral && !dstEphemeral)
        {
            string srcKey = MemoryNaming.StripEphemeralPrefix(src);
            var entry = GetEphemeral(srcKey);
            if (entry is null)
            {
                message = $"Error: source memory '{src}' was not found.";
                return false;
            }

            // Nmp2ChunkedEncoder.DecodeChunk is 1-based — it throws ArgumentOutOfRangeException for
            // any index below 1 — so the loop runs [1, chunkCount], not [0, chunkCount).
            // DecodeAllChunksVerified does this same per-chunk walk with CRC re-checks and size
            // guards, but it is `internal` to Scrinia.Core and this assembly (Scrinia) cannot call
            // it, so the plain per-chunk decode is re-implemented here instead.
            int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(entry.Artifact);
            string[] chunks = new string[chunkCount];
            for (int i = 0; i < chunkCount; i++)
                chunks[i] = Nmp2ChunkedEncoder.DecodeChunk(entry.Artifact, i + 1);

            var entryDto = new CarriedEntryDto(
                UpdatedAt: entry.UpdatedAt,
                Description: entry.ContentPreview,
                Attributes: entry.Attributes,
                Keywords: entry.Keywords,
                TermFrequencies: entry.TermFrequencies,
                ChunkEntries: entry.ChunkEntries?
                    .Select(c => new ChunkEntryDto(c.ChunkIndex, c.ContentPreview, c.Keywords, c.TermFrequencies))
                    .ToArray(),
                FileHashes: entry.FileHashes,
                AgentKeywords: entry.AgentKeywords);

            string encodedDst = Uri.EscapeDataString(dst);
            var promoteReq = new CopyFromClientApiRequest(chunks, entryDto, overwrite);
            var promoteContent = JsonContent.Create(promoteReq, HttpStoreJsonContext.Default.CopyFromClientApiRequest);
            using var promoteHttpReq = new HttpRequestMessage(
                HttpMethod.Post, $"{BaseUrl}/memories/{encodedDst}/copy-from-client") { Content = promoteContent };
            using var promoteResp = SendSync(promoteHttpReq);

            if (promoteResp.IsSuccessStatusCode)
            {
                message = $"Copied '{src}' to '{dst}'.";
                return true;
            }

            // See ruling 2(b) above: this route creates its destination and resolves nothing of its
            // own, so a 404 is the ROUTE being absent — an older server that predates
            // copy-from-client — not "the memory was not found".
            if (promoteResp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                message = $"Error: server does not support promoting '{src}' to '{dst}' " +
                           "(no copy-from-client route on this server).";
                return false;
            }

            message = $"Error copying '{src}' to '{dst}': {promoteResp.StatusCode}.{ReadErrorBody(promoteResp)}";
            return false;
        }

        // a → ~b (demotion): no wire change from before this task — GET .../memories/{name}/entry
        // for the metadata, GET .../memories/{name} for the content, then build the EphemeralEntry
        // field-for-field as FileMemoryStore.cs's persistent → ephemeral branch does (including its
        // `sourceEntry?.` null tolerance and its "Loaded from {sourceName}" preview fallback), and
        // write it into THIS process's ephemeral map so it is visible to the client that asked.
        if (!srcEphemeral && dstEphemeral)
        {
            string dstKey = MemoryNaming.StripEphemeralPrefix(dst);
            string encodedSrc = Uri.EscapeDataString(src);

            // Ruling — source addressing this branch now reaches. Before the I-1 fix below, a
            // non-qualified-name `src` (an inline "NMP/2 " artifact, or a workspace-scoped
            // `file://` URI — the other two branches of IMemoryStore.ResolveArtifactAsync,
            // FileMemoryStore.cs:1550-1563) would 404 at the /entry lookup and never reach the show
            // route that actually understands those shapes. With /entry no longer gating this
            // branch, they do reach it now. ACCEPTED, not restricted: no new permission surface —
            // it is the same `read` permission and the same show/recall route any caller already
            // has (Scrinia.Server.Endpoints.MemoryEndpoints.RecallMemory), just reached one branch
            // earlier than intended — and MemoryTools.Copy's own `nameOrUri` parameter is documented
            // as "Memory name or file:// URI to copy", so a file:// source is a use this tool already
            // promises, not a novel one. FileMemoryStore.CopyMemory's local demotion branch does NOT
            // handle either shape (it goes straight to ParseQualifiedName/FindArtifactPath), so this
            // is a real remote/local capability gap — but it is remote gaining a capability the tool
            // contract already advertises, not remote losing a safety check local relies on. If the
            // two are ever made to converge, the fix would be to teach FileMemoryStore.CopyMemory
            // these two source shapes too (routing through ResolveArtifactAsync the way ShowAsync
            // does), not to add a shape check here that narrows remote back down.
            //
            // GetEntry (the /entry route) resolves through the INDEX only
            // (Scrinia.Server.Endpoints.MemoryEndpoints.FindEntry -> IMemoryStore.LoadIndex). The
            // show route below resolves through the ARTIFACT FILE only
            // (IMemoryStore.ResolveArtifactAsync's qualified-name branch: FindArtifactPath +
            // File.Exists, no index read at all). The two can disagree — a stale or missing index
            // sidecar for an artifact still on disk — and FileMemoryStore.cs's own persistent ->
            // ephemeral branch already tolerates exactly that gap (its `sourceEntry?.` null
            // tolerance and synthesized defaults). So a 404 here means only "no metadata", not
            // "no memory": it degrades sourceEntry to null and lets the show route's own 404 (below)
            // be the authoritative "source not found" answer.
            MemoryEntry? sourceEntry;
            using (var entryReq = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/memories/{encodedSrc}/entry"))
            using (var entryResp = SendSync(entryReq))
            {
                if (entryResp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    sourceEntry = null;
                }
                else if (!entryResp.IsSuccessStatusCode)
                {
                    message = $"Error copying '{src}' to '{dst}': {entryResp.StatusCode}.{ReadErrorBody(entryResp)}";
                    return false;
                }
                else
                {
                    using var entryStream = entryResp.Content.ReadAsStream();
                    sourceEntry = JsonSerializer.Deserialize(entryStream, HttpStoreJsonContext.Default.MemoryEntry);
                }
            }

            if (!overwrite && GetEphemeral(dstKey) is not null)
            {
                message = $"Error: destination memory '{dst}' already exists. Set overwrite=true to replace it.";
                return false;
            }

            ShowApiResponse? show;
            using (var showReq = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/memories/{encodedSrc}"))
            using (var showResp = SendSync(showReq))
            {
                if (showResp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    message = $"Error: source memory '{src}' was not found.";
                    return false;
                }
                if (!showResp.IsSuccessStatusCode)
                {
                    message = $"Error copying '{src}' to '{dst}': {showResp.StatusCode}.{ReadErrorBody(showResp)}";
                    return false;
                }
                using var showStream = showResp.Content.ReadAsStream();
                show = JsonSerializer.Deserialize(showStream, HttpStoreJsonContext.Default.ShowApiResponse);
            }
            if (show is null)
            {
                message = $"Error: source memory '{src}' was not found.";
                return false;
            }

            string artifact = Nmp2ChunkedEncoder.EncodeChunks(show.Content);

            // Ruling — OriginalBytes in the no-index-entry case. FileMemoryStore.cs:2008 falls back
            // to `?? 0` here, because on the local path a missing sourceEntry means no cheap byte
            // count is available without re-reading and re-hashing the artifact file, and 0 was
            // judged good enough for a synthesized entry. The remote path already has a real count
            // for free — `show.OriginalBytes` is the server's own `Content.Sum(UTF8 byte length)`,
            // computed for this exact request regardless of whether an index entry exists — so `?? 0`
            // would be manufacturing a wrong answer purely for symmetry with a value the local path
            // only settled for out of convenience, not correctness. KEPT AS ITS OWN VALUE, not
            // matched to local: if the two are ever unified, `0` is the one that should move,
            // to whatever FileMemoryStore.cs can cheaply compute (e.g. a File.Exists + byte-length
            // read it does not currently do) — not the other way around.
            var ephEntry = new EphemeralEntry(
                Name: dstKey,
                Artifact: artifact,
                OriginalBytes: sourceEntry?.OriginalBytes ?? show.OriginalBytes,
                ChunkCount: sourceEntry?.ChunkCount ?? show.Content.Length,
                CreatedAt: DateTimeOffset.UtcNow,
                ContentPreview: sourceEntry?.Description ?? $"Loaded from {src}",
                Keywords: sourceEntry?.Keywords,
                TermFrequencies: sourceEntry?.TermFrequencies,
                UpdatedAt: sourceEntry?.UpdatedAt,
                ChunkEntries: sourceEntry?.ChunkEntries?
                    .Select(c => new ChunkEntry(c.ChunkIndex, c.ContentPreview, c.Keywords, c.TermFrequencies))
                    .ToArray(),
                FileHashes: sourceEntry?.FileHashes,
                Attributes: sourceEntry?.Attributes,
                AgentKeywords: sourceEntry?.AgentKeywords);
            RememberEphemeral(dstKey, ephEntry);
            message = $"Copied '{src}' to '{dst}'.";
            return true;
        }

        // a → b: unchanged, still POST .../copy. No `~` is stripped from src here any more — this
        // branch can no longer be reached with one, since both ephemeral-source cases are handled
        // above.
        string encoded = Uri.EscapeDataString(src);
        var req = new CopyApiRequest(dst, overwrite);
        var content = JsonContent.Create(req, HttpStoreJsonContext.Default.CopyApiRequest);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/memories/{encoded}/copy") { Content = content };
        using var resp = SendSync(httpReq);

        if (resp.IsSuccessStatusCode)
        {
            message = $"Copied '{src}' to '{dst}'.";
            return true;
        }

        message = $"Error copying '{src}' to '{dst}': {resp.StatusCode}.{ReadErrorBody(resp)}";
        return false;
    }

    /// <summary>
    /// Best-effort read of a failed response's body, truncated for display via
    /// <see cref="HttpErrorBody.Truncate"/> — the same rule <see cref="PostCheckedAsync"/> uses for
    /// its async failures, applied here for <see cref="CopyMemory"/>'s synchronous ones.
    ///
    /// <para>A bare status name ("BadRequest") tells a caller nothing actionable; the server's own
    /// refusal — the destination-collision message, or one of Task 3's carried-entry bounds errors —
    /// is the entire remedy for a caller that hit it. Without this, the local variant of the same
    /// refusal names the exact fix ("Set overwrite=true to replace it.") while the remote one names
    /// only an HTTP status. A body-read failure must not replace the status-code message with a
    /// different one, so this degrades to an empty string rather than throwing.</para>
    /// </summary>
    private static string ReadErrorBody(HttpResponseMessage resp)
    {
        try
        {
            string body = HttpErrorBody.Truncate(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return string.IsNullOrWhiteSpace(body) ? "" : $" {body}";
        }
        catch
        {
            return "";
        }
    }

    public void ArchiveVersion(string subject, string scope = "local")
    {
        // No-op in remote mode — server archives internally
    }

    // ── Addressing ──────────────────────────────────────────────────────────

    public string ArtifactUri(string name, string scope = "local") =>
        $"remote://{_store}/{scope}/{SanitizeName(name)}";

    // ── The write surface: raw input in, derived entry out ───────────────────

    /// <summary>
    /// Pure deferral: POST the raw <see cref="MemoryWrite"/>, return the entry the SERVER derived.
    ///
    /// <para><b>Nothing here derives, and nothing here may.</b> The response is the complete entry
    /// the server's own <see cref="MemoryDerivation"/> pass produced, mapped whole through
    /// <see cref="ToArtifactEntry"/> — keywords, term frequencies, chunk entries, attributes, the
    /// preview and the drift baseline all come back as the store computed them. Assembling any of
    /// them on this side would re-create the four-copies-of-the-contract defect this branch
    /// removed; there is no <c>TextAnalysis</c> call in this file and there must not be one.</para>
    ///
    /// <para>The write response carries the entry rather than this method POSTing and then GETting
    /// it: two round trips are racy — another writer can change the entry in between — so the
    /// second one could hand back an entry that is not the one this call produced.</para>
    /// </summary>
    public async Task<ArtifactEntry> StoreAsync(MemoryWrite write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (write.Content is null || write.Content.Length == 0)
            throw new ArgumentException("A store writes at least one chunk.", nameof(write));

        if (IsEphemeral(write.Name))
            return await WriteEphemeralInProcessAsync(write, append: false, ct);

        string name = WireName(write);
        var body = new StoreApiRequest(
            write.Content, name, write.Description, write.Attributes, write.Keywords);

        using var resp = await PostCheckedAsync(
            $"{BaseUrl}/memories",
            () => JsonContent.Create(body, HttpStoreJsonContext.Default.StoreApiRequest),
            name, ct);

        var stored = await resp.Content.ReadFromJsonAsync(HttpStoreJsonContext.Default.StoreApiResponse, ct);
        return EntryOf(stored?.Entry, name);
    }

    /// <inheritdoc cref="StoreAsync"/>
    public async Task<ArtifactEntry> AppendAsync(MemoryWrite write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        // Same refusal FileMemoryStore makes, for the same reason (the derivation pipeline can
        // express exactly one appended chunk). Enforced here as well as on the wire — the wire's
        // scalar Content field cannot even express a second chunk, but the interface contract is
        // what callers program against, so both implementations must reject the same input.
        if (write.Content is null || write.Content.Length != 1)
            throw new ArgumentException(
                "An append adds exactly one chunk, so MemoryWrite.Content must hold one element.",
                nameof(write));

        if (IsEphemeral(write.Name))
            return await WriteEphemeralInProcessAsync(write, append: true, ct);

        string name = WireName(write);
        var body = new AppendApiRequest(
            write.Content[0], write.Description, write.Attributes, write.Keywords);

        using var resp = await PostCheckedAsync(
            $"{BaseUrl}/memories/{Uri.EscapeDataString(name)}/append",
            () => JsonContent.Create(body, HttpStoreJsonContext.Default.AppendApiRequest),
            name, ct);

        var appended = await resp.Content.ReadFromJsonAsync(HttpStoreJsonContext.Default.AppendApiResponse, ct);
        return EntryOf(appended?.Entry, name);
    }

    /// <summary>The address the server knows a write by. <see cref="MemoryWrite"/> carries the
    /// resolved <c>(Name, Scope)</c> pair every store member takes; the REST API takes the single
    /// user-facing path, which is what <see cref="FormatQualifiedName"/> reassembles. An ephemeral
    /// name never reaches this method — see <see cref="WriteEphemeralInProcessAsync"/>.</summary>
    private string WireName(MemoryWrite write) =>
        FormatQualifiedName(write.Scope, write.Name);

    /// <summary>
    /// Serves an ephemeral write HERE rather than over the wire, because in this store a
    /// <c>~name</c> addresses THIS PROCESS and not the store behind it. Every ephemeral read on
    /// this side — <see cref="GetEphemeral"/>, <see cref="ListEphemeral"/>, and the short-circuit at
    /// the top of <see cref="ResolveArtifactAsync"/> — is answered from the in-process map without
    /// asking the server. POSTing the write instead would land the memory in the SERVER's ephemeral
    /// map, where nothing on this side can ever read it back: <c>remember({ path: '/temp/x' })</c>
    /// would report success and <c>recall</c> would answer "not found". That hazard was recorded as
    /// latent when this write surface landed — every ephemeral path called
    /// <see cref="RememberEphemeral"/> directly and never reached a write member — and repointing
    /// the MCP tools onto <see cref="StoreAsync"/>/<see cref="AppendAsync"/> is what made it
    /// reachable. Answering it here preserves the addressing this store already had, rather than
    /// changing what <c>~name</c> means.
    ///
    /// <para><b>The derivation is still a store's, not this file's.</b> The local helper is a real
    /// <see cref="FileMemoryStore"/> running the same <c>MemoryDerivation</c> pass the server would
    /// run — nothing about the contract is re-implemented on this side, which is the rule this file
    /// exists under. The helper's own ephemeral map is only a scratch space for the one call: the
    /// memory is seeded into it, written through the store, and lifted back out into
    /// <see cref="_ephemeral"/>, which is the map every ephemeral read here consults. It is cleared
    /// afterwards so a memory is never resident in two maps.</para>
    ///
    /// <para>Concurrent writes to the SAME ephemeral key are not serialized, matching what this
    /// store already offered — <see cref="_ephemeral"/> is a concurrent dictionary but an append has
    /// always been a read-modify-write over it.</para>
    /// </summary>
    private async Task<ArtifactEntry> WriteEphemeralInProcessAsync(
        MemoryWrite write, bool append, CancellationToken ct)
    {
        string key = MemoryNaming.StripEphemeralPrefix(write.Name);
        var current = GetEphemeral(key);
        if (current is not null)
            _localHelper.RememberEphemeral(key, current);
        else
            _localHelper.ForgetEphemeral(key);

        try
        {
            var entry = append
                ? await _localHelper.AppendAsync(write, ct)
                : await _localHelper.StoreAsync(write, ct);

            // The stored EphemeralEntry, not a projection of it: the artifact bytes live only on
            // that record, and ResolveArtifactAsync serves reads straight out of it.
            var stored = _localHelper.GetEphemeral(key)
                ?? throw new InvalidOperationException(
                    $"The local store did not persist ephemeral memory '~{key}'.");
            RememberEphemeral(key, stored);
            return entry;
        }
        finally
        {
            _localHelper.ForgetEphemeral(key);
        }
    }

    /// <summary>Guards the one thing a 2xx write response can still get wrong: a body that is
    /// absent or carries no entry. Returning a fabricated entry there would hand a caller a
    /// zero-byte, keyword-less memory that looks successfully stored.</summary>
    private static ArtifactEntry EntryOf(MemoryEntry? entry, string name) =>
        entry is null
            ? throw new HttpRequestException(
                $"Remote store write for '{name}' succeeded but returned no entry. The server must " +
                "answer a write with the complete entry it derived; a response without one cannot " +
                "be completed on this side without deriving.")
            : ToArtifactEntry(entry);

    // ── Artifact file I/O via REST API ──────────────────────────────────────

    /// <summary>
    /// The one asynchronous POST path, and the one place its failure contract lives. Mirrors
    /// <see cref="SendSync"/> for the async side: fresh content per attempt (HttpContent is
    /// consumed after send — SEC-029/QAL-017), the version guard applied BEFORE any status-code
    /// handling can treat the response as trustworthy, circuit-breaker bookkeeping, and — on a
    /// non-2xx — an <see cref="HttpRequestException"/> carrying the server's own error body.
    ///
    /// <para>That body matters and is not decoration: the server's 4xx responses carry an
    /// <c>ErrorResponse</c> naming the exact bound or field refused ("attributes may not exceed N
    /// values per key", "Permission 'store' required"). Without it, a fixable, self-describing
    /// rejection reached the agent as a bare "400 BadRequest". <c>HttpErrorBody.Truncate</c> is the
    /// repo's one place for this — bodies can be megabytes of proxy HTML — and reading it is
    /// best-effort, so a failure to read must not replace the status-code exception with a
    /// different one.</para>
    ///
    /// <para>The caller owns the returned response and must dispose it. Every throwing path here
    /// disposes it first: before this helper existed, a version-guard throw leaked the response and
    /// its content stream on every call against a misversioned server.</para>
    /// </summary>
    private async Task<HttpResponseMessage> PostCheckedAsync(
        string url, Func<HttpContent> content, string subject, CancellationToken ct)
    {
        _circuitBreaker.EnsureClosed();
        var resp = await RetryPolicy.ExecuteAsync(
            () => _http.PostAsync(url, content(), ct),
            r => TransientDetector.IsTransient(r),
            _retryOptions, logger: null, ct);

        try
        {
            RemoteApiVersionGuard.Validate(resp);
        }
        catch
        {
            resp.Dispose();
            throw;
        }

        if (resp.IsSuccessStatusCode)
        {
            _circuitBreaker.RecordSuccess();
            return resp;
        }

        // A reachable server that answered with a failure status must not look like a successful
        // store to callers (matches FileMemoryStore's throw-on-failure contract).
        if (TransientDetector.IsTransient(resp))
            _circuitBreaker.RecordFailure();

        string body;
        try
        {
            body = HttpErrorBody.Truncate(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            body = "";
        }
        string detail = string.IsNullOrWhiteSpace(body) ? "" : $" {body}";
        var failure = new HttpRequestException(
            $"Remote store write failed for '{subject}': {(int)resp.StatusCode} {resp.StatusCode}.{detail}",
            inner: null,
            statusCode: resp.StatusCode);
        resp.Dispose();
        throw failure;
    }

    public async Task WriteArtifactAsync(string subject, string scope, string artifactText,
        CancellationToken ct = default)
    {
        // Decode the NMP/2 artifact back to text, then store via the API
        byte[] bytes = Nmp2Strategy.Instance.Decode(artifactText);
        string text = System.Text.Encoding.UTF8.GetString(bytes);
        string qualifiedName = FormatQualifiedName(scope, subject);
        var req = new StoreApiRequest([text], qualifiedName);

        // Same send path, same failure contract, as StoreAsync above — see PostCheckedAsync. This
        // method used to carry its own copy of it.
        using var resp = await PostCheckedAsync(
            $"{BaseUrl}/memories",
            () => JsonContent.Create(req, HttpStoreJsonContext.Default.StoreApiRequest),
            qualifiedName, ct);
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
