using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;

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
        public bool ReadCalled { get; private set; }

        public (string Scope, string Subject) ParseQualifiedName(string name)
        {
            int i = name.IndexOf(':', StringComparison.Ordinal);
            return i < 0 ? ("local", name) : (name[..i], name[(i + 1)..]);
        }

        public bool IsEphemeral(string name) => ephemeral;

        public (string Scope, string Subject) ResolveAddress(MemoryAddress address) =>
            address.Kind == MemoryAddressKind.Ephemeral ? ("ephemeral", address.Value) :
            ("local:memory/api", address.Value.TrimStart('/').Split('/')[^1]);

        public Task<string> ReadArtifactAsync(MemoryAddress address, CancellationToken ct = default)
        {
            ReadCalled = true;
            return Task.FromResult(artifact);
        }
    }

    [Fact]
    public async Task ReadForAppendAsync_AcceptsAStoreThatIsOnlyAnIPluginStore()
    {
        // A real encoded artifact, not "" — an empty artifact short-circuits
        // DecodeContentElements' IsNullOrWhiteSpace guard and never reaches the decoder, so this
        // exercises the decode path itself rather than only the guard in front of it.
        string artifact = Nmp2ChunkedEncoder.Encode("probe body");
        var store = new PluginStoreOnly(ephemeral: false, artifact: artifact);

        var elements = await MemoryArtifactSource.ReadForAppendAsync(
            store, MemoryAddress.Parse("/probe"), CancellationToken.None);
        elements.Should().Equal(["probe body"], because: "a non-empty artifact must decode back to its content elements");
        store.ReadCalled.Should().BeTrue("a non-ephemeral name reads by subject and scope");
    }

    [Fact]
    public async Task ReadForAppendAsync_RoutesAnEphemeralNameThroughTypedRead()
    {
        var store = new PluginStoreOnly(ephemeral: true, artifact: "");

        await MemoryArtifactSource.ReadForAppendAsync(store, MemoryAddress.Parse("~probe"), CancellationToken.None);

        store.ReadCalled.Should().BeTrue();
    }

    [Fact]
    public void ResolveAddress_PreservesCanonicalNamespaceForPluginOnlyStores()
    {
        var store = new PluginStoreOnly(ephemeral: false, artifact: "");

        store.ResolveAddress(MemoryAddress.Parse("/memory/api/notes"))
            .Should().Be(("local:memory/api", "notes"));
        store.ResolveAddress(MemoryAddress.Parse("~notes"))
            .Should().Be(("ephemeral", "notes"));
    }

    /// <summary>A plugin-shaped sink: records what it was handed, needs nothing beyond the contract.</summary>
    private sealed class NarrowSink : IMemoryEventSink
    {
        public string? LastStored { get; private set; }

        public Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct)
        {
            LastStored = address.Value;
            return Task.CompletedTask;
        }

        public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct)
            => Task.CompletedTask;

        public Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task ASinkCanBeDrivenWithAStoreThatIsOnlyAnIPluginStore()
    {
        var sink = new NarrowSink();
        var composite = new CompositeEventSink([sink]);

        await composite.OnStoredAsync(MemoryAddress.Parse("/probe"), ["x"], new PluginStoreOnly(false, ""), CancellationToken.None);

        sink.LastStored.Should().Be("/probe",
            because: "typed callbacks carry the canonical logical address");
    }
}
