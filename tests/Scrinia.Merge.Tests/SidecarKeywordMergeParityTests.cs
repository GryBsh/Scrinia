using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Merge;
using Xunit;

namespace Scrinia.Merge.Tests;

/// <summary>
/// The keyword/provenance merge rule exists twice and must behave once.
///
/// <para><c>Scrinia.Core.Merge.SidecarKeywordMerge</c> is the original — <c>reconcile()</c>'s
/// auto-resolver calls it. <c>MetaJsonMerger</c> carries a copy because <c>Scrinia.Merge</c> is a
/// standalone AOT executable that may not reference Core. That duplication is forced; drifting is
/// not, and drift between duplicated derivation logic is the entire reason the branch this landed
/// on exists. Both mergers feed the same field into <c>MemoryDerivation</c>, so a divergence here
/// is a ranking divergence that depends on which of the two resolved a conflict — the kind of
/// difference nobody notices until a memory ranks differently on two machines.</para>
///
/// <para>Each case drives the FULL merge driver over sidecars that are identical except for the two
/// arrays, so the driver's other rules (the Jaccard gate, the three-way field merges, the timestamp
/// picks) are no-ops and the driver's output turns on this rule alone. The Jaccard threshold is
/// dropped to zero so a deliberately dissimilar case reaches the rule instead of being refused
/// before it.</para>
///
/// <para><b>What this cannot see.</b> The two sides' inputs are built SEPARATELY — Core gets typed
/// <c>string[]?</c> arrays, the driver gets hand-written JSON — so nothing structural stops them
/// drifting, and <c>reconcile()</c>, the rule's other production caller, is absent. Concretely: this
/// file calls <c>Union</c> ours-first, which matches the driver by construction rather than by
/// contract. <c>reconcile()</c> calls it BASE-first, where the base is the timestamp winner
/// (<c>MemoryTools.Lifecycle.cs:652-653</c>) — and because the union accumulates into a
/// case-insensitive <c>HashSet</c>, which keeps the first-inserted casing, the "casing differs" case
/// below resolves to <c>["Auth","JWT"]</c> through the driver and <c>["auth","jwt"]</c> through
/// reconcile whenever theirs is newer. This suite passes either way.
/// <c>Scrinia.Tests.MetaMergeSymmetricParityTests</c> drives one input through both mergers and
/// records that divergence.</para>
/// </summary>
public sealed class SidecarKeywordMergeParityTests
{
    public static TheoryData<string, string[]?, string[]?, string[]?, string[]?> Cases() => new()
    {
        // name                        oursKeywords          oursAgent         theirsKeywords            theirsAgent
        { "both known, disjoint",      ["auth", "local"],    ["local"],        ["auth", "remote"],       ["remote"] },
        { "both known, overlapping",   ["auth", "jwt"],      ["auth"],         ["auth", "jwt"],          ["jwt"] },
        { "both known, one empty",     ["auth", "jwt"],      [],              ["auth", "jwt"],          ["auth"] },
        { "both empty",                ["auth"],             [],              ["auth"],                 [] },
        { "ours unknown",              ["auth", "local"],    null,            ["auth", "remote"],       ["remote"] },
        { "theirs unknown",            ["auth", "local"],    ["local"],       ["auth", "remote"],       null },
        { "both unknown",              ["auth", "local"],    null,            ["auth", "remote"],       null },
        { "no keywords either side",   null,                 null,            null,                     null },
        { "casing differs",            ["Auth", "JWT"],      ["Auth"],        ["auth", "jwt"],          ["jwt"] },
        { "blank values",              ["auth", "  "],       ["auth", ""],    ["auth", "jwt"],          ["jwt"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void BothImplementationsAgree(
        string caseName, string[]? oursKeywords, string[]? oursAgent,
        string[]? theirsKeywords, string[]? theirsAgent)
    {
        var (expectedKeywords, expectedAgent) =
            SidecarKeywordMerge.Union(oursKeywords, oursAgent, theirsKeywords, theirsAgent);

        var (actualKeywords, actualAgent) =
            ThroughTheMergeDriver(oursKeywords, oursAgent, theirsKeywords, theirsAgent);

        actualKeywords.Should().Equal(expectedKeywords,
            because: $"[{caseName}] the copy in MetaJsonMerger must union keywords exactly as " +
                     "SidecarKeywordMerge does, including the sort");
        actualAgent.Should().Equal(expectedAgent,
            because: $"[{caseName}] and must agree about provenance — above all about when it is " +
                     "UNKNOWN, because a non-null list is read as the complete answer");
    }

    /// <summary>Runs one case through <see cref="MetaJsonMerger"/> end to end and reads the two
    /// arrays back off the merged file. Returns a null <c>AgentKeywords</c> when the key is absent,
    /// which is what the merged sidecar means by unknown.</summary>
    private static (string[] Keywords, string[]? AgentKeywords) ThroughTheMergeDriver(
        string[]? oursKeywords, string[]? oursAgent, string[]? theirsKeywords, string[]? theirsAgent)
    {
        string dir = Path.Combine(Path.GetTempPath(), "mm-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ancestor = Path.Combine(dir, "a.meta.json");
            string ours = Path.Combine(dir, "ours.meta.json");
            string theirs = Path.Combine(dir, "theirs.meta.json");

            // Identical but for the two arrays: every other field the driver merges is unchanged on
            // both sides, so its three-way rules resolve to "nobody changed it" and cannot conflict.
            File.WriteAllText(ancestor, Sidecar(null, null));
            File.WriteAllText(ours, Sidecar(oursKeywords, oursAgent));
            File.WriteAllText(theirs, Sidecar(theirsKeywords, theirsAgent));

            MetaJsonMerger.Merge(ancestor, ours, theirs, new MergeConfig(JaccardThreshold: 0.0))
                .Should().Be(MetaJsonMerger.MergeResult.Resolved,
                    because: "the Jaccard gate is disabled for these cases, so every one of them " +
                             "must reach the rule under test");

            using var doc = JsonDocument.Parse(File.ReadAllText(ours));
            return (ReadArray(doc.RootElement, "keywords") ?? [],
                    ReadArray(doc.RootElement, "agentKeywords"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Sidecar(string[]? keywords, string[]? agentKeywords)
    {
        var fields = new List<string>
        {
            "\"name\":\"m\"", "\"uri\":\"\"", "\"originalBytes\":100", "\"chunkCount\":1",
            "\"createdAt\":\"2026-01-01T00:00:00Z\"", "\"updatedAt\":\"2026-01-02T00:00:00Z\"",
        };
        if (keywords is not null) fields.Add($"\"keywords\":{JsonSerializer.Serialize(keywords)}");
        if (agentKeywords is not null) fields.Add($"\"agentKeywords\":{JsonSerializer.Serialize(agentKeywords)}");
        return "{" + string.Join(",", fields) + "}";
    }

    private static string[]? ReadArray(JsonElement root, string key) =>
        root.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray().Select(e => e.GetString() ?? "")]
            : null;
}
