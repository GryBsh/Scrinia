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
    public void EntityGraph_AttributesAndLegacyKeywords_ProduceIdenticalGraphs()
    {
        var fromKeywords = EntityGraph.Build(
            [("local|a", (IReadOnlyList<string>?)["person:maria", "org:acme"], null),
             ("local|b", ["person:maria"], null)]);
        var fromAttributes = EntityGraph.Build(
            [("local|a", null, (IReadOnlyDictionary<string, string[]>?)new Dictionary<string, string[]>
                 { ["person"] = ["Maria"], ["org"] = ["Acme"] }),
             ("local|b", null, new Dictionary<string, string[]> { ["person"] = ["maria"] })]);

        fromAttributes.EntityCount.Should().Be(fromKeywords.EntityCount);
        fromAttributes.DocCount.Should().Be(fromKeywords.DocCount);

        var scoresKw = fromKeywords.Score("tell me about maria", 0.15, 20);
        var scoresF = fromAttributes.Score("tell me about maria", 0.15, 20);
        scoresF.Should().BeEquivalentTo(scoresKw,
            because: "an attributes entry and a legacy typed-keyword entry must graph identically");
    }

    [Fact]
    public void EntityGraph_MixedDoc_UnionsBothSources()
    {
        var graph = EntityGraph.Build(
            [("local|a", (IReadOnlyList<string>?)["person:maria"],
              (IReadOnlyDictionary<string, string[]>?)new Dictionary<string, string[]> { ["org"] = ["acme"] })]);
        graph.EntityCount.Should().Be(2, because: "keywords ∪ attributes on the same doc");
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
}
