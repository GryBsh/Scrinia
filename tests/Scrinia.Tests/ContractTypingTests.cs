using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.TestDoubles;
using Xunit;

namespace Scrinia.Tests;

public sealed class ContractTypingTests
{
    [Fact]
    public void ArtifactEntry_uses_logical_address_and_opaque_artifact_key()
    {
        var properties = typeof(ArtifactEntry).GetProperties();

        properties.Single(property => property.Name == nameof(ArtifactEntry.Address))
            .PropertyType.Should().Be<MemoryAddress>();
        properties.Single(property => property.Name == nameof(ArtifactEntry.ArtifactKey))
            .PropertyType.Should().Be<string>();
        properties.Should().NotContain(property => property.Name == "Name" || property.Name == "Uri");
    }

    [Fact]
    public void Store_contracts_accept_typed_addresses_and_expose_no_uri_contract()
    {
        var methods = typeof(IMemoryStore).GetMethods();

        methods.Single(method => method.Name == "ReadAsync" &&
                method.GetParameters().First().ParameterType == typeof(MemoryAddress))
            .GetParameters().First().ParameterType.Should().Be<MemoryAddress>();
        methods.Single(method => method.Name == "WriteAsync" &&
                method.GetParameters().First().ParameterType == typeof(MemoryAddress))
            .GetParameters().First().ParameterType.Should().Be<MemoryAddress>();
        methods.Single(method => method.Name == "DeleteAsync" &&
                method.GetParameters().First().ParameterType == typeof(MemoryAddress))
            .GetParameters().First().ParameterType.Should().Be<MemoryAddress>();

        methods.Should().NotContain(method => method.Name == "ArtifactUri");
        typeof(IPluginStore).GetMethods().Should().NotContain(method =>
            method.Name == "ResolveArtifactAsync" || method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(string) &&
                parameter.Name == "name" || parameter.Name == "subject" || parameter.Name == "qualifiedName"));
        typeof(FileMemoryStore).GetMethods().Should().NotContain(method =>
            method.Name == "ArtifactUri" || method.Name == "ResolveArtifactAsync" ||
            method.GetParameters().Any(parameter => parameter.ParameterType == typeof(Uri)));
    }

    [Fact]
    public void Search_and_list_requests_use_typed_address_filters()
    {
        typeof(MemorySearchRequest).GetProperty(nameof(MemorySearchRequest.Address))!
            .PropertyType.Should().Be<MemoryAddress?>();
        typeof(MemoryListRequest).GetProperty(nameof(MemoryListRequest.Prefix))!
            .PropertyType.Should().Be<MemoryAddress?>();
    }

    [Fact]
    public void Event_sink_callbacks_receive_memory_addresses()
    {
        foreach (var method in typeof(IMemoryEventSink).GetMethods())
            method.GetParameters().First().ParameterType.Should().Be<MemoryAddress>();
    }

    [Fact]
    public void Artifact_key_rejects_uri_and_physical_path_syntax()
    {
        var address = MemoryAddress.Parse("/notes/design");

        var uri = () => new ArtifactEntry(address, "file:///tmp/design.nmp2", 1, 1, DateTimeOffset.UtcNow);
        var rooted = () => new ArtifactEntry(address, @"C:\\store\\design.nmp2", 1, 1, DateTimeOffset.UtcNow);

        uri.Should().Throw<ArgumentException>();
        rooted.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Typed_request_address_filters_are_exposed_by_store_contracts()
    {
        var address = MemoryAddress.Parse("/findings/design");
        var search = new MemorySearchRequest("design") { Address = address };
        var list = new MemoryListRequest { Prefix = address };

        search.Address.Should().Be(address);
        list.Prefix.Should().Be(address);
        typeof(IMemoryStore).GetMethod("SearchAll", [typeof(MemorySearchRequest)]).Should().NotBeNull();
        typeof(IMemoryStore).GetMethod("ListAsync", [typeof(MemoryListRequest)]).Should().NotBeNull();
    }

    [Fact]
    public void Typed_request_addresses_propagate_into_search_and_list_filtering()
    {
        var matching = MemoryAddress.Parse("/notes/design");
        var exactPrefix = MemoryAddress.Parse("/notes");
        var descendant = MemoryAddress.Parse("/notes/design");
        var prefixCollision = MemoryAddress.Parse("/notes2/design");
        var other = MemoryAddress.Parse("/archive/design");
        var store = new AddressCapturingStore(
            new ArtifactEntry(exactPrefix, "artifacts/notes.nmp2", 1, 1, DateTimeOffset.UnixEpoch),
            new ArtifactEntry(descendant, "artifacts/design.nmp2", 1, 1, DateTimeOffset.UnixEpoch),
            new ArtifactEntry(prefixCollision, "artifacts/notes2.nmp2", 1, 1, DateTimeOffset.UnixEpoch),
            new ArtifactEntry(other, "artifacts/archive.nmp2", 1, 1, DateTimeOffset.UnixEpoch));

        ((IMemoryStore)store).SearchAll(new MemorySearchRequest("design") { Address = matching });
        var listed = ((IMemoryStore)store).ListAsync(new MemoryListRequest { Prefix = exactPrefix });

        store.SearchAddress.Should().Be(matching);
        listed.Select(item => item.Entry.Address).Should().Equal(exactPrefix, descendant);
    }

    [Fact]
    public async Task Composite_event_sink_dispatches_typed_callbacks()
    {
        var sink = new TypedSink();
        var address = MemoryAddress.Parse("~scratch");

        var composite = (IMemoryEventSink)new CompositeEventSink([sink]);
        await composite.OnStoredAsync(address, ["content"], new AddressCapturingStore(), CancellationToken.None);
        await composite.OnAppendedAsync(address, "content", new AddressCapturingStore(), CancellationToken.None);
        await composite.OnForgottenAsync(address, true, new AddressCapturingStore(), CancellationToken.None);

        sink.StoredAddress.Should().Be(address);
        sink.AppendedAddress.Should().Be(address);
        sink.ForgottenAddress.Should().Be(address);
    }

    [Fact]
    public void Ephemeral_prefix_matches_only_the_same_canonical_address()
    {
        var store = new AddressCapturingStore(
            new ArtifactEntry(MemoryAddress.Parse("~scratch"), "ephemeral", 1, 1, DateTimeOffset.UnixEpoch));

        var listed = ((IMemoryStore)store).ListAsync(new MemoryListRequest { Prefix = MemoryAddress.Parse("~scratch") });

        listed.Should().ContainSingle().Which.Entry.Address.Should().Be(MemoryAddress.Parse("~scratch"));
    }

    private sealed class AddressCapturingStore(params ArtifactEntry[] entries) : FakeMemoryStore
    {
        public MemoryAddress? SearchAddress { get; private set; }
        public override IReadOnlyList<SearchResult> SearchAll(SearchQuery query)
        {
            SearchAddress = query.Address;
            return [];
        }

        public override List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes = null) =>
            entries.Select(entry => new ScopedArtifact("local", entry)).ToList();
    }

    private sealed class TypedSink : IMemoryEventSink
    {
        public MemoryAddress? StoredAddress { get; private set; }
        public MemoryAddress? AppendedAddress { get; private set; }
        public MemoryAddress? ForgottenAddress { get; private set; }

        public Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct)
        {
            StoredAddress = address;
            return Task.CompletedTask;
        }

        public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct)
        {
            AppendedAddress = address;
            return Task.CompletedTask;
        }

        public Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct)
        {
            ForgottenAddress = address;
            return Task.CompletedTask;
        }
    }
}
