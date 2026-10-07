using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Core.Bundles;

namespace Scrinia.Core;

/// <summary>
/// Abstraction over persistent and ephemeral memory storage, backed by a workspace on disk.
///
/// <para>There is one production implementation, <see cref="FileMemoryStore"/>. This is an
/// interface rather than a class because the tool surface is tested against hand-written doubles,
/// not because a second backing store is expected.</para>
///
    /// <para>The contract includes workspace paths and bundle operations because the production store
    /// owns the complete on-disk workspace. Test doubles implement the same surface so tool behavior
    /// can be exercised without filesystem setup.</para>
/// </summary>
public interface IMemoryStore : IPluginStore
{
    // Naming — ParseQualifiedName and IsEphemeral moved to IPluginStore (inherited).
    string FormatQualifiedName(string scope, string subject);
    string SanitizeName(string name);

    // ── The write surface: raw input in, derived entry out ───────────────────

    /// <summary>
    /// Writes <paramref name="write"/> as a whole memory — store, re-store or compact — and returns
    /// the entry the STORE derived from it.
    ///
    /// <para><b>The store derives; callers must not.</b> That is the whole contract. Metadata
    /// derivation used to be written out in several paths and had
    /// drifted, in four places far enough to lose data; the rulings that settled each difference are
    /// in <c>docs/superpowers/analysis/2026-08-11-derivation-inventory.md</c> and the one surviving
    /// implementation is <see cref="MemoryDerivation"/>. A caller that computes keywords, term
    /// frequencies, chunk entries or a preview for itself has re-created the defect, whatever it
    /// does with the result.</para>
    ///
    /// <para>The entry is persisted before it is returned: an existing memory is archived first, the
    /// artifact is written, and the index entry is upserted. <c>CreatedAt</c> survives a re-store and
    /// <c>UpdatedAt</c> is set only when the memory already existed.</para>
    /// </summary>
    Task<ArtifactEntry> StoreAsync(MemoryWrite write, CancellationToken ct = default);

    /// <summary>
    /// Appends <paramref name="write"/>'s single chunk and returns a <see cref="Task{TResult}"/>
    /// containing an <see cref="AppendStoreResult"/> for the durable outcome. For an existing memory,
    /// the result contains the entry derived from the full post-append content; for a missing target,
    /// the operation creates a single-chunk memory with the same semantics as <see cref="StoreAsync"/>.
    ///
    /// <para>Same contract as <see cref="StoreAsync"/> — the store derives — plus everything the
    /// memory already carries that an append must not destroy: its curated keywords, its drift
    /// baseline, its earlier chunk entries and its curated preview. Each of those was lost by one of
    /// the four hand-rolled implementations this replaces.</para>
    /// <para>The result distinguishes <see cref="AppendStoreOutcome.Created"/>,
    /// <see cref="AppendStoreOutcome.Appended"/>, and
    /// <see cref="AppendStoreOutcome.SuccessWithWarning"/>. The last means the durable write
    /// committed but post-commit cleanup or event delivery produced a non-fatal warning. A
    /// <see cref="AppendStoreOutcome.RecoveryRequired"/> result carries retained recovery evidence;
    /// an append that cannot safely produce a result throws an append-specific exception. Every
    /// result and append-specific exception carries an operation correlation ID. Existing-memory
    /// archive and cleanup failures never silently discard retained journal evidence.</para>
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <c>write.Content</c> does not hold exactly
    /// one element. The derivation pipeline can express only one appended chunk — it reads the last
    /// element for the appended chunk's attributes and builds one chunk entry for it — so a
    /// multi-element append would silently lose every chunk between the first and the last. It is
    /// refused rather than merged.</exception>
    Task<AppendStoreResult> AppendAsync(MemoryWrite write, CancellationToken ct = default);

    /// <summary>Reads a logical memory address.</summary>
    ValueTask<ArtifactEntry?> ReadAsync(MemoryAddress address, CancellationToken ct = default);

