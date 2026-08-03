# Workspace root resolution and lazy store creation — design

**Date:** 2026-08-03
**Status:** Approved (design); implementation plan pending
**Scope:** Spec 1 of 2. Spec 2 (per-memory file layout) is a separate design.

## Context

Agents running in unrelated projects see each other's memory hints. Reported case: an
agent in `C:\projects\bludon` surfacing hints for `wslc-docker-socket`.

**Root cause.** `WorkspaceSetup.FindWorkspaceRoot` (`src/Scrinia/Commands/WorkspaceSetup.cs:494-504`)
walks up from cwd looking for a `.scrinia/` directory with no stopping condition — it
ascends to the filesystem root. `C:\.scrinia` exists, so every project on the drive
without a store of its own binds to it and shares one memory pool.

Verified on the reporting machine:

| path | own `.scrinia` |
|---|---|
| `C:\projects\bludon` | no |
| `C:\projects` | no |
| `C:\SynologyDrive\Source\repos\wslc-docker-socket` | no |
| `C:\SynologyDrive\Source\repos` and every parent | no |
| `C:\` | **yes** |

**Why it is self-inflicting.** Creation is a side effect of *reads*, not writes:

- `FileMemoryStore`'s constructor calls `EnsureGitIgnore`, which unconditionally runs
  `Directory.CreateDirectory(".scrinia")` (`src/Scrinia.Core/FileMemoryStore.cs:204-210`).
  This runs on every command, including ones that store nothing.
- `FileLock.Acquire` runs `Directory.CreateDirectory` (`src/Scrinia.Core/FileLock.cs:39`)
  and opens the lock `FileMode.OpenOrCreate` (`FileLock.cs:50`). `LoadIndex` takes a shared
  lock before every read (`FileMemoryStore.cs:713`).

`scri hint` fires on every prompt in every project via the UserPromptSubmit hook and is a
pure read, yet it materializes a store. A single agent session with cwd at the drive root
is sufficient to seed `C:\.scrinia`, after which every project beneath it is captured.

The two halves are load-bearing together: bounded resolution stops a project from *joining*
a foreign store; lazy creation stops it from *seeding* one.

**Contributing factors.**

- Hooks stamp a bare `scri hint` with no `--workspace-root` (`src/Scrinia/Commands/Hooks/AgentHookSetup.cs:54`),
  so the hint path always resolves from cwd.
- Scrinia writes no MCP server configuration anywhere in `src/`, so `scri serve`'s cwd is
  entirely the client's choice.
- `FindWorkspaceRoot` is private to the CLI project and has no test coverage.
- Both `Serve` and `Hint` already document `--workspace-root` as *"Defaults to current
  working directory"* (`ScriniaCommands.Serve.cs:25`, `ScriniaCommands.HintCommand.cs:27`).
  The walk-up is undocumented behaviour that diverges from the stated contract; removing it
  brings the code back in line with its own help text.

## Rejected alternatives

**Stop the walk one level below the filesystem root.** Rejects `C:\.scrinia` but permits
`C:\projects\.scrinia`, which would capture every project under `C:\projects` — the identical
failure one level down, at a directory where a stray invocation is *more* likely because it is
where users `cd` before entering a project. The protection is also a function of accidental path
depth: the stop-point sits 1 level above `C:\projects\bludon` but 3 levels above
`C:\SynologyDrive\Source\repos\wslc-docker-socket`. UNC paths need a second definition of "root"
before the rule can even be evaluated.

**Broaden the marker set to `.gitignore` and README variants.** Markers meaning *"a human
organised this"* are not markers meaning *"this is a project root."* `.git` is structural — it
defines a version-controlled unit. A README is descriptive and is if anything *attracted* to
container directories: the reporting machine has `README_START_HERE.txt` in
`C:\SynologyDrive\Source\repos`, a directory holding 30+ unrelated repos, and that file exists
precisely because the directory is a container. `.gitignore` is nearly always co-located with
`.git` so adds little, and appears in subdirectories of repos routinely.

The failure costs are asymmetric, so the marker set is biased strict:

- **Too strict** (miss a real root) → falls back to cwd → empty store, noticed immediately,
  fixed with `--workspace-root`. Loud, local, recoverable.
- **Too loose** (accept a container) → many projects silently share one store → the reported
  bug. Silent, wide, invisible until someone spots foreign hints.

**Walk for `.scrinia` and validate it with `.git`.** Keeps walking past repo roots that have no
store, so a repo without its own store still binds to an ancestor repo's store — it crosses the
repo boundary. On the reporting machine, deleting `Scrinia/.scrinia` would silently redirect
writes into `workspace-scrinia/.scrinia`. Rejected in favour of "nearest `.git` is the root."

**Require an explicit `scri init` before any store exists.** Strictest, but adds permanent
command surface and a setup step for every new project. Lazy creation already removes the
accidental-creation path.

## Design

### 1. Resolution

New pure function `WorkspaceRoot.Resolve(startDir)` in `Scrinia.Core`, so it is directly
testable — the current implementation is private to the CLI project, which is why it has no
coverage.

```
Resolve(startDir):
  explicit --workspace-root given -> use verbatim, no walk
  examine startDir itself first, then each ancestor in turn:
      first directory containing .git -> that directory is the root, stop
  no .git found anywhere              -> root = startDir, no walk
  store path = <root>/.scrinia        (existence irrelevant to resolution)
