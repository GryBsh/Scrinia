using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Scrinia.Mcp;
using Scrinia.Merge;

namespace Scrinia.Tests;

/// <summary>
/// The whole-document parity harness for the two <c>.meta.json</c> mergers: the standalone AOT git
/// merge driver (<see cref="MetaJsonMerger"/>) and <c>reconcile()</c>'s auto-resolver.
///
/// <para><b>Why this file exists.</b> The four suites in <c>Scrinia.Merge.Tests</c> named "parity"
/// compare one shared RULE type against the driver — <c>SidecarAdmission</c>,
/// <c>SidecarKeywordMerge</c>, <c>SidecarTermFrequencyMerge</c>, <c>SidecarTimestampMerge</c> — by
/// handing the Core side typed C# values and the driver side hand-built JSON. Two inputs that merely
/// look alike are not one input: nothing structural stops them drifting, and the SECOND production
/// caller of those rules, <c>reconcile()</c>, appears in none of them. This drives <b>one</b> pair of
/// <c>.meta.json</c> strings through <b>both</b> real mergers and compares the documents they
/// produce, which is the only construction where "the two sides saw the same input" is a property of
/// the harness rather than of the author's care.</para>
///
/// <para><b>Ancestor-less on purpose.</b> <c>reconcile()</c> reads a conflicted working-tree file
/// and slices it on <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>/<c>=======</c>/<c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>; there is no
/// <c>|||||||</c> section and no base, so it structurally cannot see an ancestor. The driver is
/// therefore given a path that does not exist, which is the shape it takes when git has no merge
/// base. Handing the driver an ancestor the other side cannot have would be the same
/// two-different-inputs defect in a new place.</para>
///
/// <para><b>Key order is not compared.</b> The driver overlays onto OURS and reconcile onto its
/// chosen base node, so the two write the same properties in different order.
/// <see cref="Canonical"/> sorts keys recursively; what is asserted is the document, not its
/// serialization.</para>
/// </summary>
public sealed class MetaMergeSymmetricParityTests
{
    /// <summary>What one merger did with the pair: whether it resolved, and the document if so.</summary>
    private sealed record MergeOutcome(bool Resolved, string? Document);

