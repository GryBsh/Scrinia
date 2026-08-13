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

    // Keywords are identical on both sides in the two fixtures below, on purpose: a later admission
    // gate that refuses a merge below a keyword-similarity threshold must not have anything to
    // object to here, since these tests are about termFrequencies, not about the keyword gate.

    private const string OursLaterNoTermFrequencies = """
        {"name":"auth-flow","uri":"","originalBytes":10,"chunkCount":1,
        "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-03T00:00:00Z",
        "keywords":["auth","local"]}
        """;

    private const string TheirsEarlierWithTermFrequencies = """
        {"name":"auth-flow","uri":"","originalBytes":10,"chunkCount":1,
        "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-02T00:00:00Z",
        "keywords":["auth","local"],"termFrequencies":{"auth":4}}
        """;

    /// <summary>
    /// The A4 defect: the old code required BOTH sides to carry a termFrequencies map before
    /// unioning anything, so a memory whose base recorded none — as ours does here, and ours wins
    /// the base slot because it is the later side — lost the other side's map whole. Term
    /// frequencies are what BM25 scores against, so this was a silent ranking loss, not a visible
    /// failure.
    /// </summary>
    [Fact]
    public async Task Reconcile_AutoMergedMetaJson_KeepsAOneSidedTermFrequencyMap()
    {
        using var scope = new TestHelpers.StoreScope();
        string conflictId = await SeedConflictAsync(
            scope, OursLaterNoTermFrequencies, TheirsEarlierWithTermFrequencies);

        string result = await Tools().MemoryReconcile(conflictId: conflictId);
        ResponseParser.Parse(result).Status.Should().Be("success");

        var resolved = await ResolvedAsync(scope, conflictId);
        resolved.TryGetProperty("termFrequencies", out var tf).Should().BeTrue(
            because: "ours (the base, since it is later) recorded no term frequencies at all; that " +
                     "must not veto theirs' contribution surviving the merge");
        tf.GetProperty("auth").GetInt32().Should().Be(4,
            because: "the one-sided map must survive whole, not vanish because the base side had none");
    }

    private const string OursValidTermFrequencies = """
        {"name":"auth-flow","uri":"","originalBytes":10,"chunkCount":1,
        "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-02T00:00:00Z",
        "keywords":["auth","local"],"termFrequencies":{"auth":3}}
        """;

    private const string TheirsMalformedTermFrequencies = """
        {"name":"auth-flow","uri":"","originalBytes":10,"chunkCount":1,
        "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-03T00:00:00Z",
        "keywords":["auth","local"],"termFrequencies":{"auth":"oops"}}
        """;

    /// <summary>
    /// A termFrequencies value that isn't a readable int (a hand-edited or foreign sidecar) used to
    /// throw inside GetValue&lt;int&gt;() and let the outer catch escalate to manual resolution. The
    /// A4 fix must not regress that into a silent "resolve minus the key it couldn't read" — it has
    /// to keep escalating, because auto-resolving something the merger cannot safely interpret is
    /// worse than asking a human.
    /// </summary>
    [Fact]
    public async Task Reconcile_MalformedTermFrequencies_EscalatesToManualResolution()
    {
        using var scope = new TestHelpers.StoreScope();
        string conflictId = await SeedConflictAsync(
            scope, OursValidTermFrequencies, TheirsMalformedTermFrequencies);

        string result = await Tools().MemoryReconcile(conflictId: conflictId);

        ResponseParser.Parse(result).Status.Should().Be("error",
            because: "a termFrequencies value that isn't a readable int means the auto-resolver " +
                     "cannot safely interpret one side's intent, so the conflict must escalate to " +
                     "manual resolution rather than silently resolving with the field dropped or " +
                     "coerced");

        string path = Path.Combine(scope.WorkspaceDir, ".scrinia",
            conflictId.Replace('/', Path.DirectorySeparatorChar));
        (await File.ReadAllTextAsync(path)).Should().Contain("<<<<<<<",
            because: "escalating must not write anything — the conflict markers stay in place for a " +
                     "human or an explicit choice to resolve");
    }

    private const string TheirsExplicitNullTermFrequencies = """
        {"name":"auth-flow","uri":"","originalBytes":10,"chunkCount":1,
        "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-03T00:00:00Z",
        "keywords":["auth","local"],"termFrequencies":null}
        """;

    private const string OursEarlyCreatedEarlyUpdated = """
        {"name":"auth-flow","uri":"","originalBytes":10,"chunkCount":1,
        "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-02T00:00:00Z",
        "keywords":["auth","local"]}
        """;

    private const string TheirsLateCreatedLateUpdated = """
        {"name":"auth-flow","uri":"","originalBytes":10,"chunkCount":1,
        "createdAt":"2026-02-01T00:00:00Z","updatedAt":"2026-03-01T00:00:00Z",
        "keywords":["auth","local"]}
        """;

    /// <summary>
    /// The A3 defect: the auto-resolver picked one node WHOLE by <c>updatedAt</c> and never
    /// considered <c>createdAt</c> at all, so resolving in favour of the newer side (theirs, here)
    /// silently moved the memory's creation time forward — from 2026-01-01 to 2026-02-01 — losing
    /// when it was actually created. The git merge driver had always kept the earliest; the two
    /// mergers disagreed. Both timestamps now come from the one shared rule
    /// (<c>Scrinia.Core.Merge.SidecarTimestampMerge.Select</c>): latest <c>updatedAt</c>, but
    /// EARLIEST <c>createdAt</c>, independently of which side otherwise wins.
    /// </summary>
    [Fact]
    public async Task Reconcile_AutoMergedMetaJson_KeepsTheEarliestCreatedAt()
    {
        using var scope = new TestHelpers.StoreScope();
        string conflictId = await SeedConflictAsync(
            scope, OursEarlyCreatedEarlyUpdated, TheirsLateCreatedLateUpdated);

        string result = await Tools().MemoryReconcile(conflictId: conflictId);
        ResponseParser.Parse(result).Status.Should().Be("success");

        var resolved = await ResolvedAsync(scope, conflictId);
        resolved.GetProperty("createdAt").GetString().Should().Be("2026-01-01T00:00:00Z",
            because: "createdAt is the earliest of the two — the memory was created once, and a " +
                     "merge that moves it forward claims it did not exist during a period it did");
        resolved.GetProperty("updatedAt").GetString().Should().Be("2026-03-01T00:00:00Z",
            because: "updatedAt is the latest of the two — theirs also wins the base-node selection " +
                     "here, so this pins that createdAt is corrected independently of that choice");
    }

    /// <summary>
    /// An explicit JSON <c>null</c> is a distinct shape from an absent key: <c>node?[key]</c>
    /// (the indexer) returns a null reference for BOTH, so a naive reader cannot tell "this side
    /// recorded no term frequencies" from "this side wrote something the merger cannot interpret."
    /// Nothing in this codebase ever serializes an explicit null here — <c>DefaultIgnoreCondition
    /// = WhenWritingNull</c> omits the property instead — so a literal <c>null</c> is a hand-edit or
    /// a foreign writer, and must be treated as MALFORMED (escalate) rather than ABSENT (silently
    /// union using only the other side), exactly like the driver already does via
    /// <c>JsonObject.TryGetPropertyValue</c>.
    /// </summary>
    [Fact]
    public async Task Reconcile_ExplicitNullTermFrequencies_EscalatesToManualResolution()
    {
        using var scope = new TestHelpers.StoreScope();
        string conflictId = await SeedConflictAsync(
            scope, OursValidTermFrequencies, TheirsExplicitNullTermFrequencies);

        string result = await Tools().MemoryReconcile(conflictId: conflictId);

        ResponseParser.Parse(result).Status.Should().Be("error",
            because: "an explicit termFrequencies: null is not the same as the key being absent — " +
                     "treating it as absent would silently union using only ours' map instead of " +
                     "escalating a shape the merger did not produce and cannot safely interpret");

        string path = Path.Combine(scope.WorkspaceDir, ".scrinia",
            conflictId.Replace('/', Path.DirectorySeparatorChar));
        (await File.ReadAllTextAsync(path)).Should().Contain("<<<<<<<",
            because: "escalating must not write anything — the conflict markers stay in place for a " +
                     "human or an explicit choice to resolve");
    }
}
