# Store Unification and the v3 Memory Layout — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete `ScriniaArtifactStore` so `FileMemoryStore` is the only filesystem store, make `PathRouter` the single path authority, and collapse the three-way `store/`+`topics/`+`memories/` layout into `.scrinia/memories/` with no read fallback.

**Architecture:** Store-first sequencing. Tasks 1–4 remove the duplicate store under the existing test suite with no behaviour change. Task 5 makes `PathRouter` load-bearing. Tasks 6–9 switch the on-disk layout, wire automatic migration through a single gated choke point, delete the legacy machinery, and expose `scri migrate`.

**Tech Stack:** .NET 10 (`net10.0`), C#, xunit 2.9.3, FluentAssertions 6.12.2, Spectre.Console (CLI rendering).

**Design:** [`docs/superpowers/specs/2026-08-03-store-unification-design.md`](../specs/2026-08-03-store-unification-design.md)
**Branch:** `store-unification` (created from `main` at `97fba90`)

## Global Constraints

- **Target framework:** `net10.0`. Do not change `Directory.Build.props`.
- **`TreatWarningsAsErrors=true`** for every non-`.Tests`, non-`.Benchmarks` project. Any new warning in `src/` fails the build. Test projects are exempt.
- **`AnalysisMode=Recommended`**, `AnalysisLevel=latest`. Suppressed categories are listed in `Directory.Build.props`; do not add to that list.
- **No new `IMemoryStore` members.** Workstream B narrows this interface; widening it here works against that. All replacements must use members that already exist.
- **Atomic writes:** all artifact/sidecar writes go through `FileMemoryStore.AtomicWriteAllText` / `AtomicFileCopy`. Never `File.WriteAllText` directly on a store file.
- **JSON:** use the source-generated `JsonSerializerContext` (`FileStoreJsonContext`, `CliJsonContext`, `ScriniaMcpJsonContext`). Contextless `JsonSerializer` calls violate the `AGENTS.md` rule.
- **`.scrinia/` changes are part of your commits** (workspace `CLAUDE.md`).
- **Prefix shell commands with `rtk`** per the user's global instructions, including inside `&&` chains.
- **Test command:** `dotnet test tests/Scrinia.Tests/ -c Debug`. CI runs each test project separately (`.github/workflows/ci.yml`).
- **Baseline:** `dotnet build Scrinia.sln -c Debug` is clean (0 errors) at `97fba90`. Record the passing test count before Task 1 and treat it as the floor for every subsequent task.

## File Structure

**Created**
| File | Responsibility |
|---|---|
| `src/Scrinia.Core/StoreLayoutMigration.cs` | The single migration engine. Plans and executes any-older-layout → v3. Two public entry points, one private implementation. |
| `tests/Scrinia.Tests/StoreLayoutMigrationTests.cs` | Migration coverage: mapping, collisions, resumption, idempotency, vector dirs, trigger equivalence. |

**Deleted**
| File | Reason |
|---|---|
| `src/Scrinia/Mcp/ScriniaArtifactStore.cs` | The duplicate store (1,052 lines). |
| `tests/Scrinia.Tests/ScriniaArtifactStoreTests.cs` | 24 tests duplicate existing coverage; 17 are ported to `FileMemoryStoreTests.cs` in Task 4. |

**Modified**
| File | Change |
|---|---|
| `src/Scrinia/Commands/WorkspaceSetup.cs` | Add `Root` property; drop `ScriniaArtifactStore.Configure`. |
| `src/Scrinia/Commands/ScriniaCommands.{Setup,Hooks,Bundle,Memory,Migration}.cs` | Repoint 38 call sites. |
| `src/Scrinia/Commands/ScriniaCommands.Migration.cs` | Fix `consolidate` store mixing; replace `MigrateV1` with `Migrate`. |
| `src/Scrinia.Core/FileMemoryStore.cs` | `MemoriesRoot` gate, v3 routing, delete fallback machinery. |
| `src/Scrinia.Core/PathRouter.cs` | Becomes load-bearing; `ToLegacyPath` deleted. |
| `src/Scrinia.Core/MemoryNaming.cs` | Delete `StripNamespacePrefix`, `ReservedNamespaceDirs`, `FormatRoundTrip`; flatten `BuildScopedTopicScope`. |
| `src/Scrinia.Core/IMemoryStore.cs` | Delete `FormatRoundTripQualifiedName`. |
| `src/Scrinia.Mcp/MemoryTools.Core.cs` | Agent path writes via the store, not a `.scrinia/agent` literal. |
| `tests/Scrinia.Tests/{TestHelpers,FileMemoryStoreTests}.cs` | Drop 3 globals; absorb 17 ported tests; convert 13 fallback tests. |
| `docs/cli-reference.md` | Replace the `scri migrate_v1` section. |
| `CHANGELOG.md` | Unreleased entry. |

---

### Task 1: Root accessor and the 19 `WorkspaceRootPath` renames

**Files:**
- Modify: `src/Scrinia/Commands/WorkspaceSetup.cs:16-26`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Setup.cs:82,149,195,331,534`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Hooks.cs:32,44,63,69`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Migration.cs:39,353,557`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Bundle.cs:145`
- Modify: `src/Scrinia/Commands/WorkspaceSetup.cs:72,169,291,463,573,597`
- Test: `tests/Scrinia.Tests/WorkspaceSetupRootTests.cs` (create)

**Interfaces:**
- Consumes: nothing (first task).
- Produces: `internal static string WorkspaceSetup.Root { get; }` — the resolved workspace root. Throws `InvalidOperationException` if read before `Configure`. Used by Tasks 2, 3, 8, 9.

- [ ] **Step 1: Write the failing test**

Create `tests/Scrinia.Tests/WorkspaceSetupRootTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Commands;

namespace Scrinia.Tests;

public sealed class WorkspaceSetupRootTests
{
    [Fact]
    public void Root_AfterConfigure_ReturnsResolvedRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"scrinia_root_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        try
        {
            WorkspaceSetup.Configure(dir);
            WorkspaceSetup.Root.Should().Be(Path.GetFullPath(dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug --filter "FullyQualifiedName~WorkspaceSetupRootTests"`
Expected: FAIL — compile error, `'WorkspaceSetup' does not contain a definition for 'Root'`.

- [ ] **Step 3: Add the `Root` property**

In `src/Scrinia/Commands/WorkspaceSetup.cs`, add the backing field beside `_pluginHost` and `_embeddingProvider`, and set it inside `Configure`:

```csharp
    private static string? _root;

    /// <summary>
    /// The resolved workspace root for this process, cached by <see cref="Configure"/>.
    /// Replaces the former ScriniaArtifactStore.WorkspaceRootPath static.
    /// </summary>
    internal static string Root =>
        _root ?? throw new InvalidOperationException(
            "WorkspaceSetup.Configure must be called before reading Root.");

    internal static void Configure(string? workspaceRoot)
    {
        string root = WorkspaceRoot.Resolve(workspaceRoot, Directory.GetCurrentDirectory());
        _root = root;

        ScriniaArtifactStore.Configure(root);
        var rankerOptions = RankerOptions.FromConfig(GetConfigValue);
        MemoryStoreContext.Current = new FileMemoryStore(root, rankerOptions);
    }
```

