using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Merge;
using Xunit;

namespace Scrinia.Merge.Tests;

/// <summary>
/// The term-frequency merge rule exists twice and must behave once.
///
/// <para>Each case drives the FULL merge driver over sidecars that are identical except for the
/// termFrequencies maps, so the driver's other rules are no-ops and the driver's output turns on
/// this rule alone. The Jaccard threshold is dropped to zero so every case reaches it.</para>
///
/// <para><b>What this cannot see.</b> Core is handed a live <c>Dictionary</c> and the driver
/// hand-written JSON — two inputs, not one — and <c>reconcile()</c>, the rule's other production
/// caller, is absent. The "case-variant keys" case below is exactly where that bites: the union is
/// keyed <c>OrdinalIgnoreCase</c> and so keeps the first-inserted key, the driver unions ours-first
/// and reconcile base-first (<c>MemoryTools.Lifecycle.cs:675</c>), so the merged key is
/// <c>"Auth"</c> through one merger and <c>"auth"</c> through the other. This suite passes either
/// way. <c>Scrinia.Tests.MetaMergeSymmetricParityTests</c> drives one input through both and records
/// it.</para>
/// </summary>
public sealed class SidecarTermFrequencyMergeParityTests
{
    public static TheoryData<string, Dictionary<string, int>?, Dictionary<string, int>?> Cases() => new()
    {
        { "both null",            null,                                  null },
        { "ours only",            new() { ["auth"] = 3 },                null },
        { "theirs only",          null,                                  new() { ["auth"] = 3 } },
        { "disjoint keys",        new() { ["auth"] = 3 },                new() { ["jwt"] = 5 } },
        { "shared key, ours bigger",   new() { ["auth"] = 7 },           new() { ["auth"] = 2 } },
        { "shared key, theirs bigger", new() { ["auth"] = 2 },           new() { ["auth"] = 7 } },
        { "shared key, equal",    new() { ["auth"] = 4 },                new() { ["auth"] = 4 } },
        { "mixed",                new() { ["auth"] = 3, ["jwt"] = 1 },   new() { ["auth"] = 1, ["oidc"] = 9 } },
        { "empty maps",           new(),                                 new() },
        { "zero counts",          new() { ["auth"] = 0 },                new() { ["auth"] = 0 } },
        // Pins the comparer choice rather than merely documenting it: TextAnalysis.ComputeTermFrequencies
        // builds with OrdinalIgnoreCase and Tokenize lowercases every token, so no real producer emits
        // "Auth" and "auth" as distinct keys — but a hand-edited sidecar can, and merging that pair under
        // Ordinal would let both survive into the merged map, which MemoryDerivation.ForMetadataUpdate
        // then throws on when it copies the map into an OrdinalIgnoreCase dictionary.
        { "case-variant keys, ours upper", new() { ["Auth"] = 3 },        new() { ["auth"] = 5 } },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void BothImplementationsAgree(
        string caseName, Dictionary<string, int>? ours, Dictionary<string, int>? theirs)
    {
        var expected = SidecarTermFrequencyMerge.Union(ours, theirs);
        var actual = ThroughTheMergeDriver(ours, theirs);

        actual.Should().BeEquivalentTo(expected,
            because: $"[{caseName}] the copy in MetaJsonMerger must union term frequencies exactly " +
                     "as SidecarTermFrequencyMerge does — this field is what BM25 scores against, " +
                     "so a divergence is a ranking divergence nobody sees until two machines disagree");
    }

    /// <summary>
    /// The parity theory above reads the merged output back into a <c>Dictionary</c>, which has no
    /// order — it cannot catch a dropped <c>OrderBy(..., Ordinal)</c> in the driver. Sorted key
    /// order is what keeps a termFrequencies diff small and stable across machines instead of
    /// reordering the whole map on every merge, so it is asserted directly against the raw JSON
    /// here.
    /// </summary>
    [Fact]
    public void MergedTermFrequencies_SerializeKeysInSortedOrder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tf-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ancestor = Path.Combine(dir, "a.meta.json");
            string oursPath = Path.Combine(dir, "ours.meta.json");
            string theirsPath = Path.Combine(dir, "theirs.meta.json");

            File.WriteAllText(ancestor, Sidecar(null));
            File.WriteAllText(oursPath, Sidecar(new() { ["zebra"] = 1, ["mango"] = 2 }));
            File.WriteAllText(theirsPath, Sidecar(new() { ["apple"] = 3, ["kiwi"] = 4 }));

            MetaJsonMerger.Merge(ancestor, oursPath, theirsPath, new MergeConfig(JaccardThreshold: 0.0))
                .Should().Be(MetaJsonMerger.MergeResult.Resolved);

            using var doc = JsonDocument.Parse(File.ReadAllText(oursPath));
            string[] keys = [.. doc.RootElement.GetProperty("termFrequencies")
                .EnumerateObject().Select(p => p.Name)];

            keys.Should().Equal(keys.OrderBy(k => k, StringComparer.Ordinal),
                because: "sorted key order keeps merge output byte-stable across machines, which is " +
                         "what keeps git diffs on termFrequencies small instead of reordering the " +
                         "whole map on every merge");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A <c>termFrequencies</c> the driver cannot read ESCALATES — the same verdict
    /// <c>reconcile()</c>'s auto-resolver reaches for the same input
    /// (<c>MemoryTools.Lifecycle.TryReadIntMap</c>'s false return aborts the auto-merge), and the
    /// same verdict this driver already reaches for a malformed timestamp.
    ///
    /// <para>It used to return <c>Resolved</c>: the read was an <c>&amp;&amp;</c> guarding the
    /// merge block, so a malformed side short-circuited the WHOLE block, silently kept ours' map
    /// and dropped theirs' — resolving by guessing, on input Scrinia never produced. Both shapes
    /// are covered: an explicit JSON <c>null</c> (nothing here writes one —
    /// <c>DefaultIgnoreCondition = WhenWritingNull</c> omits the property — so a literal null is a
    /// hand-edit or a foreign writer) and a value that isn't an int.</para>
    ///
    /// <para>The verdict alone would be a weak assertion, since a refusal and a crash look alike
    /// to a caller reading only the enum; ours' file is asserted BYTE-identical afterwards, which
    /// is what "refused" has to mean for a driver whose whole side effect is rewriting that
    /// file.</para>
    /// </summary>
    [Theory]
    [InlineData("null", "an explicit JSON null is MALFORMED, not ABSENT")]
    [InlineData("{\"auth\":\"three\"}", "a value that isn't an int makes the map unreadable")]
    public void MalformedTermFrequencies_RefusesTheMerge(string theirsTermFrequencies, string why)
    {
        string dir = Path.Combine(Path.GetTempPath(), "tf-malformed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ancestor = Path.Combine(dir, "a.meta.json");
            string oursPath = Path.Combine(dir, "ours.meta.json");
            string theirsPath = Path.Combine(dir, "theirs.meta.json");

            // Sidecar() can only omit the key or write a well-formed map; the malformed shapes
            // under test need raw JSON, so both sides are written directly here.
            const string head = "{\"name\":\"m\",\"uri\":\"\",\"originalBytes\":100,\"chunkCount\":1,"
                + "\"keywords\":[\"auth\"],\"createdAt\":\"2026-01-01T00:00:00Z\","
                + "\"updatedAt\":\"2026-01-02T00:00:00Z\",\"termFrequencies\":";
            string oursJson = head + "{\"zebra\":1,\"apple\":2}}";

            File.WriteAllText(ancestor, Sidecar(null));
            File.WriteAllText(oursPath, oursJson);
            File.WriteAllText(theirsPath, head + theirsTermFrequencies + "}");

            MetaJsonMerger.Merge(ancestor, oursPath, theirsPath, new MergeConfig(JaccardThreshold: 0.0))
                .Should().Be(MetaJsonMerger.MergeResult.Conflict,
                    because: $"{why}, and refusing rather than guessing is what keeps this driver " +
                             "and reconcile()'s auto-resolver agreeing on input neither produced");

            File.ReadAllText(oursPath).Should().Be(oursJson,
                because: "a conflict must leave ours on disk untouched — git surfaces the conflict, " +
                         "and a half-merged file underneath it is the worst of both answers");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static Dictionary<string, int>? ThroughTheMergeDriver(
        Dictionary<string, int>? ours, Dictionary<string, int>? theirs)
    {
        string dir = Path.Combine(Path.GetTempPath(), "tf-parity-" + Guid.NewGuid().ToString("N"));
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
                .Should().Be(MetaJsonMerger.MergeResult.Resolved,
                    because: "the Jaccard gate is disabled for these cases, so every one of them " +
                             "must reach the rule under test");

            using var doc = JsonDocument.Parse(File.ReadAllText(oursPath));
            if (!doc.RootElement.TryGetProperty("termFrequencies", out var tf)
                || tf.ValueKind != JsonValueKind.Object)
                return null;
            return tf.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Sidecar(Dictionary<string, int>? termFrequencies)
    {
        var fields = new List<string>
        {
            "\"name\":\"m\"", "\"uri\":\"\"", "\"originalBytes\":100", "\"chunkCount\":1",
            "\"keywords\":[\"auth\"]",
            "\"createdAt\":\"2026-01-01T00:00:00Z\"", "\"updatedAt\":\"2026-01-02T00:00:00Z\"",
        };
        if (termFrequencies is not null)
            fields.Add($"\"termFrequencies\":{JsonSerializer.Serialize(termFrequencies)}");
        return "{" + string.Join(",", fields) + "}";
    }
}
