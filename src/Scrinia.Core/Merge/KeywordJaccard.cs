namespace Scrinia.Core.Merge;

/// <summary>
/// Jaccard similarity over two keyword lists — <b>the authoritative copy</b>.
///
/// <para>Used as an admission gate before two sidecars are auto-merged: below a configured
/// threshold the two are treated as different memories that happen to share a filename, and the
/// conflict is surfaced rather than resolved. See <see cref="SidecarAdmission"/>.</para>
///
/// <para><b>One duplicate survives, and it is forced.</b> <c>Scrinia.Merge</c> is a standalone AOT
/// executable with zero project references by hard constraint, so it cannot call this. It carries
/// a copy that names this type as the original, and
/// <c>Scrinia.Merge.Tests.KeywordJaccardParityTests</c> runs the same cases through both.</para>
/// </summary>
public static class KeywordJaccard
{
    /// <summary>
    /// The intersection-over-union of two keyword sets, compared case-insensitively.
    ///
    /// <para>Two empty sides are <b>identical</b> (1.0), not disjoint: a pair of sidecars that
    /// carry no keywords at all agree perfectly about what they carry, and refusing to merge them
    /// would strand every keyword-free memory. Exactly one empty side is disjoint (0.0).</para>
    /// </summary>
    public static double Compute(string[]? a, string[]? b)
    {
        bool aEmpty = a is null || a.Length == 0;
        bool bEmpty = b is null || b.Length == 0;
        if (aEmpty && bEmpty) return 1.0;   // both empty = identical
        if (aEmpty || bEmpty) return 0.0;   // exactly one empty = disjoint

        var setA = new HashSet<string>(a!, StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(b!, StringComparer.OrdinalIgnoreCase);
        int intersection = setA.Count(x => setB.Contains(x));
        int union = setA.Count + setB.Count - intersection;
        return union == 0 ? 1.0 : (double)intersection / union;
    }
}
