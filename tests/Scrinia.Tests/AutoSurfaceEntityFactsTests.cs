using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

public class AutoSurfaceEntityFactsTests
{
    private static ArtifactEntry Note(string name, params (string Key, string[] Vals)[] attrs)
    {
        var d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in attrs) d[k] = v;
        return new ArtifactEntry(name, "uri", 1, 1, DateTimeOffset.UtcNow,
            ContentPreview: "jolene france seraphim", Attributes: d);
    }

    [Fact]
    public async Task QueryNamesEntity_ProducesSummary()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;
        store.Upsert(Note("m1", ("person", ["jolene"]), ("place", ["france"])), "local:findings");
        store.Upsert(Note("m2", ("person", ["jolene"])), "local:findings");

        var outcome = await ScriniaMcpTools.ExecuteSearchAsync(
            store, "what about jolene", scopes: null, limit: 20, excludeTopics: null,
            context: null, pathPrefix: null, attributeFilter: null, cancellationToken: default,
            surfaceEntityFacts: true);

        outcome.EntityFacts.Should().Contain(f => f.Entity.Key == "person" && f.Entity.Value == "jolene"
            && f.MentionCount == 2);
    }

    [Fact]
    public async Task QueryNamesNothing_NoSummary()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;
        store.Upsert(Note("m1", ("person", ["jolene"])), "local:findings");

        var outcome = await ScriniaMcpTools.ExecuteSearchAsync(
            store, "zzqqxx", null, 20, null, null, null, null, default, surfaceEntityFacts: true);

        outcome.EntityFacts.Should().BeEmpty();
    }

    [Fact]
    public async Task FlagOff_NoSummary_EvenWhenEntityNamed()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;
        store.Upsert(Note("m1", ("person", ["jolene"])), "local:findings");

        var outcome = await ScriniaMcpTools.ExecuteSearchAsync(
            store, "jolene", null, 20, null, null, null, null, default); // surfaceEntityFacts defaults false

        outcome.EntityFacts.Should().BeEmpty();
        outcome.Matches.Should().NotBeEmpty(); // matches still returned as before
    }

    [Fact]
    public async Task ExcludedTopic_DoesNotCountTowardSummary()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;
        // jolene appears once in findings and once in an excluded 'plan' topic scope.
        store.Upsert(Note("keep", ("person", ["jolene"])), "local:findings");
        store.Upsert(Note("hidden", ("person", ["jolene"])), "local:plan");

        var outcome = await ScriniaMcpTools.ExecuteSearchAsync(
            store, "jolene", scopes: null, limit: 20, excludeTopics: "plan",
            context: null, pathPrefix: null, attributeFilter: null, cancellationToken: default,
            surfaceEntityFacts: true);

        outcome.EntityFacts.Single(f => f.Entity.Value == "jolene").MentionCount
            .Should().Be(1, "the excluded 'plan' scope must not contribute to the count");
    }
}
