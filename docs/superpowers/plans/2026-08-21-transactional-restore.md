# Transactional Restore Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `scri bundle import` incapable of deleting a memory it was not asked to touch, and incapable of leaving an entry whose content and sidecar describe different versions.

**Architecture:** `ImportTopicEntries` becomes one exclusive-locked critical section. It stops calling `SaveIndex` entirely — the destructive orphan sweep leaves the import path rather than being locked around, because import is purely additive and never needed it. Bundle bodies are staged to non-artifact filenames and committed per entry (rename, then that entry's sidecar), so a crash leaves a prefix of individually-consistent entries rather than content under stale metadata.

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions, hand-written test doubles (no mocking framework — project rule).

**Spec:** `docs/superpowers/specs/2026-08-21-transactional-restore-design.md`

## Global Constraints

- Branch: `fix/transactional-restore`, cut from `main` at `2544a7d`.
- Target file for all production changes: `src/Scrinia.Core/FileMemoryStore.cs`.
- `WithExclusiveLock` uses a **non-recursive** `ReaderWriterLockSlim` (`:611`). Any code inside it must call `*Unsafe` variants — calling `LoadIndex` or `SaveIndex` from within throws `LockRecursionException`.
- Staging filenames **must not contain a `.nmp2` segment**. `DiscoverMemoriesRecursive` (`:2317-2318`) globs `*.nmp2` to decide whether a directory is a v2 scope.
- Import lock timeout: **5 minutes, fixed**. Not scaled to entry count. Every other caller keeps the existing 5-second `FileLock` default.
- `SaveIndexUnsafe`'s behaviour must remain byte-identical after Task 1. It is a public-facing path via `SaveIndex`.
- Tests: no `using Xunit;` — it is a global using in this project. Use `TestHelpers.StoreScope` for a real store over a temp directory.
- Every commit message ends with `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.
- Run the full suite with `dotnet test Scrinia.sln -c Debug --nologo`. Baseline before starting: **2,248 passed / 0 failed / 4 skipped**.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/Scrinia.Core/FileMemoryStore.cs` | The store. Holds the lock helpers, the index publish/sweep split, and `ImportTopicEntries`. | Modify |
| `tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs` | Every test for this plan. New file so the suite has one obvious home for restore-durability behaviour. | Create |
| `CHANGELOG.md` | `[Unreleased]` entry. | Modify |

`FileMemoryStore.cs` is 2,442 lines and already large. This plan does **not** split it — that would collide with Streams C–F, which also touch it. All additions are small and sit beside the members they relate to.

---

### Task 1: Split the orphan sweep out of `SaveIndexUnsafe`

Pure refactor. `SaveIndexUnsafe` keeps identical behaviour; the non-destructive tail becomes reusable so Task 3 can commit an index without a sweep, rather than restating five steps.

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:1193-1237` (`SaveIndexUnsafe`)
- Test: `tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs` (create)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `private void PublishIndexUnsafe(List<ArtifactEntry> entries, string scope)` — normalizes attribute keys in place, writes sidecars, refreshes `_indexCache`, clears `_cachedTopics` for topic scopes, invalidates `_factStore`. Does **not** delete anything. Caller must already hold the scope's exclusive lock.

- [ ] **Step 1: Write the characterization test**

This pins the behaviour Task 1 must not change: `SaveIndex` still sweeps sidecars absent from the list it is handed. It passes before and after — that is the point of a characterization test.

Create `tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests.Store;

/// <summary>
/// Durability of bundle restore — finding C1 of the 2026-08-21 design review.
///
/// <para>The defect: <c>ImportTopicEntries</c> snapshotted the index under a shared lock that
/// released immediately, wrote every body unlocked, then committed through <c>SaveIndex</c>,
/// whose orphan sweep deletes every sidecar absent from a list that was by then seconds old.
/// A memory written during a restore was annihilated after its write reported success.</para>
///
/// <para>The fix is not a wider lock around the sweep. Import is purely additive and never
/// needed one, so the sweep left the import path entirely.</para>
/// </summary>
public sealed class TransactionalRestoreTests
{
    private const string Scope = "local:notes";

    [Fact]
    public async Task SaveIndex_StillSweepsSidecarsAbsentFromTheListItIsHanded()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        await store.StoreAsync(new MemoryWrite("keeper", Scope, ["kept"]));
        await store.StoreAsync(new MemoryWrite("victim", Scope, ["swept"]));

        var withoutVictim = store.LoadIndex(Scope)
            .Where(e => !e.Name.Equals("victim", StringComparison.OrdinalIgnoreCase))
            .ToList();

        store.SaveIndex(withoutVictim, Scope);

        store.LoadIndex(Scope).Select(e => e.Name).Should()
            .Contain("keeper").And.NotContain("victim",
                because: "SaveIndex's orphan sweep is unchanged by this work — only import "
                       + "stops using it");
    }
}
```

- [ ] **Step 2: Run it to confirm it passes on unmodified code**

Run: `dotnet test tests/Scrinia.Tests -c Debug --nologo --filter "FullyQualifiedName~TransactionalRestoreTests"`
Expected: PASS (1 passed). This is the baseline the refactor must preserve.

- [ ] **Step 3: Extract `PublishIndexUnsafe`**

In `src/Scrinia.Core/FileMemoryStore.cs`, replace the body of `SaveIndexUnsafe` (currently `:1193-1237`) so the sweep stays and everything after it moves into a new method. Keep the existing explanatory comments with the code they explain.

```csharp
    private void SaveIndexUnsafe(List<ArtifactEntry> entries, string scope)
    {
        string storeDir = GetStoreDirForScope(scope);
        Directory.CreateDirectory(storeDir);

        // Delete orphaned sidecars: any *.meta.json whose entry is no longer present.
        //
        // Correctness of this sweep depends on something the lock cannot supply: the entry list
        // must have been READ under the same exclusive lock that commits it. Holding the write
        // lock here says nothing about how old `entries` is. ImportTopicEntries used to hand over
        // a list read before any lock was taken, and this loop deleted whatever had landed since
        // (finding C1) — which is why import no longer calls this method at all.
        var expected = new HashSet<string>(
            entries.Select(e => SanitizeName(e.Name) + ".meta.json"),
            StringComparer.OrdinalIgnoreCase);
        foreach (string metaFile in Directory.EnumerateFiles(storeDir, "*.meta.json").ToList())
            if (!expected.Contains(Path.GetFileName(metaFile)))
                File.Delete(metaFile);

        PublishIndexUnsafe(entries, scope);
    }

    /// <summary>
    /// Commits <paramref name="entries"/> as the scope's index WITHOUT deleting anything: writes
    /// each sidecar, then refreshes the caches that derive from the index.
    ///
    /// <para>Split out of <see cref="SaveIndexUnsafe"/> so an ADDITIVE writer can commit without
    /// inheriting the orphan sweep. Taking ownership of the list instance is unchanged — it
    /// becomes the cache, so the caller must not retain a reference or mutate it afterwards.</para>
    ///
    /// <para>Caller must already hold the scope's exclusive lock.</para>
    /// </summary>
    private void PublishIndexUnsafe(List<ArtifactEntry> entries, string scope)
    {
        // Normalize BEFORE anything else reads this list, because this list becomes the cache.
        // WriteSidecarCore normalizes a LOCAL copy on its way to disk, so disk is always
        // "attributes"-keyed; without this, the cache below would keep whatever the caller handed
        // over — the same cache-diverges-from-disk defect the load seam and Upsert's cache writes
        // exist to prevent. In place rather than into a copy: this method takes ownership per its
        // summary, and the list is not yet reachable through _indexCache.
        for (int i = 0; i < entries.Count; i++)
            entries[i] = entries[i].NormalizeAttributesKey();

        string storeDir = GetStoreDirForScope(scope);
        Directory.CreateDirectory(storeDir);

        WriteSidecars(entries, storeDir);

        _indexCache[scope] = new CachedIndex(entries);

        if (scope.StartsWith("local:", StringComparison.Ordinal))
            _cachedTopics = null;

        lock (_factStoreLock) { _factStore = null; _factStoreGeneration++; }
    }
