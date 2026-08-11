# Case-Sensitive Guards Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn five behaviours that `remote-mode-leftovers` declared unpinnable into real, executing
tests on Windows, and delete the five now-false ruling comments that apologise for their absence.

**Architecture:** NTFS supports per-directory case sensitivity. A small shared test helper creates a
directory with that flag set, probes to confirm it took, and the tests that need Linux filesystem
semantics run against it. Where the flag cannot be obtained, the tests skip — they never silently
pass.

**Tech Stack:** C#/.NET 10, xunit 2.9.3, FluentAssertions 6.12.2, `Xunit.SkippableFact` 1.5.61.

## Global Constraints

- **`cmds.txt` is untracked and pre-existing.** Never `git add .` or `git add -A` — explicit paths
  only. Never edit, move, delete or stage it.
- **Nothing is pushed.** Local commits only.
- Prefix every shell command with `rtk`.
- **Verify against `rtk dotnet build Scrinia.sln` only.** The C# language server emits phantom
  `CS0101`/`CS1061`/`CS0234`/`CS1503`/`CS8019` errors, including for files that do not exist on
  disk. Ignore the diagnostic stream.
- **`rtk grep` silently undercounts, and so does `rtk proxy grep … | head -N`.** Use the Grep tool
  where completeness matters.
- `TreatWarningsAsErrors=true` for projects not ending in `.Tests`/`.Benchmarks`. `Scrinia.Core`,
  `Scrinia.Mcp` and `Scrinia.Server` are all such projects; the gate is 0 errors.
- **No new deferred-followups.** Anything found is fixed here or raised as an explicit decision.
- Baseline on `main` at branch point: **1867 passed, 0 failed, 4 skipped** (measured). Those four
  skips are pre-existing (embedding-model tests in `Scrinia.Tests`). Stated explicitly so a new
  skip introduced by this branch is not mistaken for one of them.
- **A skipped test is not a passing test.** If the helper cannot obtain case sensitivity, every
  test built on it must skip loudly, never pass. This plan exists because five behaviours were
  believed unobservable; shipping tests that quietly no-op would recreate that problem with a
  green tick on top.

---

## Established facts (measured on this machine, 2026-08-11)

**F1. NTFS per-directory case sensitivity works here, without admin rights and without WSL.**
`fsutil.exe file setCaseSensitiveInfo <dir> enable` returned "is enabled", after which
`MixedCase.txt` and `mixedcase.txt` coexisted in that directory.

**F2. The flag is inherited by newly created subdirectories.** A `child/grandchild` created *after*
the flag was set on the parent held both `Foo.txt` and `foo.txt`. So a store rooted at a
case-sensitive directory stays case-sensitive all the way down.

**F3. The merged fixes genuinely work under real case sensitivity.** With `TMP`/`TEMP` pointed at a
case-sensitive directory, on merge commit `3e64ccf`: the 9 casing tests in `Scrinia.Tests` passed,
and all 187 `Scrinia.Server.Tests` passed. This plan is therefore about *pinning* behaviour already
shown to be correct — not about finding new defects.

**F4. One unrelated test fails under that simulation, and it is an artifact.**
`ProcessRunnerResolveExtensionTests.ResolveExecutable_FindsAlreadyExtensionQualifiedName` fails
because `PATHEXT` is uppercase (`.COM;.EXE;.BAT;.CMD`), so `ProcessRunner.ResolveExecutable`
(`src/Scrinia.Core/Process/ProcessRunner.cs:82-89`) probes `tool.EXE` against a file named
`tool.exe`. On Linux `OperatingSystem.IsWindows()` is false and that branch never runs. **Do not
"fix" it and do not build a test on it.**

**F5. Five ruling comments are now false**, each claiming its behaviour cannot be observed on this
host. Located with the Grep tool:

| Ruling | What it guards |
|---|---|
| `src/Scrinia.Core/FileMemoryStore.cs:1084-1095` | `TryUpdateEntry`'s removed `File.Exists` gate |
| `src/Scrinia.Core/FileMemoryStore.cs:1008` | `Remove`'s case-insensitive fallback |
| `src/Scrinia.Core/FileMemoryStore.cs:1174-1193` | `DeleteArtifact`'s case-insensitive fallback |
| `src/Scrinia.Server/Services/MemoryOrchestrator.cs:191-203` | the append hoist (server) |
| `src/Scrinia.Mcp/MemoryTools.Core.cs:1063-1075` | the append hoist (MCP) |

