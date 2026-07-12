using System.Globalization;
using FluentAssertions;
using Scrinia.Core.Migration;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed class AttributeMigrationTests
{
    private static ArtifactEntry MakeEntry(
        string[]? tags = null, string[]? anchors = null,
        Dictionary<string, string>? codeRefs = null,
        DateTimeOffset? reviewAfter = null, string? reviewWhen = null,
        string[]? keywords = null, Dictionary<string, string[]>? fields = null) =>
        new(Name: "n", Uri: "u", OriginalBytes: 1, ChunkCount: 1,
            CreatedAt: DateTimeOffset.UnixEpoch, Description: "desc",
            Tags: tags, Keywords: keywords, RetrievalAnchors: anchors,
            ReviewAfter: reviewAfter, ReviewWhen: reviewWhen, CodeRefs: codeRefs, Fields: fields);

    [Fact]
    public void Forward_MapsTagsToTagAttribute_AnchorsToKeywords()
    {
        var e = MakeEntry(tags: ["security", "auth"], anchors: ["how do I log in"]);
        var m = AttributeMigration.Forward(e);
        m.Fields!["tag"].Should().BeEquivalentTo("security", "auth");
        m.Keywords.Should().Contain("how do I log in");
        // additive: legacy fields preserved
        m.Tags.Should().BeEquivalentTo("security", "auth");
        m.RetrievalAnchors.Should().BeEquivalentTo("how do I log in");
        m.Description.Should().Be("desc");
    }

    [Fact]
    public void Forward_MapsCodeRefPathsToFile_AndReviewToReview()
    {
        var e = MakeEntry(
            codeRefs: new() { ["src/A.cs"] = "hashA", ["src/B.cs"] = "hashB" },
            reviewAfter: DateTimeOffset.Parse("2026-08-01T00:00:00Z", CultureInfo.InvariantCulture),
            reviewWhen: "when auth changes");
        var m = AttributeMigration.Forward(e);
        m.Fields!["file"].Should().BeEquivalentTo("src/A.cs", "src/B.cs");   // paths, not hashes
        m.Fields!["review"].Should().Contain(v => v.StartsWith("2026-08-01"))
            .And.Contain("when auth changes");
    }

    [Fact]
    public void Forward_UnionsIntoExistingFields_WithoutDuplicating()
    {
        var e = MakeEntry(codeRefs: new() { ["src/A.cs"] = "h" },
            fields: new() { ["file"] = ["src/A.cs"], ["person"] = ["maria"] });
        var m = AttributeMigration.Forward(e);
        m.Fields!["file"].Should().BeEquivalentTo("src/A.cs");   // union dedupes
        m.Fields!["person"].Should().BeEquivalentTo("maria");    // untouched
    }

    [Fact]
    public void MigratesTo_IsIdempotent_SecondPassReportsNoChange()
    {
        var e = MakeEntry(tags: ["x"], anchors: ["y"]);
        AttributeMigration.MigratesTo(e, out var once).Should().BeTrue();
        AttributeMigration.MigratesTo(once, out _).Should().BeFalse();   // already forward-filled
    }

    [Fact]
    public void MigratesTo_NoLegacyData_ReportsNoChange()
    {
        var e = MakeEntry(fields: new() { ["topic"] = ["auth"] });
        AttributeMigration.MigratesTo(e, out _).Should().BeFalse();
    }
}
