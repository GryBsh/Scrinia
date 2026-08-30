using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
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
        foreach (var method in typeof(IMemoryEventSink).GetMethods()
                     .Where(method => method.GetParameters().FirstOrDefault()?.ParameterType == typeof(MemoryAddress)))
            method.GetParameters().First().ParameterType.Should().Be<MemoryAddress>();
    }
}