`src/Scrinia.Core/FileMemoryStore.cs:568` is also a `RULING`, for **the-sweep's G4 thread race**.
It is unrelated to case sensitivity and **must not be touched**. Verified complete with the Grep
tool: there are exactly six `RULING` comments in `src/`, and no seventh.

**F5a. Two MORE sites state the same false claim, untagged, in test XML doc comments.** They carry
no `RULING` marker, which is why the first pass over this missed them. Both must be corrected too —
closing five of seven would leave the branch's own thesis half-stated:

- `tests/Scrinia.Server.Tests/MemoryOrchestratorAttributesTests.cs:244-252` — "**cannot be
  reproduced on NTFS at all** … The order is observable here and nowhere else on this host; a
  case-sensitive filesystem (the project's CI runs `ubuntu-latest`) is what would observe the
  outcome."
- `tests/Scrinia.Tests/RememberWriteMetadataTests.cs:197-205` — the same claim, verbatim, for the
  MCP path.

**F5b. One accurate comment will go stale.** `tests/Scrinia.Tests/TryUpdateEntryTests.cs:111-115`
is correct today but cites "the ruling comment at `FileMemoryStore.TryUpdateEntry`". Task 1 Step 6
rewrites that ruling, so this citation must be updated in the same commit.

**F5c. Exact current line numbers for Task 1 Step 5's reverts** (measured, so the implementer does
not have to hunt): `TryUpdateEntry` is at `:1078`, its `WithExclusiveLock(scope, () => {` at
`:1080`, `string storeDir = GetStoreDirForScope(scope);` at `:1082` — the reinstated gate slots in
at `:1083` with `storeDir` already in scope, and the authoritative lookup it would pre-empt is at
`:1113-1116`. `Remove`'s fallback comparison is at `:1038`; `DeleteArtifact`'s at `:1208`.

**F5d. `FileMemoryStore`'s constructor parameter is `workspaceRoot`, not a store directory**
(`src/Scrinia.Core/FileMemoryStore.cs:175`). `PathRouter.ToStoreDir` resolves it to
`{workspaceRoot}/.scrinia/memories` (`src/Scrinia.Core/PathRouter.cs:154-158`). Everything still
lands under the directory you pass, which is all this plan needs, and the store creates the
subdirectories itself — no need to pre-create them.

**F5e. `MemoryStoreContext.Current` is an `AsyncLocal<IMemoryStore?>`**
(`src/Scrinia.Core/MemoryStoreContext.cs:10`), not a plain static, and neither test project
disables parallelisation. So pointing the MCP tools at a case-sensitive store is parallel-safe, and
an MCP behavioural test is straightforward rather than speculative.

**F6. `Xunit.SkippableFact` 1.5.61 is already referenced by `Scrinia.Tests`**
(`tests/Scrinia.Tests/Scrinia.Tests.csproj:24`) and by `Scrinia.Plugin.Embeddings.Tests`. It is
**not** referenced by `Scrinia.Server.Tests`.

**F7. `tests/Scrinia.TestDoubles` is referenced by both test projects** and exists precisely so
neither keeps its own copy of shared test infrastructure (its own doc comment says so). It
references only `Scrinia.Core` and opts out of `TreatWarningsAsErrors` explicitly
(`Scrinia.TestDoubles.csproj`).

**F8. Both existing fixtures build their roots from `Path.GetTempPath()`** —
`TestHelpers.StoreScope` (`tests/Scrinia.Tests/TestHelpers.cs:103`) and `ScriniaServerFactory`
(`tests/Scrinia.Server.Tests/ScriniaServerFactory.cs:24`). **This plan does not modify either.**
New tests construct a `FileMemoryStore` rooted at a case-sensitive directory directly, which is
additive and cannot disturb the 1867 existing tests.

---

## File Structure

