# Write-Path Cost Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One logical write costs one round of I/O, not N.

**Architecture:** Three independent cost defects, all the same shape. `FileMemoryStore` stops re-reading every sidecar in a scope on each write and instead updates its cache copy-on-write. `MaintenanceEventSink` stops materializing the whole store to answer a boolean. `VectorStore` gains a per-memory batch replace so a memory's K vectors cost one locked file operation instead of K. A benchmark suite measures all three rather than asserting them.

**Tech Stack:** C# / .NET 10, xUnit 2.9.3 + FluentAssertions, BenchmarkDotNet 0.15.4, PowerShell 7.

**Spec:** `docs/superpowers/specs/2026-08-17-write-path-cost-design.md` (committed `f89f204`).

**Branch:** `fix/write-path-cost`, from `main` at `f89f204`.

## Global Constraints

- **The aliasing invariant is absolute.** `CachedIndex.Entries` is handed out by reference from `LoadIndex`. Every cache write MUST install a NEW `CachedIndex` with a NEW `List<ArtifactEntry>`. NEVER call `Add`/`Remove`/`Insert`/`Clear`/`Sort`/indexer-set on an existing `CachedIndex.Entries`. A change that mutates in place must be rejected in review even if tests pass.
- **Do not revive `CachedIndex.NameToPosition`, `Rebuild()`, `Stats`, or `GetOrComputeCorpusStats()`.** They do not exist at `f89f204`. Reintroducing a live name→position map alongside an aliased list is mutually exclusive with the aliasing invariant.
- **Do not change the locking model.** `WithExclusiveLock` and the shared/exclusive lock structure stay exactly as-is.
- **Do not change `LoadIndex`'s return type or aliasing behaviour.**
- Build gate: `dotnet build Scrinia.sln` must report 0 errors. `rtk err` is NOT a reliable build gate.
- Test gate: `Scrinia.Tests`, `Scrinia.Server.Tests`, `Scrinia.Plugin.Embeddings.Tests`, `Scrinia.Merge.Tests`, `Scrinia.Retrieval.Benchmarks.Tests` all green.
- **Known pre-existing failure**, not yours, do not "fix": `Scrinia.Tests.MemoryEventSinkContextInstallDefaultTests.AParallelScopeLifecycleDoesNotDisturbDefault` — order-dependent, fails on `main`. Any OTHER failure is real.
- `Scrinia.Tests` and `Scrinia.Server.Tests` carry `global using Xunit;` — a per-file `using Xunit;` is CS8933.
- `IEmbeddingProvider : IDisposable` — test doubles need `Dispose()`.
- This repo emits phantom LSP diagnostics (CS0101/CS0104/CS0234/CS0246) mid-edit, and SynologyDrive can resurrect deleted `.cs` files as untracked ghosts. Trust `dotnet build` and `git status`, not the diagnostic stream.
- Benchmarks are evidence, never the design target. Do not tune code to move a benchmark number.

---

## File Structure

| File | Responsibility | Tasks |
|---|---|---|
| `src/Scrinia.Core/FileMemoryStore.cs` | The load seam, the three cache write sites, topic-cache invalidation | 1, 2, 3, 4 |
| `src/Scrinia.Core/MaintenanceEventSink.cs` | Orphan detection's inbound-ref question | 5 |
| `src/Scrinia.Core/Embeddings/VectorStore.cs` | Per-memory batch vector replace | 6 |
| `src/Scrinia.Core/Embeddings/MemoryVectorIndexer.cs` | Calls the batch API once per memory | 6 |
| `tests/Scrinia.Tests/Store/IndexCacheWritePathTests.cs` | New — aliasing, warm/cold parity, cold-cache write | 2, 3 |
| `tests/Scrinia.Tests/Store/NormalizationSeamTests.cs` | New — legacy-fields visibility | 1 |
| `tests/Scrinia.Tests/Store/TopicCacheInvalidationTests.cs` | New — topic lifecycle | 4 |
| `tests/Scrinia.Tests/Embeddings/VectorBatchReplaceTests.cs` | New — batch/loop equivalence, shrink, atomicity | 6 |
| `tests/Scrinia.Benchmarks/WritePathBenchmarks.cs` | New — store write scaling | 7 |
| `tests/Scrinia.Benchmarks/VectorWriteBenchmarks.cs` | New — vector write ops | 7 |
| `scripts/run-write-path-benchmarks.ps1` | New — reproducible run | 7 |
| `docs/superpowers/analysis/2026-08-17-write-path-cost-measurements.md` | New — the numbers | 7 |

---

## Task 1: Move normalization to the load seam

**Why first:** every later task removes or bypasses a rebuild that currently carries `.Select(e => e.NormalizeAttributesKey())`. Until the seam owns that rule, removing any wrapper is a silent data-visibility bug.

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:747-771` (`LoadEntriesFromSidecars`)
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:1050`, `:1119`, `:1179`, `:1195` (remove four wrappers)
- Test: `tests/Scrinia.Tests/Store/NormalizationSeamTests.cs` (create)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `LoadEntriesFromSidecars` returns entries with `NormalizeAttributesKey()` already applied. Tasks 2 and 3 rely on this — they must NOT re-apply it.

**Background the implementer needs:** `ArtifactEntry` carries a legacy `"fields"` JSON key deserialized into a private `_legacyFields`. `NormalizeAttributesKey()` (`src/Scrinia.Core/Models/ArtifactEntry.cs:85-89`) folds it into `Attributes` (new key wins) and clears the holder. `ArtifactEntry.cs:67` states the invariant: EVERY load seam must call it before an entry escapes. `LoadEntriesFromSidecars` is that seam and currently does not.

- [ ] **Step 1: Write the failing test**

