using FluentAssertions;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Regression coverage for Task 16: <see cref="WeightedFieldScorer"/> used to min-max-normalize
/// BM25 (<c>(raw - min) / range * 100</c>). When exactly one candidate carries the only positive
/// BM25 signal in the corpus, that candidate is simultaneously the min AND the max, so it
/// normalizes to 0. If BM25 is its only signal (no name/keyword/field/preview match), the
/// <c>relevance &lt;= 0</c> gate in <c>TryInsert</c> drops it from results entirely — a unique,
/// keyword-only match vanishes. Max-normalization (matching <c>EntityGraphScoreContributor</c>
/// and <c>MmrReranker</c>) fixes this: the sole positive scorer normalizes to 100, not 0.
/// </summary>
public sealed class WeightedFieldScorerBm25NormTests
{
    private static ScopedArtifact MakeEntry(
        string name,
        string[]? keywords = null,
        Dictionary<string, int>? tf = null)
    {
        return new ScopedArtifact("local", new ArtifactEntry(
            Name: name,
            Uri: "",
            OriginalBytes: 100,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            ContentPreview: null,
            Keywords: keywords ?? [],
            TermFrequencies: tf));
    }

    [Fact]
    public void UniqueBodyOnlyMatch_IsNotDroppedByBm25Normalization()
    {
        // "lighthouse" appears ONLY in doc-one's indexed body (TermFrequencies) — not in its
        // Name, Keywords, Attributes, or ContentPreview — so ScoreEntry's field score is 0 for
        // this term and BM25 is doc-one's only signal. doc-two shares no terms with the query at
        // all, so it contributes zero to the min/max BM25 window. That leaves doc-one as the sole
        // positive-BM25 candidate: under the old min-max scheme it IS the min and the max, so it
        // normalizes to 0 and gets dropped by the relevance>0 gate.
        var docOne = MakeEntry("doc-one",
            keywords: ["other-topic"],
            tf: new Dictionary<string, int> { ["lighthouse"] = 5, ["other"] = 2 });
        var docTwo = MakeEntry("doc-two",
            keywords: ["unrelated"],
            tf: new Dictionary<string, int> { ["unrelated"] = 3 });

        var scorer = new WeightedFieldScorer(new RankerOptions(AlphaRecency: 0));

        var results = scorer.SearchAll("lighthouse", [docOne, docTwo], [], 10);

        results.Should().HaveCount(1,
            "doc-one's unique body-only BM25 match must surface; doc-two shares no terms with the query");
        (results[0] as EntryResult)!.Item.Entry.Name.Should().Be("doc-one");
    }
}
