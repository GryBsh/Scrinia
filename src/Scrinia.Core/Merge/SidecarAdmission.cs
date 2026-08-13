namespace Scrinia.Core.Merge;

/// <summary>
/// Whether two conflicted sidecars are similar enough to merge automatically — <b>the
/// authoritative copy</b>.
///
/// <para>Two sidecars can collide on a filename while describing genuinely different memories.
/// Merging those produces one entry claiming both sets of keywords and both term-frequency maps,
/// which is worse than a conflict: a conflict asks a human, and a bad automatic merge does not.
/// The git merge driver has refused below a threshold since it was written;
/// <c>reconcile()</c>'s auto-resolver did not, and merged on no evidence of similarity at
/// all.</para>
/// </summary>
public static class SidecarAdmission
{
    /// <summary>
    /// True when the two keyword sets are at least <paramref name="threshold"/> similar by
    /// Jaccard, and the merge may proceed.
    ///
    /// <para>A threshold of 0 admits everything, which is how the parity tests reach the rules
    /// behind this gate. Two keyword-free sidecars score 1.0 and are admitted — see
    /// <see cref="KeywordJaccard.Compute"/> for why that is the right answer rather than a
    /// degenerate one.</para>
    /// </summary>
    public static bool IsSimilarEnough(string[]? oursKeywords, string[]? theirsKeywords, double threshold) =>
        KeywordJaccard.Compute(oursKeywords, theirsKeywords) >= threshold;
}
