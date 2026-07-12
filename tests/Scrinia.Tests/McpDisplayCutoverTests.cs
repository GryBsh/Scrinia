using System.Text;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// P6.2 reader cutover: BuildSummary/BuildFullList/AppendResultBlock must source review status,
/// tags, description/preview, and anchors from the attributes carriers (Fields/ContentPreview)
/// when present, falling back to the legacy per-memory fields for entries not yet forward-filled.
/// These tests build entries whose display data lives ONLY in the new carriers (legacy fields
/// null/empty) and assert the rendered output honours them, plus a companion test proving the
/// legacy-only fallback path still works unchanged.
/// </summary>
public sealed class McpDisplayCutoverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public McpDisplayCutoverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-mcpdisplay-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new FileMemoryStore(_tempDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static ArtifactEntry FieldsOnlyEntry(string name, DateTimeOffset reviewDate) => new(
        Name: name,
        Uri: $"file://{name}",
        OriginalBytes: 100,
        ChunkCount: 1,
        CreatedAt: DateTimeOffset.UtcNow,
        Description: "",
        Tags: null,
        ContentPreview: "the preview",
        ReviewAfter: null,
        ReviewWhen: null,
        Fields: new Dictionary<string, string[]>
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
        Fields: null);

    // ── New-carrier source (Fields/ContentPreview only; legacy fields null) ─────────────────

    [Fact]
    public void BuildFullList_FieldsOnlyEntry_ShowsStaleAndPreview()
    {
        var entry = FieldsOnlyEntry("fields-only", DateTimeOffset.UtcNow.AddDays(-1));
        var entries = new List<ScopedArtifact> { new("local", entry) };

        string output = ScriniaMcpTools.BuildFullList(entries, _store, 0, 50);

        output.Should().Contain("[stale]", because: "Fields[\"review\"] holds a past date");
        output.Should().Contain("the preview", because: "ContentPreview should be used when Description is empty");
    }

    [Fact]
    public void BuildSummary_FieldsOnlyEntry_ShowsStaleCountAndTag()
    {
        var entry = FieldsOnlyEntry("fields-only", DateTimeOffset.UtcNow.AddDays(-1));
        var entries = new List<ScopedArtifact> { new("local", entry) };

        string output = ScriniaMcpTools.BuildSummary(entries, _store);

        output.Should().Contain("1 stale", because: "the review status must come from Fields[\"review\"]");
        output.Should().Contain("alpha", because: "Top keywords must fold in Fields[\"tag\"] when Tags is null");
    }

    // ── Legacy-only fallback (ephemeral entries not yet forward-filled) ─────────────────────

    [Fact]
    public void BuildFullList_LegacyOnlyEntry_StillShowsStaleAndDescription()
    {
        var entry = LegacyOnlyEntry("legacy-only", DateTimeOffset.UtcNow.AddDays(-1));
        var entries = new List<ScopedArtifact> { new("local", entry) };

        string output = ScriniaMcpTools.BuildFullList(entries, _store, 0, 50);

        output.Should().Contain("[stale]", because: "legacy ReviewAfter in the past must still mark the entry stale");
        output.Should().Contain("legacy description");
    }

    [Fact]
    public void BuildSummary_LegacyOnlyEntry_StillShowsStaleCountAndTag()
    {
        var entry = LegacyOnlyEntry("legacy-only", DateTimeOffset.UtcNow.AddDays(-1));
        var entries = new List<ScopedArtifact> { new("local", entry) };

        string output = ScriniaMcpTools.BuildSummary(entries, _store);

        output.Should().Contain("1 stale");
        output.Should().Contain("beta", because: "legacy Tags must still fold into Top keywords when Fields is null");
    }

    // ── Search-path (AppendResultBlock/AppendAnchors) ───────────────────────────────────────

    [Fact]
    public void AppendResultBlock_EntryResult_FieldsOnlyAnchorsAndPreview_AreRendered()
    {
        var entry = new ArtifactEntry(
            Name: "search-entry",
            Uri: "file://search-entry",
            OriginalBytes: 100,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Description: "",
            Tags: null,
            ContentPreview: "search preview text",
            RetrievalAnchors: null,
            Fields: new Dictionary<string, string[]>
            {
                [KnownFields.AnchorKey] = ["anchor-one", "anchor-two"],
            });
        var scoped = new ScopedArtifact("local", entry);
        var result = new EntryResult(scoped, 42.0);
        var sb = new StringBuilder();

        ScriniaMcpTools.AppendResultBlock(sb, _store, result, 1);
        string output = sb.ToString();

        output.Should().Contain("search preview text", because: "ContentPreview should be used when Description is empty");
        output.Should().Contain("anchor-one");
        output.Should().Contain("anchor-two");
    }
}
