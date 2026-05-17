using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Core;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Locks down the R@K / NDCG@10 / per-category math. A single off-by-one in this
/// scorer would silently invalidate every benchmark result we publish.
/// </summary>
public class BenchmarkScorerTests
{
    [Fact]
    public void Id_match_hit_at_rank_1_scores_full_recall_and_ndcg_1()
    {
        var q = IdQuestion("q1", ["target"], "cat-a");
        var hits = new[]
        {
            new RetrievalHit("target", "", 100),
            new RetrievalHit("other-1", "", 50),
        };

        var r = BenchmarkScorer.ScoreQuestion(q, hits);

        r.HitAt5.Should().BeTrue();
        r.HitAt10.Should().BeTrue();
        r.HitAt50.Should().BeTrue();
        r.Ndcg10.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Id_match_hit_at_rank_3_scores_ndcg_correctly()
    {
        var q = IdQuestion("q2", ["target"]);
        var hits = new[]
        {
            new RetrievalHit("d1", "", 30),
            new RetrievalHit("d2", "", 20),
            new RetrievalHit("target", "", 15),
            new RetrievalHit("d4", "", 10),
        };

        var r = BenchmarkScorer.ScoreQuestion(q, hits);

        r.HitAt5.Should().BeTrue();
        r.HitAt10.Should().BeTrue();
        // NDCG@10 for binary relevance at rank 3 is 1 / log2(3+1) = 0.5
        r.Ndcg10.Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void Id_match_hit_outside_top_10_zero_recall_and_zero_ndcg()
    {
        var q = IdQuestion("q3", ["target"]);
        var hits = new List<RetrievalHit>();
        for (int i = 0; i < 10; i++) hits.Add(new RetrievalHit($"d{i}", "", 100 - i));
        hits.Add(new RetrievalHit("target", "", 0)); // rank 11

        var r = BenchmarkScorer.ScoreQuestion(q, hits);

        r.HitAt5.Should().BeFalse();
        r.HitAt10.Should().BeFalse();
        r.HitAt50.Should().BeTrue();
        r.Ndcg10.Should().Be(0);
    }

    [Fact]
    public void Id_match_multiple_ground_truth_any_match_counts()
    {
        var q = IdQuestion("q4", ["a", "b", "c"]);
        var hits = new[]
        {
            new RetrievalHit("z", "", 100),
            new RetrievalHit("b", "", 50),
        };

        var r = BenchmarkScorer.ScoreQuestion(q, hits);

        r.HitAt5.Should().BeTrue();
        r.Ndcg10.Should().BeApproximately(1.0 / Math.Log2(3), 1e-9);
    }

    [Fact]
    public void Text_match_substring_case_insensitive()
    {
        var q = TextQuestion("q5", ["i hate phones with bad battery"]);
        var hits = new[]
        {
            new RetrievalHit("d1", "Some unrelated content", 100),
            new RetrievalHit("d2", "User says: I HATE PHONES with bad battery life", 80),
        };

        var r = BenchmarkScorer.ScoreQuestion(q, hits);

        r.HitAt5.Should().BeTrue();
        r.Ndcg10.Should().BeApproximately(1.0 / Math.Log2(3), 1e-9); // rank 2
    }

    [Fact]
    public void Empty_hits_return_no_recall()
    {
        var q = IdQuestion("q6", ["target"]);
        var r = BenchmarkScorer.ScoreQuestion(q, []);
        r.HitAt5.Should().BeFalse();
        r.HitAt10.Should().BeFalse();
        r.HitAt50.Should().BeFalse();
        r.Ndcg10.Should().Be(0);
    }

    [Fact]
    public void Aggregate_means_across_questions()
    {
        var results = new[]
        {
            new QuestionResult("q1", "", "cat-a", ["t"], [], [], HitAt5: true,  HitAt10: true,  HitAt50: true,  Ndcg10: 1.0),
            new QuestionResult("q2", "", "cat-a", ["t"], [], [], HitAt5: false, HitAt10: true,  HitAt50: true,  Ndcg10: 0.5),
            new QuestionResult("q3", "", "cat-b", ["t"], [], [], HitAt5: false, HitAt10: false, HitAt50: true,  Ndcg10: 0.0),
            new QuestionResult("q4", "", "cat-b", ["t"], [], [], HitAt5: true,  HitAt10: true,  HitAt50: true,  Ndcg10: 1.0),
        };

        var agg = BenchmarkScorer.Aggregate(results);

        agg.QuestionCount.Should().Be(4);
        agg.RecallAt5.Should().BeApproximately(0.5, 1e-9);
        agg.RecallAt10.Should().BeApproximately(0.75, 1e-9);
        agg.RecallAt50.Should().BeApproximately(1.0, 1e-9);
        agg.Ndcg10.Should().BeApproximately(0.625, 1e-9);
    }

    [Fact]
    public void Per_category_groups_results_correctly()
    {
        var results = new[]
        {
            new QuestionResult("q1", "", "cat-a", ["t"], [], [], HitAt5: true,  HitAt10: true,  HitAt50: true,  Ndcg10: 1.0),
            new QuestionResult("q2", "", "cat-a", ["t"], [], [], HitAt5: false, HitAt10: true,  HitAt50: true,  Ndcg10: 0.5),
            new QuestionResult("q3", "", "cat-b", ["t"], [], [], HitAt5: true,  HitAt10: true,  HitAt50: true,  Ndcg10: 1.0),
        };

        var per = BenchmarkScorer.PerCategory(results);

        per.Should().ContainKey("cat-a");
        per["cat-a"].QuestionCount.Should().Be(2);
        per["cat-a"].RecallAt5.Should().BeApproximately(0.5, 1e-9);
        per["cat-b"].QuestionCount.Should().Be(1);
        per["cat-b"].RecallAt5.Should().Be(1.0);
    }

    [Fact]
    public void Aggregate_empty_results_returns_zeros()
    {
        var agg = BenchmarkScorer.Aggregate(Array.Empty<QuestionResult>());
        agg.QuestionCount.Should().Be(0);
        agg.RecallAt5.Should().Be(0);
        agg.RecallAt10.Should().Be(0);
        agg.RecallAt50.Should().Be(0);
        agg.Ndcg10.Should().Be(0);
    }

    private static BenchmarkQuestion IdQuestion(string id, string[] groundTruthIds, string category = "default") =>
        new(id, "query text", [], groundTruthIds, [], category);

    private static BenchmarkQuestion TextQuestion(string id, string[] groundTruthTexts, string category = "default") =>
        new(id, "query text", [], [], groundTruthTexts, category);
}
