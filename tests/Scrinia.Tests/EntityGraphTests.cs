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

    // --- Build: uniform type:value node identity ---

    [Fact]
    public void Build_SingleTypeValue_UsesTypeScopedNode_AndAssociationReaches()
    {
        // person:alice and org:acme are each distinct type:value nodes. Mass from "alice"
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
    public void Build_DistinctTypesSharingValue_AreDistinctNodes_BlockFalseCrossTypeBridge()
    {
        // "paris" appears as BOTH person:paris and place:paris → two distinct type:value nodes
        // by construction (no conflict-detection needed). A query about the person "alice" must
        // NOT bleed into the place doc through the shared value. Contrast with the untyped graph
        // below, which DOES bridge.
        var typed = EntityGraph.Build(new (string, IReadOnlyList<string>?)[]
        {
            ("personDoc", new[] { "person:alice", "person:paris" }),  // alice co-occurs with Paris-the-person
            ("placeDoc", new[] { "place:louvre", "place:paris" }),    // louvre co-occurs with Paris-the-place
        });
        var typedScores = typed.Score("what did alice say", alpha: 0.15, iterations: 20);
        typedScores.Should().ContainKey("personDoc");
        typedScores.Should().NotContainKey("placeDoc");   // distinct nodes prevent the false bridge

        // Same topology with a single merged "paris" node (what the old untyped graph did):
        // the bridge fires and the place doc is wrongly reached.
        var merged = new EntityGraph();
        merged.AddDoc("personDoc", ["alice", "paris"]);
        merged.AddDoc("placeDoc", ["louvre", "paris"]);
        merged.Score("what did alice say", alpha: 0.15, iterations: 20)
            .Should().ContainKey("placeDoc");
    }

    [Fact]
    public void Build_TypeScopedNode_StillSeedsFromSurfaceForm()
    {
        // A type:value node (place:paris) must still seed off the bare query word "paris".
        var g = EntityGraph.Build(new (string, IReadOnlyList<string>?)[]
        {
            ("personDoc", new[] { "person:paris" }),
            ("placeDoc", new[] { "place:paris", "place:louvre" }),
        });

        var scores = g.Score("tell me about paris", alpha: 0.15, iterations: 20);

        // "paris" collides across types, so lexical seeding lights up both type:value variants.
        scores.Should().ContainKey("personDoc");
        scores.Should().ContainKey("placeDoc");
    }

    [Fact]
    public void Build_LegacyEntity_IsTypeScoped_NoLongerBridgesToTypedEntity()
    {
        // Under uniform (key,value) identity, the legacy entity: marker is a normal "entity:value"
        // node -- it no longer folds to a bare value, so entity:paris and place:paris are DISTINCT
        // nodes and do not link. (More conservative; migration converts legacy data anyway.)
        var g = EntityGraph.Build(new (string, IReadOnlyList<string>?)[]
        {
            ("legacyDoc", new[] { "entity:paris", "entity:zzsecret" }),
            ("placeDoc", new[] { "place:paris" }),
        });

        // Seed ONLY via the legacy-exclusive value "zzsecret". Old behavior bridged
        // legacyDoc -> (shared bare "paris") -> placeDoc; uniform identity makes entity:paris and
        // place:paris distinct, so placeDoc is unreachable.
        var scores = g.Score("tell me about zzsecret", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("legacyDoc");
        scores.Should().NotContainKey("placeDoc");   // no false bridge
    }

    [Fact]
    public void Build_NonRealTypeKeys_AreTypeScoped_DoNotCrossLinkOnSharedValue()
    {
        // status:done and outcome:done share the VALUE "done" but different keys -> distinct nodes.
        // Seeded via statusDoc-exclusive "zztarget": if the two collapsed to one "done" node,
        // outcomeDoc would be reached. Type-scoped, it must not be.
        var g = EntityGraph.Build(new (string, IReadOnlyList<string>?, IReadOnlyDictionary<string, string[]>?)[]
        {
            ("statusDoc", null, new Dictionary<string, string[]> { ["status"] = new[] { "done" }, ["thing"] = new[] { "zztarget" } }),
            ("outcomeDoc", null, new Dictionary<string, string[]> { ["outcome"] = new[] { "done" } }),
        });

        var scores = g.Score("what about zztarget", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("statusDoc");
        scores.Should().NotContainKey("outcomeDoc");   // no false bridge via a shared bare "done"
    }

    [Fact]
    public void Build_SameKeyValue_StillLinks()
    {
        var g = EntityGraph.Build(new (string, IReadOnlyList<string>?, IReadOnlyDictionary<string, string[]>?)[]
        {
            ("docA", null, new Dictionary<string, string[]> { ["status"] = new[] { "done" }, ["thing"] = new[] { "zzseed" } }),
            ("docB", null, new Dictionary<string, string[]> { ["status"] = new[] { "done" } }),
        });

        var scores = g.Score("about zzseed", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("docB");   // reached via zzseed -> docA -> status:done -> docB
    }

    [Fact]
    public void Build_NonRealTypeNode_SeedsFromBareValue()
    {
        // SurfaceOf now strips ANY type prefix, so a status:done node seeds from the query word "done".
        var g = EntityGraph.Build(new (string, IReadOnlyList<string>?, IReadOnlyDictionary<string, string[]>?)[]
        {
            ("doc", null, new Dictionary<string, string[]> { ["status"] = new[] { "done" } }),
        });

        var scores = g.Score("is it done yet", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("doc");   // seeded via SurfaceOf("status:done") == "done"
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

    [Fact]
    public void ParseEntityAttributes_YieldsEveryKey_NotJustEntityKeys()
    {
        var attributes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["person"] = new[] { "maria" },   // a known entity key
            ["status"] = new[] { "done" },    // an arbitrary key — must ALSO become a node now
        };

        var pairs = EntityGraph.ParseEntityAttributes(attributes).ToList();

        pairs.Should().Contain(("person", "maria"));
        pairs.Should().Contain(("status", "done"));
    }
}
