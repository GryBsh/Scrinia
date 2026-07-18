using FluentAssertions;
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
}
