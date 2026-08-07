using System.Text;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Reader cutover: BuildSummary/BuildFullList/AppendResultBlock source review status, tags,
/// description/preview, and anchors from the attributes carriers (Attributes/ContentPreview).
/// These tests build entries whose display data lives in the new carriers and assert the rendered
/// output honours them.
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
        ContentPreview: "the preview",
        Attributes: new Dictionary<string, string[]>
        {
            [KnownAttributes.ReviewKey] = [reviewDate.ToString("o")],
            [KnownAttributes.TagKey] = ["alpha"],
        });

    // ── New-carrier source (Attributes/ContentPreview) ─────────────────────────────────────

    [Fact]
    public void BuildFullList_FieldsOnlyEntry_ShowsStaleAndPreview()
    {
        var entry = FieldsOnlyEntry("fields-only", DateTimeOffset.UtcNow.AddDays(-1));
        var entries = new List<ScopedArtifact> { new("local", entry) };

        string output = ScriniaMcpTools.BuildFullList(entries, _store, 0, 50);

        output.Should().Contain("[stale]", because: "Fields[\"review\"] holds a past date");
        output.Should().Contain("the preview", because: "ContentPreview is the surviving display carrier");
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
            ContentPreview: "search preview text",
            Attributes: new Dictionary<string, string[]>
            {
                [KnownAttributes.AnchorKey] = ["anchor-one", "anchor-two"],
            });
        var scoped = new ScopedArtifact("local", entry);
        var result = new EntryResult(scoped, 42.0);
        var sb = new StringBuilder();

        SearchResultRenderer.AppendResultBlock(sb, _store, result, 1);
        string output = sb.ToString();

        output.Should().Contain("search preview text", because: "ContentPreview is the surviving display carrier");
        output.Should().Contain("anchor-one");
        output.Should().Contain("anchor-two");
    }
}
