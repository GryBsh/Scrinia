namespace Scrinia.Core.Merge;

/// <summary>
/// The rule for merging two sidecars' <c>attributes</c> maps — <b>the authoritative copy</b>.
///
/// <para>Both <c>.meta.json</c> mergers had the same hole here that
/// <see cref="SidecarTermFrequencyMerge"/> was written to close for term frequencies:
/// <c>Scrinia.Merge.MetaJsonMerger</c> overlays merged fields onto OURS' object and carries every
/// unhandled property verbatim, so THEIRS' attributes were discarded on every auto-resolved git
/// merge; <c>reconcile()</c>'s auto-resolver merged timestamps, keywords and term frequencies and
/// never looked at attributes at all. The loss is silent and it is not cosmetic — <c>ref</c> is an
/// attribute, so a link written on one branch simply disappeared when that branch merged, and this
/// repository keeps its own <c>.scrinia/</c> in git.</para>
///
/// <para><b>One duplicate survives, and it is forced.</b> <c>Scrinia.Merge</c> is a standalone AOT
/// executable with zero project references by hard constraint. Its copy names this type as the
/// original, and <c>Scrinia.Merge.Tests.SidecarAttributeMergeParityTests</c> runs the same cases
/// through both.</para>
/// </summary>
public static class SidecarAttributeMerge
{
    /// <summary>
    /// The keys that accumulate rather than replace. Mirrors
    /// <c>Scrinia.Core.Search.KnownAttributes.IsMultiValued</c>, which is the schema point and
    /// stays the schema point — this rule does not get to invent vocabulary. Restated as a literal
    /// set rather than a call so the forced duplicate in <c>Scrinia.Merge</c> can hold the same
    /// eight strings without dragging the search assembly into an AOT binary.
    /// </summary>
    private static readonly HashSet<string> MultiValued =
        new(StringComparer.OrdinalIgnoreCase) { "person", "place", "org", "thing", "topic", "file", "ref", "mention" };

    /// <summary>
    /// Merges two attribute maps.
    ///
    /// <para><b>Multi-valued keys union.</b> Entity keys, references and mention spans accumulate
    /// across appends by design, so two branches each adding one are both right.</para>
    ///
    /// <para><b>A scalar key present on one side survives.</b> Absent means "this side recorded
    /// nothing", not "this side deleted it".</para>
    ///
    /// <para><b>A scalar key both sides set DIFFERENTLY escalates.</b> There is no timestamp on an
    /// attribute, so nothing here can tell which value is newer — only which sidecar is, and a
    /// sidecar's <c>updatedAt</c> says nothing about when one of its keys changed. Guessing would
    /// silently pick a winner for a field a human deliberately set, which is the shape of failure
    /// the rest of this merger refuses: a conflict asks a human, an automatic merge does not.</para>
    /// </summary>
    /// <param name="merged">The merged map, or null when neither side carried attributes.</param>
    /// <returns><c>false</c> when the sides disagree on a scalar key — the caller must escalate.</returns>
    public static bool TryUnion(
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

                // Scalar. Equal is not a disagreement; differing is, and it is not ours to settle.
                if (!ourValues.SequenceEqual(theirValues, StringComparer.OrdinalIgnoreCase))
                    return false;
            }
        }

        merged = result.Count == 0 ? null : result;
        return true;
    }
}