```

Note the normalization moved *after* the sweep. That is safe and intentional: the sweep keys on `SanitizeName(e.Name)`, and `NormalizeAttributesKey()` does not touch `Name`.

- [ ] **Step 4: Run the characterization test and the full store suite**

Run: `dotnet test tests/Scrinia.Tests -c Debug --nologo --filter "FullyQualifiedName~TransactionalRestoreTests|FullyQualifiedName~IndexCacheWritePathTests|FullyQualifiedName~ListScopedCompletenessTests"`
Expected: all PASS. `IndexCacheWritePathTests` pins the copy-on-write cache contract this refactor must not break.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test Scrinia.sln -c Debug --nologo`
Expected: 2,249 passed / 0 failed / 4 skipped (baseline + the new characterization test).

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs
git commit -F - <<'EOF'
refactor(store): split the orphan sweep out of SaveIndexUnsafe

PublishIndexUnsafe commits an index without deleting anything. No
behaviour change: SaveIndexUnsafe is still sweep-then-publish, pinned by
a characterization test.

Groundwork for making import additive. The sweep's correctness depends
on the entry list having been read under the same lock that commits it,
which the lock itself cannot supply -- that precondition is now stated
where the sweep lives.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
```

---

### Task 2: Let `WithExclusiveLock` take a timeout

Pure plumbing. `FileLock.AcquireExclusive` already accepts `TimeSpan? timeout` (`FileLock.cs:31`, defaulting to 5 seconds at `:37`); `WithExclusiveLock` simply does not forward one. Task 3 holds this lock across an entire restore and will blow the 5-second default.

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:606-623` (both `WithExclusiveLock` overloads)
- Test: `tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `private T WithExclusiveLock<T>(string scope, Func<T> body, TimeSpan? timeout = null)` and `private void WithExclusiveLock(string scope, Action body, TimeSpan? timeout = null)`. Omitting `timeout` preserves today's 5-second behaviour exactly.

- [ ] **Step 1: Write the failing test**

Append to `TransactionalRestoreTests.cs`:

```csharp
    [Fact]
    public void WithExclusiveLock_ForwardsAnExplicitTimeoutToFileLock()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        // Hold the scope's cross-process lock from outside the store, so the store's own
        // acquisition must wait and then give up.
        string lockPath = Path.Combine(scope.TempDir, "notes", ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        using var held = FileLock.AcquireExclusive(lockPath);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Action tooSlow = () => store.InvokeWithExclusiveLockForTest(
            Scope, () => { }, TimeSpan.FromMilliseconds(300));

        tooSlow.Should().Throw<FileLockTimeoutException>(
            because: "an explicit timeout must reach FileLock rather than being ignored");
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3),
            because: "it must have used the 300ms timeout, not FileLock's 5s default");
    }
