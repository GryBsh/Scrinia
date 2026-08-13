namespace Scrinia.Merge;

/// <summary>
/// DUPLICATE, AND THE DUPLICATION IS FORCED. The original is
/// <c>Scrinia.Core.Merge.KeywordJaccard</c>. This project is a standalone AOT executable with zero
/// project references by hard constraint, so it cannot call it. Change one and you must change the
/// other: <c>Scrinia.Merge.Tests.KeywordJaccardParityTests</c> runs the same cases through both and
/// fails when they disagree. The rule and its reasoning live in the Core copy.
/// </summary>
public static class KeywordJaccard
{
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
