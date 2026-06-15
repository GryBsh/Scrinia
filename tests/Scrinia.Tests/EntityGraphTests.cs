using FluentAssertions;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Tests for <see cref="EntityGraph"/> — the bipartite entity graph + Personalized
/// PageRank behind the entity-graph retrieval signal. The core claim it rests on is that
/// PPR can surface a memory that shares NO query overlap but is associated to a query
/// entity via a shared entity — these tests pin that down on tiny, hand-checkable graphs.
/// </summary>
public sealed class EntityGraphTests
{
    // Shared fixture:
    //   docA: {alice, acme}     docB: {acme, bob}     docC: {carol}
    // Query "alice ..." seeds only `alice`. Mass must reach docB (via alice→docA→acme→docB)
    // even though docB has nothing to do with "alice" lexically; docC (carol) is an island.
    private static EntityGraph BuildFixture()
    {
        var g = new EntityGraph();
        g.AddDoc("docA", ["alice", "acme"]);
        g.AddDoc("docB", ["acme", "bob"]);
        g.AddDoc("docC", ["carol"]);
        return g;
    }

    [Fact]
    public void Score_ReachesAssociatedMemory_WithNoQueryOverlap()
    {
        var g = BuildFixture();

        var scores = g.Score("what did alice discover", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("docB");
        scores["docB"].Should().BeGreaterThan(0);
        scores.Should().NotContainKey("docC");
    }

    [Fact]
    public void Score_DirectlySeededMemory_OutranksTwoHopMemory()
    {
        var g = BuildFixture();

        var scores = g.Score("alice", alpha: 0.15, iterations: 20);

        scores["docA"].Should().BeGreaterThan(scores["docB"]);
    }

    [Fact]
    public void Score_NoSeedMatch_ReturnsEmpty()
    {
        var g = BuildFixture();

        var scores = g.Score("nothing here matches any entity", alpha: 0.15, iterations: 20);

        scores.Should().BeEmpty();
    }

    [Fact]
    public void Score_SeedsMultiWordEntity_BySubstring()
    {
        var g = new EntityGraph();
        g.AddDoc("reno", ["home renovation"]);
        g.AddDoc("unrelated", ["taxes"]);

        var scores = g.Score("what's my budget for the home renovation", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("reno");
        scores["reno"].Should().BeGreaterThan(0);
        scores.Should().NotContainKey("unrelated");
    }

    [Fact]
    public void Score_SeedsMultiTokenEntity_WhenAllTokensPresentOutOfOrder()
    {
        var g = new EntityGraph();
        g.AddDoc("doc", ["acme corp"]);

        var scores = g.Score("the corp known as acme", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("doc");
    }

    [Fact]
    public void Score_AllReachedMassesArePositiveAndFinite()
    {
        var g = BuildFixture();

        var scores = g.Score("alice and acme", alpha: 0.15, iterations: 20);

        scores.Values.Should().OnlyContain(v => v > 0 && !double.IsNaN(v) && !double.IsInfinity(v));
    }

    [Fact]
    public void Reset_ClearsAllNodes()
    {
        var g = BuildFixture();
        g.EntityCount.Should().BeGreaterThan(0);

        g.Reset();

        g.EntityCount.Should().Be(0);
        g.DocCount.Should().Be(0);
        g.Score("alice", alpha: 0.15, iterations: 20).Should().BeEmpty();
    }

    [Fact]
    public void AddDoc_IsCaseInsensitiveAndDedups()
    {
        var g = new EntityGraph();
        g.AddDoc("doc", ["Alice", "alice", "ALICE"]);

        g.EntityCount.Should().Be(1);
        g.Score("alice", alpha: 0.15, iterations: 20).Should().ContainKey("doc");
    }

    [Fact]
    public void ParseTypedEntities_KeepsRealTypesAndLegacyEntity_ValuesWhole_InOrder()
    {
        EntityGraph.ParseTypedEntities(
                ["person:Maria", "topic:cooking", "place:home renovation", "entity:acme"])
            .Should().Equal(
                ("person", "maria"), ("topic", "cooking"),
                ("place", "home renovation"), ("entity", "acme"));
    }

    [Theory]
    [InlineData("date:2027-01-01")]   // unknown type
    [InlineData("random:thing")]      // unknown type
    [InlineData("plainkeyword")]      // no prefix
    [InlineData("person:")]           // empty value
    [InlineData(":maria")]            // empty prefix
    public void ParseTypedEntities_DropsUnknownOrEmpty(string keyword)
    {
        EntityGraph.ParseTypedEntities([keyword]).Should().BeEmpty();
    }

    // --- Build: conflict-driven node identity ---

    [Fact]
    public void Build_SingleTypeValue_DoesNotSplit_AndAssociationReaches()
    {
        // alice and acme are each claimed by exactly one type → bare nodes. Mass from "alice"
        // must still reach docB via alice→docA→acme→docB, exactly like the untyped fixture.
        var g = EntityGraph.Build(new (string, IReadOnlyList<string>?)[]
        {
            ("docA", new[] { "person:alice", "org:acme" }),
            ("docB", new[] { "org:acme", "person:bob" }),
        });

        var scores = g.Score("what did alice do", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("docB");
        scores["docB"].Should().BeGreaterThan(0);
    }

    [Fact]
    public void Build_ConflictingTypes_SplitNodes_BlockFalseCrossTypeBridge()
    {
        // "paris" is claimed as BOTH person and place → split into person:paris / place:paris.
        // A query about the person "alice" must NOT bleed into the place doc through the shared
        // name. Contrast with the untyped graph below, which DOES bridge.
        var typed = EntityGraph.Build(new (string, IReadOnlyList<string>?)[]
        {
            ("personDoc", new[] { "person:alice", "person:paris" }),  // alice co-occurs with Paris-the-person
            ("placeDoc", new[] { "place:louvre", "place:paris" }),    // louvre co-occurs with Paris-the-place
        });
        var typedScores = typed.Score("what did alice say", alpha: 0.15, iterations: 20);
        typedScores.Should().ContainKey("personDoc");
        typedScores.Should().NotContainKey("placeDoc");   // split prevents the false bridge

        // Same topology with a single merged "paris" node (what the old untyped graph did):
        // the bridge fires and the place doc is wrongly reached.
        var merged = new EntityGraph();
        merged.AddDoc("personDoc", ["alice", "paris"]);
        merged.AddDoc("placeDoc", ["louvre", "paris"]);
        merged.Score("what did alice say", alpha: 0.15, iterations: 20)
            .Should().ContainKey("placeDoc");
    }

    [Fact]
    public void Build_SplitNode_StillSeedsFromSurfaceForm()
    {
        // A split node (place:paris) must still seed off the bare query word "paris".
        var g = EntityGraph.Build(new (string, IReadOnlyList<string>?)[]
        {
            ("personDoc", new[] { "person:paris" }),
            ("placeDoc", new[] { "place:paris", "place:louvre" }),
        });

        var scores = g.Score("tell me about paris", alpha: 0.15, iterations: 20);

        // "paris" collides across types, so lexical seeding lights up both split variants.
        scores.Should().ContainKey("personDoc");
        scores.Should().ContainKey("placeDoc");
    }

    [Fact]
    public void Build_LegacyEntity_FoldsToBareValue_AndNeverSplits()
    {
        // entity:paris is untyped; even though place:paris exists, the legacy marker folds to a
        // bare "paris" node and does not itself trigger or join the person/place split set.
        var g = EntityGraph.Build(new (string, IReadOnlyList<string>?)[]
        {
            ("legacyDoc", new[] { "entity:paris", "entity:berlin" }),
            ("placeDoc", new[] { "place:paris" }),
        });

        // Only one real type (place) claims "paris", so there's no conflict and no split — the
        // bare "paris" node from both docs links them, and "berlin" seeds the legacy doc.
        g.Score("about berlin", alpha: 0.15, iterations: 20).Should().ContainKey("legacyDoc");
    }

    [Fact]
    public void Build_TopicIsAGraphNode_AssociatesMemoriesByTheme()
    {
        // Topics participate in the graph now (not BM25-only): two memories sharing a topic are
        // associated even with no other overlap.
        var g = EntityGraph.Build(new (string, IReadOnlyList<string>?)[]
        {
            ("docA", new[] { "person:alice", "topic:home renovation" }),
            ("docB", new[] { "person:bob", "topic:home renovation" }),
        });

        var scores = g.Score("alice", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("docB");   // reached via alice→docA→home renovation→docB
    }
}
