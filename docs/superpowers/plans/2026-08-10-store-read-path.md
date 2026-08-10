# Store Read Path Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Answer the aliasing-safety question that has blocked `LoadIndex` across three branches, then act on the answer — take the unnecessary syscall off the cache-hit read path and make `SearchQuery.Candidates` unable to change a search's answer.

**Architecture:** `FileMemoryStore.LoadIndex` acquires a cross-process shared file lock *before* consulting its in-memory `_indexCache`, so every call opens an OS file handle even when no I/O follows. Task 1 establishes what that lock protects and whether the cache's backing list can safely be aliased out; Tasks 2–4 apply what it finds. Task 5 runs only if Task 1 clears it.

**Tech Stack:** C# / .NET 10, xunit 2.9.3, FluentAssertions 6.12.2, BenchmarkDotNet 0.15.4. No mocking frameworks — test doubles are hand-written classes.

**Source spec:** `docs/superpowers/specs/2026-08-08-store-read-path-and-sweep-design.md`, **Part A only**.

## Scope: this plan is Part A of that spec

The spec covers two halves and says so explicitly: *"These could reasonably be two branches."* This plan takes **Part A (the read path)** and leaves **Part B (the sweep)** to its own plan and its own branch. Reasons:

- Part A is the work that gates residuals spec 1 (`remote-write-correctness`), which is why spec 4 is being executed now at all. Part B gates nothing.
- Part A is the only item in any of the four specs where a wrong fix produces a **data race** rather than a visible failure. Part B is mechanical.
- Part B's formatting fix moves every golden file. The spec's own risk section says that diff *"must not be bundled with anything else — a golden-wide diff hides whatever else is in the commit."* The same reasoning argues against reviewing a locking change in the same branch.

Part B is not being dropped. It gets `docs/superpowers/plans/2026-08-10-store-sweep.md` after this branch closes.

## Global Constraints

- **Never `git add .` or `git add -A`.** `cmds.txt` is untracked, unignored, pre-existing, and is not part of this or any work. Stage explicit paths only.
- **Nothing is pushed.** Local commits and a local merge only.
- Prefix every shell command with `rtk`, including inside `&&` chains.
- **`rtk err` is not a build gate.** Use `rtk dotnet build` and read its summary, or `rtk proxy dotnet build` and grep for `error CS` yourself.
- `TreatWarningsAsErrors=true` for projects not ending `.Tests`/`.Benchmarks` (`Directory.Build.props:44-46`). Test projects carry ~1723 pre-existing analyzer warnings — **warning count is not a health signal; the gate is 0 errors.**
- `tests/Scrinia.Tests/GlobalUsings.cs` declares `global using Xunit;` — do **not** add `using Xunit;` to a new file there. `tests/Scrinia.Server.Tests` has no `GlobalUsings.cs` and does need it.
- **Test-harness shapes** (verified, and not what you would guess): `TestHelpers.LocalStore` (`tests/Scrinia.Tests/TestHelpers.cs:83`) returns **`ILocalMemoryStore`**, not `FileMemoryStore` — deliberately, to avoid ~20 inline casts. Every member this plan calls on it is on that interface or `IMemoryStore`, so no cast is needed: `GetStoreDirForScope` and `WorkspaceRoot` are on `ILocalMemoryStore` (`:21`, `:30`); `LoadIndex`, `Upsert`, `ListScoped(string?, string?)`, `SearchAll(SearchQuery)` are on `IMemoryStore` (`:20`, `:21`, `:79`, `:68`). `TestHelpers.StoreScope` (`:96`) is `IDisposable` with `WorkspaceDir`/`TempDir` — there is **no** `StoreScope.WorkspaceRoot`; reach the root through the store.
- Full-suite baseline at the branch point: **1780 passed, 0 failed.**

---

## Established facts

Verified at `ef3221d` on 2026-08-10 by the commands/reads named in each row. The spec's own line numbers are older than this and several have moved — **these supersede them.**

| # | Fact | Evidence |
|---|---|---|
| F1 | `LoadIndex` acquires `FileLock.AcquireShared(GetLockPath(scope))` unconditionally, then the in-process read lock, then calls `LoadIndexUnsafe` | `src/Scrinia.Core/FileMemoryStore.cs:574-587` |
| F2 | The cache check is `LoadIndexUnsafe`'s **first** statement and returns `cached.Entries.ToList()` — a defensive copy | `src/Scrinia.Core/FileMemoryStore.cs:589-593` |
| F3 | `GetLockPath` = `Path.Combine(GetStoreDirForScope(scope), ".lock")`. `FileLock.AcquireShared` returns a **lock over nothing** when that directory does not exist; otherwise `Acquire` runs `Directory.CreateDirectory` and opens `.lock` with `FileMode.OpenOrCreate` | `FileMemoryStore.cs:537-538`; `FileLock.cs:40-47`, `:49-51`, `:62` |
| F4 | `CachedIndex.Entries` is assigned **by reference**, not copied | `FileMemoryStore.cs:71-75` (`Entries = entries;`) |
| F5 | `CachedIndex.NameToPosition`, `CachedIndex.Rebuild()` and `CachedIndex.GetOrComputeCorpusStats()` have **zero readers anywhere in the repo** — only `Entries` is consumed, at `:593` | repo-wide Grep for `NameToPosition\|GetOrComputeCorpusStats\|\.Rebuild\(\)` returned only the definitions plus `docs/architecture/core.md:437-445` |
| F6 | Every cache write **replaces** the `CachedIndex` object; nothing mutates an existing `Entries` list in place | assignments at `FileMemoryStore.cs:630, 903, 930, 958, 1001`; paired invalidations at `:403` (`Clear`), `:727, 928, 956, 999` (`TryRemove`). Those 11 lines are every `_indexCache` reference in the file; the only **read** is `:592` |
| F7 | `SaveIndexUnsafe` caches the **caller's own list** without copying: `_indexCache[scope] = new CachedIndex(entries)` | `FileMemoryStore.cs:903` with F4 |
| F8 | `SaveIndex` has exactly **two** occurrences in `src/`: its own declaration and one call, from `ImportTopicEntries` | Grep `SaveIndex\(` over `src/` |
| F9 | `ImportTopicEntries` mutates the list `LoadIndex` returned (`existingEntries[idx] = …` at `:1616`, `.Add(…)` at `:1618`, `.FindIndex` at `:1614`) then passes it to `SaveIndex` at `:1621` | `FileMemoryStore.cs:1588-1622` |
| F10 | `Upsert` (`:912`) and `Remove` (`:940`) do **not** call `LoadIndex` — they call `LoadEntriesFromSidecars` directly and replace the cache | `FileMemoryStore.cs:912-971` |
| F11 | `LoadIndex(` appears **23 times across 9 `src/` files** (including the `IMemoryStore` declaration at `IMemoryStore.cs:20` and the `FileMemoryStore` definition at `:574`) | Grep `LoadIndex\(` over `src/` |
| F12 | `SearchPipeline.ExecuteAsync` is the **only** production setter of `Candidates`; `HttpMemoryStore` explicitly ignores it | `Search/SearchPipeline.cs:76`; `HttpMemoryStore.cs:329` (comment) |
| F13 | `FileMemoryStore.SearchAll` resolves `query.Candidates ?? ListScoped(query.Scopes, query.ExcludeTopics)` with no filter applied to a supplied list | `FileMemoryStore.cs:1283-1284` |
| F14 | `ListScoped(scopes, excludeTopics)` yields candidates whose `Scope` is in `ResolveReadScopes(scopes, excludeTopics)`, plus `ListEphemeral()` when `scopes` is blank or names `ephemeral` | `FileMemoryStore.cs:1242-1265` |
| F15 | The ephemeral scope is the literal string `"ephemeral"`; there is no shared constant | `FileMemoryStore.cs:365`, `:1254`; `Search/SearchResultFilter.cs:24` |

