using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Core;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Locks down the R@K / NDCG@10 / per-category math for each of the three scoring
/// modes the bench supports. Off-by-ones here invalidate every benchmark result,
/// and any-hit-vs-fractional drift would silently make our numbers incomparable
/// to MemPalace's published evaluators.
/// </summary>
public class BenchmarkScorerTests
{
    // ----- ScoreAnyHitById (MemBench, LongMemEval) -----

    [Fact]
    public void AnyHitById_hit_at_rank_1_scores_full_recall_and_ndcg_1()
    {
        var q = IdQuestion("q1", ["target"]);
        var hits = new[]
        {
            new RetrievalHit("target", "", 100),
            new RetrievalHit("other-1", "", 50),
        };

        var r = BenchmarkScorer.ScoreAnyHitById(q, hits);

        r.RecallAt5.Should().Be(1.0);
        r.RecallAt10.Should().Be(1.0);
        r.RecallAt50.Should().Be(1.0);
        r.Ndcg10.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void AnyHitById_hit_at_rank_3_scores_ndcg_correctly()
    {
        var q = IdQuestion("q2", ["target"]);
        var hits = new[]
        {
            new RetrievalHit("d1", "", 30),
            new RetrievalHit("d2", "", 20),
            new RetrievalHit("target", "", 15),
            new RetrievalHit("d4", "", 10),
        };

        var r = BenchmarkScorer.ScoreAnyHitById(q, hits);

        r.RecallAt5.Should().Be(1.0);
        r.RecallAt10.Should().Be(1.0);
        // Unified NDCG: relevances = [0,0,1,0]. DCG = 1/log2(4) = 0.5.
        // Ideal: 1 gold, so IDCG = 1/log2(2) = 1.0. NDCG = 0.5 / 1.0 = 0.5.
        r.Ndcg10.Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void AnyHitById_hit_outside_top_10_zero_at_5_and_10_one_at_50()
    {
        var q = IdQuestion("q3", ["target"]);
        var hits = new List<RetrievalHit>();
        for (int i = 0; i < 10; i++) hits.Add(new RetrievalHit($"d{i}", "", 100 - i));
        hits.Add(new RetrievalHit("target", "", 0)); // rank 11

        var r = BenchmarkScorer.ScoreAnyHitById(q, hits);

        r.RecallAt5.Should().Be(0.0);
        r.RecallAt10.Should().Be(0.0);
        r.RecallAt50.Should().Be(1.0);
        r.Ndcg10.Should().Be(0.0);
    }

    [Fact]
    public void AnyHitById_multiple_gold_finds_one_already_satisfies_recall()
    {
        // any-hit semantics: hitting ONE of three golds is enough for R@K = 1.0
        var q = IdQuestion("q4", ["a", "b", "c"]);
        var hits = new[]
        {
            new RetrievalHit("z", "", 100),
            new RetrievalHit("b", "", 50),
        };

        var r = BenchmarkScorer.ScoreAnyHitById(q, hits);

        r.RecallAt5.Should().Be(1.0);
        // NDCG: relevances = [0,1]. DCG = 1/log2(3) ≈ 0.6309.
        // Ideal: min(3,10)=3 golds, IDCG = 1/log2(2) + 1/log2(3) + 1/log2(4) ≈ 2.131.
        // NDCG ≈ 0.296.
        double expectedDcg = 1.0 / Math.Log2(3);
        double expectedIdcg = 1.0 / Math.Log2(2) + 1.0 / Math.Log2(3) + 1.0 / Math.Log2(4);
        r.Ndcg10.Should().BeApproximately(expectedDcg / expectedIdcg, 1e-9);
    }

    [Fact]
    public void AnyHitById_empty_hits_return_zero_recall()
    {
        var q = IdQuestion("q5", ["target"]);
        var r = BenchmarkScorer.ScoreAnyHitById(q, []);
        r.RecallAt5.Should().Be(0.0);
        r.RecallAt10.Should().Be(0.0);
        r.RecallAt50.Should().Be(0.0);
        r.Ndcg10.Should().Be(0.0);
    }

    // ----- ScoreFractionalById (LoCoMo) -----

    [Fact]
    public void FractionalById_finds_two_of_three_evidence_scores_two_thirds()
    {
        var q = IdQuestion("loco1", ["session_1", "session_2", "session_3"]);
        var hits = new[]
        {
            new RetrievalHit("session_1", "", 100),
            new RetrievalHit("session_99", "", 50),
            new RetrievalHit("session_3", "", 25),
        };

        var r = BenchmarkScorer.ScoreFractionalById(q, hits);

        // 2 of 3 evidence ids in top-5 → fractional = 0.6666...
        r.RecallAt5.Should().BeApproximately(2.0 / 3.0, 1e-9);
        r.RecallAt10.Should().BeApproximately(2.0 / 3.0, 1e-9);
        r.RecallAt50.Should().BeApproximately(2.0 / 3.0, 1e-9);
    }

    [Fact]
    public void FractionalById_recall_at_k_is_capped_by_top_k_window()
    {
        // Evidence at ranks 1 and 7. R@5 should miss rank-7 → 1/2 = 0.5.
        // R@10 should include both → 1.0.
        var q = IdQuestion("loco2", ["a", "b"]);
        var hits = new[]
        {
            new RetrievalHit("a", "", 100),
            new RetrievalHit("noise1", "", 90),
            new RetrievalHit("noise2", "", 80),
            new RetrievalHit("noise3", "", 70),
            new RetrievalHit("noise4", "", 60),
            new RetrievalHit("noise5", "", 50),
            new RetrievalHit("b", "", 40),
        };

        var r = BenchmarkScorer.ScoreFractionalById(q, hits);

        r.RecallAt5.Should().BeApproximately(0.5, 1e-9);
        r.RecallAt10.Should().BeApproximately(1.0, 1e-9);
        r.RecallAt50.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void FractionalById_empty_evidence_returns_1_for_vacuous_recall()
    {
        // Matches upstream LoCoMo's `if not evidence_ids: return 1.0`.
        var q = IdQuestion("loco3", []);
        var hits = new[] { new RetrievalHit("anything", "", 100) };

        var r = BenchmarkScorer.ScoreFractionalById(q, hits);

        r.RecallAt5.Should().Be(1.0);
        r.RecallAt10.Should().Be(1.0);
        r.RecallAt50.Should().Be(1.0);
        // No golds → no ideal DCG → NDCG = 0 by convention.
        r.Ndcg10.Should().Be(0.0);
    }

    // ----- ScoreFractionalByText (ConvoMem) -----

    [Fact]
    public void FractionalByText_substring_match_case_insensitive()
    {
        var q = TextQuestion("conv1", ["i hate phones with bad battery"]);
        var hits = new[]
        {
            new RetrievalHit("d1", "Some unrelated content", 100),
            new RetrievalHit("d2", "User says: I HATE PHONES with bad battery life", 80),
        };

        var r = BenchmarkScorer.ScoreFractionalByText(q, hits);

        r.RecallAt5.Should().Be(1.0);
    }

    [Fact]
    public void FractionalByText_finds_one_of_three_evidence_scores_one_third()
    {
        var q = TextQuestion("conv2",
            ["he loves coffee", "she lives in tokyo", "they own a dog"]);
        var hits = new[]
        {
            new RetrievalHit("d1", "She lives in Tokyo since 2018.", 100),
            new RetrievalHit("d2", "Random other content.", 50),
        };

        var r = BenchmarkScorer.ScoreFractionalByText(q, hits);

        r.RecallAt5.Should().BeApproximately(1.0 / 3.0, 1e-9);
    }

    [Fact]
    public void FractionalByText_bidirectional_substring_matches_both_directions()
    {
        // ConvoMem upstream does `ev_text in ret_text or ret_text in ev_text`.
        // Verify the second direction: a retrieved doc shorter than the evidence
        // still counts as a hit when contained in the evidence.
        var q = TextQuestion("conv3", ["my favorite color is blue and i wear it often"]);
        var hits = new[]
        {
            new RetrievalHit("d1", "favorite color is blue", 100),
        };

        var r = BenchmarkScorer.ScoreFractionalByText(q, hits);

        r.RecallAt5.Should().Be(1.0);
    }

    [Fact]
    public void FractionalByText_recall_at_k_window_excludes_later_matches()
    {
        var q = TextQuestion("conv4", ["evidence one", "evidence two"]);
        var hits = new List<RetrievalHit> { new("d0", "evidence one match here", 100) };
        for (int i = 1; i <= 5; i++) hits.Add(new RetrievalHit($"d{i}", "noise", 100 - i));
        hits.Add(new RetrievalHit("d6", "this contains evidence two phrase", 10)); // rank 7

        var r = BenchmarkScorer.ScoreFractionalByText(q, hits);

        // Top-5 sees only "evidence one" → 1/2. Top-10 sees both → 2/2.
        r.RecallAt5.Should().BeApproximately(0.5, 1e-9);
        r.RecallAt10.Should().BeApproximately(1.0, 1e-9);
    }

    // ----- Aggregate / PerCategory -----

    [Fact]
    public void Aggregate_means_across_questions()
    {
        var results = new[]
        {
            Result("q1", "cat-a", r5: 1.0, r10: 1.0, r50: 1.0, ndcg: 1.0),
            Result("q2", "cat-a", r5: 0.0, r10: 1.0, r50: 1.0, ndcg: 0.5),
            Result("q3", "cat-b", r5: 0.0, r10: 0.0, r50: 1.0, ndcg: 0.0),
            Result("q4", "cat-b", r5: 1.0, r10: 1.0, r50: 1.0, ndcg: 1.0),
        };

        var agg = BenchmarkScorer.Aggregate(results);

        agg.QuestionCount.Should().Be(4);
        agg.RecallAt5.Should().BeApproximately(0.5, 1e-9);
        agg.RecallAt10.Should().BeApproximately(0.75, 1e-9);
        agg.RecallAt50.Should().BeApproximately(1.0, 1e-9);
        agg.Ndcg10.Should().BeApproximately(0.625, 1e-9);
    }

    [Fact]
    public void Aggregate_means_fractional_recall_correctly()
    {
        // Verify the aggregator handles non-binary per-question recall (the
        // ConvoMem/LoCoMo case) — easy to regress by replacing with boolean counts.
        var results = new[]
        {
            Result("q1", "cat-a", r5: 0.333, r10: 0.5, r50: 1.0, ndcg: 0.3),
            Result("q2", "cat-a", r5: 0.667, r10: 1.0, r50: 1.0, ndcg: 0.8),
        };

        var agg = BenchmarkScorer.Aggregate(results);

        agg.RecallAt5.Should().BeApproximately(0.5, 1e-3);
        agg.RecallAt10.Should().BeApproximately(0.75, 1e-9);
        agg.Ndcg10.Should().BeApproximately(0.55, 1e-9);
    }

    [Fact]
    public void Per_category_groups_results_correctly()
    {
        var results = new[]
        {
            Result("q1", "cat-a", r5: 1.0, r10: 1.0, r50: 1.0, ndcg: 1.0),
            Result("q2", "cat-a", r5: 0.0, r10: 1.0, r50: 1.0, ndcg: 0.5),
            Result("q3", "cat-b", r5: 1.0, r10: 1.0, r50: 1.0, ndcg: 1.0),
        };

        var per = BenchmarkScorer.PerCategory(results);

        per.Should().ContainKey("cat-a");
        per["cat-a"].QuestionCount.Should().Be(2);
        per["cat-a"].RecallAt5.Should().BeApproximately(0.5, 1e-9);
        per["cat-b"].QuestionCount.Should().Be(1);
        per["cat-b"].RecallAt5.Should().Be(1.0);
    }

    [Fact]
    public void Aggregate_reports_surfaced_and_correct_rates_over_judged_only()
    {
        var q = new BenchmarkQuestion("q", "query", [], ["g"], [], "single_hop");
        var hits = new List<RetrievalHit> { new("g", "t", 1.0) };
        var correct  = BenchmarkScorer.ScoreFractionalById(q, hits) with { SurfacedFromContext = true,  AnsweredCorrectly = true };
        var wrong    = BenchmarkScorer.ScoreFractionalById(q, hits) with { SurfacedFromContext = true,  AnsweredCorrectly = false };
        var missed   = BenchmarkScorer.ScoreFractionalById(q, hits) with { SurfacedFromContext = false, AnsweredCorrectly = false };
        var unjudged = BenchmarkScorer.ScoreFractionalById(q, hits);

        var agg = BenchmarkScorer.Aggregate([correct, wrong, missed, unjudged]);

        Assert.Equal(4, agg.QuestionCount);
        Assert.Equal(2.0 / 3.0, agg.SurfacedRate, 5);
        Assert.Equal(1.0 / 3.0, agg.CorrectRate, 5);
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

    // ----- helpers -----

    private static BenchmarkQuestion IdQuestion(string id, string[] groundTruthIds, string category = "default") =>
        new(id, "query text", [], groundTruthIds, [], category);

    private static BenchmarkQuestion TextQuestion(string id, string[] groundTruthTexts, string category = "default") =>
        new(id, "query text", [], [], groundTruthTexts, category);

    private static QuestionResult Result(string id, string category, double r5, double r10, double r50, double ndcg) =>
        new(
            QuestionId: id,
            Query: "",
            Category: category,
            GroundTruthIds: ["t"],
            GroundTruthTexts: [],
            Retrieved: [],
            RecallAt5: r5,
            RecallAt10: r10,
            RecallAt50: r50,
            Ndcg10: ndcg);
}
