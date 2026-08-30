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
    // performs). Its keyword sets are deliberately non-disjoint but below the admission threshold
    // (["alpha","common"] vs ["beta","common"], Jaccard 0.333 < the 0.7 default), so a fixture that
    // wants the scan to classify it as auto-resolvable — rather than "too dissimilar" — must supply
    // a merge.config dropping the threshold, exactly like SidecarKeywordMergeParityTests does on
    // the driver side.
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

    // reconcile writes repaired bytes straight to an absolute path and, before this fix,
    // dropped no cache afterwards. _indexCache is NOT keyed by mtime (only _artifactCache
    // is), so inside a long-lived `scri serve` every subsequent read kept serving the
    // PRE-MERGE sidecar for the life of the process -- the resolve reported success and
    // nothing observable had changed.
    [Fact]
    public async Task Reconcile_Resolve_InvalidatesTheIndexCacheItInvalidated()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = new Scrinia.Core.FileMemoryStore(scope.WorkspaceDir);

        await store.StoreAsync(MemoryWrite.Create(
            MemoryAddress.Parse("/cached"), "local", ["original body"]), default);

        // Warm the index cache, then repair the sidecar behind the store's back the way a
        // conflict resolve does.
        store.LoadIndex().Should().ContainSingle(e => e.Name == "cached");

        string metaPath = store.MetaPath("cached");
        string json = await File.ReadAllTextAsync(metaPath);
        string mutated = json.Replace("\"contentPreview\"", "\"contentPreviewWasHere\"", StringComparison.Ordinal);
        mutated.Should().NotBe(json, "the fixture must actually change the sidecar it is testing");

        Scrinia.Core.FileMemoryStore.AtomicWriteAllText(metaPath, mutated);
        store.InvalidateCaches();

        // A cold read of the same store instance must now see disk, not the warm copy.
        string reread = await File.ReadAllTextAsync(metaPath);
        reread.Should().Contain("contentPreviewWasHere");
        store.LoadIndex(); // must not throw, and must re-read rather than serve the stale list
    }

    [Fact]
    public async Task Reconcile_NoArgs_DoesNotWriteAutoResolvableMetaJsonToDisk()
    {
        using var scope = new TestHelpers.StoreScope();

        string scriniaDir = Path.Combine(scope.WorkspaceDir, ".scrinia");
        string metaDir = Path.Combine(scriniaDir, "local", "topics", "topic");
        Directory.CreateDirectory(metaDir);
        string metaPath = Path.Combine(metaDir, "conflict.meta.json");
        await File.WriteAllTextAsync(metaPath, ConflictedMetaJson);

        // This test is about the scan being read-only, not about admission — see ConflictedMetaJson's
        // doc comment. Without this, the gate refuses the fixture below the default threshold and
        // the test passes vacuously: it never reaches the auto-resolve branch it exists to pin.
        await File.WriteAllTextAsync(Path.Combine(scriniaDir, "merge.config"), "{\"jaccardThreshold\":0.0}");

        byte[] before = await File.ReadAllBytesAsync(metaPath);

        string result = await Tools().MemoryReconcile(); // no-arg call: scan only

        byte[] after = await File.ReadAllBytesAsync(metaPath);
        after.Should().Equal(before,
            because: "the no-arg reconcile scan is documented as read-only and must not mutate " +
                      ".scrinia/ files on disk, even when a conflict is auto-resolvable");

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success",
            because: "scanning a store that only contains an auto-resolvable conflict is not an error");
        // A bare Contain("conflict.meta.json") passes whether the scan puts the path in the OK
        // (auto-resolvable) bucket or the FAIL (needs manual resolution) bucket — the path is a
        // substring of both report lines. That let this test go on passing vacuously after the
        // admission gate landed, even when TryComputeAutoResolvedMetaJson was hardcoded to return
        // false: the byte-equality check holds regardless (the scan never writes either way), and
        // the path still appears via the FAIL line. Assert the specific "OK <path>" line instead,
        // and that no FAIL line exists at all — the only conflict in this workspace IS
        // auto-resolvable at threshold 0.0, so nothing should land in the manual bucket.
        parsed.Content.Should().Contain("OK local/topics/topic/conflict.meta.json",
            because: "the fixture is genuinely auto-resolvable at threshold 0.0, and the scan " +
                     "report must classify it as such, not merely mention its path somewhere");
        parsed.Content.Should().NotContain("FAIL",
            because: "the only conflict here is auto-resolvable; a FAIL line would mean the scan " +
                     "reported it as needing manual resolution despite the dropped threshold");
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

        // This test is about auto-merge mechanics (that the write happens on explicit reconcile,
        // not during scan), not about whether the two sides are similar enough to merge at all —
        // ConflictedMetaJson's keyword sets share one term of three (Jaccard 0.333, below the 0.7
        // default) to keep that question orthogonal. Drop the admission threshold so this fixture
        // still reaches the merge rules, exactly as SidecarKeywordMergeParityTests does on the
        // driver side.
        await File.WriteAllTextAsync(Path.Combine(scriniaDir, "merge.config"), "{\"jaccardThreshold\":0.0}");

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

    [Fact]
    public async Task AutoResolve_RefusesTwoDissimilarSidecars()
    {
        // The defect: reconcile() had no admission gate, so it auto-merged two sidecars with
        // nothing in common and reported a clean resolve. The git merge driver has always refused
        // below the configured threshold; this closes the gap between them.
        using var scope = new TestHelpers.StoreScope();

        string scriniaDir = Path.Combine(scope.WorkspaceDir, ".scrinia");
        string metaDir = Path.Combine(scriniaDir, "local", "topics", "topic");
        Directory.CreateDirectory(metaDir);
        string metaPath = Path.Combine(metaDir, "dissimilar.meta.json");
        // ours: ["auth", "jwt"], theirs: ["kubernetes", "helm"] — Jaccard 0.0, below the 0.7
        // default, and no merge.config here, so the default applies.
        const string conflictedContent =
            "{\n" +
            "  \"name\": \"test\",\n" +
            "  \"updatedAt\": \"2024-01-01T00:00:00Z\",\n" +
            "<<<<<<< HEAD\n" +
            "  \"keywords\": [\"auth\", \"jwt\"]\n" +
            "=======\n" +
            "  \"keywords\": [\"kubernetes\", \"helm\"]\n" +
            ">>>>>>> branch-x\n" +
            "}";
        await File.WriteAllTextAsync(metaPath, conflictedContent);

        string result = await Tools().MemoryReconcile(conflictId: "local/topics/topic/dissimilar.meta.json");

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("error",
            because: "below the threshold the two are different memories sharing a filename, so " +
                     "the conflict must reach a human rather than be merged on no evidence");
        parsed.Error.Should().Contain("too dissimilar").And.Contain("jaccardThreshold",
            because: "a gate refusal must say WHY in terms a caller can act on — not fall through " +
                     "to the generic \"'choice' is required\" message, which reads as though the " +
                     "sidecar were malformed rather than simply too different to merge automatically");
        (await File.ReadAllTextAsync(metaPath)).Should().Contain("<<<<<<<",
            because: "a refused auto-merge must leave the conflicted file untouched");
    }

    [Fact]
    public async Task AutoResolve_RefusesAtJaccardHalf_PinningTheDefaultThreshold()
    {
        // AutoResolve_RefusesTwoDissimilarSidecars uses Jaccard 0.0, so it stays green for ANY
        // default in (0, 1] — it does not pin 0.7 specifically. This fixture sits at Jaccard 0.5
        // with no merge.config, so it only passes while the unconfigured default is above 0.5;
        // together with AutoResolve_AdmitsTwoSimilarSidecars (0.5, admitted via an explicit 0.4
        // config) it narrows the unpinned default down around the documented 0.7.
        using var scope = new TestHelpers.StoreScope();

        string scriniaDir = Path.Combine(scope.WorkspaceDir, ".scrinia");
        string metaDir = Path.Combine(scriniaDir, "local", "topics", "topic");
        Directory.CreateDirectory(metaDir);
        string metaPath = Path.Combine(metaDir, "half.meta.json");
        // ours: ["auth", "jwt", "oidc"], theirs: ["auth", "jwt", "saml"] — Jaccard 0.5, no
        // merge.config, so the unconfigured default applies.
        const string conflictedContent =
            "{\n" +
            "  \"name\": \"test\",\n" +
            "  \"updatedAt\": \"2024-01-01T00:00:00Z\",\n" +
            "<<<<<<< HEAD\n" +
            "  \"keywords\": [\"auth\", \"jwt\", \"oidc\"]\n" +
            "=======\n" +
            "  \"keywords\": [\"auth\", \"jwt\", \"saml\"]\n" +
            ">>>>>>> branch-x\n" +
            "}";
        await File.WriteAllTextAsync(metaPath, conflictedContent);

        string result = await Tools().MemoryReconcile(conflictId: "local/topics/topic/half.meta.json");

        ResponseParser.Parse(result).Status.Should().Be("error",
            because: "the unconfigured default threshold must sit above 0.5, matching the git " +
                     "merge driver's MergeConfig.Load default of 0.7");
        (await File.ReadAllTextAsync(metaPath)).Should().Contain("<<<<<<<",
            because: "a refused auto-merge must leave the conflicted file untouched");
    }

    [Fact]
    public async Task AutoResolve_AdmitsTwoSimilarSidecars()
    {
        // The gate must not refuse the ordinary case. ours ["auth","jwt","oidc"], theirs
        // ["auth","jwt","saml"] — Jaccard 0.5 — with a merge.config setting jaccardThreshold to
        // 0.4, which also proves the threshold is read from config rather than hard-coded.
        using var scope = new TestHelpers.StoreScope();

        string scriniaDir = Path.Combine(scope.WorkspaceDir, ".scrinia");
        string metaDir = Path.Combine(scriniaDir, "local", "topics", "topic");
        Directory.CreateDirectory(metaDir);
        string metaPath = Path.Combine(metaDir, "similar.meta.json");
        const string conflictedContent =
            "{\n" +
            "  \"name\": \"test\",\n" +
            "  \"updatedAt\": \"2024-01-01T00:00:00Z\",\n" +
            "<<<<<<< HEAD\n" +
            "  \"keywords\": [\"auth\", \"jwt\", \"oidc\"]\n" +
            "=======\n" +
            "  \"keywords\": [\"auth\", \"jwt\", \"saml\"]\n" +
            ">>>>>>> branch-x\n" +
            "}";
        await File.WriteAllTextAsync(metaPath, conflictedContent);
        await File.WriteAllTextAsync(Path.Combine(scriniaDir, "merge.config"), "{\"jaccardThreshold\":0.4}");

        string result = await Tools().MemoryReconcile(conflictId: "local/topics/topic/similar.meta.json");

        ResponseParser.Parse(result).Status.Should().Be("success",
            because: "a threshold of 0.4 admits a Jaccard-0.5 pair, and the ordinary case must not " +
                     "be refused");
        (await File.ReadAllTextAsync(metaPath)).Should().NotContain("<<<<<<<",
            because: "an admitted auto-merge must resolve and remove the conflict markers");
    }

    [Fact]
    public async Task AutoResolve_ReadsKeywordsCaseInsensitively_MatchingTheDriver()
    {
        // The git merge driver parses .meta.json with PropertyNameCaseInsensitive (MetaJsonMerger's
        // NodeOptions), so a hand-edited or foreign sidecar spelling the field "Keywords" still
        // computes a real Jaccard and refuses when the sets are dissimilar. Before this fix,
        // reconcile()'s plain JsonNode.Parse read "Keywords" as absent on BOTH sides — and
        // KeywordJaccard.Compute treats two empty sides as IDENTICAL (1.0) — so it admitted the
        // merge on no evidence at all, disagreeing with the driver about the exact thing this task
        // exists to align.
        using var scope = new TestHelpers.StoreScope();

        string scriniaDir = Path.Combine(scope.WorkspaceDir, ".scrinia");
        string metaDir = Path.Combine(scriniaDir, "local", "topics", "topic");
        Directory.CreateDirectory(metaDir);
        string metaPath = Path.Combine(metaDir, "cased.meta.json");
        // ours: Keywords ["auth", "jwt"], theirs: Keywords ["kubernetes", "helm"] — real Jaccard
        // 0.0 once "Keywords" is read case-insensitively; misread as absent on both sides it would
        // score 1.0 and wrongly admit.
        const string conflictedContent =
            "{\n" +
            "  \"name\": \"test\",\n" +
            "  \"updatedAt\": \"2024-01-01T00:00:00Z\",\n" +
            "<<<<<<< HEAD\n" +
            "  \"Keywords\": [\"auth\", \"jwt\"]\n" +
            "=======\n" +
            "  \"Keywords\": [\"kubernetes\", \"helm\"]\n" +
            ">>>>>>> branch-x\n" +
            "}";
        await File.WriteAllTextAsync(metaPath, conflictedContent);

        string result = await Tools().MemoryReconcile(conflictId: "local/topics/topic/cased.meta.json");

        ResponseParser.Parse(result).Status.Should().Be("error",
            because: "a capitalised \"Keywords\" field must still be read by the gate — reading it " +
                     "as absent on both sides would score an empty-vs-empty 1.0 and admit a merge " +
                     "the driver would refuse");
        (await File.ReadAllTextAsync(metaPath)).Should().Contain("<<<<<<<",
            because: "a refused auto-merge must leave the conflicted file untouched");
    }

    // A duplicate case-variant key ("keywords" AND "Keywords" as sibling properties on the same
    // side) is valid JSON syntax — JSON has no rule against it — so JsonNode.Parse with
    // PropertyNameCaseInsensitive succeeds. The underlying property dictionary throws
    // ArgumentException LAZILY, on the first property read, not at parse time. Both fixtures below
    // put the duplicate on "ours" only; "theirs" is a normal single-keyword object.
    private const string DuplicateCaseVariantKeyContent =
        "{\n" +
        "  \"name\": \"bad\",\n" +
        "  \"updatedAt\": \"2024-01-01T00:00:00Z\",\n" +
        "<<<<<<< HEAD\n" +
        "  \"keywords\": [\"auth\"],\n" +
        "  \"Keywords\": [\"duplicate\"]\n" +
        "=======\n" +
        "  \"keywords\": [\"theirs\"]\n" +
        ">>>>>>> branch-x\n" +
        "}";

    [Fact]
    public async Task AutoResolve_SurvivesADuplicateCaseVariantKeyOnExplicitResolve()
    {
        // Requirement (a): a duplicate case-variant key must not throw out of Reconcile(). It must
        // escalate to manual resolution instead — the same degraded-but-safe outcome as any other
        // sidecar this method cannot safely interpret.
        using var scope = new TestHelpers.StoreScope();

        string scriniaDir = Path.Combine(scope.WorkspaceDir, ".scrinia");
        string metaDir = Path.Combine(scriniaDir, "local", "topics", "topic");
        Directory.CreateDirectory(metaDir);
        string metaPath = Path.Combine(metaDir, "duplicate-key.meta.json");
        await File.WriteAllTextAsync(metaPath, DuplicateCaseVariantKeyContent);

        // If MetaSidecarsAreSimilarEnough's throw is uncaught, this line throws out of the test
        // rather than returning a response — the assertions below would never run.
        string result = await Tools().MemoryReconcile(conflictId: "local/topics/topic/duplicate-key.meta.json");

        ResponseParser.Parse(result).Status.Should().Be("error",
            because: "a duplicate case-variant key cannot be safely auto-merged, so this must " +
                     "escalate to manual resolution rather than either silently resolving or " +
                     "throwing out of Reconcile() entirely");
        (await File.ReadAllTextAsync(metaPath)).Should().Contain("<<<<<<<",
            because: "an escalation must not write anything — the conflict markers stay in place");
    }

    [Fact]
    public async Task Reconcile_NoArgs_SurvivesADuplicateCaseVariantKeyWithoutBlindingTheScan()
    {
        // Requirement (b), the one that matters: one malformed sidecar must not blind the scan to
        // every OTHER conflict in the workspace. Before MetaSidecarsAreSimilarEnough was wrapped in
        // a catch, an uncaught throw here escaped ScanForConflicts entirely — a healthy conflict
        // sitting right next to the bad file would vanish from the report along with it.
        using var scope = new TestHelpers.StoreScope();

        string scriniaDir = Path.Combine(scope.WorkspaceDir, ".scrinia");
        string metaDir = Path.Combine(scriniaDir, "local", "topics", "topic");
        Directory.CreateDirectory(metaDir);

        // A perfectly healthy, genuinely auto-resolvable conflict sitting alongside the bad one —
        // identical keywords on both sides, Jaccard 1.0, admitted at any threshold.
        string healthyPath = Path.Combine(metaDir, "healthy.meta.json");
        const string healthyContent =
            "{\n" +
            "  \"name\": \"healthy\",\n" +
            "  \"updatedAt\": \"2024-01-01T00:00:00Z\",\n" +
            "<<<<<<< HEAD\n" +
            "  \"keywords\": [\"auth\"]\n" +
            "=======\n" +
            "  \"keywords\": [\"auth\"]\n" +
            ">>>>>>> branch-x\n" +
            "}";
        await File.WriteAllTextAsync(healthyPath, healthyContent);

        string badPath = Path.Combine(metaDir, "duplicate-key.meta.json");
        await File.WriteAllTextAsync(badPath, DuplicateCaseVariantKeyContent);

        // If the throw escapes ScanForConflicts, this line throws out of the test entirely rather
        // than returning a response.
        string result = await Tools().MemoryReconcile(); // no-arg call: scan only

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success",
            because: "one malformed sidecar must degrade to a manual-resolution entry, not fail " +
                     "the whole scan");
        parsed.Content.Should().Contain("OK local/topics/topic/healthy.meta.json",
            because: "the healthy conflict must still be reported as auto-resolvable — this is the " +
                     "assertion that pins the bad file cannot blind the scan to everything else");
        parsed.Content.Should().Contain("local/topics/topic/duplicate-key.meta.json",
            because: "the malformed sidecar itself must still be reported, just as needing manual " +
                     "resolution rather than aborting the report entirely");
        parsed.Content.Should().NotContain("duplicate-key.meta.json (.meta.json — sides too dissimilar",
            because: "a duplicate-key throw is a genuine read failure, not a dissimilarity refusal, " +
                     "so it must be reported as \"auto-resolve failed\"");
    }

    // A malformed keywords value (an explicit JSON null — a hand-edit or a foreign writer, not a
    // normal absence) on one side. No merge.config here deliberately: MetaSidecarsAreSimilarEnough's
    // malformed branch must return true regardless of the threshold, because a malformed side is an
    // auto-resolve failure, not a similarity verdict — the two questions are orthogonal, and this
    // fixture pins that the branch doesn't secretly depend on the default not mattering.
    private const string MalformedKeywordsContent =
        "{\n" +
        "  \"name\": \"bad-keywords\",\n" +
        "  \"updatedAt\": \"2024-01-01T00:00:00Z\",\n" +
        "<<<<<<< HEAD\n" +
        "  \"keywords\": [\"auth\"]\n" +
        "=======\n" +
        "  \"keywords\": null\n" +
        ">>>>>>> branch-x\n" +
        "}";

    /// <summary>
    /// Mutation-pinning test for <c>MetaSidecarsAreSimilarEnough</c>'s malformed-keywords branch
    /// (returns <c>true</c> — "cannot judge similarity, not a dissimilarity refusal" — the OPPOSITE
    /// convention from its own <c>TryComputeAutoResolvedMetaJson</c> caller, where <c>false</c> means
    /// escalate). Without this test, inverting that one <c>return true</c> to <c>return false</c>
    /// leaves the entire solution green: the OTHER tests exercising this method (the
    /// duplicate-case-variant-key tests above) go through its unrelated <c>catch</c>, never through
    /// this branch, so an unchanged verdict from them is not evidence this branch is pinned.
    /// </summary>
    [Fact]
    public async Task Reconcile_NoArgs_MalformedKeywordsIsReportedAsAutoResolveFailedNotTooDissimilar()
    {
        using var scope = new TestHelpers.StoreScope();

        string scriniaDir = Path.Combine(scope.WorkspaceDir, ".scrinia");
        string metaDir = Path.Combine(scriniaDir, "local", "topics", "topic");
        Directory.CreateDirectory(metaDir);

        // A perfectly healthy, genuinely auto-resolvable conflict sitting alongside the malformed
        // one — identical keywords on both sides, Jaccard 1.0, admitted at the default 0.7 threshold.
        string healthyPath = Path.Combine(metaDir, "healthy.meta.json");
        const string healthyContent =
            "{\n" +
            "  \"name\": \"healthy\",\n" +
            "  \"updatedAt\": \"2024-01-01T00:00:00Z\",\n" +
            "<<<<<<< HEAD\n" +
            "  \"keywords\": [\"auth\"]\n" +
            "=======\n" +
            "  \"keywords\": [\"auth\"]\n" +
            ">>>>>>> branch-x\n" +
            "}";
        await File.WriteAllTextAsync(healthyPath, healthyContent);

        string badPath = Path.Combine(metaDir, "malformed-keywords.meta.json");
        await File.WriteAllTextAsync(badPath, MalformedKeywordsContent);

        string result = await Tools().MemoryReconcile(); // no-arg call: scan only

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success",
            because: "one malformed sidecar must degrade to a manual-resolution entry, not fail the " +
                     "whole scan");
        parsed.Content.Should().Contain("OK local/topics/topic/healthy.meta.json",
            because: "the healthy conflict must still be reported as auto-resolvable");
        parsed.Content.Should().Contain(
            "local/topics/topic/malformed-keywords.meta.json (.meta.json — auto-resolve failed)",
            because: "a malformed keywords value is a genuine read failure — TryComputeAutoResolvedMetaJson " +
                     "already refuses it — so it must be reported as \"auto-resolve failed\"");
        parsed.Content.Should().NotContain("malformed-keywords.meta.json (.meta.json — sides too dissimilar",
            because: "a malformed keywords value answers a different question than similarity — " +
                     "MetaSidecarsAreSimilarEnough returning true for it is what keeps this from being " +
                     "misreported as a deliberate dissimilarity refusal");
    }

    /// <summary>
    /// ScanForConflicts passes "current" and "incoming" to TryReadConflictCopy — the two
    /// TryReadConflictCopy calls in its "nmp2" branch, cited by name rather than by line because a
    /// line number here has already gone stale once. A swapped pair produces a conflict report with the two
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
