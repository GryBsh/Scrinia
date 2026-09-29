# Migrate Fidelity Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `scri migrate` report an accurate migration count, keep `--dry-run` truly read-only, and persist legacy-flat-dir entries — so `migrate` becomes a trustworthy completeness gate before the P6.6b member removal.

**Architecture:** `AttributeMigration.MigrateStore` currently measures "what needs migrating" by calling `store.ListScoped("all")` and testing `MigratesTo` on the results. But `ListScoped → LoadIndex → LoadIndexUnsafe` runs the read-time forward-fill **write-back** as a side effect of loading (on a cold cache), so entries are upgraded and written to disk *before* `MigrateStore` inspects them — yielding count 0 while disk was mutated, writing under `--dry-run`, and (for shadowed legacy-flat-dir entries) upgrading in memory but never persisting. The fix gives `MigrateStore` a **raw enumeration** (`FileMemoryStore.EnumerateRawForMigration`) that deserializes sidecars without the forward-fill write-back, measures `MigratesTo` on the un-upgraded entry (accurate count), and — only when not `dryRun` — writes the upgrade itself to the entry's *actual source dir* (`PersistMigratedEntry`), closing the flat-dir gap without scope promotion. The read-time forward-fill on normal reads is left untouched as a safety net.

**Tech Stack:** .NET 10, C#, xUnit + FluentAssertions. `AttributeMigration` (Scrinia.Core.Migration), `FileMemoryStore` (Scrinia.Core), `ScriniaCommands.Migration` (Scrinia CLI). Source-gen STJ sidecars.

## Global Constraints

- **NOT a scoring change.** `AttributeMigration.Forward` and all weights are untouched. The migrated data outcome is identical to what the read-time forward-fill already produces; flat-dir entries already scored via the in-memory `Forward` merge. **No ranking measurement gate is required** (unlike P6.6a).
- **Behavior-preserving for normal reads.** Do NOT change `LoadIndexUnsafe`'s read-time forward-fill write-back (the safety net) or any read/search path. Only `MigrateStore`, the two new `FileMemoryStore` internal primitives, and the `migrate` command change.
- **No scope promotion.** A shadowed legacy-flat-dir entry (reached via `ResolveLegacyFallbackDir`) MUST be written back to its **flat** dir, never the namespaced scope dir. This is the central correctness trap.
- **Cold-cache is the test contract.** The bug is invisible on a warm `_indexCache`. Every behavioral test MUST construct sidecars via one `FileMemoryStore` (or raw file write), dispose it, then run `migrate` through a **fresh** `FileMemoryStore` over the same dir.
- TWAE on product projects. CLI `dotnet build`/`dotnet test` is the warning source of truth; CS8019/CS8933-class IDE-only diagnostics are stale noise.
- Do NOT touch: `benchmarks/**`, `tests/Scrinia.Retrieval.Benchmarks.Tests/**`, `src/Scrinia.Mcp/plugins/opencode/*`, `.superpowers/**`. The working tree has pre-existing uncommitted changes under `benchmarks/**` — never stage them.
- Commit trailer EXACT on every commit:
  ```
  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
  ```
- Never push. Local `--no-ff` merge only, on explicit user confirmation (controller step).
- Branch: `feature/migrate-fidelity-fix` off `main` (base = current `main` HEAD).

---

## File Structure

- **Modify** `src/Scrinia.Core/FileMemoryStore.cs` — add two `internal` methods: `EnumerateRawForMigration()` (raw, hook-free scope walk yielding `(entry, scope, storeDir)`) and `PersistMigratedEntry(entry, storeDir, scope)` (targeted write to an explicit dir under the scope lock + cache invalidation).
- **Modify** `src/Scrinia.Core/Migration/AttributeMigration.cs` — retarget `MigrateStore` to take `FileMemoryStore` and drive it off the raw enumerator + targeted persist.
- **Modify** `src/Scrinia/Commands/ScriniaCommands.Migration.cs` — pass the `FileMemoryStore` (the `migrate` command always configures one) to `MigrateStore`.
- **Test** `tests/Scrinia.Tests/AttributeMigrationTests.cs` (or a new `MigrateColdCacheTests.cs`) — enumerator unit tests + cold-cache behavioral tests.

