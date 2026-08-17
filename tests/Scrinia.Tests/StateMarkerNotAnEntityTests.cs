using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// A boolean state marker must never become an entity-graph node.
///
/// <para><see cref="EntityGraph.ParseEntityAttributes"/> deliberately yields EVERY attribute key, so
/// that arbitrary keys can form graph associations rather than only the five blessed
/// <see cref="KnownAttributes.EntityKeys"/>. That is right for keys whose values NAME something —
/// two memories sharing <c>status:done</c> really are related by it. It breaks down for a marker
/// whose value is a constant: every entry carrying <c>orphan:true</c> shares one value, so it forms
/// a single hub node linking every unrelated entry that has it, and the hub grows as the corpus
/// ages.</para>
///
/// <para>Measured before <see cref="KnownAttributes.StateMarkerKeys"/> existed, over six orphans
/// plus one non-orphan sharing <c>person:zanzibar</c>: a query naming the shared person gave five
/// semantically unrelated orphans 12.14 entity-graph points each purely for being orphans, and the
/// query "is that true" scored every orphan 31.52–50.00 — because
/// <c>EntityQuerySeeder.SurfaceOf("orphan:true")</c> is <c>"true"</c> and seeding is substring-based.
/// The three tests below pin each of those, plus the node's absence itself.</para>
///
/// <para>This is about the GRAPH only. A state marker still seeds the term-frequency index and still
/// answers an <c>attributeFilter</c> — it is barred from being an entity, not from being searchable.
/// </para>
/// </summary>
public sealed class StateMarkerNotAnEntityTests
{
    private static Dictionary<string, string[]> Orphan(params (string Key, string Value)[] extra)
    {
        var attributes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [KnownAttributes.OrphanKey] = ["true"],
        };
        foreach (var (key, value) in extra) attributes[key] = [value];
        return attributes;
    }

    // EntityGraph.Build/Score were deleted as dead code (replaced in production by
    // EntityFactStore.ScoringAdjacency + PersonalizedPageRank + EntityQuerySeeder — what
    // EntityGraphScoreContributor actually runs). The corpus and every test below are
    // re-expressed against that live path; only the final test's direct call to
    // EntityGraph.ParseEntityAttributes is unchanged, since that static parser survives.

    // Shared with EntityGraphTests and StateMarkerNotAnEntityTests: see EntityGraphTestFixture,
    // which builds stores through the real EntityFactDerivation instead of transcribing its body.
    private static EntityFactStore StoreOf(
        params (string DocKey, string[]? Keywords, IReadOnlyDictionary<string, string[]>? Attributes)[] docs)
        => EntityGraphTestFixture.StoreOf(docs);

    private static IReadOnlyDictionary<string, double> ScoreQuery(
        EntityFactStore store, string query, double alpha, int iterations)
        => EntityGraphTestFixture.ScoreQuery(store, query, alpha, iterations);

    /// <summary>Six orphans, one of which shares a real person with a seventh, non-orphan memory —
    /// the shape the divergence was measured on.</summary>
    private static EntityFactStore BuildCorpus() => StoreOf(
        ("orphan-with-person", null, Orphan(("person", "zanzibar"))),
        ("orphan-a", null, Orphan(("topic", "apricots"))),
        ("orphan-b", null, Orphan(("topic", "bicycles"))),
        ("orphan-c", null, Orphan(("topic", "cement"))),
        ("orphan-d", null, Orphan(("topic", "dolphins"))),
        ("orphan-e", null, Orphan(("topic", "engines"))),
        ("linked", null, new Dictionary<string, string[]> { ["person"] = ["zanzibar"] }));

    [Fact]
    public void OrphanMarker_IsNotAGraphNode()
    {
        var store = BuildCorpus();

        // person:zanzibar plus five topic:* nodes. The marker would be a seventh — one hub node
        // shared by six of the seven memories, and it grows with every orphan the corpus gains.
        store.Entities().Should().HaveCount(6,
            because: "a value shared by every orphan is a hub, not an entity; counting the nodes " +
                     "is what catches it appearing, and the count is exact so a node cannot be " +
                     "added silently");

        // The corpus is genuinely graphed otherwise; without this the assertion above would pass
        // just as well against a build that produced nothing.
        store.ScoringAdjacency().DocToEntities.Count.Should().Be(7);
    }

    /// <summary>A query naming a real entity must reach the memories that carry it — and only
    /// those. Five of the six orphans have nothing to do with "zanzibar"; they used to collect
    /// 12.14 points each through the shared <c>orphan:true</c> hub.</summary>
    [Fact]
    public void QueryNamingAnEntity_DoesNotCollectUnrelatedOrphansThroughTheMarker()
    {
        var scores = ScoreQuery(BuildCorpus(), "zanzibar", alpha: 0.15, iterations: 20);

        scores.Keys.Should().Contain("s|linked");
        scores.Keys.Should().Contain("s|orphan-with-person",
            because: "this one genuinely carries person:zanzibar");

        foreach (string unrelated in new[] { "s|orphan-a", "s|orphan-b", "s|orphan-c", "s|orphan-d", "s|orphan-e" })
            scores.Should().NotContainKey(unrelated,
                because: "being an orphan is not a relationship to zanzibar, and routing one " +
                         "through a shared boolean is how five unrelated memories scored on a " +
                         "query that names none of them");
    }

    /// <summary>The marker's value is a generic English word, and seeding is substring-based over
    /// a node's value. So an ordinary sentence used to seed the hub and light up every orphan in
    /// the corpus at 31.52–50.00.</summary>
    [Theory]
    [InlineData("is that true")]
    [InlineData("true")]
    public void QueryContainingTheMarkerValue_SeedsNothing(string query)
    {
        var scores = ScoreQuery(BuildCorpus(), query, alpha: 0.15, iterations: 20);

        scores.Should().BeEmpty(
            because: "no node in this corpus is named by these words — the only thing that ever " +
                     "matched was the marker's own value, which is not an entity");
    }

    /// <summary>The complement, stated so the trade is explicit rather than implied: the marker is
    /// not reachable through the graph by its own name either. It never was — the seeder matches a
    /// node's VALUE, not its key, so <c>orphan:true</c> answered to "true" and not to "orphan".
    /// Nothing is lost here; the entry remains findable through BM25 and through an
    /// <c>attributeFilter</c>, which is where a state marker belongs.</summary>
    [Fact]
    public void QueryNamingTheMarkerItself_SeedsNothing()
    {
        ScoreQuery(BuildCorpus(), KnownAttributes.OrphanKey, alpha: 0.15, iterations: 20)
            .Should().BeEmpty();
    }

    /// <summary>The classification is what is under test, not the string. A second state marker
    /// added to the set must be excluded on the same terms, with no further code change — that is
    /// the difference between a rule and a special case.</summary>
    [Fact]
    public void TheExclusionIsDrivenByTheClassification_NotByTheKeyName()
    {
        KnownAttributes.IsStateMarker(KnownAttributes.OrphanKey).Should().BeTrue();
        KnownAttributes.IsStateMarker("ORPHAN").Should().BeTrue(
            because: "attribute keys are matched case-insensitively everywhere else");
        KnownAttributes.IsStateMarker("person").Should().BeFalse();
        KnownAttributes.IsStateMarker("status").Should().BeFalse(
            because: "an arbitrary key whose values name something stays in the graph — that " +
                     "uniformity is deliberate and this must not quietly narrow it");

        EntityGraph.ParseEntityAttributes(new Dictionary<string, string[]>
        {
            ["status"] = ["done"],
            [KnownAttributes.OrphanKey] = ["true"],
        }).Should().ContainSingle().Which.Should().Be(("status", "done"));
    }
}
