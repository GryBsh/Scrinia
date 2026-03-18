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
    List<ArtifactEntry> LoadIndex(string scope = "local");
    void SaveIndex(List<ArtifactEntry> entries, string scope = "local");
    void Upsert(ArtifactEntry entry, string scope = "local");
    bool Remove(string name, string scope = "local");

    // Listing & Search
    List<ScopedArtifact> ListScoped(string? scopes = null);
    IReadOnlyList<SearchResult> SearchAll(string query, string? scopes = null, int limit = 20);

    /// <summary>
    /// Searches with optional supplemental scores from plugins (e.g. embeddings).
    /// Default implementation falls back to the standard SearchAll.
    /// </summary>
    IReadOnlyList<SearchResult> SearchAll(string query, string? scopes, int limit,
        IReadOnlyDictionary<string, double>? supplementalScores)
        => SearchAll(query, scopes, limit);

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
    /// Searches memories, excluding results from the specified topics.
    /// Default implementation post-filters after calling <see cref="SearchAll(string, string?, int)"/>.
    /// FileMemoryStore overrides with efficient scope-level filtering.
    /// </summary>
    /// <param name="query">Search query.</param>
    /// <param name="scopes">Optional comma-separated scope filter.</param>
    /// <param name="limit">Maximum results to return.</param>
    /// <param name="excludeTopics">Optional comma-separated topic names to exclude (e.g. "plan,task,project,learn").</param>
    IReadOnlyList<SearchResult> SearchAll(string query, string? scopes, int limit, string? excludeTopics)
        => string.IsNullOrWhiteSpace(excludeTopics)
            ? SearchAll(query, scopes, limit)
            : SearchAll(query, scopes, limit)
                .Where(r => !ShouldExcludeScope(GetResultScope(r), excludeTopics))
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
    /// Case-insensitive. Topics are matched as "local-topic:{topicName}".
    /// </summary>
    static bool ShouldExcludeScope(string scope, string? excludeTopics)
    {
        if (string.IsNullOrWhiteSpace(excludeTopics) || string.IsNullOrWhiteSpace(scope))
            return false;
        var excluded = BuildExcludedScopeSet(excludeTopics);
        return excluded.Contains(scope);
    }

    /// <summary>Builds a HashSet of excluded scope names from a comma-separated excludeTopics string.</summary>
    static HashSet<string> BuildExcludedScopeSet(string excludeTopics) =>
        new(
            excludeTopics
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => $"local-topic:{t.Trim().ToLowerInvariant()}"),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the scope string from any <see cref="SearchResult"/> subtype.
    /// Returns empty string for unknown result types.
    /// </summary>
    static string GetResultScope(SearchResult result) => result switch
    {
        EntryResult er => er.Item.Scope,
        ChunkEntryResult cr => cr.ParentItem.Scope,
        TopicResult tr => tr.Scope,
        _ => string.Empty
    };

    // Ephemeral
    void RememberEphemeral(string key, EphemeralEntry entry);
    bool ForgetEphemeral(string key);
    EphemeralEntry? GetEphemeral(string key);

    // Copy & Archive
    bool CopyMemory(string src, string dst, bool overwrite, out string message);
    void ArchiveVersion(string subject, string scope = "local");

    // Paths
    string ArtifactPath(string name, string scope = "local");
    string ArtifactUri(string name, string scope = "local");
    string FindArtifactPath(string subject, string normalizedScope);
    string GetStoreDirForScope(string scope);

    // Content utility
    string GenerateContentPreview(string content, int maxLength = 500);

    // Artifact file I/O
    Task WriteArtifactAsync(string subject, string scope, string artifactText, CancellationToken ct = default);
    Task<string> ReadArtifactAsync(string subject, string scope, CancellationToken ct = default);
    bool DeleteArtifact(string subject, string scope);

    // Topic discovery
    string[] DiscoverTopics();
    List<TopicInfo> GatherTopicInfos(string? scopes = null);

    // Export/Import support
    List<(string Name, string FilePath)> ListTopicArtifacts(string topicScope);
    void ImportTopicEntries(string topicScope, List<ArtifactEntry> entries,
        Dictionary<string, string> artifactContents, bool overwrite);

    // Scope resolution
    IReadOnlyList<string> ResolveReadScopes(string? scopes = null);
}
