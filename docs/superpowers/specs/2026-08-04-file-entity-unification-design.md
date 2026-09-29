# File-Entity Unification Design

**Date:** 2026-08-04
**Status:** Approved
**Scope:** Workstream A follow-up — the `.scrinia/skills/` and `.scrinia/agent/` hand-authored file entities

## Goal

Give the hand-authored Markdown file entities (`skills`, `agent`) exactly one path
authority, delete the dead `workflows` category and the vestigial `local:skill` NMP/2
read fallback, and move `agent` back out of the memory store so that a store scope
directory can never contain a foreign metadata sidecar.

This is a code change. It contains no data migration — see [Why there is no
migration](#why-there-is-no-migration).

## Context

### The DRY problem

`.scrinia/skills/` is named in five places, each re-deriving the location by hand:

| # | Site | Role |
|---|------|------|
| 1 | `MemoryTools.Skills.cs:219` | writes `.scrinia/skills/{name}.md` |
| 2 | `MemoryTools.Skills.cs:274` | lists skills (`SkillLoad` with no name) |
| 3 | `MemoryTools.Skills.cs:392` | loads one skill |
| 4 | `MemoryTools.Lifecycle.cs:606` | session primer's "Skills available (N): …" |
| 5 | `BundleFormatService.cs:288` (`CategoryDir`) | bundle export + import |

Each calls a `GetScriniaBaseDir(store)` helper and joins `"skills"`. There are **two
different implementations** of that helper:

- `MemoryTools.Skills.cs:85` walks up the tree until it finds a directory named `.scrinia`.
- `BundleFormatService.cs:277` takes `Path.GetDirectoryName(GetStoreDirForScope("local"))`.

Under v3 both land on `.scrinia` and agree, but they are different algorithms; the second
breaks silently if `local` ever resolves more than one level deep. They agree by
coincidence, not by construction.

The unit of duplication here is not text. It is the single piece of knowledge *"project
skill overrides live at `.scrinia/skills/`"*, which currently has five homes and no
authoritative one.

### The sidecar collision

`MemoryTools.Skills.WriteSidecarMeta` writes metadata via
`Path.ChangeExtension(filePath, ".meta.json")`, so `qa.md` produces `qa.meta.json`.
`FileMemoryStore.WriteSidecarCore` writes an NMP/2 entry named `qa` as `qa.meta.json`.
**The two metadata models share one filename namespace.**

That is harmless while the file entities live outside store directories. The
store-unification work moved `agent` *into* `.scrinia/memories/agent/`, putting
`AgentFileMeta` sidecars in a directory the store enumerates. `SaveIndexUnsafe`
(`FileMemoryStore.cs:896-903`) deletes every `*.meta.json` it does not recognise:

```csharp
foreach (string metaFile in Directory.EnumerateFiles(storeDir, "*.meta.json").ToList())
    if (!expected.Contains(Path.GetFileName(metaFile)))
        File.Delete(metaFile);
```

Reachable path: `BundleFormatService` import → `IMemoryStore.ImportTopicEntries(topicScope, …)`
→ `SaveIndex(entries, topicScope)`. Importing a bundle containing a memory topic named
`agent` deletes the `.meta.json` of every `.md` agent config in `memories/agent/`.

This call chain was traced statically, not executed. A regression test is specified below.

### `PathRouter` is half-wired

`PathRouter` has four public methods. Only `ToStoreDir` has a production caller
(`FileMemoryStore.cs:417`). `ToFilesystemPath`, `ToVersionsDir`, and `ToMetadataPath` are
public, unit-tested, and called by nothing but each other and tests.

`ToFilesystemPath` already encodes an extension-per-first-segment rule
(`"agent" or "skill" => ".md"`, `"workflow" => ".json"`) — the model this design needs.
Its directory mapping is wrong (it joins under `memories/`), so it is corrected rather
than adopted as-is.

### `workflows` is dead

`FileEntityCategories = ["workflows", "skills", "agent"]`, but nothing writes a workflow.
The only code touching the category is bundle export/import. A `.scrinia/workflows/`
directory can only come into existence by importing a bundle that already contains one.

## Rejected alternatives

1. **Move skills into `memories/skill/` as file-bodied memories.** Skills become real
   store entries with a `.md` body; `SkillFileMeta` folds into `ArtifactEntry.Attributes`.
   Elegant, and it dissolves the collision by unification — but it relocates live,
   hand-authored, git-tracked files for no user-facing benefit, and it makes skills store
   entries when they are deliberately excluded from search. Rejected: the goal is
   unification of *knowledge*, not relocation of *data*.
2. **Leave `agent` in `memories/agent/` and teach the store to skip foreign sidecars.**
   Preserves the merge, but permanently leaves two metadata models sharing one directory
   and one filename pattern. The hazard is contained rather than removed, and two
   file-entity kinds end up in two different homes. Rejected in favour of a structural fix.
3. **Keep everything flat and only centralise the constant.** Fixes DRY, leaves the
   `agent` collision open for a separate spec. Rejected: the collision is a live data-loss
   path and the fix is small once `agent` moves back.
4. **Carry a defensive `memories/agent/` → `.scrinia/agent/` move-back rule.** See
   [Accepted risk](#accepted-risk). Rejected as dead-on-arrival code.

## Design

### 1. End state

```
.scrinia/
  memories/          NMP/2 store. Only FileMemoryStore writes here.
  skills/*.md        hand-authored, git-tracked
  agent/*.md         hand-authored, git-tracked
  embeddings/        derived
```

**The rule:** `.scrinia/memories/` holds the memory store and nothing else. Hand-authored
Markdown lives at the top level of `.scrinia/`. A store scope directory never contains a
foreign `.meta.json`, so the sidecar collision is impossible by construction rather than
by care.

### 2. `PathRouter` becomes the single authority

```csharp
// Segment name -> directory name. The singular/plural mismatch is historical and is
// currently implicit in five call sites; this is the only place it exists.
private static readonly Dictionary<string, string> FileEntityDirs = new(StringComparer.OrdinalIgnoreCase)
{
    ["skill"] = "skills",
    ["agent"] = "agent",
};

public static bool IsFileEntity(string segment);

/// Throws ArgumentException on an unknown kind, matching ToStoreDir's contract.
/// `kind` is always a literal, never user input.
public static string ToFileEntityDir(string kind, string workspaceRoot);

/// Returns false for an invalid or escaping name. Enforces both FileEntityName
/// validity and PathContainment, so no caller can forget either.
public static bool TryToFileEntityPath(string kind, string name, string workspaceRoot, out string path);
```

`ToFilesystemPath` delegates to these for `skill`/`agent` instead of joining under
`memories/`, which corrects it and finally gives it production callers. Its
`"workflow" => ".json"` arm is deleted with the rest of workflows.

### 3. Call sites repointed

| File | Change |
|------|--------|
| `MemoryTools.Skills.cs` ×3 | write / list / load → `ToFileEntityDir("skill", …)` |
| `MemoryTools.Lifecycle.cs:606` | skills listing → same |
| `MemoryTools.Lifecycle.cs:625` | agent followUp listing → `ToFileEntityDir("agent", …)` |
| `MemoryTools.Core.cs:38, :276` | agent recall / remember → same |
| `BundleFormatService.cs:288` | `CategoryDir` special-case **deleted**; both kinds resolve identically |

### 4. Deletions

- `"workflows"` from `FileEntityCategories`, leaving `["skills", "agent"]`.
- `PathRouter.ToFilesystemPath`'s `"workflow" => ".json"` arm.
- `ReadLegacySkillNmpAsync` and its two call sites (`MemoryTools.Skills.cs:249, :406`).
- `"agent"` from `StoreLayoutMigration.LegacyRoots`, leaving `["store", "topics"]`, plus
  the class-doc paragraphs describing agent's sweep.
- Both `GetScriniaBaseDir` implementations.
- `MemoryNaming.EntityTopics` **and `FileMemoryStore.IsEntityScope` entirely** — not just
  the `EntityTopics` lookup inside it. `IsEntityScope` has two branches: a namespaced
  `local:entity/*` test and a bare-topic `EntityTopics` test. Both are dead under v3 —
  the migration leaves no `entity/` directories, and skills are no longer store entries.
  Deleting only the set would leave a half-dead predicate behind.

  Its single caller is the default-scope loop at `FileMemoryStore.cs:523-531`, which skips
  entity-classified topics so unfiltered searches focus on user content. That loop
  simplifies to `ordered.AddRange(DiscoverTopics())`.

  `EntityTopics`' other use — the `entityTypes` parameter of `PathParser.Parse` at
  `FileMemoryStore.cs:268` — is already documented as inert (only `PathSegment.Value`
  feeds the result, never `IsEntityType`). The parameter still requires *some* set;
  pass an empty one.

  **Behaviour change, accepted:** a workspace holding a `memories/skill/` topic would
  newly appear in default search results. That state is only reachable if an unreleased
  dev build migrated a v2 `topics/entity/skill/` directory, the same population as the
  `local:skill` fallback deletion — so this costs nothing in practice.

  Three doc comments in `StoreLayoutMigration.cs` (lines 440, 618, 632) name
  `MemoryNaming.EntityTopics` and `FileMemoryStore.IsEntityScope` as explanatory
  references. They are `<c>` tags, not `<see cref>`, so deletion does not break the
  build — but they must be reworded rather than left pointing at removed symbols.

### 5. One new interface member

`IMemoryStore` gains `string WorkspaceRoot { get; }`. `FileMemoryStore` returns its
`_workspaceRoot`; `HttpMemoryStore` throws `NotSupportedException`, matching how it
already handles `ListTopicArtifacts` and `ImportTopicEntries`.

This makes explicit something currently accidental: **file entities are local-only.**
Remote mode gets a clear error instead of today's undefined behaviour from
`GetStoreDirForScope("local")`.

### 6. Sequencing

One absolute constraint: **removing `"agent"` from `LegacyRoots` and repointing the agent
readers must land in the same commit.** Any intermediate state has either the migration
moving `agent` while readers look at the old path, or readers moved while the migration
still sweeps — both silently orphan live files.

Everything else (skills, workflows, the fallback deletion, the `PathRouter` API) is
independent and may be ordered freely.

### 7. Error handling and edge cases

- **Invalid or escaping names:** `TryToFileEntityPath` returns `false`; callers keep their
  existing MCP error responses. Today the traversal guard is duplicated twice in
  `MemoryTools.Skills.cs` and absent from the listing paths.
- **Unknown kind:** `ToFileEntityDir` throws `ArgumentException`.
- **Remote store:** `NotSupportedException` from `WorkspaceRoot`.
- **Legacy bundles containing `files/workflows/`:** the import allowlist already
  `continue`s on unrecognised categories, so dropping `workflows` handles them — but
  silently. The import response must list skipped categories so a user is not left
  wondering where their workflows went.
- **Legacy NMP/2 skill entries:** now report not-found. This is a deliberate break; see
  below for why it costs nothing.

### Why there is no migration

`StoreLayoutMigration.cs` is absent at `v0.4.0` (2026-03-19), the latest tag, and the
store-unification work sits under `## [Unreleased]`. **The v3 migration has never shipped**,
so no released workspace has `memories/agent/`.

`v0.4.0` also contains no `skill` code anywhere in `src/` — the entire skills feature was
added on 2026-04-27, after that release. So `local:skill` NMP/2 entries can only exist in
workspaces driven by unreleased dev builds. Deleting the fallback orphans no released
user's data.

Moving `agent` back out is therefore not a data migration; it is deleting a sweep rule
before release.

### Accepted risk

Anyone who built and ran merged `main` between the store-unification merge and this fix,
*and* had a `.scrinia/agent/`, will have had it moved into `memories/agent/`. Afterwards
the readers look at `.scrinia/agent/` and their configs appear to vanish. The migration's
`.scrinia-backup-{timestamp}/` makes this recoverable.

**Decision: no code fixup.** The population is source-builders during a few-hour window;
the only known member is this repository, which has no `agent/` directory. A defensive
move-back rule would be dead code forever after. This is recorded in the CHANGELOG instead.

## Testing

The centrepiece is an invariant test, because it is the one that would have caught the
original collision.

1. **Store purity invariant.** `.scrinia/memories/**` contains no file-entity `.md` and no
   foreign `.meta.json` after any supported operation. This is §1's structural guarantee
   made executable.
2. **Bundle-import regression.** Importing a memory topic named `agent` must not delete
   anything under `.scrinia/agent/`. Encodes the exact `SaveIndex` orphan-sweep data loss.
3. **All readers agree.** Every repointed call site resolves to the directory `PathRouter`
   reports, mirroring the existing `ToStoreDir_MatchesFileMemoryStoreForEveryScopeShape`.
   This is the DRY guarantee made executable.
4. **`PathRouter` table.** Each kind maps to its literal directory — asserting the
   `skill`→`skills` and `agent`→`agent` quirk explicitly; unknown kind throws; traversal
   names rejected.
5. **Round-trip.** create → list → load → bundle export → import into a fresh workspace →
   still at `.scrinia/skills/`.
6. **Deliberate breaks asserted.** A workspace holding only a legacy NMP/2 skill entry
   reports not-found. `agent` is absent from `LegacyRoots`.
7. **Workflows.** A bundle containing `files/workflows/` imports without error and reports
   the skip.

## Out of scope

- Any data migration.
- Making skills searchable or store-backed.
- Workstream B (search contract, `IMemoryStore` split) and workstream C (write-path
  unification, REST anchors).
- The `.scrinia/workflows/` directory in any existing workspace: the category is deleted
  from the code, and any such directory is simply left on disk, unreferenced.