```

This needs a test seam, because `WithExclusiveLock` is private. Add one rather than making the member public — `Scrinia.Core` already grants `InternalsVisibleTo` to the test project.

- [ ] **Step 2: Add the internal test seam**

In `src/Scrinia.Core/FileMemoryStore.cs`, immediately after the two `WithExclusiveLock` overloads:

```csharp
    /// <summary>
    /// Test-only seam for <see cref="WithExclusiveLock(string, Action, TimeSpan?)"/>, which is
    /// private because every production write reaches it through a named store operation. Exists
    /// so the timeout-forwarding contract can be pinned without widening the real API.
    /// </summary>
    internal void InvokeWithExclusiveLockForTest(string scope, Action body, TimeSpan? timeout = null)
        => WithExclusiveLock(scope, body, timeout);
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests -c Debug --nologo --filter "FullyQualifiedName~WithExclusiveLock_ForwardsAnExplicitTimeout"`
Expected: FAIL — compile error, because `WithExclusiveLock` does not yet accept a third argument.

- [ ] **Step 4: Add the parameter**

Replace `:606-623`:

```csharp
    private T WithExclusiveLock<T>(string scope, Func<T> body, TimeSpan? timeout = null)
    {
        EnsureMaterialized(scope);
        using var fileLock = FileLock.AcquireExclusive(GetLockPath(scope), timeout);
        var lk = GetIndexLock(scope);
        lk.EnterWriteLock();
        try
        {
            return body();
        }
        finally
        {
            lk.ExitWriteLock();
        }
    }

    private void WithExclusiveLock(string scope, Action body, TimeSpan? timeout = null) =>
        WithExclusiveLock(scope, () => { body(); return true; }, timeout);
```

`FileLock.AcquireExclusive`'s own signature is `(string lockPath, TimeSpan? timeout = null, ILogger? logger = null)`, and it applies `timeout ?? TimeSpan.FromSeconds(5)`. Passing `null` therefore reproduces today's behaviour exactly, so no existing caller changes.

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests -c Debug --nologo --filter "FullyQualifiedName~WithExclusiveLock_ForwardsAnExplicitTimeout"`
Expected: PASS.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test Scrinia.sln -c Debug --nologo`
Expected: 2,250 passed / 0 failed / 4 skipped.

- [ ] **Step 7: Commit**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs
git commit -F - <<'EOF'
feat(store): let WithExclusiveLock take an explicit timeout

FileLock.AcquireExclusive has always accepted one; WithExclusiveLock
did not forward it, so every store write was pinned to the 5s default.
Omitting the argument preserves that exactly, so no existing caller
changes behaviour.

Needed because a restore holds its scope lock for the whole import and
would otherwise fail against its own default on a large bundle.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
```

---

### Task 3: Make import additive and atomic against concurrent writers

