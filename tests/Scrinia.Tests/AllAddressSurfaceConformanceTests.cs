using System.Reflection;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class AllAddressSurfaceConformanceTests
{
    [Fact]
    public void Event_sink_callbacks_and_artifact_source_use_memory_address()
    {
        foreach (var callback in typeof(IMemoryEventSink).GetMethods())
            callback.GetParameters().First().ParameterType.Should().Be<MemoryAddress>(callback.Name);

        var addressProperties = new[]
        {
            typeof(ArtifactEntry).GetProperty(nameof(ArtifactEntry.Address))!,
            typeof(MemoryWrite).GetProperty(nameof(MemoryWrite.Address))!,
            typeof(MemorySearchRequest).GetProperty(nameof(MemorySearchRequest.Address))!,
            typeof(MemoryListRequest).GetProperty(nameof(MemoryListRequest.Prefix))!
        };

        addressProperties.Should().OnlyContain(property =>
            property.PropertyType == typeof(MemoryAddress) || property.PropertyType == typeof(MemoryAddress?));
    }

    [Fact]
    public void Public_contract_methods_have_no_uri_parameters()
    {
        var types = new[]
        {
            typeof(IMemoryStore), typeof(IPluginStore), typeof(IMemoryEventSink),
            typeof(ArtifactEntry), typeof(MemoryWrite)
        };

        foreach (var method in types.SelectMany(type => type.GetMethods(BindingFlags.Public |
                     BindingFlags.Instance | BindingFlags.Static)))
        {
            method.GetParameters().Should().NotContain(parameter =>
                parameter.ParameterType == typeof(Uri) ||
                parameter.ParameterType == typeof(string) &&
                (parameter.Name == "uri" || parameter.Name == "artifactUri" || parameter.Name == "nameOrUri"));
        }
    }
}
