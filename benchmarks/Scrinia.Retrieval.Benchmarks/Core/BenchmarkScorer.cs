namespace Scrinia.Retrieval.Benchmarks.Core;

/// <summary>
/// Computes R@K and NDCG@10 for retrieval benchmark questions. Each upstream benchmark
/// defines R@K slightly differently, and getting the wrong one silently distorts every
/// reported number — so we ship one scoring function per benchmark family, mirroring
/// the upstream evaluator verbatim:
///
/// <list type="bullet">
/// <item><description><b>Any-hit by id</b> (<see cref="ScoreAnyHitById"/>) — used by
/// MemBench and LongMemEval. R@K is 1.0 if ANY ground-truth id appears in the top-K
/// retrieved set, else 0.0. Matches <c>bool(target_ids &amp; set(retrieved_ids))</c>
/// and <c>float(any(cid in top_k_ids for cid in correct_ids))</c> in the upstream
/// Python.</description></item>
/// <item><description><b>Fractional by id</b> (<see cref="ScoreFractionalById"/>) —
/// used by LoCoMo. R@K = (count of evidence ids in top-K) / (total evidence ids).
/// Matches <c>sum(1 for eid in evidence_ids if eid in retrieved_ids) /
/// len(evidence_ids)</c> upstream.</description></item>
/// <item><description><b>Fractional by text</b> (<see cref="ScoreFractionalByText"/>)
/// — used by ConvoMem. R@K = (count of evidence texts substring-matched by any
/// retrieved doc in top-K) / (total evidence texts). Matches the
/// <c>ev_text in ret_text or ret_text in ev_text</c> bidirectional substring loop
/// upstream.</description></item>
/// </list>
///
/// <para><b>NDCG@10</b> is unified across all three: standard binary-relevance
/// cumulative DCG normalized by ideal DCG. For each retrieved doc <c>i</c> in top-10,
/// relevance is 1 if it matches gold (id-match or text-match per benchmark), 0
/// otherwise. DCG = sum(rel_i / log2(i+2)). Ideal DCG = sum(1/log2(i+2)) for
/// <c>i</c> in <c>[0, min(num_golds, 10))</c>. NDCG = DCG / IDCG when IDCG > 0.
/// This replaces an earlier first-hit-only formula that diverged from upstream's
/// per-position cumulative metric.</para>
///
/// <para>An earlier unified <c>ScoreQuestion</c> implementation used any-hit on a
/// single matcher (id or text) and a first-hit NDCG formula, which inflated ConvoMem
/// and LoCoMo recall and made NDCG@10 incomparable to upstream's standard binary NDCG.
/// The three-function split below makes the choice of semantics explicit at the
/// benchmark CLI boundary.</para>
/// </summary>
public static class BenchmarkScorer
{
    /// <summary>
    /// Score one question via per-benchmark scoring rules. Each CLI command supplies
    /// the variant matching its upstream evaluator.
    /// </summary>
    public delegate QuestionResult Scorer(BenchmarkQuestion question, IReadOnlyList<RetrievalHit> hits);

    private static readonly int[] RecallKs = [5, 10, 50];
    private const int NdcgK = 10;

    /// <summary>
    /// Any-hit recall on id-match, binary NDCG@10. MemBench, LongMemEval.
    /// </summary>
    public static QuestionResult ScoreAnyHitById(BenchmarkQuestion q, IReadOnlyList<RetrievalHit> hits)
    {
        ArgumentNullException.ThrowIfNull(q);
        ArgumentNullException.ThrowIfNull(hits);

        var gold = new HashSet<string>(q.GroundTruthIds, StringComparer.OrdinalIgnoreCase);
        bool IsRelevant(RetrievalHit h) => gold.Contains(h.Id);

        var relevances = BuildRelevances(hits, IsRelevant, NdcgK);
        double r5 = AnyHitAtK(hits, IsRelevant, 5);
        double r10 = AnyHitAtK(hits, IsRelevant, 10);
        double r50 = AnyHitAtK(hits, IsRelevant, 50);
        double ndcg = NdcgFromRelevances(relevances, gold.Count);

        return BuildResult(q, hits, r5, r10, r50, ndcg);
    }

