using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.TestDoubles;

/// <summary>
/// A do-nothing <see cref="IMemoryStore"/> — every member returns an empty or default result.
/// <para>Members are <c>virtual</c> so a test can subclass and override just the one or two it
/// cares about, rather than hand-writing a full member set per double.</para>
/// <para><b>The filesystem-layout members throw</b> rather than answering with a plausible path.
/// This double has no workspace, and a fabricated one would be worse than a refusal: a test that
/// reached <see cref="WorkspaceRoot"/> unnoticed would read and write real files under whatever
/// directory was invented for it. Until the remote store was dropped these members lived on a
/// separate <c>ILocalMemoryStore</c> that this type deliberately did not implement, so the
/// compiler caught the same mistake; with one interface it is caught on the first call instead.
/// A test that needs them overrides them, or uses a real <c>FileMemoryStore</c> over a temp
/// directory.</para>
/// </summary>
public class FakeMemoryStore : IMemoryStore
{
    public virtual ValueTask<ArtifactEntry?> ReadAsync(MemoryAddress address, CancellationToken ct = default) =>
        ValueTask.FromException<ArtifactEntry?>(new NotSupportedException("Fake store does not support typed reads."));
    public virtual ValueTask<ArtifactEntry> WriteAsync(MemoryAddress address, MemoryWrite write, CancellationToken ct = default) =>
        ValueTask.FromException<ArtifactEntry>(new NotSupportedException("Fake store does not support typed writes."));
    public virtual ValueTask DeleteAsync(MemoryAddress address, CancellationToken ct = default) =>
        ValueTask.FromException(new NotSupportedException("Fake store does not support typed deletes."));
    public virtual (string Scope, string Subject) ParseQualifiedName(string name) =>
        ResolveAddress(MemoryAddress.Parse(name));
    public virtual (string Scope, string Subject) ResolveAddress(MemoryAddress address) =>
        address.Kind == MemoryAddressKind.Ephemeral ? ("ephemeral", address.Value) :
        ResolvePersistentAddress(address);
    private static (string Scope, string Subject) ResolvePersistentAddress(MemoryAddress address)
    {
        var segments = address.Value.TrimStart('/').Split('/');
        return segments.Length == 1
            ? ("local", segments[0])
            : ($"local:{string.Join('/', segments[..^1])}", segments[^1]);
    }
    public virtual string FormatQualifiedName(string scope, string subject) => $"{scope}:{subject}";
    public virtual bool IsEphemeral(string name) => MemoryAddress.Parse(name).Kind == MemoryAddressKind.Ephemeral;
    public virtual string SanitizeName(string name) => name;
    // Derives nothing, like every other member here: the returned entry echoes the write's own name
    // and chunk count so a test can tell which call produced it, and overriding is how a test that
    // cares about derivation gets a real answer.
    public virtual Task<ArtifactEntry> StoreAsync(MemoryWrite write, CancellationToken ct = default) =>
        Task.FromResult(new ArtifactEntry(write.Name, "", 0, write.Content.Count, DateTimeOffset.UtcNow));
    public virtual Task<ArtifactEntry> AppendAsync(MemoryWrite write, CancellationToken ct = default) =>
        Task.FromResult(new ArtifactEntry(write.Name, "", 0, write.Content.Count, DateTimeOffset.UtcNow));
    public virtual IReadOnlyList<ArtifactEntry> LoadIndex(string scope = "local") => [];
    public virtual void Upsert(ArtifactEntry entry, string scope = "local") { }
    public virtual bool Remove(string name, string scope = "local") => false;
    public virtual List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes = null) => [];
    public virtual IReadOnlyList<SearchResult> SearchAll(SearchQuery query) => [];
    public virtual void RememberEphemeral(string key, EphemeralEntry entry) { }
    public virtual bool ForgetEphemeral(string key) => false;
    public virtual EphemeralEntry? GetEphemeral(string key) => null;
    public virtual void ArchiveVersion(string subject, string scope = "local") { }
    public virtual Task<string> ReadArtifactAsync(MemoryAddress address, CancellationToken ct = default) =>
        Task.FromException<string>(new NotSupportedException("Fake store does not support artifact reads."));
    public virtual Task WriteArtifactAsync(string subject, string scope, string artifactText,
        CancellationToken ct = default) => Task.CompletedTask;
    public virtual bool DeleteArtifact(string subject, string scope) => false;
    public virtual string[] DiscoverTopics() => [];
    public virtual List<TopicInfo> GatherTopicInfos(IReadOnlyList<string>? scopes = null) => [];
    public virtual IReadOnlyList<string> ResolveReadScopes(IReadOnlyList<string>? scopes = null) => ["local"];

    // ── Filesystem layout: no workspace, so no answer. See the type summary. ──

    public virtual string WorkspaceRoot => throw NoWorkspace(nameof(WorkspaceRoot));
    public virtual string ArtifactPath(string name, string scope = "local") => throw NoWorkspace(nameof(ArtifactPath));
    public virtual string FindArtifactPath(string subject, string normalizedScope) => throw NoWorkspace(nameof(FindArtifactPath));
    public virtual string GetStoreDirForScope(string scope) => throw NoWorkspace(nameof(GetStoreDirForScope));
    public virtual List<(string Name, string FilePath)> ListTopicArtifacts(string topicScope) => throw NoWorkspace(nameof(ListTopicArtifacts));
    public virtual void ImportTopicEntries(string topicScope, List<ArtifactEntry> entries,
        Dictionary<string, byte[]> artifactBytes, bool overwrite) => throw NoWorkspace(nameof(ImportTopicEntries));

    private static NotSupportedException NoWorkspace(string member) => new(
        $"{nameof(FakeMemoryStore)}.{member} has no workspace on disk. Override it on a subclass, "
        + "or drive this path through a real FileMemoryStore over a temp directory.");
}
