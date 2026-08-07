using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
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

    [Fact]
    public async Task RendersEntityFactsBlock_BeforeResults()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;
        store.Upsert(Note("m1", ("person", ["jolene"]), ("place", ["france"])), "local:findings");
        store.Upsert(Note("m2", ("person", ["jolene"]), ("place", ["france"])), "local:findings");

        string yaml = (await new ScriniaMcpTools().Search("jolene", cancellationToken: default)).ToYaml();

        yaml.Should().Contain("entity_facts:");
        yaml.Should().Contain("person:jolene");
        yaml.Should().Contain("2 memories");
        yaml.Should().Contain("with place:france (2)");
        yaml.IndexOf("entity_facts:", StringComparison.Ordinal)
            .Should().BeLessThan(yaml.IndexOf("results:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SingleMention_UsesSingular_AndNoCoOccurrenceClause()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;
        store.Upsert(Note("solo", ("person", ["jolene"])), "local:findings");

        string yaml = (await new ScriniaMcpTools().Search("jolene", cancellationToken: default)).ToYaml();

        yaml.Should().Contain("1 memory");
        yaml.Should().NotContain("; with ");
    }

    [Fact]
    public async Task NoEntityNamed_NoBlock()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;
        store.Upsert(Note("m1", ("person", ["jolene"])), "local:findings");

        // "seraphim" is only in ContentPreview (so it matches for a results list) but is NOT an
        // entity attribute on any memory, so the seeder names no entity -> no block.
        string yaml = (await new ScriniaMcpTools().Search("seraphim", cancellationToken: default)).ToYaml();

        yaml.Should().NotContain("entity_facts:");
        yaml.Should().Contain("results:");
    }

    /// <summary>Stable projection of a match for equality comparison — name + score. The summary
    /// side-channel must never perturb which memories are returned or their order.</summary>
    private static (string Name, double Score) Project(SearchResult r) => r switch
    {
        EntryResult er => (er.Item.Entry.Name, er.Score),
        ChunkEntryResult cr => (cr.ParentItem.Entry.Name, cr.Score),
        TopicResult tr => (tr.TopicName, tr.Score),
        _ => (r.ToString() ?? "", r.Score),
    };

    [Fact]
    public async Task SurfaceEntityFactsFlag_NeverPerturbsMatches()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;
        store.Upsert(Note("m1", ("person", ["jolene"]), ("place", ["france"])), "local:findings");
        store.Upsert(Note("m2", ("person", ["jolene"])), "local:findings");
        store.Upsert(Note("m3", ("place", ["france"])), "local:findings");

        var withFlag = await ScriniaMcpTools.ExecuteSearchAsync(
            store, "what about jolene", scopes: null, limit: 20, excludeTopics: null,
            context: null, pathPrefix: null, attributeFilter: null, cancellationToken: default,
            surfaceEntityFacts: true);

        var withoutFlag = await ScriniaMcpTools.ExecuteSearchAsync(
            store, "what about jolene", scopes: null, limit: 20, excludeTopics: null,
            context: null, pathPrefix: null, attributeFilter: null, cancellationToken: default,
            surfaceEntityFacts: false);

        withFlag.EntityFacts.Should().NotBeEmpty();
        withoutFlag.EntityFacts.Should().BeEmpty();

        var flagOn = withFlag.Matches.Select(Project).ToList();
        var flagOff = withoutFlag.Matches.Select(Project).ToList();

        // Same result set, same order (name sequence) — the hard invariant under test.
        flagOn.Select(p => p.Name).Should().Equal(flagOff.Select(p => p.Name),
            "the entity_facts side-channel must never change which memories are selected or their order");

        // Scores should match too, but compared with tolerance: WeightedAttributeScorer's recency
        // term is computed from DateTimeOffset.UtcNow at scoring time, so two sequential real-clock
        // calls a few milliseconds apart legitimately produce tiny floating-point differences in the
        // decayed recency term — that jitter is not the ranking perturbation this test guards against.
        for (int i = 0; i < flagOn.Count; i++)
            flagOn[i].Score.Should().BeApproximately(flagOff[i].Score, precision: 1e-4,
                because: "scores must be the same (modulo real-clock recency jitter) with the flag on or off");
    }

    [Fact]
    public async Task DateSpan_RendersYyyyMmDdArrowFormat()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;
        var early = new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.Zero);
        var late = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);

        store.Upsert(new ArtifactEntry("m1", "uri", 1, 1, early,
            ContentPreview: "jolene france", Attributes: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["person"] = ["jolene"],
            }), "local:findings");
        store.Upsert(new ArtifactEntry("m2", "uri", 1, 1, late,
            ContentPreview: "jolene france", Attributes: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["person"] = ["jolene"],
            }), "local:findings");

        string yaml = (await new ScriniaMcpTools().Search("jolene", cancellationToken: default)).ToYaml();

        yaml.Should().Contain("entity_facts:");
        yaml.Should().MatchRegex(@"\d{4}-\d{2}-\d{2}→\d{4}-\d{2}-\d{2}",
            "the entity_facts block must render a first->last date span in yyyy-MM-dd->yyyy-MM-dd form");
        // Pin the actual values too: Upsert preserves the explicit CreatedAt we passed in, so the
        // span reflects the two distinct dates rather than collapsing them.
        yaml.Should().Contain("2026-01-04→2026-07-20");
    }

    [Fact]
    public async Task MentionAttribute_SurfacesUniformly_LikeAnyOtherEntityKey()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;
        store.Upsert(Note("m1", ("mention", ["jolene"])), "local:findings");

        var outcome = await ScriniaMcpTools.ExecuteSearchAsync(
            store, "jolene", scopes: null, limit: 20, excludeTopics: null,
            context: null, pathPrefix: null, attributeFilter: null, cancellationToken: default,
            surfaceEntityFacts: true);

        outcome.EntityFacts.Should().Contain(f => f.Entity.Key == "mention" && f.Entity.Value == "jolene");

        string yaml = (await new ScriniaMcpTools().Search("jolene", cancellationToken: default)).ToYaml();
        yaml.Should().Contain("mention:jolene");
    }
}
