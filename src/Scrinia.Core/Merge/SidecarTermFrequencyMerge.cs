namespace Scrinia.Core.Merge;

/// <summary>
/// The rule for merging two sidecars' term-frequency maps — <b>the authoritative copy</b>.
///
/// <para>Term frequencies are what BM25 scores against, so losing them is a silent ranking change
/// rather than a visible failure. Both <c>.meta.json</c> mergers had a hole here:
/// <c>Scrinia.Merge.MetaJsonMerger</c> never merged the field at all — it preserved OURS verbatim
/// and dropped theirs' contribution on every git merge — and <c>reconcile()</c>'s auto-resolver
/// unioned correctly but only when BOTH sides carried a map, so a one-sided map vanished.</para>
///
/// <para><b>One duplicate survives, and it is forced.</b> <c>Scrinia.Merge</c> is a standalone AOT
/// executable with zero project references by hard constraint. Its copy names this type as the
/// original, and <c>Scrinia.Merge.Tests.SidecarTermFrequencyMergeParityTests</c> runs the same
/// cases through both.</para>
/// </summary>
public static class SidecarTermFrequencyMerge
{
    /// <summary>
    /// Unions two term-frequency maps: every key from either side, and the LARGER count on a key
    /// both carry.
    ///
    /// <para><b>Max, not sum.</b> A count is the term's frequency in one memory's content, not a
    /// running total. Two branches that each appended the same chunk hold overlapping counts, and
    /// summing them would inflate a term on every merge, compounding — the same class of bug as
    /// re-boosting a keyword on every append. Max is the smallest answer that loses nothing.</para>
    ///
    /// <para><b>A null side contributes nothing; it does not veto.</b> Null means "this side
    /// recorded no term frequencies", so the other side's map survives whole. Treating null as a
    /// reason to drop everything is exactly the defect this rule replaces.</para>
    /// </summary>
    /// <returns>The merged map, or null when neither side carried one.</returns>
    public static Dictionary<string, int>? Union(
        IReadOnlyDictionary<string, int>? ours,
        IReadOnlyDictionary<string, int>? theirs)
    {
        if (ours is null && theirs is null) return null;

        // Ordinal, matching the map TextAnalysis produces: these keys are already-normalized index
        // tokens, and folding case here would merge two tokens the scorer treats as distinct.
        var merged = new Dictionary<string, int>(StringComparer.Ordinal);
        if (ours is not null)
            foreach (var (term, count) in ours)
                merged[term] = count;
        if (theirs is not null)
            foreach (var (term, count) in theirs)
                merged[term] = merged.TryGetValue(term, out int existing) ? Math.Max(existing, count) : count;

        return merged;
    }
}
