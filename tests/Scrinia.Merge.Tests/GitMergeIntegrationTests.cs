using FluentAssertions;
using Xunit;

namespace Scrinia.Merge.Tests;

[CollectionDefinition(GitMergeCollection.Name, DisableParallelization = true)]
public sealed class GitMergeCollection
{
    public const string Name = "git-merge-integration";
}

/// <summary>
/// Drives real `git merge` against the built scri-merge binary. Each case spawns processes,
/// so the collection is serialised.
/// </summary>
[Collection(GitMergeCollection.Name)]
public sealed class GitMergeIntegrationTests
{
    private const string Artifact = ".scrinia/memories/local/auth-flow.nmp2";
    private const string Sidecar = ".scrinia/memories/local/auth-flow.meta.json";
    private const string NmpAttributes = "*.meta.json merge=scrinia-meta\n*.nmp2 merge=scrinia-nmp2\n";

    private static string Nmp2(string payload) =>
        $"NMP/2 {payload.Length}B CRC32:00000000 BR+B64 C:1\n##CHUNK:1\n{payload}\n";

    [Fact]
    public void OneSideUnchanged_GitResolvesWithoutInvokingTheDriver()
    {
        using var repo = new GitMergeHarness();
        // A driver that always fails. If git invokes it the merge cannot succeed, so a clean
        // merge is proof that git resolved the path itself by blob OID at the tree level.
        repo.RegisterRawDriver("nmp2", "exit 9");
        repo.WriteFile(".scrinia/.gitattributes", NmpAttributes);
        repo.SeedThreeWay(Artifact, Nmp2("YmFzZQ"), oursContent: null, theirsContent: Nmp2("dGhlaXJz"));

        var result = repo.Merge("other");

        result.ExitCode.Should().Be(0,
            because: "when one side is unchanged git never invokes the merge driver");
        repo.ReadFile(Artifact).Should().Contain("dGhlaXJz",
            because: "the side that changed wins");
    }

    [Fact]
    public void MetaDriver_WithSimilarKeywords_AutoResolves()
    {
        using var repo = new GitMergeHarness();
        repo.RegisterDriver("meta");
        repo.WriteFile(".scrinia/.gitattributes", NmpAttributes);
        repo.SeedThreeWay(
            Sidecar,
            baseContent: Meta(["a", "b", "c", "d"], "2026-01-01T00:00:00+00:00"),
            oursContent: Meta(["a", "b", "c", "d", "shared"], "2026-02-01T00:00:00+00:00"),
            theirsContent: Meta(["a", "b", "c", "d", "shared"], "2026-03-01T00:00:00+00:00"));

        var result = repo.Merge("other");

        result.ExitCode.Should().Be(0,
            because: "keyword sets above the Jaccard threshold auto-merge");
        string merged = repo.ReadFile(Sidecar);
        merged.Should().Contain("shared");
        merged.Should().Contain("2026-03-01",
            because: "the later updatedAt wins");
    }

    private static string Meta(string[] keywords, string updatedAt)
    {
        string kw = string.Join(",", keywords.Select(k => $"\"{k}\""));
        return $"{{\"name\":\"auth-flow\",\"keywords\":[{kw}],\"originalBytes\":10," +
               $"\"chunkCount\":1,\"updatedAt\":\"{updatedAt}\"}}\n";
    }

    [Fact]
    public void BothSidesChanged_WritesCopiesNamedAfterTheArtifact()
    {
        using var repo = new GitMergeHarness();
        repo.RegisterDriver("nmp2");
        repo.WriteFile(".scrinia/.gitattributes", NmpAttributes);
        repo.WriteFile(Sidecar, "{\"name\":\"auth-flow\"}\n");
        repo.SeedThreeWay(Artifact, Nmp2("YmFzZQ"), Nmp2("b3Vycw"), Nmp2("dGhlaXJz"));

        var result = repo.Merge("other");

        result.StdErr.Should().NotContain("Unhandled exception",
            because: "the driver must not crash on the path shape git actually sends");
        repo.FileExists(".scrinia/memories/local/conflict/current/auth-flow.nmp2").Should().BeTrue(
            because: "copies are named after the artifact, not after git's temp file");
        repo.FileExists(".scrinia/memories/local/conflict/incoming/auth-flow.nmp2").Should().BeTrue();
        repo.ReadFile(".scrinia/memories/local/conflict/current/auth-flow.nmp2").Should().Contain("b3Vycw");
        repo.ReadFile(".scrinia/memories/local/conflict/incoming/auth-flow.nmp2").Should().Contain("dGhlaXJz");
    }