| File | Change | Task |
|---|---|---|
| `tests/Scrinia.TestDoubles/CaseSensitiveDirectory.cs` | Create — the helper | 1 |
| `tests/Scrinia.Tests/CaseSensitiveFileStoreTests.cs` | Create — three store-level guards | 1 |
| `src/Scrinia.Core/FileMemoryStore.cs` | Replace three false rulings | 1 |
| `tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj` | Add `Xunit.SkippableFact` | 2 |
| `tests/Scrinia.Server.Tests/CaseSensitiveAppendTests.cs` | Create — the append guard | 2 |
| `src/Scrinia.Server/Services/MemoryOrchestrator.cs` | Replace one false ruling | 2 |
| `src/Scrinia.Mcp/MemoryTools.Core.cs` | Replace one false ruling | 2 |
| `docs/superpowers/specs/2026-08-10-residuals-closure.md` | Correct the unpinnable claim | 3 |
| `CHANGELOG.md` | Nothing — no production behaviour changes | — |

---

### Task 1: The helper, and the three store-level guards

**Files:**
- Create: `tests/Scrinia.TestDoubles/CaseSensitiveDirectory.cs`
- Create: `tests/Scrinia.Tests/CaseSensitiveFileStoreTests.cs`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` (three ruling comments only — **no logic**)

**Interfaces:**
- Produces: `Scrinia.TestDoubles.CaseSensitiveDirectory`, an `IDisposable` with
  `static CaseSensitiveDirectory? TryCreate(string prefix)` returning `null` when case sensitivity
  cannot be obtained, and a `string Path` property. Task 2 consumes it.

- [ ] **Step 1: Write the helper**

Create `tests/Scrinia.TestDoubles/CaseSensitiveDirectory.cs`:

```csharp
using System.Diagnostics;

namespace Scrinia.TestDoubles;

/// <summary>
/// A temp directory whose filesystem distinguishes "Foo" from "foo" — Linux/macOS-like semantics,
/// obtainable on Windows because NTFS supports the case-sensitivity flag per directory.
///
/// <para>This exists because a whole class of this codebase's name-handling defects is invisible on
/// a case-insensitive filesystem. Five behaviours were documented as unpinnable on Windows and
/// shipped with ruling comments instead of tests; they are pinnable, and this is how.</para>
///
/// <para><b>Returns null rather than throwing when the flag cannot be obtained</b> (a non-NTFS
/// volume, a policy that blocks fsutil, a case-insensitive macOS volume). Callers must SKIP in that
/// case, never pass — a test that silently no-ops on the platform it was written for is worse than
/// no test, and is exactly the failure this class exists to correct.</para>
///
/// <para>The flag is inherited by subdirectories created after it is set, so a store rooted here is
/// case-sensitive all the way down.</para>
/// </summary>
public sealed class CaseSensitiveDirectory : IDisposable
{
    public string Path { get; }

    private CaseSensitiveDirectory(string path) => Path = path;

