# Store unification and the v3 memory layout — design

**Date:** 2026-08-03
**Status:** Approved (design); implementation plan pending
**Scope:** Workstream A of 3, from the 2026-08-03 design review. Fulfils the
"Spec 2 (per-memory file layout)" referenced by
[`2026-08-03-workspace-root-resolution-design.md`](2026-08-03-workspace-root-resolution-design.md).
Workstream B (search contract) and C (write-path unification) are separate designs.

## Context

Scrinia has three entry points — CLI, MCP, REST — and each grew its own copy of the store
rather than becoming a thin adapter over one. This design addresses the storage half of
that: two full store implementations over a single on-disk format, and four independent
answers to "where does a memory live".

### Two stores, one format

`src/Scrinia.Core/FileMemoryStore.cs` (2,020 lines) and `src/Scrinia/Mcp/ScriniaArtifactStore.cs`
(1,052 lines) are independent implementations reading and writing the same `.scrinia/` tree.
Roughly 44 members exist in both. Several are word-for-word identical, including comments —
`GetStoreDirForScope` (`ScriniaArtifactStore.cs:180` / `FileMemoryStore.cs:389`) shares its
three-line explanatory comment verbatim.

This is not latent duplication. Measured over the repository history:

| Metric | Value |
|---|---|
| Commits touching either store file | 53 |
| Commits that had to touch **both** | 19 (36%) |
| `AttributeMigration` references — `FileMemoryStore` / `ScriniaArtifactStore` | 3 / **0** |
| `FileLock` + `ReaderWriterLock` references — `FileMemoryStore` / `ScriniaArtifactStore` | 5 / 2 |

The stores have already diverged on read-time attribute migration and on locking. The
divergence is load-bearing: `tests/Scrinia.Tests/WriterCutoverTests.cs` reads through
`ScriniaArtifactStore.LoadIndex` *specifically because* it is "a plain `*.meta.json` sidecar
deserialize with NO call to AttributeMigration anywhere in its path." A test now depends on
the duplicate being wrong.

The last 120 commits are 51 `fix` to 10 `feat`, concentrated in these files.

### The mixed-store hazard is concentrated in one command

The CLI already delegates its mutations to `ScriniaMcpTools` (6 construction sites in
`ScriniaCommands.Memory.cs`), which runs on `MemoryStoreContext.Current` — a `FileMemoryStore`.
Residual `ScriniaArtifactStore` use in the CLI is mostly formatting and path helpers.

The exception is `scri consolidate` (`ScriniaCommands.Migration.cs`), which uses both stores in
one operation: reads via `ScriniaArtifactStore.ListScoped` (`:383`), holds a live
`FileMemoryStore` in `store` (`:410`), then writes via `ScriniaArtifactStore.ArchiveVersion`
(`:499`) and `.Upsert` (`:513`). Those writes bypass `FileMemoryStore`'s per-scope write lock and
leave its `_indexCache` stale — the cache has no mtime or generation check, and
`LoadIndexUnsafe:764` returns it unconditionally. The artifact write at `:504` is a bare
`File.WriteAllText`, bypassing the `AtomicWriteAllText` convention used everywhere else.

### Four answers to one question

`.scrinia` appears as a path literal in 42 places across `src/`. The layout is three-way:

```
local                    ->  .scrinia/store/
local:<single-topic>     ->  .scrinia/topics/<topic>/
local:<multi/segment>    ->  .scrinia/memories/<multi>/<segment>/
```

Plus a fourth: `MemoryTools.Core.cs:276-277` writes agent configs directly to `.scrinia/agent/`.

`src/Scrinia.Core/PathRouter.cs` is a clean, pure, fully-documented router that was designed to
be the single answer. In production **only `ToLegacyPath` is ever called** (2 sites);
`ToFilesystemPath`, `ToVersionsDir`, and `ToMetadataPath` appear exclusively in tests. Worse, its
`/agent/` mapping (`.scrinia/memories/agent/x.md`) contradicts the live writer
(`.scrinia/agent/x.md`), and lists the live location under *legacy* fallback. The router's
"current" target is one nothing writes; its "legacy" target is the one in use.

