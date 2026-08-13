using System.Text.Json;
using FluentAssertions;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// <c>reconcile()</c> auto-merges a conflicted <c>.meta.json</c> when the caller supplies no
/// explicit choice, and that makes it the SECOND caller of the keyword/provenance merge rule — the
/// git merge driver is the other. It had its own hand-rolled copy: it picked a base side by latest
/// <c>updatedAt</c>, unioned <c>keywords</c> into it, and wrote the base — so <c>agentKeywords</c>
/// came whole from whichever side won the timestamp, recording the other side's curated keywords as
/// auto-extracted while the tool reported a clean automatic merge. The next content rewrite then
/// demoted them +5 → +2 with nothing to recover from.
///
/// <para>It calls <c>Scrinia.Core.Merge.SidecarKeywordMerge</c> now. The rule's own behaviour is
/// specified there and cross-checked against the driver's forced AOT copy by
/// <c>Scrinia.Merge.Tests.SidecarKeywordMergeParityTests</c>; what these tests pin is that this
/// tool reaches the rule at all, end to end, rather than keeping a third opinion.</para>
/// </summary>
public sealed class ReconcileMetaProvenanceTests
{
    private static ScriniaMcpTools Tools() => new();

    /// <summary>Writes a conflicted sidecar into the store and returns its conflict id.</summary>
    private static async Task<string> SeedConflictAsync(
        TestHelpers.StoreScope scope, string oursJson, string theirsJson)
    {
        string dir = Path.Combine(scope.WorkspaceDir, ".scrinia", "memories", "local");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "auth-flow.meta.json"),
            "<<<<<<< HEAD\n" + oursJson + "\n=======\n" + theirsJson + "\n>>>>>>> theirs\n");
        return "memories/local/auth-flow.meta.json";
    }

    private static async Task<JsonElement> ResolvedAsync(TestHelpers.StoreScope scope, string conflictId)
    {
        string path = Path.Combine(scope.WorkspaceDir, ".scrinia",
            conflictId.Replace('/', Path.DirectorySeparatorChar));
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        return doc.RootElement.Clone();
    }

    private static string[]? Array(JsonElement root, string key) =>
        root.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray().Select(e => e.GetString() ?? "")]
            : null;

    private const string OursLater = """
        {"name":"auth-flow","uri":"","originalBytes":10,"chunkCount":1,
        "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-03T00:00:00Z",
        "keywords":["auth","local"],"agentKeywords":["local"]}
        """;

    private const string TheirsEarlier = """
        {"name":"auth-flow","uri":"","originalBytes":10,"chunkCount":1,
        "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-02T00:00:00Z",
        "keywords":["auth","remote"],"agentKeywords":["remote"]}
        """;

    [Fact]
    public async Task Reconcile_AutoMergedMetaJson_UnionsProvenanceWithTheKeywordsItNames()
    {
        using var scope = new TestHelpers.StoreScope();
        string conflictId = await SeedConflictAsync(scope, OursLater, TheirsEarlier);

        string result = await Tools().MemoryReconcile(conflictId: conflictId);
        ResponseParser.Parse(result).Status.Should().Be("success");

        var resolved = await ResolvedAsync(scope, conflictId);
        Array(resolved, "keywords").Should().Equal(["auth", "local", "remote"]);
        Array(resolved, "agentKeywords").Should().Equal(["local", "remote"],
            because: "the union pulled both sides' curated keywords into the list, so taking the " +
                     "provenance from the timestamp winner alone would record the loser's as " +
                     "auto-extracted — while reporting a clean automatic merge");
    }

    /// <summary>The mirror: the side that wins the timestamp is the one whose provenance would have
    /// survived, so the defect is invisible unless both directions are checked.</summary>
    [Fact]
    public async Task Reconcile_AutoMergedMetaJson_UnionsProvenanceWhicheverSideIsNewer()
    {
        using var scope = new TestHelpers.StoreScope();
        string conflictId = await SeedConflictAsync(scope, TheirsEarlier, OursLater);

        string result = await Tools().MemoryReconcile(conflictId: conflictId);
        ResponseParser.Parse(result).Status.Should().Be("success");

        Array(await ResolvedAsync(scope, conflictId), "agentKeywords")
            .Should().Equal(["local", "remote"]);
    }

    /// <summary>
    /// One side pre-provenance: the merged sidecar must come out UNKNOWN, not "only what the
    /// recorded side knew". A non-null list is read as the complete answer — <c>Compact</c> skips
    /// its recovery heuristic entirely when the field is present — so keeping the known side's list
    /// would positively assert that the other side's curated keywords were auto-extracted, and
    /// demote them permanently. Under-claiming a set is benign; flipping unknown to known is not.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reconcile_AutoMergedMetaJson_StaysUnknownWhenOneSideNeverRecordedProvenance(
        bool unrecordedSideIsOurs)
    {
        using var scope = new TestHelpers.StoreScope();

        const string unrecorded = """
            {"name":"auth-flow","uri":"","originalBytes":10,"chunkCount":1,
            "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-04T00:00:00Z",
            "keywords":["auth","portcullis"]}
            """;
        const string recorded = """
            {"name":"auth-flow","uri":"","originalBytes":10,"chunkCount":1,
            "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-02T00:00:00Z",
            "keywords":["auth","gatehouse"],"agentKeywords":["gatehouse"]}
            """;

        string conflictId = unrecordedSideIsOurs
            ? await SeedConflictAsync(scope, unrecorded, recorded)
            : await SeedConflictAsync(scope, recorded, unrecorded);

        string result = await Tools().MemoryReconcile(conflictId: conflictId);
        ResponseParser.Parse(result).Status.Should().Be("success");

        var resolved = await ResolvedAsync(scope, conflictId);
        Array(resolved, "keywords").Should().Contain("portcullis",
            because: "precondition: the unrecorded side's curated keyword did survive the union, " +
                     "so there is something for a wrong provenance to mislabel");
        Array(resolved, "agentKeywords").Should().BeNull(
            because: "the merged entry does not know which of its keywords were curated, and " +
                     "saying it does is unrecoverable — the recovery heuristic only runs for null");
    }
}
