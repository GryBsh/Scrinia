namespace Scrinia.Merge;

public static class KeywordJaccard
{
    public static double Compute(string[]? a, string[]? b)
    {
        if (a is null || b is null || (a.Length == 0 && b.Length == 0))
            return 1.0; // both empty = identical

        var setA = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);

        int intersection = setA.Count(x => setB.Contains(x));
        int union = setA.Count + setB.Count - intersection;

        return union == 0 ? 1.0 : (double)intersection / union;
    }
}