    /// <summary>Writes a validated payload at a logical memory address.</summary>
    ValueTask<ArtifactEntry> WriteAsync(MemoryAddress address, MemoryWrite write,
        CancellationToken ct = default);

    /// <summary>Deletes a logical memory address.</summary>
    ValueTask DeleteAsync(MemoryAddress address, CancellationToken ct = default);

    // Neither StoreAsync nor AppendAsync has a default implementation, deliberately: every store
    // implements both, so a store added later cannot silently inherit a derivation that is wrong
    // for it. This is a rule about DERIVATION, not a policy for the whole interface — six members
    // below do carry defaults, and carry them correctly. Four of the six read through ListScoped
    // and are safe because of its completeness contract: GetEntityFactStore, ListScoped(scopes,
    // excludeTopics), ResolveReadScopes(scopes, excludeTopics), and AnyScoped(scopes, predicate).
    // The other two, TryUpdateEntry and TryUpdateEphemeral, do not read through it at all — they
    // go via LoadIndex and GetEphemeral respectively — and are safe for their own reasons,
    // documented on each.

    /// <summary>
    /// The scope's index entries. <b>The returned list may be a live view of the store's internal
    /// cache — callers must not mutate it.</b> Call <c>ToList()</c> first if you need to.
    /// The read-only type makes that a compile error rather than silent cache corruption.
    /// </summary>
    IReadOnlyList<ArtifactEntry> LoadIndex(string scope = "local");
    void Upsert(ArtifactEntry entry, string scope = "local");
    bool Remove(MemoryAddress address);

    /// <summary>
    /// Atomically load the entry for <paramref name="subject"/> in <paramref name="scope"/>, apply
    /// <paramref name="mutate"/>, and persist the result. Returns false when the entry is missing or
    /// <paramref name="mutate"/> returns null (no write). Fires NO event sinks — it is the seam the
    /// async typing sink uses to write promotions back without re-entering the sink pipeline.
    ///
    /// <para><b>The store re-derives term frequencies from the mutation; the caller must not.</b>
    /// Attribute values feed the BM25 index, so a mutation that adds one and leaves
    /// <c>TermFrequencies</c> alone makes the entry unfindable by its own new attribute. Every
    /// mutation eventually runs <see cref="MemoryDerivation.ForMetadataUpdate"/> over
    /// <c>(current, mutated)</c> before persisting, and <c>FileMemoryStore.TryUpdateEntry</c> is
    /// where it runs — one place, which is what makes this member behave the same however the
    /// mutation arrived. A caller that computes the boost itself has re-created the defect this
    /// branch exists to remove; one of them did (<c>MemoryTools.UpdateMeta</c>), and the other two
    /// mutation routes through this member (<c>EntityTypingEventHandler.PromoteAsync</c>,
    /// <c>MaintenanceEventSink</c>) computed nothing at all — see
    /// <see cref="MemoryDerivation.ForMetadataUpdate"/>'s doc comment.</para>
    /// <para>Default implementation is a non-atomic load→mutate→Upsert, adequate for in-memory/test
    /// stores with no concurrent writers. FileMemoryStore overrides it to run under its per-scope
    /// write lock.</para>
    /// <para>Name matching is case-insensitive here — <c>SanitizeName</c> does not fold case, so an
    /// ordinal comparison silently reported "not found" for an entry stored under a different
    /// casing. Every implementation agrees, on every filesystem: this default, and
    /// <c>FileMemoryStore.TryUpdateEntry</c> and <c>FileMemoryStore.Remove</c> (which share one
    /// resolver: an exact-name probe falling back to a case-insensitive sidecar scan). No
    /// case-sensitive gate remains on any of these paths.</para>
    /// </summary>
    bool TryUpdateEntry(MemoryAddress address, Func<ArtifactEntry, ArtifactEntry?> mutate)
    {
        var (scope, subject) = ResolveAddress(address);
        var current = LoadIndex(scope).FirstOrDefault(
            e => MemoryNaming.NameEquals(e.Name, subject));
        if (current is null) return false;
        var updated = mutate(current);
        if (updated is null) return false;
        Upsert(MemoryDerivation.ForMetadataUpdate(current, updated), scope);
        return true;
    }

