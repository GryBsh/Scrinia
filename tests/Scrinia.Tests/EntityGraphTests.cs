using FluentAssertions;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Tests for the entity-graph retrieval signal's building blocks. <see cref="EntityGraph"/>'s
/// instance side (Build/Score/AddDoc — the bipartite graph + Personalized PageRank walk it used
/// to carry directly) was deleted as dead code once the store-backed path
/// (<see cref="EntityFactStore.ScoringAdjacency"/> + <see cref="PersonalizedPageRank"/> +
/// <see cref="EntityQuerySeeder"/>, exactly what <see cref="EntityGraphScoreContributor"/> uses
/// in production) replaced it. These tests drive that same production path directly, so the core
/// claim it rests on is still pinned down: PPR can surface a memory that shares NO query overlap
/// but is associated to a query entity via a shared entity, on tiny, hand-checkable graphs. Only
/// <see cref="EntityGraph.ParseEntityKeywords"/> and <see cref="EntityGraph.ParseEntityAttributes"/>
/// survive on <see cref="EntityGraph"/> itself — they are exercised directly below, unchanged.
/// </summary>
public sealed class EntityGraphTests
{
    // ── Helpers: build an EntityFactStore the way EntityFactDerivation does, then run the same
    //    seed → PPR walk EntityGraphScoreContributor runs, but returning the raw (unnormalized,
    //    unscaled) PPR mass — matching what the deleted EntityGraph.Score used to return. ──

    private static EntityFactStore StoreOf(
        params (string DocKey, string[]? Keywords, IReadOnlyDictionary<string, string[]>? Attributes)[] docs) =>
        new(docs.Select(d => new MemoryEntities(
            "s", d.DocKey, default,
            EntityGraph.ParseEntityKeywords(d.Keywords)
                .Concat(EntityGraph.ParseEntityAttributes(d.Attributes))
                .Select(t => EntityRef.Of(t.Type, t.Value))
                .Distinct()
                .ToArray())));

    private static IReadOnlyDictionary<string, double> ScoreQuery(
        EntityFactStore store, string query, double alpha, int iterations)
    {
        var (entityToDocs, docToEntities) = store.ScoringAdjacency();
        var seeds = EntityQuerySeeder.Seed(query, entityToDocs.Keys);
        return seeds.Count == 0
            ? new Dictionary<string, double>()
            : PersonalizedPageRank.Score(entityToDocs, docToEntities, seeds, alpha, iterations);
    }

    // Shared fixture. "entity:" is the legacy untyped marker ParseEntityKeywords already
    // recognizes, so raw values round-trip as their own type:value node without inventing a
    // synthetic scheme.
    //   docA: {alice, acme}     docB: {acme, bob}     docC: {carol}
    // Doc keys land as "s|docA" etc. (MemoryEntities.ScoringKey = "{Scope}|{Subject}").
    // Query "alice ..." seeds only `alice`. Mass must reach docB (via alice→docA→acme→docB)
    // even though docB has nothing to do with "alice" lexically; docC (carol) is an island.
    private static EntityFactStore BuildFixtureStore() => StoreOf(
        ("docA", ["entity:alice", "entity:acme"], null),
        ("docB", ["entity:acme", "entity:bob"], null),
        ("docC", ["entity:carol"], null));