Supporting this dual model costs 237 lines of fallback machinery (133 in `FileMemoryStore`,
104 in `ScriniaArtifactStore`) plus 33 references to the v1 namespace concept.

### Why fallback is the disease, not the cure

Every piece of that machinery is a previous migration's temporary fallback that was never
removed. `scri migrate_v1` is the proof. It uses `File.Copy`, not move, and prints:

```
Original files preserved in topics/ for fallback.
Run 'scri migrate_v1 --cleanup' to remove originals after verifying.
```

Migrate → keep both copies → fallback reads both → nobody runs `--cleanup` → the fallback is
permanent and the two locations drift. Any design that ships another "keep it for one release"
fallback builds the next instance of this problem.

## Rejected alternatives

**Layout-first, then delete the duplicate.** Requires implementing the new routing twice, or
keeping the two stores agreeing mid-flight, and runs the data migration while the
known-diverged duplicate is still live. Strictly worse than store-first.

**Strangler via a new `IMemoryLayout` seam.** Extract the layout abstraction first, have both
stores delegate to it, verify routing from two call paths, then delete `ScriniaArtifactStore`.
The safety it buys — catching routing divergence early — is bought more cheaply by deleting the
duplicate first. Rejected: it introduces an abstraction that is then partly dissolved.

**Keep a legacy read-fallback for one release.** Rejected on the evidence above. This is the
mechanism that produced the current state.

**No fallback plus a startup gate that refuses to run on an unmigrated store.** Rejected: it
invents a user-facing failure, an error message, and a manual step that automatic migration
makes unnecessary.

**Lazy per-scope migrate-on-read.** Rejected: it never converges. A scope nobody reads stays in
`topics/` indefinitely, so "is the old layout gone?" has no answer and the migration code becomes
permanent by default — the same trap in a new shape.

**Retaining `--cleanup`.** That flag exists only because the migration copies. Moving removes the
need, and removes the step everyone skips.

**A `--restore` flag.** The backup is a plain directory copy; a flag would duplicate `cp -r` and
need its own tests. Documented instead.

## Design

### 1. End state

```
Before                                          After

CLI   -> ScriniaArtifactStore (static) --.      CLI  --.
MCP   -> MemoryStoreContext.Current      |      MCP   -+-> IMemoryStore -> FileMemoryStore
REST  -> MemoryStoreContext.Current    --'      REST --'                   HttpMemoryStore
              .scrinia/{store,topics,memories,agent}/                            |
         PathRouter: tests only                                             PathRouter
                                                                                 |
                                                                        .scrinia/memories/
```

- `ScriniaArtifactStore` and `ScriniaArtifactStoreTests.cs` are deleted.
- `PathRouter` becomes load-bearing; `GetStoreDirForScope`, `ArtifactPath`, and
  `FindArtifactPath` call into it.
- Three of the five `TestHelpers.StoreScope` ambient globals disappear
  (`OverrideWorkspaceRoot`, `OverrideStoreDir`, `OverrideEphemeralStore`), because they exist
  only to isolate the static store's process state.

### 2. Replacement mapping

All 11 `ScriniaArtifactStore` members used by the CLI have a direct `IMemoryStore` equivalent
already on the interface. **No interface member is added** — workstream B will argue
`IMemoryStore` should get narrower, and widening it here would work against that.

| Member | Call sites | Replacement |
|---|---|---|
| `WorkspaceRootPath` | 19 | `WorkspaceSetup.Root` — new one-line property caching the `root` that `Configure` already computes via `WorkspaceRoot.Resolve` |
| `FormatQualifiedName` | 7 | `store.FormatQualifiedName` |
| `SanitizeName` | 3 | `store.SanitizeName` |
| `ListScoped` | 2 | `store.ListScoped` |
| `ParseQualifiedName`, `Upsert`, `ArchiveVersion`, `FindArtifactPath`, `GetStoreDirForScope`, `GenerateContentPreview` | 1 each | same-named `store.*` |
| `Configure` | 1 | **deleted** — `WorkspaceSetup.Configure` already builds the `FileMemoryStore` from the same `root` |

