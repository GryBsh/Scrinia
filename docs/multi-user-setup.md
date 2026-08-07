# Multi-User Setup Guide

When multiple developers use scrinia on the same repository, memory files in `.scrinia/` are tracked by git and merge like any other file. This guide covers the tools and configuration that prevent and resolve merge conflicts.

## Prerequisites

- Git 2.x or later
- Scrinia installed and on your PATH

## What Can Conflict

| File type | Conflict risk | Resolution |
|-----------|--------------|------------|
| `.meta.json` | Medium — keywords/timestamps may diverge | Auto-resolved by merge driver (keyword union) when keyword similarity is at or above `jaccardThreshold` (default 0.7) and no scalar field changed on both sides; otherwise conflicts |
| `.nmp2` | Low — per-file sidecars mean different memories don't conflict | Manual via `reconcile({ conflictId, choice })` |
| `versions/` | None — timestamped archives never collide | N/A |

## Merge Driver Setup

Run this once per clone:

```bash
scri setup --multi-user
```

It registers both merge drivers in the repository's local git config, writes
`.scrinia/.gitattributes` mapping `*.meta.json` and `*.nmp2` to them, and creates
`.scrinia/merge.config`. The `scri-merge` binary it points at ships alongside `scri`, so
there is nothing to copy.

### Upgrading an existing workspace

**Workspaces configured before this release must re-run `scri setup --multi-user`.** The
older registration points at `.scrinia/hooks/scri-merge`, a path no release ever shipped.
Git cannot run it, so the driver never executes and cannot repair its own registration:
the merge leaves the path unmerged with `ours`' content and no conflict markers, and
`reconcile()` finds nothing to resolve. Re-running the command re-points both drivers at
the installed binary. Do it once per clone.

## Workflow After Merge

When pulling or merging a branch that touches `.scrinia/`:

1. **Pull/merge** — git applies the merge driver for `.meta.json` files automatically
2. **Read the conflict report** — a conflicted `.nmp2` stops the merge and git marks the file unmerged
3. **`reconcile()`** — run in your agent session to scan for and resolve remaining conflicts
4. **`reconcile({ conflictId, choice })`** — resolve each conflict by its workspace-relative path (e.g. `memories/local/auth-flow.nmp2`): `"ours"`, `"theirs"`, or `"merged"` with custom content
5. **`reconcile()`** again — verify 0 conflicts remaining
6. **Stage the resolved paths** — `reconcile()` only rewrites the working-tree file; git's
   index still shows the path unmerged until you stage it. Run `git add <path>` (or
   `git add .scrinia/`). The driver writes a `.gitignore` into each conflict directory as it
   creates it, so the `conflict/current` and `conflict/incoming` copies stay out of the
   commit; delete those directories once you are done to keep the workspace tidy
7. **Commit** — commit the resolved `.scrinia/` files

## Structural Prevention

Scrinia's architecture minimizes conflicts by design:
- **Per-file sidecars** — each memory has its own `.meta.json`, so different memories modified by different developers never conflict
- **Per-phase retrospectives** — `learn:retro-gN-phaseId` instead of one growing monolith
- **Sorted metadata** — keywords and term frequencies sorted alphabetically for deterministic diffs

## Troubleshooting

**`error: cannot run <path>: No such file or directory`** — git could not launch the
registered driver. Run `scri setup --multi-user`, then confirm `scri-merge` sits in the same
directory as `scri`. If the path in the message is `.scrinia/hooks/scri-merge`, this is the
pre-release registration described under [Upgrading an existing workspace](#upgrading-an-existing-workspace).

**`.meta.json` conflict with nothing for `reconcile` to find** — the meta driver conflicts
when keyword similarity scores below `jaccardThreshold` in `.scrinia/merge.config`, or when
`name`, `uri`, `description`, `originalBytes`, or `chunkCount` changed on both sides. It writes
nothing, so the path is left unmerged in git's index with `ours`' content and no conflict
markers — `reconcile()` has nothing to scan for. Resolve it with git directly:
`git checkout --ours -- <path>` or `--theirs`, or edit the file by hand, then `git add <path>`.

**Stale memories after merge** — Run `list({ mode: "drift" })` to detect memories referencing files that changed on the other branch.