    /// <summary>Creates a case-sensitive temp directory, or returns null if this machine cannot
    /// provide one. Never throws for the "not supported here" case.</summary>
    public static CaseSensitiveDirectory? TryCreate(string prefix)
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);

        if (OperatingSystem.IsWindows() && !TryEnableOnWindows(path))
        {
            TryDelete(path);
            return null;
        }

        // The probe is the authority, not the platform or fsutil's exit code. macOS is
        // case-INsensitive by default, and a filesystem can ignore the flag silently.
        if (!IsCaseSensitive(path))
        {
            TryDelete(path);
            return null;
        }

        return new CaseSensitiveDirectory(path);
    }

    private static bool TryEnableOnWindows(string path)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("fsutil.exe")
            {
                ArgumentList = { "file", "setCaseSensitiveInfo", path, "enable" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return false;
            proc.WaitForExit(30_000);
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch
        {
            return false; // fsutil absent or blocked — the caller skips
        }
    }

    private static bool IsCaseSensitive(string path)
    {
        string upper = System.IO.Path.Combine(path, "CaseProbe.tmp");
        string lower = System.IO.Path.Combine(path, "caseprobe.tmp");
        try
        {
            File.WriteAllText(upper, "u");
            bool sensitive = !File.Exists(lower);
            File.Delete(upper);
            return sensitive;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    public void Dispose() => TryDelete(Path);
}
```

- [ ] **Step 2: Prove the helper works, and prove the probe is honest**

Before writing any test on top of it, verify by experiment — this helper is the foundation for
everything else, and a broken one produces five tests that skip forever while looking fine.

Write a temporary throwaway test (delete it before committing) that asserts `TryCreate` returns
non-null on this machine and that the directory it hands back really does hold both `A.txt` and
`a.txt`. Run it. Then temporarily make `TryEnableOnWindows` return `false` unconditionally and
confirm `TryCreate` returns `null` rather than a case-insensitive directory. Restore it.

Record both observations in your report. If `TryCreate` returns `null` on this machine, STOP and
report — fact F1 is then wrong and the whole plan needs rethinking.

- [ ] **Step 3: Write the three store-level guards**

Create `tests/Scrinia.Tests/CaseSensitiveFileStoreTests.cs`. These pin the three
`FileMemoryStore` behaviours that shipped with rulings instead of tests. Each constructs a
`FileMemoryStore` rooted at the case-sensitive directory.

Read `tests/Scrinia.Tests/TryUpdateEntryTests.cs` and `tests/Scrinia.Tests/FileMemoryStoreTests.cs`
first and match their construction and assertion style. Use `[SkippableFact]` with
`Skip.If(dir is null, "…")` — `Xunit.SkippableFact` is already referenced (F6).

The three behaviours:

1. **`TryUpdateEntry` resolves a case-mismatched subject.** Upsert an entry named `MixedCase`, call
   `TryUpdateEntry("mixedcase", …)`, assert it returns true and the mutation persisted. Before the
   `File.Exists` gate was removed this returned false on a case-sensitive filesystem.
2. **`Remove` deletes a case-mismatched name.** Upsert `MixedCase`, call `Remove("mixedcase")`,
   assert true and that the entry is gone from `LoadIndex`.
3. **`DeleteArtifact` deletes a case-mismatched name.** Write an artifact under `MixedCase`, call
   `DeleteArtifact("mixedcase", scope)`, assert true and that the artifact file is gone. This is
   the half that was left case-sensitive while `Remove` was fixed — the defect that reported
   success while leaving the artifact orphaned on disk.

For each, assert the *outcome*, not merely a return value, and state in the doc comment what the
pre-fix behaviour was on a case-sensitive filesystem.

- [ ] **Step 4: Run them and confirm they pass**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~CaseSensitiveFileStore"`
Expected: 3 passed, 0 skipped.

**A skipped result here is a failure of this task**, not a pass — it means the helper did not obtain
case sensitivity. Investigate rather than proceeding.

- [ ] **Step 5: Prove each guard is load-bearing**

For each of the three, temporarily revert the corresponding production fix and confirm the test
fails, then restore. The three reverts:

1. `FileMemoryStore.TryUpdateEntry` — reinstate a `File.Exists(Path.Combine(storeDir, SanitizeName(subject) + ".meta.json"))`
   early-return guard at the top of the `WithExclusiveLock` body.
2. `FileMemoryStore.Remove` — make its fallback scan match with `StringComparison.Ordinal` instead
   of `OrdinalIgnoreCase`.
3. `FileMemoryStore.DeleteArtifact` — same, on its fallback.

Record the exact failure message for each in your report. **If any of the three still passes with
its fix reverted, that test is not pinning what it claims — stop and report.** This is the single
most important step in the task.

- [ ] **Step 6: Replace the three false rulings**

In `src/Scrinia.Core/FileMemoryStore.cs`, rewrite the rulings at `:1084-1095`, `:1008` and
`:1174-1193`. **Change comments only — no logic.**

Each currently says the behaviour cannot be observed on this host. That is false. Replace the
"why nothing here can pin it" paragraph in each with a short statement naming the guard, e.g.:

```csharp
            // Pinned by CaseSensitiveFileStoreTests in tests/Scrinia.Tests, which runs against a
            // directory carrying NTFS's per-directory case-sensitivity flag — so this is exercised
            // on Windows, not only on a Linux CI leg. The test skips (never silently passes) on a
            // volume that cannot provide the flag.
```

Keep each ruling's explanation of *what* the code does and *why*; only the unobservability claim is
wrong. **Do not touch the `RULING` at `:568`** — that is the-sweep's G4 thread-race ruling and has
nothing to do with case sensitivity.

Also update `tests/Scrinia.Tests/TryUpdateEntryTests.cs:111-115` in this same commit (F5b): it is
accurate today but cites the ruling you just rewrote. Point it at the new guard instead.

- [ ] **Step 7: Full build and suite**

`rtk dotnet build Scrinia.sln` (0 errors), then `rtk dotnet test Scrinia.sln` — expect
**1870 passed, 0 failed** (1867 + 3).

- [ ] **Step 8: Commit**

```bash
rtk git add tests/Scrinia.TestDoubles/CaseSensitiveDirectory.cs tests/Scrinia.Tests/CaseSensitiveFileStoreTests.cs src/Scrinia.Core/FileMemoryStore.cs
rtk git commit -m "test: pin the three store behaviours that shipped as rulings"
```

---

### Task 2: The append guard, and the last two rulings

**Files:**
- Modify: `tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj`
- Create: `tests/Scrinia.Server.Tests/CaseSensitiveAppendTests.cs`
- Modify: `src/Scrinia.Server/Services/MemoryOrchestrator.cs` (ruling comment only — **no logic**)
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` (ruling comment only — **no logic**)

**Interfaces:**
- Consumes: `Scrinia.TestDoubles.CaseSensitiveDirectory` from Task 1.

This is the behaviour that mattered most: before the hoist, `append()` under a mismatched casing
*replaced* the memory's content on a case-sensitive filesystem. It currently has only an ordering
guard (which asserts `ResolveArtifactAsync` is never reached). Now it can have a behavioural one.

- [ ] **Step 1: Add the package**

Add to `tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj`, matching the style of
`tests/Scrinia.Tests/Scrinia.Tests.csproj:24`:

```xml
    <PackageReference Include="Xunit.SkippableFact" Version="1.5.61" />
```

Run `rtk dotnet build Scrinia.sln` and confirm it restores and builds with 0 errors before writing
any test against it.

- [ ] **Step 2: Write the behavioural append guard**

Create `tests/Scrinia.Server.Tests/CaseSensitiveAppendTests.cs`. Drive
`MemoryOrchestrator.AppendAsync` directly against a `FileMemoryStore` rooted at a
`CaseSensitiveDirectory` — no Kestrel host needed, since this is about filesystem semantics, not
HTTP.

Read `tests/Scrinia.Server.Tests/MemoryOrchestratorAttributesTests.cs` first and match how it
constructs a store and calls the orchestrator.

The test: store a memory as `MixedCase` with known content, then `AppendAsync(store, "mixedcase", …)`,
then assert **the resulting memory contains both the original and the appended content**. Before the
hoist, the append fell through to the store path and replaced the content with the appended chunk
alone.

**`CreatedAt` is NOT a discriminator here — measured.** `StoreAsync` preserves `CreatedAt`, so the
fall-through path keeps it too and a `CreatedAt` assertion passes with the bug present. Assert
content first, and if you assert `CreatedAt` at all, say in the doc comment that it is a
supplementary check and not what pins the fix. Also worth asserting: no `mixedcase.nmp2` /
`mixedcase.meta.json` was created alongside the original.

- [ ] **Step 3: Run it, then prove it is load-bearing**

Run it and confirm it passes (not skips). Then temporarily revert the hoist in
`MemoryOrchestrator.AppendAsync` — move the index lookup back below the artifact read so the read
runs on the caller's casing — and confirm the test fails **on the content assertion**, showing the
original content was destroyed. Restore. Record the exact failure in your report.

If it fails on some other assertion, say which and why in your report rather than adjusting the
test to match.

- [ ] **Step 4: Replace the last two false rulings**

Rewrite the "why nothing here can pin it" paragraphs in
`src/Scrinia.Server/Services/MemoryOrchestrator.cs:191-203` and
`src/Scrinia.Mcp/MemoryTools.Core.cs:1063-1075`. **Comments only — no logic.**

The server one is now pinned behaviourally by `CaseSensitiveAppendTests` as well as by the existing
ordering guard; say so, and keep the existing note that the ordering guard's real discriminator is
that `ResolveArtifactAsync` is never reached.

**Add the MCP behavioural test too.** This was left conditional in an earlier draft; verification
settled it — `MemoryStoreContext.Current` is an `AsyncLocal<IMemoryStore?>` (F5e), so pointing the
MCP tools at a `FileMemoryStore` rooted in a case-sensitive directory is both straightforward and
parallel-safe. Mirror `tests/Scrinia.Tests/RememberWriteMetadataTests.cs:217-221`, substituting a
case-sensitive root for `TestHelpers.StoreScope`. Same assertion shape as the server one, and the
same warning applies: content is the discriminator, `CreatedAt` is not.

Then rewrite the MCP ruling to name that guard. Do not imply a guard that does not exist — if you
end up unable to add it, the ruling must say plainly that only the ordering guard covers that path.

- [ ] **Step 4b: Correct the two untagged false claims in test doc comments**

These carry no `RULING` marker and were missed by the first pass over this branch (F5a). Both state
the defect "cannot be reproduced on NTFS at all" and name `ubuntu-latest` as the only instrument.
Both are false:

- `tests/Scrinia.Server.Tests/MemoryOrchestratorAttributesTests.cs:244-252`
- `tests/Scrinia.Tests/RememberWriteMetadataTests.cs:197-205`

Rewrite each to state what is now true, and to keep the accurate part — that the *ordering* is what
that particular test observes, and the *outcome* is what the new case-sensitive test observes. The
two guards are complementary; say so rather than implying the older one is now redundant.

- [ ] **Step 5: Full build and suite**

`rtk dotnet build Scrinia.sln` (0 errors), then `rtk dotnet test Scrinia.sln` — expect Task 1's
total + however many tests you added. State the exact number.

- [ ] **Step 6: Commit**

```bash
rtk git add tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj tests/Scrinia.Server.Tests/CaseSensitiveAppendTests.cs src/Scrinia.Server/Services/MemoryOrchestrator.cs src/Scrinia.Mcp/MemoryTools.Core.cs
rtk git commit -m "test(server): pin append's case-sensitive behaviour behaviourally"
```

Add any MCP test file you created to that `git add` line.

---

### Task 3: Correct the record in the closure spec

**Files:**
- Modify: `docs/superpowers/specs/2026-08-10-residuals-closure.md`

- [ ] **Step 1: Correct the unpinnable claim**

**There are TWO such passages, not one, and they cover different pairs of sites.** An earlier draft
of this plan conflated them, which would have left one standing. Both are inside the Group C
section (`:108-276`):

- **`:180-192`** — "Two sites carry rulings instead of tests, because NTFS cannot observe either
  fix… a green test written at either site would be green for the filesystem's reasons, not the
  code's… The reviewer specifically checked whether 'unpinnable' was a convenient excuse here and
  judged it genuine." Covers `FileMemoryStore.cs:1008` (`Remove`) and `:1084` (`TryUpdateEntry`).
- **`:269-275`** — "Two of the three fixes are unobservable on NTFS and say so at the site… naming
  the instrument that would observe them (a case-sensitive filesystem; CI runs `ubuntu-latest`)."
  Covers the append hoist and `DeleteArtifact`'s fallback — a **different** pair.

Rewrite both. Between them they account for seven false claims, not two: five `RULING` comments and
the two untagged test doc comments from Task 2 Step 4b.

Rewrite that passage to record what is now true: the behaviours are pinned by tests running against
a case-sensitive directory on Windows; the rulings are gone; and the reason they were believed
unobservable was that nobody checked whether NTFS could be made case-sensitive — a claim two
reviewers examined for convenience and passed, because they were asking whether the excuse was
sincere rather than whether it was true.

Cite the new test files and this branch's commits. Cite nothing under `.superpowers/`.

- [ ] **Step 2: Commit**

```bash
rtk git add docs/superpowers/specs/2026-08-10-residuals-closure.md
rtk git commit -m "docs(residuals): the unpinnable rulings were wrong"
```

---

## Self-review notes

**No production logic changes anywhere in this plan.** Every `src/` edit is a comment. If a task
finds itself changing behaviour, that is out of scope — stop and raise it.

**The failure mode to watch** is a test that skips instead of running and is mistaken for a pass.
Task 1 Step 4 and Task 2 Step 3 both require observing a genuine pass, and Task 1 Step 5 and Task 2
Step 3 both require observing a genuine failure with the fix reverted. Those four observations are
the whole value of the branch; everything else is comments.

**Not in scope:** the `ProcessRunner.ResolveExecutable` PATHEXT artifact (F4), the `WorkspaceRoot`
and `WithInfo` Group G items, and `CopyMemory`'s destination existence check, which remains a
recorded open decision.