    [Fact]
    public void BothSidesChanged_HaltsTheMergeAndLeavesResolvableMarkers()
    {
        using var repo = new GitMergeHarness();
        repo.RegisterDriver("nmp2");
        repo.WriteFile(".scrinia/.gitattributes", NmpAttributes);
        repo.WriteFile(Sidecar, "{\"name\":\"auth-flow\"}\n");
        repo.SeedThreeWay(Artifact, Nmp2("YmFzZQ"), Nmp2("b3Vycw"), Nmp2("dGhlaXJz"));

        var result = repo.Merge("other");

        result.ExitCode.Should().NotBe(0,
            because: "a conflicted memory must stop the merge rather than discard one side");
        repo.StatusCode(Artifact).Should().Be("UU",
            because: "git records the path as unmerged so it cannot be committed by accident");

        string body = repo.ReadFile(Artifact);
        body.Should().Contain("<<<<<<<").And.Contain("=======").And.Contain(">>>>>>>",
            because: "reconcile() detects conflicts by scanning for markers");
        body.Should().Contain("b3Vycw").And.Contain("dGhlaXJz",
            because: "both sides remain present in the marker body");
    }

    [Fact]
    public void LegacyFourArgumentConfig_IsRepairedAndTheMergeStops()
    {
        using var repo = new GitMergeHarness();
        repo.RegisterDriver("nmp2", passPathname: false);   // pre-%P registration
        repo.WriteFile(".scrinia/.gitattributes", NmpAttributes);
        repo.WriteFile(Sidecar, "{\"name\":\"auth-flow\"}\n");
        repo.SeedThreeWay(Artifact, Nmp2("YmFzZQ"), Nmp2("b3Vycw"), Nmp2("dGhlaXJz"));

        var first = repo.Merge("other");

        first.ExitCode.Should().NotBe(0,
            because: "without %P the driver cannot identify the artifact, so it must not guess");
        string configured = repo.Git("config merge.scrinia-nmp2.driver").StdOut;
        configured.Should().Contain("%P", because: "the driver repairs its own registration");

        repo.Git("merge --abort", throwOnFailure: false);
        var second = repo.Merge("other");

        second.ExitCode.Should().NotBe(0, because: "the conflict is real; it still stops the merge");
        repo.FileExists(".scrinia/memories/local/conflict/current/auth-flow.nmp2").Should().BeTrue(
            because: "the repaired registration produces the full conflict payload");
    }

    [Fact]
    public void LegacyFourArgumentConfig_RepairsBothDriverKeys_NotOnlyTheInvokedOne()
    {
        using var repo = new GitMergeHarness();
        repo.RegisterDriver("meta", passPathname: false);
        repo.RegisterDriver("nmp2", passPathname: false);
        repo.WriteFile(".scrinia/.gitattributes", NmpAttributes);
        repo.WriteFile(Sidecar, "{\"name\":\"auth-flow\"}\n");
        // Only the artifact diverges, so this merge invokes the nmp2 driver and never the meta one.
        repo.SeedThreeWay(Artifact, Nmp2("YmFzZQ"), Nmp2("b3Vycw"), Nmp2("dGhlaXJz"));

        repo.Merge("other");

        repo.Git("config merge.scrinia-nmp2.driver").StdOut.Should().Contain("%P");
        repo.Git("config merge.scrinia-meta.driver").StdOut.Should().Contain("%P",
            because: "leaving the uninvoked key at four arguments only defers the same failure "
                     + "to the next merge that touches a sidecar");
    }