    /// <summary>
    /// The merged cross-scope <see cref="EntityFactStore"/> — a materialized view over every
    /// (key, value) entity pair across every scope. Default implementation rebuilds it fresh
    /// from <see cref="ListScoped(IReadOnlyList{string}?)"/> on every call (adequate for in-memory/test stores).
    /// <para>
    /// FileMemoryStore overrides this with a cache-on-first-read, generation-invalidated build (a
    /// per-scope <c>facts.json</c> sidecar reused when its token matches, plus an in-memory merged
    /// view cleared on any Upsert/Remove/TryUpdateEntry) — the seam that lets callers like the
    /// async typing sink (<c>EntityTypingEventHandler</c>) query the existing typed vocabulary
    /// without a bespoke full-store scan on every write event.
    /// </para>
    /// <para>Correct for any store whose <see cref="ListScoped(IReadOnlyList{string}?)"/> honours its completeness
    /// contract, and only for such a store: facts derived over a truncated listing are wrong, not
    /// partial.</para>
    /// </summary>
    EntityFactStore GetEntityFactStore() =>
        EntityFactDerivation.BuildStore(ListScoped((IReadOnlyList<string>?)null));

    // Listing & Search

    /// <summary>
    /// Every entry in <paramref name="scopes"/>, or across every readable scope when null.
    ///
    /// <para><b>The listing is COMPLETE.</b> An implementation backed by a paged source must page
    /// to exhaustion before returning; returning one page is not a degraded answer, it is a wrong
    /// one, and it is silent — the caller sees a short list, not an error.</para>
    ///
    /// <para>This is the precondition every default implementation that reads through this member
    /// rests on: <see cref="GetEntityFactStore"/>, <see cref="ListScoped(IReadOnlyList{string}?, IReadOnlyList{string}?)"/> and
    /// (through <c>DiscoverTopics</c>) <see cref="ResolveReadScopes(IReadOnlyList{string}?, IReadOnlyList{string}?)"/> are all
    /// correct exactly insofar as this member is. The store that broke it was the deleted
    /// a paged source that returned only its first page; all three defaults inherited the truncation,
    /// including the one that makes whole topic scopes disappear rather than merely lose entries.
    /// The rule is written down here because any future paged source must honor it; the production
    /// implementation reads a directory, which cannot page.</para>
    ///
    /// <para><c>ListScopedCompletenessTests</c> holds it. Not by proving the four defaults are
    /// safe — they cannot be — but by running each of them through a double that pages correctly
    /// and an identical one that stops after the first page, so the blast radius of a truncating
    /// listing is executable rather than asserted here in prose.</para>
    /// </summary>
    List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes = null);

    /// <summary>Returns the deterministic lexical top-K snapshot without requiring a full listing.</summary>
    IReadOnlyList<ScopedArtifact> SelectLexicalCandidates(
        IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics, string query, int limit) =>
        SearchCandidateOptions.RankLexically(ListScoped(scopes, excludeTopics), query).Take(limit).ToArray();

    /// <summary>
    /// Returns the deterministic lexical top-K snapshot after applying the query's scopes,
    /// path prefix and attributes. Supplied candidates are constrained to the query's scopes;
    /// otherwise the default reads the complete listing before selecting candidates.
    /// </summary>
    IReadOnlyList<ScopedArtifact> SelectLexicalCandidates(SearchQuery query, int limit)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        IEnumerable<ScopedArtifact> source;
        if (query.Candidates is { } candidates)
        {
            var allowed = new HashSet<string>(
                ResolveReadScopes(query.Scopes, query.ExcludeTopics), StringComparer.OrdinalIgnoreCase);
            if (TopicDiscoveryService.IncludesEphemeral(query.Scopes))
                allowed.Add("ephemeral");
            source = candidates.Where(candidate => allowed.Contains(candidate.Scope));
        }
        else
        {
            source = ListScoped(query.Scopes, query.ExcludeTopics);
        }

        return SearchCandidateSelection.Select(source, query, limit, FormatQualifiedName);
    }

    /// <summary>
    /// True if any entry in <paramref name="scopes"/> (or across every readable scope when null)
    /// matches <paramref name="predicate"/>. Answers a boolean without requiring the caller to
    /// materialize the listing it would otherwise call <see cref="ListScoped(IReadOnlyList{string}?)"/> and
    /// <c>.Any(...)</c> over.
    ///
    /// <para>Default implementation is <c>ListScoped(scopes).Any(predicate)</c> — correct for any
    /// store, on the same precondition as <see cref="GetEntityFactStore"/> and the other
    /// <see cref="ListScoped(IReadOnlyList{string}?)"/>-derived defaults: it is exactly as complete as that
    /// listing is, no more and no less. It is not cheap: it still materializes the full listing
    /// before testing it. FileMemoryStore overrides this to enumerate lazily, scope by scope, and
    /// return on the first match — the reason this member exists rather than callers writing
    /// <c>ListScoped(scopes).Any(predicate)</c> themselves — but a store that has not overridden it
    /// still answers correctly, just without the short-circuit.</para>
    /// </summary>
    bool AnyScoped(IReadOnlyList<string>? scopes, Func<ScopedArtifact, bool> predicate) =>
        ListScoped(scopes).Any(predicate);

    /// <summary>
    /// The one search member. Takes a <see cref="SearchQuery"/> and has no default implementation
    /// on purpose: the five overloads this replaced carried four default bodies that post-filtered
    /// after the top-K cut, so any store that did not override them silently returned fewer than
    /// <c>Limit</c> results for a selective filter. A store that cannot filter must say so at
    /// compile time — for THIS member. That is a consequence of the specific defect it fixes (four
    /// default bodies that post-filtered after the top-K cut), not a rule the rest of this
    /// interface follows.
    /// </summary>
    IReadOnlyList<SearchResult> SearchAll(SearchQuery query);

    /// <summary>Searches using the typed request shape.</summary>
    IReadOnlyList<SearchResult> SearchAll(MemorySearchRequest request) =>
        SearchAll(new SearchQuery(request.Query) { Address = request.Address });

    /// <summary>Lists using the typed request shape.</summary>
    List<ScopedArtifact> ListAsync(MemoryListRequest request) =>
        ListScoped(request.Scopes, request.ExcludeTopics)
            .Where(item => request.Prefix is null || IsAddressUnderPrefix(item.Entry.Address, request.Prefix.Value))
            .ToList();

    private static bool IsAddressUnderPrefix(MemoryAddress address, MemoryAddress prefix)
    {
        if (address.Kind != prefix.Kind)
            return false;
        if (prefix.Kind == MemoryAddressKind.Ephemeral)
            return address == prefix;

        return address == prefix ||
            address.Value.StartsWith(prefix.Value + "/", StringComparison.OrdinalIgnoreCase);
    }

    // Listing & Search with topic exclusion

    /// <summary>
    /// Lists memories, excluding entries from the specified topics.
    /// Default implementation post-filters after calling <see cref="ListScoped(IReadOnlyList{string}?)"/>.
    /// FileMemoryStore overrides with efficient scope-level filtering.
    /// <para>Post-filtering is correct here only because <see cref="ListScoped(IReadOnlyList{string}?)"/> is
    /// complete; see its contract.</para>
    /// </summary>
    /// <param name="scopes">Optional scope filter. An empty list behaves identically to null (Task 9, D-4).</param>
    /// <param name="excludeTopics">Optional topic names to exclude (e.g. ["plan", "task", "project", "learn"]).
    /// An empty list behaves identically to null (Task 9, D-4).</param>
    List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics)
        => excludeTopics is not { Count: > 0 }
            ? ListScoped(scopes)
            : ListScoped(scopes)
                .Where(e => !ShouldExcludeScope(e.Scope, excludeTopics))
                .ToList();

    /// <summary>
    /// Resolves read scopes, excluding the specified topic scopes.
    /// Default implementation post-filters after calling <see cref="ResolveReadScopes(IReadOnlyList{string}?)"/>.
    /// FileMemoryStore overrides with efficient scope-level filtering.
    /// <para>Correct for the same reason as its sibling, and with the same precondition: the
    /// underlying single-argument resolution must see every scope.</para>
    /// </summary>
    IReadOnlyList<string> ResolveReadScopes(IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics)
    {
        var resolved = ResolveReadScopes(scopes);
        if (excludeTopics is not { Count: > 0 })
            return resolved;
        var excluded = BuildExcludedScopeSet(excludeTopics);
        return resolved.Where(s => !excluded.Contains(s)).ToList();
    }

    // Static helpers for scope exclusion

    /// <summary>
    /// Returns true if the given scope should be excluded based on the excludeTopics list.
    /// Case-insensitive. Topics are matched as "local:{topicName}". An empty or null
    /// <paramref name="excludeTopics"/> excludes nothing (Task 9, D-4).
    /// </summary>
    static bool ShouldExcludeScope(string scope, IReadOnlyList<string>? excludeTopics)
    {
        if (excludeTopics is not { Count: > 0 } || string.IsNullOrWhiteSpace(scope))
            return false;
        var excluded = BuildExcludedScopeSet(excludeTopics);
        return excluded.Contains(scope);
    }

    /// <summary>Builds a HashSet of excluded scope names from an excludeTopics list. Every entry
    /// goes through <see cref="MemoryNaming.TryBuildScopedTopicScope"/> — the ONE scope
    /// constructor (Task 13).
    ///
    /// <para>This used to fork on whether the input contained a '/', interpolating
    /// <c>"local:{input}"</c> directly in that case and lower-casing only the other. The
    /// constructor splits on '/' itself, so the fork is gone; the lower-casing went with it, since
    /// this set is already <see cref="StringComparer.OrdinalIgnoreCase"/>. Being an exclusion
    /// FILTER, an unconstructable topic is dropped rather than throwing — it could not have named
    /// a stored scope, so it excludes nothing.</para>
    /// <para>Blank and null entries are dropped by <see cref="MemoryNaming.NormalizeFilterList"/>
    /// rather than a bespoke <c>Where(!IsNullOrWhiteSpace)</c> here (Task 9 fix round 1, F-1/F-2/
    /// F-3) — one shared rule for both <c>scopes</c> and <c>excludeTopics</c>, not two.</para></summary>
    static HashSet<string> BuildExcludedScopeSet(IReadOnlyList<string> excludeTopics) =>
        new(
            (MemoryNaming.NormalizeFilterList(excludeTopics) ?? [])
                .Select(t => MemoryNaming.TryBuildScopedTopicScope(t, out string scope) ? scope : null)
                .OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

    // Ephemeral
    void RememberEphemeral(MemoryAddress address, EphemeralEntry entry);
    bool ForgetEphemeral(MemoryAddress address);
    EphemeralEntry? GetEphemeral(MemoryAddress address);

    /// <summary>
    /// Atomically load the ephemeral memory under <paramref name="key"/>, apply
    /// <paramref name="mutate"/>, and store the result. Returns false when the memory is missing or
    /// <paramref name="mutate"/> returns null (no write). The ephemeral counterpart of
    /// <see cref="TryUpdateEntry"/>, and it exists for the same reason: a caller doing
    /// <see cref="GetEphemeral"/> → mutate → <see cref="RememberEphemeral"/> is a read-modify-write
    /// with no synchronization, so a concurrent writer's change is silently lost. The value dropped
    /// is whatever the loser was adding — for <c>link</c>, a <c>ref</c> attribute, which makes a lost
    /// update produce exactly the dangling one-sided <c>ref</c> that <c>link</c>'s both-sides-checked
    /// precheck exists to prevent.
    ///
    /// <para><b><paramref name="mutate"/> may run more than once.</b> The real implementations are a
    /// compare-and-swap retry loop: if another writer replaces the entry between the read and the
    /// swap, the mutation is re-applied to the NEWER entry rather than overwriting it. A mutation
    /// that accumulates into caller-owned state must therefore reset that state on entry, not append
    /// across attempts. (<see cref="TryUpdateEntry"/> carries no such caveat — it is invoked exactly
    /// once by all three of its implementations.)</para>
    ///
    /// <para><b><paramref name="mutate"/> must be pure with respect to this store: it must not write
    /// to <paramref name="key"/>.</b> The retry is unbounded, so a mutation that stores to the same
    /// key on every invocation invalidates its own swap every time and spins forever. This is a
    /// contract, not a cap, deliberately: under legitimate contention the loop must retry until it
    /// wins, and an attempt limit would convert "lost a race" into a spurious false return that a
    /// caller would report as "nothing to change". The only in-repo mutation that writes is the test
    /// that exercises the retry, which escapes by writing on its first invocation only.</para>
    ///
    /// <para>Unlike <see cref="TryUpdateEntry"/> this takes no scope: an ephemeral memory is its own
    /// whole address, and it never reaches disk — a <c>~name</c> addresses the process holding
    /// it.</para>
    ///
    /// <para>Default implementation is the unsynchronized load→mutate→store, adequate for
    /// in-memory/test stores with no concurrent writers, matching how
    /// <see cref="TryUpdateEntry"/>'s default is scoped.</para>
    /// </summary>
    bool TryUpdateEphemeral(MemoryAddress address, Func<EphemeralEntry, EphemeralEntry?> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var current = GetEphemeral(address);
        if (current is null) return false;
        var updated = mutate(current);
        if (updated is null) return false;
        RememberEphemeral(address, updated);
        return true;
    }

    // Archive
    void ArchiveVersion(MemoryAddress address);

    // Naming / addressing

    // Artifact file I/O — ReadArtifactAsync moved to IPluginStore (inherited).
    /// <summary>Writes the artifact text for <paramref name="subject"/>.</summary>
    Task WriteArtifactAsync(MemoryAddress address, string artifactText,
        CancellationToken ct = default);
    bool DeleteArtifact(string subject, string scope);

    // Topic discovery
    string[] DiscoverTopics();
    List<TopicInfo> GatherTopicInfos(IReadOnlyList<string>? scopes = null);

    // Scope resolution
    IReadOnlyList<string> ResolveReadScopes(IReadOnlyList<string>? scopes = null);

    // ── Filesystem layout ────────────────────────────────────────────────────
    // Filesystem members are part of this contract because the production store is workspace-backed.

    string FindArtifactPath(string subject, string normalizedScope);
    string GetStoreDirForScope(string scope);

    /// <summary>
    /// Absolute path to the workspace root — the directory that contains <c>.scrinia/</c>.
    /// The authority for the workspace's filesystem layout: hand-authored file entities
    /// (via <see cref="PathRouter.ToFileEntityDir"/>), bundle export/import, conflict
    /// scanning, and drift detection all resolve their on-disk location from this one
    /// property.
    /// </summary>
    string WorkspaceRoot { get; }

    // Export/Import support
    List<(string Name, string FilePath)> ListTopicArtifacts(string topicScope);
    void ImportTopicEntries(string topicScope, List<ArtifactEntry> entries,
        Dictionary<string, byte[]> artifactBytes, bool overwrite);

    /// <summary>Restores a fully validated bundle plan as one all-or-rollback operation.</summary>
    Task<BundleRestoreResult> RestoreBundleAsync(BundleRestorePlan plan, CancellationToken ct = default);
}
