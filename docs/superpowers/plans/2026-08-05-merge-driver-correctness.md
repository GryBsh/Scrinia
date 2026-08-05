# Merge Driver Correctness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the `.nmp2` git merge driver work — it currently throws an unhandled exception on every real conflict — and fix the entity-fact cache staleness and merge documentation that shipped alongside it.

**Architecture:** Git hands merge drivers extensionless temp files but exposes the real artifact path via `%P`, which this driver never requested. Threading `%P` through `Program.cs` → `Nmp2ConflictHandler` fixes the crash and every downstream path derivation. A conflicted merge then writes whole ours/theirs copies beside the artifact, marks the sidecar, writes conflict markers as a detection signal, and returns 1 so git halts. All of it is proven by an integration harness that shells out to real `git merge` against the real built binary.

**Tech Stack:** .NET 10, C#, xunit 2.9.3, FluentAssertions 6.12.2, `Scrinia.Merge` (self-contained AOT exe, `AssemblyName=scri-merge`), `Scrinia.Core`, `Scrinia.Mcp`.

**Spec:** `docs/superpowers/specs/2026-08-05-review-remediation-design.md`

## Global Constraints

- Branch: `merge-driver-correctness`, forked from `main` at `801c818`. This plan covers **branch 1 of 2**; C4/C5/C6 get their own plan after this merges.
- Prefix every shell command with `rtk` — including inside `&&` chains.
- Build: `rtk dotnet build Scrinia.sln -c Debug`. Never run `dotnet build` while a test run is in flight.
- Never run two `dotnet test` invocations against the same project concurrently.
- Test projects touched here: `tests/Scrinia.Merge.Tests`, `tests/Scrinia.Tests`.
- `.scrinia/` changes are part of your commits.
- Comment style rule 1 — **no unfounded attribution**: describe an observed failure, never a presumed culprit.
- Comment style rule 2 — **no meta commentary**: state the factual why in one or two lines. Do not narrate the review that produced a change or cross-reference sibling files.
- A claim in a comment must be something actually established.

---

## File Structure

**Create:**
- `tests/Scrinia.Merge.Tests/GitMergeHarness.cs` — throwaway git repo wired to the real `scri-merge` binary. Owns driver location, git invocation, three-way seeding, cleanup.
- `tests/Scrinia.Merge.Tests/GitMergeIntegrationTests.cs` — the five integration cases. Assertions run against what `git merge` produces.
- `src/Scrinia.Merge/GitConfigRepair.cs` — reads and rewrites `merge.scrinia-*.driver` in local repo config.
- `tests/Scrinia.Tests/EntityFactCacheInvalidationTests.cs` — C1 coverage.
- `tests/Scrinia.Tests/ReconcileConflictCopyTests.cs` — reconcile prefers the whole copies.

**Modify:**
- `src/Scrinia.Merge/Program.cs` — accept `%P`, resolve `.scrinia` from it, dispatch auto-repair.
- `src/Scrinia.Merge/Nmp2ConflictHandler.cs` — `mergedPath` parameter, conflict semantics.
- `src/Scrinia/Commands/ScriniaCommands.Setup.cs:334-337` — register both drivers with `%P`.
- `tests/Scrinia.Merge.Tests/Nmp2ConflictHandlerTests.cs` — new parameter, new return value.
- `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` — reconcile prefers `conflict/` copies.
- `src/Scrinia.Core/FileMemoryStore.cs:885-908` — fact-store invalidation.
- `docs/multi-user-setup.md`, `src/Scrinia.Mcp/skills/merge-safety.md`, `CHANGELOG.md`.

---

### Task 1: Real-git integration harness

Build the harness against behaviour that already works, so a green suite proves the harness itself before it is used to prove anything else.

**Files:**
- Create: `tests/Scrinia.Merge.Tests/GitMergeHarness.cs`
- Create: `tests/Scrinia.Merge.Tests/GitMergeIntegrationTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `GitMergeHarness` with `RepoDir`, `LocateDriver()`, `RegisterDriver(string kind, bool passPathname = true)`, `RegisterRawDriver(string kind, string command)`, `WriteFile(string, string)`, `ReadFile(string)`, `FileExists(string)`, `CommitAll(string)`, `Git(string, bool throwOnFailure = true)` returning `(int ExitCode, string StdOut, string StdErr)`, `Merge(string branch)`, `StatusCode(string)`, `SeedThreeWay(string, string, string?, string?)`. Collection name constant `GitMergeCollection.Name`.

- [ ] **Step 1: Write the harness**

Create `tests/Scrinia.Merge.Tests/GitMergeHarness.cs`:

```csharp
using System.Diagnostics;

namespace Scrinia.Merge.Tests;

