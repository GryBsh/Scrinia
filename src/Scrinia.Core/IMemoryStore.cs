using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Core;

/// <summary>
/// Abstraction over persistent and ephemeral memory storage.
/// Seam for Phase 2: local (filesystem) vs remote (HTTP) implementations.
/// </summary>
public interface IMemoryStore
{
    // Naming
    (string Scope, string Subject) ParseQualifiedName(string name);
    string FormatQualifiedName(string scope, string subject);
    bool IsEphemeral(string name);
    string SanitizeName(string name);

    // ── The write surface: raw input in, derived entry out ───────────────────

    /// <summary>
    /// Writes <paramref name="write"/> as a whole memory — store, re-store or compact — and returns
    /// the entry the STORE derived from it.
    ///
    /// <para><b>The store derives; callers must not.</b> That is the whole contract. Metadata
    /// derivation used to be written out four times (two MCP paths, two server paths) and had
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
    /// Appends <paramref name="write"/>'s single chunk to an existing memory and returns the entry
    /// the store derived over the memory's full post-append content. Appending to a memory that does
    /// not exist yet is a <see cref="StoreAsync"/>.
    ///
    /// <para>Same contract as <see cref="StoreAsync"/> — the store derives — plus everything the
    /// memory already carries that an append must not destroy: its curated keywords, its drift
    /// baseline, its earlier chunk entries and its curated preview. Each of those was lost by one of
    /// the four hand-rolled implementations this replaces.</para>
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <c>write.Content</c> does not hold exactly
    /// one element. The derivation pipeline can express only one appended chunk — it reads the last
    /// element for the appended chunk's attributes and builds one chunk entry for it — so a
    /// multi-element append would silently lose every chunk between the first and the last. It is
    /// refused rather than merged.</exception>
    Task<ArtifactEntry> AppendAsync(MemoryWrite write, CancellationToken ct = default);

    // Neither member has a default implementation, deliberately: every store implements both, so a
    // store added later cannot silently inherit a derivation that is wrong for it.

    // CRUD
    Task<string> ResolveArtifactAsync(string nameOrArtifact, CancellationToken ct = default);

    /// <summary>
    /// The scope's index entries. <b>The returned list may be a live view of the store's internal
    /// cache — callers must not mutate it.</b> Call <c>ToList()</c> first if you need to.
    /// The read-only type makes that a compile error rather than silent cache corruption.
    /// </summary>
    IReadOnlyList<ArtifactEntry> LoadIndex(string scope = "local");
    void Upsert(ArtifactEntry entry, string scope = "local");
    bool Remove(string name, string scope = "local");

    /// <summary>
    /// Atomically load the entry for <paramref name="subject"/> in <paramref name="scope"/>, apply
    /// <paramref name="mutate"/>, and persist the result. Returns false when the entry is missing or
    /// <paramref name="mutate"/> returns null (no write). Fires NO event sinks — it is the seam the
    /// async typing sink uses to write promotions back without re-entering the sink pipeline.
    ///
    /// <para><b>The store re-derives term frequencies from the mutation; the caller must not.</b>
    /// Attribute values feed the BM25 index, so a mutation that adds one and leaves
    /// <c>TermFrequencies</c> alone makes the entry unfindable by its own new attribute. Every
    /// implementation runs <see cref="MemoryDerivation.ForMetadataUpdate"/> over
    /// <c>(current, mutated)</c> before persisting — which is what makes this member behave the
    /// same however the mutation arrived, and the same whether the store is local or remote (a
    /// remote store sends the attributes and the server's store derives). A caller that computes
    /// the boost itself has re-created the defect this branch exists to remove; three of them did,
    /// each differently, and one of the three could not persist it over HTTP at all.</para>
    /// <para>Default implementation is a non-atomic load→mutate→Upsert, adequate for in-memory/test
    /// stores with no concurrent writers. FileMemoryStore overrides it to run under its per-scope
    /// write lock.</para>
    /// <para>Name matching is case-insensitive here — <c>SanitizeName</c> does not fold case, so an
    /// ordinal comparison silently reported "not found" for an entry stored under a different
    /// casing. Every implementation now agrees, on every filesystem: this default,
    /// <c>FileMemoryStore.TryUpdateEntry</c> (which resolves identity from the sidecars themselves,
    /// with no <c>File.Exists</c> gate in front of it), <c>FileMemoryStore.Remove</c> (whose
    /// exact-name probe falls back to a case-insensitive sidecar scan), and
    /// <c>HttpMemoryStore.TryUpdateEntry</c>. No case-sensitive gate remains on any of these
    /// paths.</para>
    /// </summary>
    bool TryUpdateEntry(string subject, string scope, Func<ArtifactEntry, ArtifactEntry?> mutate)
    {
        var current = LoadIndex(scope).FirstOrDefault(
            e => SanitizeName(e.Name).Equals(SanitizeName(subject), StringComparison.OrdinalIgnoreCase));
        if (current is null) return false;
        var updated = mutate(current);
        if (updated is null) return false;
        Upsert(MemoryDerivation.ForMetadataUpdate(current, updated), scope);
        return true;
    }

