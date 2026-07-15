using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// P6.5: <see cref="WeightedFieldScorer"/> deleted its legacy direct-carrier scoring blocks
/// (Tags exact/contains, RetrievalAnchors contains, key:value-in-Keywords dual-read,
/// Description contains) now that Fields is populated everywhere via read-time forward-fill
/// (<see cref="Scrinia.Core.Migration.AttributeMigration"/>, P6.1/P6.3). These tests prove the
/// Fields block is the SOLE tag scorer post-cleanup.
/// </summary>
public sealed class ScorerCleanupTests
{
    [Fact]
    public void ForwardFilledTag_ScoresValueExact40_NotLegacyTagExact50()
    {
        // A real write populates Attributes["tag"] directly (Merge 2's attributes-native write
        // path). The legacy Tags member is gone entirely (Merge 3 Task 7), so Attributes is the
        // only way "widgets" can be found, at the Field-value-exact tier (40).
        using var scope = new TestHelpers.StoreScope();
        // Upsert's in-memory cache holds the RAW entry it just wrote, so reading it back through
        // the SAME store instance would short-circuit past the cold-load path entirely. A fresh
        // store instance over the same directory forces a cold LoadIndex.
        using (var writer = new FileMemoryStore(scope.WorkspaceDir))
        {
            writer.Upsert(new ArtifactEntry(
                Name: "tag-only", Uri: "u", OriginalBytes: 10, ChunkCount: 1,
                CreatedAt: DateTimeOffset.UtcNow,
                Attributes: new() { ["tag"] = ["widgets"] }),
                "local");
        }

        using var store = new FileMemoryStore(scope.WorkspaceDir);
        var entry = store.LoadIndex("local").Single(e => e.Name == "tag-only");
        entry.Attributes.Should().NotBeNull();
        entry.Attributes!["tag"].Should().Contain("widgets");

        var scored = new WeightedFieldScorer().Search("widgets", [new ScopedArtifact("local", entry)], 10);

        scored.Should().ContainSingle();
        scored[0].Score.Should().Be(40,
            because: "Fields[\"tag\"] value-exact match scores the Field-value tier (40) now that " +
                "the legacy tag-exact block (50) has been deleted — the Fields block is the sole " +
                "tag scorer post-P6.5");
    }

    [Fact]
    public void ForwardFilledTag_OutranksContentPreviewOnlyMatch()
    {
        using var scope = new TestHelpers.StoreScope();
        using (var writer = new FileMemoryStore(scope.WorkspaceDir))
        {
            writer.Upsert(new ArtifactEntry(
                Name: "tag-only", Uri: "u", OriginalBytes: 10, ChunkCount: 1,
                CreatedAt: DateTimeOffset.UtcNow,
                Attributes: new() { ["tag"] = ["widgets"] }),
                "local");
            writer.Upsert(new ArtifactEntry(
                Name: "preview-only", Uri: "u", OriginalBytes: 10, ChunkCount: 1,
                CreatedAt: DateTimeOffset.UtcNow,
                ContentPreview: "notes that mention widgets in passing"),
                "local");
        }

        using var store = new FileMemoryStore(scope.WorkspaceDir);
        var entries = store.LoadIndex("local");
        var scoped = entries.Select(e => new ScopedArtifact("local", e)).ToList();

        var scored = new WeightedFieldScorer().Search("widgets", scoped, 10);

        scored.Should().HaveCount(2);
        var tagResult = scored.Single(r => r.Item.Entry.Name == "tag-only");
        var previewResult = scored.Single(r => r.Item.Entry.Name == "preview-only");

        previewResult.Score.Should().Be(5, because: "content-preview-contains is the lowest tier");
        tagResult.Score.Should().BeGreaterThan(previewResult.Score,
            because: "the Fields-sourced tag match (40) must still comfortably outrank a bare " +
                "content-preview match (5)");
    }
}