/// <summary>
/// A throwaway git repository wired to the built scri-merge binary. Assertions run against
/// what `git merge` produces rather than a direct Handle() call: git passes extensionless
/// temp files, which is the one input shape a direct call never reproduces.
/// </summary>
internal sealed class GitMergeHarness : IDisposable
{
    public string RepoDir { get; }
    private readonly string _driverPath;

    public GitMergeHarness()
    {
        RepoDir = Path.Combine(Path.GetTempPath(), $"scrinia-gitmerge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RepoDir);
        _driverPath = LocateDriver();

        Git("init -q .");
        Git("config user.email test@scrinia.invalid");
        Git("config user.name scrinia-test");
        Git("config commit.gpgsign false");
        // Line-ending rewriting would alter artifact bytes between commit and checkout and
        // break content assertions.
        Git("config core.autocrlf false");
    }

    /// <summary>Absolute path to the built scri-merge executable.</summary>
    internal static string LocateDriver()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Scrinia.sln")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException(
                $"Could not find Scrinia.sln walking up from {AppContext.BaseDirectory}.");

        string bin = Path.Combine(dir.FullName, "src", "Scrinia.Merge", "bin");
        if (!Directory.Exists(bin))
            throw new InvalidOperationException($"{bin} does not exist. Build Scrinia.sln first.");

        string? exe = Directory.EnumerateFiles(bin, "scri-merge*", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p) is "scri-merge.exe" or "scri-merge")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        return exe ?? throw new InvalidOperationException(
            $"No scri-merge executable under {bin}. Build Scrinia.sln first.");
    }

    /// <summary>
    /// Registers the built driver for one kind. Git runs driver commands through sh even on
    /// Windows, so the path is forward-slashed and single-quoted: a backslash would be read
    /// as an escape character.
    /// </summary>
    public void RegisterDriver(string kind, bool passPathname = true)
    {
        string p = _driverPath.Replace('\\', '/');
        string placeholders = passPathname ? "%O %A %B %P" : "%O %A %B";
        Git($"config merge.scrinia-{kind}.driver \"'{p}' {kind} {placeholders}\"");
    }

    /// <summary>Registers an arbitrary sh command as a driver, for probing git's own dispatch.</summary>
    public void RegisterRawDriver(string kind, string command) =>
        Git($"config merge.scrinia-{kind}.driver \"{command}\"");

    public void WriteFile(string relativePath, string content)
    {
        string full = Path.Combine(RepoDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public string ReadFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoDir, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public bool FileExists(string relativePath) =>
        File.Exists(Path.Combine(RepoDir, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public void CommitAll(string message)
    {
        Git("add -A");
        Git($"commit -q -m \"{message}\"");
    }

    public (int ExitCode, string StdOut, string StdErr) Git(string arguments, bool throwOnFailure = true)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = RepoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start git. Is it on PATH?");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (throwOnFailure && proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {arguments} failed ({proc.ExitCode}).\nstdout: {stdout}\nstderr: {stderr}");
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>Runs `git merge` without throwing — a conflict is an expected outcome here.</summary>
    public (int ExitCode, string StdOut, string StdErr) Merge(string branch) =>
        Git($"merge {branch}", throwOnFailure: false);

    /// <summary>Porcelain status code for one path, e.g. "UU" for an unmerged file.</summary>
    public string StatusCode(string relativePath)
    {
        var (_, stdout, _) = Git($"status --porcelain -- {relativePath}", throwOnFailure: false);
        string? line = stdout.Split('\n')
            .FirstOrDefault(l => l.Contains(Path.GetFileName(relativePath), StringComparison.Ordinal));
        return line is { Length: >= 2 } ? line[..2] : "";
    }

    /// <summary>
    /// Commits <paramref name="baseContent"/>, branches "other" carrying
    /// <paramref name="theirsContent"/>, and leaves the default branch carrying
    /// <paramref name="oursContent"/>. A null side commits an unrelated file instead, so the
    /// branch still diverges without touching the artifact.
    /// </summary>
    public void SeedThreeWay(string relativePath, string baseContent, string? oursContent, string? theirsContent)
    {
        WriteFile(relativePath, baseContent);
        CommitAll("base");
        string defaultBranch = Git("rev-parse --abbrev-ref HEAD").StdOut.Trim();

        Git("checkout -q -b other");
        if (theirsContent is not null) WriteFile(relativePath, theirsContent);
        else WriteFile("unrelated-theirs.txt", "x");
        CommitAll("theirs");

        Git($"checkout -q {defaultBranch}");
        if (oursContent is not null) WriteFile(relativePath, oursContent);
        else WriteFile("unrelated-ours.txt", "x");
        CommitAll("ours");
    }

    public void Dispose()
    {
        try { ForceDelete(RepoDir); } catch { }
    }

    // git marks objects under .git read-only; a plain recursive delete fails on Windows.
    private static void ForceDelete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }
}
```

- [ ] **Step 2: Write the two cases that pass today**

Create `tests/Scrinia.Merge.Tests/GitMergeIntegrationTests.cs`:

```csharp
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
        repo.RegisterDriver("meta", passPathname: false);
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
}
```

- [ ] **Step 3: Build, then run the new tests**

```bash
rtk dotnet build Scrinia.sln -c Debug
rtk dotnet test tests/Scrinia.Merge.Tests/ -c Debug --no-build
```

Expected: PASS. Both cases describe behaviour that already works. A failure here is a harness defect — fix it before continuing, because every later task depends on this harness telling the truth.

- [ ] **Step 4: Commit**

```bash
rtk git add tests/Scrinia.Merge.Tests/GitMergeHarness.cs tests/Scrinia.Merge.Tests/GitMergeIntegrationTests.cs
rtk git commit -m "test(merge): drive the real driver through real git merges"
```

---

### Task 2: Thread `%P` through the driver

Fixes the crash and every path derivation. Conflict semantics stay as they are; Task 3 changes those.

**Files:**
- Modify: `src/Scrinia.Merge/Nmp2ConflictHandler.cs:5-55`
- Modify: `src/Scrinia.Merge/Program.cs:3-30`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Setup.cs:334-337`
- Modify: `tests/Scrinia.Merge.Tests/Nmp2ConflictHandlerTests.cs`
- Modify: `tests/Scrinia.Merge.Tests/GitMergeIntegrationTests.cs`

