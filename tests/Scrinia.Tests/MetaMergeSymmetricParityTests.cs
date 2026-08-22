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
            // Added after the two implementations were allowed to diverge on exactly this axis:
            // the git driver gained a version gate and an unknown-key copy while reconcile did
            // not, and all twelve existing cases stayed green because NONE of them carried a
            // differing "v" or a one-sided unmodelled key. A parity suite that cannot see the
            // axis a change moves on is not enforcing parity on that axis.
            "differing schema versions",
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\","
                  + "\"keywords\":[\"auth\"]"),
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-03T00:00:00Z\","
                  + "\"keywords\":[\"auth\"],\"v\":2")
        },
        {
            "unknown key on one side only",
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\","
                  + "\"keywords\":[\"auth\"]"),
            Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-03T00:00:00Z\","
                  + "\"keywords\":[\"auth\"],\"futureThing\":{\"a\":1}")
        },
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

    // ── The two closed divergences ────────────────────────────────────────────
    //
    // Both were verified byte-identical at merge-base bc7fdb3 on both sides, so neither belonged to
    // the branch that found them, and both sat on ONE axis: which side's object is the substrate the
    // merged fields are written onto. The driver always uses OURS ("Overlay the merged fields onto
    // OURS' full object", MetaJsonMerger.cs:104-107). reconcile used to pick a whole base node by
    // latest updatedAt, DEFAULTING TO THEIRS, because it has no ancestor by construction —
    // TryExtractMetaSides slices only <<<<<<< / ======= / >>>>>>>, never |||||||.
    //
    // RULED 2026-08-15: reconcile adopts the driver's rule. Its substrate is ours, and the fields
    // the merge does not itself decide are 3-way-merged against an absent ancestor rather than
    // carried from a winner — so a disagreement escalates instead of being guessed at.
    //
    // These two tests now assert AGREEMENT, and each additionally pins the specific outcome. Both
    // halves matter: asserting only that the two agree would pass if both regressed together.

    /// <summary>
    /// Consequence 1, closed: one conflict, one set of bytes.
    ///
    /// <para>Both mergers union keywords into a case-INSENSITIVE collection, which keeps the
    /// FIRST-inserted spelling, and the same holds for the <c>OrdinalIgnoreCase</c> term-frequency
    /// map. The driver seeds from ours (<c>MetaJsonMerger.cs:74-75</c>); reconcile used to seed from
    /// its base node — theirs, whenever theirs was newer — and now seeds from ours too. On this
    /// input the two used to write <c>["Auth","JWT"]</c>/<c>{"Auth":5}</c> and
    /// <c>["auth","jwt"]</c>/<c>{"auth":5}</c> respectively.</para>
    ///
    /// <para>Theirs is deliberately the NEWER side: that is the direction the old base selection
    /// took, so it is the direction a regression would show up in.</para>
    /// </summary>
    [Fact]
    public async Task CaseVariantKeywords_BothMergersWriteOursCasing()
    {
        string ours = Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\","
                            + "\"keywords\":[\"Auth\",\"JWT\"],\"termFrequencies\":{\"Auth\":3}");
        string theirs = Sidecar("\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-03T00:00:00Z\","
                              + "\"keywords\":[\"auth\",\"jwt\"],\"termFrequencies\":{\"auth\":5}");

        await BothProduceTheSameDocument("case-variant keywords, theirs newer", ours, theirs);

        // ...and specifically OURS' casing, on both sides. Agreement alone would also be satisfied
        // by both switching to theirs'.
        var reconcile = await ThroughReconcileAsync(ours, theirs, 0.0);
        var resolved = JsonNode.Parse(reconcile.Document!)!;
        resolved["keywords"]!.ToJsonString().Should().Be("[\"Auth\",\"JWT\"]",
            because: "the substrate is ours, so ours' spelling is the one the union keeps — the "
                   + "driver's rule, now reconcile's");
        resolved["termFrequencies"]!.ToJsonString().Should().Be("{\"Auth\":5}",
            because: "the count is still the MAX of the two sides; only the key's casing came from "
                   + "the substrate, and term frequencies are what BM25 scores against");
    }

    /// <summary>
    /// Consequence 2, closed: the driver escalates and so does reconcile.
    ///
    /// <para>The driver 3-way-merges <c>name</c>, <c>uri</c>, <c>description</c>,
    /// <c>originalBytes</c> and <c>chunkCount</c>, returning <c>Conflict</c> when both sides changed
    /// one differently — with no ancestor, that is any disagreement between two readable values
    /// (<c>MetaJsonMerger.cs:51-57</c>, <c>TryMergeField</c> at <c>:313-320</c>). reconcile used to
    /// read none of the five: they rode along on the base node, so it kept the timestamp winner's
    /// copy and reported a clean automatic merge. The standalone driver asked a human; the MCP tool
    /// guessed, and because the default base was theirs, the side it discarded was the local one.
    /// <c>originalBytes</c>/<c>chunkCount</c> describe the artifact's physical shape, so the guess
    /// could leave a sidecar describing an artifact it does not match.</para>
    ///
    /// <para>The two <c>updatedAt</c> values are equal, so the OLD behaviour took its default —
    /// theirs. A regression therefore reappears as <c>originalBytes: 30</c>, not as a coin flip.</para>
    /// </summary>
    [Fact]
    public async Task DisagreeingCarriedFields_BothMergersEscalate()
    {
        const string tail = "\"createdAt\":\"2026-01-01T00:00:00Z\","
                          + "\"updatedAt\":\"2026-02-01T00:00:00Z\",\"keywords\":[\"auth\",\"jwt\"]";
        string ours = "{\"name\":\"auth-flow\",\"uri\":\"\",\"description\":\"ours\","
                    + "\"originalBytes\":20,\"chunkCount\":2," + tail + "}";
        string theirs = "{\"name\":\"auth-flow\",\"uri\":\"\",\"description\":\"theirs\","
                      + "\"originalBytes\":30,\"chunkCount\":3," + tail + "}";

        await BothProduceTheSameDocument("disagreeing carried fields", ours, theirs);

        // ...and specifically by REFUSING. Agreement alone would also be satisfied by both
        // resolving, which is the behaviour this closed.
        ThroughTheDriver(ours, theirs, 0.0).Resolved.Should().BeFalse();
        (await ThroughReconcileAsync(ours, theirs, 0.0)).Resolved.Should().BeFalse(
            because: "with no common ancestor there is nothing to say which side changed "
                   + "originalBytes, so the merge must ask a human rather than keep one side's "
                   + "copy of the artifact's physical shape");
    }

    /// <summary>
    /// The other half of the driver's rule, which a gate alone would have got wrong: a field only
    /// ONE side sets is not a disagreement. <c>TryMergeField</c> takes the side that has it, because
    /// a side that never set the field did not change it — so reconcile must ADOPT theirs'
    /// <c>description</c> onto its own substrate, not merely keep ours' absence.
    /// </summary>
    [Fact]
    public async Task CarriedFieldOnOneSideOnly_BothMergersAdoptIt()
    {
        const string tail = "\"createdAt\":\"2026-01-01T00:00:00Z\","
                          + "\"updatedAt\":\"2026-02-01T00:00:00Z\",\"keywords\":[\"auth\",\"jwt\"]";
        string ours = "{\"name\":\"auth-flow\",\"uri\":\"\",\"originalBytes\":20,\"chunkCount\":2," + tail + "}";
        string theirs = "{\"name\":\"auth-flow\",\"uri\":\"\",\"description\":\"only theirs has this\","
                      + "\"originalBytes\":20,\"chunkCount\":2," + tail + "}";

        await BothProduceTheSameDocument("carried field on theirs only", ours, theirs);

        var reconcile = await ThroughReconcileAsync(ours, theirs, 0.0);
        reconcile.Resolved.Should().BeTrue(because: "one-sided is not a disagreement");
        JsonNode.Parse(reconcile.Document!)!["description"]!.GetValue<string>()
            .Should().Be("only theirs has this",
                because: "ours never set it, so ours did not change it — refusing here, or keeping "
                       + "ours' absence, would each be a NEW divergence from the driver");
    }

    /// <summary>
    /// The refusal has to say what it refused. Falling through to the generic "'choice' is required"
    /// reads as though the sidecar were malformed, when it parsed perfectly and the two sides simply
    /// state different facts — the same reason the dissimilarity gate has its own message.
    /// </summary>
    [Fact]
    public async Task DisagreeingCarriedFields_ReconcileNamesTheFieldsItRefusedOn()
    {
        const string tail = "\"createdAt\":\"2026-01-01T00:00:00Z\","
                          + "\"updatedAt\":\"2026-02-01T00:00:00Z\",\"keywords\":[\"auth\",\"jwt\"]";
        using var scope = new TestHelpers.StoreScope();
        string scriniaDir = Path.Combine(scope.WorkspaceDir, ".scrinia");
        Directory.CreateDirectory(scriniaDir);
        File.WriteAllText(Path.Combine(scriniaDir, "merge.config"), "{\"jaccardThreshold\":0.0}");
        string dir = Path.Combine(scriniaDir, "memories", "local");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "auth-flow.meta.json"),
            "<<<<<<< HEAD\n"
            + "{\"name\":\"auth-flow\",\"uri\":\"\",\"originalBytes\":20,\"chunkCount\":2," + tail + "}"
            + "\n=======\n"
            + "{\"name\":\"auth-flow\",\"uri\":\"\",\"originalBytes\":30,\"chunkCount\":3," + tail + "}"
            + "\n>>>>>>> theirs\n");

        string result = await new ScriniaMcpTools().MemoryReconcile(
            conflictId: "memories/local/auth-flow.meta.json");

        result.Should().Contain("originalBytes").And.Contain("chunkCount",
            because: "which field disagrees is the whole of what a caller needs in order to choose "
                   + "between 'ours' and 'theirs'");
        result.Should().NotContain("'choice' is required",
            because: "this is a deliberate refusal, not a parse failure — reporting it as the "
                   + "generic error is the defect the dissimilarity message already fixed once");
        result.Should().NotContain("too dissimilar",
            because: "the keyword sets are identical; naming the wrong gate sends the caller to "
                   + "change jaccardThreshold, which would not help");
    }
}
