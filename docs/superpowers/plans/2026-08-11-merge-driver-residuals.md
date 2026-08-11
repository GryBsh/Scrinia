# Merge Driver Residuals Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the eight items the `merge-driver-correctness` branch parked, per `docs/superpowers/specs/2026-08-08-merge-driver-residuals-design.md` (Group A of the closure list).

**Architecture:** `Scrinia.Merge` is a standalone AOT/trimmed executable with **zero** project references — not even `Scrinia.Core`. That isolation is deliberate and this plan preserves it: the only new coupling is a test-project reference, in the opposite direction (`Scrinia.Tests` → `Scrinia.Merge`), because `Scrinia.Merge` has nothing to drag in.

**Tech Stack:** C#/.NET 10, xunit 2.9.3, FluentAssertions 6.12.2, no mocking frameworks. `Scrinia.Merge` is AOT-published; `System.Text.Json` source-generated contexts on both sides of the `merge.config` contract.

## Global Constraints

- **Never `git add .` or `git add -A`.** `cmds.txt` is a pre-existing untracked file at the repo root, not part of this work (now covered by `.gitignore:40`, but the rule stands). Stage explicit paths only.
- **Nothing is pushed.** Local commits only.
- Prefix every shell command with `rtk`, including inside `&&` chains.
- `TreatWarningsAsErrors=true` for non-`.Tests`/`.Benchmarks` projects. Test projects carry ~1784 pre-existing analyzer warnings — **the gate is 0 errors, not 0 warnings.**
- **`Scrinia.Merge` must not gain a `ProjectReference` to anything.** It is AOT-published and dependency-free by design (`src/Scrinia.Merge/Scrinia.Merge.csproj` has only an `InternalsVisibleTo`). A task that adds one has taken a wrong turn — stop and report.
- The C# language server emits **phantom** `CS1061`/`CS0101`/`CS8019` errors, including against files that do not exist on disk. **Verify against `rtk dotnet build Scrinia.sln` only.**
- `rtk grep` silently undercounts. Use the `Grep` tool or `rtk proxy grep --include=*.cs`.
- **This branch does not get to write new deferred-followups.** Anything found is fixed here or raised as an explicit decision at the time.

---

## Fact base — re-verified at `5e780fa`

The spec's facts were established at `c3773bf`. A verification pass re-checked every one against current bytes. **`git log c3773bf..5e780fa` is 63 commits, none of which touch `src/Scrinia.Merge/`, `tests/Scrinia.Merge.Tests/`, `docs/multi-user-setup.md`, or the merge-config block of `ScriniaCommands.Setup.cs`.** The spec has not drifted.

| # | Status | Current fact |
|---|---|---|
| F1 | VERIFIED | `IsWithin` returns `true` on equality — `Nmp2ConflictHandler.cs:116-124`, equality branch at `:118-119` |
| F2 | VERIFIED | Check is `Path.IsPathRooted(config.ConflictDir) \|\| !IsWithin(dirFull, conflictDir)` at `Nmp2ConflictHandler.cs:31-33` |
| F3 | VERIFIED | Legacy branch `Program.cs:13-35`; `try` opens at `:37` |
| F4 | VERIFIED | Both keys reported unconditionally — `Program.cs:29-33` |
| F5 | VERIFIED | `InsertConflictedFlag` at `Nmp2ConflictHandler.cs:126-135`, literal write at `:134`. **No production reader anywhere**, including `web/` TypeScript. Only test assertions and this repo's own dogfooded `.scrinia/` memory data |
| F6 | VERIFIED | `OrderByDescending(File.GetLastWriteTimeUtc)` — `GitMergeHarness.cs:44-47` |
| F7 | VERIFIED | `MemoryTools.Lifecycle.cs:250-251`, passing `"current"` and `"incoming"` |
| F8 | **CORRECTED** | The phrase spans `docs/multi-user-setup.md:52-53`, not `:52`. And `:53` **already says** "delete those directories once you are done to keep the workspace tidy" — so the spec's "no untrack guidance" overstates it. The real gap is narrower: that is generic cleanup advice, not upgrade-specific *untrack* instruction, and there is no freshness caveat anywhere in the file |
| F8a | VERIFIED | `MergeConfigTests.cs` — grep for `Setup\|CliJson\|WriteMergeConfig\|Serialize` returns nothing; all three tests `File.WriteAllText` hand-built JSON and call `MergeConfig.Load` |
| F9 | **VERIFIED AND STRONGER** | `ScriniaCommands.Setup.cs:352` — `ConflictDir: "conflict"` is **hard-coded**. No CLI flag, env var or config key can change it (searched all of `src/Scrinia/Commands/*.cs`). Hand-editing `merge.config` is the only route to the §1 failure |

**Answers to the questions the spec left open:**

- **Q1 — `IsWithin` has exactly one call site** (`Nmp2ConflictHandler.cs:31`). It is `private` to that class. A **separate** `Scrinia.Core.PathContainment.IsWithin` (`src/Scrinia.Core/PathContainment.cs:12`) has 6 call sites and also treats equality as containment — it is independent, symlink-resolving code in another assembly. **Do not touch it.** Changing the merge helper cannot affect it.
- **Q2 — the writer and reader are two different types, and they do not even have the same shape.** Writer: `internal sealed record MergeConfig(double JaccardThreshold, string Resolver, string ConflictDir)` — `src/Scrinia/Commands/CliJsonContext.cs:28-31`, **three** properties, no defaults. Reader: `public sealed record MergeConfig(double JaccardThreshold = 0.7, string Resolver = "none", string? ResolverCommand = null, string ConflictDir = "conflict")` — `src/Scrinia.Merge/MergeConfig.cs:6-11`, **four** properties, all defaulted.

  **`ResolverCommand` exists only on the reader.** The CLI writer cannot express it at all, so `scri setup` can never produce a `merge.config` that sets it — it is reachable only by hand-editing. That is a second, previously unrecorded divergence in the same contract §5 exists to pin, found by this plan's verification pass. It is not a defect to fix here (the field is honoured when hand-written, which is coherent), but Task 4's test must not pretend the two types match.

