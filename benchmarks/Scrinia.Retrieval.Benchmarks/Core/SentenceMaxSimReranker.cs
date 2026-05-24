using Scrinia.Core.Embeddings;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Retrieval.Benchmarks.Core;

/// <summary>
/// Sentence-level MaxSim reranker (model2vec-native, bi-encoder) with optional
/// blending against the first-stage score.
///
/// <para>For each candidate, computes cosine similarity between the query embedding
/// and every sentence embedding of the candidate, then takes the <b>max</b>. A long
/// doc whose mean embedding averages to a mediocre cosine score may still contain
/// one sentence that's a near-perfect semantic match — MaxSim surfaces that. The
/// intuition mirrors ColBERT's late-interaction trick but at sentence granularity
/// rather than token, which lets us reuse model2vec's existing single-vector API
/// without exposing token vectors.</para>
///
/// <para><b>Blending with first-stage.</b> Pure MaxSim score-replacement empirically
/// loses to no-reranking on workloads where BM25 carries the discriminating signal
/// (LongMemEval, LoCoMo) — the reranker throws away BM25's contribution. Mitigation:
/// blend <c>α · normalizedFirstStage + (1-α) · normalizedMaxSim</c>, with both
/// signals min-max normalized within the candidate pool so α is a clean weight in
/// [0, 1]. <see cref="FirstStageWeight"/> = 0 reduces to pure MaxSim (the prior
/// behavior), = 1 reduces to a no-op (first-stage order preserved).</para>
///
/// <para><b>Fallback when no sentence vectors exist</b> for a candidate (e.g. doc
/// was too short to split, or the embedder failed at ingest): use the first-stage
/// portion alone — the candidate participates in the ranking but contributes zero
/// MaxSim signal. The reranker never drops a candidate just because the sentence
/// index is empty.</para>
/// </summary>
public sealed class SentenceMaxSimReranker : ISearchReranker
{
    private readonly IEmbeddingProvider _embedder;
    private readonly Func<string, IReadOnlyList<float[]>?> _sentenceLookup;
    private readonly double _maxSimScale;
    private readonly double _firstStageWeight;

    /// <summary>Default scale brings normalized blended scores (0..1) into the
    /// same order-of-magnitude as first-stage raw scores so downstream MMR and
    /// tie-breaking behave consistently with the no-rerank baseline.</summary>
    public const double DefaultMaxSimScale = 100.0;

    /// <summary>Default blend weighting. Tuned empirically across convomem,
    /// LongMemEval, and LoCoMo: α=0.3 (30% first-stage, 70% MaxSim) strictly beats
    /// Sum-fusion on 2/3 benchmarks and ties on the third for R@5. Lower α gives
    /// MaxSim more voice, which helps on workloads where embeddings are
    /// discriminating; the 30% first-stage floor is enough BM25 weight to prevent
    /// the catastrophic single-session-assistant regression we observed at α=0.</summary>
    public const double DefaultFirstStageWeight = 0.3;

    /// <summary>Blend weight α applied to the (normalized) first-stage score.
    /// Final relevance = α · normalizedFirstStage + (1-α) · normalizedMaxSim,
    /// scaled by <see cref="DefaultMaxSimScale"/>. Clamped to [0, 1].</summary>
    public double FirstStageWeight => _firstStageWeight;