### Corrections to the spec

- **The spec's F5 ("exactly one production site mutates the returned list") is confirmed but for a narrower reason than stated.** It is not that the other callers were audited and found read-only — it is that `SaveIndex` has exactly one caller in `src/` (F8), and that caller is the mutating one (F9). Task 1 still owes the full caller audit; F8 only bounds the *persisting* mutators, not the transient ones.
- **The spec's F6 is confirmed** (F10 here).
- **The spec's F8 is confirmed** (F12 here).
- **New, and the spec does not have it: F5.** `NameToPosition` is dead. This matters directly — a live name→position map alongside an aliased list would desync the moment a caller mutated the alias. It is dead, so that failure mode does not exist. Task 1 must not assume this away; it must re-derive it, because it is the single fact doing the most work in the analysis.
- **New, and the spec does not have it: F7.** The cache already aliases a caller-owned list on the **write** path. `ImportTopicEntries` hands `SaveIndex` a list it has been mutating, and `SaveIndex` stores that exact instance as the cache. Today nothing mutates it after the call returns, so it is latent rather than live — but it is a second aliasing direction, and any conclusion Task 1 reaches about the read direction has to hold here too or it is incomplete.
- **`docs/architecture/core.md:437-445` claims `NameToPosition` "enables O(1) upsert and remove without scanning."** That is false on two counts: the member is dead (F5), and `Upsert`/`Remove` rebuild from sidecars (F10). Task 6 corrects it.

---

## Task 1: The aliasing-safety and lock-necessity analysis

**This task produces no code.** Its output is a written finding, and its conclusions gate Tasks 2 and 5. The spec is explicit: *"If the answer is 'not safe', the correct result of this branch is a written finding and no code change — and that is a success, not a failure."*

Take that at face value. Do not arrive at "safe" because the plan has later tasks. The reviewer for this task is instructed to reject a finding that reads as though the conclusion was fixed in advance.

**Files:**
- Create: `docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md`

**Interfaces:**
- Produces: a document with two verdicts, each one of `SAFE` / `UNSAFE` / `SAFE WITH CONDITIONS`:
  - **Verdict A (aliasing):** may `LoadIndex` return a live alias into `_indexCache`'s backing list instead of `cached.Entries.ToList()`?
  - **Verdict B (lock):** what does `FileLock.AcquireShared` protect on the **cache-hit** path specifically, and may it be skipped there?
- Task 2 consumes Verdict B. Task 5 consumes Verdict A.

### Why this has stalled three times

The search-contract branch originally recorded "roughly ten mutating callers" of `LoadIndex`, asserted from a variable name (`existingEntries`) rather than traced, then reversed itself once traced. That reversal is why this spec exists rather than the change simply having been made. **Trace every call site. Do not classify one from its variable name.**

- [ ] **Step 1: Enumerate every `LoadIndex` call site in `src/`**

Run:

```bash
rtk grep -rn "LoadIndex(" src/
```

Expected: 23 lines across 9 files (F11). If the count differs, the tree has moved since this plan was written — record the actual list and continue with it.

The 21 non-declaration sites to classify:

| File | Lines |
|---|---|
| `src/Scrinia/Services/EntityTypingEventHandler.cs` | 38 |
| `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` | 574, 710, 722 |
| `src/Scrinia.Mcp/MemoryTools.Core.cs` | 319, 466, 1123 |
| `src/Scrinia.Server/Services/MemoryOrchestrator.cs` | 106, 259 |
| `src/Scrinia.Core/Bundles/BundleFormatService.cs` | 56 |
| `src/Scrinia.Core/FileMemoryStore.cs` | 1110, 1261, 1415, 1443, 1559, 1577, 1594 |
| `src/Scrinia.Core/MaintenanceEventSink.cs` | 56, 83 |
| `src/Scrinia.Core/IMemoryStore.cs` | 35 (default `TryUpdateEntry` body) |
| `src/Scrinia/HttpMemoryStore.cs` | 253 (returns `[]`; an implementation, not a caller) |

- [ ] **Step 2: Classify each site as READ-ONLY or MUTATING**

For each, read enough surrounding code to see what happens to the returned list. A site is **MUTATING** if it calls any of `Add`, `AddRange`, `Remove`, `RemoveAt`, `RemoveAll`, `Insert`, `Clear`, `Sort`, `Reverse`, or an indexer *set* on the returned instance — or if it passes the instance somewhere that does.

