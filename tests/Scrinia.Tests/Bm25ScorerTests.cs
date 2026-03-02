using FluentAssertions;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class Bm25ScorerTests
{
    [Fact]
    public void Score_MatchingTerm_PositiveScore()
    {
        var queryTerms = new[] { "auth" };
        var entryTf = new Dictionary<string, int> { ["auth"] = 5, ["token"] = 3 };
        var docFreqs = new Dictionary<string, int> { ["auth"] = 2, ["token"] = 3 };

        double score = Bm25Scorer.Score(queryTerms, entryTf, 8, 10.0, 5, docFreqs);

        score.Should().BeGreaterThan(0, because: "matching term should produce positive BM25 score");
    }

    [Fact]
    public void Score_NoMatchingTerm_ZeroScore()
    {
        var queryTerms = new[] { "missing" };
        var entryTf = new Dictionary<string, int> { ["auth"] = 5 };
        var docFreqs = new Dictionary<string, int> { ["auth"] = 2 };

        double score = Bm25Scorer.Score(queryTerms, entryTf, 5, 10.0, 5, docFreqs);

        score.Should().Be(0, because: "non-matching term should produce zero score");
    }

    [Fact]
    public void Score_EmptyQueryTerms_ZeroScore()
    {
        var entryTf = new Dictionary<string, int> { ["auth"] = 5 };
        var docFreqs = new Dictionary<string, int> { ["auth"] = 2 };

        double score = Bm25Scorer.Score([], entryTf, 5, 10.0, 5, docFreqs);

        score.Should().Be(0);
    }

    [Fact]
    public void Score_EmptyTf_ZeroScore()
    {
        var queryTerms = new[] { "auth" };
        var docFreqs = new Dictionary<string, int> { ["auth"] = 2 };

        double score = Bm25Scorer.Score(queryTerms, new Dictionary<string, int>(), 0, 10.0, 5, docFreqs);

        score.Should().Be(0);
    }

    [Fact]
    public void Score_RareTermScoresHigherThanCommonTerm()
    {
        // "rare" appears in 1 of 10 docs, "common" appears in 9 of 10
        var entryTf = new Dictionary<string, int> { ["rare"] = 3, ["common"] = 3 };
        var docFreqs = new Dictionary<string, int> { ["rare"] = 1, ["common"] = 9 };

        double rareScore = Bm25Scorer.Score(["rare"], entryTf, 6, 10.0, 10, docFreqs);
        double commonScore = Bm25Scorer.Score(["common"], entryTf, 6, 10.0, 10, docFreqs);

        rareScore.Should().BeGreaterThan(commonScore,
            because: "rare terms should have higher IDF and thus higher BM25 score");
    }

    [Fact]
    public void Score_HigherTfScoresHigher()
    {
        var highTf = new Dictionary<string, int> { ["auth"] = 10 };
        var lowTf = new Dictionary<string, int> { ["auth"] = 1 };
        var docFreqs = new Dictionary<string, int> { ["auth"] = 3 };

        double highScore = Bm25Scorer.Score(["auth"], highTf, 10, 10.0, 10, docFreqs);
        double lowScore = Bm25Scorer.Score(["auth"], lowTf, 10, 10.0, 10, docFreqs);

        highScore.Should().BeGreaterThan(lowScore,
            because: "higher term frequency should produce higher score");
    }

    [Fact]
    public void Score_MultipleQueryTerms_Additive()
    {
        var entryTf = new Dictionary<string, int> { ["auth"] = 3, ["token"] = 2 };
        var docFreqs = new Dictionary<string, int> { ["auth"] = 2, ["token"] = 2 };

        double singleScore = Bm25Scorer.Score(["auth"], entryTf, 5, 10.0, 5, docFreqs);
        double multiScore = Bm25Scorer.Score(["auth", "token"], entryTf, 5, 10.0, 5, docFreqs);

        multiScore.Should().BeGreaterThan(singleScore,
            because: "matching more query terms should increase score");
    }

    // ── ComputeCorpusStats ───────────────────────────────────────────────────

    [Fact]
    public void ComputeCorpusStats_CalculatesAvgDocLengthAndDocFreqs()
    {
        var tfs = new[]
        {
            new Dictionary<string, int> { ["auth"] = 3, ["token"] = 2 },   // docLen=5
            new Dictionary<string, int> { ["auth"] = 1, ["refresh"] = 4 }, // docLen=5
            new Dictionary<string, int> { ["token"] = 6 },                  // docLen=6
        };

        var (avgDocLen, docFreqs) = Bm25Scorer.ComputeCorpusStats(
            tfs.Select(t => (IReadOnlyDictionary<string, int>?)t));

        avgDocLen.Should().BeApproximately(16.0 / 3.0, 0.01);
        docFreqs["auth"].Should().Be(2);
        docFreqs["token"].Should().Be(2);
        docFreqs["refresh"].Should().Be(1);
    }

    [Fact]
    public void ComputeCorpusStats_SkipsNullEntries()
    {
        var tfs = new IReadOnlyDictionary<string, int>?[]
        {
            new Dictionary<string, int> { ["auth"] = 3 },
            null,
            new Dictionary<string, int> { ["token"] = 2 },
        };

        var (avgDocLen, docFreqs) = Bm25Scorer.ComputeCorpusStats(tfs);

        // 2 docs with total length 5
        avgDocLen.Should().BeApproximately(2.5, 0.01);
        docFreqs.Should().HaveCount(2);
    }

    [Fact]
    public void ComputeCorpusStats_EmptyCollection_ReturnsZero()
    {
        var (avgDocLen, docFreqs) = Bm25Scorer.ComputeCorpusStats(
            Array.Empty<IReadOnlyDictionary<string, int>?>());

        avgDocLen.Should().Be(0);
        docFreqs.Should().BeEmpty();
    }

    // ── BM25 normalization tests ─────────────────────────────────────────────

    [Fact]
    public void NormalizedBm25_InSearchAll_ProducesNonNegativeScores()
    {
        // Verify through WeightedFieldScorer.SearchAll that normalized scores work correctly
        var candidates = new[]
        {
            new ScopedArtifact("local", new ArtifactEntry("auth-doc", "", 100, 1, DateTimeOffset.UtcNow, "auth system",
                Keywords: ["auth", "token"],
                TermFrequencies: new Dictionary<string, int> { ["auth"] = 10, ["token"] = 5 })),
            new ScopedArtifact("local", new ArtifactEntry("other-doc", "", 100, 1, DateTimeOffset.UtcNow, "other stuff",
                Keywords: ["other"],
                TermFrequencies: new Dictionary<string, int> { ["other"] = 8 })),
        };

        var scorer = new WeightedFieldScorer();
        var results = scorer.SearchAll("auth", candidates, [], 10);

        results.Should().NotBeEmpty();
        results.Should().AllSatisfy(r => r.Score.Should().BeGreaterThan(0));
    }

    // ── Min-heap top-K tests ──────────────────────────────────────────────────

    [Fact]
    public void MinHeap_SearchAll_ReturnsCorrectTopK()
    {
        // Create more candidates than the limit to verify heap selects top-K
        var now = DateTimeOffset.UtcNow;
        var candidates = new List<ScopedArtifact>();
        for (int i = 0; i < 20; i++)
        {
            candidates.Add(new ScopedArtifact("local", new ArtifactEntry(
                $"entry-{i}", "", 100, 1, now.AddMinutes(-i), $"doc about entry {i}",
                Keywords: [$"entry{i}"],
                TermFrequencies: new Dictionary<string, int> { [$"entry{i}"] = i + 1 })));
        }

        // Add one entry that matches the query strongly
        candidates.Add(new ScopedArtifact("local", new ArtifactEntry(
            "target", "", 100, 1, now, "the target entry",
            Keywords: ["target"],
            TermFrequencies: new Dictionary<string, int> { ["target"] = 20 })));

        var scorer = new WeightedFieldScorer();
        var results = scorer.SearchAll("target", candidates, [], 5);

        results.Should().HaveCountLessOrEqualTo(5);
        (results[0] as EntryResult)!.Item.Entry.Name.Should().Be("target");
    }

    [Fact]
    public void MinHeap_SearchAll_OrderingMatchesFullSort()
    {
        // Verify min-heap produces same ordering as a full sort would
        var now = DateTimeOffset.UtcNow;
        var candidates = new[]
        {
            new ScopedArtifact("local", new ArtifactEntry("best-match", "", 100, 1, now, "best match test query",
                Keywords: ["best", "match", "test", "query"],
                TermFrequencies: new Dictionary<string, int> { ["best"] = 10, ["match"] = 8, ["test"] = 5, ["query"] = 5 })),
            new ScopedArtifact("local", new ArtifactEntry("partial-match", "", 100, 1, now, "partial match only",
                Keywords: ["partial", "match"],
                TermFrequencies: new Dictionary<string, int> { ["partial"] = 3, ["match"] = 2 })),
            new ScopedArtifact("local", new ArtifactEntry("no-match", "", 100, 1, now, "irrelevant content xyz",
                Keywords: ["irrelevant"],
                TermFrequencies: new Dictionary<string, int> { ["irrelevant"] = 5 })),
        };

        var scorer = new WeightedFieldScorer();
        var results = scorer.SearchAll("best match", candidates, [], 10);

        // "best-match" should score highest (matches both terms + intersection bonus)
        results.Should().HaveCountGreaterOrEqualTo(2);
        (results[0] as EntryResult)!.Item.Entry.Name.Should().Be("best-match");

        // Scores should be descending
        for (int i = 1; i < results.Count; i++)
            results[i].Score.Should().BeLessOrEqualTo(results[i - 1].Score);
    }
}
