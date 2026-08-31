using System.Text.Json.Nodes;
using FluentAssertions;
using Scrinia.Core.Bundles;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Pins the two pre-Merge-3 bundle keys that <see cref="BundleLegacyKeys"/> migrates. These are
/// the ONLY legacy keys it knows about; every other member of an entry passes through untouched,
/// which is what lets bundle deserialization stay total by construction.
/// </summary>
public sealed class BundleLegacyKeysTests
{
    private static JsonObject IndexWith(JsonObject entry) =>
        new() { ["entries"] = new JsonArray(entry) };

    [Fact]
    public void LegacyDescription_BecomesContentPreview()
    {
        var index = IndexWith(new JsonObject { ["name"] = "a", ["description"] = "old text" });

        BundleLegacyKeys.NormalizeIndex(index);

        index["entries"]![0]!["contentPreview"]!.GetValue<string>().Should().Be("old text");
        index["entries"]![0]!["description"].Should().BeNull("the legacy key is consumed, not left behind");
    }

    [Fact]
    public void ModernContentPreview_WinsOverLegacyDescription()
    {
        var index = IndexWith(new JsonObject
        {
            ["name"] = "a",
            ["description"] = "old text",
            ["contentPreview"] = "new text",
        });

        BundleLegacyKeys.NormalizeIndex(index);

        index["entries"]![0]!["contentPreview"]!.GetValue<string>().Should().Be("new text");
    }

    [Fact]
    public void LegacyTags_BecomeTheTagAttribute()
    {
        var index = IndexWith(new JsonObject
        {
            ["name"] = "a",
            ["tags"] = new JsonArray("alpha", "beta"),
        });

        BundleLegacyKeys.NormalizeIndex(index);

        var tag = index["entries"]![0]!["attributes"]![KnownAttributes.TagKey]!.AsArray();
        tag.Select(t => t!.GetValue<string>()).Should().Equal("alpha", "beta");
        index["entries"]![0]!["tags"].Should().BeNull();
    }

    [Fact]
    public void ModernTagAttribute_WinsOverLegacyTags()
    {
        var index = IndexWith(new JsonObject
        {
            ["name"] = "a",
            ["tags"] = new JsonArray("legacy"),
            ["attributes"] = new JsonObject { [KnownAttributes.TagKey] = new JsonArray("modern") },
        });

        BundleLegacyKeys.NormalizeIndex(index);

        var tag = index["entries"]![0]!["attributes"]![KnownAttributes.TagKey]!.AsArray();
        tag.Select(t => t!.GetValue<string>()).Should().Equal("modern");
    }

    [Fact]
    public void LegacyTags_FoldIntoAnExistingAttributesObject_WithoutDisturbingOtherKeys()
    {
        var index = IndexWith(new JsonObject
        {
            ["name"] = "a",
            ["tags"] = new JsonArray("alpha"),
            ["attributes"] = new JsonObject { ["topic"] = new JsonArray("retrieval") },
        });

        BundleLegacyKeys.NormalizeIndex(index);

        var attributes = index["entries"]![0]!["attributes"]!.AsObject();
        attributes["topic"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("retrieval");
        attributes[KnownAttributes.TagKey]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("alpha");
    }

    [Fact]
    public void BlankAndNonStringTags_AreDropped_AndAnAllBlankArrayCreatesNoAttribute()
    {
        var index = IndexWith(new JsonObject
        {
            ["name"] = "a",
            ["tags"] = new JsonArray("", "  ", null, 7),
        });

        BundleLegacyKeys.NormalizeIndex(index);

        index["entries"]![0]!["attributes"].Should().BeNull(
            "an all-blank tags array must fold to nothing, not to an empty attributes object");
    }

    [Fact]
    public void AModernEntry_GainsTypedAddressAndPortableArtifactKey()
    {
        var entry = new JsonObject
        {
            ["name"] = "a",
            ["contentPreview"] = "text",
            ["agentKeywords"] = new JsonArray("curated"),
            ["codeRefs"] = new JsonObject { ["src/x.cs"] = "deadbeef" },
        };
        var index = IndexWith(entry);
        BundleLegacyKeys.NormalizeIndex(index);

        var normalized = index["entries"]![0]!;
        normalized["address"]!.GetValue<string>().Should().Be("/a");
        normalized["artifactKey"]!.GetValue<string>().Should().Be("artifacts/bundle/0.nmp2");
        normalized["name"]!.GetValue<string>().Should().Be("a");
        normalized["contentPreview"]!.GetValue<string>().Should().Be("text");
    }

    [Fact]
    public void AnIndexWithNoEntriesArray_IsANoOp()
    {
        var index = new JsonObject { ["somethingElse"] = 1 };

        Action act = () => BundleLegacyKeys.NormalizeIndex(index);

        act.Should().NotThrow();
    }

    [Fact]
    public void EveryEntryInAMultiEntryIndex_IsNormalizedIndependently()
    {
        var index = new JsonObject
        {
            ["entries"] = new JsonArray(
                new JsonObject { ["name"] = "first", ["description"] = "old text" },
                new JsonObject { ["name"] = "second", ["tags"] = new JsonArray("alpha", "beta") },
                new JsonObject { ["name"] = "third", ["contentPreview"] = "already modern" })
        };

        BundleLegacyKeys.NormalizeIndex(index);

        var entries = index["entries"]!.AsArray();

        entries[0]!["contentPreview"]!.GetValue<string>().Should().Be("old text");
        entries[0]!["description"].Should().BeNull();

        var tag = entries[1]!["attributes"]![KnownAttributes.TagKey]!.AsArray();
        tag.Select(t => t!.GetValue<string>()).Should().Equal("alpha", "beta");
        entries[1]!["tags"].Should().BeNull();

        entries[2]!["contentPreview"]!.GetValue<string>().Should().Be("already modern");
        entries[2]!["attributes"].Should().BeNull();
    }

    [Fact]
    public void MixedTags_KeepOnlyTheNonBlankOnes_InOrder()
    {
        var index = IndexWith(new JsonObject
        {
            ["name"] = "a",
            ["tags"] = new JsonArray("", "alpha", "  ", "beta"),
        });

        BundleLegacyKeys.NormalizeIndex(index);

        var tag = index["entries"]![0]!["attributes"]![KnownAttributes.TagKey]!.AsArray();
        tag.Select(t => t!.GetValue<string>()).Should().Equal("alpha", "beta");
    }
}