LINQ that returns a new sequence (`Select`, `Where`, `FirstOrDefault`, `Any`, `OrderBy`, `ToList`, `ToArray`) is READ-ONLY. `List<T>.FindIndex` is READ-ONLY. `foreach` is READ-ONLY.

Record the verdict per site in a table with the one-line reason and the specific expression that decided it.

Two you can expect but must still verify yourself:
- `FileMemoryStore.cs:1594` (`ImportTopicEntries`) — MUTATING, per F9.
- `FileMemoryStore.cs:1110` and `:1261` (`ListScoped`) — `foreach (var entry in LoadIndex(scope))`, READ-ONLY.

- [ ] **Step 3: Answer Verdict A**

The question is not "does any caller mutate today" — one does (F9). It is: **if `LoadIndex` returned a live alias and the one mutating caller were given an explicit `.ToList()`, what would break?**

Address each of these explicitly. They are the ones that actually decide it:

1. **Does anything read a derived structure that would desync?** Per F5, `NameToPosition` has no readers and `Rebuild()` is never called. Re-derive this with your own grep — if it is wrong, Verdict A is almost certainly UNSAFE, because a caller mutating the alias would leave the map stale with nothing to rebuild it.
2. **Does anything mutate `Entries` in place after caching?** Per F6, every cache write constructs a new `CachedIndex`. Re-derive.
3. **The write direction (F7).** `SaveIndexUnsafe` already caches the caller's list by reference. Does `ImportTopicEntries` — or anything else reachable from `SaveIndex` — touch that list after the call returns? If the read direction is made an alias too, `ImportTopicEntries` would be reading an alias, mutating it, and handing the same instance back to be cached. Walk that sequence concretely.
4. **Concurrency.** Two threads calling `LoadIndex` on the same scope currently get two independent copies. With an alias they get the same `List<T>` instance. `List<T>` is not thread-safe for concurrent read/write, and enumeration throws `InvalidOperationException` if the list is structurally modified mid-enumeration. Given the answers above, can two threads ever hold the same instance while one mutates?
5. **Test doubles.** `tests/` has 141 `LoadIndex(` occurrences. Do any implement `IMemoryStore` and return a field the test then mutates? A test double that hands out its own backing list is not a correctness problem for production, but it decides how much of Task 5's diff is mechanical.

- [ ] **Step 4: Answer Verdict B**

Establish what the shared file lock buys, then what it buys **on a cache hit specifically**. The two are different questions and the second is the one that matters.

Facts to reason from, each re-derived:
- `FileLock.AcquireExclusive` opens with `FileShare.None` (`FileLock.cs:32`); `AcquireShared` with `FileAccess.Read, FileShare.Read` (`:46`). So a shared reader blocks while any writer — in this process or another — holds the exclusive lock.
- There is **no cross-process cache invalidation of `_indexCache` at all.** Confirm this. If it holds, then a cache hit already returns data that may be arbitrarily stale relative to another process's committed write, and it does so *whether or not* the lock was taken. The lock cannot be protecting the freshness of a cache hit, because the cache hit is not fresh either way.
- The remaining candidate protection is torn reads of sidecar files — which only applies on the **cold** path, where `LoadEntriesFromSidecars` actually touches disk.
- **Side effect check (this is the one most likely to be missed):** `GetLockPath` calls `GetStoreDirForScope` (F3), which per the comment at `FileMemoryStore.cs:667-670` gates on `MemoriesRoot` and can trigger the layout-migration latch. If the cache-hit path stops calling `GetLockPath`, that trigger stops firing on those calls. Determine whether a cache entry can exist without migration having already been triggered. Every path that populates `_indexCache` (`:630, 903, 930, 958, 1001`) is reached through a `GetStoreDirForScope` call — verify that, and say so explicitly, because it is the argument that makes skipping the lock path safe rather than merely cheap.

- [ ] **Step 5: Write the finding**

Structure:

```markdown
# `LoadIndex` aliasing safety and lock necessity — analysis

**Commit analysed:** <sha>
**Verdict A (aliasing):** SAFE | UNSAFE | SAFE WITH CONDITIONS
**Verdict B (cache-hit lock):** SAFE TO SKIP | UNSAFE TO SKIP | SAFE WITH CONDITIONS

## Call-site classification
<the table from Step 2 — all 21 sites, each with the expression that decided it>

## Verdict A
<the five questions from Step 3, answered, each with file:line evidence>

## Verdict B
<the questions from Step 4, answered, each with file:line evidence>

## What would change the answer
<the specific future edit that would invalidate each verdict — e.g. "any new reader of
NameToPosition", "any cross-process cache invalidation">

## Findings recorded, not acted on
<anything discovered that is out of this branch's scope>
```

The **"What would change the answer"** section is required. A verdict without it is a snapshot that the next person has to redo from scratch — which is exactly how this item reached its third branch.

- [ ] **Step 6: Commit**

```bash
rtk git add docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md
rtk git commit -m "docs(analysis): answer the LoadIndex aliasing and lock questions"
```

---

## Task 2: Move the shared file lock behind the cache check