    [Fact]
    public void Score_ReachesAssociatedMemory_WithNoQueryOverlap()
    {
        var store = BuildFixtureStore();

        var scores = ScoreQuery(store, "what did alice discover", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("s|docB");
        scores["s|docB"].Should().BeGreaterThan(0);
        scores.Should().NotContainKey("s|docC");
    }

    [Fact]
    public void Score_DirectlySeededMemory_OutranksTwoHopMemory()
    {
        var store = BuildFixtureStore();

        var scores = ScoreQuery(store, "alice", alpha: 0.15, iterations: 20);

        scores["s|docA"].Should().BeGreaterThan(scores["s|docB"]);
    }

    [Fact]
    public void Score_NoSeedMatch_ReturnsEmpty()
    {
        var store = BuildFixtureStore();

        var scores = ScoreQuery(store, "nothing here matches any entity", alpha: 0.15, iterations: 20);

        scores.Should().BeEmpty();
    }

    [Fact]
    public void Score_SeedsMultiWordEntity_BySubstring()
    {
        var store = StoreOf(
            ("reno", ["entity:home renovation"], null),
            ("unrelated", ["entity:taxes"], null));

        var scores = ScoreQuery(store, "what's my budget for the home renovation", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("s|reno");
        scores["s|reno"].Should().BeGreaterThan(0);
        scores.Should().NotContainKey("s|unrelated");
    }

    [Fact]
    public void Score_SeedsMultiTokenEntity_WhenAllTokensPresentOutOfOrder()
    {
        var store = StoreOf(("doc", ["entity:acme corp"], null));

        var scores = ScoreQuery(store, "the corp known as acme", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("s|doc");
    }

    [Fact]
    public void Score_AllReachedMassesArePositiveAndFinite()
    {
        var store = BuildFixtureStore();

        var scores = ScoreQuery(store, "alice and acme", alpha: 0.15, iterations: 20);

        scores.Values.Should().OnlyContain(v => v > 0 && !double.IsNaN(v) && !double.IsInfinity(v));
    }

    [Fact]
    public void EntityValueCaseVariants_Dedupe()
    {
        // EntityRef.Of lowercases, and EntityFactStore.Distinct()s a memory's entities, so
        // case-only variants of the same value collapse to one entity node — the store-backed
        // equivalent of the deleted EntityGraph.AddDoc's case-insensitive dedup.
        var store = StoreOf(("doc", ["entity:Alice", "entity:alice", "entity:ALICE"], null));

        store.Entities().Should().HaveCount(1);
        ScoreQuery(store, "alice", alpha: 0.15, iterations: 20).Should().ContainKey("s|doc");
    }

    [Fact]
    public void ParseEntityKeywords_KeepsRealTypesAndLegacyEntity_ValuesWhole_InOrder()
    {
        EntityGraph.ParseEntityKeywords(
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
    public void ParseEntityKeywords_DropsUnknownOrEmpty(string keyword)
    {
        EntityGraph.ParseEntityKeywords([keyword]).Should().BeEmpty();
    }

    // --- Uniform type:value node identity (formerly Build's contract; now EntityFactStore's) ---

    [Fact]
    public void SingleTypeValue_UsesTypeScopedNode_AndAssociationReaches()
    {
        // person:alice and org:acme are each distinct type:value nodes. Mass from "alice"
        // must still reach docB via alice→docA→acme→docB, exactly like the untyped fixture.
        var store = StoreOf(
            ("docA", ["person:alice", "org:acme"], null),
            ("docB", ["org:acme", "person:bob"], null));

        var scores = ScoreQuery(store, "what did alice do", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("s|docB");
        scores["s|docB"].Should().BeGreaterThan(0);
    }

    [Fact]
    public void DistinctTypesSharingValue_AreDistinctNodes_BlockFalseCrossTypeBridge()
    {
        // "paris" appears as BOTH person:paris and place:paris → two distinct type:value nodes
        // by construction (no conflict-detection needed). A query about the person "alice" must
        // NOT bleed into the place doc through the shared value. Contrast with the flat/untyped
        // graph below, which DOES bridge.
        var typed = StoreOf(
            ("personDoc", ["person:alice", "person:paris"], null),  // alice co-occurs with Paris-the-person
            ("placeDoc", ["place:louvre", "place:paris"], null));   // louvre co-occurs with Paris-the-place
        var typedScores = ScoreQuery(typed, "what did alice say", alpha: 0.15, iterations: 20);
        typedScores.Should().ContainKey("s|personDoc");
        typedScores.Should().NotContainKey("s|placeDoc");   // distinct nodes prevent the false bridge

        // Same topology with a single merged "paris" node (what a flat, untyped identity scheme
        // — the deleted EntityGraph.AddDoc's raw-string node keys — would do): the bridge fires
        // and the place doc is wrongly reached. EntityFactStore never constructs a merged node
        // (every entity ref is a (key, value) pair), so this counterfactual is built directly
        // over the PPR primitive rather than through the store.
        var mergedEntityToDocs = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["alice"] = new HashSet<string> { "personDoc" },
            ["paris"] = new HashSet<string> { "personDoc", "placeDoc" },
            ["louvre"] = new HashSet<string> { "placeDoc" },
        };
        var mergedDocToEntities = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["personDoc"] = new HashSet<string> { "alice", "paris" },
            ["placeDoc"] = new HashSet<string> { "louvre", "paris" },
        };
        var mergedSeeds = EntityQuerySeeder.Seed("what did alice say", mergedEntityToDocs.Keys);
        PersonalizedPageRank.Score(mergedEntityToDocs, mergedDocToEntities, mergedSeeds, 0.15, 20)
            .Should().ContainKey("placeDoc");
    }

    [Fact]
    public void TypeScopedNode_StillSeedsFromSurfaceForm()
    {
        // A type:value node (place:paris) must still seed off the bare query word "paris".
        var store = StoreOf(
            ("personDoc", ["person:paris"], null),
            ("placeDoc", ["place:paris", "place:louvre"], null));

        var scores = ScoreQuery(store, "tell me about paris", alpha: 0.15, iterations: 20);

        // "paris" collides across types, so lexical seeding lights up both type:value variants.
        scores.Should().ContainKey("s|personDoc");
        scores.Should().ContainKey("s|placeDoc");
    }

    [Fact]
    public void LegacyEntity_IsTypeScoped_NoLongerBridgesToTypedEntity()
    {
        // Under uniform (key,value) identity, the legacy entity: marker is a normal "entity:value"
        // node -- it no longer folds to a bare value, so entity:paris and place:paris are DISTINCT
        // nodes and do not link. (More conservative; migration converts legacy data anyway.)
        var store = StoreOf(
            ("legacyDoc", ["entity:paris", "entity:zzsecret"], null),
            ("placeDoc", ["place:paris"], null));

        // Seed ONLY via the legacy-exclusive value "zzsecret". Old behavior bridged
        // legacyDoc -> (shared bare "paris") -> placeDoc; uniform identity makes entity:paris and
        // place:paris distinct, so placeDoc is unreachable.
        var scores = ScoreQuery(store, "tell me about zzsecret", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("s|legacyDoc");
        scores.Should().NotContainKey("s|placeDoc");   // no false bridge
    }

    [Fact]
    public void NonRealTypeKeys_AreTypeScoped_DoNotCrossLinkOnSharedValue()
    {
        // status:done and outcome:done share the VALUE "done" but different keys -> distinct nodes.
        // Seeded via statusDoc-exclusive "zztarget": if the two collapsed to one "done" node,
        // outcomeDoc would be reached. Type-scoped, it must not be.
        var store = StoreOf(
            ("statusDoc", null, new Dictionary<string, string[]> { ["status"] = ["done"], ["thing"] = ["zztarget"] }),
            ("outcomeDoc", null, new Dictionary<string, string[]> { ["outcome"] = ["done"] }));

        var scores = ScoreQuery(store, "what about zztarget", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("s|statusDoc");
        scores.Should().NotContainKey("s|outcomeDoc");   // no false bridge via a shared bare "done"
    }

    [Fact]
    public void SameKeyValue_StillLinks()
    {
        var store = StoreOf(
            ("docA", null, new Dictionary<string, string[]> { ["status"] = ["done"], ["thing"] = ["zzseed"] }),
            ("docB", null, new Dictionary<string, string[]> { ["status"] = ["done"] }));

        var scores = ScoreQuery(store, "about zzseed", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("s|docB");   // reached via zzseed -> docA -> status:done -> docB
    }

    [Fact]
    public void NonRealTypeNode_SeedsFromBareValue()
    {
        // SurfaceOf strips ANY type prefix, so a status:done node seeds from the query word "done".
        var store = StoreOf(("doc", null, new Dictionary<string, string[]> { ["status"] = ["done"] }));

        var scores = ScoreQuery(store, "is it done yet", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("s|doc");   // seeded via SurfaceOf("status:done") == "done"
    }

    [Fact]
    public void TopicIsAGraphNode_AssociatesMemoriesByTheme()
    {
        // Topics participate in the graph (not BM25-only): two memories sharing a topic are
        // associated even with no other overlap.
        var store = StoreOf(
            ("docA", ["person:alice", "topic:home renovation"], null),
            ("docB", ["person:bob", "topic:home renovation"], null));

        var scores = ScoreQuery(store, "alice", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("s|docB");   // reached via alice→docA→home renovation→docB
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