**Interfaces:**
- Consumes: `GitMergeHarness` from Task 1.
- Produces: `Nmp2ConflictHandler.Handle(string ancestorPath, string oursPath, string theirsPath, string mergedPath, MergeConfig config)` returning `int`.

- [ ] **Step 1: Write the failing integration test**

Add to `GitMergeIntegrationTests.cs`:

```csharp
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
        repo.ReadFile(Sidecar).Should().Contain("\"conflicted\": true",
            because: "the real sidecar is marked, not a stray .meta.json in the repo root");
        repo.FileExists(".meta.json").Should().BeFalse(
            because: "ChangeExtension on an extensionless temp file used to produce exactly this");
    }
```

Also add the config-resolution guard:

```csharp
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
```

This is a regression guard on config loading, not a test of the `FindScriniaDir` fallback.
Git always runs merge drivers from the worktree root, so the old
`Directory.GetCurrentDirectory()` fallback resolved to the same `.scrinia` the `%P` walk
now finds. The fix removes a coincidence; it does not change an observable outcome here.

- [ ] **Step 2: Run both to verify they fail**

```bash
rtk dotnet test tests/Scrinia.Merge.Tests/ -c Debug --no-build --filter "BothSidesChanged_WritesCopiesNamedAfterTheArtifact"
rtk dotnet test tests/Scrinia.Merge.Tests/ -c Debug --no-build --filter "ConflictDirFromMergeConfig_IsHonoured"
```

Expected: FAIL. `StdErr` contains `System.ArgumentException: The path is empty. (Parameter 'path')` from `Nmp2ConflictHandler.cs:25`.

- [ ] **Step 3: Add `mergedPath` to the handler**

In `src/Scrinia.Merge/Nmp2ConflictHandler.cs`, replace the signature and the path derivations. The method becomes:

```csharp
    /// <param name="mergedPath">
    /// Git's %P — the pathname the merged result will be stored under. %A is an extensionless
    /// temp file in the repo root, so every path derived from it loses the artifact's identity.
    /// </param>
    public static int Handle(string ancestorPath, string oursPath, string theirsPath,
        string mergedPath, MergeConfig config)
    {
        // 1. Trivial merge: if one side is unchanged from ancestor, take the other.
        // Not reached when git drives a two-branch merge — git resolves an unchanged side by
        // blob OID before consulting a driver. Retained for callers that do reach it.
        if (FilesAreIdentical(ancestorPath, oursPath))
        {
            File.Copy(theirsPath, oursPath, overwrite: true);
            CopySidecar(theirsPath, oursPath);
            return 0;
        }

        if (FilesAreIdentical(ancestorPath, theirsPath))
            return 0;

        // 2. Both changed: conflict-as-data
        string dir = Path.GetDirectoryName(mergedPath)!;
        string dirFull = Path.GetFullPath(dir);
        string conflictDir = Path.GetFullPath(Path.Combine(dirFull, config.ConflictDir));
        if (Path.IsPathRooted(config.ConflictDir) || !IsWithin(dirFull, conflictDir))
            throw new InvalidOperationException(
                $"merge.config ConflictDir '{config.ConflictDir}' escapes the artifact directory.");
        string currentDir = Path.Combine(conflictDir, "current");
        string incomingDir = Path.Combine(conflictDir, "incoming");

        Directory.CreateDirectory(currentDir);
        Directory.CreateDirectory(incomingDir);

        string fileName = Path.GetFileName(mergedPath);

        File.Copy(oursPath, Path.Combine(currentDir, fileName), overwrite: true);
        File.Copy(theirsPath, Path.Combine(incomingDir, fileName), overwrite: true);

        MarkMetaConflicted(Path.GetFullPath(mergedPath));

        return 0;
    }
```