Leave `ScriniaArtifactStore.Configure(root)` in place for now — it is removed in Task 4.

- [ ] **Step 4: Run test to verify it passes**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug --filter "FullyQualifiedName~WorkspaceSetupRootTests"`
Expected: PASS.

- [ ] **Step 5: Replace all 19 read sites**

Mechanical replacement of `ScriniaArtifactStore.WorkspaceRootPath` → `WorkspaceSetup.Root` at exactly these locations:

```
ScriniaCommands.Bundle.cs:145
ScriniaCommands.Hooks.cs:32,44,63,69
ScriniaCommands.Migration.cs:39,353,557
ScriniaCommands.Setup.cs:82,149,195,331,534
WorkspaceSetup.cs:72,169,291,463,573,597
```

`WorkspaceSetup.cs` sites need no qualifier — use bare `Root`.

- [ ] **Step 6: Verify no sites remain**

Run: `rtk grep -rn "ScriniaArtifactStore.WorkspaceRootPath" --include=*.cs src/`
Expected: no output.

- [ ] **Step 7: Run the full suite**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug`
Expected: PASS, count ≥ the Task-0 baseline.

- [ ] **Step 8: Commit**

```bash
rtk git add src/Scrinia/Commands/ tests/Scrinia.Tests/WorkspaceSetupRootTests.cs && rtk git commit -m "refactor(cli): replace ScriniaArtifactStore.WorkspaceRootPath with WorkspaceSetup.Root"
```

---

### Task 2: Repoint the 9 formatting and path helpers

**Files:**
- Modify: `src/Scrinia/Commands/ScriniaCommands.Bundle.cs:122,143,163,178,219`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs:35,90,124,282,290,376,377`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Migration.cs:418,455`

**Interfaces:**
- Consumes: `WorkspaceSetup.Root` (Task 1).
- Produces: nothing new. After this task the CLI's only remaining `ScriniaArtifactStore` uses are in `CompactEntryToSingleChunk` (Task 3) and `WorkspaceSetup.Configure` (Task 4).

- [ ] **Step 1: Add the store accessor**

These call sites need an `IMemoryStore`. Add to `src/Scrinia/Commands/WorkspaceSetup.cs` beside `Root`:

```csharp
    /// <summary>
    /// The configured store for this process. Throws if <see cref="Configure"/> has not run.
    /// </summary>
    internal static IMemoryStore Store =>
        MemoryStoreContext.Current ?? throw new InvalidOperationException(
            "WorkspaceSetup.Configure must be called before reading Store.");
```

- [ ] **Step 2: Replace the call sites**

Every replacement is `ScriniaArtifactStore.X(...)` → `WorkspaceSetup.Store.X(...)`, same arguments:

| File:line | Member |
|---|---|
| `Bundle.cs:122,163,219` | `SanitizeName` |
| `Bundle.cs:143` | `GetStoreDirForScope("local")` |
| `Bundle.cs:178` | `GenerateContentPreview` |
| `Memory.cs:35` | `ListScoped(scopes)` |
| `Memory.cs:90,124,282,290,377` | `FormatQualifiedName` |
| `Memory.cs:376` | `ParseQualifiedName` |
| `Migration.cs:418,455` | `FormatQualifiedName` |

- [ ] **Step 3: Verify only the expected sites remain**

Run: `rtk grep -rn "ScriniaArtifactStore\." --include=*.cs src/Scrinia/Commands/`
Expected: exactly 5 lines — `Migration.cs:383,493,499,513` and `WorkspaceSetup.cs:20`.

