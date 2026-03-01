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
