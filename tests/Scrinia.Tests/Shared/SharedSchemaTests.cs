using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Mcp.SharedContracts;

namespace Scrinia.Tests.Shared;

public sealed class SharedSchemaTests
{
    [Fact]
    public void Schemas_reject_unknown_properties_and_enforce_all_ranges_defaults_and_conditional_facts_arguments()
    {
        var contractTypes = new[]
        {
            typeof(SharedBaseSummary), typeof(SearchHit), typeof(FileDriftRef), typeof(ListItem),
            typeof(RecallResult), typeof(FactsEntitiesResult), typeof(FactsProfileResult),
            typeof(FactsIntersectResult), typeof(MutationResult), typeof(RefreshResult), typeof(SharedWarning)
        };

        contractTypes.Should().OnlyContain(type => type.IsClass);
        typeof(SharedTools).GetMethod(nameof(SharedTools.Search))!.GetParameters()
            .Select(parameter => parameter.Name).Should().ContainInOrder("query", "base", "limit", "pathPrefix", "attributeFilter", "context", "excludeTopics", "cancellationToken");
        typeof(SharedTools).GetMethod(nameof(SharedTools.List))!.GetParameters()
            .Select(parameter => parameter.Name).Should().ContainInOrder("base", "offset", "limit", "mode", "excludeTopics", "cancellationToken");
        typeof(SharedTools).GetMethod(nameof(SharedTools.Facts))!.GetParameters()
            .Select(parameter => parameter.Name).Should().ContainInOrder("base", "op", "entity", "entities", "key", "limit", "excludeTopics", "cancellationToken");

        typeof(SharedTools).GetMethod(nameof(SharedTools.Search))!.GetParameters()[2].DefaultValue.Should().Be(20);
        typeof(SharedTools).GetMethod(nameof(SharedTools.List))!.GetParameters()[1].DefaultValue.Should().Be(0);
        typeof(SharedTools).GetMethod(nameof(SharedTools.List))!.GetParameters()[2].DefaultValue.Should().Be(50);
        typeof(SharedTools).GetMethod(nameof(SharedTools.List))!.GetParameters()[3].DefaultValue.Should().Be("summary");
        typeof(SharedTools).GetMethod(nameof(SharedTools.Facts))!.GetParameters()[5].DefaultValue.Should().Be(50);
        typeof(SharedTools).GetMethod(nameof(SharedTools.Compact))!.GetParameters()[2].DefaultValue.Should().Be(0);

        var deserialize = () => JsonSerializer.Deserialize<SharedWarning>(
            "{\"base\":\"alpha\",\"code\":\"x\",\"diagnostic\":\"y\",\"extra\":true}",
            SharedDtoJsonContext.Default.SharedWarning);
        deserialize.Should().Throw<JsonException>();
    }
}