    public SentenceMaxSimReranker(
        IEmbeddingProvider embedder,
        Func<string, IReadOnlyList<float[]>?> sentenceLookup,
        double maxSimScale = DefaultMaxSimScale,
        double firstStageWeight = DefaultFirstStageWeight)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(sentenceLookup);
        _embedder = embedder;
        _sentenceLookup = sentenceLookup;
        _maxSimScale = maxSimScale;
        // Clamp rather than throw — a config typo of 1.2 should degrade to "pure
        // first-stage, MaxSim ignored" instead of crashing the bench mid-run.
        _firstStageWeight = Math.Clamp(firstStageWeight, 0.0, 1.0);
    }

    public async Task<IReadOnlyList<SearchResult>> RerankAsync(
        string query,
        IReadOnlyList<SearchResult> candidates,
        CancellationToken ct)
    {
        if (candidates.Count == 0) return candidates;

        float[]? queryVec = await _embedder.EmbedAsync(query, ct);
        if (queryVec is null) return candidates; // embedder unavailable → pass through

        // Pass 1: collect raw first-stage score, raw MaxSim (or 0 if no sentence
        // vectors), and a per-candidate "has MaxSim signal" flag.
        var raw = new List<(SearchResult Result, double FirstStage, double MaxSim, bool HasMaxSim)>(candidates.Count);
        foreach (var c in candidates)
        {
            string? name = ExtractName(c);
            if (name is null)
            {
                // Unknown result type (e.g. TopicResult) — keep at first-stage only.
                raw.Add((c, c.Score, 0, false));
                continue;
            }

            var sentenceVecs = _sentenceLookup(name);
            if (sentenceVecs is null || sentenceVecs.Count == 0)
            {
                raw.Add((c, c.Score, 0, false));
                continue;
            }

            double maxSim = MaxCosine(queryVec, sentenceVecs);
            raw.Add((c, c.Score, maxSim, true));
        }

        // Pass 2: min-max normalize each signal across the candidate pool so the
        // blend weight α has a clean [0,1] interpretation independent of raw
        // magnitudes. First-stage scores can be 0..500+, MaxSim is 0..1 — without
        // normalization, α=0.5 wouldn't actually weight them evenly.
        double minFs = double.MaxValue, maxFs = double.MinValue;
        double minMs = double.MaxValue, maxMs = double.MinValue;
        foreach (var r in raw)
        {
            if (r.FirstStage < minFs) minFs = r.FirstStage;
            if (r.FirstStage > maxFs) maxFs = r.FirstStage;
            if (r.HasMaxSim)
            {
                if (r.MaxSim < minMs) minMs = r.MaxSim;
                if (r.MaxSim > maxMs) maxMs = r.MaxSim;
            }
        }
        double fsRange = Math.Max(maxFs - minFs, 1e-9);
        double msRange = Math.Max(maxMs - minMs, 1e-9);

        // Pass 3: blend and rescore. Candidates without MaxSim signal use first-stage
        // alone (full α weight, no MaxSim contribution).
        var rescored = new List<(SearchResult Result, double Score)>(raw.Count);
        foreach (var r in raw)
        {
            double normFs = (r.FirstStage - minFs) / fsRange;
            double blend;
            if (r.HasMaxSim)
            {
                double normMs = (r.MaxSim - minMs) / msRange;
                blend = _firstStageWeight * normFs + (1 - _firstStageWeight) * normMs;
            }
            else
            {
                // No MaxSim → use normalized first-stage alone. Same scale as blended
                // candidates so the sort is well-defined across the mixed pool.
                blend = normFs;
            }
            double finalScore = blend * _maxSimScale;
            rescored.Add((Rescored(r.Result, finalScore), finalScore));
        }

        rescored.Sort((a, b) => b.Score.CompareTo(a.Score));
        return rescored.Select(t => t.Result).ToList();
    }

    private static string? ExtractName(SearchResult r) => r switch
    {
        EntryResult er => er.Item.Entry.Name,
        ChunkEntryResult cr => cr.ParentItem.Entry.Name,
        _ => null,
    };

    /// <summary>
    /// Returns a copy of <paramref name="orig"/> with <see cref="SearchResult.Score"/>
    /// replaced. Pattern-matches on runtime type because <c>with</c> on the abstract
    /// base would lose the derived shape. Topic results retain their original score.
    /// </summary>
    private static SearchResult Rescored(SearchResult orig, double newScore) => orig switch
    {
        EntryResult er => er with { Score = newScore },
        ChunkEntryResult cr => cr with { Score = newScore },
        _ => orig,
    };

    private static double MaxCosine(float[] queryVec, IReadOnlyList<float[]> sentenceVecs)
    {
        double max = 0;
        foreach (var sv in sentenceVecs)
        {
            double sim = CosineSimilarity(queryVec, sv);
            if (sim > max) max = sim;
        }
        return max;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        // Model2vec output is L2-normalized → dot product equals cosine sim.
        // Matches ScriniaRetriever.CosineSimilarity exactly so first-stage and
        // rerank stages share scoring semantics.
        int n = Math.Min(a.Length, b.Length);
        double dot = 0;
        for (int i = 0; i < n; i++) dot += a[i] * b[i];
        return dot;
    }
}
