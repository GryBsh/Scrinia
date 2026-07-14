using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// P6.2 reader cutover: the CLI's <c>list</c>/<c>search</c> display and <c>BuildCliSummary</c>
/// aggregation must source review status, tags, and description/preview from the attributes
/// carriers (<c>Fields</c>/<c>ContentPreview</c>) when present, falling back to the legacy
/// per-memory fields for entries not yet forward-filled. These tests build entries whose
/// display data lives ONLY in the new carriers (legacy fields null/empty) and assert the
/// builder methods honour them, plus a companion test proving the legacy-only fallback path
/// still works unchanged.
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

    private static ArtifactEntry LegacyOnlyEntry(string name, DateTimeOffset reviewAfter) => new(
        Name: name,
        Uri: $"file://{name}",
        OriginalBytes: 100,
        ChunkCount: 1,
        CreatedAt: DateTimeOffset.UtcNow,
        Description: "legacy description",
        Tags: ["beta"],
        ContentPreview: null,
        ReviewAfter: reviewAfter,
        ReviewWhen: null,
        Attributes: null);

    // ── BuildCliSummary: new-carrier source (Fields/ContentPreview only) ────────────────────

    [Fact]
    public void BuildCliSummary_FieldsOnlyEntry_ReportsStaleCountAndTag()
    {
        var entry = FieldsOnlyEntry("fields-only", DateTimeOffset.UtcNow.AddDays(-1));
        var entries = new List<ScopedArtifact> { new("local", entry) };

        var summary = ScriniaCommands.BuildCliSummary(entries, forJson: false);

        summary.StaleCount.Should().Be(1, because: "the review status must come from Fields[\"review\"]");
        summary.TopKeywords.Should().Contain("alpha", because: "Top keywords must fold in Fields[\"tag\"] when Tags is null");
    }

    // ── BuildCliSummary: legacy-only fallback ───────────────────────────────────────────────

    [Fact]
    public void BuildCliSummary_LegacyOnlyEntry_StillReportsStaleCountAndTag()
    {
        var entry = LegacyOnlyEntry("legacy-only", DateTimeOffset.UtcNow.AddDays(-1));
        var entries = new List<ScopedArtifact> { new("local", entry) };

        var summary = ScriniaCommands.BuildCliSummary(entries, forJson: false);

        summary.StaleCount.Should().Be(1);
        summary.TopKeywords.Should().Contain("beta", because: "legacy Tags must still fold into Top keywords when Fields is null");
    }

    // ── BuildCliMemoryEntry (list --json): new-carrier source ───────────────────────────────

    [Fact]
    public void BuildCliMemoryEntry_FieldsOnlyEntry_IsStaleTagsFromFields_DescriptionFromContentPreview()
    {
        var entry = FieldsOnlyEntry("fields-only", DateTimeOffset.UtcNow.AddDays(-1));
        var scoped = new ScopedArtifact("local", entry);

        var cli = ScriniaCommands.BuildCliMemoryEntry(scoped, DateTimeOffset.UtcNow);

        cli.IsStale.Should().BeTrue();
        cli.Tags.Should().BeEquivalentTo(["alpha"]);
        cli.Description.Should().Be("cli preview");
    }

    // ── BuildCliMemoryEntry (list --json): legacy-only fallback ─────────────────────────────

    [Fact]
    public void BuildCliMemoryEntry_LegacyOnlyEntry_StillWorks()
    {
        var entry = LegacyOnlyEntry("legacy-only", DateTimeOffset.UtcNow.AddDays(-1));
        var scoped = new ScopedArtifact("local", entry);

        var cli = ScriniaCommands.BuildCliMemoryEntry(scoped, DateTimeOffset.UtcNow);

        cli.IsStale.Should().BeTrue();
        cli.Tags.Should().BeEquivalentTo(["beta"]);
        cli.Description.Should().Be("legacy description");
    }
}