Note the two sidecar copies that used to accompany the artifact copies are gone: they resolved to a bare `.meta.json` and never copied anything. The real sidecar is marked instead.

- [ ] **Step 4: Pass `%P` from `Program.cs`**

Replace lines 3-18 of `src/Scrinia.Merge/Program.cs`:

```csharp
// Usage: scri-merge meta %O %A %B %P
//        scri-merge nmp2 %O %A %B %P
// Exit 0 = merge resolved, Exit 1 = conflict

if (args.Length < 5)
{
    Console.Error.WriteLine("Usage: scri-merge <meta|nmp2> <ancestor> <ours> <theirs> <pathname>");
    return 1;
}

string command = args[0];
string ancestor = args[1];
string ours = args[2];
string theirs = args[3];
string mergedPath = Path.GetFullPath(args[4]);

var config = MergeConfig.Load(FindScriniaDir(mergedPath));
```

and the `nmp2` dispatch arm:

```csharp
    "nmp2" => Nmp2ConflictHandler.Handle(ancestor, ours, theirs, mergedPath, config),
```

`FindScriniaDir` is unchanged, but now receives a path inside the workspace, so it finds `.scrinia` by walking up instead of falling through to its `Directory.GetCurrentDirectory()` fallback.

- [ ] **Step 5: Register `%P` in setup**

In `src/Scrinia/Commands/ScriniaCommands.Setup.cs`, replace lines 334-337:

```csharp
        RunGit(root, "config", "merge.scrinia-meta.driver",
            $".scrinia/hooks/scri-merge meta %O %A %B %P");
        RunGit(root, "config", "merge.scrinia-nmp2.driver",
            $".scrinia/hooks/scri-merge nmp2 %O %A %B %P");
```

- [ ] **Step 6: Update the existing unit tests for the new parameter**

In `tests/Scrinia.Merge.Tests/Nmp2ConflictHandlerTests.cs`, every `Handle(...)` call takes the merged path. For the three tests that exercise the conflict branch, the merged path is the artifact's real location; pass `oursPath` for the two trivial-merge tests since those branches ignore it.

Replace line 48:
```csharp
        int result = Nmp2ConflictHandler.Handle(ancestorPath, oursPath, theirsPath, oursPath, _config);
```
Replace line 63:
```csharp
        int result = Nmp2ConflictHandler.Handle(ancestorPath, oursPath, theirsPath, oursPath, _config);
```
Replace line 82:
```csharp
        int result = Nmp2ConflictHandler.Handle(ancestorPath, oursPath, theirsPath, oursPath, _config);
```
Replace line 110:
```csharp
        Nmp2ConflictHandler.Handle(ancestorPath, oursPath, theirsPath, oursPath, _config);
```
Replace line 129:
```csharp
        Nmp2ConflictHandler.Handle(ancestorPath, oursPath, theirsPath, oursPath, _config);
```

`Handle_OursUnchanged_CopiesSidecar` (line 118) still passes: the trivial-merge branch retains `CopySidecar`.

- [ ] **Step 7: Build and run both merge suites**

```bash
rtk dotnet build Scrinia.sln -c Debug
rtk dotnet test tests/Scrinia.Merge.Tests/ -c Debug --no-build
```

Expected: PASS, including `BothSidesChanged_WritesCopiesNamedAfterTheArtifact`.

- [ ] **Step 8: Commit**

```bash
rtk git add src/Scrinia.Merge/ src/Scrinia/Commands/ScriniaCommands.Setup.cs tests/Scrinia.Merge.Tests/
rtk git commit -m "fix(merge): resolve artifact paths from %P, not git's temp file"
```

---

### Task 3: Halt the merge and write markers

**Files:**
- Modify: `src/Scrinia.Merge/Nmp2ConflictHandler.cs`
- Modify: `tests/Scrinia.Merge.Tests/Nmp2ConflictHandlerTests.cs:84`
- Modify: `tests/Scrinia.Merge.Tests/GitMergeIntegrationTests.cs`

**Interfaces:**
- Consumes: `Handle(..., mergedPath, config)` from Task 2.
- Produces: `Handle` returns `1` on the both-changed branch. `%A` holds marker-wrapped content.

- [ ] **Step 1: Write the failing test**