    [Fact]
    public void LegacyFourArgumentConfig_DoesNotReportUnregisteredKeyAsFailure()
    {
        using var repo = new GitMergeHarness();
        repo.RegisterDriver("nmp2", passPathname: false);   // meta is never registered at all
        repo.WriteFile(".scrinia/.gitattributes", NmpAttributes);
        repo.WriteFile(Sidecar, "{\"name\":\"auth-flow\"}\n");
        repo.SeedThreeWay(Artifact, Nmp2("YmFzZQ"), Nmp2("b3Vycw"), Nmp2("dGhlaXJz"));

        var result = repo.Merge("other");

        result.StdErr.Should().NotContain("[scrinia:error]",
            because: "the invoked key (nmp2) repaired successfully; the merge must not be framed as a failure");
        // The old fixed "meta: X; nmp2: Y" pairing read the invoked key's own success as if it were
        // just one half of a two-key failure report.
        result.StdErr.Should().NotContain("could not read merge.scrinia-meta.driver from local git config; nmp2:",
            because: "meta's status must not be paired ahead of the invoked key's outcome as if both " +
                     "keys carried equal weight");
        // `git config --get` on merge.scrinia-meta.driver exits 1 with empty stderr because the key was
        // never registered, not because anything failed — GitConfigRepair.TryAppendPathname cannot tell
        // the difference (F4), so Program.cs must not surface its "could not read" text at all when the
        // invoked key succeeded; it can only assert that a repair was attempted, not characterise a
        // result it doesn't actually know.
        result.StdErr.Should().NotContain("could not read",
            because: "the invoked key succeeded; the other key's unreadable-vs-failed ambiguity must " +
                     "not be surfaced as failure language when nothing is known to have failed");
    }

    [Fact]
    public void ConflictCopies_AreIgnoredByGitSoTheyCannotBeCommitted()
    {
        using var repo = new GitMergeHarness();
        repo.RegisterDriver("nmp2");
        repo.WriteFile(".scrinia/.gitattributes", NmpAttributes);
        repo.WriteFile(Sidecar, "{\"name\":\"auth-flow\"}\n");
        repo.SeedThreeWay(Artifact, Nmp2("YmFzZQ"), Nmp2("b3Vycw"), Nmp2("dGhlaXJz"));

        repo.Merge("other");

        repo.FileExists(".scrinia/memories/local/conflict/.gitignore").Should().BeTrue(
            because: "the driver ignores its own scratch as it creates it, whatever the dir is named");

        string status = repo.Git("status --porcelain", throwOnFailure: false).StdOut;
        status.Should().NotContain("conflict/",
            because: "a committed copy would silently override every later conflict resolution");
    }

    [Fact]
    public void SidecarAndArtifactConflictingTogether_MetaDriverGenuinelyConflicts()
    {
        using var repo = new GitMergeHarness();
        repo.RegisterDriver("meta");
        repo.RegisterDriver("nmp2");
        repo.WriteFile(".scrinia/.gitattributes", NmpAttributes);

        // Both paths diverge on both branches, so git invokes both drivers in one merge.
        repo.WriteFile(Artifact, Nmp2("YmFzZQ"));
        repo.WriteFile(Sidecar, Meta(["base-kw"], "2026-01-01T00:00:00+00:00"));
        repo.CommitAll("base");
        string defaultBranch = repo.Git("rev-parse --abbrev-ref HEAD").StdOut.Trim();

        repo.Git("checkout -q -b other");
        repo.WriteFile(Artifact, Nmp2("dGhlaXJz"));
        repo.WriteFile(Sidecar, Meta(["theirs-kw"], "2026-03-01T00:00:00+00:00"));
        repo.CommitAll("theirs");

        repo.Git($"checkout -q {defaultBranch}");
        repo.WriteFile(Artifact, Nmp2("b3Vycw"));
        repo.WriteFile(Sidecar, Meta(["ours-kw"], "2026-02-01T00:00:00+00:00"));
        repo.CommitAll("ours");

        var result = repo.Merge("other");

        result.ExitCode.Should().NotBe(0,
            because: "disjoint keywords fall below the Jaccard threshold and the artifact diverged too");

        string sidecar = repo.ReadFile(Sidecar);
        sidecar.Should().NotContain("theirs-kw",
            because: "the meta driver must genuinely conflict here rather than auto-merge — a union of the "
                     + "disjoint keyword sets would silently pick a side instead of stopping the merge");
    }