```

`startDir` is inclusive: a repository root resolves to itself, not to an ancestor.

Two properties follow. Resolution never consults whether a store exists, so a repo with no
store cannot be pulled into an ancestor's. The walk terminates at the first repo boundary, so
it cannot escape a project regardless of what sits above it.

**`.git` must be matched as a file or a directory.** In worktrees and submodules `.git` is a
*file* containing a `gitdir:` pointer. This repository uses worktrees (`.claude/worktrees/` is
gitignored, a `worktree-agent-ab94da5f` branch exists, and the harness creates them), so a
directory-only check would fail exactly when an agent runs inside a worktree. No secondary
worktree exists at design time, so this would not surface in manual testing.

Behaviour against the reported bug: `bludon` resolves to `C:\projects\bludon` (own `.git`),
`wslc-docker-socket` resolves to `C:\SynologyDrive\Source\repos` (nearest `.git`). Different
roots, no shared store, `C:\.scrinia` unreachable from both. The nested pair
`workspace-scrinia` / `workspace-scrinia\Scrinia` each resolve to themselves.

The one behavioural change for correct existing setups is the subdirectory case, and it
improves: `cd myproj/src && scri remember` currently creates `myproj/src/.scrinia`; it will
now resolve to `myproj` and write to the real store.

### 2. Lazy creation

Creation is split by lock intent rather than removed from `FileLock` wholesale. `FileLock` is
shared by three stores — `FileMemoryStore`, `ScriniaArtifactStore`, and `VectorStore` — and
`AttributeMigration.cs:163-166` documents a dependence on the directory-creating side effect.

- `FileLock.AcquireExclusive` — **unchanged.** Exclusive means write intent, so creating the
  lock's parent directory is correct. Preserves write behaviour across all three stores and
  the migration path.
- `FileLock.AcquireShared` — if the lock's parent directory does not exist, return a no-op
  lock; there is nothing to guard. Otherwise behave as today.

This confines the change to read paths and generalises correctly: `VectorStore` reading absent
vectors stops materialising an embeddings directory (`VectorStore.cs:372`), and
`ScriniaArtifactStore` gets the same (`ScriniaArtifactStore.cs:369`).

Writes route through one materialising wrapper so the invariant cannot be routed around:

```
WithExclusiveLock(scope, action):
    EnsureMaterialized(scope)      // mkdir .scrinia; write .gitignore if absent; mkdir scope dir
    using var l = FileLock.AcquireExclusive(GetLockPath(scope))
    action()