`FileMemoryStore` reuses its own existing privates: `LoadEntriesFromSidecars`, `LoadEntriesFromLegacyIndex`, `ResolveReadScopes`, `ResolveLegacyFallbackDir`, `GetStoreDirForScope`, `GetLockPath`, `GetIndexLock`, `WriteSidecar`, `_indexCache`, `_cachedTopics`. No new dependency.

---

### Task 1: Raw migration primitives on `FileMemoryStore`

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` (add two internal methods)
- Test: `tests/Scrinia.Tests/AttributeMigrationTests.cs`

**Interfaces:**
- Consumes (existing privates on `FileMemoryStore`): `IReadOnlyList<string> ResolveReadScopes(string? scopes)`, `List<ArtifactEntry> LoadEntriesFromSidecars(string storeDir)`, `static List<ArtifactEntry> LoadEntriesFromLegacyIndex(string storeDir)`, `string? ResolveLegacyFallbackDir(string scope)`, `string GetStoreDirForScope(string scope)`, `string GetLockPath(string scope)`, `ReaderWriterLockSlim GetIndexLock(string scope)`, `void WriteSidecar(ArtifactEntry entry, string storeDir)`, `ConcurrentDictionary<string,CachedIndex> _indexCache`, `List<string>? _cachedTopics`.
- Produces (new internals, consumed by Task 2):
  - `internal IEnumerable<(ArtifactEntry Entry, string Scope, string StoreDir)> EnumerateRawForMigration()`
  - `internal void PersistMigratedEntry(ArtifactEntry entry, string storeDir, string scope)`

- [ ] **Step 1: Write the failing enumerator test**

Add to `tests/Scrinia.Tests/AttributeMigrationTests.cs`. This asserts the enumerator returns **raw** (un-`Forward`-ed) entries and writes nothing. Uses the existing `TempWorkspace`/`MakeEntry` helpers. `FileMemoryStore` is `internal`-visible to the test project via `InternalsVisibleTo("Scrinia.Tests")`.

```csharp
[Fact]
public void EnumerateRawForMigration_YieldsRawEntries_AndWritesNothing()
{
    using var tmp = new TempWorkspace();
    // Write a legacy sidecar to disk via one store, then read it cold via a fresh store.
    var seed = tmp.NewStore();
    seed.Upsert(MakeEntry(tags: ["security"], anchors: ["how do I log in"], contentPreview: null) with { Name = "a", Description = "" }, "local");
    seed.Dispose();

    var cold = new FileMemoryStore(tmp.Root);
    var items = cold.EnumerateRawForMigration().ToList();

    var a = items.Single(i => i.Entry.Name == "a");
    a.Entry.Fields.Should().BeNull("enumeration must return the raw, un-forward-filled entry");
    a.Scope.Should().Be("local");
    a.StoreDir.Should().Be(cold.StoreDirForScopeTestHook("local")); // see Step 3 note
    // Enumerating must not mutate disk: re-read raw, still no fields.
    new FileMemoryStore(tmp.Root).EnumerateRawForMigration()
        .Single(i => i.Entry.Name == "a").Entry.Fields.Should().BeNull();
    cold.Dispose();
}
```

Note: `TempWorkspace` currently exposes only `NewStore()`; add a `public string Root => _tempDir;` accessor to it (small test-helper change) so a second `FileMemoryStore` can open the same dir. If `StoreDirForScopeTestHook` is undesirable, assert `a.StoreDir.EndsWith(Path.Combine("store"))` instead (the `local` scope maps to `.scrinia/store`) — pick the dir assertion that avoids adding a production test hook; the `EndsWith("store")` form is preferred (no production change).

- [ ] **Step 2: Run it to verify it FAILS**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~EnumerateRawForMigration" -v minimal`
Expected: FAIL to compile (`EnumerateRawForMigration` / `Root` do not exist yet).

- [ ] **Step 3: Add `EnumerateRawForMigration` to `FileMemoryStore`**