Create `tests/Scrinia.Tests/Store/NormalizationSeamTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests.Store;

/// <summary>
/// Pins the invariant stated at ArtifactEntry.cs:67 — every load seam normalizes the legacy
/// "fields" key before an entry escapes. Before this moved into LoadEntriesFromSidecars, four
/// write sites each pasted `.Select(e => e.NormalizeAttributesKey())` around it; deleting any one
/// of them silently hid that entry's facets from search, filter and display for the process
/// lifetime. This test fails if the seam change is reverted.
/// </summary>
public sealed class NormalizationSeamTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public NormalizationSeamTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_norm_seam_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void LoadIndex_NormalizesLegacyFieldsKey_OnAColdLoad()
    {
        // Seed a sidecar carrying the LEGACY "fields" key and no "attributes" key at all —
        // the on-disk shape written before the attributes rename.
        var seed = new ArtifactEntry("legacy-note", "", 10, 1, DateTimeOffset.UtcNow);
        _store.Upsert(seed, "local");
        string metaPath = _store.MetaPath("legacy-note", "local");

        File.WriteAllText(metaPath, """
            {"name":"legacy-note","description":"","size":10,"chunks":1,
             "createdAt":"2026-01-01T00:00:00+00:00",
             "fields":{"topic":["alpha"]}}
            """);

        // Force a cold load: drop the cache the Upsert above installed.
        using var reader = new FileMemoryStore(_root);

        var entry = reader.LoadIndex("local").Single(e => e.Name == "legacy-note");

        entry.Attributes.Should().NotBeNull(
            because: "the load seam must fold the legacy \"fields\" key into Attributes; "
                   + "an entry escaping with Attributes == null hides its facets from search");
        entry.Attributes!["topic"].Should().BeEquivalentTo(["alpha"]);
    }

    [Fact]
    public void WriteFirstSession_DoesNotCacheAnUnnormalizedEntry()
    {
        // A write-first session: Upsert before any cold LoadIndex on this scope. The cached
        // entries must be normalized even though no read populated the cache.
        var seed = new ArtifactEntry("note-a", "", 5, 1, DateTimeOffset.UtcNow);
        _store.Upsert(seed, "local");
        string metaPath = _store.MetaPath("note-a", "local");
        File.WriteAllText(metaPath, """
            {"name":"note-a","description":"","size":5,"chunks":1,
             "createdAt":"2026-01-01T00:00:00+00:00",
             "fields":{"topic":["beta"]}}
            """);

        // Second write to a DIFFERENT name in the same scope — whatever populates the cache
        // as a result must carry note-a normalized.
        _store.Upsert(new ArtifactEntry("note-b", "", 5, 1, DateTimeOffset.UtcNow), "local");

        var a = _store.LoadIndex("local").Single(e => e.Name == "note-a");
        a.Attributes.Should().NotBeNull();
        a.Attributes!["topic"].Should().BeEquivalentTo(["beta"]);
    }
}
```

If `MetaPath` is not public, use `Path.Combine(_root, ".scrinia", "memories", "local", "note-a.meta.json")` — verify the real layout first with `Directory.GetFiles(_root, "*.meta.json", SearchOption.AllDirectories)` and hard-code what you observe. Do not guess.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~NormalizationSeamTests"`

Expected: `LoadIndex_NormalizesLegacyFieldsKey_OnAColdLoad` FAILS — `Attributes` is null, because `LoadEntriesFromSidecars` returns the raw deserialized entry and the cold path only installs a normalized form when `AttributeMigration.MigratesTo` returns true.

**Record the observed failure message in your report.** If a test passes here, it is not testing what it claims — fix the test before touching source.

- [ ] **Step 3: Apply normalization at the seam**

In `LoadEntriesFromSidecars`, normalize immediately after deserializing:

```csharp
                string json = FileRetry.Run(() => File.ReadAllText(metaFile));
                var entry = JsonSerializer.Deserialize(json, FileStoreJsonContext.Default.ArtifactEntry);
                if (entry is not null)
                    entries.Add(entry.NormalizeAttributesKey());
```

Update the method's doc/comment to state that it is THE load seam and owns this rule, so callers neither need nor should re-apply it.

- [ ] **Step 4: Delete the four now-redundant wrappers**

`FileMemoryStore.cs:1050`, `:1119`, `:1195` become:

```csharp
            var entries = LoadEntriesFromSidecars(storeDir);
```

`:1178-1180` becomes:

```csharp
            var current = LoadEntriesFromSidecars(storeDir)
                .FirstOrDefault(e => SanitizeName(e.Name).Equals(SanitizeName(subject), StringComparison.OrdinalIgnoreCase));
```

Also update the three comments above those sites that explain the coalescing (`:1043-1048`, `:1115-1117`, and the one above `:1195`) — they now describe a rule that lives at the seam. Do not delete the explanation; move it.

- [ ] **Step 5: Verify**

Run: `dotnet build Scrinia.sln` → 0 errors
Run: `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~NormalizationSeamTests"` → PASS
Run: `dotnet test Scrinia.sln --no-build` → all suites green except the known flake

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/Store/NormalizationSeamTests.cs
git commit -m "refactor(store): normalize at the load seam, not at four write sites"
```

---

## Task 2: Copy-on-write cache update for `Upsert` and `Remove`

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:1033-1059` (`Upsert`), `:1061-1135` (`Remove`)
- Test: `tests/Scrinia.Tests/Store/IndexCacheWritePathTests.cs` (create)

**Interfaces:**
- Consumes: Task 1's seam — `LoadEntriesFromSidecars` already normalizes. Do NOT re-apply.
- Produces: after a write, `_indexCache[scope]` is either absent (was absent before the write) or a NEW `CachedIndex` reflecting the write. Task 3 follows the same rule.

**The rule to implement, at both sites:**

```
if the scope IS cached:
    build a NEW List<ArtifactEntry> from the cached entries, with the written entry's slot
    replaced (Upsert, existing name) / appended (Upsert, new name) / omitted (Remove)
    install a NEW CachedIndex over that new list
else:
    _indexCache.TryRemove(scope, out _)   // leave absent; next read cold-loads
```

Entry identity: `SanitizeName(e.Name).Equals(SanitizeName(target), StringComparison.OrdinalIgnoreCase)` — the same rule `TryUpdateEntry`'s lookup already uses.

**Never** populate a partial cache. A cache holding only the just-written entry would make `LoadIndex` return one entry for the whole scope.

