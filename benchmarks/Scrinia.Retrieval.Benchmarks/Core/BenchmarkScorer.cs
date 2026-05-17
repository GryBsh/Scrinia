namespace Scrinia.Retrieval.Benchmarks.Core;

/// <summary>
/// Computes R@K and NDCG@10 for a benchmark question's retrieved hits against its
/// ground truth. Two matching modes are supported:
///
/// <list type="bullet">
///   <item><description>ID-match: <see cref="BenchmarkQuestion.GroundTruthIds"/> non-empty.
///     A hit is relevant when its <see cref="RetrievalHit.Id"/> matches any
///     ground-truth ID (case-insensitive ordinal).</description></item>
///   <item><description>Text-match: <see cref="BenchmarkQuestion.GroundTruthTexts"/>
///     non-empty (ConvoMem). A hit is relevant when its <see cref="RetrievalHit.Text"/>
///     contains any ground-truth text as a substring (case-insensitive).</description></item>
/// </list>
///
/// When both ground-truth sets are populated, ID-match takes precedence (treating
/// text-match as a fallback for benchmarks that don't ship stable doc IDs).
/// </summary>
public static class BenchmarkScorer
{
    /// <summary>
    /// Scores one question. Returns a <see cref="QuestionResult"/> with R@5, R@10,
    /// R@50, and NDCG@10 computed from the supplied hits.
    /// </summary>
    public static QuestionResult ScoreQuestion(BenchmarkQuestion question, IReadOnlyList<RetrievalHit> hits)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(hits);

        var matcher = BuildMatcher(question);

        int firstHitRank = -1;
        for (int i = 0; i < hits.Count; i++)
        {
            if (matcher(hits[i]))
            {
                firstHitRank = i + 1;
                break;
            }
        }

        bool hitAt5 = firstHitRank is > 0 and <= 5;
        bool hitAt10 = firstHitRank is > 0 and <= 10;
        bool hitAt50 = firstHitRank is > 0 and <= 50;
        double ndcg10 = (firstHitRank is > 0 and <= 10)
            ? 1.0 / Math.Log2(firstHitRank + 1)
            : 0.0;

        return new QuestionResult(
            QuestionId: question.Id,
            Query: question.QueryText,
            Category: question.Category,
            GroundTruthIds: question.GroundTruthIds,
            GroundTruthTexts: question.GroundTruthTexts,
            Retrieved: hits,
            HitAt5: hitAt5,
            HitAt10: hitAt10,
            HitAt50: hitAt50,
            Ndcg10: ndcg10);
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
            if (r.HitAt5) r5++;
            if (r.HitAt10) r10++;
            if (r.HitAt50) r50++;
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

    private static Func<RetrievalHit, bool> BuildMatcher(BenchmarkQuestion question)
    {
        if (question.GroundTruthIds.Count > 0)
        {
            var ids = new HashSet<string>(question.GroundTruthIds, StringComparer.OrdinalIgnoreCase);
            return hit => ids.Contains(hit.Id);
        }

        if (question.GroundTruthTexts.Count > 0)
        {
            // Substring match on the haystack doc text. Pre-lowered once per question.
            var lowered = question.GroundTruthTexts
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.ToLowerInvariant())
                .ToArray();
            return hit =>
            {
                string h = hit.Text.ToLowerInvariant();
                foreach (string gt in lowered)
                    if (h.Contains(gt, StringComparison.Ordinal)) return true;
                return false;
            };
        }

        return static _ => false;
    }
}
