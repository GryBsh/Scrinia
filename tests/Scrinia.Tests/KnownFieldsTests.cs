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
}
