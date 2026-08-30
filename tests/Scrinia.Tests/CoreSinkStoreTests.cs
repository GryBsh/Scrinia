using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

/// <summary>
/// Core's own handlers need more of the store than the sink contract declares, so they cast back
/// through CoreSinkStore.Require. It THROWS rather than skipping: silently not typing entities or
/// not maintaining cross-memory references is invisible data loss, and CompositeEventSink's
/// per-sink isolation already turns the throw into one logged sink failure.
/// </summary>
public class CoreSinkStoreTests
{
    private sealed class NotAMemoryStore : IPluginStore
    {
        public (string Scope, string Subject) ResolveAddress(MemoryAddress address) => ("local", address.Value);
        public Task<string> ReadArtifactAsync(MemoryAddress address, CancellationToken ct = default) => Task.FromResult("");
    }

    [Fact]
    public void Require_ReturnsTheStoreWhenItIsAFullMemoryStore()
    {
        using var scope = new TestHelpers.StoreScope();
        IPluginStore store = MemoryStoreContext.Current!;

        CoreSinkStore.Require(store, "TestHandler").Should().BeSameAs(store);
    }

    [Fact]
    public void Require_ThrowsNamingTheHandlerWhenTheStoreIsNarrow()
    {
        var act = () => CoreSinkStore.Require(new NotAMemoryStore(), "EntityTypingEventHandler");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*EntityTypingEventHandler*",
                because: "a bare cast failure would not say which handler could not run");
    }
}