Place near `MigrateStore`'s data source, e.g. after `LoadEntriesFromLegacyIndex` (around line 837). It mirrors `ListScoped`'s scope resolution and `Upsert`'s legacy-merge shape, but **raw** — no `LoadIndex`, no forward-fill, no cache, no writes:

```csharp
/// <summary>
/// Yields every persistent entry across all scopes as RAW deserialized sidecars — WITHOUT the
/// read-time forward-fill write-back that <see cref="LoadIndex"/>/<see cref="ListScoped(string?)"/>
/// perform as a side effect of loading. Each item carries the entry, its scope, and the exact
/// directory the sidecar lives in: the primary scope dir, or — for a shadowed legacy-flat-dir
/// entry reached via <see cref="ResolveLegacyFallbackDir"/> — that flat dir, so a migration write
/// targets the correct dir and never promotes the entry's scope. Read-only; used by
/// <see cref="Migration.AttributeMigration.MigrateStore"/> to measure and persist accurately.
/// </summary>
internal IEnumerable<(ArtifactEntry Entry, string Scope, string StoreDir)> EnumerateRawForMigration()
{
    foreach (string scope in ResolveReadScopes("all"))
    {
        string primaryDir = GetStoreDirForScope(scope);
        var primary = LoadEntriesFromSidecars(primaryDir);
        if (primary.Count == 0)
            primary = LoadEntriesFromLegacyIndex(primaryDir); // raw index.json fallback
        var seen = new HashSet<string>(primary.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in primary)
            yield return (entry, scope, primaryDir);

        string? legacyDir = ResolveLegacyFallbackDir(scope);
        if (legacyDir is not null && Directory.Exists(legacyDir))
        {
            foreach (var le in LoadEntriesFromSidecars(legacyDir))
                if (!seen.Contains(le.Name))
                    yield return (le, scope, legacyDir); // write target = the FLAT dir (no promotion)
        }
    }
}
```

- [ ] **Step 4: Write the failing persist test**

This guards the scope-promotion trap and cache invalidation:

```csharp
[Fact]
public void PersistMigratedEntry_WritesToGivenDir_InvalidatesCache_NoPromotion()
{
    using var tmp = new TempWorkspace();
    var store = tmp.NewStore();
    // Target an explicit dir that is NOT the entry's scope dir (simulates a flat-dir write target).
    string flatDir = Path.Combine(tmp.Root, ".scrinia", "topics", "api");
    var upgraded = AttributeMigration.Forward(
        MakeEntry(tags: ["t"], contentPreview: null) with { Name = "flatnote", Description = "" });

    store.PersistMigratedEntry(upgraded, flatDir, "local-topic:memory/api");

    File.Exists(Path.Combine(flatDir, "flatnote.meta.json")).Should().BeTrue("written to the given dir");
    // NOT promoted into the namespaced scope dir:
    Directory.Exists(Path.Combine(tmp.Root, ".scrinia", "memories", "memory", "api")).Should().BeFalse();
    Directory.Exists(Path.Combine(tmp.Root, ".scrinia", "topics", "memory", "api")).Should().BeFalse();
    store.Dispose();
}
```

- [ ] **Step 5: Add `PersistMigratedEntry` to `FileMemoryStore`**

Mirror `Upsert`'s lock discipline (`ScriniaCommands`/reader safety), but write to the **given** dir instead of `GetStoreDirForScope(scope)`:

```csharp
/// <summary>
/// Writes an already-upgraded entry's sidecar to a SPECIFIC directory under <paramref name="scope"/>'s
/// exclusive lock, then invalidates that scope's cached index. Unlike <see cref="Upsert"/> (which always
/// targets <see cref="GetStoreDirForScope"/>), the explicit <paramref name="storeDir"/> lets a shadowed
/// legacy-flat-dir entry be rewritten in place (its flat dir) rather than promoted into the namespaced
/// scope dir. Used only by the migrate path.
/// </summary>
internal void PersistMigratedEntry(ArtifactEntry entry, string storeDir, string scope)
{
    using var fileLock = FileLock.AcquireExclusive(GetLockPath(scope));
    var lk = GetIndexLock(scope);
    lk.EnterWriteLock();
    try
    {
        Directory.CreateDirectory(storeDir);
        WriteSidecar(entry, storeDir);
        _indexCache.TryRemove(scope, out _); // next read rebuilds from the now-upgraded sidecars
        if (scope.StartsWith("local-topic:", StringComparison.Ordinal))
            _cachedTopics = null;
    }
    finally
    {
        lk.ExitWriteLock();
    }
}
```

