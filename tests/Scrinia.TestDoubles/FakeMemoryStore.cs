using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.TestDoubles;

/// <summary>
/// A do-nothing <see cref="IMemoryStore"/> — every member returns an empty or default result.
/// <para>It implements <see cref="IMemoryStore"/> and deliberately <b>not</b>
/// <see cref="ILocalMemoryStore"/>, which is the point: after the store split the compiler will
/// not let it be mistaken for a store with a workspace on disk. Any tool path that reaches for a
/// local filesystem member has to deal with its absence, and a test can pin what it does.</para>
/// <para>Members are <c>virtual</c> so a test can subclass and override just the one or two it
/// cares about, rather than hand-writing 23 member bodies per double.</para>
/// <para>Shared by <c>Scrinia.Tests</c> and <c>Scrinia.Server.Tests</c> — both reference this
/// project so neither has to keep its own copy in sync with the other.</para>
/// </summary>
public class FakeMemoryStore : IMemoryStore
{
    public virtual (string Scope, string Subject) ParseQualifiedName(string name) => ("local", name);
    public virtual string FormatQualifiedName(string scope, string subject) => $"{scope}:{subject}";
    public virtual bool IsEphemeral(string name) => name.StartsWith('~');
    public virtual string SanitizeName(string name) => name;
    public virtual Task<string> ResolveArtifactAsync(string nameOrArtifact, CancellationToken ct = default) => Task.FromResult("");
    // Derives nothing, like every other member here: the returned entry echoes the write's own name
    // and chunk count so a test can tell which call produced it, and overriding is how a test that
    // cares about derivation gets a real answer.
    public virtual Task<ArtifactEntry> StoreAsync(MemoryWrite write, CancellationToken ct = default) =>
        Task.FromResult(new ArtifactEntry(write.Name, "", 0, write.Content.Length, DateTimeOffset.UtcNow));
    public virtual Task<ArtifactEntry> AppendAsync(MemoryWrite write, CancellationToken ct = default) =>
        Task.FromResult(new ArtifactEntry(write.Name, "", 0, write.Content.Length, DateTimeOffset.UtcNow));
    public virtual IReadOnlyList<ArtifactEntry> LoadIndex(string scope = "local") => [];
    public virtual void Upsert(ArtifactEntry entry, string scope = "local") { }
    public virtual bool Remove(string name, string scope = "local") => false;
    public virtual List<ScopedArtifact> ListScoped(string? scopes = null) => [];
    public virtual IReadOnlyList<SearchResult> SearchAll(SearchQuery query) => [];
    public virtual void RememberEphemeral(string key, EphemeralEntry entry) { }
    public virtual bool ForgetEphemeral(string key) => false;
    public virtual EphemeralEntry? GetEphemeral(string key) => null;
    public virtual bool CopyMemory(string src, string dst, bool overwrite, out string message) { message = ""; return false; }
    public virtual void ArchiveVersion(string subject, string scope = "local") { }
    public virtual string ArtifactUri(string name, string scope = "local") => "";
    public virtual Task WriteArtifactAsync(string subject, string scope, string artifactText,
        CancellationToken ct = default) => Task.CompletedTask;
    public virtual Task<string> ReadArtifactAsync(string subject, string scope, CancellationToken ct = default) => Task.FromResult("");
    public virtual bool DeleteArtifact(string subject, string scope) => false;
    public virtual string[] DiscoverTopics() => [];
    public virtual List<TopicInfo> GatherTopicInfos(string? scopes = null) => [];
    public virtual IReadOnlyList<string> ResolveReadScopes(string? scopes = null) => ["local"];
}