    [Fact]
    public void ConflictDirFromMergeConfig_IsHonoured()
    {
        using var repo = new GitMergeHarness();
        repo.RegisterDriver("nmp2");
        repo.WriteFile(".scrinia/.gitattributes", NmpAttributes);
        repo.WriteFile(".scrinia/merge.config",
            "{\"jaccardThreshold\":0.7,\"resolver\":\"none\",\"conflictDir\":\"disputes\"}\n");
        repo.WriteFile(Sidecar, "{\"name\":\"auth-flow\"}\n");
        repo.SeedThreeWay(Artifact, Nmp2("YmFzZQ"), Nmp2("b3Vycw"), Nmp2("dGhlaXJz"));

        repo.Merge("other");

        repo.FileExists(".scrinia/memories/local/disputes/current/auth-flow.nmp2").Should().BeTrue(
            because: "MergeConfig must be loaded and its ConflictDir honoured");
    }

    [Fact]
    public void LegacyBranch_IsInsideTheTopLevelTryCatch()
    {
        // GitConfigRepair.TryGit's catch filter covers only Win32Exception and
        // InvalidOperationException (GitConfigRepair.cs); an IOException from reading git's stdout
        // escapes uncaught. The legacy four-argument branch makes two TryGit calls (one per key via
        // TryAppendPathname), so it must run inside Program.cs's own top-level try/catch to turn an
        // escaping IOException into a "[scrinia:error]" diagnostic instead of a raw stack trace
        // reaching git's merge output.
        //
        // Driving an actual IOException out of TryGit deterministically would require replacing the
        // real `git` process it shells out to with one that fails mid-read in a controlled, portable
        // way -- not achievable without restructuring TryGit to accept an injected process runner,
        // which is beyond this task's scope. This test instead pins the structural guarantee directly
        // from source: the legacy branch's git-calling code sits after the try's opening brace, and
        // nothing above the try can return once inside it.
        string source = File.ReadAllText(FindProgramCs());

        int usageGuardIndex = source.IndexOf("if (args.Length < 4)", StringComparison.Ordinal);
        int tryIndex = source.IndexOf("\ntry", StringComparison.Ordinal);
        int legacyGuardIndex = source.IndexOf("if (args.Length < 5)", StringComparison.Ordinal);
        int repairCallIndex = source.IndexOf("GitConfigRepair.TryAppendPathname", StringComparison.Ordinal);
        int catchIndex = source.IndexOf("catch (Exception ex)", StringComparison.Ordinal);

        usageGuardIndex.Should().BeGreaterThan(-1, because: "the usage guard must still exist");
        tryIndex.Should().BeGreaterThan(-1, because: "Program.cs must open a top-level try block");
        legacyGuardIndex.Should().BeGreaterThan(-1, because: "the legacy four-argument branch must still exist");
        repairCallIndex.Should().BeGreaterThan(-1, because: "the legacy branch must still repair via GitConfigRepair");
        catchIndex.Should().BeGreaterThan(-1, because: "the top-level catch must still exist");

        usageGuardIndex.Should().BeLessThan(tryIndex,
            because: "the < 4 argument usage guard makes no git call and stays outside the try");
        tryIndex.Should().BeLessThan(legacyGuardIndex)
            .And.BeLessThan(repairCallIndex,
                because: "the legacy branch's length check and its two GitConfigRepair.TryAppendPathname " +
                         "calls must sit inside the try, so an IOException escaping TryGit is caught here " +
                         "rather than reaching git as a raw stack trace");
        repairCallIndex.Should().BeLessThan(catchIndex,
            because: "the catch must close over the repair calls too -- moving the try's opening brace " +
                     "without the catch still covering this code would leave the exception uncaught again");
    }

    private static string FindProgramCs() =>
        Path.Combine(GitMergeHarness.FindRepoRoot(), "src", "Scrinia.Merge", "Program.cs");
}
