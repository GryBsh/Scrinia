using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Scrinia.Core.Configuration;
using Scrinia.Mcp.SharedContracts;

namespace Scrinia.Tests.Shared;

public sealed class SharedSchemaTests
{
    [Fact]
    public void Shared_dto_serialization_uses_lower_case_enums_omits_nulls_and_rejects_unknown_properties()
    {
        var baseSummary = JsonSerializer.Serialize(
            new SharedBaseSummary("alpha", null, SharedBaseAvailability.Available, null),
            SharedDtoJsonContext.Default.SharedBaseSummary);
        baseSummary.Should().Be("{\"base\":\"alpha\",\"availability\":\"available\"}");

        var warningJson = JsonSerializer.Serialize(
            new SharedWarning("alpha", "DEGRADED", "safe"), SharedDtoJsonContext.Default.SharedWarning);
        warningJson.Should().Be("{\"base\":\"alpha\",\"code\":\"DEGRADED\",\"diagnostic\":\"safe\"}");
        var mutationJson = JsonSerializer.Serialize(
            new MutationResult("alpha", null, null, MutationKind.None, "generation", []),
            SharedDtoJsonContext.Default.MutationResult);
        mutationJson.Should().Be("{\"base\":\"alpha\",\"mutation\":\"none\",\"generation\":\"generation\",\"manifest\":[]}");

        foreach (var availability in Enum.GetValues<SharedBaseAvailability>())
            JsonSerializer.Serialize(availability, SharedDtoJsonContext.Default.SharedBaseAvailability)
                .Should().MatchRegex("^\\\"(available|unavailable|stale|conflicted)\\\"$");
        foreach (var kind in Enum.GetValues<SharedHitKind>())
            JsonSerializer.Serialize(kind, SharedDtoJsonContext.Default.SharedHitKind)
                .Should().MatchRegex("^\\\"(entry|chunk)\\\"$");
        foreach (var kind in Enum.GetValues<SharedListKind>())
            JsonSerializer.Serialize(kind, SharedDtoJsonContext.Default.SharedListKind)
                .Should().MatchRegex("^\\\"persistent\\\"$");
        foreach (var drift in Enum.GetValues<SharedDrift>())
            JsonSerializer.Serialize(drift, SharedDtoJsonContext.Default.SharedDrift)
                .Should().MatchRegex("^\\\"(unchanged|changed|missing)\\\"$");
        foreach (var operation in Enum.GetValues<FactsOperation>())
            JsonSerializer.Serialize(operation, SharedDtoJsonContext.Default.FactsOperation)
                .Should().MatchRegex("^\\\"(entities|profile|intersect)\\\"$");
        foreach (var mutation in Enum.GetValues<MutationKind>())
            JsonSerializer.Serialize(mutation, SharedDtoJsonContext.Default.MutationKind)
                .Should().MatchRegex("^\\\"(applied|none)\\\"$");

        var representativeDtos = new (string Json, JsonTypeInfo TypeInfo)[]
        {
            ("{\"base\":\"alpha\",\"availability\":\"available\",\"extra\":true}", SharedDtoJsonContext.Default.SharedBaseSummary),
            ("{\"base\":\"alpha\",\"code\":\"x\",\"diagnostic\":\"y\",\"extra\":true}", SharedDtoJsonContext.Default.SharedWarning),
            ("{\"base\":\"alpha\",\"path\":\"/note\",\"kind\":\"entry\",\"chunk\":null,\"score\":1,\"preview\":\"x\",\"attributes\":{},\"extra\":true}", SharedDtoJsonContext.Default.SearchHit)
        };
        foreach (var (json, typeInfo) in representativeDtos)
            AssertUnknownPropertyIsRejected(json, typeInfo);
    }

    [Fact]
    public void Shared_tool_signatures_expose_owned_defaults_and_facts_arguments_without_claiming_handler_validation()
    {
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
    }

    private static void AssertUnknownPropertyIsRejected(string json, JsonTypeInfo typeInfo)
    {
        var deserialize = () => JsonSerializer.Deserialize(json, typeInfo);
        deserialize.Should().Throw<JsonException>();
    }
}
