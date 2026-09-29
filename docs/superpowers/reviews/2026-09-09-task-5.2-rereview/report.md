# Task 5.2 re-review report

## Scope

- Baseline: current worktree against `HEAD`, scoped to the package/lifecycle
  diff paths in `manifest.tsv`.
- Inventory: exactly 5 rows in `manifest.tsv`.
- Diff boundary: `exact.diff` contains only those 5 paths.
- No source or test files were changed while producing this package.

## Failure groups — none excluded

1. Package metadata and package artifact contents (all eleven package assertions).
2. Global-tool lifecycle stages: pack, install, version, local serve, shared serve,
   setup, update, restart, and uninstall.
3. Child-process termination, process-tree cleanup, redirected stdout/stderr
   draining, and the positive child-liveness proof.
4. Cross-test/process environment isolation and fixture cleanup.
5. Plugin coverage, release build coverage, and scoped diff integrity.

The parent/child timeout fixture is explicitly covered by all three lifecycle
transcripts. Its child-owned named event is created before the startup marker;
the child blocks before `child-finished`, and `TryOpenExisting` fails after
`CloseAndExitAsync`, proving positive child termination rather than relying on a
fixed delay. No failure group was removed, reclassified, skipped, or
rebaselined.

### Complete retained inventory

The package lane is scoped to rows 17–28, but the complete Phase A inventory is
reproduced here so no failure group is silently omitted: 1) compact no-op CLI
exit; 2) compact notification/no-event contract; 3) compact response golden; 4)
generic no-result/no-op warning status; 5) release-workflow manifest
invocations; 6) packaging/docs merge-retirement surface; 7) Phase 4
documentation byte identity; 8) living documentation retired-surface scan; 9)
historical planning/release byte identity; 10) obsolete-address classification
file/surface scan; 11) onboarding shared-base guidance; 12) shared-envelope
follow-up/page ordering; 13) shared-envelope payload-family serialization; 14)
present-empty-files serialization; 15) CLI retained-journal recovery block; 16)
MCP retained-journal recovery block; 17) isolated global-tool lifecycle; 18)
package declares `scrinia` DotnetTool metadata; 19) `scri` command is under
`tools`, not `lib`; 20) managed dependencies are present; 21) prompts,
skills, schemas, and setup templates are present; 22) package readme is at the
root; 23) merge/`scri-merge` is excluded; 24) Vulkan/LLamaSharp is excluded;
25) native RID assets are excluded; 26) downloaded models are excluded; 27)
repository/workspace data is excluded; 28) mutable caches are excluded.

Rows 1–16 remain retained Phase A failures/inventory context; this package does
not claim to resolve or discard them. Rows 17–28 are the package/lifecycle
scope evidenced here.

## Evidence result

- Release solution build: passed; 13 projects, 0 errors, 2,920 existing analyzer
  warnings plus preview-SDK messages.
- Plugin lane: 41 passed, 0 failed, 0 skipped.
- Lifecycle run 1: 3 passed, 0 failed, 0 skipped.
- Lifecycle run 2: 3 passed, 0 failed, 0 skipped.
- Lifecycle run 3: 3 passed, 0 failed, 0 skipped.
- Package-content lane: 12 passed, 0 failed, 0 skipped (standalone rerun, 8 s).
- Scoped diff check: passed; no whitespace findings in the five scoped paths.

## Assessment

The package is fresh through the timestamps recorded in `freshness.txt` and
`freshness-files.tsv`. Reviewers can verify all five scoped paths, inspect the
exact diff, and compare the package, plugin, build, diff, and three lifecycle
transcripts without relying on unrelated worktree changes. The concurrent
package-content timeout is recorded as a timeout, not a pass; only the
standalone rerun is counted as its result.
