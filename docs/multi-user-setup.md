# Multi-User Setup Guide

When multiple developers use scrinia on the same repository, memory files in `.scrinia/` are tracked by git and merge like any other file. This guide covers the tools and configuration that prevent and resolve merge conflicts.

## Prerequisites

- Git 2.x or later
- `jq` command-line tool (for the bash merge driver) — install via your package manager
- PowerShell 7+ (for the PowerShell merge driver on Windows)

## What Can Conflict

| File type | Conflict risk | Resolution |
|-----------|--------------|------------|
| `.meta.json` | Medium — keywords/timestamps may diverge | Auto-resolved by merge driver (keyword union) |
| `.nmp2` | Low — per-file sidecars mean different memories don't conflict | Manual via `resolve_conflict()` |
| `versions/` | None — timestamped archives never collide | N/A |

## Merge Driver Setup

The merge driver auto-resolves `.meta.json` conflicts by unioning keywords and taking the latest timestamps.

### Bash (Linux/macOS)

```bash
# Configure git to use the scrinia merge driver
git config merge.scrinia-meta.driver ".scrinia/hooks/scrinia-merge-meta.sh %O %A %B"
```

### PowerShell (Windows)

```powershell
# Configure git to use the PowerShell merge driver
git config merge.scrinia-meta.driver "pwsh .scrinia/hooks/scrinia-merge-meta.ps1 %O %A %B"
```

### Verify

The `.scrinia/.gitattributes` file (tracked in the repo) tells git which files use the driver:
- `*.nmp2 binary` — prevents text merge on compressed artifacts
- `*.meta.json merge=scrinia-meta` — routes to the custom driver

## Post-Merge Hook

The post-merge hook warns you when conflicts remain after a merge.

### Installation

```bash
# Option 1: Copy (manual updates needed)
cp .scrinia/hooks/post-merge .git/hooks/post-merge
chmod +x .git/hooks/post-merge

# Option 2: Symlink (auto-updates)
ln -s ../../.scrinia/hooks/post-merge .git/hooks/post-merge
```

## Workflow After Merge

When pulling or merging a branch that touches `.scrinia/`:

1. **Pull/merge** — git applies the merge driver for `.meta.json` files automatically
2. **Check for warnings** — the post-merge hook reports any remaining conflicts
3. **`reconcile()`** — run in your agent session to scan for and resolve remaining conflicts
4. **`resolve_conflict(id, choice)`** — resolve each conflict: `"ours"`, `"theirs"`, or `"merged"` with custom content
5. **`reconcile()`** again — verify 0 conflicts remaining
6. **Commit** — commit the resolved `.scrinia/` files

## Structural Prevention

Scrinia's architecture minimizes conflicts by design:
- **Per-file sidecars** — each memory has its own `.meta.json`, so different memories modified by different developers never conflict
- **Per-phase retrospectives** — `learn:retro-gN-phaseId` instead of one growing monolith
- **Sorted metadata** — keywords and term frequencies sorted alphabetically for deterministic diffs
- **Binary marking** — `.nmp2` files marked as binary to prevent garbled text merge

## Troubleshooting

**"merge driver not found"** — Run the `git config` command from the Merge Driver Setup section. The driver path is relative to the repo root.

**"jq not found"** — Install jq: `brew install jq` (macOS), `apt install jq` (Ubuntu), `choco install jq` (Windows).

**Conflicts after driver runs** — The merge driver handles single-conflict .meta.json files. Multiple conflict regions in one file (rare) fall through to manual resolution via `reconcile()`.

**Stale memories after merge** — Run `check_drift()` to detect memories referencing files that changed on the other branch.