**Gated by Task 1 Verdict B.** If Verdict B is `UNSAFE TO SKIP`, do not implement this task — record why in the CHANGELOG-free branch summary and move to Task 4.

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:574-587`
- Test: `tests/Scrinia.Tests/LoadIndexCacheHitTests.cs` (create)

**Interfaces:**
- Consumes: Task 1's Verdict B and its reasoning.
- Produces: no signature change. `LoadIndex(string scope = "local")` still returns `List<ArtifactEntry>`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/LoadIndexCacheHitTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

/// <summary>
/// Pins the read path's fast lane: an in-memory cache hit performs no filesystem work at all.
///
/// The observable proxy for "opened a file handle" is the .lock file itself. FileLock.Acquire
/// opens it with FileMode.OpenOrCreate (FileLock.cs:62) after a Directory.CreateDirectory
/// (:51), so any call that reaches the file lock recreates it. Deleting it and finding it
/// still absent afterwards is direct evidence that no handle was opened — which is stronger
/// than timing the call, and deterministic.
/// </summary>
public class LoadIndexCacheHitTests
{
    private static ArtifactEntry Entry(string name) =>
        new(name, $"file:///{name}.nmp2", 10, 1, DateTimeOffset.UtcNow);

    [Fact]
    public void LoadIndex_OnACacheHit_OpensNoLockFile()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.LocalStore;

        // Upsert both writes the sidecar and leaves the scope's cache populated
        // (FileMemoryStore.cs:930), so the very next LoadIndex is a cache hit.
        store.Upsert(Entry("warm"), "local");
        string lockPath = Path.Combine(store.GetStoreDirForScope("local"), ".lock");
        File.Delete(lockPath);

        store.LoadIndex("local").Should().ContainSingle("the cache still answers the read");

        File.Exists(lockPath).Should().BeFalse(
            because: "a cache hit needs no file handle — the shared lock exists to keep a COLD "
                   + "read off a half-written sidecar, and cannot make an in-process cache hit "
                   + "more correct");
    }

    [Fact]
    public void LoadIndex_OnAColdRead_StillTakesTheSharedFileLock()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.LocalStore;
        store.Upsert(Entry("cold"), "local");

        // A second store over the same workspace has an empty _indexCache, so this read is cold
        // and must go through the lock — the fast path must not swallow the lock outright.
        using var fresh = new FileMemoryStore(store.WorkspaceRoot);
        string lockPath = Path.Combine(store.GetStoreDirForScope("local"), ".lock");
        File.Delete(lockPath);

        fresh.LoadIndex("local").Should().ContainSingle();

        File.Exists(lockPath).Should().BeTrue(
            because: "a cold read reads sidecars from disk and still needs the shared lock");
    }

    [Fact]
    public void LoadIndex_AfterAWriteThroughTheSameStore_SeesTheWrite()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.LocalStore;

        store.Upsert(Entry("first"), "local");
        store.LoadIndex("local").Should().ContainSingle();

        store.Upsert(Entry("second"), "local");

        store.LoadIndex("local").Select(e => e.Name)
            .Should().BeEquivalentTo(["first", "second"],
                because: "the fast path must not outlive the cache invalidation Upsert performs");
    }

    [Fact]
    public void LoadIndex_DoesNotSeeAnotherStoreInstancesWrite_WhichIsWhyTheLockBuysNothingHere()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.LocalStore;
        store.Upsert(Entry("mine"), "local");
        store.LoadIndex("local").Should().ContainSingle();   // warm the cache

        using var other = new FileMemoryStore(store.WorkspaceRoot);
        other.Upsert(Entry("theirs"), "local");

        store.LoadIndex("local").Select(e => e.Name)
            .Should().BeEquivalentTo(["mine"],
                because: "nothing invalidates _indexCache across store instances or processes, "
                       + "so a cache hit was ALREADY stale with respect to an outside write — "
                       + "with the shared lock or without it. This is pre-existing behaviour, "
                       + "pinned here because it is the whole argument for moving the lock: a "
                       + "lock cannot protect the freshness of an answer that was never fresh.");
    }
}
```

- [ ] **Step 2: Run the tests to verify the first one fails**

```bash
rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~LoadIndexCacheHitTests"
```

Expected: `LoadIndex_OnACacheHit_OpensNoLockFile` **FAILS** — `.lock` is recreated because the unconditional `FileLock.AcquireShared` runs `Directory.CreateDirectory` + `OpenOrCreate` before the cache is consulted.

The other three should **PASS** already. If `LoadIndex_OnAColdRead_StillTakesTheSharedFileLock` fails at this point, stop — the test is wrong about the current behaviour, not the code.

- [ ] **Step 3: Implement**

Replace `src/Scrinia.Core/FileMemoryStore.cs:574-587` with:

```csharp
    public List<ArtifactEntry> LoadIndex(string scope = "local")
    {
        var lk = GetIndexLock(scope);

        // Fast lane: an in-memory cache hit performs no I/O, so it needs no file handle.
        //
        // The shared file lock keeps a COLD read from seeing a half-written sidecar. It cannot
        // make a cache hit more correct: nothing invalidates _indexCache across processes, so a
        // hit already returns whatever this process last loaded, lock or no lock. Taking the
        // lock here bought a file handle open and close per call and no guarantee whatsoever.
        //
        // Skipping GetLockPath also skips its GetStoreDirForScope call — which is load-bearing
        // for the layout-migration latch. Safe because every path that populates _indexCache
        // reaches it through GetStoreDirForScope first, so a cache entry cannot exist before
        // migration has been triggered. See
        // docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md.
        lk.EnterReadLock();
        try
        {
            if (_indexCache.TryGetValue(scope, out var cached))
                return cached.Entries.ToList();
        }
        finally
        {
            lk.ExitReadLock();
        }

        using var fileLock = FileLock.AcquireShared(GetLockPath(scope));
        lk.EnterReadLock();
        try
        {
            // LoadIndexUnsafe re-checks the cache as its first statement, which covers the
            // window between the two acquisitions above — another thread may have populated it
            // while this one was waiting on the file lock.
            return LoadIndexUnsafe(scope);
        }
        finally
        {
            lk.ExitReadLock();
        }
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~LoadIndexCacheHitTests"
```

Expected: 4 passed.

- [ ] **Step 5: Run the full suite**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test Scrinia.sln
```

Expected: 0 build errors; 1784 passed, 0 failed (1780 baseline + 4 new).

This step is not optional. `LoadIndex` is on the hottest read path in the store and is reached from 21 call sites across 9 files — a suite-wide run is the only thing that covers them.

- [ ] **Step 6: Commit**

```bash
rtk git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/LoadIndexCacheHitTests.cs
rtk git commit -m "perf(core): stop opening a file handle on every cached LoadIndex"
```

---

## Task 3: Measure the cache-hit path and record the number

The spec requires a measurement and requires it to be reported **even if uninteresting**: *"A finding of 'this costs nothing measurable' is a legitimate outcome and should be recorded rather than quietly dropped."*

It also requires the justification to stay clean: the change is justified by the syscall being unnecessary, **not** by a benchmark delta. The benchmark is a check on the claim, not the reason for it. Do not retune anything to improve the number.

The search-contract branch's figures (~0.87–0.91ms vs ~1.09–1.14ms at 70 memories; 2.16 vs 2.45ms at 272) came from a LoCoMo-seeded corpus that topped out at 272 memories in one scope, and that branch recorded its scaling beyond that point as extrapolated. Hence the sizes below.

**Files:**
- Create: `tests/Scrinia.Benchmarks/LoadIndexBenchmarks.cs`
- Modify: `tests/Scrinia.Benchmarks/Scrinia.Benchmarks.csproj` (only if a reference is missing — check first)

**Interfaces:**
- Consumes: Task 2's implementation.
- Produces: a numbers block pasted into `docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md` under a new `## Measurement` heading.

