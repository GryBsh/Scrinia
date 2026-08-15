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
/// threshold</c> admits — so the <see cref="Cases"/> theory deliberately includes the boundary
/// itself, which is the value a hand-copied predicate gets wrong first. Every theory case drives
/// the FULL merge driver over sidecars that differ only in their keyword lists, so the driver's
/// other rules are no-ops and the driver's verdict turns on this rule alone.</para>
///
/// <para><b>What this file is NOT, corrected.</b> It compares the driver's full pipeline against a
/// bare predicate, and it builds the two sides' inputs SEPARATELY —
/// <c>SidecarAdmission.IsSimilarEnough</c> is handed typed <c>string[]?</c> arrays while the driver
/// is handed hand-written JSON. Two inputs that look alike are not one input. It also omits the
/// rule's OTHER production caller, <c>reconcile()</c>'s auto-resolver, entirely. Two claims this doc
/// used to make have been removed as false: that "what is compared is exactly the shared rule" (what
/// is compared is a pipeline against a predicate), and that each standalone <c>[Fact]</c> below "is
/// paired with a reconcile-side case in <c>ReconcileMetaProvenanceTests</c> that drives the same
/// input through <c>reconcile()</c>" — the paired cases share the malformed value and nothing else:
/// different <c>name</c>, different <c>originalBytes</c>, and a counterpart side that is a
/// present array here and an absent key there.</para>
///
/// <para><b>Where whole-document parity actually lives.</b>
/// <c>Scrinia.Tests.MetaMergeSymmetricParityTests</c> drives ONE pair of <c>.meta.json</c> strings
/// through BOTH real mergers — <see cref="MetaJsonMerger.Merge"/> and <c>reconcile()</c> end to end
/// — and compares the documents they write, including the malformed-input cases below. That is the
/// suite to extend when a merge rule changes. This file is kept for what it is genuinely good at:
/// pinning the admission predicate's boundary value against the driver, cheaply and without a
/// workspace.</para>
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
    /// The reconcile-side analogue — a DIFFERENT fixture, not the same
    /// input; see this class's doc — is <c>ReconcileMetaProvenanceTests.Reconcile_MalformedKeywords_NonArrayValue_EscalatesToManualResolution</c>.
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
    ///
    /// <para>Deliberately puts the malformed value on <c>theirs</c> — the non-array-value case above
    /// puts it on <c>ours</c> — so a check that only reads one of the two sides cannot pass both
    /// tests. The reconcile-side analogue — a DIFFERENT fixture, not the same
    /// input; see this class's doc — is
    /// <c>ReconcileMetaProvenanceTests.Reconcile_MalformedKeywords_ExplicitNull_EscalatesToManualResolution</c>.
    /// </para>
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
            File.WriteAllText(oursPath, Sidecar(["auth"]));
            File.WriteAllText(theirsPath,
                "{\"name\":\"m\",\"uri\":\"\",\"originalBytes\":100,\"chunkCount\":1," +
                "\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\"," +
                "\"keywords\":null}");

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

    /// <summary>
    /// A <c>keywords</c> array containing an element that isn't a JSON string (here, a bare number —
    /// a hand-edited or foreign sidecar) is MALFORMED, not "mostly readable." Before this fix, the
    /// old <c>GetStrings</c>' inner loop (<c>el is JsonValue v &amp;&amp; v.TryGetValue&lt;string&gt;
    /// (out var s)</c>, with no <c>else</c>) silently skipped the bad element and kept the rest —
    /// dropping one keyword out of the array with no signal, and merging on the reduced set — while
    /// reconcile()'s auto-resolver happened to throw on the same input and get caught by a broad
    /// outer catch, so the two mergers reached the same escalated outcome by accident rather than by
    /// a shared rule. Both now refuse explicitly.
    /// The reconcile-side analogue — a DIFFERENT fixture, not the same
    /// input; see this class's doc — is <c>ReconcileMetaProvenanceTests.Reconcile_MalformedKeywords_NonStringElement_EscalatesToManualResolution</c>.
    /// </summary>
    [Fact]
    public void MalformedKeywords_NonStringElement_RefusesTheMerge()
    {
        string dir = Path.Combine(Path.GetTempPath(), "keywords-nonstring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ancestor = Path.Combine(dir, "a.meta.json");
            string oursPath = Path.Combine(dir, "ours.meta.json");
            string theirsPath = Path.Combine(dir, "theirs.meta.json");

            File.WriteAllText(ancestor, Sidecar(null));
            File.WriteAllText(oursPath, Sidecar(["auth"]));
            File.WriteAllText(theirsPath,
                "{\"name\":\"m\",\"uri\":\"\",\"originalBytes\":100,\"chunkCount\":1," +
                "\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\"," +
                "\"keywords\":[\"auth\",5]}");

            MetaJsonMerger.Merge(ancestor, oursPath, theirsPath, new MergeConfig(JaccardThreshold: 0.0))
                .Should().Be(MetaJsonMerger.MergeResult.Conflict,
                    because: "a keywords array containing a non-string element means the driver " +
                             "cannot safely interpret one side's intent, so it must escalate rather " +
                             "than silently drop the bad element and merge the rest");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// <c>agentKeywords</c> is the field where getting ABSENT vs MALFORMED wrong matters most: absent
    /// means UNKNOWN provenance (a normal side of the union), so a malformed value silently read as
    /// absent would be misclassified as UNKNOWN instead of escalating. The admission gate only reads
    /// <c>keywords</c>, so both sides here carry an identical, valid <c>keywords</c> array to reach
    /// the union code where <c>agentKeywords</c> is actually read.
    /// The reconcile-side analogue — a DIFFERENT fixture, not the same
    /// input; see this class's doc — is <c>ReconcileMetaProvenanceTests.Reconcile_MalformedAgentKeywords_ExplicitNull_EscalatesToManualResolution</c>.
    /// </summary>
    [Fact]
    public void MalformedAgentKeywords_ExplicitNull_RefusesTheMerge()
    {
        string dir = Path.Combine(Path.GetTempPath(), "agentkw-null-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ancestor = Path.Combine(dir, "a.meta.json");
            string oursPath = Path.Combine(dir, "ours.meta.json");
            string theirsPath = Path.Combine(dir, "theirs.meta.json");

            File.WriteAllText(ancestor, Sidecar(["auth"]));
            File.WriteAllText(oursPath, Sidecar(["auth"]));
            File.WriteAllText(theirsPath,
                "{\"name\":\"m\",\"uri\":\"\",\"originalBytes\":100,\"chunkCount\":1," +
                "\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\"," +
                "\"keywords\":[\"auth\"],\"agentKeywords\":null}");

            MetaJsonMerger.Merge(ancestor, oursPath, theirsPath, new MergeConfig(JaccardThreshold: 0.0))
                .Should().Be(MetaJsonMerger.MergeResult.Conflict,
                    because: "agentKeywords: null is present but not a JSON array — the driver cannot " +
                             "safely interpret one side's intent, so it must escalate rather than " +
                             "misrecord it as UNKNOWN provenance");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>The non-array-value twin of the explicit-null case above, for the same field.
    /// The reconcile-side analogue — a DIFFERENT fixture, not the same
    /// input; see this class's doc — is <c>ReconcileMetaProvenanceTests.Reconcile_MalformedAgentKeywords_NonArrayValue_EscalatesToManualResolution</c>.
    /// </summary>
    [Fact]
    public void MalformedAgentKeywords_NonArrayValue_RefusesTheMerge()
    {
        string dir = Path.Combine(Path.GetTempPath(), "agentkw-nonarray-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ancestor = Path.Combine(dir, "a.meta.json");
            string oursPath = Path.Combine(dir, "ours.meta.json");
            string theirsPath = Path.Combine(dir, "theirs.meta.json");

            File.WriteAllText(ancestor, Sidecar(["auth"]));
            File.WriteAllText(oursPath, Sidecar(["auth"]));
            File.WriteAllText(theirsPath,
                "{\"name\":\"m\",\"uri\":\"\",\"originalBytes\":100,\"chunkCount\":1," +
                "\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\"," +
                "\"keywords\":[\"auth\"],\"agentKeywords\":\"urgent\"}");

            MetaJsonMerger.Merge(ancestor, oursPath, theirsPath, new MergeConfig(JaccardThreshold: 0.0))
                .Should().Be(MetaJsonMerger.MergeResult.Conflict,
                    because: "an agentKeywords value that isn't a readable JSON array means the " +
                             "driver cannot safely interpret one side's intent, so it must escalate " +
                             "rather than misrecord it as UNKNOWN provenance");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