    /// <summary>
    /// Fractional recall on id-match, binary NDCG@10. LoCoMo.
    /// </summary>
    public static QuestionResult ScoreFractionalById(BenchmarkQuestion q, IReadOnlyList<RetrievalHit> hits)
    {
        ArgumentNullException.ThrowIfNull(q);
        ArgumentNullException.ThrowIfNull(hits);

        var gold = new HashSet<string>(q.GroundTruthIds, StringComparer.OrdinalIgnoreCase);
        bool IsRelevant(RetrievalHit h) => gold.Contains(h.Id);

        var relevances = BuildRelevances(hits, IsRelevant, NdcgK);
        double r5 = FractionalRecallAtK_ById(hits, q.GroundTruthIds, 5);
        double r10 = FractionalRecallAtK_ById(hits, q.GroundTruthIds, 10);
        double r50 = FractionalRecallAtK_ById(hits, q.GroundTruthIds, 50);
        double ndcg = NdcgFromRelevances(relevances, gold.Count);

        return BuildResult(q, hits, r5, r10, r50, ndcg);
    }

    /// <summary>
    /// Fractional recall on bidirectional substring text-match, binary NDCG@10. ConvoMem.
    /// </summary>
    public static QuestionResult ScoreFractionalByText(BenchmarkQuestion q, IReadOnlyList<RetrievalHit> hits)
    {
        ArgumentNullException.ThrowIfNull(q);
        ArgumentNullException.ThrowIfNull(hits);

        // Pre-lower evidence and (per-K below) retrieved text. ConvoMem's upstream eval
        // does case-insensitive substring matching in both directions; we mirror that.
        string[] evidenceLowered = q.GroundTruthTexts
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.ToLowerInvariant())
            .ToArray();

        // Precompute lowered retrieved text once — used by every K and by the NDCG path.
        string[] hitsLowered = new string[hits.Count];
        for (int i = 0; i < hits.Count; i++)
            hitsLowered[i] = hits[i].Text?.ToLowerInvariant() ?? string.Empty;

        bool IsRelevant(int hitIdx) =>
            evidenceLowered.Any(ev => HitContainsText(hitsLowered[hitIdx], ev));

        var relevances = new int[Math.Min(NdcgK, hits.Count)];
        for (int i = 0; i < relevances.Length; i++)
            relevances[i] = IsRelevant(i) ? 1 : 0;

        double r5 = FractionalRecallAtK_ByText(hitsLowered, evidenceLowered, 5);
        double r10 = FractionalRecallAtK_ByText(hitsLowered, evidenceLowered, 10);
        double r50 = FractionalRecallAtK_ByText(hitsLowered, evidenceLowered, 50);
        double ndcg = NdcgFromRelevances(relevances, evidenceLowered.Length);