- [ ] **Step 1: Write the benchmark**

`tests/Scrinia.Benchmarks` already references `Scrinia.Core` and runs via `BenchmarkSwitcher` (`Program.cs:14`), so a new `[MemoryDiagnoser]` class is discovered automatically.

Create `tests/Scrinia.Benchmarks/LoadIndexBenchmarks.cs`:

```csharp
using BenchmarkDotNet.Attributes;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Benchmarks;

/// <summary>
/// Measures a warm <see cref="FileMemoryStore.LoadIndex"/> — the case that used to open and
/// close a .lock file handle before consulting the in-memory cache.
///
/// Sizes deliberately run past 272 entries: the prior numbers on this path came from a
/// LoCoMo-seeded corpus that topped out there, and that branch recorded its scaling beyond
/// that point as extrapolated rather than measured.
///
/// The defensive ToList() on a cache hit is included in the measurement because it is part of
/// what a caller pays today; it is measured, not removed, since removing it is a separate
/// question with its own analysis.
/// </summary>
[MemoryDiagnoser]
public class LoadIndexBenchmarks
{
    [Params(100, 1_000, 5_000)]
    public int EntryCount;

    private string _root = null!;
    private FileMemoryStore _store = null!;

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_bench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);

        var entries = new List<ArtifactEntry>(EntryCount);
        for (int i = 0; i < EntryCount; i++)
            entries.Add(new ArtifactEntry($"entry-{i}", $"file:///entry-{i}.nmp2", 512, 1,
                DateTimeOffset.UtcNow));

        // SaveIndex writes every sidecar and leaves the scope's cache populated, so the
        // benchmark body below measures the warm path from its first iteration.
        _store.SaveIndex(entries, "local");
        _store.LoadIndex("local");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    [Benchmark]
    public int LoadIndex_WarmCache() => _store.LoadIndex("local").Count;
}
```

- [ ] **Step 2: Run it**

```bash
rtk dotnet run -c Release --project tests/Scrinia.Benchmarks -- --filter "*LoadIndexBenchmarks*"
```

BenchmarkDotNet builds and runs a child process; expect this to take several minutes. If the run fails to launch, check that `-c Release` was passed — the csproj comment at `Scrinia.Benchmarks.csproj:9` says it is required.

- [ ] **Step 3: Measure the before-state too**

A single after-number is not a measurement. Get the before-number from the same harness:

```bash
rtk git stash push src/Scrinia.Core/FileMemoryStore.cs
rtk dotnet run -c Release --project tests/Scrinia.Benchmarks -- --filter "*LoadIndexBenchmarks*"
rtk git stash pop
```

Record both. If `git stash pop` reports a conflict, stop and resolve it before doing anything else — do not proceed with a half-restored working tree.

- [ ] **Step 4: Record the numbers**

Append to `docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md`:

```markdown
## Measurement

Harness: `tests/Scrinia.Benchmarks/LoadIndexBenchmarks.cs`, BenchmarkDotNet 0.15.4, `-c Release`.
Machine and runtime as reported in the BenchmarkDotNet header — paste it verbatim.

| Entries | Before (lock first) | After (cache first) | Delta | Alloc before | Alloc after |
|---|---|---|---|---|---|
| 100 | | | | | |
| 1000 | | | | | |
| 5000 | | | | | |

**Reading:** <one paragraph. If the delta is in the noise, say exactly that. The change stands
on the syscall being unnecessary, not on this table.>
```

- [ ] **Step 5: Commit**

```bash
rtk git add tests/Scrinia.Benchmarks/LoadIndexBenchmarks.cs docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md
rtk git commit -m "bench(core): measure the warm LoadIndex path before and after"
```

---

## Task 4: `SearchQuery.Candidates` becomes a hint, not a contract

**Not gated by Task 1.** This is independent of the aliasing question and should be done regardless of Verdicts A and B.

Per F13, a caller supplying a differently-scoped candidate list silently gets different results — `Candidates` can currently change the **answer**, not just the **cost**. Per F12 there is exactly one production setter, so this is a hardening now and would be a behaviour change later, once the benchmark retrievers become setters.

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:1281-1284` (and add one private helper)
- Modify: `src/Scrinia.Core/Search/SearchQuery.cs:36-49` (the XML doc contract)
- Test: `tests/Scrinia.Tests/SearchCandidatesHintTests.cs` (create)

**Interfaces:**
- Produces: `private List<ScopedArtifact> FilterToQueryScopes(IReadOnlyList<ScopedArtifact> candidates, string? scopes, string? excludeTopics)` — private to `FileMemoryStore`, no caller outside it.

- [ ] **Step 1: Write the failing test**

Create `tests/Scrinia.Tests/SearchCandidatesHintTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// SearchQuery.Candidates is a cost hint: supplying it may save the store a ListScoped, but it
/// must never change which memories come back. Before this, a caller who materialized a wider
/// slice than the query asked for got results from scopes the query excluded — silently, with
/// no error, and only if you knew to look.
/// </summary>
public class SearchCandidatesHintTests
{
    private static ArtifactEntry Entry(string name) =>
        new(name, $"file:///{name}.nmp2", 64, 1, DateTimeOffset.UtcNow)
        {
            ContentPreview = "shared needle text",
        };

    /// <summary>
    /// SearchResult carries only Score; the name lives on each derived type. Chunk results are
    /// folded to their PARENT's name deliberately — a leaked out-of-scope memory must fail these
    /// assertions whether it surfaced as a whole entry or as one of its chunks.
    /// </summary>
    private static IEnumerable<string> Names(IReadOnlyList<SearchResult> results) =>
        results.Select(r => r switch
        {
            EntryResult e => e.Item.Entry.Name,
            ChunkEntryResult c => c.ParentItem.Entry.Name,
            TopicResult t => t.TopicName,
            _ => throw new InvalidOperationException($"unhandled result type {r.GetType().Name}"),
        });

