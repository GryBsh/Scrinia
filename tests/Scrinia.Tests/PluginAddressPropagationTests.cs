using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.TestDoubles;
using Xunit;

namespace Scrinia.Tests;

public sealed class PluginAddressPropagationTests
{
    [Fact]
    public async Task Event_sink_receives_one_canonical_ephemeral_address_for_both_aliases()
    {
        var sink = new CapturingSink();
        var composite = new CompositeEventSink([sink]);
        var store = new FakeMemoryStore();

        await composite.OnStoredAsync(MemoryAddress.Parse("/temp/probe"), ["stored"], store, default);
        await composite.OnAppendedAsync(MemoryAddress.Parse("~probe"), "appended", store, default);
        await composite.OnForgottenAsync(MemoryAddress.Parse("/temp/probe"), true, store, default);

        sink.Addresses.Should().HaveCount(3);
        sink.Addresses.Should().OnlyContain(address =>
            address.Kind == MemoryAddressKind.Ephemeral && address.Value == "probe");
    }

    [Fact]
    public void Plugin_store_resolves_typed_ephemeral_address_without_string_reparsing()
    {
        var store = new FakeMemoryStore();

        store.ResolveAddress(MemoryAddress.Parse("/temp/probe"))
            .Should().Be(("ephemeral", "probe"));
        store.ResolveAddress(MemoryAddress.Parse("~probe"))
            .Should().Be(("ephemeral", "probe"));
    }

    private sealed class CapturingSink : IMemoryEventSink
    {
        public List<MemoryAddress> Addresses { get; } = [];

        public Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct)
        {
            Addresses.Add(address);
            return Task.CompletedTask;
        }

        public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct)
        {
            Addresses.Add(address);
            return Task.CompletedTask;
        }

        public Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct)
        {
            Addresses.Add(address);
            return Task.CompletedTask;
        }
    }
}