```

The five write methods — `PersistMigratedEntry`, `SaveIndex`, `Upsert`, `Remove`,
`TryUpdateEntry` (`FileMemoryStore.cs:896, 1020, 1060, 1106, 1165`) — convert to the wrapper,
which also removes five copies of the same lock preamble. `PersistMigratedEntry` takes
`storeDir` as a parameter rather than deriving it from scope and needs a small signature
reconciliation; it is the only site that is not a mechanical swap.

`LoadIndex` early-returns `[]` when the store directory is absent, before taking any lock.

**Rejected structural alternatives.** Materialising inside `FileLock` itself was rejected: it
makes a generic lock primitive Scrinia-aware and would silently create stores for any future
caller that locks something. Calling `EnsureMaterialized` at each of the five write sites was
rejected: it is five call sites held together by discipline, and a sixth write method added
later would fail with a confusing missing-directory exception.

### 3. Components

1. `src/Scrinia.Core/WorkspaceRoot.cs` — new. `Resolve(startDir)`, `FindOrphanStores(root, startDir)`.
2. `src/Scrinia.Core/FileLock.cs` — shared/exclusive split.
3. `src/Scrinia.Core/FileMemoryStore.cs` — `EnsureGitIgnore` out of the constructor; add
   `EnsureMaterialized(scope)` and `WithExclusiveLock(scope, action)`; convert the five write
   methods; `LoadIndex` early-return.
4. `src/Scrinia/Commands/WorkspaceSetup.cs` — `Configure` delegates to `WorkspaceRoot.Resolve`.
   All 22 `Configure` call sites inherit the fix unchanged; none of them need editing.
5. `src/Scrinia/Commands/Hooks/AgentHookSetup.cs` — stamp `--workspace-root` into hook commands
   at install time; installers already receive the workspace root.
6. Setup output and docs — show the MCP server entry with an explicit `--workspace-root`.

### 4. Data flow

```
construct:  new FileMemoryStore(root)      -> touches no disk at all
read:       LoadIndex -> dir missing?      -> return [], no lock, no create
write:      WithExclusiveLock -> EnsureMaterialized -> AcquireExclusive -> write
```

The constructor becoming pure is what removes the seeding mechanism: `scri hint` builds a
store object on every prompt, and today that alone writes to disk.

**Concurrency.** Skipping the shared lock when the directory is absent is safe because there is
nothing to read. If another process is mid-first-write, the reader returns empty, which is
indistinguishable from having read a moment earlier. A partially-created tree is never observed,
because the reader returns before touching anything.

### 5. Diagnostics and error handling

`FindOrphanStores(root, startDir)` collects `.scrinia` directories that resolution deliberately
did not use: every ancestor strictly above the resolved root, up to the filesystem root, plus
cwd itself when cwd differs from root. The resolved root's own store is never reported.

It runs **only** on writes, setup/install, and `serve` startup — never on `hint`, `recall`, or
`search`. The hint hook fires on every prompt, so the hot path gains zero probes.

The warning enumerates the orphan's topic directories so the migration command is concrete:

```
scrinia: ignoring store at C:\.scrinia
         using C:\SynologyDrive\Source\repos

to migrate its memories:
  scri bundle export --workspace-root "C:\" --topics agent,patterns,memory
  scri bundle import --workspace-root "C:\SynologyDrive\Source\repos" --path <file>
```

`bundle export` and `bundle import` both already accept `--workspace-root`
(`ScriniaCommands.Bundle.cs:25, 66`), so no new command surface is needed. Cost is one
directory listing, on paths already doing real work.

**No guard on where a store may be created.** An explicit `scri remember` run from `C:\` will
still create `C:\.scrinia`. A drive-root/home-dir blocklist is deliberately not added: it
reintroduces the arbitrary-boundary problem rejected above, and lazy creation already removes
the accidental path — creation now happens only where a user explicitly asked to store
something.

**Failure modes.** An unwritable root surfaces at first write with the path named. An explicit
`--workspace-root` pointing at a nonexistent directory follows the same lazy rule and is created
on first write.

**Existing stores are not touched.** `C:\.scrinia` is left in place; the warning surfaces it and
the user decides.

### 6. Rider — `.gitignore` template

`EnsureGitIgnore` is being moved, so the template it writes (`FileMemoryStore.cs:215-231`) is
corrected in the same change:

- `**/.lock` → `**/*.lock`. The current pattern only matches files named exactly `.lock`. The
  vector store writes `<name>.bin.lock`, which was never covered — `vectors.bin.lock` was
  tracked in git as a result. Fixed in this repo's checked-in copy in `f301f9f`, but the
  template that generates every new store still carries the bug.
- Add `embeddings/`. Vector indexes are derived from topic content and rebuildable with
  `scri reindex`, so they churn on every re-embed.

## Testing

**Resolution** (new coverage — currently none):

- nested repos stay separate (`Scrinia` vs `workspace-scrinia`)
- `.git` as a *file* resolves (worktree/submodule)
- no `.git` in ancestry → cwd, no walk
- an ancestor `.scrinia` above a `.git` boundary is never reached
- explicit `--workspace-root` bypasses resolution
- regression: two sibling directories under a parent holding `.scrinia` resolve to different
  roots — the reported bug

**Lazy creation** — asserts the *absence* of side effects, the part most likely to silently
regress:

- after `recall` / `search` / `hint` against an empty workspace, no `.scrinia` exists on disk
- after the first `remember`, the full tree plus `.gitignore` exists
- a shared lock against a missing store creates nothing
- an exclusive lock still creates its parent directory (guards the migration path)

**Orphan warning:**

- fires on write when an ancestor store exists
- silent for a genuinely new project
- absent from `hint`

## Out of scope

Per-memory file layout — making each memory a self-contained stack of same-name files, and
de-aggregating the per-scope `facts.json` and `vectors.bin` that are the actual git-conflict
hotspots. That is spec 2, written next. It is independent: this fix stands alone, and the
layout change does not depend on it.
