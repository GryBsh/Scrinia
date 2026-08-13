namespace Scrinia.Core.Merge;

/// <summary>
/// The rule for merging two sidecars' keyword lists and their provenance — <b>the authoritative
/// copy</b>.
///
/// <para><b>Why this is a shared type and not inlined at each site.</b> Three places merge
/// <c>.meta.json</c> sidecars: this rule's callers are the git merge driver
/// (<c>Scrinia.Merge.MetaJsonMerger</c>) and <c>reconcile()</c>'s auto-resolver
/// (<c>MemoryTools.Lifecycle.TryComputeAutoResolvedMetaJson</c>), which reached the same files by
/// different routes and had drifted: the driver unioned <c>agentKeywords</c> with the keywords it
/// names, and <c>reconcile()</c> took it whole from one side — recording the other side's curated
/// keyword as auto-extracted while reporting a clean automatic merge. Both then feed
/// <see cref="MemoryDerivation"/>, so a divergence here is a ranking divergence that depends on
/// which of the two resolved the conflict.</para>
///
/// <para><b>One duplicate survives, and it is forced.</b> <c>Scrinia.Merge</c> is a standalone AOT
/// executable with zero project references by hard constraint, so it cannot call this. It carries a
/// copy that names this type as the original, and
/// <c>Scrinia.Merge.Tests.SidecarKeywordMergeParityTests</c> runs the same cases through both and
/// asserts identical output — the only thing that keeps a copy honest.</para>
/// </summary>
public static class SidecarKeywordMerge
{
    /// <summary>
    /// Unions two sidecars' keyword lists, and their <c>agentKeywords</c> provenance with them.
    ///
    /// <para><b>Keywords union</b> — case-insensitively, sorted ascending, so the merged file is
    /// stable whichever side is "ours".</para>
    ///
    /// <para><b>Provenance is UNKNOWN unless BOTH sides know it.</b> Null means "nobody recorded
    /// which keywords were curated" (<see cref="Models.ArtifactEntry.AgentKeywords"/>), and a
    /// reader treats a non-null list as the complete answer — <c>Compact</c> skips its recovery
    /// heuristic entirely when the field is present. So merging a side that recorded provenance
    /// with a side that did not must NOT produce a list: the result would positively assert that
    /// the unrecorded side's curated keywords were auto-extracted, demote them +5 → +2 on the next
    /// content rewrite, and leave no way to tell that anything had been lost. Under-claiming a SET
    /// is benign; flipping unknown to known is not, and it is exactly what the null state exists to
    /// prevent. Both-known unions; either-unknown stays unknown.</para>
    /// </summary>
    /// <returns>The merged keyword list, and the merged provenance or null for unknown.</returns>
    public static (string[] Keywords, string[]? AgentKeywords) Union(
        string[]? oursKeywords, string[]? oursAgentKeywords,
        string[]? theirsKeywords, string[]? theirsAgentKeywords)
    {
        string[] keywords = Sorted(Set(oursKeywords, theirsKeywords));

        // Deliberately not filtered to members of `keywords`: both inputs are subsets of their own
        // side's list by construction, so the union is already a subset of the union. Filtering
        // would be a second rule for the AOT copy to drift from, for no case that can occur.
        string[]? agentKeywords = oursAgentKeywords is null || theirsAgentKeywords is null
            ? null
            : Sorted(Set(oursAgentKeywords, theirsAgentKeywords));

        return (keywords, agentKeywords);
    }

    private static HashSet<string> Set(string[]? a, string[]? b)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string s in a ?? []) if (!string.IsNullOrWhiteSpace(s)) set.Add(s);
        foreach (string s in b ?? []) if (!string.IsNullOrWhiteSpace(s)) set.Add(s);
        return set;
    }

    private static string[] Sorted(HashSet<string> set) =>
        [.. set.Order(StringComparer.OrdinalIgnoreCase)];
}
