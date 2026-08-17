using FluentAssertions;
using FluentAssertions.Execution;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests.Search;

/// <summary>
/// Finding M6 is a readability fix, not a behaviour change: the rubric is stated four times,
/// three of which the compiler sees. These tests capture exact scores and ordering from the
/// pre-refactor scorer so that "identical" is a checked claim rather than an intention.
///
/// <para>Each case asserts an ABSOLUTE score, not just a difference between two results —
/// a difference-only assertion passes under a uniform shift of both constants (e.g. NameExact
/// 100/NameStartsWith 30 shifted to 110/40 still yields a diff of 70), so it does not actually
/// pin either constant on its own.</para>
///
/// <para>Coverage: 14 of the 26 per-match-type rubric constants (<see cref="ScoringWeights"/>,
/// excluding the <c>Bm25Weight</c> multiplier) are pinned here by an absolute-value assertion —
/// NameExact, NameStartsWith, NameContains, KeywordExact, KeywordContains, AttributePairExact,
/// AttributeValueExact, AttributeValueContains, ContentPreviewContains, TopicTagExact,
/// TopicTagContains, ChunkKeywordExact, ChunkContentPreviewContains, IntersectionBonus. The
/// remaining 12 (TopicNameExact/StartsWith/Contains, TopicDescriptionContains,
/// TopicEntryNameAggregate, ChunkKeywordContains, and all six ChunkParent* rows) plus
/// Bm25Weight are unpinned by this file — Task 8's static literal audit is the proof for those,
/// not this suite. See task-8-report.md for the full mapping.</para>
/// </summary>
public sealed class ScoringWeightsPinningTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────
    // AlphaRecency: 0 isolates relevance from the recency bump (RankerCompositionTests pattern).

    private static ScopedArtifact Entry(
        string name,
        string[]? keywords = null,
        ChunkEntry[]? chunks = null,
        string? contentPreview = null,
        Dictionary<string, string[]>? attributes = null)
    {
        return new ScopedArtifact("local", new ArtifactEntry(
            Name: name,
            Uri: "",
            OriginalBytes: 100,
            ChunkCount: chunks?.Length ?? 1,
            CreatedAt: DateTimeOffset.UtcNow,
            ContentPreview: contentPreview,
            Keywords: keywords,
            ChunkEntries: chunks,
            Attributes: attributes));
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

    private static IReadOnlyList<(string ParentName, double Score)> SearchChunkScores(
        string query, ScopedArtifact[] entries)
    {
        var scorer = new WeightedAttributeScorer(new RankerOptions(AlphaRecency: 0));
        return scorer.SearchAll(query, entries, [], 10)
            .OfType<ChunkEntryResult>()
            .Select(r => (r.ParentItem.Entry.Name, r.Score))
            .ToList();
    }

    // ── Entry ladder ─────────────────────────────────────────────────────────

    [Fact]
    public void ExactNameMatch_OutranksNameStartsWith_ByTheDocumentedMargin()
    {
        var results = Search("alpha", [Entry("alpha"), Entry("alphabet-soup")]);

        using var scope = new AssertionScope();
        results[0].Entry.Name.Should().Be("alpha");
        // "alphabet-soup" starts with "alpha" (does not merely contain it), so this pins
        // NameExact (100) against NameStartsWith (30) — the header table's first and fifth rows.
        results[0].Score.Should().Be(100.0);
        results[1].Score.Should().Be(30.0);
        (results[0].Score - results[1].Score).Should().BeApproximately(70.0, 1e-9);
    }

    [Fact]
    public void NameContainsMatch_ScoresTheDocumentedMagnitude()
    {
        // "pre-alpha-post" contains "alpha" but does not start with it, isolating NameContains
        // from NameStartsWith.
        var score = Search("alpha", [Entry("pre-alpha-post")])[0].Score;
        score.Should().Be(20.0);
    }

    [Fact]
    public void KeywordExactMatch_ScoresTheDocumentedMagnitude()
    {
        var score = Search("beta", [Entry("unrelated-name", keywords: ["beta"])])[0].Score;
        score.Should().Be(40.0);
    }

    [Fact]
    public void KeywordContainsMatch_ScoresTheDocumentedMagnitude()
    {
        var score = Search("delta", [Entry("unrelated-name", keywords: ["deltaroo"])])[0].Score;
        score.Should().Be(12.0);
    }

    [Fact]
    public void AttributePairExactMatch_ScoresTheDocumentedMagnitude()
    {
        // Compound "key:value" query term hitting a (key, value) pair.
        var score = Search("topic:security",
            [Entry("unrelated-name", attributes: new Dictionary<string, string[]> { ["topic"] = ["security"] })])[0].Score;
        score.Should().Be(50.0);
    }

    [Fact]
    public void AttributeValueExactMatch_ScoresTheDocumentedMagnitude()
    {
        var score = Search("security",
            [Entry("unrelated-name", attributes: new Dictionary<string, string[]> { ["topic"] = ["security"] })])[0].Score;
        score.Should().Be(40.0);
    }

    [Fact]
    public void AttributeValueContainsMatch_ScoresTheDocumentedMagnitude()
    {
        var score = Search("security",
            [Entry("unrelated-name", attributes: new Dictionary<string, string[]> { ["topic"] = ["security-audit"] })])[0].Score;
        score.Should().Be(12.0);
    }

    [Fact]
    public void ContentPreviewContainsMatch_ScoresTheDocumentedMagnitude()
    {
        var score = Search("lorem", [Entry("unrelated-name", contentPreview: "lorem ipsum dolor")])[0].Score;
        score.Should().Be(5.0);
    }

    [Fact]
    public void TwoTermMatch_AddsTheIntersectionBonusExactlyOnce()
    {
        var single = Search("alpha", [Entry("alpha", keywords: ["beta"])])[0].Score;
        var both = Search("alpha beta", [Entry("alpha", keywords: ["beta"])])[0].Score;

        // single: 100 (exact name only; "beta" keyword doesn't match the lone term "alpha").
        // both: 100 (exact name) + 40 (keyword exact) + 15 (one bonus for the second matched term).
        single.Should().Be(100.0);
        both.Should().Be(155.0);
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
        results[0].Score.Should().Be(50.0); // TopicTagExact
        results[1].Score.Should().Be(15.0); // TopicTagContains
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

        using var scope = new AssertionScope();
        scores.Should().HaveCount(2);
        scores.Single(s => s.ParentName == "doc-a").Score.Should().Be(40.0); // ChunkKeywordExact
        scores.Single(s => s.ParentName == "doc-b").Score.Should().Be(5.0);  // ChunkContentPreviewContains
    }
}