This is the task that fixes finding C1. After it, the reproduced data loss is gone.

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:2375-2419` (`ImportTopicEntries`)
- Test: `tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs`

**Interfaces:**
- Consumes: `PublishIndexUnsafe(List<ArtifactEntry>, string)` from Task 1; `WithExclusiveLock(string, Action, TimeSpan?)` from Task 2.
- Produces: `private static readonly TimeSpan ImportLockTimeout = TimeSpan.FromMinutes(5);`. `ImportTopicEntries`'s public signature is unchanged.

- [ ] **Step 1: Write the failing test**

The naive version of this test — pre-create a memory the bundle omits, assert it survives — **passes before the fix**, because that memory is in the snapshot. C1 requires the snapshot to be *stale*. A second `FileMemoryStore` over the same root has its own cache, so it stands in for a concurrent process with no threads and no timing.

Append to `TransactionalRestoreTests.cs`:

```csharp
    [Fact]
    public async Task Import_DoesNotDeleteAMemoryThatLandedAfterItsSnapshot()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        await store.StoreAsync(new MemoryWrite("seed", Scope, ["seeded"]));
        store.LoadIndex(Scope); // warm this store's cache: it now knows only "seed"

        // A SECOND store over the same root has its own index cache, so this write is invisible
        // to `store` until it reloads — precisely the state a concurrent process leaves behind,
        // and precisely what made the first store's import snapshot stale.
        using (var otherProcess = new FileMemoryStore(scope.WorkspaceDir))
            await otherProcess.StoreAsync(new MemoryWrite("bystander", Scope, ["written during the restore"]));

        var bundled = new List<ArtifactEntry>
        {
            new("imported", "file://placeholder", 11, 1, DateTimeOffset.UtcNow),
        };
        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["imported"] = "bundle body",
        };

        store.ImportTopicEntries(Scope, bundled, contents, overwrite: true);

        // Cold read: a fresh store, so nothing is answered from a cache the import updated.
        using var cold = new FileMemoryStore(scope.WorkspaceDir);
        cold.LoadIndex(Scope).Select(e => e.Name).Should()
            .Contain("bystander", because:
                "import is additive — it must never delete a memory the bundle does not mention, "
              + "however stale its own view of the scope was")
            .And.Contain("imported")
            .And.Contain("seed");
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Scrinia.Tests -c Debug --nologo --filter "FullyQualifiedName~Import_DoesNotDeleteAMemoryThatLandedAfterItsSnapshot"`
Expected: FAIL — `Expected collection to contain "bystander"`. The sweep deleted its sidecar because the import's snapshot predated it.

- [ ] **Step 3: Rewrite `ImportTopicEntries`**

Replace `:2375-2419` entirely:

```csharp
    /// <summary>
    /// Cross-process lock budget for a restore. A restore holds its scope's lock for the whole
    /// import, which is unbounded in the bundle's size, so the 5s default that suits a single
    /// write would fail a large one against itself.
    ///
    /// <para>Fixed rather than scaled to entry count: this is a ceiling that exists so a
    /// legitimate long restore does not fail spuriously, not a correctness parameter. A formula
    /// would be one more thing to get wrong. The accepted cost is that a genuine deadlock on an
    /// import surfaces in five minutes rather than five seconds.</para>
    /// </summary>
    private static readonly TimeSpan ImportLockTimeout = TimeSpan.FromMinutes(5);

    public void ImportTopicEntries(string topicScope, List<ArtifactEntry> entries,
        Dictionary<string, string> artifactContents, bool overwrite)
    {
        WithExclusiveLock(topicScope, () =>
        {
            string storeDir = GetStoreDirForScope(topicScope);
            Directory.CreateDirectory(storeDir);

            // Read under the lock that will commit it. The snapshot and the commit used to sit on
            // opposite sides of an unlocked write loop, which is what let a concurrent write land
            // in between and then be swept as an orphan (finding C1).
            //
            // ToList() because this method mutates the list below and hands it to
            // PublishIndexUnsafe, which takes ownership of the instance it is given (it becomes
            // the cache). LoadIndexUnsafe returns the cache's own backing list, so mutating it
            // here would corrupt every other reader's live view.
            var existingEntries = LoadIndexUnsafe(topicScope).ToList();

            foreach (var entry in entries)
            {
                bool exists = existingEntries.Any(
                    e => e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
                if (exists && !overwrite)
                    continue;

                if (artifactContents.TryGetValue(entry.Name, out string? content))
                {
                    string filePath = Path.Combine(storeDir, SanitizeName(entry.Name) + ".nmp2");
                    AtomicWriteAllText(filePath, content);
                }

                // Uri is recomputed deliberately: it encodes THIS store's root, so the exporting
                // machine's value is wrong here. CreatedAt is NOT — a restore reinstates the
                // memory as it was, and overwriting it reset every memory's creation date on
                // every restore (design review finding M4). A bundle carrying no "createdAt" key
                // deserializes to default(DateTimeOffset) — 0001-01-01T00:00:00+00:00 — and that
                // specific value is the only one rewritten to "now" here. Any OTHER unparseable
                // createdAt is refused earlier, in ImportTopicsFromZip's deserialize.
                var updatedEntry = entry with
                {
                    Uri = ArtifactUri(entry.Name, topicScope),
                    CreatedAt = entry.CreatedAt == default ? DateTimeOffset.UtcNow : entry.CreatedAt,
                };

                int idx = existingEntries.FindIndex(
                    e => e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                    existingEntries[idx] = updatedEntry;
                else
                    existingEntries.Add(updatedEntry);
            }

            // PublishIndexUnsafe, not SaveIndexUnsafe: import ADDS and OVERWRITES, and never
            // removes. The orphan sweep is not a capability it needs — it is the mechanism that
            // destroyed data here, so it is gone from this path rather than locked around.
            PublishIndexUnsafe(existingEntries, topicScope);
        }, ImportLockTimeout);
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests -c Debug --nologo --filter "FullyQualifiedName~Import_DoesNotDeleteAMemoryThatLandedAfterItsSnapshot"`
Expected: PASS.

- [ ] **Step 5: Add the concurrency invariant test**

This is the reproduced defect, asserted as a disjunction on purpose. Asserting "the write survives" would encode a scheduling assumption and could flake; "survived **or** threw" is the actual contract, and silent disappearance fails both branches so it cannot flake into a false pass.

Append to `TransactionalRestoreTests.cs`:

```csharp
    [Fact]
    public async Task Import_ConcurrentWriteToTheSameScope_SurvivesOrFailsLoudly()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        await store.StoreAsync(new MemoryWrite("seed", Scope, ["seeded"]));

        var bundled = new List<ArtifactEntry>();
        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string blob = new string('x', 60_000);
        for (int i = 0; i < 120; i++)
        {
            string name = $"bundled-{i:D3}";
            bundled.Add(new ArtifactEntry(name, "file://placeholder", blob.Length, 1, DateTimeOffset.UtcNow));
            contents[name] = blob;
        }

        Exception? importError = null;
        var importDone = new ManualResetEventSlim(false);
        var importThread = new Thread(() =>
        {
            try { store.ImportTopicEntries(Scope, bundled, contents, overwrite: true); }
            catch (Exception ex) { importError = ex; }
            finally { importDone.Set(); }
        });
        importThread.Start();

        bool wrote;
        try
        {
            using var otherProcess = new FileMemoryStore(scope.WorkspaceDir);
            await otherProcess.StoreAsync(new MemoryWrite("urgent", Scope, ["written mid-restore"]));
            wrote = true;
        }
        catch (FileLockTimeoutException)
        {
            wrote = false; // blocked out by the import's lock — loud, and acceptable
        }

        importDone.Wait(TimeSpan.FromMinutes(2)).Should().BeTrue();
        importThread.Join();
        importError.Should().BeNull();

        if (wrote)
        {
            using var cold = new FileMemoryStore(scope.WorkspaceDir);
            cold.LoadIndex(Scope).Select(e => e.Name).Should().Contain("urgent",
                because: "a write that reported success must never be silently deleted by a "
                       + "concurrent restore — it may block, or fail loudly, but not vanish");
        }
    }
```

Two details this test depends on, both verified against the tree: `TestHelpers.StoreScope` exposes
`WorkspaceDir` and `TempDir` as public properties, and `FileLockTimeoutException` lives in
`Scrinia.Core`, which the test file already imports.

- [ ] **Step 6: Run it**

Run: `dotnet test tests/Scrinia.Tests -c Debug --nologo --filter "FullyQualifiedName~Import_ConcurrentWriteToTheSameScope"`
Expected: PASS.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test Scrinia.sln -c Debug --nologo`
Expected: 2,252 passed / 0 failed / 4 skipped. Pay attention to `BundleRoundTripTotalityTests` and `BundleV2Tests` — they drive this method and must stay green.

- [ ] **Step 8: Commit**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs
git commit -F - <<'EOF'
fix(store)!: stop bundle restore deleting memories written during it

ImportTopicEntries snapshotted the index under a shared lock that
released immediately, wrote every body unlocked, then committed through
SaveIndex -- whose orphan sweep deletes every sidecar absent from a list
that was by then seconds old. A memory written mid-restore was
annihilated after its write reported success, and a memory the bundle
never mentioned had its sidecar reverted so content and metadata
described different versions.

The fix is not a wider lock around the sweep. Import adds and
overwrites and never removes, so it now commits through
PublishIndexUnsafe and the sweep is gone from this path entirely --
the mechanism removed rather than fenced. DualModeParseTests already
documented that mechanism as "the data-loss path behind the aliasing";
the trigger was closed then, the mechanism was not, and import reached
it by a route nobody enumerated.

BREAKING: a concurrent writer to a scope being restored now blocks for
the duration of that scope's import, and fails loudly if it exceeds its
own acquire timeout. It previously appeared to succeed and lost the write.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
```

---

### Task 4: Stage bundle bodies, commit per entry

Closes the crash-safety hole: content is currently written to live artifact paths before any metadata commits, so a crash or a mid-loop throw leaves a memory whose `.nmp2` is new and whose sidecar is old — the `seed` corruption, reachable with no concurrency at all.

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` (`ImportTopicEntries` from Task 3)
- Test: `tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs`

**Interfaces:**
- Consumes: everything from Task 3.
- Produces: `private const string ImportStagingSuffix = ".staging";` and the staged-then-committed write sequence. No signature changes.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task Import_ThatFailsPartWay_LeavesNoLiveArtifactUnderAStaleSidecar()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        await store.StoreAsync(new MemoryWrite("victim", Scope, ["ORIGINAL BODY"]));

        string storeDir = Path.Combine(scope.TempDir, "notes");
        var before = store.LoadIndex(Scope).Single(e => e.Name == "victim");

        // Raw artifact text, captured for an exact comparison below. ReadArtifactAsync returns
        // the NMP/2 envelope (Brotli + URL-safe Base64), NOT the decoded content — so asserting
        // on a substring of the original prose would never match, before or after the fix.
        string bodyBefore = await store.ReadArtifactAsync("victim", Scope);

        // A bundle that overwrites `victim` and then fails: the second entry claims content the
        // dictionary does not carry AND a name that cannot be written, so the import throws after
        // `victim` would previously have been overwritten in place.
        var bundled = new List<ArtifactEntry>
        {
            new("victim", "file://placeholder", 13, 1, DateTimeOffset.UtcNow),
            new(new string('n', 300), "file://placeholder", 5, 1, DateTimeOffset.UtcNow),
        };
        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["victim"] = "REPLACEMENT BODY",
            [new string('n', 300)] = "boom",
        };

        Action failing = () => store.ImportTopicEntries(Scope, bundled, contents, overwrite: true);
        failing.Should().Throw<Exception>(because: "the over-length component cannot be written");

        using var cold = new FileMemoryStore(scope.WorkspaceDir);
        var after = cold.LoadIndex(Scope).SingleOrDefault(e => e.Name == "victim");

        after.Should().NotBeNull();
        string bodyAfter = await cold.ReadArtifactAsync("victim", Scope);
        bodyAfter.Should().Be(bodyBefore, because:
            "a failed import must not leave new content under a sidecar that still describes the "
          + "old content — content and metadata must agree, whatever the outcome");
        after!.OriginalBytes.Should().Be(before.OriginalBytes);

        Directory.EnumerateFiles(storeDir, "*.staging").Should().BeEmpty(
            because: "a failed import rolls back everything it staged");
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Scrinia.Tests -c Debug --nologo --filter "FullyQualifiedName~Import_ThatFailsPartWay"`
Expected: FAIL — the body assertion finds `REPLACEMENT`, because `victim`'s live `.nmp2` was overwritten before the throw while its sidecar was never updated.

- [ ] **Step 3: Stage, then commit per entry**

In `ImportTopicEntries`, add the constant beside `ImportLockTimeout`:

```csharp
    /// <summary>
    /// Suffix for a bundle body staged but not yet committed.
    ///
    /// <para>Deliberately carries NO <c>.nmp2</c> segment: <c>DiscoverMemoriesRecursive</c> decides
    /// whether a directory is a v2 scope with <c>EnumerateFiles(dir, "*.nmp2")</c>, and a staging
    /// file that registered as an artifact would make a half-restored scope discoverable as a
    /// topic. Keeping <c>.nmp2</c> out of the name removes the question rather than answering
    /// it.</para>
    /// </summary>
    private const string ImportStagingSuffix = ".staging";
```

Then replace the write and commit portions of the loop body. The `foreach` now stages only, and a second loop commits:

```csharp
            var staged = new List<(string StagedPath, string FinalPath, ArtifactEntry Entry)>();
            try
            {
                foreach (var entry in entries)
                {
                    bool exists = existingEntries.Any(
                        e => e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
                    if (exists && !overwrite)
                        continue;

                    if (artifactContents.TryGetValue(entry.Name, out string? content))
                    {
                        // Stage beside the destination — same directory, so the commit below is a
                        // rename rather than a copy. The live artifact is not touched until every
                        // body in this bundle is durably on disk.
                        string sanitized = SanitizeName(entry.Name);
                        string finalPath = Path.Combine(storeDir, sanitized + ".nmp2");
                        string stagedPath = Path.Combine(
                            storeDir, $"{sanitized}.import-{Guid.NewGuid():N}{ImportStagingSuffix}");
                        AtomicWriteAllText(stagedPath, content);
                        staged.Add((stagedPath, finalPath, updatedEntry));
                    }

                    // ... the `updatedEntry` construction and list splice from Task 3, unchanged ...
                }

                // COMMIT. Per entry, content then that entry's sidecar, so a crash leaves a PREFIX
                // of fully-applied entries rather than any single entry half-applied. The live
                // .nmp2 is never new while its sidecar is old, which is the corruption this
                // ordering exists to prevent.
                foreach (var (stagedPath, finalPath, entry) in staged)
                {
                    File.Move(stagedPath, finalPath, overwrite: true);
                    WriteSidecar(entry, storeDir);
                }

                PublishIndexUnsafe(existingEntries, topicScope);
            }
            catch
            {
                // Roll back what is still staged. Anything already renamed is committed and
                // consistent; rolling that back would need the previous content, which staging
                // deliberately did not retain.
                foreach (var (stagedPath, _) in staged)
                    TryDeleteStagingFile(stagedPath);
                throw;
            }
```

Add the helper beside `ImportTopicEntries`:

```csharp
    /// <summary>Best-effort removal of a staging file. A leftover is inert litter, not
    /// corruption — it is invisible to every enumerator in the store — so a failure to delete
    /// must not mask the exception that caused the rollback.</summary>
    private static void TryDeleteStagingFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests -c Debug --nologo --filter "FullyQualifiedName~Import_ThatFailsPartWay"`
Expected: PASS.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test Scrinia.sln -c Debug --nologo`
Expected: 2,253 passed / 0 failed / 4 skipped.

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs
git commit -F - <<'EOF'
fix(store): stage bundle bodies instead of overwriting live artifacts

A restore wrote every .nmp2 to its live path before committing any
metadata, so a crash or a mid-loop throw left a memory whose content
was new and whose sidecar still described the old version. No
concurrency required -- the reproduction hit this on a memory the
bundle never mentioned.

Bodies now stage to a name carrying no .nmp2 segment (so
DiscoverMemoriesRecursive cannot mistake one for an artifact), and
commit per entry: rename, then that entry's sidecar. A crash leaves a
prefix of individually-consistent entries; a throw rolls back
everything still staged.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
```

---

### Task 5: Sweep staging litter left by a crash

Rollback covers the throw path. A hard crash leaves staged files behind, and nothing removes them.

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` (`ImportTopicEntries`)
- Test: `tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs`

**Interfaces:**
- Consumes: `ImportStagingSuffix`, `TryDeleteStagingFile` from Task 4.
- Produces: `private void SweepStaleStagingUnsafe(string storeDir)`.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task Import_SweepsStagingLitterLeftByAPriorCrash()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        await store.StoreAsync(new MemoryWrite("seed", Scope, ["seeded"]));
        string storeDir = Path.Combine(scope.TempDir, "notes");

        string litter = Path.Combine(storeDir, "ghost.import-deadbeef.staging");
        File.WriteAllText(litter, "a body from an import that died before committing");

        store.ImportTopicEntries(
            Scope,
            [new("fresh", "file://placeholder", 5, 1, DateTimeOffset.UtcNow)],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["fresh"] = "hello" },
            overwrite: true);

        File.Exists(litter).Should().BeFalse(because: "a later import clears staging litter");

        using var cold = new FileMemoryStore(scope.WorkspaceDir);
        cold.LoadIndex(Scope).Select(e => e.Name).Should()
            .Contain("fresh").And.Contain("seed").And.NotContain("ghost",
                because: "litter is discarded, never imported");
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Scrinia.Tests -c Debug --nologo --filter "FullyQualifiedName~Import_SweepsStagingLitter"`
Expected: FAIL — `Expected File.Exists(litter) to be false, but found True`.

- [ ] **Step 3: Add the sweep and call it**

Add beside `TryDeleteStagingFile`:

```csharp
    /// <summary>
    /// Removes staging files left by an import that died before committing or rolling back.
    ///
    /// <para>Unconditional rather than age-based: the caller holds the scope's exclusive lock, so
    /// no other import can be staging into this directory, and every staging file present is
    /// therefore abandoned by definition.</para>
    ///
    /// <para>Caller must already hold the scope's exclusive lock.</para>
    /// </summary>
    private void SweepStaleStagingUnsafe(string storeDir)
    {
        if (!Directory.Exists(storeDir)) return;
        foreach (string stale in Directory.EnumerateFiles(storeDir, "*" + ImportStagingSuffix).ToList())
            TryDeleteStagingFile(stale);
    }
```

Call it in `ImportTopicEntries` immediately after `Directory.CreateDirectory(storeDir);` and before the snapshot:

```csharp
            SweepStaleStagingUnsafe(storeDir);
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests -c Debug --nologo --filter "FullyQualifiedName~Import_SweepsStagingLitter"`
Expected: PASS.

- [ ] **Step 5: Verify staging files are invisible to discovery**

```csharp
    [Fact]
    public void StagingFiles_AreInvisibleToScopeDiscovery()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        string ghostScopeDir = Path.Combine(scope.TempDir, "ghostscope");
        Directory.CreateDirectory(ghostScopeDir);
        File.WriteAllText(
            Path.Combine(ghostScopeDir, "x.import-deadbeef.staging"), "staged, never committed");

        store.ResolveReadScopes(null).Should().NotContain("local:ghostscope",
            because: "a directory holding only staging litter is not a scope — the suffix "
                   + "carries no .nmp2 segment precisely so DiscoverMemoriesRecursive ignores it");
    }