`ScriniaArtifactStore`'s ephemeral store has **zero** CLI callers; it is live only in tests.

`WorkspaceSetup.Configure` is called before every `WorkspaceRootPath` read
(`ScriniaCommands.Setup.cs:41`→`:82`, `:533`→`:534`, `:650`), so the root is always configured
before use.

### 3. Change sequence

Store-first. Each commit leaves the suite green.

1. **Add `WorkspaceSetup.Root`; rewrite the 19 `WorkspaceRootPath` reads.** Pure rename.
2. **Repoint the 9 formatting/path helpers onto `MemoryStoreContext.Current`.**
   Behaviour-preserving — both classes implement these identically.
3. **Fix `scri consolidate` to use one store.** Route `ListScoped`, `FindArtifactPath`,
   `ArchiveVersion`, and `Upsert` through `store`. Replace the bare `File.WriteAllText` at
   `:504` with `AtomicWriteAllText`. The only intentional behaviour change in this workstream.
4. **Delete `ScriniaArtifactStore` and its tests; drop 3 of 5 `StoreScope` globals.**
5. **Make `PathRouter` the only router**, collapsing the duplicated `ResolveV2Legacy*` probes.
   Layout still unchanged at this point.
6. **Switch to the v3 layout and add `StoreLayoutMigration`** (sections 4–5).

Commits 1, 2, 4 are behaviour-preserving and guarded by the existing suite with no test edits.

### 4. The v3 layout

Routing collapses to one function:

```csharp
public string GetStoreDirForScope(string scope) => scope switch
{
    "local" => Path.Combine(_workspaceRoot, ".scrinia", "memories"),
    _ when scope.StartsWith("local:", StringComparison.Ordinal) =>
        Path.Combine(_workspaceRoot, ".scrinia", "memories",
            scope["local:".Length..].Replace('/', Path.DirectorySeparatorChar)),
    _ => throw new ArgumentException($"Unknown scope: {scope}")
};
```

`PathRouter.ToFilesystemPath` is the shared implementation behind it.

**Scope strings are almost entirely unchanged.** `MemoryNaming.BuildScopedTopicScope` already
routes ordinary topics flat (`local:{topic}`); only two namespaced families remain:

| Scope family | v2 | v3 |
|---|---|---|
| bare / ordinary topic / multi-segment | `local`, `local:findings`, `local:goal/G-5` | unchanged |
| skill | `local:entity/skill` | `local:skill` |
| legacy memory namespace | `local:memory/api` | `local:api` |

With those two gone, `StripNamespacePrefix` has nothing to strip and `FormatQualifiedName`
becomes identical to `FormatRoundTrip`.

**Deleted in this workstream:**

```
ResolveLegacyFallbackDir    ResolveV2LegacyDir          ResolveV2LegacyFilePath
PathRouter.ToLegacyPath     LoadEntriesFromLegacyIndex  IsV2PathScope
StripNamespacePrefix        ReservedNamespaceDirs       FormatRoundTripQualifiedName
```

The read path knows exactly one layout. No fallback chain, no gate, no user-visible failure
state for an unmigrated store.

### 5. Migration — one engine, two triggers

Modelled on `EmbeddingReindexer`, which already pairs an automatic trigger
(`ReindexIfStaleAsync`, called from `WorkspaceSetup`) with a manual one (`ForceReindexAsync`,
called by `scri reindex`) over a single implementation.

```csharp
// Scrinia.Core — the only implementation
public static class StoreLayoutMigration
{
    public static Result MigrateIfNeeded(string root);                    // automatic
    public static Result Migrate(string root, bool dryRun, bool backup);  // manual
    public sealed record Result(int Moved, int Conflicts, bool Ran, string? BackupPath);
}
```

Both funnel to one private routine, so there is no second path to drift.

