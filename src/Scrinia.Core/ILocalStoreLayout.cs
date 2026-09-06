namespace Scrinia.Core;

/// <summary>
/// Narrowed layout interface for filesystem-backed stores. Carries the path members
/// that only a local store implements. Narrower than <see cref="IMemoryStore"/> so
/// a caller that needs paths fails to compile against a non-local store.
/// </summary>
public interface ILocalStoreLayout
{
    string WorkspaceRoot { get; }
    string GetStoreDirForScope(string scope);
    string FindArtifactPath(string subject, string normalizedScope);
    string ArtifactPath(string name, string scope = "local");
    string MetaPath(string name, string scope = "local");
    IReadOnlyList<string> ListTopicArtifacts(string topic);
    void ImportTopicEntries(string topic, IReadOnlyList<Models.ArtifactEntry> entries);
}
