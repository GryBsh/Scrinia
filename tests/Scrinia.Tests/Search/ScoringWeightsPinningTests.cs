using FluentAssertions;
using FluentAssertions.Execution;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests.Search;

/// <summary>
/// Finding M6 is a readability fix, not a behaviour change: the rubric is stated four times,
/// three of which the compiler sees. These tests capture exact scores and ordering from the
/// pre-refactor scorer so that "identical" is a checked claim rather than an intention.
/// </summary>
public sealed class ScoringWeightsPinningTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────
    // AlphaRecency: 0 isolates relevance from the recency bump (RankerCompositionTests pattern).

    private static ScopedArtifact Entry(
        string name,
        string[]? keywords = null,
        ChunkEntry[]? chunks = null,
        string? contentPreview = null)
    {
        return new ScopedArtifact("local", new ArtifactEntry(
            Name: name,
            Uri: "",
            OriginalBytes: 100,
            ChunkCount: chunks?.Length ?? 1,
            CreatedAt: DateTimeOffset.UtcNow,
            ContentPreview: contentPreview,
            Keywords: keywords,
            ChunkEntries: chunks));
    }

    private static TopicInfo Topic(string name, string[]? tags = null)
        => new("local", name, EntryCount: 0, Description: "", Tags: tags, EntryNames: []);

    private static IReadOnlyList<(ArtifactEntry Entry, double Score)> Search(
        string query, ScopedArtifact[] entries)
    {
        var scorer = new WeightedAttributeScorer(new RankerOptions(AlphaRecency: 0));
        return scorer.SearchAll(query, entries, [], 10)
            .OfType<EntryResult>()
            .Select(r => (r.Item.Entry, r.Score))
            .ToList();
    }

    private static IReadOnlyList<(string TopicName, double Score)> SearchTopics(
        string query, TopicInfo[] topics)
    {
        var scorer = new WeightedAttributeScorer(new RankerOptions(AlphaRecency: 0));
        return scorer.SearchAll(query, [], topics, 10)
            .OfType<TopicResult>()
            .Select(r => (r.TopicName, r.Score))
            .ToList();
    }

    private static IReadOnlyList<double> SearchChunkScores(
        string query, ScopedArtifact[] entries)
    {
        var scorer = new WeightedAttributeScorer(new RankerOptions(AlphaRecency: 0));
        return scorer.SearchAll(query, entries, [], 10)
            .OfType<ChunkEntryResult>()
            .Select(r => r.Score)
            .ToList();
    }

    // ── Entry ladder ─────────────────────────────────────────────────────────

    [Fact]
    public void ExactNameMatch_OutranksNameContains_ByTheDocumentedMargin()
    {
        var results = Search("alpha", [Entry("alpha"), Entry("alphabet-soup")]);

        using var scope = new AssertionScope();
        results[0].Entry.Name.Should().Be("alpha");
        // 100 (exact) vs 30 (starts-with) — the header table's first and fifth rows.
        (results[0].Score - results[1].Score).Should().BeApproximately(70.0, 1e-9);
    }

    [Fact]
    public void TwoTermMatch_AddsTheIntersectionBonusExactlyOnce()
    {
        var single = Search("alpha", [Entry("alpha", keywords: ["beta"])])[0].Score;
        var both = Search("alpha beta", [Entry("alpha", keywords: ["beta"])])[0].Score;

        // 100 (exact name) + 40 (keyword exact) + 15 (one bonus for the second matched term).
        (both - single).Should().BeApproximately(55.0, 1e-9);
    }

    // ── Topic ladder ─────────────────────────────────────────────────────────

    [Fact]
    public void TopicTagExactMatch_OutranksTopicTagContains_ByTheDocumentedMargin()
    {
        var results = SearchTopics("gizmo",
        [
            Topic("widget-a", tags: ["gizmo"]),
            Topic("widget-b", tags: ["gizmoid"]),
        ]);

        using var scope = new AssertionScope();
        results[0].TopicName.Should().Be("widget-a");
        // 50 (tag exact) vs 15 (tag contains).
        (results[0].Score - results[1].Score).Should().BeApproximately(35.0, 1e-9);
    }

    // ── Chunk-entry ladder ───────────────────────────────────────────────────

    [Fact]
    public void ChunkKeywordExactMatch_OutranksChunkContentPreviewContains()
    {
        // Parent Name/Keywords deliberately don't match "delta", so no EntryResult is produced
        // for either memory and the per-memory dedup in SearchAll can't hide the chunk result
        // behind a higher entry-level score.
        var entryA = Entry("doc-a", chunks: [new ChunkEntry(ChunkIndex: 0, Keywords: ["delta"])]);
        var entryB = Entry("doc-b", chunks: [new ChunkEntry(ChunkIndex: 0, ContentPreview: "pre-delta text")]);

        var scores = SearchChunkScores("delta", [entryA, entryB]);

        scores.Should().HaveCount(2);
        // 40 (chunk keyword exact) vs 5 (chunk content preview contains).
        (scores.Max() - scores.Min()).Should().BeApproximately(35.0, 1e-9);
    }
}
