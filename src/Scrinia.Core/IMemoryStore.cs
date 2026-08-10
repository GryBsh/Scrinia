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
    /// <para>Default implementation is a non-atomic load→mutate→Upsert, adequate for in-memory/test
    /// stores with no concurrent writers. FileMemoryStore overrides it to run under its per-scope
    /// write lock.</para>
    /// </summary>
    bool TryUpdateEntry(string subject, string scope, Func<ArtifactEntry, ArtifactEntry?> mutate)
    {
        var current = LoadIndex(scope).FirstOrDefault(e => SanitizeName(e.Name) == SanitizeName(subject));
        if (current is null) return false;
        var updated = mutate(current);
        if (updated is null) return false;
        Upsert(updated, scope);
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
    Task WriteArtifactAsync(string subject, string scope, string artifactText, CancellationToken ct = default);
    Task<string> ReadArtifactAsync(string subject, string scope, CancellationToken ct = default);
    bool DeleteArtifact(string subject, string scope);

    // Topic discovery
    string[] DiscoverTopics();
    List<TopicInfo> GatherTopicInfos(string? scopes = null);

    // Scope resolution
    IReadOnlyList<string> ResolveReadScopes(string? scopes = null);
}
