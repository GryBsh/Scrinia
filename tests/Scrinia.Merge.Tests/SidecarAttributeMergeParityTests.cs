using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Merge;
using Xunit;

namespace Scrinia.Merge.Tests;

/// <summary>
/// The attribute merge rule exists twice and must behave once.
///
/// <para>Each case drives the FULL merge driver over sidecars that are identical except for the
/// attributes map, so the driver's other rules are no-ops and its output turns on this rule alone.
/// The Jaccard threshold is dropped to zero so every case reaches it.</para>
///
/// <para><b>Why this rule exists at all.</b> Before it, <c>MetaJsonMerger</c> carried
/// <c>attributes</c> verbatim from OURS — the same hole <see cref="SidecarTermFrequencyMerge"/> was
/// written to close for term frequencies — so THEIRS' attributes were discarded on every
/// auto-resolved merge. <c>ref</c> is an attribute, so a link written on one branch vanished when
/// it merged, and this repository keeps its own <c>.scrinia/</c> in git.</para>
///
/// <para><b>What this cannot see</b>, stated for the same reason the term-frequency suite states
/// it: Core is handed a live <c>Dictionary</c> and the driver hand-written JSON — two inputs, not
/// one — and <c>reconcile()</c>'s auto-resolver, the rule's other production caller, is absent
/// here.</para>
/// </summary>
public sealed class SidecarAttributeMergeParityTests
{
    public static TheoryData<string, Dictionary<string, string[]>?, Dictionary<string, string[]>?> Cases() => new()
    {
        { "both null",             null,                                        null },
        { "ours only",             new() { ["topic"] = ["auth"] },              null },
        { "theirs only",           null,                                        new() { ["topic"] = ["auth"] } },
        { "disjoint keys",         new() { ["topic"] = ["auth"] },              new() { ["person"] = ["maria"] } },

        // The defect this rule was written for: multi-valued keys accumulate by design, so two
        // branches each adding one value are BOTH right and neither may be dropped.
        { "multi-valued union",    new() { ["topic"] = ["auth"] },              new() { ["topic"] = ["jwt"] } },
        { "ref union (link)",      new() { ["ref"] = ["/a"] },                  new() { ["ref"] = ["/b"] } },
        { "multi-valued overlap",  new() { ["topic"] = ["auth", "jwt"] },       new() { ["topic"] = ["jwt", "oidc"] } },
        { "case-variant values",   new() { ["topic"] = ["Auth"] },              new() { ["topic"] = ["auth"] } },
        { "all eight multi keys",  new() { ["person"] = ["a"], ["place"] = ["b"], ["org"] = ["c"], ["thing"] = ["d"],
                                           ["topic"] = ["e"], ["file"] = ["f"], ["ref"] = ["g"], ["mention"] = ["h"] },
                                   new() { ["person"] = ["z"], ["place"] = ["z"], ["org"] = ["z"], ["thing"] = ["z"],
                                           ["topic"] = ["z"], ["file"] = ["z"], ["ref"] = ["z"], ["mention"] = ["z"] } },

        // Scalar keys: one side only survives, and an identical value is not a disagreement.
        { "scalar, ours only",     new() { ["review"] = ["2026-01-01"] },       null },
        { "scalar, theirs only",   null,                                        new() { ["review"] = ["2026-01-01"] } },
        { "scalar, same value",    new() { ["review"] = ["2026-01-01"] },       new() { ["review"] = ["2026-01-01"] } },

        { "empty maps",            new(),                                       new() },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void BothImplementationsAgree(
        string caseName, Dictionary<string, string[]>? ours, Dictionary<string, string[]>? theirs)
    {
        SidecarAttributeMerge.TryUnion(ours, theirs, out var expected).Should().BeTrue(
            because: $"[{caseName}] is a fixture that must resolve, not escalate");

        var actual = ThroughTheMergeDriver(ours, theirs, expectResolved: true);

        actual.Should().BeEquivalentTo(expected,
            because: $"[{caseName}] the copy in MetaJsonMerger must merge attributes exactly as " +
                     "SidecarAttributeMerge does — attributes carry ref links and entity facets, so " +
                     "a divergence loses graph edges nobody notices until two machines disagree");
    }

    /// <summary>
    /// A scalar key both sides set to DIFFERENT values has no orderable answer — an attribute
    /// carries no timestamp of its own, and the sidecar's <c>updatedAt</c> says nothing about when
    /// one key changed. Both implementations must refuse rather than pick a winner, which is the
    /// same stance the driver already takes on malformed keywords and timestamps.
    /// </summary>
    [Fact]
    public void ScalarDisagreement_EscalatesInBothImplementations()
    {
        var ours = new Dictionary<string, string[]> { ["review"] = ["2026-01-01"] };
        var theirs = new Dictionary<string, string[]> { ["review"] = ["2026-06-30"] };

        SidecarAttributeMerge.TryUnion(ours, theirs, out _).Should().BeFalse(
            because: "nothing in a sidecar can order two values a human set deliberately");

        ThroughTheMergeDriver(ours, theirs, expectResolved: false);
    }

    /// <summary>
    /// The parity theory reads the merged output into a Dictionary, which has no order, so it
    /// cannot catch a dropped <c>OrderBy(..., Ordinal)</c>. Sorted key order is what keeps a merged
    /// sidecar's diff small and byte-stable across machines instead of reshuffling on every merge.
    /// </summary>
    [Fact]
    public void MergedAttributes_SerializeKeysInSortedOrder()
    {
        var ours = new Dictionary<string, string[]> { ["topic"] = ["z"], ["person"] = ["m"] };
        var theirs = new Dictionary<string, string[]> { ["file"] = ["a"], ["org"] = ["k"] };

        string dir = Path.Combine(Path.GetTempPath(), "attr-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ancestor = Path.Combine(dir, "a.meta.json");
            string oursPath = Path.Combine(dir, "ours.meta.json");
            string theirsPath = Path.Combine(dir, "theirs.meta.json");

            File.WriteAllText(ancestor, Sidecar(null));
            File.WriteAllText(oursPath, Sidecar(ours));
            File.WriteAllText(theirsPath, Sidecar(theirs));

            MetaJsonMerger.Merge(ancestor, oursPath, theirsPath, new MergeConfig(JaccardThreshold: 0.0))
                .Should().Be(MetaJsonMerger.MergeResult.Resolved);

            using var doc = JsonDocument.Parse(File.ReadAllText(oursPath));
            string[] keys = [.. doc.RootElement.GetProperty("attributes").EnumerateObject().Select(p => p.Name)];

            keys.Should().Equal(keys.OrderBy(k => k, StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static Dictionary<string, string[]>? ThroughTheMergeDriver(
        Dictionary<string, string[]>? ours, Dictionary<string, string[]>? theirs, bool expectResolved)
    {
        string dir = Path.Combine(Path.GetTempPath(), "attr-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ancestor = Path.Combine(dir, "a.meta.json");
            string oursPath = Path.Combine(dir, "ours.meta.json");
            string theirsPath = Path.Combine(dir, "theirs.meta.json");

            File.WriteAllText(ancestor, Sidecar(null));
            File.WriteAllText(oursPath, Sidecar(ours));
            File.WriteAllText(theirsPath, Sidecar(theirs));

            var result = MetaJsonMerger.Merge(ancestor, oursPath, theirsPath, new MergeConfig(JaccardThreshold: 0.0));

            if (!expectResolved)
            {
                result.Should().Be(MetaJsonMerger.MergeResult.Conflict,
                    because: "the driver must escalate a scalar disagreement rather than resolve it");
                return null;
            }

            result.Should().Be(MetaJsonMerger.MergeResult.Resolved,
                because: "the Jaccard gate is disabled for these cases, so every one must reach the rule");

            using var doc = JsonDocument.Parse(File.ReadAllText(oursPath));
            if (!doc.RootElement.TryGetProperty("attributes", out var attrs)
                || attrs.ValueKind != JsonValueKind.Object)
                return null;

            return attrs.EnumerateObject().ToDictionary(
                p => p.Name,
                p => p.Value.EnumerateArray().Select(v => v.GetString()!).ToArray(),
                StringComparer.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Sidecar(Dictionary<string, string[]>? attributes)
    {
        var fields = new List<string>
        {
            "\"name\":\"m\"", "\"uri\":\"\"", "\"originalBytes\":100", "\"chunkCount\":1",
            "\"keywords\":[\"auth\"]",
            "\"createdAt\":\"2026-01-01T00:00:00Z\"", "\"updatedAt\":\"2026-01-02T00:00:00Z\"",
        };
        if (attributes is not null)
            fields.Add($"\"attributes\":{JsonSerializer.Serialize(attributes)}");
        return "{" + string.Join(",", fields) + "}";
    }
}