- [ ] **Step 4: Run the full suite**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug`
Expected: PASS at baseline count. This task is behaviour-preserving — both classes implement these members identically.

- [ ] **Step 5: Commit**

```bash
rtk git add src/Scrinia/Commands/ && rtk git commit -m "refactor(cli): route formatting and path helpers through IMemoryStore"
```

---

### Task 3: Fix `scri consolidate` to use a single store

**Files:**
- Modify: `src/Scrinia/Commands/ScriniaCommands.Migration.cs:383,491-514`
- Test: `tests/Scrinia.Tests/ConsolidateSingleStoreTests.cs` (create)

**Interfaces:**
- Consumes: `WorkspaceSetup.Store` (Task 2).
- Produces: `CompactEntryToSingleChunk(ScopedArtifact item, IMemoryStore store, CancellationToken ct)` — note the parameter is now non-nullable `IMemoryStore`, changed from `IMemoryStore?`. Task 9 does not depend on this.

This is the **only intentional behaviour change** in the workstream. Today the method reads through one store and writes through the other, bypassing the write lock and leaving `FileMemoryStore._indexCache` stale.

- [ ] **Step 1: Write the failing test**

Create `tests/Scrinia.Tests/ConsolidateSingleStoreTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class ConsolidateSingleStoreTests
{
    [Fact]
    public async Task CompactEntryToSingleChunk_InvalidatesTheStoreIndexCache()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;
        const string scopeName = "local:sessions";
        const string subject = "day-one";

        string artifact = Nmp2ChunkedEncoder.EncodeChunks(["alpha chunk", "beta chunk"]);
        await store.WriteArtifactAsync(subject, scopeName, artifact);
        store.Upsert(
            new ArtifactEntry(
                Name: subject,
                OriginalBytes: 22,
                ChunkCount: 2,
                CreatedAt: DateTimeOffset.UtcNow.AddDays(-90),
                ContentPreview: "alpha"),
            scopeName);

        // Warm the in-memory index cache so a stale read is observable.
        store.LoadIndex(scopeName).Single().ChunkCount.Should().Be(2);

        var item = new ScopedArtifact(scopeName, store.LoadIndex(scopeName).Single());
        await ScriniaCommands.CompactEntryToSingleChunk(item, store, CancellationToken.None);

        // Must reflect the compaction without a process restart.
        store.LoadIndex(scopeName).Single().ChunkCount.Should().Be(1);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug --filter "FullyQualifiedName~ConsolidateSingleStoreTests"`
Expected: FAIL — final assertion reads `2`, because `ScriniaArtifactStore.Upsert` wrote the sidecar without invalidating `FileMemoryStore._indexCache`.

- [ ] **Step 3: Route every call through the store**

In `src/Scrinia/Commands/ScriniaCommands.Migration.cs`, change the signature and the four calls:

```csharp
    internal static async Task CompactEntryToSingleChunk(ScopedArtifact item, IMemoryStore store, CancellationToken ct)
    {
        string path = store.FindArtifactPath(item.Entry.Name, item.Scope);
        if (!File.Exists(path)) return;

        string artifact = await store.ReadArtifactAsync(item.Entry.Name, item.Scope, ct);
        if (Nmp2ChunkedEncoder.GetChunkCount(artifact) <= 1) return;

        store.ArchiveVersion(item.Entry.Name, item.Scope);

        byte[] allBytes = Nmp2Strategy.Instance.Decode(artifact);
        string fullText = System.Text.Encoding.UTF8.GetString(allBytes);
        string compacted = Nmp2ChunkedEncoder.Encode(fullText);
        await store.WriteArtifactAsync(item.Entry.Name, item.Scope, compacted, ct);

        var updated = item.Entry with
        {
            ChunkCount = 1,
            OriginalBytes = allBytes.LongLength,
            UpdatedAt = DateTimeOffset.UtcNow,
            ChunkEntries = null,
        };
        store.Upsert(updated, item.Scope);
```

`store.WriteArtifactAsync` replaces the bare `File.WriteAllText(path, compacted)` at the old line 504, satisfying the atomic-write constraint.

- [ ] **Step 4: Make the store non-optional at the call site**

At line 383 replace `ScriniaArtifactStore.ListScoped(null)` with `WorkspaceSetup.Store.ListScoped(null)`. Then in the `if (!dryRun)` block, load plugins unconditionally when there are candidates and pass the non-null store:

```csharp
            IMemoryStore store = WorkspaceSetup.Store;
            if (compactionCandidates.Count > 0)
                await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

            foreach (var item in compactionCandidates)
            {
                try
                {
                    await CompactEntryToSingleChunk(item, store, cancellationToken);
```

- [ ] **Step 5: Run the new test**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug --filter "FullyQualifiedName~ConsolidateSingleStoreTests"`
Expected: PASS.

- [ ] **Step 6: Run the consolidate regression tests**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug --filter "FullyQualifiedName~ConsolidateReembed"`
Expected: PASS. This is the one place the design predicts a test edit — if a test asserted the old null-store path, update it to pass `WorkspaceSetup.Store` and record why in the commit message.

- [ ] **Step 7: Run the full suite**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
rtk git add src/Scrinia/Commands/ tests/Scrinia.Tests/ && rtk git commit -m "fix(cli): consolidate reads and writes through one store

CompactEntryToSingleChunk read via ScriniaArtifactStore and wrote via it too,
bypassing FileMemoryStore's per-scope write lock and leaving its _indexCache
stale for the rest of the process. The artifact write was also a bare
File.WriteAllText, bypassing the atomic-write convention."
```

---

### Task 4: Delete `ScriniaArtifactStore`, port 17 tests, drop 3 globals

**Files:**
- Delete: `src/Scrinia/Mcp/ScriniaArtifactStore.cs`
- Delete: `tests/Scrinia.Tests/ScriniaArtifactStoreTests.cs`
- Modify: `src/Scrinia/Commands/WorkspaceSetup.cs:20`
- Modify: `tests/Scrinia.Tests/TestHelpers.cs:96-118`
- Modify: `tests/Scrinia.Tests/FileMemoryStoreTests.cs` (absorb 17 tests)
- Modify: `tests/Scrinia.Tests/{AttributesKeyMigration,AutoReviewWhen,ChunkIndexing,ConsolidateReembed,MentionSpanWiring,ScriniaMcpTools,WriterCutover}Tests.cs`

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces: `TestHelpers.StoreScope` with only `SessionBudget.OverrideStore` and `MemoryStoreContext.Current` set. `WorkspaceDir` and `TempDir` keep their current meaning.

- [ ] **Step 1: Port the 17 gap tests**

Move these from `ScriniaArtifactStoreTests.cs` into `FileMemoryStoreTests.cs`, rewriting `ScriniaArtifactStore.X(...)` as `store.X(...)` against a `FileMemoryStore` built from `scope.WorkspaceDir`. They are **not** duplicates — `FileMemoryStoreTests` has no equivalent today.

| Group | Count |
|---|---|
| `LoadIndex_{CorruptFile,EmptyIndexFile,MissingFile,OnAbsentStoreDirectory}` | 4 |
| `SanitizeName_{InvalidChars,SpaceAndDot,ValidName}` | 3 |
| `ArchiveVersion_{ExistingFile,NoFile}` | 2 |
| `SaveIndex_{AtomicWrite_IndexRoundTrips,NoTempFileLeft_AfterWrite}` | 2 |
| `NameFromUri_{StoreUri,TempUri}` | 2 |
| `ResolveReadScopes_{Default,ExplicitFilter}` | 2 |
| `NormalizeScopeFilters_BareTopicName_ReturnsLocalOnly` | 1 |
| `EveryWriteMaterializesTheGitIgnore` | 1 |
| **Total** | **17** |

`NormalizeScopeFilters` and `ResolveReadScopes(string?)` are `internal` on `FileMemoryStore`; `tests/Scrinia.Tests` already has `InternalsVisibleTo` via `src/Scrinia.Core/Properties/AssemblyInfo.cs`. Verify with `rtk grep -n InternalsVisibleTo src/Scrinia.Core/Properties/AssemblyInfo.cs` before assuming — if `Scrinia.Tests` is absent, add it rather than making the members public.

- [ ] **Step 2: Run the ported tests**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug --filter "FullyQualifiedName~FileMemoryStoreTests"`
Expected: PASS, 17 more tests than before.

- [ ] **Step 3: Delete the class and its tests**

```bash
rtk git rm src/Scrinia/Mcp/ScriniaArtifactStore.cs tests/Scrinia.Tests/ScriniaArtifactStoreTests.cs
```

Then remove `ScriniaArtifactStore.Configure(root);` from `WorkspaceSetup.cs:20` and the now-unused `using Scrinia.Mcp;` if no other member of that namespace is referenced in the file.

- [ ] **Step 4: Simplify `StoreScope`**

In `tests/Scrinia.Tests/TestHelpers.cs`, the constructor and `Dispose` become:

```csharp
        public StoreScope(bool createStoreDir = true)
        {
            WorkspaceDir = Path.Combine(Path.GetTempPath(), $"scrinia_test_{Guid.NewGuid():N}");
            TempDir = Path.Combine(WorkspaceDir, ".scrinia", "store");
            if (createStoreDir)
                Directory.CreateDirectory(TempDir);
            SessionBudget.OverrideStore(
                new System.Collections.Concurrent.ConcurrentDictionary<string, long>(
                    StringComparer.OrdinalIgnoreCase));
            MemoryStoreContext.Current = new FileMemoryStore(WorkspaceDir);
        }

        public void Dispose()
        {
            SessionBudget.OverrideStore(null);
            MemoryStoreContext.Current = null;
            try { Directory.Delete(WorkspaceDir, recursive: true); } catch { }
        }
```

Three globals gone: `OverrideWorkspaceRoot`, `OverrideStoreDir`, `OverrideEphemeralStore`.

- [ ] **Step 5: Fix the remaining test references**

Six other test files reference `ScriniaArtifactStore`. Each read is a store read; replace with `MemoryStoreContext.Current!`:

```
AttributesKeyMigrationTests.cs  AutoReviewWhenTests.cs   ChunkIndexingTests.cs
ConsolidateReembedTests.cs      MentionSpanWiringTests.cs ScriniaMcpToolsTests.cs
WriterCutoverTests.cs
```

`WriterCutoverTests.cs` needs care: its doc comment says it reads through `ScriniaArtifactStore.LoadIndex` *specifically because* that path never calls `AttributeMigration`. That property is gone. Replace the read with a direct sidecar deserialize inside the test so the write-time-vs-read-time isolation the test exists to prove is preserved, and update the doc comment to say so:

```csharp
    private static ArtifactEntry ReadSidecarDirect(string storeDir, string name)
    {
        string path = Path.Combine(storeDir, name + ".meta.json");
        return System.Text.Json.JsonSerializer.Deserialize(
            File.ReadAllText(path), ScriniaMcpJsonContext.Default.ArtifactEntry)!;
    }
```

- [ ] **Step 6: Verify no references remain**

Run: `rtk grep -rn "ScriniaArtifactStore" --include=*.cs src/ tests/`
Expected: no output.

- [ ] **Step 7: Run the full suite**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug`
Expected: PASS. Count = baseline − 24 (deleted duplicates) + 17 (ported) + 1 (Task 1) + 1 (Task 3).

- [ ] **Step 8: Commit**

```bash
rtk git add -A src/ tests/ && rtk git commit -m "refactor: delete ScriniaArtifactStore, the duplicate filesystem store

1,052 lines duplicating FileMemoryStore across ~44 members. 19 of 53 commits
touching either file had to touch both, and they had already diverged:
AttributeMigration referenced 3 times in FileMemoryStore and 0 times here.

17 tests covering real gaps are ported to FileMemoryStoreTests; 24 duplicated
existing coverage and are dropped. Three ambient test globals go with it."
```

---

### Task 5: Make `PathRouter` the only router

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:389-408,410-414,419-447`
- Modify: `src/Scrinia.Core/PathRouter.cs`
- Test: `tests/Scrinia.Tests/PathRouterTests.cs`

**Interfaces:**
- Consumes: Task 4.
- Produces: `PathRouter.ToStoreDir(string scope, string workspaceRoot)` — maps an internal scope string to its directory. Used by `FileMemoryStore.GetStoreDirForScope` and by `StoreLayoutMigration` (Task 6).

Layout is **unchanged** in this task. Only the duplication is removed.

- [ ] **Step 1: Write the failing test**

Add to `tests/Scrinia.Tests/PathRouterTests.cs`:

```csharp
    [Fact]
    public void ToStoreDir_MatchesFileMemoryStoreForEveryScopeShape()
    {
        var store = new FileMemoryStore(_root);
        foreach (string scope in new[] { "local", "local:findings", "local:goal/G-5", "local:agent" })
            PathRouter.ToStoreDir(scope, _root).Should().Be(store.GetStoreDirForScope(scope));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug --filter "FullyQualifiedName~ToStoreDir_Matches"`
Expected: FAIL — `PathRouter` has no `ToStoreDir`.

- [ ] **Step 3: Add `ToStoreDir` to `PathRouter`**

Move the body of `FileMemoryStore.GetStoreDirForScope` verbatim, plus its `IsV2PathScope` helper:

```csharp
    /// <summary>
    /// Maps an internal scope string ("local", "local:findings", "local:goal/G-5") to its
    /// store directory. The single authority for scope-to-directory resolution.
    /// </summary>
    public static string ToStoreDir(string scope, string workspaceRoot)
    {
        if (scope == "local")
            return Path.Combine(workspaceRoot, ".scrinia", "store");

        if (scope.StartsWith("local:", StringComparison.Ordinal))
        {
            string topic = scope["local:".Length..];
            if (IsV2PathScope(topic))
                return Path.Combine(workspaceRoot, ".scrinia", MemoriesDir,
                    topic.Replace('/', Path.DirectorySeparatorChar));
            return Path.Combine(workspaceRoot, ".scrinia", "topics", topic);
        }

        throw new ArgumentException($"Unknown scope: {scope}");
    }
```

Copy `IsV2PathScope` from `FileMemoryStore.cs:362-387` into `PathRouter` as `private static`.

- [ ] **Step 4: Delegate from `FileMemoryStore`**

```csharp
    public string GetStoreDirForScope(string scope) => PathRouter.ToStoreDir(scope, _workspaceRoot);
```

Delete `FileMemoryStore.IsV2PathScope`.

- [ ] **Step 5: Run the full suite**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug`
Expected: PASS at Task 4's count + 1.

- [ ] **Step 6: Commit**

```bash
rtk git add -A src/ tests/ && rtk git commit -m "refactor(core): PathRouter owns scope-to-directory resolution"
```

---

### Task 6: The `StoreLayoutMigration` engine

**Files:**
- Create: `src/Scrinia.Core/StoreLayoutMigration.cs`
- Create: `tests/Scrinia.Tests/StoreLayoutMigrationTests.cs`

**Interfaces:**
- Consumes: `PathRouter.ToStoreDir` (Task 5).
- Produces:
  - `StoreLayoutMigration.Result` — `sealed record Result(int Moved, int Conflicts, bool Ran, string? BackupPath)`
  - `static Result MigrateIfNeeded(string workspaceRoot)` — automatic trigger; backup always on.
  - `static Result Migrate(string workspaceRoot, bool dryRun, bool backup)` — manual trigger.
  - `static bool NeedsMigration(string workspaceRoot)` — the three-stat fast path.
  Tasks 7 and 9 consume all four.

Not wired to anything in this task. Pure engine, fully tested in isolation.

- [ ] **Step 1: Write the failing test**

Create `tests/Scrinia.Tests/StoreLayoutMigrationTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

public sealed class StoreLayoutMigrationTests : IDisposable
{
    private readonly string _root;

    public StoreLayoutMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_mig_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string relative, string content)
    {
        string full = Path.Combine(_root, ".scrinia", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private bool Exists(string relative) =>
        File.Exists(Path.Combine(_root, ".scrinia", relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void Migrate_MovesEveryLegacyRootIntoMemories()
    {
        Write("store/notes.nmp2", "a");
        Write("store/notes.meta.json", "{}");
        Write("topics/findings/review.nmp2", "b");
        Write("topics/memory/api/auth.nmp2", "c");
        Write("topics/entity/skill/qa.nmp2", "d");
        Write("topics/agent/profile.nmp2", "e");
        Write("agent/profile.md", "f");

        var result = StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);

        result.Ran.Should().BeTrue();
        Exists("memories/notes.nmp2").Should().BeTrue();
        Exists("memories/notes.meta.json").Should().BeTrue();
        Exists("memories/findings/review.nmp2").Should().BeTrue();
        Exists("memories/api/auth.nmp2").Should().BeTrue();
        Exists("memories/skill/qa.nmp2").Should().BeTrue();
        Exists("memories/agent/profile.nmp2").Should().BeTrue();
        Exists("memories/agent/profile.md").Should().BeTrue();

        // Moved, not copied.
        Exists("store/notes.nmp2").Should().BeFalse();
        Exists("topics/findings/review.nmp2").Should().BeFalse();
        Directory.Exists(Path.Combine(_root, ".scrinia", "topics")).Should().BeFalse();
    }

    [Fact]
    public void Migrate_PreservesVersionsSubdirectories()
    {
        Write("topics/findings/versions/review_20260801-010101.nmp2", "old");
        StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);
        Exists("memories/findings/versions/review_20260801-010101.nmp2").Should().BeTrue();
    }

    [Fact]
    public void Migrate_Collision_NonNamespacedWins_LoserQuarantined()
    {
        Write("topics/api/auth.nmp2", "flat-wins");
        Write("topics/memory/api/auth.nmp2", "namespaced-loses");

        var result = StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);

        result.Conflicts.Should().Be(1);
        File.ReadAllText(Path.Combine(_root, ".scrinia", "memories", "api", "auth.nmp2"))
            .Should().Be("flat-wins");
        Directory.EnumerateDirectories(_root, ".scrinia-conflicts-*").Should().NotBeEmpty();
    }

    [Fact]
    public void Migrate_IsIdempotent_SecondRunDoesNothing()
    {
        Write("topics/findings/review.nmp2", "b");
        StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);

        var second = StoreLayoutMigration.Migrate(_root, dryRun: false, backup: true);

        second.Ran.Should().BeFalse();
        second.Moved.Should().Be(0);
        second.BackupPath.Should().BeNull();
        Directory.EnumerateDirectories(_root, ".scrinia-backup-*").Should().BeEmpty();
    }

    [Fact]
    public void Migrate_DryRun_MovesNothing()
    {
        Write("topics/findings/review.nmp2", "b");
        var result = StoreLayoutMigration.Migrate(_root, dryRun: true, backup: false);

        result.Moved.Should().Be(1);
        Exists("topics/findings/review.nmp2").Should().BeTrue();
        Exists("memories/findings/review.nmp2").Should().BeFalse();
    }

    [Fact]
    public void Migrate_Resumes_AfterPartialMigration()
    {
        Write("memories/findings/already.nmp2", "done");   // simulates interrupted run
        Write("topics/findings/pending.nmp2", "todo");

        StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);

        Exists("memories/findings/already.nmp2").Should().BeTrue();
        Exists("memories/findings/pending.nmp2").Should().BeTrue();
    }

    [Fact]
    public void NeedsMigration_FalseWhenOnlyMemoriesExists()
    {
        Write("memories/findings/review.nmp2", "b");
        StoreLayoutMigration.NeedsMigration(_root).Should().BeFalse();
    }

    [Fact]
    public void BothTriggers_ProduceIdenticalLayout()
    {
        Write("topics/findings/review.nmp2", "b");
        Write("store/notes.nmp2", "a");
        StoreLayoutMigration.MigrateIfNeeded(_root);

        Exists("memories/findings/review.nmp2").Should().BeTrue();
        Exists("memories/notes.nmp2").Should().BeTrue();
        StoreLayoutMigration.NeedsMigration(_root).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug --filter "FullyQualifiedName~StoreLayoutMigrationTests"`
Expected: FAIL — `StoreLayoutMigration` does not exist.

- [ ] **Step 3: Implement the engine**

Create `src/Scrinia.Core/StoreLayoutMigration.cs`. Port `BuildMigrationPlan` and `MapTopicFileToV2` from `src/Scrinia/Commands/ScriniaCommands.Migration.cs:126-225` — they already target `.scrinia/memories/` and already strip the `entity/` and `memory/` namespaces. Four changes are required: add the `store/` root, stop skipping `versions/`, move instead of copy, and detect collisions.

```csharp
namespace Scrinia.Core;

/// <summary>
/// Migrates a workspace store from any older on-disk layout to v3, where every memory
/// lives under <c>.scrinia/memories/</c>.
///
/// <para>One implementation, two triggers: <see cref="MigrateIfNeeded"/> runs automatically
/// on first store access, <see cref="Migrate"/> backs the `scri migrate` command. Modelled on
/// <c>EmbeddingReindexer</c>, which pairs an automatic and a manual entry point the same way.</para>
///
/// <para>This is not a fallback. It moves rather than copies, so exactly one copy of each
/// memory exists at all times, and it terminates permanently once the legacy roots are gone.
/// Nothing reads through it.</para>
/// </summary>
public static class StoreLayoutMigration
{
    private static readonly string[] LegacyRoots = ["store", "topics", "agent", "skills"];

    public sealed record Result(int Moved, int Conflicts, bool Ran, string? BackupPath);

    /// <summary>True when any legacy root still holds files. Three-stat fast path.</summary>
    public static bool NeedsMigration(string workspaceRoot)
    {
        string scriniaDir = Path.Combine(workspaceRoot, ".scrinia");
        foreach (string root in LegacyRoots)
        {
            string dir = Path.Combine(scriniaDir, root);
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any())
                return true;
        }
        return false;
    }

    /// <summary>Automatic trigger. No-op when nothing needs moving. Backup always on.</summary>
    public static Result MigrateIfNeeded(string workspaceRoot) =>
        Migrate(workspaceRoot, dryRun: false, backup: true);

    /// <summary>Manual trigger, backing `scri migrate`.</summary>
    public static Result Migrate(string workspaceRoot, bool dryRun, bool backup)
    {
        if (!NeedsMigration(workspaceRoot))
            return new Result(0, 0, Ran: false, BackupPath: null);

        string scriniaDir = Path.Combine(workspaceRoot, ".scrinia");
        var plan = BuildPlan(scriniaDir);
        if (plan.Count == 0)
            return new Result(0, 0, Ran: false, BackupPath: null);

        if (dryRun)
            return new Result(plan.Count, CountCollisions(plan), Ran: false, BackupPath: null);

        using var fileLock = FileLock.AcquireExclusive(Path.Combine(scriniaDir, ".migration.lock"));

        string? backupPath = null;
        if (backup)
        {
            backupPath = Path.Combine(workspaceRoot,
                $".scrinia-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
            CopyDirectory(scriniaDir, backupPath);
        }

        string conflictsDir = Path.Combine(workspaceRoot,
            $".scrinia-conflicts-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");

        int moved = 0, conflicts = 0;
        foreach (var (source, target, isNamespaced) in plan)
        {
            if (!File.Exists(source)) continue;      // resumption: already moved
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            if (File.Exists(target))
            {
                // Non-namespaced wins, matching the precedence the old read fallback used.
                string quarantine = Path.Combine(conflictsDir, Path.GetRelativePath(scriniaDir, source));
                Directory.CreateDirectory(Path.GetDirectoryName(quarantine)!);
                File.Move(source, quarantine, overwrite: true);
                conflicts++;
                continue;
            }

            File.Move(source, target);
            moved++;
        }

        MoveRenamedVectorDirs(scriniaDir);
        PruneEmptyLegacyRoots(scriniaDir);
        return new Result(moved, conflicts, Ran: true, backupPath);
    }
}
```

Implement the private helpers in the same file:

- `BuildPlan(string scriniaDir)` → `List<(string Source, string Target, bool IsNamespaced)>`. Scan all four `LegacyRoots`. `store/*` maps to `memories/<file>`. `topics/*` maps via the ported `MapTopicFileToV2`, with `IsNamespaced` true when the relative path starts with `entity/` or `memory/`. `agent/*` → `memories/agent/`, `skills/*` → `memories/skill/`. **Do not skip `versions/`** — include those files so archives move with their artifact. Order the plan non-namespaced-first so the winner lands before its loser is evaluated.
- `CountCollisions(plan)` → count targets appearing more than once.
- `CopyDirectory(string source, string dest)` → recursive file copy.
- `MoveRenamedVectorDirs(string scriniaDir)` → `embeddings/local/entity/skill` → `embeddings/local/skill`; `embeddings/local/memory/<topic>` → `embeddings/local/<topic>`. These are the only two scope families whose string changes, per `VectorStore.ScopeToRelativeDir`. On failure, leave the directory and continue — `scri reindex` rebuilds.
- `PruneEmptyLegacyRoots(string scriniaDir)` → delete each legacy root that is now empty, so `NeedsMigration` returns false permanently.

- [ ] **Step 4: Run the tests**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug --filter "FullyQualifiedName~StoreLayoutMigrationTests"`
Expected: PASS, 8 tests.

- [ ] **Step 5: Run the full suite**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug`
Expected: PASS. Nothing is wired yet, so no existing test changes behaviour.

- [ ] **Step 6: Commit**

```bash
rtk git add src/Scrinia.Core/StoreLayoutMigration.cs tests/Scrinia.Tests/StoreLayoutMigrationTests.cs && rtk git commit -m "feat(core): add StoreLayoutMigration engine for the v3 layout"
```

---

### Task 7: Switch to v3 routing and wire the automatic trigger

**Files:**
- Modify: `src/Scrinia.Core/PathRouter.cs` (`ToStoreDir`)
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` (add `MemoriesRoot` gate; `DiscoverTopics`)
- Test: `tests/Scrinia.Tests/StoreLayoutMigrationTests.cs`

**Interfaces:**
- Consumes: `StoreLayoutMigration.{NeedsMigration,MigrateIfNeeded}` (Task 6), `PathRouter.ToStoreDir` (Task 5).
- Produces: `private string FileMemoryStore.MemoriesRoot` — the gated choke point every path computation flows through.

The legacy read-fallback is still present after this task; Task 8 removes it. Keeping the two steps separate means each ends green.

- [ ] **Step 1: Write the failing test**

Add to `tests/Scrinia.Tests/StoreLayoutMigrationTests.cs`:

```csharp
    [Fact]
    public void FileMemoryStore_MigratesAutomatically_OnFirstAccess()
    {
        Write("topics/findings/review.nmp2", "body");
        Write("topics/findings/review.meta.json",
            """{"Name":"review","OriginalBytes":4,"ChunkCount":1,"CreatedAt":"2026-08-01T00:00:00+00:00","ContentPreview":"body"}""");

        var store = new FileMemoryStore(_root);
        var entries = store.LoadIndex("local:findings");

        entries.Should().ContainSingle(e => e.Name == "review");
        Exists("memories/findings/review.nmp2").Should().BeTrue();
        Exists("topics/findings/review.nmp2").Should().BeFalse();
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug --filter "FullyQualifiedName~MigratesAutomatically"`
Expected: FAIL — files remain under `topics/`; nothing triggers migration.

- [ ] **Step 3: Switch `ToStoreDir` to the v3 mapping**

Replace the body added in Task 5:

```csharp
    public static string ToStoreDir(string scope, string workspaceRoot)
    {
        string memories = Path.Combine(workspaceRoot, ".scrinia", MemoriesDir);
        if (scope == "local")
            return memories;

        if (scope.StartsWith("local:", StringComparison.Ordinal))
            return Path.Combine(memories,
                scope["local:".Length..].Replace('/', Path.DirectorySeparatorChar));

        throw new ArgumentException($"Unknown scope: {scope}");
    }
```

Delete `PathRouter.IsV2PathScope` — no caller remains.

- [ ] **Step 4: Add the gated choke point**

In `src/Scrinia.Core/FileMemoryStore.cs`, add beside the other private members:

```csharp
    private int _layoutChecked;

    /// <summary>
    /// The root every persistent path resolves under. Runs the one-time v3 layout migration
    /// on first use, which is why every path computation must flow through here — a method
    /// that builds a path another way would read a store that has not been migrated yet.
    /// </summary>
    private string MemoriesRoot
    {
        get
        {
            if (Interlocked.Exchange(ref _layoutChecked, 1) == 0 &&
                StoreLayoutMigration.NeedsMigration(_workspaceRoot))
            {
                var result = StoreLayoutMigration.MigrateIfNeeded(_workspaceRoot);
                if (result.Ran)
                    Console.Error.WriteLine(
                        $"scrinia: migrated {result.Moved} file(s) to the v3 layout" +
                        (result.Conflicts > 0 ? $", {result.Conflicts} conflict(s) quarantined" : "") +
                        (result.BackupPath is not null ? $"; backup at {result.BackupPath}" : ""));
                _indexCache.Clear();
                _cachedTopics = null;
            }
            return Path.Combine(_workspaceRoot, ".scrinia", "memories");
        }
    }

    public string GetStoreDirForScope(string scope)
    {
        _ = MemoriesRoot;   // gate
        return PathRouter.ToStoreDir(scope, _workspaceRoot);
    }
```

- [ ] **Step 5: Route `DiscoverTopics` through the gate**

`DiscoverTopics` is the only method that builds a path without `GetStoreDirForScope`. At `FileMemoryStore.cs:1890` replace the literal with the gated property:

```csharp
        string memoriesRoot = MemoriesRoot;
        if (Directory.Exists(memoriesRoot))
            DiscoverMemoriesRecursive(memoriesRoot, "", results);
```

Leave the `topics/` scan at line 1850 for now — Task 8 deletes it.

- [ ] **Step 6: Run the new test**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug --filter "FullyQualifiedName~MigratesAutomatically"`
Expected: PASS.

- [ ] **Step 7: Run the full suite**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug`
Expected: PASS. Tests that seed `topics/` directly now see it migrated on first access; the still-present fallback keeps any stragglers readable.

- [ ] **Step 8: Commit**

```bash
rtk git add -A src/ tests/ && rtk git commit -m "feat(core): route every memory to .scrinia/memories and migrate on first access"
```

---

### Task 8: Delete the fallback machinery and convert 13 tests

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:419-570,761-836,1840-1900`
- Modify: `src/Scrinia.Core/PathRouter.cs` (delete `ToLegacyPath`)
- Modify: `src/Scrinia.Core/MemoryNaming.cs`
- Modify: `src/Scrinia.Core/IMemoryStore.cs:18-34`
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs:275-283`
- Modify: `tests/Scrinia.Tests/FileMemoryStoreTests.cs`

**Interfaces:**
- Consumes: Task 7.
- Produces: `MemoryNaming.BuildScopedTopicScope(string topic)` now returns `$"local:{topic}"` for **every** topic including `skill` and `agent`. `FormatRoundTripQualifiedName` no longer exists — callers use `FormatQualifiedName`.

- [ ] **Step 1: Convert the 13 fallback tests**

In `tests/Scrinia.Tests/FileMemoryStoreTests.cs`, each of these asserts that a *read* merges legacy locations. Rewrite each to assert that *migration moved the file* and the entry is then found normally.

| Test | Count |
|---|---|
| `LoadIndex_MergesEntriesFromLegacyFlatDir` (+ `_WhenNamespacedDirDoesNotExist`) | 2 |
| `LoadIndex_NamespacedEntries_TakePrecedenceOverLegacy` | 1 |
| `LoadIndex_MergesLegacyAndNamespacedEntries_WithPrecedence` | 1 |
| `ReadArtifact_FallsBackToLegacyPath` | 1 |
| `DiscoverTopics_LegacyFlatDir_{NotShadowed,ShadowedByNamespaced}` | 2 |
| `Upsert_NamespacedScope_PreservesLegacyEntriesInCache` | 1 |
| `Remove_NamespacedScope_PreservesLegacyEntriesInCache` | 1 |
| `BuildScopedTopicScope_{Entity,User}Topic_*` | 2 |
| `FormatQualifiedName_{Entity,Memory}Namespaced_StripsPrefix` | 2 |
| **Total** | **13** |

Pattern — before:

```csharp
    [Fact]
    public void LoadIndex_MergesEntriesFromLegacyFlatDir()
    {
        // seeds .scrinia/topics/api/, asserts LoadIndex("local:entity/api") sees it
    }
```

after:

```csharp
    [Fact]
    public void LoadIndex_MigratesEntriesFromLegacyFlatDir()
    {
        using var scope = new TestHelpers.StoreScope();
        SeedLegacySidecar(scope.WorkspaceDir, "topics/api", "auth-flow");

        var store = new FileMemoryStore(scope.WorkspaceDir);
        store.LoadIndex("local:api").Should().ContainSingle(e => e.Name == "auth-flow");

        File.Exists(Path.Combine(scope.WorkspaceDir, ".scrinia", "memories", "api", "auth-flow.nmp2"))
            .Should().BeTrue();
        Directory.Exists(Path.Combine(scope.WorkspaceDir, ".scrinia", "topics"))
            .Should().BeFalse();
    }
```

Do **not** touch `BuildScopedTopicScope_AgentTopic_RoutesToAgentScope`, `FormatQualifiedName_AgentScope_StripsPrefix`, or the three `AttributeMigration` forward-fill tests (`LoadIndex_ForwardFillsLegacySidecar_InMemory`, `LoadIndex_PersistsForwardFill_ToPrimarySidecar`, `LoadIndex_ConcurrentColdLoads_ForwardFillWriteBackDoesNotRace`). Attribute forward-fill is orthogonal to directory layout and survives unchanged.

- [ ] **Step 2: Run the converted tests to verify they fail**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug --filter "FullyQualifiedName~FileMemoryStoreTests"`
Expected: the converted tests FAIL on the `Directory.Exists(...topics).Should().BeFalse()` assertion, because the fallback still populates from `topics/`.

- [ ] **Step 3: Delete the fallback machinery**

| Location | Delete |
|---|---|
| `FileMemoryStore.cs:449-570` | `ResolveLegacyFallbackDir`, `ResolveV2LegacyDir`, `ResolveV2LegacyFilePath` |
| `FileMemoryStore.cs:425-438` | the two fallback branches in `FindArtifactPath` — return `primary` |
| `FileMemoryStore.cs:809-830` | the legacy-dir merge block at the end of `LoadIndexUnsafe` |
| `FileMemoryStore.cs:771-776` | the `LoadEntriesFromLegacyIndex` fallback, and the method itself at `:864` |
| `FileMemoryStore.cs:1848-1888` | the `topics/` scan in `DiscoverTopics` |
| `FileMemoryStore.cs:1213-1253` | `LoadIndexFromCacheOrDisk` — documented as having no callers |
| `PathRouter.cs` | `ToLegacyPath` |
| `MemoryNaming.cs` | `StripNamespacePrefix`, `ReservedNamespaceDirs`, `FormatRoundTrip`, `ClassifyTopic`, `EntityTopics`, `AgentTopics` |
| `IMemoryStore.cs:18-34` | `FormatRoundTripQualifiedName` and its doc comment |

`BuildScopedTopicScope` flattens to:

```csharp
    /// <summary>Builds the internal scope string for a topic. Every topic is flat under v3.</summary>
    public static string BuildScopedTopicScope(string topic) => $"local:{topic}";
```

`FormatScopeLabel` drops its `StripNamespacePrefix` call and returns `scope["local:".Length..]` directly.

- [ ] **Step 4: Fix the `FormatRoundTripQualifiedName` callers**

Two call sites use it (`EmbeddingReindexer.cs:226`, `ScriniaCommands.Migration.cs:531`). Both become `FormatQualifiedName` — under v3 the two are identical, which is why the method is being removed. Delete the explanatory comment at `Migration.cs:527-530`; it describes a hazard that no longer exists.

- [ ] **Step 5: Route the agent path through the store**

`MemoryTools.Core.cs:275-277` writes to a `.scrinia/agent` literal. Replace with the store's own resolution so `/agent/x` lands in `memories/agent/x.md`:

```csharp
            string agentDir = store.GetStoreDirForScope("local:agent");
            string filePath = Path.Combine(agentDir, $"{agentSubject}.md");
```

Apply the same change to the `/agent/` read branch at `MemoryTools.Core.cs:30-40`.

- [ ] **Step 6: Run the full suite**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug`
Expected: PASS. If any *unconverted* legacy test still passes, the fallback was not fully removed — find and delete the surviving path.

- [ ] **Step 7: Verify the machinery is gone**

Run: `rtk grep -rn "ResolveLegacyFallbackDir\|ResolveV2Legacy\|ToLegacyPath\|StripNamespacePrefix\|FormatRoundTrip\|LoadEntriesFromLegacyIndex\|ReservedNamespaceDirs" --include=*.cs src/`
Expected: no output.

- [ ] **Step 8: Commit**

```bash
rtk git add -A src/ tests/ && rtk git commit -m "refactor(core): delete the legacy layout fallback

237 lines of fallback machinery plus the v1 namespace concept. The read path
now knows exactly one layout. 13 tests that asserted reads merge legacy
locations now assert migration moves them."
```

---

### Task 9: The `scri migrate` command and documentation

**Files:**
- Modify: `src/Scrinia/Commands/ScriniaCommands.Migration.cs:29-225,227-290`
- Modify: `docs/cli-reference.md:452-478`
- Modify: `CHANGELOG.md`
- Test: `tests/Scrinia.Tests/MigrationTests.cs`

**Interfaces:**
- Consumes: `StoreLayoutMigration.Migrate` (Task 6), `WorkspaceSetup.Root` (Task 1).
- Produces: `scri migrate [--workspace <path>] [--dry-run] [--backup=true|false]`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Scrinia.Tests/MigrationTests.cs`:

```csharp
    [Fact]
    public async Task Migrate_DryRun_ReportsPlanAndMovesNothing()
    {
        string root = Path.Combine(Path.GetTempPath(), $"scrinia_cli_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        string legacy = Path.Combine(root, ".scrinia", "topics", "findings");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "review.nmp2"), "body");
        try
        {
            int exit = await ScriniaCommands.Migrate(root, dryRun: true, backup: false);

            exit.Should().Be(0);
            File.Exists(Path.Combine(legacy, "review.nmp2")).Should().BeTrue();
            Directory.Exists(Path.Combine(root, ".scrinia", "memories")).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug --filter "FullyQualifiedName~Migrate_DryRun"`
Expected: FAIL — no `ScriniaCommands.Migrate`.

- [ ] **Step 3: Replace `MigrateV1` with `Migrate`**

In `src/Scrinia/Commands/ScriniaCommands.Migration.cs`, delete `MigrateV1` (`:29-124`), `BuildMigrationPlan` (`:126`), `MapTopicFileToV2`, and `RunCleanup` (`:227`) — the planning logic now lives in `StoreLayoutMigration` (Task 6). Keep `BackupScriniaDir` only if another caller remains; otherwise delete it too. Add:

```csharp
    /// <summary>
    /// Migrates a workspace store to the v3 layout, where every memory lives under
    /// .scrinia/memories/. Runs automatically on first store access; this command exists
    /// to preview the plan, migrate deliberately, or re-run after restoring a backup.
    /// </summary>
    public static Task<int> Migrate(
        string? workspace = null,
        bool dryRun = false,
        bool backup = true,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspace);
        string root = WorkspaceSetup.Root;

        if (!Directory.Exists(Path.Combine(root, ".scrinia")))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No .scrinia/ directory found.");
            return Task.FromResult(1);
        }

        var result = StoreLayoutMigration.Migrate(root, dryRun, backup);

        if (!result.Ran && result.Moved == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to migrate — the store is already v3.[/]");
            return Task.FromResult(0);
        }

        if (dryRun)
        {
            AnsiConsole.MarkupLine($"[bold][DRY RUN][/] Would move {result.Moved} file(s).");
            if (result.Conflicts > 0)
                AnsiConsole.MarkupLine($"[yellow]{result.Conflicts} collision(s) would be quarantined.[/]");
            return Task.FromResult(0);
        }

        AnsiConsole.MarkupLine($"[green]Migrated {result.Moved} file(s) to the v3 layout.[/]");
        if (result.Conflicts > 0)
            AnsiConsole.MarkupLine(
                $"[yellow]{result.Conflicts} collision(s) quarantined — non-namespaced entries won.[/]");
        if (result.BackupPath is not null)
            AnsiConsole.MarkupLine($"[dim]Backup: {Markup.Escape(result.BackupPath)}[/]");
        return Task.FromResult(0);
    }
```

Remove the `[Hidden]` attribute — this is a supported command now. Drop the `cleanup` parameter entirely.

- [ ] **Step 4: Run the new test**

Run: `rtk dotnet test tests/Scrinia.Tests/ -c Debug --filter "FullyQualifiedName~Migrate_DryRun"`
Expected: PASS.

- [ ] **Step 5: Update the CLI reference**

Replace `docs/cli-reference.md:452-478` (the `### scri migrate_v1` section) with:

````markdown
### scri migrate

Migrates a workspace store to the v3 layout, where every memory lives under
`.scrinia/memories/`. Migration also runs **automatically** on first store access, so this
command is only needed to preview the plan, migrate deliberately before a large operation,
or re-run after restoring a backup.

```bash
scri migrate [--workspace <path>] [--dry-run] [--backup=true|false]
```

| Flag | Default | Description |
|---|---|---|
| `--workspace` | cwd | Workspace root containing the `.scrinia/` directory. |
| `--dry-run` | false | Print how many files would move without moving any. |
| `--backup` | true | Copy `.scrinia/` to `.scrinia-backup-{timestamp}/` before migrating. |

Files are **moved**, not copied — exactly one copy of each memory exists at all times. Where
two legacy locations map to the same target (for example `topics/api/x` and
`topics/memory/api/x`), the non-namespaced entry wins and the other is moved to
`.scrinia-conflicts-{timestamp}/` rather than being discarded.

To roll back, stop any running Scrinia process and replace `.scrinia/` with the backup
directory.
````

Also update the `## Surface overview` list at line 5 if it enumerates `migrate_v1`.

- [ ] **Step 6: Add the CHANGELOG entry**

Under `## [Unreleased]` in `CHANGELOG.md`:

```markdown
### Changed — store unification and the v3 memory layout

- `ScriniaArtifactStore` is deleted. `FileMemoryStore` is the only filesystem store, and
  `PathRouter` is the only authority on where a memory lives.
- Every memory now lives under `.scrinia/memories/`. The `store/`, `topics/`, and `agent/`
  roots are gone, along with the `entity/` and `memory/` namespace prefixes.
- Existing workspaces migrate automatically on first store access, with a backup written to
  `.scrinia-backup-{timestamp}/`. `scri migrate` runs the same migration manually and gains
  `--dry-run`; it replaces the hidden `scri migrate_v1`, whose `--cleanup` step is no longer
  needed because files are moved rather than copied.
- `scri consolidate` no longer reads through one store and writes through another, which
  could leave the in-memory index stale for the rest of the process.
```

- [ ] **Step 7: Full verification**

```bash
rtk dotnet build Scrinia.sln -c Debug
rtk dotnet test tests/Scrinia.Tests/ -c Debug
rtk dotnet test tests/Scrinia.Server.Tests/ -c Debug
rtk dotnet test tests/Scrinia.Merge.Tests/ -c Debug
rtk dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/ -c Debug
```
Expected: build 0 errors, all suites PASS.

- [ ] **Step 8: End-to-end against a real v2 store**

The synthetic fixtures do not prove this works on real data. Copy this repository's own store and migrate it:

```bash
rtk cp -r .scrinia /tmp/scrinia-e2e-check
rtk dotnet run --project src/Scrinia -- migrate --workspace /tmp/scrinia-e2e --dry-run
```

Expected: reports the 3 memories under `topics/findings/` plus their `versions/` archives. Then run without `--dry-run` and confirm `scri list` shows all three, `scri show /findings/whole-codebase-review-2026-07-29` returns content, and `embeddings/local_findings/` was handled. Record the output in the commit message.

- [ ] **Step 9: Commit**

```bash
rtk git add -A && rtk git commit -m "feat(cli): scri migrate replaces the hidden migrate_v1"
```

---

## Self-Review

**Spec coverage.** Every design section maps to a task: §1 end state → Tasks 4, 5, 7; §2 replacement mapping → Tasks 1, 2; §3 change sequence → Tasks 1–9; §4 v3 layout → Task 7; §5 migration engine and both triggers → Tasks 6, 7, 9; §6 failure handling → Task 6 (lock, resumption, collisions); Testing → Tasks 4 (17 ported), 6 (8 new), 8 (13 converted), 9 (E2E gate).

**Two design refinements found while gathering signatures**, both consistent with approved intent:

1. The design said the automatic trigger fires "from `LoadIndex` / `EnsureMaterialized`." `EnsureMaterialized` is write-only (called from `WithExclusiveLock`), and `ReadArtifactAsync`, `DiscoverTopics`, and `ListTopicArtifacts` reach disk without `LoadIndex` — so that pair would miss read paths. Task 7 instead gates `MemoriesRoot`, the one property every path computation must flow through, plus `DiscoverTopics`, which is the sole method that builds a path independently (and only until Task 8 deletes its `topics/` scan).

2. `BuildMigrationPlan` already targets `.scrinia/memories/` and `MapTopicFileToV2` already strips the `entity/` and `memory/` prefixes. Task 6 ports rather than writes them, needing only four additions: the `store/` root, `versions/` inclusion (currently explicitly skipped), move-instead-of-copy, and collision detection.

**Placeholder scan:** no TBD/TODO; every code step carries real code; no "similar to Task N".

**Type consistency:** `Result(int Moved, int Conflicts, bool Ran, string? BackupPath)` is used identically in Tasks 6, 7, 9. `WorkspaceSetup.Root` (Task 1) and `.Store` (Task 2) are used with those exact names throughout. `PathRouter.ToStoreDir(scope, workspaceRoot)` keeps its argument order in Tasks 5 and 7. `CompactEntryToSingleChunk` takes non-nullable `IMemoryStore` from Task 3 onward.

**One known ordering constraint:** Task 8 must follow Task 7. Deleting the fallback before the automatic migration is wired would make an unmigrated store read as empty.
