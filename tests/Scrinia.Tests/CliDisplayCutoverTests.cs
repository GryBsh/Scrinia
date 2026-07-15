using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Reader cutover: the CLI's <c>list</c>/<c>search</c> display and <c>BuildCliSummary</c>
/// aggregation source review status, tags, and description/preview from the attributes carriers
/// (<c>Attributes</c>/<c>ContentPreview</c>). These tests build entries whose display data lives
/// in the new carriers and assert the builder methods honour them.
/// </summary>
public sealed class CliDisplayCutoverTests
{
    private static ArtifactEntry FieldsOnlyEntry(string name, DateTimeOffset reviewDate) => new(
        Name: name,
        Uri: $"file://{name}",
        OriginalBytes: 100,
        ChunkCount: 1,
        CreatedAt: DateTimeOffset.UtcNow,
        Description: "",
        Tags: null,
        ContentPreview: "cli preview",
        ReviewAfter: null,
        ReviewWhen: null,
        Attributes: new Dictionary<string, string[]>
        {
            [KnownFields.ReviewKey] = [reviewDate.ToString("o")],
            [KnownFields.TagKey] = ["alpha"],
        });

    // ── BuildCliSummary: new-carrier source (Attributes/ContentPreview) ─────────────────────

    [Fact]
    public void BuildCliSummary_FieldsOnlyEntry_ReportsStaleCountAndTag()
    {
        var entry = FieldsOnlyEntry("fields-only", DateTimeOffset.UtcNow.AddDays(-1));
        var entries = new List<ScopedArtifact> { new("local", entry) };

        var summary = ScriniaCommands.BuildCliSummary(entries, forJson: false);

        summary.StaleCount.Should().Be(1, because: "the review status must come from Fields[\"review\"]");
        summary.TopKeywords.Should().Contain("alpha", because: "Top keywords must fold in Fields[\"tag\"] when Tags is null");
    }

    // ── BuildCliMemoryEntry (list --json): new-carrier source ───────────────────────────────

    [Fact]
    public void BuildCliMemoryEntry_FieldsOnlyEntry_IsStaleTagsFromFields_DescriptionFromContentPreview()
    {
        var entry = FieldsOnlyEntry("fields-only", DateTimeOffset.UtcNow.AddDays(-1));
        var scoped = new ScopedArtifact("local", entry);

        var cli = ScriniaCommands.BuildCliMemoryEntry(scoped, DateTimeOffset.UtcNow);

        cli.IsStale.Should().BeTrue();
        cli.Attributes.Should().ContainKey(KnownFields.TagKey)
            .WhoseValue.Should().BeEquivalentTo(["alpha"]);
        cli.Description.Should().Be("cli preview");
    }

    // ── list --json wire shape: attributes surfaced, tags/reviewAfter/reviewWhen dropped ────

    [Fact]
    public void BuildCliMemoryEntry_Json_SurfacesAttributes_DropsTagsAndReviewFields()
    {
        var entry = FieldsOnlyEntry("fields-only", DateTimeOffset.UtcNow.AddDays(-1));
        var scoped = new ScopedArtifact("local", entry);

        var cli = ScriniaCommands.BuildCliMemoryEntry(scoped, DateTimeOffset.UtcNow);
        var output = new CliListOutput([cli], 1, null);
        string json = System.Text.Json.JsonSerializer.Serialize(output, CliJsonContext.Default.CliListOutput);

        json.Should().Contain("\"attributes\"");
        json.Should().NotContain("\"tags\"");
        json.Should().NotContain("\"reviewAfter\"");
        json.Should().NotContain("\"reviewWhen\"");
    }

}