```

Run: `dotnet test tests/Scrinia.Tests -c Debug --nologo --filter "FullyQualifiedName~StagingFiles_AreInvisibleToScopeDiscovery"`
Expected: PASS.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test Scrinia.sln -c Debug --nologo`
Expected: 2,255 passed / 0 failed / 4 skipped.

- [ ] **Step 7: Commit**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs
git commit -F - <<'EOF'
fix(store): clear staging litter left by a crashed import

Rollback handles the throw path; a hard crash leaves staged bodies
behind with nothing to remove them. An import now clears them first,
unconditionally -- it holds the scope's exclusive lock, so every staging
file present is abandoned by definition.

Also pins that a directory holding only staging litter is not
discovered as a scope, which is the reason the suffix carries no .nmp2
segment.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
```

---

### Task 6: Record the change

**Files:**
- Modify: `CHANGELOG.md` (`[Unreleased]`, immediately after the `## [Unreleased]` heading)

**Interfaces:**
- Consumes: everything above. Produces: nothing.

- [ ] **Step 1: Add the entry**

Insert directly below `## [Unreleased]`:

```markdown
### Fixed (breaking) — `scri bundle import` no longer deletes memories written during it

- A restore snapshotted the scope index under a shared lock that released immediately, wrote every
  bundled artifact with no lock held, then committed through an orphan sweep that deletes any
  sidecar absent from that snapshot. A memory written while a restore ran was therefore deleted
  **after its own write reported success** — content orphaned, sidecar gone, absent from the index
  permanently. Separately, a memory the bundle never mentioned could have its sidecar rewritten
  from the stale snapshot, leaving its metadata describing an older version than its content.
- Import is now a single locked, additive operation: it adds and overwrites, and the orphan sweep
  is gone from this path entirely rather than locked around it. `SaveIndex`'s own behaviour is
  unchanged.
- Bundled bodies are staged and committed per entry, so a failed or interrupted restore can no
  longer leave a memory whose content and sidecar disagree. A restore that dies part-way now
  leaves a prefix of fully-applied entries; staging leftovers are cleared by the next import.
- **Behaviour change:** a concurrent writer to a scope being restored now blocks for the duration
  of that scope's import, and fails loudly if it exceeds its own lock timeout. It previously
  appeared to succeed and silently lost the write. The restore itself takes a 5-minute lock
  budget, so a large bundle no longer fails against the 5-second default that suits a single write.
```