        return BuildResult(q, hits, r5, r10, r50, ndcg);
    }

    /// <summary>
    /// Aggregates per-question results into a single <see cref="AggregateMetrics"/>.
    /// All metrics are simple means across the questions in <paramref name="results"/>.
    /// </summary>
    public static AggregateMetrics Aggregate(IReadOnlyCollection<QuestionResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
            return new AggregateMetrics(0, 0, 0, 0, 0);

        double r5 = 0, r10 = 0, r50 = 0, ndcg = 0;
        foreach (var r in results)
        {
            r5 += r.RecallAt5;
            r10 += r.RecallAt10;
            r50 += r.RecallAt50;
            ndcg += r.Ndcg10;
        }

        int n = results.Count;
        return new AggregateMetrics(
            QuestionCount: n,
            RecallAt5: r5 / n,
            RecallAt10: r10 / n,
            RecallAt50: r50 / n,
            Ndcg10: ndcg / n);
    }

    /// <summary>
    /// Groups <paramref name="results"/> by <see cref="QuestionResult.Category"/> and
    /// returns per-category aggregates. Categories with zero questions are omitted.
    /// </summary>
    public static IReadOnlyDictionary<string, AggregateMetrics> PerCategory(IReadOnlyCollection<QuestionResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return results
            .GroupBy(r => string.IsNullOrEmpty(r.Category) ? "uncategorized" : r.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Aggregate(g.ToList()), StringComparer.OrdinalIgnoreCase);
    }

    // ----- helpers -----

    private static double AnyHitAtK(IReadOnlyList<RetrievalHit> hits, Func<RetrievalHit, bool> isRelevant, int k)
    {
        int upper = Math.Min(k, hits.Count);
        for (int i = 0; i < upper; i++)
            if (isRelevant(hits[i])) return 1.0;
        return 0.0;
    }

    private static double FractionalRecallAtK_ById(
        IReadOnlyList<RetrievalHit> hits, IReadOnlyList<string> evidenceIds, int k)
    {
        if (evidenceIds.Count == 0) return 1.0;
        int upper = Math.Min(k, hits.Count);
        var topIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < upper; i++) topIds.Add(hits[i].Id);

        int found = 0;
        foreach (string eid in evidenceIds)
            if (topIds.Contains(eid)) found++;
        return (double)found / evidenceIds.Count;
    }

    private static double FractionalRecallAtK_ByText(
        string[] hitsLowered, string[] evidenceLowered, int k)
    {
        if (evidenceLowered.Length == 0) return 1.0;
        int upper = Math.Min(k, hitsLowered.Length);

        int found = 0;
        foreach (string ev in evidenceLowered)
        {
            for (int i = 0; i < upper; i++)
            {
                if (HitContainsText(hitsLowered[i], ev))
                {
                    found++;
                    break;
                }
            }
        }
        return (double)found / evidenceLowered.Length;
    }

    /// <summary>
    /// Bidirectional substring match used by ConvoMem's upstream evaluator:
    /// <c>ev in ret or ret in ev</c>. Both inputs must already be lower-cased.
    /// </summary>
    private static bool HitContainsText(string hitLowered, string evidenceLowered)
    {
        if (string.IsNullOrEmpty(evidenceLowered) || string.IsNullOrEmpty(hitLowered))
            return false;
        return hitLowered.Contains(evidenceLowered, StringComparison.Ordinal)
            || evidenceLowered.Contains(hitLowered, StringComparison.Ordinal);
    }

    private static int[] BuildRelevances(
        IReadOnlyList<RetrievalHit> hits, Func<RetrievalHit, bool> isRelevant, int cap)
    {
        int upper = Math.Min(cap, hits.Count);
        var rel = new int[upper];
        for (int i = 0; i < upper; i++)
            rel[i] = isRelevant(hits[i]) ? 1 : 0;
        return rel;
    }

    /// <summary>
    /// Standard NDCG@10 with binary relevance: DCG = sum(rel_i / log2(i+2)) for
    /// <paramref name="relevances"/>; ideal DCG = sum(1/log2(i+2)) for
    /// <c>i</c> in <c>[0, min(numGolds, 10))</c>; returns DCG / IDCG when IDCG > 0,
    /// else 0. Matches MemPalace's <c>longmemeval_bench.py</c> <c>dcg()</c> helper.
    /// </summary>
    private static double NdcgFromRelevances(int[] relevances, int numGolds)
    {
        if (numGolds <= 0) return 0.0;
        double dcg = 0.0;
        for (int i = 0; i < relevances.Length; i++)
            if (relevances[i] != 0)
                dcg += relevances[i] / Math.Log2(i + 2);

        int idealCount = Math.Min(numGolds, NdcgK);
        double idcg = 0.0;
        for (int i = 0; i < idealCount; i++)
            idcg += 1.0 / Math.Log2(i + 2);

        return idcg > 0.0 ? dcg / idcg : 0.0;
    }

    private static QuestionResult BuildResult(
        BenchmarkQuestion q, IReadOnlyList<RetrievalHit> hits,
        double r5, double r10, double r50, double ndcg) =>
        new(
            QuestionId: q.Id,
            Query: q.QueryText,
            Category: q.Category,
            GroundTruthIds: q.GroundTruthIds,
            GroundTruthTexts: q.GroundTruthTexts,
            Retrieved: hits,
            RecallAt5: r5,
            RecallAt10: r10,
            RecallAt50: r50,
            Ndcg10: ndcg);
}