    [Fact]
    public void SearchAll_WithCandidatesFromAWiderSlice_IgnoresTheOutOfScopeOnes()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.LocalStore;

        store.Upsert(Entry("alpha-note"), "local:alpha");
        store.Upsert(Entry("beta-note"), "local:beta");

        // A caller that materialized EVERY scope, then searches asking for alpha only.
        var everything = store.ListScoped(null, null);
        everything.Select(c => c.Scope).Should().Contain(["local:alpha", "local:beta"]);

        var results = store.SearchAll(new SearchQuery("needle")
        {
            Scopes = "local:alpha",
            Candidates = everything,
        });

        Names(results).Should().NotContain("beta-note",
            because: "Candidates is a hint about cost, never about the answer");
    }

    [Fact]
    public void SearchAll_WithCandidatesIgnoringExcludeTopics_StillExcludesThem()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.LocalStore;

        store.Upsert(Entry("alpha-note"), "local:alpha");
        store.Upsert(Entry("beta-note"), "local:beta");

        var results = store.SearchAll(new SearchQuery("needle")
        {
            ExcludeTopics = "beta",
            Candidates = store.ListScoped(null, null),
        });

        Names(results).Should().NotContain("beta-note",
            because: "an excludeTopics filter the caller ignored is still this query's filter");
    }

    [Fact]
    public void SearchAll_WithCorrectlyScopedCandidates_ReturnsTheSameAsNoCandidates()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.LocalStore;

        store.Upsert(Entry("alpha-note"), "local:alpha");
        store.Upsert(Entry("beta-note"), "local:beta");

        var withHint = store.SearchAll(new SearchQuery("needle")
        {
            Scopes = "local:alpha",
            Candidates = store.ListScoped("local:alpha", null),
        });
        var withoutHint = store.SearchAll(new SearchQuery("needle") { Scopes = "local:alpha" });

        Names(withHint).Should().BeEquivalentTo(Names(withoutHint),
            because: "the hint must be invisible in the results when the caller honoured it");
    }
}
```

Note on `ContentPreview`: verified to exist — `ArtifactEntry` is a positional record (`src/Scrinia.Core/Models/ArtifactEntry.cs:3-9`) whose 6th parameter is `string? ContentPreview = null`, so both the 5-argument construction and the `{ ContentPreview = … }` initializer are valid. What is **not** verified is that the scorer actually matches on it. If these tests return zero results for both scopes, the seed is wrong, not the fix — mirror whatever a passing search test in `tests/Scrinia.Tests/` seeds (grep for `new SearchQuery(`; `Bm25ScorerTests.cs` is the closest model) rather than inventing a member.

- [ ] **Step 2: Run the tests to verify the first two fail**

```bash
rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~SearchCandidatesHintTests"
```

Expected: `WithCandidatesFromAWiderSlice` and `WithCandidatesIgnoringExcludeTopics` **FAIL** (beta-note comes back). `WithCorrectlyScopedCandidates` **PASSES** already — it is the regression guard for the fix, not a driver of it.

- [ ] **Step 3: Implement**

Replace `src/Scrinia.Core/FileMemoryStore.cs:1281-1284` with:

```csharp
        // Candidates set means the caller (SearchPipeline) already materialized this slice for
        // the contributors — skip the second ListScoped. It is a hint about COST, never about
        // the ANSWER: re-apply this query's own scope and excludeTopics filter so a caller who
        // supplies a wider or differently-scoped list gets exactly what it would have got with
        // no Candidates at all. One HashSet build plus a Contains per candidate, against the
        // full re-listing it replaces.
        IReadOnlyList<ScopedArtifact> allCandidates =
            query.Candidates is { } supplied
                ? FilterToQueryScopes(supplied, query.Scopes, query.ExcludeTopics)
                : ListScoped(query.Scopes, query.ExcludeTopics);
```

Then add this private helper immediately after `SearchAll` (after the closing brace at what is currently `:1308`):

```csharp
    /// <summary>
    /// Constrains a caller-supplied candidate list to exactly the scopes this query resolves to,
    /// so <see cref="SearchQuery.Candidates"/> cannot change a search's answer.
    /// Mirrors <see cref="ListScoped(string?, string?)"/>'s scope resolution, including its rule
    /// for when ephemeral memories are in play.
    /// </summary>
    private List<ScopedArtifact> FilterToQueryScopes(
        IReadOnlyList<ScopedArtifact> candidates, string? scopes, string? excludeTopics)
    {
        var allowed = new HashSet<string>(
            ResolveReadScopes(scopes, excludeTopics), StringComparer.OrdinalIgnoreCase);

        bool includeEphemeral = string.IsNullOrWhiteSpace(scopes)
            || scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Any(s => s.Equals("ephemeral", StringComparison.OrdinalIgnoreCase));
        if (includeEphemeral)
            allowed.Add("ephemeral");

        var result = new List<ScopedArtifact>(candidates.Count);
        foreach (var candidate in candidates)
            if (allowed.Contains(candidate.Scope))
                result.Add(candidate);
        return result;
    }
```

- [ ] **Step 4: Update the contract the doc comment states**

`src/Scrinia.Core/Search/SearchQuery.cs:36-49` currently documents the old contract — *"Handing over a differently-scoped list silently changes the result set."* That sentence is now false. Replace the `<para><b>Contract:</b>…</para>` block with:

```csharp
    /// <para><b>Contract:</b> a hint about cost, not about the answer. The store re-applies this
    /// query's own <see cref="Scopes"/> and <see cref="ExcludeTopics"/> to whatever is supplied,
    /// so a wider or differently-scoped list cannot change the result set — it only wastes the
    /// work spent materializing the surplus. <see cref="SearchPipeline"/> is the only production
    /// caller that sets this, and it builds both from the same values.</para>
```

Leave the `<b>Staleness:</b>` paragraph exactly as it is — a snapshot is still a snapshot, and that trade-off is unchanged by this task.

- [ ] **Step 5: Run the tests to verify they pass**

```bash
rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~SearchCandidatesHintTests"
```

Expected: 3 passed.

- [ ] **Step 6: Run the full suite**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test Scrinia.sln
```

Expected: 0 build errors; 1787 passed, 0 failed (1784 + 3 new).

Pay attention to any `SearchPipeline` test that fails here. `SearchPipeline` is the one production setter, and it builds candidates from the same `Scopes`/`ExcludeTopics` it puts on the query (`SearchPipeline.cs:73-77`) — so a failure means either that claim is wrong or `FilterToQueryScopes` does not match `ListScoped`'s rule. Investigate; do not loosen the filter to make it green.

- [ ] **Step 7: Commit**

```bash
rtk git add src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Core/Search/SearchQuery.cs tests/Scrinia.Tests/SearchCandidatesHintTests.cs
rtk git commit -m "fix(core): SearchQuery.Candidates can no longer change a search's answer"
```

---

## Task 5: Narrow `LoadIndex` to `IReadOnlyList<ArtifactEntry>`

**Gated by Task 1 Verdict A.** If Verdict A is `UNSAFE`, **skip this task entirely** and record the reason in the CHANGELOG entry (Task 6). Skipping is the spec's stated success case, not a failure.

If Verdict A is `SAFE WITH CONDITIONS`, implement only if every condition is met by the code as it stands — not by a promise to keep it that way.

**Files:**
- Modify: `src/Scrinia.Core/IMemoryStore.cs:20` (the interface declaration)
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:574` and `:589` (signatures), `:593` and `:632` (return the alias), `:1594` (the one mutating caller)
- Modify: `src/Scrinia/HttpMemoryStore.cs:253`
- Modify: whatever the compiler names — see Step 3

**Interfaces:**
- Consumes: Task 1's Verdict A and its call-site table.
- Produces: `IReadOnlyList<ArtifactEntry> LoadIndex(string scope = "local")` on `IMemoryStore` and every implementation. `SaveIndex(List<ArtifactEntry>, string)` is **unchanged** — it takes ownership of the list it is given (F7), and narrowing it is a separate question this task does not touch.

- [ ] **Step 1: Narrow the interface and the two implementations**

`src/Scrinia.Core/IMemoryStore.cs:20`:

```csharp
    IReadOnlyList<ArtifactEntry> LoadIndex(string scope = "local");
```

`src/Scrinia.Core/FileMemoryStore.cs` — the two signatures:

```csharp
    public IReadOnlyList<ArtifactEntry> LoadIndex(string scope = "local")
```

```csharp
    private IReadOnlyList<ArtifactEntry> LoadIndexUnsafe(string scope)
```

`src/Scrinia/HttpMemoryStore.cs:253` — narrow the return type; the body already returns `[]`.

- [ ] **Step 2: Stop copying on the way out**

In `LoadIndexUnsafe`, `:593`:

```csharp
        if (_indexCache.TryGetValue(scope, out var cached))
            return cached.Entries;
```

and `:632`:

```csharp
        return entries;
```

Then the same in `LoadIndex`'s fast lane added by Task 2:

```csharp
            if (_indexCache.TryGetValue(scope, out var cached))
                return cached.Entries;
```

**Add this comment above the `CachedIndex.Entries` property** at `FileMemoryStore.cs:65`, because the safety of every line above depends on it and nothing else states it:

```csharp
        /// <summary>The cached entries. Handed out by reference from LoadIndex — callers get a
        /// live view of this list, not a copy. That is safe only because no reader mutates it
        /// and every cache write REPLACES the CachedIndex rather than mutating this instance.
        /// A caller that needs to mutate must call ToList() explicitly, as ImportTopicEntries
        /// does. See docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md.</summary>
        public List<ArtifactEntry> Entries { get; }
```

- [ ] **Step 3: Fix the one mutating caller**

`src/Scrinia.Core/FileMemoryStore.cs:1594`, in `ImportTopicEntries`:

```csharp
        // ToList() because this method mutates the list below and hands it to SaveIndex, which
        // takes ownership of the instance it is given (it becomes the cache). Mutating the
        // cache's own backing list here would corrupt every other reader's live view.
        var existingEntries = LoadIndex(topicScope).ToList();
```

- [ ] **Step 4: Let the compiler find the rest**

```bash
rtk dotnet build Scrinia.sln
```

Every remaining break is a site that needs `List<T>`-specific behaviour. Fix each by adding an explicit `.ToList()` **at that site** — do not widen the return type back.

Read the summary line, not `rtk err`. Expect breaks in `tests/` (141 `LoadIndex(` occurrences, plus any hand-written `IMemoryStore` double that declares the old signature). Test-double signatures must be narrowed to match the interface; test bodies that mutate get `.ToList()`.

Cross-check every break against Task 1's call-site table. **A production break at a site the table classified READ-ONLY means the table is wrong** — stop, correct the analysis document, and re-derive Verdict A before continuing. That misclassification is the exact failure this whole task sequence exists to prevent.

- [ ] **Step 5: Run the full suite**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test Scrinia.sln
```

Expected: 0 build errors; 1787 passed, 0 failed. This task adds no tests — it is a type narrowing whose test is the existing suite, which is only true if the suite genuinely covers the 21 call sites. If the suite passes but Task 1's table flagged a site as untested, say so in the commit body.

- [ ] **Step 6: Commit**

```bash
rtk git add src/Scrinia.Core/IMemoryStore.cs src/Scrinia.Core/FileMemoryStore.cs src/Scrinia/HttpMemoryStore.cs
rtk git add <each test file the compiler required>
rtk git commit -m "refactor(core): LoadIndex returns IReadOnlyList and stops copying on cache hits"
```

---

## Task 6: CHANGELOG and architecture docs

**Files:**
- Modify: `CHANGELOG.md` (the `[Unreleased]` block)
- Modify: `docs/architecture/core.md:437-445`

- [ ] **Step 1: Add the CHANGELOG entries**

Add to the `[Unreleased]` block, following the existing section conventions. Write only the entries for tasks that actually landed:

```markdown
### Changed — store read path

- A cached `LoadIndex` no longer opens a file handle. The shared `.lock` was acquired before
  the in-memory cache was consulted, so every call paid an OS handle open and close even when
  no I/O followed. The lock still guards cold reads, which are the only ones that touch disk —
  it never guarded cache freshness, because nothing invalidates the cache across processes.
- `SearchQuery.Candidates` is now a cost hint that cannot change a search's answer. Supplying a
  wider or differently-scoped candidate list previously returned memories from scopes the query
  had excluded, silently. The store now re-applies the query's own `scopes`/`excludeTopics` to
  whatever is handed in. `SearchPipeline` — the only production caller that sets it — already
  built both from the same values, so no caller's results change.
```

Add this bullet **only if Task 5 landed**:

```markdown
- `IMemoryStore.LoadIndex` returns `IReadOnlyList<ArtifactEntry>` and no longer copies the
  cached list on every read. The single caller that mutates the result (`ImportTopicEntries`)
  now copies explicitly.
```

If Task 5 was skipped, add nothing about it here — the analysis document is where a
deliberate no-change belongs, and it already says so.

- [ ] **Step 2: Correct the architecture doc**

`docs/architecture/core.md:437-445` documents `CachedIndex` and claims `NameToPosition`
*"enables O(1) upsert and remove without scanning."* That is false on two counts: the member
has no readers anywhere in the repo (F5), and `Upsert`/`Remove` rebuild from sidecars rather
than consulting it (F10).

Correct the passage to describe what the type actually does. Do not delete the dead members as
part of this — that is a code change outside this spec's scope, and it goes to residuals.

**Also `docs/architecture/core.md:476`**, which already claims `LoadIndex(scope)` returns
`IReadOnlyList<ArtifactEntry>`. That is false today (it returns `List<ArtifactEntry>`) and
becomes true only if Task 5 landed. Correct it to match whichever outcome this branch reached —
this is a doc that has been describing the change this branch was deciding whether to make.

- [ ] **Step 3: Verify the CHANGELOG claims against the diff**

```bash
rtk git diff main --stat
```

Read each bullet you wrote against the actual diff. The surface-defects branch shipped a
CHANGELOG entry naming a route that did not exist, caught only by a verification pass — a
CHANGELOG entry is user-facing output and gets checked like any other.

- [ ] **Step 4: Commit**

```bash
rtk git add CHANGELOG.md docs/architecture/core.md
rtk git commit -m "docs: record the read-path changes and fix the CachedIndex description"
```

---

## Verification pass — RUN, 2026-08-10

A subagent pass ran against this plan before it was handed to any implementer, and its findings
are already folded in above. Recorded here so a reader knows what was checked and what was not.

**Confirmed exact:** F1–F4, F7–F15; the `.lock`-absence premise (`Directory.CreateDirectory` at
`FileLock.cs:51` creates the directory, never the file, and a contended open requires the file to
already exist — so nothing but a real `Acquire` can produce it); `Upsert`'s unconditional cache
re-population at `:928-930`; the 1780 baseline (re-run: `1780 tests passed, 0 warnings in 5
projects`), so the derived 1784/1787 are arithmetically right; `SearchPipeline.cs:51` building
candidates from the same `Scopes`/`ExcludeTopics` it puts on the query at `:68-77`.

**F5 was confirmed and is airtight** — the fact carrying the most weight in Verdict A.
`NameToPosition` appears only at its declaration, its constructor assignment, and inside
`Rebuild()`; `GetOrComputeCorpusStats` and `Rebuild` have zero call sites anywhere in `src/`,
`tests/`, or `benchmarks/`. No reflection reaches them, and `CachedIndex` is an `internal` type
in `Scrinia.Core`, which declares no `InternalsVisibleTo` at all. Task 1 must still re-derive
this rather than cite this paragraph — but it is not expected to come out differently.

**Three claims were wrong and have been corrected above:**

1. **LOUD — `SearchResult` has no `Name` property.** It is `abstract record SearchResult(double
   Score)` and `Score` is its only member; the name lives on `EntryResult.Item.Entry.Name`,
   `ChunkEntryResult.ParentItem.Entry.Name`, and `TopicResult.TopicName`. Task 4's test file
   would have failed to compile with three `CS1061`s. Fixed with a `Names()` helper that folds
   chunk results to their parent — which the original `r.Name` would not have covered even if it
   had compiled, so this fix is slightly stronger than a mechanical repair.
2. **SILENT — `TestHelpers.LocalStore` returns `ILocalMemoryStore`, not `FileMemoryStore`.**
   Everything the tests call is on that interface, so nothing breaks; the plan's stated fact was
   simply wrong. Recorded in Global Constraints.
3. **SILENT — F6's evidence list** mixed `Clear`/`TryRemove` invalidations in with the actual
   assignments. The substantive claim held. Citations corrected.

**Bonus finding, folded into Task 6:** `docs/architecture/core.md:476` already documents
`LoadIndex` as returning `IReadOnlyList<ArtifactEntry>` — false today, and true only if Task 5
lands.

**Not verified, and left to the implementer:** whether the scorer actually matches on
`ContentPreview` (the member exists; whether it drives a match is untested). Task 4 Step 1 says
what to do if it does not.

---

## Self-review

**Spec coverage (Part A):**

| Spec section | Task |
|---|---|
| "The blocking question" — the analysis | Task 1 |
| §1 Narrow `LoadIndex` to `IReadOnlyList` | Task 5 (gated) |
| §2 Move the lock behind the cache check | Task 2 (gated) |
| §2 "Measure it" | Task 3 |
| §3 `Candidates` becomes a hint | Task 4 |
| Part B — the sweep | **Deliberately out of this plan** — see "Scope" above |

**Type consistency:** `LoadIndex` returns `List<ArtifactEntry>` through Tasks 1–4 and becomes
`IReadOnlyList<ArtifactEntry>` only in Task 5. Task 2's code block and Task 5's Step 2 both
touch the same fast-lane `return` — Task 5 restates it in full rather than referring back.
`FilterToQueryScopes` is named identically at its definition and its one call site.

**Ordering:** Task 4 is independent of Task 1 and could run first. It is placed after Tasks 2–3
so that a branch which stops early on an `UNSAFE` verdict still has the read-path work
contiguous in the log.
