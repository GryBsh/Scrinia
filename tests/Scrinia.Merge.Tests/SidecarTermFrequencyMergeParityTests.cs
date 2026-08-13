using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Merge;
using Xunit;

namespace Scrinia.Merge.Tests;

/// <summary>
/// The term-frequency merge rule exists twice and must behave once.
///
/// <para>Each case drives the FULL merge driver over sidecars that are identical except for the
/// termFrequencies maps, so the driver's other rules are no-ops and what is compared is exactly
/// the shared rule. The Jaccard threshold is dropped to zero so every case reaches it.</para>
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
