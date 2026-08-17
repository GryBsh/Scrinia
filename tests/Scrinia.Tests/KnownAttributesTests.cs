using FluentAssertions;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Tests for <see cref="KnownAttributes"/> — the single schema point for attributes:
/// which keys are multi-valued (entity + reference) and how merges behave per class.
/// </summary>
public sealed class KnownAttributesTests
{
    [Theory]
    [InlineData("person"), InlineData("place"), InlineData("org"), InlineData("thing"), InlineData("topic")]
    [InlineData("file"), InlineData("ref")]
    [InlineData("PERSON")] // case-insensitive
    public void IsMultiValued_TrueForEntityAndReferenceKeys(string key) =>
        KnownAttributes.IsMultiValued(key).Should().BeTrue();

    [Theory]
    [InlineData("status"), InlineData("verdict"), InlineData("version"), InlineData("why")]
    public void IsMultiValued_FalseForScalarKeys(string key) =>
        KnownAttributes.IsMultiValued(key).Should().BeFalse();

    [Fact]
    public void Merge_MultiValued_UnionsAndDedupes()
    {
        var existing = new Dictionary<string, string[]> { ["person"] = ["alice"] };
        var incoming = new Dictionary<string, string[]> { ["person"] = ["Bob", "ALICE"] };

        var merged = KnownAttributes.Merge(existing, incoming);

        merged!["person"].Should().BeEquivalentTo(["alice", "Bob"],
            because: "entity attributes accumulate across appends, deduped case-insensitively");
    }

    [Fact]
    public void Merge_Scalar_NewerWinsReplace()
    {
        var existing = new Dictionary<string, string[]> { ["status"] = ["active"], ["type"] = ["finding"] };
        var incoming = new Dictionary<string, string[]> { ["status"] = ["done"] };

        var merged = KnownAttributes.Merge(existing, incoming);

        merged!["status"].Should().BeEquivalentTo(["done"],
            because: "scalar attributes replace — a changed status must not accumulate stale values");
        merged["type"].Should().BeEquivalentTo(["finding"], because: "untouched keys survive");
    }

    [Fact]
    public void Merge_NullHandling_AndCap()
    {
        KnownAttributes.Merge(null, null).Should().BeNull();
        KnownAttributes.Merge(new() { ["a"] = ["1"] }, null).Should().ContainKey("a");
        KnownAttributes.Merge(null, new() { ["b"] = ["2"] }).Should().ContainKey("b");

        var big = Enumerable.Range(1, 40).ToDictionary(i => $"key{i}", i => new[] { $"v{i}" });
        KnownAttributes.Merge(null, big)!.Should().HaveCount(KnownAttributes.MaxAttributes);
    }

    [Fact]
    public void Merge_ValuesUnderOneKey_CappedAtMaxValuesPerKey()
    {
        var incoming = new Dictionary<string, string[]>
        {
            ["person"] = Enumerable.Range(1, KnownAttributes.MaxValuesPerKey + 44)
                .Select(i => $"p{i}").ToArray(),
        };

        var merged = KnownAttributes.Merge(null, incoming);

        merged!["person"].Should().HaveCount(KnownAttributes.MaxValuesPerKey,
            because: "local Merge must cap a single key's values the same way the wire-level " +
                     "400 does, so local and remote accept the same input by construction");
    }

    // EntityGraph.Build/Score were deleted as dead code (replaced in production by
    // EntityFactStore.ScoringAdjacency + PersonalizedPageRank + EntityQuerySeeder — what
    // EntityGraphScoreContributor actually runs). These two tests are re-expressed against that
    // live path; only EntityGraph.ParseEntityKeywords/ParseEntityAttributes (called below) survive
    // on EntityGraph itself.

    // Shared with EntityGraphTests and StateMarkerNotAnEntityTests: see EntityGraphTestFixture,
    // which builds stores through the real EntityFactDerivation instead of transcribing its body.
    private static EntityFactStore StoreOf(
        params (string DocKey, string[]? Keywords, IReadOnlyDictionary<string, string[]>? Attributes)[] docs)
        => EntityGraphTestFixture.StoreOf(docs);

    private static IReadOnlyDictionary<string, double> ScoreQuery(
        EntityFactStore store, string query, double alpha, int iterations)
        => EntityGraphTestFixture.ScoreQuery(store, query, alpha, iterations);

    [Fact]
    public void EntityGraph_AttributesAndLegacyKeywords_ProduceIdenticalGraphs()
    {
        var fromKeywords = StoreOf(
            ("a", ["person:maria", "org:acme"], null),
            ("b", ["person:maria"], null));
        var fromAttributes = StoreOf(
            ("a", null, new Dictionary<string, string[]> { ["person"] = ["Maria"], ["org"] = ["Acme"] }),
            ("b", null, new Dictionary<string, string[]> { ["person"] = ["maria"] }));

        fromAttributes.Entities().Should().HaveCount(fromKeywords.Entities().Count);
        fromAttributes.ScoringAdjacency().DocToEntities.Count.Should().Be(
            fromKeywords.ScoringAdjacency().DocToEntities.Count);

        var scoresKw = ScoreQuery(fromKeywords, "tell me about maria", 0.15, 20);
        var scoresAttr = ScoreQuery(fromAttributes, "tell me about maria", 0.15, 20);
        scoresAttr.Should().BeEquivalentTo(scoresKw,
            because: "an attributes entry and a legacy key:value keyword entry must graph identically");
    }

    [Fact]
    public void EntityGraph_MixedDoc_UnionsBothSources()
    {
        var store = StoreOf(("a", ["person:maria"], new Dictionary<string, string[]> { ["org"] = ["acme"] }));

        store.Entities().Should().HaveCount(2, because: "keywords ∪ attributes on the same doc");
    }

    [Fact]
    public void AttributeWeight_IsParityOneForKnownAndUnknownKeys()
    {
        // Parity: the table exists but starts neutral — known and arbitrary keys weigh the
        // same (1.0) so this change alters no existing score. Curation values come later.
        KnownAttributes.AttributeWeight("person").Should().Be(1.0);
        KnownAttributes.AttributeWeight("status").Should().Be(1.0);   // arbitrary key
        KnownAttributes.AttributeWeight("PERSON").Should().Be(1.0);   // case-insensitive
    }

    [Fact]
    public void RecommendedKeys_AreTheGuidedSet()
    {
        KnownAttributes.RecommendedKeys.Should().BeEquivalentTo(
            new[] { "person", "place", "org", "thing", "topic", "file", "ref", "review" });
    }

    [Fact]
    public void ReviewKey_IsReview()
    {
        KnownAttributes.ReviewKey.Should().Be("review");
    }

    [Fact]
    public void Mention_IsMultiValued_AndUnionsOnMerge()
    {
        KnownAttributes.MentionKey.Should().Be("mention");
        KnownAttributes.IsMultiValued("mention").Should().BeTrue();
        KnownAttributes.IsMultiValued("MENTION").Should().BeTrue(); // case-insensitive

        var existing = new Dictionary<string, string[]> { ["mention"] = ["seraphim"] };
        var incoming = new Dictionary<string, string[]> { ["mention"] = ["france"] };

        var merged = KnownAttributes.Merge(existing, incoming);

        merged!["mention"].Should().BeEquivalentTo(["seraphim", "france"]);
    }
}
