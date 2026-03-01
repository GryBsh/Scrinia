using FluentAssertions;
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
}