- [ ] **Step 2: Verify the markdown renders and links resolve**

Run: `grep -n "bundle import no longer deletes" CHANGELOG.md`
Expected: one hit, under `[Unreleased]`.

- [ ] **Step 3: Run the full suite one final time**

Run: `dotnet test Scrinia.sln -c Debug --nologo`
Expected: 2,255 passed / 0 failed / 4 skipped.

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md
git commit -F - <<'EOF'
docs(changelog): record the restore durability fix

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
```

---

## Spec coverage

| Spec section | Task |
|---|---|
| Part A — one critical section, no sweep | 3 |
| A1 — extract `PublishIndexUnsafe` | 1 |
| A2 — list no longer stale by construction; amend the sweep comment | 1 (comment), 3 (locking) |
| B1 — staging, no `.nmp2` segment | 4, verified by the discovery test in 5 |
| B2 — commit per entry | 4 |
| B3 — rollback | 4 |
| B4 — lock timeout | 2 (plumbing), 3 (the 5-minute value) |
| Testing — additivity | 3 |
| Testing — rollback | 4 |
| Testing — crash prefix | 4 (the same test asserts content/sidecar agreement) |
| Testing — staging sweep | 5 |
| Testing — concurrency invariant | 3 |
| Scope boundaries — `ImportFileEntities` out of scope | not implemented, by design |

## Notes for the executor

- **Task 3 is the one that matters.** If time runs short, Tasks 1–3 alone close the reproduced data loss; 4–5 close the crash hole.
- The reproduction harness from the review lives at `scratchpad/repro/` and can be re-run against the fixed build as an end-to-end check. It is throwaway — do not add it to the repo.
- `SaveIndexUnsafe` keeps its sweep and its single remaining caller becomes tests only. Do **not** delete it; `SaveIndex` is public API surface.
- If `dotnet test` reports a different baseline than 2,248, stop and reconcile before starting — the counts in each task assume it.
