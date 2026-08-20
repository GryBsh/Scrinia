namespace Scrinia.Merge;

/// <summary>
/// DUPLICATE, AND THE DUPLICATION IS FORCED. The authoritative copy is
/// <c>Scrinia.Core.Merge.SidecarAttributeMerge</c>; this project is a standalone AOT executable
/// with zero project references by hard constraint, so it cannot call it.
/// <para>Change one and you must change the other.
/// <c>Scrinia.Merge.Tests.SidecarAttributeMergeParityTests</c> runs the same cases through both
/// and fails when they disagree — that test is the only thing keeping this honest.</para>
/// <para>The rule and the reasoning behind it live in the original. In summary: the eight
/// multi-valued keys union; a scalar key on one side only survives; a scalar key both sides set
/// differently escalates rather than being guessed at, because nothing here can tell which value
/// is newer.</para>
/// </summary>
internal static class SidecarAttributeMerge
{
    private static readonly HashSet<string> MultiValued =
        new(StringComparer.OrdinalIgnoreCase) { "person", "place", "org", "thing", "topic", "file", "ref", "mention" };

    /// <returns><c>false</c> when the sides disagree on a scalar key — the caller must escalate.</returns>
    internal static bool TryUnion(
        IReadOnlyDictionary<string, string[]>? ours,
        IReadOnlyDictionary<string, string[]>? theirs,
        out Dictionary<string, string[]>? merged)
    {
        merged = null;
        if (ours is null && theirs is null) return true;

        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (ours is not null)
            foreach (var (key, values) in ours)
                result[key] = values;

        if (theirs is not null)
        {
            foreach (var (key, theirValues) in theirs)
            {
                if (!result.TryGetValue(key, out var ourValues))
                {
                    result[key] = theirValues;
                    continue;
                }

                if (MultiValued.Contains(key))
                {
                    var union = new List<string>(ourValues);
                    foreach (string v in theirValues)
                        if (!union.Contains(v, StringComparer.OrdinalIgnoreCase))
                            union.Add(v);
                    result[key] = [.. union];
                    continue;
                }

                if (!ourValues.SequenceEqual(theirValues, StringComparer.OrdinalIgnoreCase))
                    return false;
            }
        }

        merged = result.Count == 0 ? null : result;
        return true;
    }
}
