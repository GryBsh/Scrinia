# Multi-User Setup Guide

When multiple developers use scrinia on the same repository, memory files in `.scrinia/` are tracked by git and merge like any other file. This guide covers the tools and configuration that prevent and resolve merge conflicts.

## Prerequisites

- Git 2.x or later
- Scrinia installed and on your PATH

## What Can Conflict

| File type | Conflict risk | Resolution |
|-----------|--------------|------------|
| `.meta.json` | Medium — keywords/timestamps may diverge | Auto-resolved by merge driver (keyword union) |
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

## Workflow After Merge

When pulling or merging a branch that touches `.scrinia/`:

1. **Pull/merge** — git applies the merge driver for `.meta.json` files automatically
2. **Read the conflict report** — a conflicted `.nmp2` stops the merge and git marks the file unmerged
3. **`reconcile()`** — run in your agent session to scan for and resolve remaining conflicts
4. **`reconcile({ conflictId, choice })`** — resolve each conflict by its workspace-relative path (e.g. `memories/local/auth-flow.nmp2`): `"ours"`, `"theirs"`, or `"merged"` with custom content
5. **`reconcile()`** again — verify 0 conflicts remaining
6. **Commit** — commit the resolved `.scrinia/` files

## Structural Prevention

Scrinia's architecture minimizes conflicts by design:
- **Per-file sidecars** — each memory has its own `.meta.json`, so different memories modified by different developers never conflict
- **Per-phase retrospectives** — `learn:retro-gN-phaseId` instead of one growing monolith
- **Sorted metadata** — keywords and term frequencies sorted alphabetically for deterministic diffs

## Troubleshooting

**"merge driver not found"** — Run `scri setup --multi-user`, then confirm `scri-merge` sits
in the same directory as `scri`.

**Conflicts after driver runs** — The merge driver handles single-conflict .meta.json files. Multiple conflict regions in one file (rare) fall through to manual resolution via `reconcile`.

**Stale memories after merge** — Run `list({ mode: "drift" })` to detect memories referencing files that changed on the other branch.