**ORDERING — decide explicitly and record the decision.** Every cache population today comes from `Directory.EnumerateFiles` order. Appending a new entry produces a different order than a cold load would, so a warm cache and a cold cache could disagree. Pick ONE:

- **(a)** Replace-in-slot for existing entries, append for new ones — and establish that no caller depends on `LoadIndex` ordering. The callers to check: `BundleFormatService.cs:60`, `EntityTypingEventHandler.cs:40`, `FileMemoryStore.cs:1253, 1334, 1647, 1798, 1908, 2022, 2085, 2215, 2233, 2253`, `IMemoryStore.cs:110`, `MemoryTools.Lifecycle.cs:1110, 1286, 1298`, `MemoryEndpoints.cs:291`. Most are `FirstOrDefault` by name and are order-independent; the enumerating ones (`ListScoped` at `:1647`/`:1798`, and the `MemoryTools.Lifecycle` ones) need an actual look at whether output order is observable.
- **(b)** Sort both the cold path and the warm update by the same key, so they are identical by construction.

State which you chose and why in your report and in a code comment. `Cold_And_Warm_Caches_Agree` below must pass either way.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/Store/IndexCacheWritePathTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests.Store;

/// <summary>
/// Pins the write path's cache behaviour. Writes must not re-read every sidecar in the scope,
/// and — load-bearing — must never mutate an existing CachedIndex.Entries in place, because
/// LoadIndex hands that list out BY REFERENCE. See
/// docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md.
/// </summary>
public sealed class IndexCacheWritePathTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public IndexCacheWritePathTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_cache_write_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static ArtifactEntry E(string name) =>
        new(name, "", 4, 1, DateTimeOffset.UtcNow);

    [Fact]
    public void AReaderHoldingALoadIndexResult_IsNotDisturbedByALaterWrite()
    {
        _store.Upsert(E("a"), "local");
        _store.Upsert(E("b"), "local");

        // The alias a reader holds. LoadIndex returns a live view of CachedIndex.Entries.
        var held = _store.LoadIndex("local");
        int countBefore = held.Count;
        var namesBefore = held.Select(e => e.Name).ToArray();

        _store.Upsert(E("c"), "local");
        _store.Remove("a", "local");

        held.Count.Should().Be(countBefore,
            because: "a write must install a NEW CachedIndex, never mutate the list a reader "
                   + "already holds — an in-place Add/Remove would be visible through this alias");
        held.Select(e => e.Name).Should().BeEquivalentTo(namesBefore);
    }

    [Fact]
    public void Cold_And_Warm_Caches_Agree()
    {
        _store.Upsert(E("a"), "local");
        _store.Upsert(E("b"), "local");
        _store.Upsert(E("c"), "local");
        _store.Remove("b", "local");

        var warm = _store.LoadIndex("local").Select(e => e.Name).ToArray();

        using var cold = new FileMemoryStore(_root);
        var coldNames = cold.LoadIndex("local").Select(e => e.Name).ToArray();

        warm.Should().BeEquivalentTo(coldNames,
            because: "a warm copy-on-write cache and a cold load of the same scope must "
                   + "describe the same scope");
    }

    [Fact]
    public void WriteAgainstAnUncachedScope_LeavesTheWholeScopeReadable()
    {
        // Populate on disk, then start a fresh store so the scope is NOT cached.
        _store.Upsert(E("a"), "local");
        _store.Upsert(E("b"), "local");

        using var fresh = new FileMemoryStore(_root);

        // First operation on this instance is a WRITE, against a cold cache.
        fresh.Upsert(E("c"), "local");

        fresh.LoadIndex("local").Select(e => e.Name)
            .Should().BeEquivalentTo(["a", "b", "c"],
                because: "a write against an uncached scope must not install a partial cache "
                       + "holding only the written entry");
    }

    [Fact]
    public void Upsert_ReplacesAnExistingEntry_RatherThanDuplicatingIt()
    {
        _store.Upsert(E("a"), "local");
        var updated = new ArtifactEntry("a", "second", 9, 1, DateTimeOffset.UtcNow);
        _store.Upsert(updated, "local");

        var entries = _store.LoadIndex("local").Where(e => e.Name == "a").ToList();
        entries.Should().HaveCount(1);
        entries[0].Description.Should().Be("second");
    }

    [Fact]
    public void Remove_DropsTheEntryFromTheCache()
    {
        _store.Upsert(E("a"), "local");
        _store.Upsert(E("b"), "local");
        _store.Remove("a", "local");

        _store.LoadIndex("local").Select(e => e.Name).Should().BeEquivalentTo(["b"]);
    }
}
```

- [ ] **Step 2: Run to verify they fail (and which ones)**

Run: `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~IndexCacheWritePathTests"`