- **`MergeConfig.Load(string scriniaDir)`** (`MergeConfig.cs:15-31`) takes a **directory**, combines it with `"merge.config"`, and returns `Default` on a missing file **or on any exception** — the catch is broad. Defaults: `JaccardThreshold=0.7`, `Resolver="none"`, `ResolverCommand=null`, `ConflictDir="conflict"`. **This silent fallback is why Task 4's test must assert non-default values**: a test using the defaults cannot distinguish a correct round trip from a total parse failure.
- **Q5 — `tests/Scrinia.Merge.Tests/ConflictDirContainmentTests.cs` already exists** and covers `"../../../escape"` and `/abs/escape`, plus `RelativeConflictDir_IsAllowed` for `"conflict"`. It does **not** cover `"."` or `""`, and never asserts `.gitignore` absence. §1 **extends** this file. The `.gitignore` write is `Nmp2ConflictHandler.cs:44`, content literally `"*\n"`.

**Group G item, now settled — record it, do not fix it here.** `reconcile()`'s resolution path (`MemoryTools.Lifecycle.cs:199-212`, and the meta auto-merge branch at `:125-138`) writes with a raw `File.WriteAllText` straight to the artifact path. It never goes through `FileMemoryStore`'s write pipeline, never touches the sidecar, and never triggers a reindex. So `originalBytes`, `chunkCount`, `contentPreview` and `keywords` do keep their pre-merge values. **The three-generations-old "unverified" claim is true exactly as described.** The spec puts it out of scope as a behaviour change needing its own design; Task 6 records that verdict.

---

## Decisions taken while planning

Two points the spec left to the planner. Both are recorded here so a reviewer sees them as decisions, not drift.

**§4 — delete the write-only `"conflicted"` flag.** The spec recommends deletion and flags it as "most likely to be overturned on review". Taking the recommendation: F5 confirms zero production readers across all file types including the frontend, `WriteSidecarCore` drops the unknown property on the next write anyway, and its only live effect is a spurious committed diff on every conflicted merge. Four lines to restore if a reader ever wants it, and by then the reader's needs will be known.

**§5 — the round-trip test lives in `tests/Scrinia.Tests`, which gains a `ProjectReference` to `Scrinia.Merge`.** The verification pass suggested the reverse (`Scrinia.Merge.Tests` → `Scrinia`, plus a new `InternalsVisibleTo`). This direction is strictly smaller: `Scrinia.Merge` is dependency-free, so referencing it drags in nothing, whereas referencing `Scrinia` would make the merge tests build the entire CLI. And `src/Scrinia/Scrinia.csproj:30` **already** has `InternalsVisibleTo Include="Scrinia.Tests"`, so the internal writer type is already visible and **no new `InternalsVisibleTo` is needed at all**.

---

## File Structure

| File | Change | Task |
|---|---|---|
| `src/Scrinia.Merge/Nmp2ConflictHandler.cs` | `IsWithin` → strict (§1); delete `InsertConflictedFlag` (§4) | 1, 3 |
| `tests/Scrinia.Merge.Tests/ConflictDirContainmentTests.cs` | Extend for `"."`/`""` + `.gitignore` absence | 1 |
| `src/Scrinia.Merge/Program.cs` | `try` moves above the legacy branch (§2); re-phrase outcomes (§3) | 2 |
| `tests/Scrinia.Merge.Tests/MarkMetaConflictedTests.cs`, `Nmp2ConflictHandlerTests.cs`, `GitMergeIntegrationTests.cs` | Remove `"conflicted"` assertions | 3 |
| `src/Scrinia/Commands/ScriniaCommands.Setup.cs` | Extract the merge.config writer so a test can call it | 4 |
| `tests/Scrinia.Tests/Scrinia.Tests.csproj` | Add `ProjectReference` to `Scrinia.Merge` | 4 |
| `tests/Scrinia.Tests/MergeConfigRoundTripTests.cs` | Create — real writer → real reader | 4 |
| `tests/Scrinia.Merge.Tests/GitMergeHarness.cs` | Select driver by build configuration (§6) | 5 |
| `tests/Scrinia.Tests/…` conflict-scan coverage | `ScanForConflicts` current/incoming distinguishable by content (§7) | 5 |
| `docs/multi-user-setup.md` | Upgrade note: untrack + freshness caveat (§8) | 6 |
| `docs/superpowers/specs/2026-08-10-residuals-closure.md` | Strike Group A; record the Group G verdict | 6 |
| `CHANGELOG.md` | The `"conflicted"` removal and the containment tightening | 6 |

---

### Task 1: §1 — a `ConflictDir` of `"."` or `""` stops silently untracking the workspace

**Files:**
- Modify: `src/Scrinia.Merge/Nmp2ConflictHandler.cs:116-124` (the helper), `:31` (the call)
- Test: `tests/Scrinia.Merge.Tests/ConflictDirContainmentTests.cs` (extend — the file exists)

**Interfaces:**
- Produces: `IsStrictlyWithin(string baseFull, string candidateFull)` replacing `IsWithin`, returning `false` when the two are equal. Private to `Nmp2ConflictHandler`; nothing outside the class can see it.