Also add to the test helper `TempWorkspace` (top of `AttributeMigrationTests.cs`): `public string Root => _tempDir;`

- [ ] **Step 6: Run both Task 1 tests to verify PASS**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~EnumerateRawForMigration|FullyQualifiedName~PersistMigratedEntry_WritesToGivenDir" -v minimal`
Expected: both PASS.

- [ ] **Step 7: Build Core (TWAE)**

Run: `dotnet build src/Scrinia.Core/Scrinia.Core.csproj -c Debug`
Expected: 0 warnings, 0 errors.

- [ ] **Step 8: Commit**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/AttributeMigrationTests.cs
git commit -m "feat(core): add raw hook-free migration primitives to FileMemoryStore"
```

---

### Task 2: Drive `MigrateStore` off the raw path; fix count, dry-run, flat-dir

**Files:**
- Modify: `src/Scrinia.Core/Migration/AttributeMigration.cs` (`MigrateStore` signature + body)
- Modify: `src/Scrinia/Commands/ScriniaCommands.Migration.cs` (pass the `FileMemoryStore`)
- Test: `tests/Scrinia.Tests/AttributeMigrationTests.cs`

**Interfaces:**
- Consumes: `FileMemoryStore.EnumerateRawForMigration()`, `FileMemoryStore.PersistMigratedEntry(entry, storeDir, scope)` (Task 1), `MigratesTo`, existing `MigrateSummary`.
- Produces: `MigrateStore(FileMemoryStore store, bool dryRun = false, Action<int,int>? onProgress = null) -> MigrateSummary` with an accurate `Migrated` count, no writes under `dryRun`, and flat-dir entries persisted+counted.

- [ ] **Step 1: Write the failing cold-cache behavioral tests**

Add to `tests/Scrinia.Tests/AttributeMigrationTests.cs`. All construct sidecars via a first store, dispose, then migrate through a **fresh** store (cold cache). These fail today (count 0, dry-run writes).

```csharp
[Fact]
public void MigrateStore_ColdCache_AccurateCount_AndWrites()
{
    using var tmp = new TempWorkspace();
    var seed = tmp.NewStore();
    seed.Upsert(MakeEntry(tags: ["security"], anchors: ["how to log in"], contentPreview: null) with { Name = "legacy", Description = "" }, "local");
    seed.Upsert(MakeEntry(fields: new() { ["topic"] = ["auth"] }) with { Name = "clean" }, "local"); // already current
    seed.Dispose();

    var cold = new FileMemoryStore(tmp.Root);
    var s = AttributeMigration.MigrateStore(cold);

    s.Total.Should().Be(2);
    s.Migrated.Should().Be(1, "only the legacy entry needed migrating");
    s.Skipped.Should().Be(1);
    new FileMemoryStore(tmp.Root).LoadIndex("local").First(e => e.Name == "legacy")
        .Fields!.Should().ContainKeys("tag", "anchor");
    cold.Dispose();
}

[Fact]
public void MigrateStore_ColdCache_Idempotent_SecondRunReportsZero()
{
    using var tmp = new TempWorkspace();
    var seed = tmp.NewStore();
    seed.Upsert(MakeEntry(tags: ["x"], contentPreview: null) with { Name = "a", Description = "" }, "local");
    seed.Dispose();

    AttributeMigration.MigrateStore(new FileMemoryStore(tmp.Root)).Migrated.Should().Be(1);
    var second = AttributeMigration.MigrateStore(new FileMemoryStore(tmp.Root));
    second.Migrated.Should().Be(0, "sidecars are upgraded on disk after run 1");
    second.Skipped.Should().Be(second.Total);
}