Expected: `AReaderHoldingALoadIndexResult_IsNotDisturbedByALaterWrite` PASSES already (today's rebuild installs a new CachedIndex, satisfying the invariant the expensive way). That is correct and expected — it is a REGRESSION GUARD for the change you are about to make, not a red test. The others should pass too.

**This task is therefore refactoring under a green suite.** Record in your report which tests were green before your change. Their job is to fail if you implement the optimisation incorrectly.

- [ ] **Step 3: Implement copy-on-write in `Upsert`**

Replace `FileMemoryStore.cs:1049-1051`:

```csharp
            // Copy-on-write cache update. Never re-read the scope's sidecars here: that made a
            // single write cost a full directory enumeration plus one read and JSON parse per
            // entry, so writing N memories cost O(N^2) reads (finding H5).
            //
            // MUST install a NEW CachedIndex over a NEW list. LoadIndex hands Entries out BY
            // REFERENCE, and that is safe only because every cache write replaces the whole
            // object rather than mutating it — see
            // docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md.
            //
            // When the scope is not cached, leave it absent rather than installing a cache
            // holding only this entry; the next read cold-loads the whole scope.
            if (_indexCache.TryGetValue(scope, out var cachedIndex))
            {
                string target = SanitizeName(entry.Name);
                var next = new List<ArtifactEntry>(cachedIndex.Entries.Count + 1);
                bool replaced = false;
                foreach (var existing in cachedIndex.Entries)
                {
                    if (!replaced
                        && SanitizeName(existing.Name).Equals(target, StringComparison.OrdinalIgnoreCase))
                    {
                        next.Add(entry);
                        replaced = true;
                        continue;
                    }
                    next.Add(existing);
                }
                if (!replaced) next.Add(entry);
                _indexCache[scope] = new CachedIndex(next);
            }
            else
            {
                _indexCache.TryRemove(scope, out _);
            }
```

Note the entry cached is the one being written, which the caller already holds in normalized form; `WriteSidecar` persists that same value.

- [ ] **Step 4: Implement copy-on-write in `Remove`**

Replace `FileMemoryStore.cs:1118-1120` with the same shape, omitting the removed name:

```csharp
            // Copy-on-write cache update — see the matching comment in Upsert for why this must
            // install a NEW CachedIndex and must not re-read the scope's sidecars.
            if (_indexCache.TryGetValue(scope, out var cachedIndex))
            {
                string target = SanitizeName(name);
                var next = new List<ArtifactEntry>(cachedIndex.Entries.Count);
                foreach (var existing in cachedIndex.Entries)
                {
                    if (SanitizeName(existing.Name).Equals(target, StringComparison.OrdinalIgnoreCase))
                        continue;
                    next.Add(existing);
                }
                _indexCache[scope] = new CachedIndex(next);
            }
            else
            {
                _indexCache.TryRemove(scope, out _);
            }
```

`Remove` resolves a case-mismatched name to an actual sidecar path earlier in the method; use the name that was actually removed, not the caller's raw argument, if they differ.

- [ ] **Step 5: Verify**

Run: `dotnet build Scrinia.sln` → 0 errors
Run: `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~IndexCacheWritePathTests"` → all PASS
Run: `dotnet test Scrinia.sln --no-build` → green except the known flake

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/Store/IndexCacheWritePathTests.cs
git commit -m "perf(store): update the index cache copy-on-write instead of re-reading the scope"
```

---

## Task 3: `TryUpdateEntry` stops paying the full scan twice

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:1178-1180` (the read), `:1194-1196` (the write)
- Test: `tests/Scrinia.Tests/Store/IndexCacheWritePathTests.cs` (extend)

**Interfaces:**
- Consumes: Task 1's seam; Task 2's copy-on-write shape (mirror it exactly).
- Produces: nothing new.

**Context:** `TryUpdateEntry` does a full `LoadEntriesFromSidecars` scan to find ONE entry (`:1178`), then after writing does the full rebuild again (`:1195`). The comment at `:1172-1177` accepts the scan on the grounds that a hit and a miss pay the same thing. That reasoning stands — this task only avoids the cost when the answer is already in memory. Keep the comment and extend it rather than deleting it.

- [ ] **Step 1: Write the failing test**

Append to `IndexCacheWritePathTests`:

```csharp
    [Fact]
    public void TryUpdateEntry_UpdatesTheCache_AndKeepsTheRestOfTheScope()
    {
        _store.Upsert(E("a"), "local");
        _store.Upsert(E("b"), "local");

        bool ok = _store.TryUpdateEntry("a", "local", e => e with { Description = "patched" });

        ok.Should().BeTrue();
        var entries = _store.LoadIndex("local");
        entries.Select(x => x.Name).Should().BeEquivalentTo(["a", "b"]);
        entries.Single(x => x.Name == "a").Description.Should().Be("patched");
    }

    [Fact]
    public void TryUpdateEntry_AgainstAMissingEntry_ReturnsFalse_AndLeavesTheCacheIntact()
    {
        _store.Upsert(E("a"), "local");

        bool ok = _store.TryUpdateEntry("nope", "local", e => e with { Description = "x" });

        ok.Should().BeFalse();
        _store.LoadIndex("local").Select(x => x.Name).Should().BeEquivalentTo(["a"]);
    }

    [Fact]
    public void TryUpdateEntry_AgainstAnUncachedScope_StillFindsTheEntry()
    {
        _store.Upsert(E("a"), "local");
        using var fresh = new FileMemoryStore(_root);

        // First operation on this instance, cold cache — must fall back to the sidecar scan.
        bool ok = fresh.TryUpdateEntry("a", "local", e => e with { Description = "cold" });

        ok.Should().BeTrue();
        fresh.LoadIndex("local").Single(x => x.Name == "a").Description.Should().Be("cold");
    }
```

- [ ] **Step 2: Run to establish the baseline**

Run: `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~IndexCacheWritePathTests"`

Expected: all PASS (again, these are regression guards for a refactor). Record that they were green.

- [ ] **Step 3: Serve the read from the cache when warm**

Replace the lookup at `:1178-1180`:

```csharp
            // Serve the lookup from the cache when the scope is warm; fall back to the sidecar
            // scan when it is not. The scan's cost was accepted above on the grounds that a hit
            // and a miss pay the same thing — still true, and unchanged for the cold case. This
            // only avoids the cost when the answer is already in memory.
            var current = (_indexCache.TryGetValue(scope, out var warmIndex)
                    ? (IEnumerable<ArtifactEntry>)warmIndex.Entries
                    : LoadEntriesFromSidecars(storeDir))
                .FirstOrDefault(e => SanitizeName(e.Name).Equals(SanitizeName(subject), StringComparison.OrdinalIgnoreCase));
            if (current is null) return false;
```

- [ ] **Step 4: Replace the post-write rebuild with the copy-on-write update**

Replace `:1194-1196` with the same block Task 2 added to `Upsert`, keyed on `updated.Name`.

- [ ] **Step 5: Verify**

Run: `dotnet build Scrinia.sln` → 0 errors
Run: `dotnet test Scrinia.sln --no-build` → green except the known flake

Pay attention to `EntityTypingEventHandler.PromoteAsync`'s tests — it is the sole production caller of `TryUpdateEntry`.

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/Store/IndexCacheWritePathTests.cs
git commit -m "perf(store): TryUpdateEntry reads from the warm cache and updates it in place-free"
```

---

## Task 4: Precise `_cachedTopics` invalidation

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:1055` (`Upsert`), `:1127` (`Remove`)
- Test: `tests/Scrinia.Tests/Store/TopicCacheInvalidationTests.cs` (create)

**Interfaces:**
- Consumes: Tasks 2 and 3's cache shape.
- Produces: nothing new.

**Context:** `_cachedTopics = null` currently fires on EVERY write to a `local:` scope, so `DiscoverTopics` (`:2163-2176`) re-enumerates directories. The topic list changes only when a scope directory appears or a scope empties.

`Remove`'s existing comment (`:1122-1125`) explains why invalidation on remove is needed at all — DiscoverTopics would keep reporting an emptied topic. Preserve that reasoning; only narrow WHEN it fires.

- [ ] **Step 1: Write the failing test**

Create `tests/Scrinia.Tests/Store/TopicCacheInvalidationTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests.Store;

/// <summary>
/// The topic list changes when a scope directory appears or empties — not on every write into an
/// existing scope. Before this, every write to a "local:" scope nulled _cachedTopics and forced
/// DiscoverTopics to re-enumerate directories (finding H5).
/// </summary>
public sealed class TopicCacheInvalidationTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public TopicCacheInvalidationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_topic_cache_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static ArtifactEntry E(string name) =>
        new(name, "", 4, 1, DateTimeOffset.UtcNow);

    [Fact]
    public void ANewTopicScope_AppearsInDiscoverTopics_AfterItsFirstWrite()
    {
        _store.DiscoverTopics().Should().NotContain("alpha");

        _store.Upsert(E("first"), "local:alpha");

        _store.DiscoverTopics().Should().Contain("alpha",
            because: "the first write into a new topic scope creates its directory, which changes "
                   + "the topic list");
    }

    [Fact]
    public void AdditionalWritesIntoAnExistingTopic_KeepItDiscoverable()
    {
        _store.Upsert(E("first"), "local:alpha");
        _store.DiscoverTopics().Should().Contain("alpha");

        _store.Upsert(E("second"), "local:alpha");

        _store.DiscoverTopics().Should().Contain("alpha",
            because: "narrowing invalidation must not stale the topic list");
    }

    [Fact]
    public void EmptyingATopicScope_RemovesItFromDiscoverTopics()
    {
        _store.Upsert(E("only"), "local:beta");
        _store.DiscoverTopics().Should().Contain("beta");

        _store.Remove("only", "local:beta");

        _store.DiscoverTopics().Should().NotContain("beta",
            because: "DiscoverTopics must not keep reporting a topic with no remaining entries");
    }

    [Fact]
    public void RemovingOneOfSeveralEntries_KeepsTheTopic()
    {
        _store.Upsert(E("a"), "local:gamma");
        _store.Upsert(E("b"), "local:gamma");

        _store.Remove("a", "local:gamma");

        _store.DiscoverTopics().Should().Contain("gamma");
    }
}
```

Verify `DiscoverTopics()`'s exact name, accessibility and return shape against `FileMemoryStore.cs:2163-2176` before writing — adjust the assertions to whatever it actually returns (it may return fully-qualified scope strings rather than bare topic names). Do not guess; read it.

- [ ] **Step 2: Run to establish the baseline**

Run: `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~TopicCacheInvalidationTests"`

Expected: all four PASS against current code (today's blanket invalidation is correct, just expensive). These are regression guards.

- [ ] **Step 3: Narrow the invalidation in `Upsert`**

The directory is created by `Directory.CreateDirectory(storeDir)` at `:1038`. Capture whether it already existed, and invalidate only when it did not:

```csharp
            string storeDir = GetStoreDirForScope(scope);
            bool scopeDirIsNew = !Directory.Exists(storeDir);
            Directory.CreateDirectory(storeDir);
```

then at `:1055`:

```csharp
            // Only a NEW scope directory changes the topic list; writing into an existing topic
            // does not. Blanket invalidation here forced DiscoverTopics to re-enumerate
            // directories on every write (finding H5).
            if (scopeDirIsNew && scope.StartsWith("local:", StringComparison.Ordinal))
                _cachedTopics = null;
```

- [ ] **Step 4: Narrow the invalidation in `Remove`**

Invalidate only when the scope now holds no entries. After the copy-on-write cache update from Task 2, the remaining count is already in hand:

```csharp
            // Only an EMPTIED scope changes the topic list — see the reasoning above, which is
            // unchanged; this narrows only WHEN it fires.
            if (scope.StartsWith("local:", StringComparison.Ordinal)
                && !Directory.EnumerateFiles(storeDir, "*.meta.json").Any())
                _cachedTopics = null;
        }
```

Use the directory probe rather than the cache count: the cache may be absent, and `DiscoverTopics` answers from the filesystem, so the filesystem is the authority for this question.

- [ ] **Step 5: Verify**

Run: `dotnet build Scrinia.sln` → 0 errors
Run: `dotnet test Scrinia.sln --no-build` → green except the known flake

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/Store/TopicCacheInvalidationTests.cs
git commit -m "perf(store): invalidate the topic cache on scope lifecycle, not on every write"
```

---

## Task 5: Orphan detection short-circuits instead of materializing the store

**Files:**
- Modify: `src/Scrinia.Core/MaintenanceEventSink.cs:~148-155`
- Test: `tests/Scrinia.Tests/` — extend the existing MaintenanceEventSink coverage (locate it with `grep -rln "MaintenanceEventSink" tests/ --include=*.cs`)

**Interfaces:**
- Consumes: Tasks 2–4 (they are what make `ListScoped(null)` I/O-free).
- Produces: nothing new.

**Context:** the sink answers "does any entry reference me?" — a boolean — by materializing a `List<ScopedArtifact>` of every entry in the store and then calling `.Any(...)` on it:

```csharp
            var allEntries = store.ListScoped(null);
            bool hasInboundRefs = allEntries.Any(sa =>
                (sa.Entry.Keywords?.Any(k => k.Equals(inboundRefKey, StringComparison.OrdinalIgnoreCase)) == true)
                || (sa.Entry.Attributes is not null
                    && sa.Entry.Attributes.TryGetValue("ref", out var refs)
                    && refs.Any(v => v.Equals(sourceQualified, StringComparison.OrdinalIgnoreCase))));
```

**Do NOT route this through `EntityFactStore`.** It genuinely is a reverse index for `("ref", target)`, but `_factStore` is invalidated by the write that immediately precedes this sink, so a cold `GetEntityFactStore()` would call `ListScoped(null)` AND read or rebuild a `facts.json` sidecar per scope — strictly more work. The spec records this; do not re-litigate it.

**The change:** keep the same predicate and the same answer; stop materializing. `ListScoped` returns `List<ScopedArtifact>`, so it materializes inside the store too. Add a short-circuiting path rather than changing `ListScoped`'s contract:

- Add to `IMemoryStore` a default-implemented `bool AnyScoped(string? scopes, Func<ScopedArtifact, bool> predicate) => ListScoped(scopes).Any(predicate);` and override it in `FileMemoryStore` to enumerate lazily (`ListEphemeral()` then per-scope `LoadIndex(scope)`) and return on the first match.
- **Read `src/Scrinia.Core/IMemoryStore.cs:55-70` first.** That file carries an explicit warning about default-bodied interface members, and a prior review finding (H1) turned on exactly this. If adding a default-bodied member conflicts with what that comment requires, implement it as a concrete method on `FileMemoryStore` plus an explicit `HttpMemoryStore` implementation instead, and say so in your report. **Do not add a default body that silently gives `HttpMemoryStore` wrong behaviour.**

- [ ] **Step 1: Write the failing test**

Add to the existing MaintenanceEventSink test file a test asserting the orphan outcome is unchanged in both directions — an entry WITH an inbound ref is not marked orphan, an entry WITHOUT one is. Model the setup on the tests already in that file; do not invent a new harness. Include the dual-marked migration case the sink's own comment describes (attribute marker present, legacy keyword also present).

- [ ] **Step 2: Run to establish the baseline**

Run the file's tests. Expected: PASS. Record it. This task must not change behaviour, only cost.

- [ ] **Step 3: Implement the short-circuit**

Replace the materialize-then-`Any` with the lazy call, keeping the predicate byte-identical:

```csharp
            bool hasInboundRefs = store.AnyScoped(null, sa =>
                (sa.Entry.Keywords?.Any(k => k.Equals(inboundRefKey, StringComparison.OrdinalIgnoreCase)) == true)
                || (sa.Entry.Attributes is not null
                    && sa.Entry.Attributes.TryGetValue("ref", out var refs)
                    && refs.Any(v => v.Equals(sourceQualified, StringComparison.OrdinalIgnoreCase))));
```

Update the comment above it: it currently explains why this is computed before the update, which is still true and must be kept.

- [ ] **Step 4: Verify**

Run: `dotnet build Scrinia.sln` → 0 errors
Run: `dotnet test Scrinia.sln --no-build` → green except the known flake

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/MaintenanceEventSink.cs src/Scrinia.Core/IMemoryStore.cs src/Scrinia.Core/FileMemoryStore.cs tests/
git commit -m "perf(maintenance): answer the inbound-ref question lazily instead of listing the store"
```

---

## Task 6: One file operation per memory, not per vector

**Files:**
- Modify: `src/Scrinia.Core/Embeddings/VectorStore.cs` (add the batch API near `UpsertAsync`)
- Modify: `src/Scrinia.Core/Embeddings/MemoryVectorIndexer.cs:133-138`
- Test: `tests/Scrinia.Tests/Embeddings/VectorBatchReplaceTests.cs` (create)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:

```csharp
public async Task ReplaceMemoryVectorsAsync(
    string scope, string name,
    IReadOnlyList<(int? ChunkIndex, float[] Vector)> vectors,
    CancellationToken ct = default)
```

**Context.** `MemoryVectorIndexer.IndexAsync` currently does:

```csharp
        Func<Task> write = async () =>
        {
            await _store.RemoveAsync(scope, subject, ct);
            for (int i = 0; i < plan.Count; i++)
                await _store.UpsertAsync(scope, subject, plan[i].ChunkIndex, vectors[i], ct);
        };
```

`UpsertAsync` (`VectorStore.cs:~115-185`) per vector: takes the scope lock, rebuilds the in-memory list copy-on-write, `File.Exists` + `ProbeAppendable`, appends a delete op if superseding plus an add op, updates op counters, maybe compacts. `RemoveAsync` (`:~190-240`) does its own lock, probe and appends.

For a memory of K vectors replacing P existing ones: 1 + K locks, 1 + K probes, up to P + 2K append operations.

`UpsertAsync` and `RemoveAsync` STAY — they have callers at `src/Scrinia.Plugin.Embeddings.Cli/EmbeddingsTools.cs:101` and throughout the test suite. This ADDS an API.

**Semantics to preserve exactly:**
- Name/chunk matching is `OrdinalIgnoreCase`, matching `UpsertAsync`, `RemoveAsync` and `ReadEntryOps`' op-log replay.
- The ephemeral scope is not persisted (`scope.Equals("ephemeral", OrdinalIgnoreCase)` → in-memory only).
- A cold scope loads via `TryLoadFromDisk` and throws `VectorStoreUnavailableException` when not authoritative — same as `UpsertAsync`'s opening block.
- `AppendProbe.Unknown` → `VectorStoreUnavailableException`. `NotAppendable` → `SaveAsSvf3Async`.
- Compaction check after the batch: same ratio rule (`deletes/ops > 0.2 && deletes >= 10`).
- Copy-on-write on `_scopeVectors` — build a new list, never mutate the existing one. `GetVectors` hands out a reference, exactly like `LoadIndex` does.

**This is a correctness improvement too.** `IndexAsync`'s comment concedes the current loop is not atomic ("If the upsert loop is interrupted the next index pass rebuilds this memory cleanly"). One batched write makes the per-memory replace genuinely atomic.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/Embeddings/VectorBatchReplaceTests.cs`. Read `tests/Scrinia.Tests/Embeddings/MemoryVectorIndexerTests.cs` first and follow its `VectorStore` setup.

```csharp
using FluentAssertions;
using Scrinia.Core.Embeddings;

namespace Scrinia.Tests.Embeddings;

/// <summary>
/// ReplaceMemoryVectorsAsync must be observationally identical to the RemoveAsync-then-Upsert-loop
/// it replaces, while costing one locked file operation instead of one per vector (finding H5).
/// </summary>
public sealed class VectorBatchReplaceTests : IDisposable
{
    private readonly string _dir;

    public VectorBatchReplaceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"scrinia_vec_batch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static (int? ChunkIndex, float[] Vector)[] Plan(int count) =>
        Enumerable.Range(1, count).Select(i => ((int?)i, new[] { (float)i, 0f, 0f })).ToArray();

    [Fact]
    public async Task BatchReplace_MatchesTheRemoveThenLoop_ForTheSameInput()
    {
        string loopDir = Path.Combine(_dir, "loop");
        string batchDir = Path.Combine(_dir, "batch");
        Directory.CreateDirectory(loopDir);
        Directory.CreateDirectory(batchDir);

        var plan = Plan(5);

        using (var loop = new VectorStore(loopDir, expectedSignature: null))
        {
            await loop.RemoveAsync("local", "note");
            foreach (var (ci, v) in plan)
                await loop.UpsertAsync("local", "note", ci, v);
        }

        using (var batch = new VectorStore(batchDir, expectedSignature: null))
        {
            await batch.ReplaceMemoryVectorsAsync("local", "note", plan);
        }

        using var loopRead = new VectorStore(loopDir, expectedSignature: null);
        using var batchRead = new VectorStore(batchDir, expectedSignature: null);

        var loopVectors = loopRead.GetVectors("local")
            .OrderBy(v => v.ChunkIndex).Select(v => (v.Name, v.ChunkIndex)).ToArray();
        var batchVectors = batchRead.GetVectors("local")
            .OrderBy(v => v.ChunkIndex).Select(v => (v.Name, v.ChunkIndex)).ToArray();

        batchVectors.Should().BeEquivalentTo(loopVectors);
    }

    [Fact]
    public async Task BatchReplace_ShrinkingAMemory_DropsStaleHigherChunkIndices()
    {
        using var store = new VectorStore(_dir, expectedSignature: null);

        await store.ReplaceMemoryVectorsAsync("local", "note", Plan(6));
        await store.ReplaceMemoryVectorsAsync("local", "note", Plan(2));

        store.GetVectors("local").Select(v => v.ChunkIndex)
            .Should().BeEquivalentTo([1, 2],
                because: "replacing a memory's vectors must drop the ones it no longer has — "
                       + "a shrunk memory keeping chunk 3..6 is the bug the atomic replace prevents");
    }

    [Fact]
    public async Task BatchReplace_LeavesOtherMemoriesAlone()
    {
        using var store = new VectorStore(_dir, expectedSignature: null);

        await store.ReplaceMemoryVectorsAsync("local", "keep", Plan(2));
        await store.ReplaceMemoryVectorsAsync("local", "note", Plan(3));
        await store.ReplaceMemoryVectorsAsync("local", "note", Plan(1));

        store.GetVectors("local").Where(v => v.Name == "keep")
            .Select(v => v.ChunkIndex).Should().BeEquivalentTo([1, 2]);
    }

    [Fact]
    public async Task BatchReplace_SurvivesAReload()
    {
        using (var store = new VectorStore(_dir, expectedSignature: null))
            await store.ReplaceMemoryVectorsAsync("local", "note", Plan(4));

        using var reloaded = new VectorStore(_dir, expectedSignature: null);
        reloaded.GetVectors("local").Select(v => v.ChunkIndex)
            .Should().BeEquivalentTo([1, 2, 3, 4]);
    }

    [Fact]
    public async Task BatchReplace_WithAnEmptyPlan_RemovesEveryVectorForThatMemory()
    {
        using var store = new VectorStore(_dir, expectedSignature: null);

        await store.ReplaceMemoryVectorsAsync("local", "note", Plan(3));
        await store.ReplaceMemoryVectorsAsync("local", "note", []);

        store.GetVectors("local").Where(v => v.Name == "note").Should().BeEmpty();
    }
}
```

Verify `VectorEntry`'s member names (`Name`, `ChunkIndex`, `Vector`) and `VectorStore`'s constructor signature against the real source before running — adjust to what exists.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~VectorBatchReplaceTests"`

Expected: FAIL to compile — `ReplaceMemoryVectorsAsync` does not exist. Record the error.

- [ ] **Step 3: Implement `ReplaceMemoryVectorsAsync`**

Model it on `UpsertAsync`'s structure. One lock, one load-if-cold, one copy-on-write rebuild dropping every entry whose `Name` matches (`OrdinalIgnoreCase`) and appending all new ones, one probe, one batch of appends, one compaction check.

Write the appends as one sequence over the already-open path rather than reopening per op — that is the point of the task. If `AppendAddOpAsync`/`AppendDeleteOpAsync` (`VectorStore.cs:607`, `:627`) each open and close the file, add private overloads taking an open `Stream`/`StreamWriter` and keep the existing single-op methods delegating to them, so the other callers are unaffected.

Count ops for the compaction ratio the same way `UpsertAsync` does: one delete per superseded entry, one add per new vector.

- [ ] **Step 4: Wire the indexer**

`MemoryVectorIndexer.cs:133-138`:

```csharp
        // One locked file operation for the whole memory, not one per vector. This is also what
        // makes the per-memory replace genuinely atomic — the previous RemoveAsync-then-loop
        // could be interrupted between vectors, leaving the memory half-replaced until the next
        // index pass.
        Func<Task> write = async () =>
        {
            var batch = new (int? ChunkIndex, float[] Vector)[plan.Count];
            for (int i = 0; i < plan.Count; i++)
                batch[i] = (plan[i].ChunkIndex, vectors[i]);
            await _store.ReplaceMemoryVectorsAsync(scope, subject, batch, ct);
        };
```

Update the comment above it (`:130-132`) — the "clearing first" explanation describes the old shape.

- [ ] **Step 5: Verify**

Run: `dotnet build Scrinia.sln` → 0 errors
Run: `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~VectorBatchReplaceTests"` → PASS
Run: `dotnet test Scrinia.sln --no-build` → green except the known flake

The embeddings suites are the real gate here: `Scrinia.Plugin.Embeddings.Tests` and the reindex/indexer tests in `Scrinia.Tests`.

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Core/Embeddings/VectorStore.cs src/Scrinia.Core/Embeddings/MemoryVectorIndexer.cs tests/Scrinia.Tests/Embeddings/VectorBatchReplaceTests.cs
git commit -m "perf(embeddings): replace a memory's vectors in one locked write, atomically"
```

---

## Task 7: Measure it

**Files:**
- Create: `tests/Scrinia.Benchmarks/WritePathBenchmarks.cs`
- Create: `tests/Scrinia.Benchmarks/VectorWriteBenchmarks.cs`
- Create: `scripts/run-write-path-benchmarks.ps1`
- Create: `docs/superpowers/analysis/2026-08-17-write-path-cost-measurements.md`
- Modify: `tests/Scrinia.Benchmarks/Program.cs` (register the new benchmarks)

**Interfaces:**
- Consumes: Tasks 1–6.
- Produces: the measured numbers, including the vectors-per-memory figure that feeds the deferred decision on vector COUNT.

**Context:** follow `tests/Scrinia.Benchmarks/LoadIndexBenchmarks.cs` for structure and `docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md`'s "Measurement" section for the report format — environment block, then a before/after table with mean, CI, N and allocation, then a plain-English "Reading" paragraph.

**"Before" numbers** come from running the same benchmark against the merge-base commit. Get them by checking out `f89f204` into a temp worktree, running there, then discarding it — do NOT revert anything on this branch.

- [ ] **Step 1: Write the store write-path benchmark**

`WritePathBenchmarks.cs`: `[Params(100, 1000, 5000)] public int Entries;` — a benchmark that performs N sequential `Upsert` calls into a fresh scope on a temp directory, with `[IterationSetup]` creating a clean store and `[IterationCleanup]` deleting it. This is the measurement that shows the O(N²) → O(N) shape.

Add a second benchmark performing one `Upsert` into a scope already holding N entries — the marginal cost of a single write, which is what a user actually feels.

- [ ] **Step 2: Write the vector write benchmark**

`VectorWriteBenchmarks.cs`: `[Params(1, 10, 50, 200)] public int Vectors;` — index one memory of K vectors through `MemoryVectorIndexer`, against a `VectorStore` on a temp directory, using a deterministic fake `IEmbeddingProvider` (copy `FakeProvider` from `tests/Scrinia.Tests/CoreEmbeddingEventHandlerAppendTests.cs`). Report time and allocation.

- [ ] **Step 3: Write the runner script**

`scripts/run-write-path-benchmarks.ps1` — builds `-c Release` and runs the two benchmark classes, writing results under `artifacts/benchmarks/`. Follow whatever conventions `scripts/` already uses; read the directory first. The README or the analysis doc references the script; do not leave a copy-pasteable command block as the canonical path.

- [ ] **Step 4: Measure the vectors-per-memory figure**

Report, as a measured number, how many vectors a representative memory produces under the current per-element windowing — the input to the deferred decision on reducing vector COUNT. A small harness over a set of real memories from `.scrinia/memories/` is fine. **This branch reports it and changes nothing about it.**

- [ ] **Step 5: Run everything and write it up**

Run the script. Write `docs/superpowers/analysis/2026-08-17-write-path-cost-measurements.md` with the environment block, the before/after tables, and an honest "Reading" paragraph. If a number is disappointing or contradicts the premise, say so plainly — the measurement is the deliverable, not a favourable result.

- [ ] **Step 6: Commit**

```bash
git add tests/Scrinia.Benchmarks/ scripts/run-write-path-benchmarks.ps1 docs/superpowers/analysis/2026-08-17-write-path-cost-measurements.md
git commit -m "test(bench): measure the write path before and after"
```

---

## Task 8: Documentation

**Files:**
- Modify: `docs/architecture/` — the store and embeddings pages that describe the write path
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: Tasks 1–7.

- [ ] **Step 1: Find what is now stale**

```bash
grep -rn "LoadEntriesFromSidecars\|rebuild\|CachedIndex\|NormalizeAttributesKey" docs/ --include=*.md
grep -rn "UpsertAsync\|per vector\|RemoveAsync" docs/ --include=*.md
```

Read every hit. Anything describing the write path as re-reading the scope, or vector writes as per-vector, is now wrong.

- [ ] **Step 2: Correct it**

State the new shape: writes update the cache copy-on-write and never re-read the scope; the load seam owns normalization; a memory's vectors are replaced in one atomic locked write. Cite the measurements doc for the numbers rather than repeating them.

Do not describe the aliasing invariant as new — it is long-standing and documented; note only that the write path now honours it more cheaply.

- [ ] **Step 3: Changelog**

Add entries under the appropriate heading. These are user-visible as performance and as one behaviour change worth naming: per-memory vector replacement is now atomic.

- [ ] **Step 4: Verify and commit**

```bash
dotnet build Scrinia.sln
git add docs/ CHANGELOG.md
git commit -m "docs: describe the write path as it now behaves"
```

---

## Self-review notes

**Spec coverage:** A1 → Task 1. A2 → Task 2. A3 → Task 3. A4 → Task 4. B → Task 5. C → Task 6. D → Task 7. Testing section → distributed across the task that owns each behaviour. Scope boundaries → carried into Global Constraints and Task 5's "do not route through EntityFactStore".

**Known gaps handed to the implementer deliberately, with the decision framed rather than hidden:**
- The ordering decision in Task 2 (spec requires it be explicit; the caller list is provided).
- Task 5's `IMemoryStore` default-body question, which prior finding H1 makes genuinely risky — the plan requires reading the file's own warning first and offers the concrete alternative.
- Task 6's append-helper refactor depends on whether the existing helpers open per call; the plan states the requirement and the fallback rather than guessing the current shape.