**Why this is the sharpest item.** With `ConflictDir` of `"."` or `""`, `Path.Combine(dirFull, ".")` resolves back to `dirFull`. `IsWithin` returns `true` on equality (F1), so the guard at `:31` passes, and the driver then writes a `.gitignore` containing `*` **into the artifact directory itself** (`:44`). That silently untracks every memory at and below it and clobbers any `.gitignore` the user had. Before the `.gitignore` write existed, the same misconfiguration merely left a stray `current/`+`incoming/` pair — **the previous fix wave made this failure mode worse.**

Reaching it requires hand-editing `merge.config`, because `scri setup` hard-codes `"conflict"` (F9). Low-likelihood, high-consequence.

- [ ] **Step 1: Write the failing tests**

Read `tests/Scrinia.Merge.Tests/ConflictDirContainmentTests.cs` first and **match its existing fixture and helper style** — it already has working setup for invoking the handler with a chosen `ConflictDir`. Add two cases to it, using whatever helper the existing `OutOfBoundsConflictDir_Throws` uses:

```csharp
    [Theory]
    [InlineData(".")]
    [InlineData("")]
    public void DegenerateConflictDir_Throws_AndWritesNoGitignore(string conflictDir)
    {
        // "." and "" both resolve Path.Combine(dirFull, x) back to dirFull. IsWithin treated
        // equality as containment, so the guard passed and the driver wrote a .gitignore of "*"
        // into the artifact directory — silently untracking every memory at and below it.
        // Asserting the absence matters more than the throw: the .gitignore write is the harm.
```

The assertion must cover **both** halves:
1. The handler throws `InvalidOperationException` whose message names `ConflictDir`.
2. **No `.gitignore` exists in the artifact directory afterwards** — and if the test creates one beforehand with known content, that content is unchanged. Assert the file content, not just its existence, so a clobber is caught as well as a create.

Also confirm no `current/` or `incoming/` directory was created.

- [ ] **Step 2: Run to verify they fail**

```bash
rtk dotnet test tests/Scrinia.Merge.Tests --filter "FullyQualifiedName~ConflictDirContainment"
```

Expected: the two new cases FAIL — no throw, and a `.gitignore` containing `*` present in the artifact directory. **If they pass before the fix, the test is not reaching the code path** — say so rather than proceeding.

- [ ] **Step 3: Make the helper strict**

Replace `Nmp2ConflictHandler.cs:116-124`. The helper has exactly one call site (verified), so tightening it in place is safe and clearer than adding a third clause at the call site:

```csharp
    /// <summary>
    /// True when <paramref name="candidateFull"/> is strictly below <paramref name="baseFull"/>.
    ///
    /// <para>Equality returns FALSE, deliberately. A ConflictDir of "." or "" resolves back to the
    /// artifact directory, and the caller writes a .gitignore of "*" into whatever this approves —
    /// so treating "the directory itself" as an acceptable conflict directory silently untracks
    /// every memory at and below it. The separate Scrinia.Core.PathContainment.IsWithin keeps its
    /// equality case; this one is private to this class and has a single caller.</para>
    /// </summary>
    private static bool IsStrictlyWithin(string baseFull, string candidateFull)
    {
```

Delete the equality branch that returned `true`, keep the rest of the body unchanged, and update the single call at `:31` to `IsStrictlyWithin`.

**Do not touch `src/Scrinia.Core/PathContainment.cs`.** It is a different helper in a different assembly with six unrelated callers.