    /// <summary>
    /// The merged cross-scope <see cref="EntityFactStore"/> — a materialized view over every
    /// (key, value) entity pair across every scope. Default implementation rebuilds it fresh
    /// from <see cref="ListScoped(string?)"/> on every call (adequate for in-memory/test stores).
    /// <para>
    /// FileMemoryStore overrides this with a cache-on-first-read, generation-invalidated build (a
    /// per-scope <c>facts.json</c> sidecar reused when its token matches, plus an in-memory merged
    /// view cleared on any Upsert/Remove/TryUpdateEntry) — the seam that lets callers like the
    /// async typing sink (<c>EntityTypingEventHandler</c>) query the existing typed vocabulary
    /// without a bespoke full-store scan on every write event.
    /// </para>
    /// </summary>
    EntityFactStore GetEntityFactStore() =>
        EntityFactDerivation.BuildStore(ListScoped(null));

    // Listing & Search
    List<ScopedArtifact> ListScoped(string? scopes = null);

    /// <summary>
    /// The one search member. Takes a <see cref="SearchQuery"/> and has no default implementation
    /// on purpose: the five overloads this replaced carried four default bodies that post-filtered
    /// after the top-K cut, so any store that did not override them silently returned fewer than
    /// <c>Limit</c> results for a selective filter. A store that cannot filter must now say so at
    /// compile time.
    /// </summary>
    IReadOnlyList<SearchResult> SearchAll(SearchQuery query);

    // Listing & Search with topic exclusion

    /// <summary>
    /// Lists memories, excluding entries from the specified topics.
    /// Default implementation post-filters after calling <see cref="ListScoped(string?)"/>.
    /// FileMemoryStore overrides with efficient scope-level filtering.
    /// </summary>
    /// <param name="scopes">Optional comma-separated scope filter.</param>
    /// <param name="excludeTopics">Optional comma-separated topic names to exclude (e.g. "plan,task,project,learn").</param>
    List<ScopedArtifact> ListScoped(string? scopes, string? excludeTopics)
        => string.IsNullOrWhiteSpace(excludeTopics)
            ? ListScoped(scopes)
            : ListScoped(scopes)
                .Where(e => !ShouldExcludeScope(e.Scope, excludeTopics))
                .ToList();

    /// <summary>
    /// Resolves read scopes, excluding the specified topic scopes.
    /// Default implementation post-filters after calling <see cref="ResolveReadScopes(string?)"/>.
    /// FileMemoryStore overrides with efficient scope-level filtering.
    /// </summary>
    IReadOnlyList<string> ResolveReadScopes(string? scopes, string? excludeTopics)
    {
        var resolved = ResolveReadScopes(scopes);
        if (string.IsNullOrWhiteSpace(excludeTopics))
            return resolved;
        var excluded = BuildExcludedScopeSet(excludeTopics);
        return resolved.Where(s => !excluded.Contains(s)).ToList();
    }

    // Static helpers for scope exclusion

    /// <summary>
    /// Returns true if the given scope should be excluded based on the excludeTopics string.
    /// Case-insensitive. Topics are matched as "local:{topicName}".
    /// </summary>
    static bool ShouldExcludeScope(string scope, string? excludeTopics)
    {
        if (string.IsNullOrWhiteSpace(excludeTopics) || string.IsNullOrWhiteSpace(scope))
            return false;
        var excluded = BuildExcludedScopeSet(excludeTopics);
        return excluded.Contains(scope);
    }

    /// <summary>Builds a HashSet of excluded scope names from a comma-separated excludeTopics string.
    /// Already-scoped inputs (containing '/') pass through as "local:{input}";
    /// bare topic names are routed through <see cref="MemoryNaming.BuildScopedTopicScope"/>.</summary>
    static HashSet<string> BuildExcludedScopeSet(string excludeTopics) =>
        new(
            excludeTopics
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t =>
                {
                    var trimmed = t.Trim();
                    return trimmed.Contains('/')
                        ? $"local:{trimmed}"
                        : MemoryNaming.BuildScopedTopicScope(trimmed.ToLowerInvariant());
                }),
            StringComparer.OrdinalIgnoreCase);

    // Ephemeral
    void RememberEphemeral(string key, EphemeralEntry entry);
    bool ForgetEphemeral(string key);
    EphemeralEntry? GetEphemeral(string key);

    // Copy & Archive
    bool CopyMemory(string src, string dst, bool overwrite, out string message);
    void ArchiveVersion(string subject, string scope = "local");

    // Naming / addressing
    string ArtifactUri(string name, string scope = "local");

    // Artifact file I/O
    /// <summary>
    /// Writes the artifact text for <paramref name="subject"/>.
    /// <para><paramref name="meta"/> is honoured only by stores that cannot receive metadata
    /// through <c>Upsert</c> — in practice, remote stores. <see cref="FileMemoryStore"/> ignores
    /// it by design; see the comment at its implementation.</para>
    /// </summary>
    Task WriteArtifactAsync(string subject, string scope, string artifactText,
        ArtifactWriteMetadata? meta = null, CancellationToken ct = default);
    Task<string> ReadArtifactAsync(string subject, string scope, CancellationToken ct = default);
    bool DeleteArtifact(string subject, string scope);

    // Topic discovery
    string[] DiscoverTopics();
    List<TopicInfo> GatherTopicInfos(string? scopes = null);

    // Scope resolution
    IReadOnlyList<string> ResolveReadScopes(string? scopes = null);
}
