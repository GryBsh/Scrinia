using FluentAssertions;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Tests for <see cref="KnownFields"/> — the single schema point for unified fields:
/// which keys are multi-valued (entity + reference) and how merges behave per class.
/// </summary>
public sealed class KnownFieldsTests
{
    [Theory]
    [InlineData("person"), InlineData("place"), InlineData("org"), InlineData("thing"), InlineData("topic")]
    [InlineData("file"), InlineData("ref")]
    [InlineData("PERSON")] // case-insensitive
    public void IsMultiValued_TrueForEntityAndReferenceKeys(string key) =>
        KnownFields.IsMultiValued(key).Should().BeTrue();

    [Theory]
    [InlineData("status"), InlineData("verdict"), InlineData("version"), InlineData("why")]
    public void IsMultiValued_FalseForScalarKeys(string key) =>
        KnownFields.IsMultiValued(key).Should().BeFalse();

    [Fact]
    public void Merge_MultiValued_UnionsAndDedupes()
    {
        var existing = new Dictionary<string, string[]> { ["person"] = ["alice"] };
        var incoming = new Dictionary<string, string[]> { ["person"] = ["Bob", "ALICE"] };

        var merged = KnownFields.Merge(existing, incoming);

        merged!["person"].Should().BeEquivalentTo(["alice", "Bob"],
            because: "entity fields accumulate across appends, deduped case-insensitively");
    }

    [Fact]
    public void Merge_Scalar_NewerWinsReplace()
    {
        var existing = new Dictionary<string, string[]> { ["status"] = ["active"], ["type"] = ["finding"] };
        var incoming = new Dictionary<string, string[]> { ["status"] = ["done"] };

        var merged = KnownFields.Merge(existing, incoming);

        merged!["status"].Should().BeEquivalentTo(["done"],
            because: "scalar fields replace — a changed status must not accumulate stale values");
        merged["type"].Should().BeEquivalentTo(["finding"], because: "untouched keys survive");
    }

    [Fact]
    public void Merge_NullHandling_AndCap()
    {
        KnownFields.Merge(null, null).Should().BeNull();
        KnownFields.Merge(new() { ["a"] = ["1"] }, null).Should().ContainKey("a");
        KnownFields.Merge(null, new() { ["b"] = ["2"] }).Should().ContainKey("b");

        var big = Enumerable.Range(1, 40).ToDictionary(i => $"key{i}", i => new[] { $"v{i}" });
        KnownFields.Merge(null, big)!.Should().HaveCount(KnownFields.MaxFields);
    }

    [Fact]
    public void EntityGraph_FieldsAndLegacyKeywords_ProduceIdenticalGraphs()
    {
        var fromKeywords = EntityGraph.Build(
            [("local|a", (IReadOnlyList<string>?)["person:maria", "org:acme"], null),
             ("local|b", ["person:maria"], null)]);
        var fromFields = EntityGraph.Build(
            [("local|a", null, (IReadOnlyDictionary<string, string[]>?)new Dictionary<string, string[]>
                 { ["person"] = ["Maria"], ["org"] = ["Acme"] }),
             ("local|b", null, new Dictionary<string, string[]> { ["person"] = ["maria"] })]);

        fromFields.EntityCount.Should().Be(fromKeywords.EntityCount);
        fromFields.DocCount.Should().Be(fromKeywords.DocCount);

        var scoresKw = fromKeywords.Score("tell me about maria", 0.15, 20);
        var scoresF = fromFields.Score("tell me about maria", 0.15, 20);
        scoresF.Should().BeEquivalentTo(scoresKw,
            because: "a fields entry and a legacy typed-keyword entry must graph identically");
    }

    [Fact]
    public void EntityGraph_MixedDoc_UnionsBothSources()
    {
        var graph = EntityGraph.Build(
            [("local|a", (IReadOnlyList<string>?)["person:maria"],
              (IReadOnlyDictionary<string, string[]>?)new Dictionary<string, string[]> { ["org"] = ["acme"] })]);
        graph.EntityCount.Should().Be(2, because: "keywords ∪ fields on the same doc");
    }
}