Add to `GitMergeIntegrationTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run it to verify it fails**

```bash
rtk dotnet test tests/Scrinia.Merge.Tests/ -c Debug --no-build --filter "BothSidesChanged_HaltsTheMergeAndLeavesResolvableMarkers"
```

Expected: FAIL — `ExitCode` is 0 and the file has no markers, because `Handle` still returns 0 and leaves `%A` as ours.

- [ ] **Step 3: Write markers and return 1**

In `src/Scrinia.Merge/Nmp2ConflictHandler.cs`, add `using System.Text;` at the top. Replace the tail of the both-changed branch (`MarkMetaConflicted(...)` through `return 0;`) with:

```csharp
        MarkMetaConflicted(Path.GetFullPath(mergedPath));

        // Git adds no markers of its own for a custom driver — whatever the driver leaves in
        // %A becomes the working-tree file. The markers are the detection signal reconcile()
        // scans for; the decodable content lives in the conflict/ copies.
        WriteConflictMarkers(oursPath, theirsPath, fileName);

        return 1;
    }

    private static void WriteConflictMarkers(string oursPath, string theirsPath, string fileName)
    {
        string ours = File.ReadAllText(oursPath);
        string theirs = File.ReadAllText(theirsPath);

        var sb = new StringBuilder();
        sb.Append("<<<<<<< ours:").Append(fileName).Append('\n');
        sb.Append(ours);
        if (!ours.EndsWith('\n')) sb.Append('\n');
        sb.Append("=======\n");
        sb.Append(theirs);
        if (!theirs.EndsWith('\n')) sb.Append('\n');
        sb.Append(">>>>>>> theirs:").Append(fileName).Append('\n');

        File.WriteAllText(oursPath, sb.ToString());
    }
```

- [ ] **Step 4: Update the unit test that asserted exit 0**

In `tests/Scrinia.Merge.Tests/Nmp2ConflictHandlerTests.cs`, replace line 84:

```csharp
        result.Should().Be(1, because: "a conflicted artifact must stop the merge");
```

- [ ] **Step 5: Build and run**

```bash
rtk dotnet build Scrinia.sln -c Debug
rtk dotnet test tests/Scrinia.Merge.Tests/ -c Debug --no-build
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
rtk git add src/Scrinia.Merge/Nmp2ConflictHandler.cs tests/Scrinia.Merge.Tests/
rtk git commit -m "fix(merge): halt a conflicted nmp2 merge and leave resolvable markers"
```

---

### Task 4: Auto-repair a legacy 4-argument invocation

**Files:**
- Create: `src/Scrinia.Merge/GitConfigRepair.cs`
- Modify: `src/Scrinia.Merge/Program.cs`
- Modify: `tests/Scrinia.Merge.Tests/GitMergeIntegrationTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `GitConfigRepair.TryAppendPathname(string kind, out string message)` returning `bool`.

- [ ] **Step 1: Write the failing test**

Add to `GitMergeIntegrationTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run it to verify it fails**

```bash
rtk dotnet test tests/Scrinia.Merge.Tests/ -c Debug --no-build --filter "LegacyFourArgumentConfig_IsRepairedAndTheMergeStops"
```

Expected: FAIL — the config still lacks `%P` and no conflict payload is produced.

- [ ] **Step 3: Write the repair helper**

Create `src/Scrinia.Merge/GitConfigRepair.cs`:

```csharp
using System.Diagnostics;

namespace Scrinia.Merge;

/// <summary>
/// Repairs a driver registration made before %P was passed. Scrinia.Merge is a standalone
/// AOT executable with no Scrinia.Core dependency, so it shells out to git directly.
/// </summary>
public static class GitConfigRepair
{
    /// <summary>
    /// Appends %P to merge.scrinia-{kind}.driver in LOCAL repository config. Never writes
    /// global config: a merge driver must not alter a user's global git state.
    /// </summary>
    public static bool TryAppendPathname(string kind, out string message)
    {
        string key = $"merge.scrinia-{kind}.driver";

        if (!TryGit($"config --local --get {key}", out string current) ||
            string.IsNullOrWhiteSpace(current))
        {
            message = $"could not read {key} from local git config";
            return false;
        }

        current = current.Trim();
        if (current.Contains("%P", StringComparison.Ordinal))
        {
            message = $"{key} already passes %P";
            return true;
        }

        string repaired = current + " %P";
        if (!TryGit($"config --local {key} \"{repaired}\"", out _))
        {
            message = $"could not write {key} to local git config";
            return false;
        }

        message = $"updated {key} to pass %P";
        return true;
    }