**Automatic.** `MigrateIfNeeded` runs on first store access — from `LoadIndex` /
`EnsureMaterialized`, not the constructor, which does no IO today and should not start. The fast
path is three `Directory.Exists` calls; once the old roots are gone it can never do work again.
Silent on no-op; one stderr line when it moves something, so an unexpected backup directory is
never a surprise.

**Manual.** `scri migrate` is the **existing** `migrate_v1` command, repurposed:

| | `migrate_v1` today | `scri migrate` |
|---|---|---|
| target | v1 → v2 | any older layout → v3 |
| visibility | `[Hidden]` | visible in `--help` |
| file operation | `File.Copy`, originals kept | move |
| `--cleanup` | required second step | removed |
| `--dry-run` / `--backup` / `--workspace` | yes | yes |

It exists so a user can preview, migrate deliberately before a large operation, or re-run after
restoring a backup — and it is the only trigger with a console to report through.

**Shared mechanics.**

- **Whole-store in one bounded pass**, never lazy per-scope.
- **`FileLock.AcquireExclusive`** on `.scrinia/` for the duration; cross-process safe.
- **Moves, never copies.** Exactly one copy of each memory exists at all times.
- **Idempotent and resumable.** The rule is "anything still in an old location moves," so a
  crash mid-pass leaves a valid partial state the next trigger completes. No marker file — the
  absence of the old directories *is* the signal, and cannot itself drift.
- **Backup once, only when there is work:** `.scrinia-backup-{ts}/`.
- **Collisions resolved at migration time.** `topics/api/x` and `topics/memory/api/x` both
  target `memories/api/x`: the non-namespaced entry wins (matching the precedence the old read
  fallback used), and the loser moves to `.scrinia-conflicts-{ts}/` with a printed summary.
  Never silently dropped, never left where a read could find it.

**Mapping.**

```
.scrinia/store/<s>.nmp2            ->  .scrinia/memories/<s>.nmp2
.scrinia/topics/<topic>/<s>.nmp2   ->  .scrinia/memories/<topic>/<s>.nmp2
.scrinia/topics/memory/<topic>/…   ->  .scrinia/memories/<topic>/…       (namespace dropped)
.scrinia/topics/entity/skill/…     ->  .scrinia/memories/skill/…         (namespace dropped)
.scrinia/topics/agent/…            ->  .scrinia/memories/agent/…
.scrinia/agent/<n>.md              ->  .scrinia/memories/agent/<n>.md
.scrinia/memories/**               ->  unchanged
```

Sidecars (`.meta.json`), `versions/`, and `facts.json` move with their artifact. The
`index.json` → sidecar upgrade currently performed by `LoadEntriesFromLegacyIndex` moves into
the migration.

**Vector directories.** `VectorStore.ScopeToRelativeDir` derives the embedding directory from the
scope string, so only the two renaming families orphan vectors. Migration moves
`embeddings/local/entity/skill/` → `embeddings/local/skill/` and the `memory/<topic>`
equivalents. If a move fails it is reported with a pointer to `scri reindex` rather than left
half-done.

### 6. Failure handling and rollback

| Condition | Behaviour |
|---|---|
| Mid-pass failure (disk full, permissions) | Partial state is valid and resumable; backup already written; next access completes it |
| Lock contention | `AcquireExclusive` retries with backoff; on timeout the migration **throws**. Proceeding would report an empty store, which is data-loss-shaped silence |
| Collision | Never blocks; resolved deterministically and reported |

**Rollback** is: stop the tool, replace `.scrinia/` with `.scrinia-backup-{ts}/`. Documented under
`scri migrate` in `docs/cli-reference.md`.

## Testing

### Disposition of the 41 `ScriniaArtifactStoreTests`

**24 deleted** — duplicates of existing `FileMemoryStoreTests` coverage (`Upsert_*` ×3,
`Remove_*` ×3, ephemeral round-trips and predicates ×7, `ResolveArtifactAsync_*` ×2,
`DiscoverTopics_LocalTopics_Found`, `Search_MultiTerm_SumsPerTermScores`, the serialization
round-trips ×4), tests of SAS-only statics (`Configure_DoesNotMaterializeTheStore`,
`StoreDir_DefaultValue_ContainsScrinia`), and the two `ScriniaArtifactStore_LoadIndex_MergesLegacyEntries`
tests covering machinery being removed.

