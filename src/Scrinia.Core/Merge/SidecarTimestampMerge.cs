using System.Globalization;

namespace Scrinia.Core.Merge;

/// <summary>
/// The rule for picking two sidecars' timestamps — <b>the authoritative copy</b>.
///
/// <para>The two <c>.meta.json</c> mergers had already diverged here.
/// <c>Scrinia.Merge.MetaJsonMerger</c> compared ISO strings with <c>StringComparison.Ordinal</c>
/// and kept the earliest <c>createdAt</c>; <c>reconcile()</c>'s auto-resolver parsed
/// <c>DateTimeOffset</c>, selected one whole node by <c>updatedAt</c>, and never considered
/// <c>createdAt</c> at all — so an automatic reconcile could move a memory's creation time
/// FORWARD, losing when it was actually created.</para>
///
/// <para>Ordinal comparison is independently wrong across offsets:
/// <c>2026-08-13T10:00:00+10:00</c> and <c>2026-08-13T00:00:00Z</c> are the same instant and do
/// not compare equal as strings.</para>
///
/// <para><b>One duplicate survives, and it is forced.</b> See
/// <c>Scrinia.Merge.Tests.SidecarTimestampMergeParityTests</c>.</para>
/// </summary>
public static class SidecarTimestampMerge
{
    /// <summary>
    /// Picks the merged pair: the LATEST <c>updatedAt</c> and the EARLIEST <c>createdAt</c>.
    ///
    /// <para>The asymmetry is deliberate and is the whole rule. A memory was created once, at the
    /// earlier of the two recorded moments; taking the later one would claim it did not exist
    /// during a period it demonstrably did. It was last updated at the later of the two, because
    /// both updates really happened.</para>
    ///
    /// <para><b>Unparseable values are treated as absent for comparison, and the parseable side
    /// wins.</b> When neither parses, OURS is returned unchanged — an unrecognized OURS value is
    /// never invented or reformatted. That guarantee is about OURS specifically, not the input
    /// pair as a whole: when ours is absent and theirs is unparseable, the result is OURS' absence
    /// (null), and theirs' unrecognized value is dropped rather than preserved. What always holds
    /// across all four inputs is narrower — the returned value, whenever non-null, is one of the
    /// four inputs verbatim, so a merge never silently renormalizes a format it does keep.</para>
    /// </summary>
    public static (string? CreatedAt, string? UpdatedAt) Select(
        string? oursCreated, string? oursUpdated, string? theirsCreated, string? theirsUpdated) =>
        (Pick(oursCreated, theirsCreated, earliest: true),
         Pick(oursUpdated, theirsUpdated, earliest: false));

    private static string? Pick(string? ours, string? theirs, bool earliest)
    {
        bool oursOk = TryParse(ours, out var o);
        bool theirsOk = TryParse(theirs, out var t);

        if (!oursOk && !theirsOk) return ours;
        if (!oursOk) return theirs;
        if (!theirsOk) return ours;

        return (earliest ? o <= t : o >= t) ? ours : theirs;
    }

    private static bool TryParse(string? value, out DateTimeOffset parsed)
    {
        parsed = default;
        return !string.IsNullOrWhiteSpace(value)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed);
    }
}
