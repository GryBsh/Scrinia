using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Embeddings;

namespace Scrinia.Tests;

/// <summary>
/// The load-bearing assertion of this branch: code on the sink path must work with a store that
/// is ONLY an IPluginStore. PluginStoreOnly deliberately does NOT implement IMemoryStore — if
/// someone widens the sink path back to the full store, this file stops compiling, which is a
/// louder failure than an assertion.
/// </summary>
public class PluginStoreOnlySinkTests
{
    /// <summary>Records which read path was taken, so the ephemeral routing is pinned too.</summary>
    private sealed class PluginStoreOnly(bool ephemeral, string artifact) : IPluginStore
    {
        public bool ResolveCalled { get; private set; }
        public bool ReadCalled { get; private set; }

        public (string Scope, string Subject) ParseQualifiedName(string name)
        {
            int i = name.IndexOf(':', StringComparison.Ordinal);
            return i < 0 ? ("local", name) : (name[..i], name[(i + 1)..]);
        }

        public bool IsEphemeral(string name) => ephemeral;

        public Task<string> ResolveArtifactAsync(string nameOrArtifact, CancellationToken ct = default)
        {
            ResolveCalled = true;
            return Task.FromResult(artifact);
        }

        public Task<string> ReadArtifactAsync(string subject, string scope, CancellationToken ct = default)
        {
            ReadCalled = true;
            return Task.FromResult(artifact);
        }
    }

    [Fact]
    public async Task ReadForAppendAsync_AcceptsAStoreThatIsOnlyAnIPluginStore()
    {
        var store = new PluginStoreOnly(ephemeral: false, artifact: "");

        var (scope, subject, elements) = await MemoryArtifactSource.ReadForAppendAsync(
            store, "local:probe", CancellationToken.None);

        scope.Should().Be("local");
        subject.Should().Be("probe");
        elements.Should().BeEmpty("an empty artifact decodes to no elements");
        store.ReadCalled.Should().BeTrue("a non-ephemeral name reads by subject and scope");
        store.ResolveCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ReadForAppendAsync_RoutesAnEphemeralNameThroughResolve()
    {
        var store = new PluginStoreOnly(ephemeral: true, artifact: "");

        await MemoryArtifactSource.ReadForAppendAsync(store, "~probe", CancellationToken.None);

        store.ResolveCalled.Should().BeTrue(
            "ephemerality is a property of the NAME, so the ephemeral branch resolves rather " +
            "than reading by scope — narrowing the parameter must not change that routing");
        store.ReadCalled.Should().BeFalse();
    }
}