**17 ported to `FileMemoryStoreTests`** — real gaps that would silently open if the file were
simply deleted:

| Test group | Count |
|---|---|
| `LoadIndex` empty states — corrupt / empty / missing / absent store dir | 4 |
| `SanitizeName_*` edge cases | 3 |
| `ArchiveVersion_*` | 2 |
| `SaveIndex_*` — atomic round-trip, no temp file left | 2 |
| `NameFromUri_*` | 2 |
| `ResolveReadScopes_*` | 2 |
| `NormalizeScopeFilters_BareTopicName_ReturnsLocalOnly` | 1 |
| `EveryWriteMaterializesTheGitIgnore` | 1 |
| **Total** | **17** |

`FileMemoryStoreTests` has no `ResolveReadScopes` or `NormalizeScopeFilters` coverage today;
those two groups are genuine gaps, not relocations.

### Converting the fallback tests

Exactly **13 existing `FileMemoryStoreTests` exercise behaviour this workstream removes**:

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

`BuildScopedTopicScope_AgentTopic_RoutesToAgentScope` and
`FormatQualifiedName_AgentScope_StripsPrefix` are **not** in this set: `local:agent` survives v3
unchanged and contains no namespace separator to strip, so both still pass as written.

The three `AttributeMigration` forward-fill tests — `LoadIndex_ForwardFillsLegacySidecar_InMemory`,
`LoadIndex_PersistsForwardFill_ToPrimarySidecar`, and
`LoadIndex_ConcurrentColdLoads_ForwardFillWriteBackDoesNotRace` — are also **not** in this set.
Read-time attribute forward-fill is orthogonal to directory layout and survives this workstream
unchanged.

Each is **converted, not deleted**: "entries exist in the legacy flat dir" stops meaning *reads
merge them* and starts meaning *migration moves them*. Same scenario and intent, relocated
assertion. This is the strongest available signal that the fallback is genuinely gone — if any of
these still passes unchanged, it was not removed.

### New coverage for `StoreLayoutMigration`

- Every source layout in the mapping table, including `index.json` → sidecar.
- Collision: non-namespaced wins, loser in `.scrinia-conflicts-{ts}/`, count reported.
- **Resumption**: interrupt mid-pass, re-trigger, verify completion and no duplicates.
- **Idempotency**: a second run is a no-op and writes no backup.
- Vector directories move for the two renaming scope families.
- **Trigger equivalence**: parameterised over `MigrateIfNeeded` and `Migrate` asserting identical
  on-disk results — the test that keeps the two entry points from drifting.

### Gates

- The existing suite (1,529 `[Fact]`/`[Theory]` methods) green after every commit.
- `ConsolidateReembedTests.cs` reviewed at commit 3 — the one place a test edit is predicted.
- A manual end-to-end against a copy of this repository's own `.scrinia/`: 3 memories under
  `topics/findings/` plus an `embeddings/local_findings/` flat-layout vector directory. A real v2
  store, not a synthetic fixture.

## Out of scope

- **`IMemoryStore`'s size and the `SearchAll` overload family**, including the interface default
  implementations that `HttpMemoryStore` silently inherits — workstream B.
- **The `MemoryTools.Remember` / `MemoryOrchestrator.StoreAsync` duplication** and the missing
  REST anchors/attributes — workstream C.
- **The `IMemoryEventSink` / `IMemoryOperationHook` dual plugin pipeline** — deferred to a later
  design; noted here because it is the third instance of the same entry-point-duplication
  pattern.
- **Removal of `StoreLayoutMigration` itself.** It sits behind a self-clearing condition and
  touches no read path, so once a store is migrated, deleting it cannot change that store's
  behaviour. No removal is scheduled because none is needed.