[Fact]
public void MigrateStore_ColdCache_DryRun_WritesNothing_ButCountsTruthfully()
{
    using var tmp = new TempWorkspace();
    var seed = tmp.NewStore();
    seed.Upsert(MakeEntry(tags: ["x"], contentPreview: null) with { Name = "a", Description = "" }, "local");
    seed.Dispose();

    var cold = new FileMemoryStore(tmp.Root);
    var s = AttributeMigration.MigrateStore(cold, dryRun: true);

    s.Migrated.Should().Be(1, "dry-run still reports the true count");
    new FileMemoryStore(tmp.Root).EnumerateRawForMigration()
        .Single(i => i.Entry.Name == "a").Entry.Fields
        .Should().BeNull("dry-run must not write");
    cold.Dispose();
}
```

- [ ] **Step 2: Write the failing flat-dir tests (gap C)**

Reproduce the shadowed-flat-dir layout from the investigation: a namespaced scope dir present, plus a legacy entry in the bare flat dir. After migrate, the **flat** sidecar gains `fields`, the entry is counted, and it is NOT written into the namespaced dir.

```csharp
[Fact]
public void MigrateStore_ColdCache_LegacyFlatDir_Persisted_Counted_NoPromotion()
{
    using var tmp = new TempWorkspace();
    var seed = tmp.NewStore();
    // Namespaced sibling so the bare flat dir is shadowed (reached only via the legacy merge).
    seed.Upsert(MakeEntry(fields: new() { ["topic"] = ["api"] }) with { Name = "nsnote" }, seed.BuildScopedTopicScopeTestHook("api"));
    // Legacy entry in the bare flat dir .scrinia/topics/api
    seed.Upsert(MakeEntry(tags: ["legacy"], contentPreview: null) with { Name = "flatnote", Description = "" }, "local-topic:api");
    seed.Dispose();

    var cold = new FileMemoryStore(tmp.Root);
    var s = AttributeMigration.MigrateStore(cold);

    s.Migrated.Should().BeGreaterThanOrEqualTo(1, "the shadowed flat entry needed migrating");
    // The FLAT sidecar is upgraded in place:
    string flatMeta = Path.Combine(tmp.Root, ".scrinia", "topics", "api", "flatnote.meta.json");
    File.ReadAllText(flatMeta).Should().Contain("\"fields\"").And.Contain("\"tag\"");
    cold.Dispose();
}
```

Note on `BuildScopedTopicScope`: the investigation used `BuildScopedTopicScope("api")` to create the `memory/api` namespaced scope. If that helper is not test-accessible, construct the namespaced scope string the same way the production code does (verify the exact scope string / dir — `.scrinia/topics/memory/api/` or the v2 `.scrinia/memories/...` path — by reading `GetStoreDirForScope`/`DiscoverTopics` before writing the test) and Upsert to it directly; do NOT invent a scope string. If a production test hook is required to reach it, prefer building the on-disk dir + sidecar by hand (raw file write) over adding a production accessor.

- [ ] **Step 3: Run the new tests to verify they FAIL**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~MigrateStore_ColdCache" -v minimal`
Expected: FAIL (compile error on the `FileMemoryStore` overload not existing yet, or assertion failures showing today's count=0 / dry-run-writes behavior).

- [ ] **Step 4: Rewrite `MigrateStore` to use the raw path**

In `src/Scrinia.Core/Migration/AttributeMigration.cs`, replace the current `MigrateStore` (lines ~130-144). Narrow the parameter to `FileMemoryStore` and drive off the raw enumerator:

```csharp
/// <summary>Forward-fills every persistent entry across all scopes, reporting an accurate count.
/// Reads each sidecar RAW (via <see cref="FileMemoryStore.EnumerateRawForMigration"/>) — bypassing the
/// read-time forward-fill write-back — so the count reflects entries that actually needed migrating and
/// <paramref name="dryRun"/> writes nothing. Legacy-flat-dir entries are persisted back to their own dir
/// (no scope promotion). Idempotent: a re-run over upgraded sidecars reports 0.</summary>
public static MigrateSummary MigrateStore(FileMemoryStore store, bool dryRun = false, Action<int, int>? onProgress = null)
{
    var items = store.EnumerateRawForMigration().ToList();
    int total = items.Count, migrated = 0, done = 0;
    foreach (var (entry, scope, storeDir) in items)
    {
        if (MigratesTo(entry, out var next))
        {
            if (!dryRun) store.PersistMigratedEntry(next, storeDir, scope);
            migrated++;
        }
        onProgress?.Invoke(++done, total);
    }
    return new MigrateSummary(total, migrated, total - migrated);
}
```

(Remove the old `IMemoryStore`-based body. `MigrateSummary`, `MigratesTo`, `Forward` are unchanged.)

- [ ] **Step 5: Update the `migrate` command to pass the `FileMemoryStore`**

In `src/Scrinia/Commands/ScriniaCommands.Migration.cs` (around line 508-515), the command already calls `WorkspaceSetup.Configure`, so `MemoryStoreContext.Current` is a `FileMemoryStore`. Narrow it explicitly:

```csharp
        WorkspaceSetup.Configure(workspaceRoot);
        if (MemoryStoreContext.Current is not FileMemoryStore store)
            throw new InvalidOperationException("Migrate requires a file-backed workspace store.");

        if (!skipBackup && !dryRun)
            BackupScriniaDir(ScriniaArtifactStore.WorkspaceRootPath);

        int lastDone = -1;
        var summary = AttributeMigration.MigrateStore(store, dryRun, (done, total) =>
        {
            // ...unchanged progress callback...
        });
```

(Add a `using Scrinia.Core;` if `FileMemoryStore` is not already in scope. Confirm the file compiles.)

- [ ] **Step 6: Run the new tests + full migration suite to verify PASS**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AttributeMigrationTests|FullyQualifiedName~MigrateStore_ColdCache" -v minimal`
Expected: all new cold-cache tests PASS. The pre-existing warm-cache tests (`MigrateStore_DryRun_ReportsButDoesNotWrite`, `MigrateStore_ForwardFillsLegacyEntries_AndIsIdempotent`, `MigrateStore_CoversTopicScopes`) MUST still PASS unchanged — they now flow through the raw path (which reads the Upserted on-disk sidecars) and should report the same counts. If any pre-existing test's `var store = tmp.NewStore();` now fails to bind to the narrowed `FileMemoryStore` parameter, it is because `NewStore()` returns `FileMemoryStore` already — no change needed; if a test used an `IMemoryStore`-typed variable, retype it to `FileMemoryStore`. Report any test you touch and why.

- [ ] **Step 7: Full suite + product build (regression)**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -v minimal` then `dotnet build src/Scrinia/Scrinia.csproj -c Debug`
Expected: green suite; CLI builds 0 warnings (TWAE).

- [ ] **Step 8: Commit**

```bash
git add src/Scrinia.Core/Migration/AttributeMigration.cs src/Scrinia/Commands/ScriniaCommands.Migration.cs tests/Scrinia.Tests/AttributeMigrationTests.cs
git commit -m "fix(migrate): accurate count, dry-run safety, and legacy-flat-dir persistence via raw path"
```

---

## Self-Review

- **Spec coverage:** Fixes all three confirmed defects (accurate count / dry-run dry / flat-dir persistence) from `scratchpad/migrate-count-fix-design.md` §1-3 via the recommended Fork A. Count measured on raw entries (Task 2 Step 4); `dryRun` gates the only writer (`PersistMigratedEntry`); flat-dir entries yielded with their flat dir as write target (Task 1 Step 3) and persisted there (Task 2 flat-dir test). ✅
- **Placeholder scan:** every code step shows complete code; the two "verify the exact scope string / dir before writing the test" notes are deliberate guards against inventing scope strings, not deferred logic. ✅
- **Type consistency:** `EnumerateRawForMigration() : IEnumerable<(ArtifactEntry, string, string)>` and `PersistMigratedEntry(ArtifactEntry, string, string)` are used exactly as defined in `MigrateStore`; `MigrateStore` narrowed to `FileMemoryStore` and the command passes one; `MigrateSummary(Total, Migrated, Skipped)` unchanged. ✅
- **No scoring change / no read-path change:** `Forward`, weights, and `LoadIndexUnsafe`'s safety-net write-back are untouched. ✅
