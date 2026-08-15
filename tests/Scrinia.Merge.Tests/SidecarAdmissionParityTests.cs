using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Merge;
using Xunit;

namespace Scrinia.Merge.Tests;

/// <summary>
/// The admission rule exists twice and must decide once.
///
/// <para>Two conflicted sidecars can collide on a filename while describing genuinely different
/// memories; merging those produces one entry claiming both keyword sets, which is worse than a
/// conflict because a conflict asks a human. <c>Scrinia.Core.Merge.SidecarAdmission</c> is the
/// authoritative copy, called by <c>reconcile()</c>'s auto-resolver; the git merge driver states
/// the same predicate inline (<c>MetaJsonMerger.Merge</c>) because that project is a standalone
/// AOT executable with zero project references by hard constraint.</para>
///
/// <para>The two are exact COMPLEMENTS — <c>jaccard &lt; threshold</c> refuses, <c>jaccard &gt;=
/// threshold</c> admits — so the cases below deliberately include the boundary itself, which is
/// the value a hand-copied predicate gets wrong first. Every case drives the FULL merge driver
/// over sidecars that differ only in their keyword lists, so the driver's other rules are no-ops
/// and what is compared is exactly the shared rule.</para>
/// </summary>
public sealed class SidecarAdmissionParityTests
{
    public static TheoryData<string, string[]?, string[]?, double> Cases() => new()
    {
        { "identical, mid threshold",     ["auth", "jwt"],  ["auth", "jwt"],     0.7 },
        { "disjoint, mid threshold",      ["auth"],         ["remote"],          0.7 },
        { "score well below threshold",   ["auth", "jwt"],  ["auth", "remote"],  0.7 },
        { "score EXACTLY at threshold",   ["auth", "jwt"],  ["auth", "remote"],  1.0 / 3.0 },
        { "score a hair under threshold", ["auth", "jwt"],  ["auth", "remote"],  0.34 },
        { "threshold 0 admits disjoint",  ["auth"],         ["remote"],          0.0 },
        { "threshold 1 needs identity",   ["auth", "jwt"],  ["auth"],            1.0 },
        { "both keyword-free",            null,             null,                0.7 },
        { "one keyword-free",             ["auth"],         null,                0.7 },
        { "casing differs",               ["Auth"],         ["auth"],            1.0 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void BothImplementationsAgree(string caseName, string[]? ours, string[]? theirs, double threshold)
    {
        bool expected = SidecarAdmission.IsSimilarEnough(ours, theirs, threshold);
        bool admitted = ThroughTheMergeDriver(ours, theirs, threshold);

        admitted.Should().Be(expected,
            because: $"[{caseName}] the driver's inline predicate must admit exactly what " +
                     "SidecarAdmission admits — a divergence here is one merger auto-merging two " +
                     "unrelated memories into one while the other correctly asks a human");
    }

    /// <summary>True when the driver admitted the merge (Resolved), false when it refused.</summary>
    private static bool ThroughTheMergeDriver(string[]? ours, string[]? theirs, double threshold)
    {
        string dir = Path.Combine(Path.GetTempPath(), "admission-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ancestor = Path.Combine(dir, "a.meta.json");
            string oursPath = Path.Combine(dir, "ours.meta.json");
            string theirsPath = Path.Combine(dir, "theirs.meta.json");

            File.WriteAllText(ancestor, Sidecar(null));
            File.WriteAllText(oursPath, Sidecar(ours));
            File.WriteAllText(theirsPath, Sidecar(theirs));

            return MetaJsonMerger.Merge(ancestor, oursPath, theirsPath, new MergeConfig(threshold))
                == MetaJsonMerger.MergeResult.Resolved;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A sidecar identical on every field the driver's OTHER rules read, so admission is
    /// the only rule that can decide the verdict. Timestamps are equal on both sides (no
    /// SelectTimestamps divergence), termFrequencies is absent (a normal side), and the ancestor
    /// carries no keywords so the scalar 3-way merges are all "neither side changed".</summary>
    private static string Sidecar(string[]? keywords)
    {
        var fields = new List<string>
        {
            "\"name\":\"m\"", "\"uri\":\"\"", "\"originalBytes\":100", "\"chunkCount\":1",
            "\"createdAt\":\"2026-01-01T00:00:00Z\"", "\"updatedAt\":\"2026-01-02T00:00:00Z\"",
        };
        if (keywords is not null)
            fields.Add($"\"keywords\":{JsonSerializer.Serialize(keywords)}");
        return "{" + string.Join(",", fields) + "}";
    }

    /// <summary>
    /// A <c>keywords</c> value that is present but not a JSON array (here, a bare string — a
    /// hand-edited or foreign sidecar) is MALFORMED, not ABSENT. Plain <c>o.TryGetPropertyValue(key,
    /// out var n) || n is not JsonArray arr) return null</c> (the old <c>GetStrings</c>) cannot tell
    /// this apart from the key being missing, and would silently feed it to
    /// <c>KeywordJaccard.Compute</c> as an empty side instead of refusing the merge. reconcile()'s
    /// auto-resolver refuses the identical shape through
    /// <c>MemoryTools.Lifecycle.TryReadStrings</c>, so the driver must refuse too.
    /// Paired with <c>ReconcileMetaProvenanceTests.Reconcile_MalformedKeywords_NonArrayValue_EscalatesToManualResolution</c>.
    /// </summary>
    [Fact]
    public void MalformedKeywords_NonArrayValue_RefusesTheMerge()
    {
        string dir = Path.Combine(Path.GetTempPath(), "keywords-malformed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ancestor = Path.Combine(dir, "a.meta.json");
            string oursPath = Path.Combine(dir, "ours.meta.json");
            string theirsPath = Path.Combine(dir, "theirs.meta.json");

            File.WriteAllText(ancestor, Sidecar(null));
            File.WriteAllText(oursPath,
                "{\"name\":\"m\",\"uri\":\"\",\"originalBytes\":100,\"chunkCount\":1," +
                "\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\"," +
                "\"keywords\":\"urgent\"}");
            File.WriteAllText(theirsPath, Sidecar(["auth"]));

            MetaJsonMerger.Merge(ancestor, oursPath, theirsPath, new MergeConfig(JaccardThreshold: 0.0))
                .Should().Be(MetaJsonMerger.MergeResult.Conflict,
                    because: "keywords is present but not a JSON array — the driver cannot safely " +
                             "interpret one side's intent, so it must escalate rather than silently " +
                             "treat it as an empty keyword set");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// An explicit <c>"keywords": null</c> is a distinct shape from an absent key, exactly like
    /// <c>createdAt: null</c> in <c>SidecarTimestampMergeParityTests</c> — <c>TryGetPropertyValue</c>
    /// returns true for a present-but-null key, and the value is then not a <c>JsonArray</c> at all
    /// (a JSON null is a null node reference), so <c>TryGetStrings</c> reports MALFORMED for it.
    /// Paired with <c>ReconcileMetaProvenanceTests.Reconcile_MalformedKeywords_ExplicitNull_EscalatesToManualResolution</c>.
    /// </summary>
    [Fact]
    public void MalformedKeywords_ExplicitNull_RefusesTheMerge()
    {
        string dir = Path.Combine(Path.GetTempPath(), "keywords-null-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ancestor = Path.Combine(dir, "a.meta.json");
            string oursPath = Path.Combine(dir, "ours.meta.json");
            string theirsPath = Path.Combine(dir, "theirs.meta.json");

            File.WriteAllText(ancestor, Sidecar(null));
            File.WriteAllText(oursPath,
                "{\"name\":\"m\",\"uri\":\"\",\"originalBytes\":100,\"chunkCount\":1," +
                "\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\"," +
                "\"keywords\":null}");
            File.WriteAllText(theirsPath, Sidecar(["auth"]));

            MetaJsonMerger.Merge(ancestor, oursPath, theirsPath, new MergeConfig(JaccardThreshold: 0.0))
                .Should().Be(MetaJsonMerger.MergeResult.Conflict,
                    because: "keywords: null is present but not a JSON array — the driver cannot " +
                             "safely interpret one side's intent, so it must escalate rather than " +
                             "silently treat it as an empty keyword set");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
