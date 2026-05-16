using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>
/// Maximal Marginal Relevance (Carbonell &amp; Goldstein, 1998) diversity rerank. Greedy
/// algorithm: at each step, pick the candidate that maximises
/// <c>λ · relevance(c) − (1 − λ) · max_{s ∈ selected} sim(c, s)</c>, then move that
/// candidate into the selected set.
///
/// <para>Used by the hint pipeline to fix a documented retrieval failure: one chatty
/// session can flood the top-K results because its chunks all share the query's
/// vocabulary. With λ ≈ 0.5–0.7 the reranker breaks the flood by demoting candidates
/// that look too much like already-selected ones, surfacing memories from other
/// sources (a <c>/findings/</c> note, a <c>/skill/</c> entry) that the BM25 top-3
/// would otherwise miss.</para>
///
/// <para>Similarity is <b>TF-cosine</b> over <see cref="ArtifactEntry.TermFrequencies"/>
/// — already in memory after <c>WeightedFieldScorer.SearchAll</c> runs, so MMR is
/// effectively free on the search hot path. Legacy entries with null TF (the v2 index
/// format) are treated as maximally-diverse to everything; they neither flood nor get
/// flooded out.</para>
///
/// <para>λ corner cases:
/// <list type="bullet">
/// <item>λ = 1.0 → pure relevance ordering (the pre-MMR behavior)</item>
/// <item>λ = 0.0 → pure diversity (least similar to already-selected wins)</item>
/// <item>λ ∈ (0, 1) → balanced; 0.6 is the project default</item>
/// </list>
/// </para>
/// </summary>
public static class MmrReranker
{
    /// <summary>
    /// Returns the MMR-diversified top-<paramref name="k"/> from <paramref name="candidates"/>.
    /// Inputs are not mutated. If <paramref name="candidates"/> has fewer than k items,
    /// returns them all (sorted by relevance descending). Negative/zero scores are tolerated
    /// — the relevance term is rescaled to [0, 1] via max-norm before the MMR step so
    /// score-magnitude differences across queries don't change the diversity tradeoff.
    /// </summary>
    /// <param name="candidates">Pool to diversify. Usually the top-N from a relevance ranker.</param>
    /// <param name="k">How many to return after rerank.</param>
    /// <param name="lambda">Diversity tradeoff in [0, 1]. 1 = pure relevance, 0 = pure diversity.</param>
    public static IReadOnlyList<SearchResult> Rerank(
        IReadOnlyList<SearchResult> candidates,
        int k,
        double lambda)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0) return [];
        if (k <= 0) return [];
        if (lambda < 0) lambda = 0;
        if (lambda > 1) lambda = 1;

        // Fewer candidates than requested: just return them sorted by score desc — no
        // diversity tradeoff to make.
        if (candidates.Count <= k)
        {
            var sorted = candidates.ToArray();
            Array.Sort(sorted, (a, b) => b.Score.CompareTo(a.Score));
            return sorted;
        }

        // Normalise relevance to [0, 1] via max-norm. Without this, a query that produces
        // top scores in the 500s would see the (1-λ) similarity term (always in [0, 1])
        // overwhelmed by relevance, and a query with top scores in the 1s would see the
        // opposite. Normalising keeps λ's meaning stable across queries.
        double maxScore = 0;
        foreach (var c in candidates) if (c.Score > maxScore) maxScore = c.Score;
        if (maxScore <= 0)
        {
            // All zero / negative — degenerate; just return the first k in original order.
            return candidates.Take(k).ToArray();
        }

        int n = candidates.Count;
        var relevance = new double[n];
        for (int i = 0; i < n; i++) relevance[i] = candidates[i].Score / maxScore;

        // Greedy selection. Pick the highest-relevance candidate first (no similarity term
        // applies to the empty selected set), then each subsequent pick maximises MMR
        // against the running selected set.
        var selected = new List<int>(k);
        var taken = new bool[n];

        // Cache pairwise similarities lazily — we only compute sim(i, j) when j is selected
        // and i is a remaining candidate. For k=3 and n=10 that's at most 27 cosine ops
        // over already-loaded TF dicts; negligible.
        var simCache = new double[n, n];
        var simComputed = new bool[n, n];

        for (int step = 0; step < k; step++)
        {
            int bestIdx = -1;
            double bestMmr = double.NegativeInfinity;

            for (int i = 0; i < n; i++)
            {
                if (taken[i]) continue;

                double maxSim = 0;
                foreach (int j in selected)
                {
                    double sim;
                    if (simComputed[i, j])
                    {
                        sim = simCache[i, j];
                    }
                    else
                    {
                        sim = TfCosine(candidates[i], candidates[j]);
                        simCache[i, j] = sim;
                        simCache[j, i] = sim;
                        simComputed[i, j] = true;
                        simComputed[j, i] = true;
                    }
                    if (sim > maxSim) maxSim = sim;
                }

                double mmr = lambda * relevance[i] - (1 - lambda) * maxSim;
                if (mmr > bestMmr)
                {
                    bestMmr = mmr;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0) break; // defensive — shouldn't happen given the count check above
            selected.Add(bestIdx);
            taken[bestIdx] = true;
        }

        return selected.Select(i => candidates[i]).ToList();
    }

    /// <summary>
    /// Cosine similarity between two memories' term-frequency dicts. Null TF (v2 legacy
    /// entries) returns 0 — treats those as maximally diverse so they neither flood nor
    /// get flooded by the rerank.
    /// </summary>
    private static double TfCosine(SearchResult a, SearchResult b)
    {
        Dictionary<string, int>? tfA = GetTf(a);
        Dictionary<string, int>? tfB = GetTf(b);
        if (tfA is null || tfB is null) return 0;
        if (tfA.Count == 0 || tfB.Count == 0) return 0;

        // Iterate the smaller dict against TryGetValue on the larger — keeps the inner
        // loop bounded by min(|a|, |b|).
        Dictionary<string, int> small;
        Dictionary<string, int> large;
        if (tfA.Count <= tfB.Count) { small = tfA; large = tfB; }
        else { small = tfB; large = tfA; }

        long dot = 0;
        long smallSq = 0;
        foreach (var kv in small)
        {
            smallSq += (long)kv.Value * kv.Value;
            if (large.TryGetValue(kv.Key, out int lv))
                dot += (long)kv.Value * lv;
        }

        long largeSq = 0;
        foreach (var kv in large) largeSq += (long)kv.Value * kv.Value;

        if (smallSq == 0 || largeSq == 0) return 0;
        return dot / Math.Sqrt((double)smallSq * largeSq);
    }

    /// <summary>
    /// Pulls the TF dict from any SearchResult subtype. TopicResult has no TF — return null
    /// so the cosine helper treats it as fully diverse from anything else. Returns the
    /// concrete Dictionary type to avoid an interface dispatch on every TryGetValue.
    /// </summary>
    private static Dictionary<string, int>? GetTf(SearchResult r) => r switch
    {
        EntryResult er => er.Item.Entry.TermFrequencies,
        ChunkEntryResult cr => cr.ParentItem.Entry.TermFrequencies,
        _ => null,
    };
}