    private static bool TryGit(string arguments, out string stdout)
    {
        stdout = "";
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
```

- [ ] **Step 4: Dispatch to it from `Program.cs`**

Replace the `args.Length < 5` guard added in Task 2 with:

```csharp
if (args.Length < 4)
{
    Console.Error.WriteLine("Usage: scri-merge <meta|nmp2> <ancestor> <ours> <theirs> <pathname>");
    return 1;
}

if (args.Length < 5)
{
    // Registered before %P was passed. Without the pathname the artifact cannot be
    // identified, so repair the registration and stop rather than guess.
    string kind = args[0];
    bool repaired = GitConfigRepair.TryAppendPathname(kind, out string repairMessage);
    Console.Error.WriteLine(repaired
        ? $"[scrinia] {repairMessage}; re-run your merge"
        : $"[scrinia:error] merge driver invoked without %P and {repairMessage}. " +
          "Re-run: scri setup --multi-user");
    return 1;
}
```

- [ ] **Step 5: Build and run**

```bash
rtk dotnet build Scrinia.sln -c Debug
rtk dotnet test tests/Scrinia.Merge.Tests/ -c Debug --no-build
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
rtk git add src/Scrinia.Merge/ tests/Scrinia.Merge.Tests/
rtk git commit -m "fix(merge): repair a pre-%P driver registration instead of guessing"
```

---

### Task 5: reconcile prefers the whole conflict copies

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:147-155`
- Create: `tests/Scrinia.Tests/ReconcileConflictCopyTests.cs`

**Interfaces:**
- Consumes: the `conflict/current` and `conflict/incoming` layout from Task 2.
- Produces: private helpers `TryReadConflictCopy(string artifactPath, string side)` and `ReadConflictDirName(string artifactDir)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Scrinia.Tests/ReconcileConflictCopyTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core.Encoding;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// A conflicted .nmp2 in the working tree holds two interleaved base64 halves, which do not
/// reliably decode. The merge driver also writes whole valid copies to conflict/current and
/// conflict/incoming; reconcile must resolve from those.
/// </summary>
public sealed class ReconcileConflictCopyTests
{
    private static ScriniaMcpTools Tools() => new();

    [Fact]
    public async Task Reconcile_Nmp2WithConflictCopies_ResolvesFromTheWholeCopy()
    {
        using var scope = new TestHelpers.StoreScope();

        string dir = Path.Combine(scope.WorkspaceDir, ".scrinia", "memories", "local");
        Directory.CreateDirectory(dir);
        string artifact = Path.Combine(dir, "auth-flow.nmp2");

        string oursWhole = Nmp2ChunkedEncoder.Encode("the ours version");
        string theirsWhole = Nmp2ChunkedEncoder.Encode("the theirs version");

        // Working-tree file: markers wrapping halves that will not decode on their own.
        await File.WriteAllTextAsync(artifact,
            "<<<<<<< ours:auth-flow.nmp2\n" + oursWhole +
            "\n=======\n" + theirsWhole +
            "\n>>>>>>> theirs:auth-flow.nmp2\n");

        Directory.CreateDirectory(Path.Combine(dir, "conflict", "current"));
        Directory.CreateDirectory(Path.Combine(dir, "conflict", "incoming"));
        await File.WriteAllTextAsync(Path.Combine(dir, "conflict", "current", "auth-flow.nmp2"), oursWhole);
        await File.WriteAllTextAsync(Path.Combine(dir, "conflict", "incoming", "auth-flow.nmp2"), theirsWhole);

        string result = await Tools().MemoryReconcile(
            conflictId: "memories/local/auth-flow.nmp2", choice: "theirs");

        ResponseParser.Parse(result).Status.Should().Be("success");

        string resolved = await File.ReadAllTextAsync(artifact);
        resolved.Should().NotContain("<<<<<<<");
        System.Text.Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(resolved))
            .Should().Be("the theirs version",
                because: "the whole incoming copy is decodable where the marker half is not");
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
rtk dotnet test tests/Scrinia.Tests/ -c Debug --no-build --filter "Reconcile_Nmp2WithConflictCopies_ResolvesFromTheWholeCopy"
```

Expected: FAIL — the resolved content comes from the marker half, not the whole copy.

- [ ] **Step 3: Add the helpers**

In `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`, add these two private static methods to the class:

```csharp
    /// <summary>
    /// Whole conflict copy the merge driver wrote beside the artifact, or null when absent.
    /// The marker body holds two partial base64 streams; these copies are complete artifacts.
    /// </summary>
    private static string? TryReadConflictCopy(string artifactPath, string side)
    {
        string dir = Path.GetDirectoryName(artifactPath)!;
        string candidate = Path.Combine(dir, ReadConflictDirName(dir), side, Path.GetFileName(artifactPath));
        if (!File.Exists(candidate)) return null;
        try { return File.ReadAllText(candidate); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>ConflictDir from .scrinia/merge.config, defaulting to "conflict".</summary>
    private static string ReadConflictDirName(string artifactDir)
    {
        var dir = new DirectoryInfo(artifactDir);
        while (dir is not null && !string.Equals(dir.Name, ".scrinia", StringComparison.OrdinalIgnoreCase))
            dir = dir.Parent;
        if (dir is null) return "conflict";

        string configPath = Path.Combine(dir.FullName, "merge.config");
        if (!File.Exists(configPath)) return "conflict";
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(configPath));
            string? configured = node?["conflictDir"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(configured) ? "conflict" : configured;
        }
        catch { return "conflict"; }
    }
```

- [ ] **Step 4: Prefer the copy in the resolve path**

In `ResolveConflictByPath`, replace lines 149-153:

```csharp
                if (type == "nmp2")
                {
                    string side = normalisedChoice == "ours" ? "current" : "incoming";
                    extracted = TryReadConflictCopy(absolutePath, side) ?? extracted;
                    try { extracted = System.Text.Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(extracted!)); }
                    catch { /* fall through: write extracted as-is */ }
                }
```

- [ ] **Step 5: Prefer the copy in the scan preview**

In `ScanForConflicts`, replace lines 235-236:

```csharp
                string oursDecoded = TryDecodeNmp2(TryReadConflictCopy(filePath, "current") ?? oursRaw!);
                string theirsDecoded = TryDecodeNmp2(TryReadConflictCopy(filePath, "incoming") ?? theirsRaw ?? "");
```

- [ ] **Step 6: Build and run**

```bash
rtk dotnet build Scrinia.sln -c Debug
rtk dotnet test tests/Scrinia.Tests/ -c Debug --no-build
```

Expected: PASS, and no regression in `ReconcileScanTests`.

- [ ] **Step 7: Commit**

```bash
rtk git add src/Scrinia.Mcp/MemoryTools.Lifecycle.cs tests/Scrinia.Tests/ReconcileConflictCopyTests.cs
rtk git commit -m "fix(mcp): resolve nmp2 conflicts from the whole copies, not the marker body"
```

---

### Task 6: Invalidate the entity-fact cache on bundle import

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:885-908`
- Create: `tests/Scrinia.Tests/EntityFactCacheInvalidationTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: no new public API.

- [ ] **Step 1: Write the failing test**

Create `tests/Scrinia.Tests/EntityFactCacheInvalidationTests.cs`:

```csharp
using System.IO.Compression;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// SaveIndexUnsafe is the only index-mutation path that does not invalidate the cached
/// EntityFactStore, and bundle import reaches it. A store whose cache was warmed before an
/// import would serve facts that omit every imported memory for the rest of the process.
/// </summary>
public sealed class EntityFactCacheInvalidationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceRoot;
    private readonly string _targetRoot;
    private readonly FileMemoryStore _source;
    private readonly FileMemoryStore _target;

    public EntityFactCacheInvalidationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-factcache-" + Guid.NewGuid().ToString("N")[..8]);
        _sourceRoot = Path.Combine(_tempDir, "source");
        _targetRoot = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(_sourceRoot);
        Directory.CreateDirectory(_targetRoot);
        _source = new FileMemoryStore(_sourceRoot);
        _target = new FileMemoryStore(_targetRoot);
    }

    public void Dispose()
    {
        _source.Dispose();
        _target.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ImportingABundle_InvalidatesTheCachedFactStore()
    {
        const string topicScope = "local:api";

        await _source.WriteArtifactAsync("auth-flow", topicScope, "auth flow notes");
        _source.Upsert(new ArtifactEntry(
            Name: "auth-flow",
            Uri: "",
            OriginalBytes: 15,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            ContentPreview: "auth flow notes",
            Attributes: new Dictionary<string, string[]> { ["topic"] = ["authentication"] }),
            topicScope);

        string bundlePath = Path.Combine(_tempDir, "api.scrinia-bundle");
        using (var stream = new FileStream(bundlePath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            BundleFormatService.ExportTopicsToZip(zip, _source, ["api"]);

        // Warm the target's cache BEFORE the import — this is what makes the staleness visible.
        _target.GetEntityFactStore().Entities("topic").Should().BeEmpty();

        using (var readStream = new FileStream(bundlePath, FileMode.Open, FileAccess.Read))
        using (var zipRead = new ZipArchive(readStream, ZipArchiveMode.Read))
            BundleFormatService.ImportTopicsFromZip(zipRead, _target, filterTopics: null, overwrite: true);

        _target.GetEntityFactStore().Entities("topic")
            .Should().Contain(EntityRef.Of("topic", "authentication"),
                because: "an imported memory's attributes must reach the fact store");
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
rtk dotnet test tests/Scrinia.Tests/ -c Debug --no-build --filter "ImportingABundle_InvalidatesTheCachedFactStore"
```

Expected: FAIL — the assertion finds an empty entity list because the warmed cache is served unchanged.

- [ ] **Step 3: Add the invalidation**

In `src/Scrinia.Core/FileMemoryStore.cs`, in `SaveIndexUnsafe`, after the topic-cache invalidation at lines 905-907, add:

```csharp
        lock (_factStoreLock) { _factStore = null; _factStoreGeneration++; }
```

- [ ] **Step 4: Run to verify it passes**

```bash
rtk dotnet test tests/Scrinia.Tests/ -c Debug --no-build --filter "ImportingABundle_InvalidatesTheCachedFactStore"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/EntityFactCacheInvalidationTests.cs
rtk git commit -m "fix(core): invalidate the fact-store cache on bundle import"
```

---

### Task 7: Correct the merge documentation and the shipped skill

No code changes, so no test cycle — the deliverable is prose that matches the code.

**Files:**
- Modify: `docs/multi-user-setup.md`
- Modify: `src/Scrinia.Mcp/skills/merge-safety.md`
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Rewrite the merge-driver setup section of `docs/multi-user-setup.md`**

Replace the Prerequisites (lines 5-9) with:

```markdown
## Prerequisites

- Git 2.x or later
- Scrinia installed and on your PATH
```

Replace the whole "Merge Driver Setup" section (lines 19-41) with:

```markdown
## Merge Driver Setup

Run this once per clone:

```bash
scri setup --multi-user
```

It registers both merge drivers in the repository's local git config, writes
`.scrinia/.gitattributes` mapping `*.meta.json` and `*.nmp2` to them, and creates
`.scrinia/merge.config`. Copy the `scri-merge` binary for your platform into
`.scrinia/hooks/` — it is gitignored, because it is platform-specific.
```

Delete the "Post-Merge Hook" section entirely (lines 43-56); no such hook exists.

In "Workflow After Merge", replace step 2 (line 63) with:

```markdown
2. **Read the conflict report** — a conflicted `.nmp2` stops the merge and git marks the file unmerged
```

In "Structural Prevention", delete the "Binary marking" bullet (line 75) — `.gitattributes` maps `.nmp2` to the merge driver, not to `binary`.

In "Troubleshooting", delete the `jq` entry (line 81) and replace the "merge driver not found" entry (line 79) with:

```markdown
**"merge driver not found"** — Run `scri setup --multi-user`, then confirm the `scri-merge`
binary is present in `.scrinia/hooks/`.
```

Update the example path on line 65 from `local/skills/qa.nmp2` to `memories/local/auth-flow.nmp2`.

- [ ] **Step 2: Correct the shipped skill**

In `src/Scrinia.Mcp/skills/merge-safety.md`, replace the "Team setup" section (lines 36-40) with:

```markdown
### 5. Team setup
For new team members:
- Run `scri setup --multi-user` in the clone — it registers both merge drivers, writes
  `.scrinia/.gitattributes`, and creates `.scrinia/merge.config`
- Copy the `scri-merge` binary for the platform into `.scrinia/hooks/`
- See docs/multi-user-setup.md for full instructions
```

Replace the "Binary marking" bullet (line 33) with:

```markdown
- **Merge driver**: `.nmp2` conflicts stop the merge and are written to `conflict/current`
  and `conflict/incoming` beside the artifact for `reconcile` to resolve
```

Replace line 15 with:

```markdown
- `.nmp2` conflicts stop the merge; resolve them with `reconcile`
```

- [ ] **Step 3: Add the CHANGELOG entry**

In `CHANGELOG.md`, directly under `## [Unreleased]` (line 11), insert:

```markdown
### Fixed — git merge driver

- The `.nmp2` merge driver crashed on every real conflict. Git passes extensionless temp
  files, so `Path.GetDirectoryName` returned an empty string and `Path.GetFullPath` threw;
  the merge halted with an unhandled `ArgumentException` and no way to resolve it through
  `reconcile()`. Both drivers now receive git's `%P` pathname and resolve the artifact,
  its sidecar, and the conflict directory from it.
- A conflicted `.nmp2` now stops the merge with git's path marked unmerged, writes whole
  `conflict/current` and `conflict/incoming` copies beside the artifact, marks the sidecar
  `"conflicted": true`, and leaves markers for `reconcile()` to find. `reconcile()` resolves
  from the whole copies rather than the partial base64 in the marker body.
- A driver registered before `%P` repairs its own local git config and stops, rather than
  proceeding without knowing which artifact it is merging.
- Bundle import left the cached entity-fact store stale, so `facts()` and entity-fact
  surfacing in search omitted every imported memory for the life of the process.
```

- [ ] **Step 4: Full verification**

```bash
rtk dotnet build Scrinia.sln -c Debug
rtk dotnet test tests/Scrinia.Tests/ -c Debug --no-build
rtk dotnet test tests/Scrinia.Server.Tests/ -c Debug --no-build
rtk dotnet test tests/Scrinia.Merge.Tests/ -c Debug --no-build
```

Expected: all green.

- [ ] **Step 5: Commit**

```bash
rtk git add docs/multi-user-setup.md src/Scrinia.Mcp/skills/merge-safety.md CHANGELOG.md
rtk git commit -m "docs(merge): describe the driver that ships, not the one that never did"
```

---

## Done when

- All six merge-driver integration cases pass against real `git merge` and the real binary.
- `Scrinia.Tests`, `Scrinia.Server.Tests` and `Scrinia.Merge.Tests` are green.
- No file in `docs/multi-user-setup.md` or `merge-safety.md` names a script that does not exist.
- Branch 2 (`internal-consistency`: C4, C5, C6) gets its own plan after this merges.
