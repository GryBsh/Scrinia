using System.Reflection;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class NoObsoleteAddressSurfaceTests
{
    [Fact]
    public void Public_memory_contracts_do_not_expose_uri_or_resolver_members()
    {
        var contractTypes = new[]
        {
            typeof(ArtifactEntry), typeof(IMemoryStore), typeof(IPluginStore),
            typeof(IMemoryEventSink), typeof(MemoryWrite), typeof(MemorySearchRequest),
            typeof(MemoryListRequest)
        };

        foreach (var type in contractTypes)
        {
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Should().NotContain(property => property.Name == "Uri" || property.Name == "ArtifactUri");
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Should().NotContain(method => method.Name == "ArtifactUri" || method.Name == "ResolveArtifactAsync");
        }
    }

    [Fact]
    public void Artifact_and_write_constructors_require_typed_addresses()
    {
        typeof(ArtifactEntry).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Should().OnlyContain(constructor =>
                constructor.GetParameters().First().ParameterType == typeof(MemoryAddress));
        typeof(MemoryWrite).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Should().OnlyContain(constructor =>
                constructor.GetParameters().First().ParameterType == typeof(MemoryAddress));
        typeof(MemoryWrite).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Should().NotContain(constructor =>
                constructor.GetParameters().Length > 0 &&
                constructor.GetParameters()[0].ParameterType == typeof(string));
    }

    [Fact]
    public void Public_store_and_plugin_routes_accept_typed_addresses()
    {
        typeof(IMemoryStore).GetMethod(nameof(IMemoryStore.ReadAsync))!
            .GetParameters().First().ParameterType.Should().Be<MemoryAddress>();
        typeof(IMemoryStore).GetMethod(nameof(IMemoryStore.WriteAsync))!
            .GetParameters().First().ParameterType.Should().Be<MemoryAddress>();
        typeof(IMemoryStore).GetMethod(nameof(IMemoryStore.DeleteAsync))!
            .GetParameters().First().ParameterType.Should().Be<MemoryAddress>();
        typeof(IPluginStore).GetMethod(nameof(IPluginStore.ReadArtifactAsync))!
            .GetParameters().First().ParameterType.Should().Be<MemoryAddress>();
    }

    [Fact]
    public void Physical_store_helpers_are_not_published_as_logical_address_routes()
    {
        typeof(FileMemoryStore).GetMethod(nameof(FileMemoryStore.ParseQualifiedName))
            .Should().BeNull();
        typeof(FileMemoryStore).GetMethod(nameof(FileMemoryStore.IsEphemeral))
            .Should().BeNull();
        typeof(MemoryArtifactSource).GetMethod(nameof(MemoryArtifactSource.IsEphemeral))
            .Should().BeNull();
        typeof(MemoryVectorIndexer).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Should().NotContain(method => method.Name == "RemoveAsync" &&
                method.GetParameters().Length > 0 &&
                method.GetParameters()[0].ParameterType == typeof(string));
    }
}