    /// <summary>Recursively key-sorted JSON, so two documents that differ only in property order
    /// compare equal and a real difference still shows up in the failure text.</summary>
    private static string Canonical(string json)
    {
        static JsonNode? Sort(JsonNode? node) => node switch
        {
            JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => KeyValuePair.Create(kv.Key, Sort(kv.Value?.DeepClone())))),
            JsonArray a => new JsonArray([.. a.Select(e => Sort(e?.DeepClone()))]),
            _ => node,
        };

        return Sort(JsonNode.Parse(json))!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Runs the pair through the git merge driver. The driver writes its result over
    /// <c>%A</c> (ours) and reports Resolved/Conflict, so the document is read back from there.</summary>
    private static MergeOutcome ThroughTheDriver(string oursJson, string theirsJson, double threshold)
    {
        string dir = Path.Combine(Path.GetTempPath(), "meta-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string oursPath = Path.Combine(dir, "ours.meta.json");
            string theirsPath = Path.Combine(dir, "theirs.meta.json");
            // Deliberately never created — see the class doc: reconcile has no ancestor, so neither
            // may the driver.
            string absentAncestor = Path.Combine(dir, "no-such-ancestor.meta.json");

            File.WriteAllText(oursPath, oursJson);
            File.WriteAllText(theirsPath, theirsJson);

            var verdict = MetaJsonMerger.Merge(absentAncestor, oursPath, theirsPath, new MergeConfig(threshold));
            return verdict == MetaJsonMerger.MergeResult.Resolved
                ? new MergeOutcome(true, File.ReadAllText(oursPath))
                : new MergeOutcome(false, null);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Runs the SAME pair through <c>reconcile()</c>, end to end: the two strings are
    /// assembled into a conflicted working-tree file, the real MCP tool resolves it, and the
    /// resolved file is read back off disk.</summary>
    private static async Task<MergeOutcome> ThroughReconcileAsync(
        string oursJson, string theirsJson, double threshold)
    {
        using var scope = new TestHelpers.StoreScope();
        string scriniaDir = Path.Combine(scope.WorkspaceDir, ".scrinia");
        Directory.CreateDirectory(scriniaDir);
        File.WriteAllText(Path.Combine(scriniaDir, "merge.config"),
            $"{{\"jaccardThreshold\":{threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");

        string dir = Path.Combine(scriniaDir, "memories", "local");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "auth-flow.meta.json");
        await File.WriteAllTextAsync(path,
            "<<<<<<< HEAD\n" + oursJson + "\n=======\n" + theirsJson + "\n>>>>>>> theirs\n");

        string conflictId = "memories/local/auth-flow.meta.json";
        string result = await new ScriniaMcpTools().MemoryReconcile(conflictId: conflictId);

        return ResponseParser.Parse(result).Status == "success"
            ? new MergeOutcome(true, await File.ReadAllTextAsync(path))
            : new MergeOutcome(false, null);
    }

    /// <summary>Asserts both mergers reached the same verdict AND, when they resolved, produced the
    /// same document.</summary>
    private static async Task BothProduceTheSameDocument(
        string caseName, string oursJson, string theirsJson, double threshold = 0.0)
    {
        var driver = ThroughTheDriver(oursJson, theirsJson, threshold);
        var reconcile = await ThroughReconcileAsync(oursJson, theirsJson, threshold);

        reconcile.Resolved.Should().Be(driver.Resolved,
            because: $"[{caseName}] one merger auto-resolving what the other escalates means the "
                   + "same conflict is answered by a human on one machine and guessed at on another");

        if (!driver.Resolved)
            return;

        Canonical(reconcile.Document!).Should().Be(Canonical(driver.Document!),
            because: $"[{caseName}] the two mergers must write the same sidecar for the same "
                   + "conflict — a difference here is two machines producing different bytes from "
                   + "one merge");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────
    //
    // Every scalar the driver 3-way-merges (name, uri, description, originalBytes, chunkCount) is
    // IDENTICAL on both sides in these fixtures, deliberately. With no ancestor the driver escalates
    // any scalar disagreement, and reconcile does not — that divergence is real, pre-existing, and
    // recorded by its own test at the bottom of this file rather than smuggled into every case here.

    private static string Sidecar(string body) =>
        "{\"name\":\"auth-flow\",\"uri\":\"\",\"description\":\"d\","
        + "\"originalBytes\":10,\"chunkCount\":1," + body + "}";

    public static TheoryData<string, string, string> Cases() => new()
    {
        {
            "disjoint keywords, both sides provenance-tagged",
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-03T00:00:00Z\","
                  + "\"keywords\":[\"auth\",\"local\"],\"agentKeywords\":[\"local\"]"),
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\","
                  + "\"keywords\":[\"auth\",\"remote\"],\"agentKeywords\":[\"remote\"]")
        },
        {
            // The mirror. Which side is NEWER decides reconcile's base node and nothing about the
            // driver, so a rule that rides on the base node shows up in exactly one direction.
            "same pair, theirs newer",
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\","
                  + "\"keywords\":[\"auth\",\"local\"],\"agentKeywords\":[\"local\"]"),
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-03T00:00:00Z\","
                  + "\"keywords\":[\"auth\",\"remote\"],\"agentKeywords\":[\"remote\"]")
        },
        {
            // createdAt takes the EARLIEST and updatedAt the LATEST, so the pair cannot both come
            // from one node — the case that catches a merger letting timestamps ride the base.
            "createdAt and updatedAt come from opposite sides",
            Sidecar("\"createdAt\":\"2026-01-05T00:00:00Z\",\"updatedAt\":\"2026-01-09T00:00:00Z\","
                  + "\"keywords\":[\"auth\"]"),
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\","
                  + "\"keywords\":[\"auth\"]")
        },
        {
            "term frequencies union with MAX on the shared key",
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-03T00:00:00Z\","
                  + "\"keywords\":[\"auth\"],\"termFrequencies\":{\"auth\":3,\"local\":1}"),
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\","
                  + "\"keywords\":[\"auth\"],\"termFrequencies\":{\"auth\":5,\"remote\":2}")
        },
        {
            // agentKeywords is UNKNOWN unless BOTH sides recorded it, so one-sided input must drop
            // the key on both mergers rather than adopt the recorded side's list.
            "agentKeywords on one side only",
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-03T00:00:00Z\","
                  + "\"keywords\":[\"auth\"],\"agentKeywords\":[\"local\"]"),
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\","
                  + "\"keywords\":[\"auth\"]")
        },
        {
            // Malformed input — the axis Task 9 aligned. Both must escalate, and this is the first
            // test that drives the identical bytes at both to prove it.
            "keywords present but not an array",
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-03T00:00:00Z\","
                  + "\"keywords\":\"urgent\""),
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\","
                  + "\"keywords\":[\"auth\"]")
        },
        {
            "keywords is an explicit JSON null",
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-03T00:00:00Z\","
                  + "\"keywords\":[\"auth\"]"),
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\","
                  + "\"keywords\":null")
        },
        {
            "keywords holds a non-string element",
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-03T00:00:00Z\","
                  + "\"keywords\":[\"auth\"]"),
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\","
                  + "\"keywords\":[\"auth\",5]")
        },
        {
            "agentKeywords is an explicit JSON null",
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-03T00:00:00Z\","
                  + "\"keywords\":[\"auth\"],\"agentKeywords\":[\"local\"]"),
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\","
                  + "\"keywords\":[\"auth\"],\"agentKeywords\":null")
        },
        {
            "termFrequencies present but not an object",
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-03T00:00:00Z\","
                  + "\"keywords\":[\"auth\"],\"termFrequencies\":7"),
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\","
                  + "\"keywords\":[\"auth\"],\"termFrequencies\":{\"auth\":1}")
        },
        {
            "updatedAt present but not a string",
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":42,"
                  + "\"keywords\":[\"auth\"]"),
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\","
                  + "\"keywords\":[\"auth\"]")
        },
        {
            // An unknown property is the driver's stated contract ("preserves every unknown property
            // verbatim") and is exactly what reconcile carries from its base node — so it is only
            // safe while the two agree on WHICH side is the substrate.
            "an unknown property both sides agree on",
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-03T00:00:00Z\","
                  + "\"keywords\":[\"auth\"],\"contentPreview\":\"same on both\""),
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\","
                  + "\"keywords\":[\"auth\"],\"contentPreview\":\"same on both\"")
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheTwoMergersAgree(string caseName, string oursJson, string theirsJson) =>
        await BothProduceTheSameDocument(caseName, oursJson, theirsJson);

    /// <summary>
    /// The admission gate at its own boundary, driven through both. Threshold and keyword sets are
    /// chosen so the score sits exactly ON it (one shared term of three → 1/3): the driver refuses
    /// below and reconcile admits at-or-above, written as exact complements, so the boundary is the
    /// value a hand-copied predicate gets wrong first.
    /// </summary>
    [Theory]
    [InlineData(0.34)]        // a hair above 1/3 — both must refuse
    [InlineData(1.0 / 3.0)]   // exactly 1/3 — both must admit
    [InlineData(0.33)]        // a hair below — both must admit
    public async Task TheTwoMergersAgreeAtTheAdmissionBoundary(double threshold) =>
        await BothProduceTheSameDocument(
            $"admission boundary at {threshold}",
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-03T00:00:00Z\","
                  + "\"keywords\":[\"auth\",\"jwt\"]"),
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\","
                  + "\"keywords\":[\"auth\",\"remote\"]"),
            threshold);

    // ── Recorded divergences: PRE-EXISTING, and deliberately not fixed here ───
    //
    // Both tests below are verified identical at merge-base bc7fdb3 on both sides — neither was
    // introduced by this branch — and both sit on ONE axis: which side's object is the substrate the
    // merged fields are written onto. The driver always uses OURS ("Overlay the merged fields onto
    // OURS' full object", MetaJsonMerger.cs:104-107). reconcile has no ancestor by construction —
    // TryExtractMetaSides slices only <<<<<<< / ======= / >>>>>>>, never |||||||  — so it cannot do
    // the driver's 3-way merge and instead picks a whole base node by latest updatedAt, DEFAULTING
    // TO THEIRS. Closing that is a design decision, not a review fix, so these record the behaviour
    // as it stands. Both will fail if it is changed; that is the point — a fix must be a deliberate
    // edit here, not a silent one.

    /// <summary>
    /// Same conflict, different bytes. Both mergers union keywords into a case-insensitive
    /// <c>HashSet</c>, which keeps the FIRST-inserted casing; the driver inserts ours first
    /// (<c>MetaJsonMerger.cs:74-75</c>) while reconcile inserts its base node first
    /// (<c>MemoryTools.Lifecycle.cs:652-653</c>) — and the base node is the timestamp winner. The
    /// same asymmetry lands on <c>termFrequencies</c>, whose union is keyed
    /// <c>OrdinalIgnoreCase</c>. Two machines resolving one conflict therefore commit different
    /// files.
    /// </summary>
    [Fact]
    public async Task CaseVariantKeywords_TheTwoMergersDisagree_PreExistingDivergence()
    {
        string ours = Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\","
                            + "\"keywords\":[\"Auth\",\"JWT\"],\"termFrequencies\":{\"Auth\":3}");
        string theirs = Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-03T00:00:00Z\","
                              + "\"keywords\":[\"auth\",\"jwt\"],\"termFrequencies\":{\"auth\":5}");

        var driver = ThroughTheDriver(ours, theirs, 0.0);
        var reconcile = await ThroughReconcileAsync(ours, theirs, 0.0);

        driver.Resolved.Should().BeTrue();
        reconcile.Resolved.Should().BeTrue(
            because: "both admit the merge — the disagreement is in what they write, not whether");

        JsonNode.Parse(driver.Document!)!["keywords"]!.ToJsonString()
            .Should().Be("[\"Auth\",\"JWT\"]",
                because: "the driver seeds the case-insensitive set from OURS, so ours' casing wins");
        JsonNode.Parse(reconcile.Document!)!["keywords"]!.ToJsonString()
            .Should().Be("[\"auth\",\"jwt\"]",
                because: "reconcile seeds it from its BASE node — theirs here, because theirs is "
                       + "newer — so theirs' casing wins on the identical input");
        JsonNode.Parse(driver.Document!)!["termFrequencies"]!.ToJsonString()
            .Should().Be("{\"Auth\":5}");
        JsonNode.Parse(reconcile.Document!)!["termFrequencies"]!.ToJsonString()
            .Should().Be("{\"auth\":5}",
                because: "the same base-node asymmetry decides the term-frequency key's casing, and "
                       + "term frequencies are what BM25 scores against");
    }

    /// <summary>
    /// The escalation divergence. The driver 3-way-merges <c>name</c>, <c>uri</c>,
    /// <c>description</c>, <c>originalBytes</c> and <c>chunkCount</c> and returns
    /// <c>Conflict</c> when both sides changed one differently — with no ancestor, that is ANY
    /// disagreement (<c>MetaJsonMerger.cs:51-57</c>, <c>TryMergeField</c> at <c>:313-320</c>).
    /// reconcile never reads those five fields at all: they ride along on the base node
    /// (<c>MemoryTools.Lifecycle.cs:611-623, 682</c>), so it silently keeps the timestamp winner's
    /// copy and reports a clean automatic merge. So the standalone driver asks a human and the MCP
    /// tool guesses — and, because the default base is THEIRS, the side it discards is the local one.
    /// <c>originalBytes</c>/<c>chunkCount</c> describe the artifact's physical shape, so the guess
    /// can leave a sidecar describing an artifact it does not match.
    /// </summary>
    [Fact]
    public async Task DisagreeingScalars_DriverEscalatesAndReconcileGuesses_PreExistingDivergence()
    {
        const string tail = "\"createdAt\":\"2026-01-01T00:00:00Z\","
                          + "\"updatedAt\":\"2026-02-01T00:00:00Z\",\"keywords\":[\"auth\",\"jwt\"]";
        string ours = "{\"name\":\"auth-flow\",\"uri\":\"\",\"description\":\"ours\","
                    + "\"originalBytes\":20,\"chunkCount\":2," + tail + "}";
        string theirs = "{\"name\":\"auth-flow\",\"uri\":\"\",\"description\":\"theirs\","
                      + "\"originalBytes\":30,\"chunkCount\":3," + tail + "}";

        ThroughTheDriver(ours, theirs, 0.0).Resolved.Should().BeFalse(
            because: "with no ancestor, both sides changing originalBytes differently is exactly "
                   + "the case TryMergeField refuses — git surfaces the conflict to a human");

        var reconcile = await ThroughReconcileAsync(ours, theirs, 0.0);
        reconcile.Resolved.Should().BeTrue(
            because: "reconcile reports a clean automatic merge for the input the driver escalates");

        var resolved = JsonNode.Parse(reconcile.Document!)!;
        resolved["originalBytes"]!.GetValue<long>().Should().Be(30);
        resolved["chunkCount"]!.GetValue<int>().Should().Be(3);
        resolved["description"]!.GetValue<string>().Should().Be("theirs",
            because: "the two updatedAt values are equal, so the base node stays at its default — "
                   + "THEIRS — and ours' description, byte count and chunk count are discarded "
                   + "without a word");
    }
}