- [ ] **Step 4: Run the tests**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test tests/Scrinia.Merge.Tests
```

Expected: 0 build errors; the new cases pass; **`RelativeConflictDir_IsAllowed` still passes** — that is the guard against over-tightening. Report if any pre-existing test in this file changed status.

- [ ] **Step 5: Commit**

```bash
rtk git add src/Scrinia.Merge/Nmp2ConflictHandler.cs tests/Scrinia.Merge.Tests/ConflictDirContainmentTests.cs
rtk git commit -m "fix(merge): a ConflictDir of '.' silently untracked the whole workspace"
```

---

### Task 2: §2 + §3 — the legacy branch gets exception cover and stops calling success a failure

**Files:**
- Modify: `src/Scrinia.Merge/Program.cs:13-35`, `:37`
- Test: `tests/Scrinia.Merge.Tests/` (new or existing file — see Step 1)

**Interfaces:** none — `Program.cs` is a top-level-statements entry point.

**Why these are one task.** Both live in the same 23-line block and are on the same failure path. Splitting them means two passes over the same code.

**§2:** `GitConfigRepair.TryGit`'s catch filter (`GitConfigRepair.cs:73`) covers only `Win32Exception` and `InvalidOperationException`. An `IOException` from `proc.StandardOutput.ReadToEnd()` (`:68`) escapes uncaught. The legacy branch sits at `Program.cs:13-35`, **above** the `try` at `:37`, so that exception reaches git as a raw .NET stack trace inside its merge output — precisely what the top-level handler at `:59-67` exists to prevent, on the one path that makes *two* git invocations instead of one.

**§3:** `TryAppendPathname` returns `false` with the message `could not read merge.scrinia-{kind}.driver from local git config` when the key is simply **absent** — because `git config --get` exits 1 on a missing key with empty stderr (F4). `Program.cs:29` interpolates **both** keys' messages unconditionally, so a workspace that registered only `nmp2` is told that `meta` "could not be read" even when the `nmp2` repair succeeded. Per F4 the appended detail cannot distinguish missing from failed, so **the fix is the phrasing, not better detection.**

- [ ] **Step 1: Write the failing tests**

Look for an existing test file covering `Program`/the four-argument path (`rtk proxy grep -rln "args.Length\|without %P" --include=*.cs tests/Scrinia.Merge.Tests/`). Extend it if one exists; otherwise create `tests/Scrinia.Merge.Tests/LegacyInvocationTests.cs`.

Two behaviours to pin:

1. **§3 — no error framing when the invoked key succeeded.** Invoke the four-argument path with `kind = "nmp2"` in a repo where `merge.scrinia-nmp2.driver` is registered but `merge.scrinia-meta.driver` is not. Assert stderr does **not** contain `[scrinia:error]`, and does **not** describe the meta key with failure language (`could not read`).
2. **§2 — a diagnostic, not a stack trace.** Drive the four-argument path so that a git invocation fails in a way that escapes `TryGit`'s filter, and assert stderr contains `[scrinia:error]` and does **not** contain `at Scrinia.Merge.` or `System.IO.IOException:`.

For (2), the honest constraint: `TryGit` shells out to a real `git`, so forcing an `IOException` from stdout is awkward from a test. **If you cannot drive (2) deterministically without restructuring production code beyond §2's scope, say so explicitly in your report and pin (1) plus a direct assertion that the legacy branch is inside the try** — e.g. by confirming via the source that no `return` precedes the `try`. Do not write a test that passes regardless; a test that cannot fail is worse than no test, and this branch has already deleted one.

- [ ] **Step 2: Run to verify they fail**

```bash
rtk dotnet test tests/Scrinia.Merge.Tests --filter "FullyQualifiedName~LegacyInvocation"
```

Expected: test (1) FAILS — stderr names the meta key with "could not read" despite the nmp2 repair succeeding.

- [ ] **Step 3: Move the `try` above the legacy branch (§2)**

Move the `try {` from `Program.cs:37` to immediately after the `args.Length < 4` guard ends (i.e. above the `if (args.Length < 5)` at `:13`), so the legacy branch is inside it. The `return 1` statements inside the block remain valid. The `catch` at `:59-67` and the local functions below it stay where they are.

Extend the catch's existing comment to record the new coverage:

```csharp
    // ... so report the message and let git mark the path unmerged. The four-argument
    // legacy branch is inside this try as well: it makes two git invocations, and
    // GitConfigRepair.TryGit's catch filter covers only Win32Exception and
    // InvalidOperationException — an IOException reading git's stdout would otherwise
    // reach git's merge output as a raw stack trace.
```

- [ ] **Step 4: Re-phrase the outcomes (§3)**

Replace `Program.cs:29-33`. Report on the key that was actually invoked; mention the other without failure framing:

```csharp
    string invokedMessage = kind switch
    {
        "meta" => metaMessage,
        "nmp2" => nmp2Message,
        _ => $"unknown driver kind '{kind}'"
    };
    string otherKind = kind == "meta" ? "nmp2" : "meta";
    string otherMessage = kind == "meta" ? nmp2Message : metaMessage;

    // Report the invoked key's outcome. The other key is repaired too — a legacy workspace
    // registered both at four arguments — but `git config --get` exits 1 with empty stderr on a
    // key that was simply never registered, so its message cannot distinguish "absent" from
    // "failed". Stating it as a plain note rather than an error stops a single-driver workspace
    // being told something is broken when nothing is.
    Console.Error.WriteLine(invokedRepaired
        ? $"[scrinia] merge driver invoked without %P — {invokedMessage}; " +
          $"also attempted {otherKind}: {otherMessage}; re-run your merge"
        : $"[scrinia:error] merge driver invoked without %P — {invokedMessage}. " +
          "Re-run: scri setup --multi-user");
```

- [ ] **Step 5: Build and test**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test tests/Scrinia.Merge.Tests
```

Expected: 0 errors, all pass. Report any pre-existing test whose expected stderr text this changes — update it and say which.

- [ ] **Step 6: Commit**

```bash
rtk git add src/Scrinia.Merge/Program.cs tests/Scrinia.Merge.Tests/
rtk git commit -m "fix(merge): cover the legacy branch and stop reporting success as failure"
```

---

### Task 3: §4 — delete the write-only `"conflicted"` flag

**Files:**
- Modify: `src/Scrinia.Merge/Nmp2ConflictHandler.cs:126-135` (and its call site)
- Modify: `tests/Scrinia.Merge.Tests/MarkMetaConflictedTests.cs`, `Nmp2ConflictHandlerTests.cs:113-114`, `GitMergeIntegrationTests.cs:91,215`

**Interfaces:** none.

**This is a decision, not a defect fix.** Recorded in "Decisions taken while planning" above. F5 confirms zero production readers repo-wide, including `web/`. `WriteSidecarCore` reserialises `ArtifactEntry` and drops the unknown property, so it cannot corrupt anything — its only live effect is a spurious committed diff on every conflicted merge that nothing can act on.

- [ ] **Step 1: Re-confirm there is no reader**

```bash
rtk proxy grep -rn "conflicted" --include=*.cs --include=*.ts --include=*.tsx --include=*.json src/ tests/ web/ | rtk proxy grep -v "conflictId\|conflictedContent\|conflicted file"
```

Every remaining hit must be the writer or a test assertion. **If a production reader appears, STOP and report** — the deletion premise is wrong and the item needs re-deciding rather than re-scoping.

- [ ] **Step 2: Delete the writer and `MarkMetaConflicted` entirely**

Remove `InsertConflictedFlag` (`Nmp2ConflictHandler.cs:126-135`), the whole `MarkMetaConflicted` method (`:102-110`), and its call site at `:51`.

`MarkMetaConflicted`'s full body is:

```csharp
private static void MarkMetaConflicted(string nmp2Path)
{
    string metaPath = Path.ChangeExtension(nmp2Path, ".meta.json");
    if (!File.Exists(metaPath))
        return;

    string meta = File.ReadAllText(metaPath);
    File.WriteAllText(metaPath, InsertConflictedFlag(meta));
}
```

It technically does work beyond the call — path resolution, an existence guard, a read, a write — but **every line of it exists to support the flag write**. Strip only `InsertConflictedFlag` and what remains reads a file and writes the identical bytes back: a no-op that still dirties the file's mtime. Delete the whole thing. (An earlier draft of this step said "if it does other work, keep it"; followed literally that produces exactly that no-op, which the verification pass caught.)

- [ ] **Step 3: Remove the assertions**

Delete the `"conflicted"` assertions. Confirmed locations:

- `tests/Scrinia.Merge.Tests/MarkMetaConflictedTests.cs` — likely exists solely for this flag; if every test in it asserts the flag, delete the file.
- `tests/Scrinia.Merge.Tests/Nmp2ConflictHandlerTests.cs:113-114`
- `tests/Scrinia.Merge.Tests/GitMergeIntegrationTests.cs:91` and `:215` — both are `.Should().Contain("\"conflicted\": true", ...)`.

**`GitMergeIntegrationTests.cs:109` is NOT a flag assertion.** It is `result.ExitCode.Should().NotBe(0, because: "a conflicted memory must stop the merge rather than discard one side")` — an exit-code assertion whose `because` string merely contains the word in prose. An earlier draft of this plan listed it; **leave it alone.**

Delete only the flag assertions, not their enclosing tests, except where a test exists solely to assert the flag. Where an integration test asserts the flag alongside other behaviour, the surrounding assertions must survive untouched. Report what you removed and why.

- [ ] **Step 4: Build and run the full suite**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test Scrinia.sln
```

Expected: 0 errors, all pass (the total drops by however many whole tests you removed — state the number).

- [ ] **Step 5: Commit**

```bash
rtk git add src/Scrinia.Merge/Nmp2ConflictHandler.cs tests/Scrinia.Merge.Tests/
rtk git commit -m "refactor(merge): delete the conflicted flag nothing ever read"
```

---

### Task 4: §5 — pin the `merge.config` contract through the real writer and the real reader

**Files:**
- Modify: `src/Scrinia/Commands/ScriniaCommands.Setup.cs:348-355` (extract the writer)
- Modify: `tests/Scrinia.Tests/Scrinia.Tests.csproj` (add one `ProjectReference`)
- Create: `tests/Scrinia.Tests/MergeConfigRoundTripTests.cs`

**Interfaces:**
- Produces: `internal static string SerializeMergeConfig(double jaccardThreshold, string resolver, string conflictDir)` on `ScriniaCommands`, returning the exact JSON text `scri setup` writes. Task 5 and 6 do not consume it.

**Why this is the highest-value item in the spec.** The contract is declared **twice, as two different types in two assemblies**, with two independent camelCase policies and nothing pinning them:

- Writer: `internal sealed record MergeConfig` — `src/Scrinia/Commands/CliJsonContext.cs:28-31`, serialised via `CliJsonContext.Default.MergeConfig`
- Reader: `public sealed record MergeConfig` — `src/Scrinia.Merge/MergeConfig.cs:6-32`, loaded via `MergeConfig.Load(dir)`

That exact divergence **was** the camelCase bug found during the original branch, and that branch's final reviewer called this the highest-value follow-up. F8a is the precise gap: `MergeConfigTests.cs` is not a weak round-trip test, it is a **reader test with hand-built input** — it would have passed under the original bug, by construction.

**A test that builds the JSON by hand pins nothing.** It must go through the real writer.

- [ ] **Step 1: Extract the writer**

`ScriniaCommands.Setup.cs:351-354` currently serialises inline. Extract it so a test can call the same code:

```csharp
    /// <summary>
    /// Produces the exact merge.config JSON that `scri setup --multi-user` writes. Extracted so a
    /// test can drive the real writer: the contract is declared twice — here and as
    /// Scrinia.Merge.MergeConfig — in two assemblies with two independent camelCase policies, and
    /// a divergence between them is a shipped bug that no reader-side test can see.
    /// </summary>
    internal static string SerializeMergeConfig(double jaccardThreshold, string resolver, string conflictDir) =>
        JsonSerializer.Serialize(
            new MergeConfig(JaccardThreshold: jaccardThreshold, Resolver: resolver, ConflictDir: conflictDir),
            CliJsonContext.Default.MergeConfig);
```

Then rewrite `:351-354` to call it, preserving the trailing newline exactly:

```csharp
        string mergeConfigJson = SerializeMergeConfig(0.7, resolverValue, "conflict");
        File.WriteAllText(mergeConfigPath, mergeConfigJson + "\n");
```

Behaviour must be byte-identical. Put the method next to the other helpers in the file.

- [ ] **Step 2: Wire the test project**

Add to `tests/Scrinia.Tests/Scrinia.Tests.csproj`, beside the existing `ProjectReference` entries:

```xml
    <ProjectReference Include="..\..\src\Scrinia.Merge\Scrinia.Merge.csproj" />
```

This direction is deliberate: `Scrinia.Merge` has **zero** project references, so this drags in nothing. The reverse (`Scrinia.Merge.Tests` → `Scrinia`) would make the merge tests build the whole CLI and would need a new `InternalsVisibleTo`. `src/Scrinia/Scrinia.csproj:30` already grants `InternalsVisibleTo Include="Scrinia.Tests"`, so the internal writer type is **already** visible — **add no new `InternalsVisibleTo`.**

**Do not add any `ProjectReference` to `src/Scrinia.Merge/Scrinia.Merge.csproj` itself.**

- [ ] **Step 3: Write the failing test**

Create `tests/Scrinia.Tests/MergeConfigRoundTripTests.cs`. Note both assemblies declare a type named `MergeConfig`, so **alias them** — an unqualified `MergeConfig` will not compile:

```csharp
using FluentAssertions;
using Scrinia.Commands;
using ReaderConfig = Scrinia.Merge.MergeConfig;

namespace Scrinia.Tests;

/// <summary>
/// The merge.config contract is declared twice — Scrinia's internal writer record and
/// Scrinia.Merge's public reader record — in two assemblies, each with its own camelCase policy
/// and nothing pinning them together. That divergence was a real shipped bug. MergeConfigTests
/// in Scrinia.Merge.Tests cannot catch it: it feeds MergeConfig.Load hand-built JSON, so it would
/// have passed under the bug. This test drives the REAL writer into the REAL reader.
/// </summary>
public class MergeConfigRoundTripTests
{
    [Fact]
    public void RealWriterOutput_LoadsThroughRealReader_WithEveryFieldIntact()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-mergecfg-" + Guid.NewGuid().ToString("N"));
        string scriniaDir = Path.Combine(root, ".scrinia");
        Directory.CreateDirectory(scriniaDir);
        try
        {
            // Values deliberately differ from every default, so a reader that silently falls back
            // to its own defaults fails instead of coincidentally matching.
            string json = ScriniaCommands.SerializeMergeConfig(0.42, "ollama", "merge-conflicts");
            File.WriteAllText(Path.Combine(scriniaDir, "merge.config"), json + "\n");

            ReaderConfig loaded = ReaderConfig.Load(scriniaDir);

            loaded.JaccardThreshold.Should().Be(0.42);
            loaded.Resolver.Should().Be("ollama");
            loaded.ConflictDir.Should().Be("merge-conflicts");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The values `scri setup --multi-user` actually writes are ALSO every one of the reader's
    /// fallback defaults (JaccardThreshold=0.7, Resolver="none", ConflictDir="conflict"), so
    /// asserting the loaded values for that case would pass even if Load threw and returned
    /// Default. This asserts the writer's JSON property names instead — the actual contract, and
    /// the thing the camelCase bug broke.
    /// </summary>
    [Fact]
    public void RealWriterEmitsThePropertyNamesTheReaderBindsTo()
    {
        string json = ScriniaCommands.SerializeMergeConfig(0.7, "none", "conflict");

        json.Should().Contain("jaccardThreshold");
        json.Should().Contain("resolver");
        json.Should().Contain("conflictDir");
    }
}
```

**Adjust the expected casing to whatever `CliJsonContext`'s policy actually produces** — read the context's `JsonSourceGenerationOptions` and the reader's binding first, then assert what both sides genuinely agree on. If the writer emits PascalCase and the reader binds case-insensitively, say so in your report: that is a live divergence the first test would catch and this one documents.

Note the reader has a fourth property, `ResolverCommand`, that the writer cannot emit at all. Do not assert on it — it is out of scope, recorded in the fact base.

If `ScriniaCommands` is not the right receiver for `SerializeMergeConfig` (e.g. it is a static helper elsewhere in the file), adjust the call to match what Step 1 produced and note the change.

- [ ] **Step 4: Prove the test can fail — this is the point of the task**

A round-trip test that cannot detect writer drift is worth nothing. Run the negative control:

Temporarily change the writer's JSON naming policy in `src/Scrinia/Commands/CliJsonContext.cs` (e.g. add or remove `JsonSourceGenerationOptions(PropertyNamingPolicy = ...)`, or rename a `[JsonPropertyName]`) so the two sides disagree — reproducing the original camelCase bug. Re-run:

```bash
rtk dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~MergeConfigRoundTrip"
```

Expected: **FAIL** — the reader falls back to defaults or throws. Then revert the change exactly and re-run; expected PASS.

**Report both results.** If the test still passes with the writer's policy broken, it is not pinning the contract — say so and fix it before moving on.

- [ ] **Step 5: Build and run the full suite**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test Scrinia.sln
```

Expected: 0 errors, all pass. Confirm `Scrinia.Merge.csproj` still has no `ProjectReference`.

- [ ] **Step 6: Commit**

```bash
rtk git add src/Scrinia/Commands/ScriniaCommands.Setup.cs tests/Scrinia.Tests/Scrinia.Tests.csproj tests/Scrinia.Tests/MergeConfigRoundTripTests.cs
rtk git commit -m "test(merge): pin merge.config through the real writer and the real reader"
```

---

### Task 5: §6 + §7 — the harness stops picking by mtime, and the conflict scan gets side coverage

**Files:**
- Modify: `tests/Scrinia.Merge.Tests/GitMergeHarness.cs:44-47`
- Test: conflict-scan coverage for `MemoryTools.Lifecycle.cs:250-251` — find the right home (see Step 3)

**Interfaces:** none.

**§6:** `LocateDriver` selects the driver binary by `OrderByDescending(File.GetLastWriteTimeUtc)`. A stale Release build can therefore be exercised against a Debug test run, and the failure would read as a driver bug rather than a harness bug — the worst kind of false signal. Select by the configuration the **test assembly** was built in.

**§7:** `ScanForConflicts` passes `"current"` and `"incoming"` at `MemoryTools.Lifecycle.cs:250-251`. Nothing tests them. A swapped pair ships undetected and produces a conflict report with the two sides transposed — **which reads as plausible output, not as an error.** That is why the test must distinguish the sides **by content**, not merely assert both are non-empty.

- [ ] **Step 1: Fix `LocateDriver` (§6)**

Read `tests/Scrinia.Merge.Tests/GitMergeHarness.cs:30-60` for the full method. Replace the mtime ordering with configuration-based selection. The configuration the test assembly was built in is available at compile time:

```csharp
#if DEBUG
        const string Configuration = "Debug";
#else
        const string Configuration = "Release";
#endif
```

Filter candidate paths to that configuration instead of ordering by write time. **If no candidate matches, throw with a message naming the configuration and the paths searched** — a harness that silently picks the wrong binary is the defect being fixed, so failing loudly is required. Do not fall back to mtime.

- [ ] **Step 2: Verify §6 by running the merge tests**

```bash
rtk dotnet test tests/Scrinia.Merge.Tests
```

Expected: all pass. Then confirm the selection is real: report the path `LocateDriver` resolves to and that it contains `/Debug/` (test runs default to Debug).

- [ ] **Step 3: Find the home for the §7 test**

`ScanForConflicts` lives in `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`. Locate existing coverage:

```bash
rtk proxy grep -rln "ScanForConflicts\|TryReadConflictCopy" --include=*.cs tests/
```

Extend the file that already exercises conflict scanning if one exists; otherwise create `tests/Scrinia.Tests/ConflictScanSideTests.cs`. **Report which you chose.**

- [ ] **Step 4: Write the failing test (§7)**

Construct a workspace with a conflicted artifact whose `conflict/current/` and `conflict/incoming/` copies contain **distinguishable** content — e.g. current holds `OURS-SIDE-MARKER`, incoming holds `THEIRS-SIDE-MARKER`. Then assert the scan reports them on the correct sides:

```csharp
    /// <summary>
    /// ScanForConflicts passes "current" and "incoming" to TryReadConflictCopy
    /// (MemoryTools.Lifecycle.cs:250-251). A swapped pair produces a conflict report with the two
    /// sides transposed, which reads as plausible output rather than as an error — so this
    /// distinguishes the sides by content, not by both being non-empty.
    /// </summary>
```

The assertion must fail if the two arguments are swapped. **Verify that by swapping them in the production code, running the test, and confirming it FAILS** — then revert. Report the result; a test that survives the swap pins nothing.

- [ ] **Step 5: Build and run the full suite**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test Scrinia.sln
```

Expected: 0 errors, all pass.

- [ ] **Step 6: Commit**

```bash
rtk git add tests/Scrinia.Merge.Tests/GitMergeHarness.cs tests/
rtk git commit -m "test(merge): select the driver by configuration; pin the conflict-scan sides"
```

---

### Task 6: §8 — the upgrade doc tells the truth, and Group A closes

**Files:**
- Modify: `docs/multi-user-setup.md:52-53` and its upgrade section
- Modify: `docs/superpowers/specs/2026-08-10-residuals-closure.md`
- Modify: `CHANGELOG.md`

**Interfaces:** none.

**Context — F8 is narrower than the spec said.** `docs/multi-user-setup.md:53` **already** says "delete those directories once you are done to keep the workspace tidy". So deletion guidance exists. The genuine gaps are two:

1. That is generic tidiness advice, not an **upgrade-specific untrack instruction**. A pre-release workspace that already *committed* its `conflict/` directories needs `git rm --cached`, not just deletion — and the adjacent upgrade section is what such a reader is following.
2. There is **no freshness caveat anywhere in the file**. A workspace that re-runs `scri setup --multi-user` is told it is fixed, and still resolves from stale committed bytes, because `reconcile()` prefers the conflict copy unconditionally with no freshness check.

Zero risk: a merge driver correctly must not `git rm` unasked, but the doc can say so.

- [ ] **Step 1: Read the document**

Read `docs/multi-user-setup.md` in full, particularly `:45-70` and whatever section addresses upgrading from a pre-release workspace. You are adding to the upgrade section, and adjusting `:52-53` only if it reads as complete when it is not.

- [ ] **Step 2: Add the untrack instruction and the freshness caveat**

In the upgrade section, add guidance that a workspace which already committed `conflict/` directories must **delete and untrack** them — `git rm -r --cached` on the committed directories, then commit — because the driver writes a `.gitignore` of `*` into the conflict directory but git keeps tracking files it already tracks.

Then state the caveat plainly: until those committed copies are removed, `reconcile()` prefers the conflict copy unconditionally with no freshness check, so it can resolve from stale pre-upgrade bytes even after `scri setup --multi-user` reports success.

Write it as fact, not as a warning about hypothetical future work. **No "for now", no "TODO".**

- [ ] **Step 3: CHANGELOG — correct the stale entry first, then add the new one**

**First, fix the contradiction.** `CHANGELOG.md:378-381` sits under `## [Unreleased]` and still describes the deleted behaviour as current:

> "A conflicted `.nmp2` now stops the merge... marks the sidecar `"conflicted": true`, and leaves markers for `reconcile()` to find."

Task 3 removed that write. The entry is unshipped, so amending it corrects a draft rather than rewriting history — the same call made on the previous branch. Strike the `marks the sidecar "conflicted": true` clause and leave the rest of the sentence (stopping the merge and leaving markers are both still true — **verify that before editing**).

Found by Task 3's review, not by this plan. Line numbers will have shifted; find it by content.

**Then** add entries under `## [Unreleased]` for the two user-visible changes in this branch:

- The `ConflictDir` containment tightening (Task 1): a `merge.config` whose `ConflictDir` resolves to the artifact directory itself — `"."` or `""` — is now **rejected** rather than accepted. Previously the driver wrote a `.gitignore` of `*` into the artifact directory, silently untracking every memory at and below it. Reaching this required hand-editing `merge.config`, since `scri setup` hard-codes `"conflict"`.
- The `"conflicted"` flag removal (Task 3): the driver no longer writes a `conflicted: true` property into `.meta.json` on a conflicted merge. Nothing read it, and `WriteSidecarCore` dropped it on the next write; its only effect was a spurious diff.

Also mention the legacy-invocation message change from Task 2 if you judge it user-visible — a workspace registered with only one driver key no longer gets an error-framed message. **Verify against the code before writing any claim.** This branch's predecessor shipped a CHANGELOG naming a route that did not exist and another listing commands that could not produce the described behaviour.

- [ ] **Step 4: Close Group A and record the Group G verdict**

In `docs/superpowers/specs/2026-08-10-residuals-closure.md`:

- Mark **Group A** closed, citing this branch's commits (get them with `rtk git log --oneline main..HEAD`) and the plan file `docs/superpowers/plans/2026-08-11-merge-driver-residuals.md`. **Do not cite anything under `.superpowers/`** — that path is gitignored, and a tracked spec pointing at files no clone will contain is the exact defect this sequence has already fixed twice.
- Renumber the closure-order list.
- Under **Group G**, record the first item as **VERIFIED TRUE** with its evidence: `reconcile()`'s resolution path writes with a raw `File.WriteAllText` (`src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:199-212`, meta auto-merge branch at `:125-138`), bypassing `FileMemoryStore`'s write pipeline entirely — so `originalBytes`, `chunkCount`, `contentPreview` and `keywords` keep their pre-merge values and no reindex is triggered. Note that it is **deliberately out of scope here** (a behaviour change to the resolution path needs its own design, per the merge-driver spec's Out of Scope section) and that it becomes **one specced item**, not a `/findings/` chunk.
- Leave Groups B, C, E2–E5 and the remaining two Group G items exactly as they are.

- [ ] **Step 5: Verify and commit**

```bash
rtk proxy grep -n "\.superpowers/" docs/superpowers/specs/2026-08-10-residuals-closure.md
```

Expected: no output.

```bash
rtk git add docs/multi-user-setup.md docs/superpowers/specs/2026-08-10-residuals-closure.md CHANGELOG.md
rtk git commit -m "docs: upgrade note tells the truth; Group A closes"
```

---

## Verification pass — RUN 2026-08-11

The spec's facts were re-verified at `5e780fa` (results in the Fact base). A second pass then falsified **this plan's own** claims. Five corrections were folded in; recorded here so a reviewer sees what the plan got wrong.

**Corrected:**

1. **The reader's `MergeConfig` has four properties, not three.** `ResolverCommand` (`string?`, default `null`) exists only on the reader; the CLI writer cannot emit it. A second divergence in the very contract §5 exists to pin, previously unrecorded. Fact base updated; Task 4 told not to assert on it.
2. **Task 4's second test asserted nothing.** `SetupDefaults_SurviveTheRoundTrip` used `0.7 / "none" / "conflict"` — which are *exactly* the reader's fallback defaults, and `Load` returns `Default` on any exception. It would have passed with the parser completely broken. Replaced with a test that asserts the writer's emitted property names.
3. **Task 3's instruction would have left dead code.** `MarkMetaConflicted` does have a body beyond the call, so "if it does other work, keep it" would produce a method that reads a file and writes identical bytes back. Now: delete it entirely, with the body quoted so the implementer can see why.
4. **`GitMergeIntegrationTests.cs:109` is not a flag assertion** — it is an exit-code assertion whose `because` string contains the word "conflicted" in prose. The plan listed it for deletion. Removed from the list, with an explicit warning.
5. **Task 4's structural assumption has live precedent**, which the plan did not cite: `tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj` **already** references the same AOT/`Exe` `Scrinia.Merge` project. Referencing it from a test project is proven, not theoretical.

**Confirmed as stated:** `Nmp2ConflictHandler.IsWithin` is private with exactly one call site and is fully independent of `Scrinia.Core.PathContainment.IsWithin` (6 unrelated callers); `Scrinia.csproj:30` grants `InternalsVisibleTo` to `Scrinia.Tests`, and no cycle is possible since neither `Scrinia` nor `Scrinia.Core` references `Scrinia.Merge`; `PublishAot`/`PublishTrimmed`/`SelfContained` are publish-only and do not affect `dotnet test`; `MergeConfig.Load` takes a directory; `LocateDriver`'s candidates do carry `Debug`/`Release` segments (with the nuance that Release has three variants — `native/`, `publish/`, plain — while Debug has one); **no existing test asserts the stderr text Task 2 rewrites**; `ConflictDirContainmentTests.cs` calls `Nmp2ConflictHandler.Handle(...)` directly with a hand-built `MergeConfig`, no git harness; `ConfigureMultiUser` is `private static void` inside `public partial class ScriniaCommands` with `JsonSerializer`/`CliJsonContext` already in scope, so the Task 4 extraction is straightforward.

**Useful context the pass surfaced:** `GitMergeIntegrationTests.cs:121-161` already covers the four-argument legacy path with two full git-driven integration tests — Task 2 should extend there rather than starting cold. And `tests/Scrinia.Tests/ReconcileScanTests.cs` exercises `ScanForConflicts`, but only for an unrelated "scan must not write" regression; it does not touch the current/incoming side question, so Task 5's §7 coverage is genuinely new.
