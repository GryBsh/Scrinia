using FluentAssertions;
using Scrinia.Core.Encoding;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Regression coverage for Task 17: the no-arg <c>reconcile()</c> tool is documented as a
/// read-only "scan and report" — it must never write to disk. Before the fix, ScanForConflicts
/// called TryAutoResolveMetaJson (which does File.WriteAllText) directly during the scan for
/// any auto-resolvable .meta.json conflict, silently mutating tracked .scrinia/ files.
/// </summary>
public sealed class ReconcileScanTests
{
    private static ScriniaMcpTools Tools() => new();

    // An auto-resolvable .meta.json conflict: both sides parse as valid JSON once the
    // conflict markers are stripped (this mirrors the exact slicing TryComputeAutoResolvedMetaJson
    // performs), so the scan should classify it as auto-resolvable rather than needing manual work.
    private const string ConflictedMetaJson =
        "{\n" +
        "  \"name\": \"test\",\n" +
        "  \"updatedAt\": \"2024-01-01T00:00:00Z\",\n" +
        "<<<<<<< HEAD\n" +
        "  \"keywords\": [\"alpha\", \"common\"],\n" +
        "  \"termFrequencies\": { \"alpha\": 2, \"common\": 1 }\n" +
        "=======\n" +
        "  \"keywords\": [\"beta\", \"common\"],\n" +
        "  \"termFrequencies\": { \"beta\": 3, \"common\": 5 }\n" +
        ">>>>>>> branch-x\n" +
        "}";

    [Fact]
    public async Task Reconcile_NoArgs_DoesNotWriteAutoResolvableMetaJsonToDisk()
    {
        using var scope = new TestHelpers.StoreScope();

        string scriniaDir = Path.Combine(scope.WorkspaceDir, ".scrinia");
        string metaDir = Path.Combine(scriniaDir, "local", "topics", "topic");
        Directory.CreateDirectory(metaDir);
        string metaPath = Path.Combine(metaDir, "conflict.meta.json");
        await File.WriteAllTextAsync(metaPath, ConflictedMetaJson);

        byte[] before = await File.ReadAllBytesAsync(metaPath);

        string result = await Tools().MemoryReconcile(); // no-arg call: scan only

        byte[] after = await File.ReadAllBytesAsync(metaPath);
        after.Should().Equal(before,
            because: "the no-arg reconcile scan is documented as read-only and must not mutate " +
                      ".scrinia/ files on disk, even when a conflict is auto-resolvable");

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success",
            because: "scanning a store that only contains an auto-resolvable conflict is not an error");
        parsed.Content.Should().Contain("conflict.meta.json",
            because: "the scan report must still list the auto-resolvable conflict by relative path");
    }

    [Fact]
    public async Task Reconcile_WithConflictIdOnly_AppliesAutoMergeForMetaJson()
    {
        using var scope = new TestHelpers.StoreScope();

        string scriniaDir = Path.Combine(scope.WorkspaceDir, ".scrinia");
        string metaDir = Path.Combine(scriniaDir, "local", "topics", "topic");
        Directory.CreateDirectory(metaDir);
        string metaPath = Path.Combine(metaDir, "conflict.meta.json");
        await File.WriteAllTextAsync(metaPath, ConflictedMetaJson);

        // The explicit apply path: pass the conflictId with no choice. This is where the write
        // now happens (moved out of the scan path).
        string result = await Tools().MemoryReconcile(conflictId: "local/topics/topic/conflict.meta.json");

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success",
            because: "an explicit reconcile call for an auto-resolvable .meta.json conflict must apply the merge");

        string after = await File.ReadAllTextAsync(metaPath);
        after.Should().NotContain("<<<<<<<",
            because: "applying the auto-merge must remove conflict markers from disk");
        after.Should().Contain("\"alpha\"").And.Contain("\"beta\"").And.Contain("\"common\"",
            because: "the auto-merge unions keywords from both sides");
    }

    /// <summary>
    /// ScanForConflicts passes "current" and "incoming" to TryReadConflictCopy
    /// (MemoryTools.Lifecycle.cs:250-251). A swapped pair produces a conflict report with the two
    /// sides transposed, which reads as plausible output rather than as an error — so this
    /// distinguishes the sides by content, not by both being non-empty.
    /// </summary>
    [Fact]
    public async Task Reconcile_NoArgs_ReportsNmp2ConflictSidesUnswapped()
    {
        using var scope = new TestHelpers.StoreScope();

        string scriniaDir = Path.Combine(scope.WorkspaceDir, ".scrinia");
        string artifactDir = Path.Combine(scriniaDir, "local", "topics", "topic");
        Directory.CreateDirectory(artifactDir);

        const string fileName = "conflict.nmp2";
        string artifactPath = Path.Combine(artifactDir, fileName);

        // The raw marker bodies only need to extract successfully (TryExtractConflictSide must
        // find well-formed markers) — TryReadConflictCopy takes precedence over this fallback
        // text once the conflict/current and conflict/incoming copies below exist.
        const string conflictedContent =
            "<<<<<<< HEAD\n" +
            "ours-placeholder\n" +
            "=======\n" +
            "theirs-placeholder\n" +
            ">>>>>>> branch-x\n";
        await File.WriteAllTextAsync(artifactPath, conflictedContent);

        // The current/incoming conflict copies carry content that is distinguishable by side, so
        // a transposed report (current shown as incoming, or vice versa) is detectable from the
        // marker text itself rather than merely by both sides being present.
        string currentDir = Path.Combine(artifactDir, "conflict", "current");
        string incomingDir = Path.Combine(artifactDir, "conflict", "incoming");
        Directory.CreateDirectory(currentDir);
        Directory.CreateDirectory(incomingDir);
        await File.WriteAllTextAsync(Path.Combine(currentDir, fileName), Nmp2ChunkedEncoder.Encode("OURS-SIDE-MARKER"));
        await File.WriteAllTextAsync(Path.Combine(incomingDir, fileName), Nmp2ChunkedEncoder.Encode("THEIRS-SIDE-MARKER"));

        string result = await Tools().MemoryReconcile(); // no-arg call: scan only

        var parsed = ResponseParser.Parse(result);
        string content = parsed.Content ?? "";

        int oursLabel = content.IndexOf("OURS:", StringComparison.Ordinal);
        int theirsLabel = content.IndexOf("THEIRS:", StringComparison.Ordinal);
        oursLabel.Should().BeGreaterThan(-1,
            because: "the scan report must include an OURS section for the .nmp2 conflict");
        theirsLabel.Should().BeGreaterThan(oursLabel,
            because: "THEIRS must follow OURS in the report layout");

        string oursSection = content[oursLabel..theirsLabel];
        string theirsSection = content[theirsLabel..];

        oursSection.Should().Contain("OURS-SIDE-MARKER",
            because: "TryReadConflictCopy(filePath, \"current\") must supply the OURS section content");
        oursSection.Should().NotContain("THEIRS-SIDE-MARKER",
            because: "a swapped current/incoming pair would leak the incoming marker into the OURS section");

        theirsSection.Should().Contain("THEIRS-SIDE-MARKER",
            because: "TryReadConflictCopy(filePath, \"incoming\") must supply the THEIRS section content");
        theirsSection.Should().NotContain("OURS-SIDE-MARKER",
            because: "a swapped current/incoming pair would leak the current marker into the THEIRS section");
    }
}
