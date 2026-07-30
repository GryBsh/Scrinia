namespace Scrinia.Merge;

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
