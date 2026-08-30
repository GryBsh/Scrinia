---
name: project_published_surface_coherence_closed
description: The fix/published-surface-coherence branch (Parts A+B, Tasks 1-10) is CLOSED as of Task 10 — do not resume it. Part C (plugin-contract inversion) was split off to a separate future M3 branch, not implemented here.
metadata:
  type: project
---

`fix/published-surface-coherence` shipped Part A (server route-permission metadata, Tasks 1-4) and
Part B (MCP tool-family coherence, Tasks 5-9) plus a Task 10 close-out (breaking-change CHANGELOG
entries, the two response-envelope decisions T10-A/B implemented and T10-C recorded, T10-D/E
closed, two known divergences documented in `docs/architecture/overview.md`). Task 10 was
explicitly the last task on this branch.

**Part C (the plugin-contract inversion — `IMemoryStore`-to-`IPluginStore` seam, moving DTOs into
`Scrinia.Plugin.Abstractions`) was never implemented here.** A pre-implementation falsification
pass found it structurally blocked (five findings, including a `FrameworkReference` on ASP.NET Core
that would leak into the trimmed CLI publish). The user decided (2026-08-18) to split it into its
own future branch rather than force it into this one. Findings are recorded in the Scrinia memory
`/findings/plugin-contract-inversion-blockers` and summarized at the foot of
`docs/superpowers/plans/2026-08-18-published-surface-coherence.md`.

**How to apply:** if a future session is asked to "continue" or "finish" this branch, the correct
answer is that it is already finished — Parts A and B are done and merged-ready; Part C is a
separate, not-yet-started piece of work with its own blockers to solve first, not a loose end on
this branch.

## Task 10 close-out record (2026-08-30)

- Commit: `67ff73f` (`docs: close Task 10 addressing guidance`).
- Committed only the Task 10 CHANGELOG, architecture and public guidance updates, MCP guide prompt,
  and `EphemeralEntry` migration projection. Benchmark/test edits, untracked server files, and
  untracked test files were intentionally left out; Task 11 was not implemented.
- Verification: `dotnet test Scrinia.sln --no-restore` completed with 187 failures, 2,036 passed,
  and 20 skipped out of 2,243 tests. The failures are in the broader in-progress address migration
  and were not changed by this close-out commit.

## Task 10 addressing follow-up verification (2026-08-30)

- Fresh verification: `dotnet test Scrinia.sln --no-restore` completed with 208 failures, 2,016
  passed, and 20 skipped out of 2,244 tests in `Scrinia.Tests`; `Scrinia.Plugin.Embeddings.Tests`
  passed 32 of 32 tests. The failures remain in the broader in-progress address migration.
- Commit status: not committed by this agent; the Task 10 candidate changes remain in the worktree
  for the orchestrator to commit. Unrelated `src/Scrinia.Server/` and Task 11 conformance test files
  remain untracked and untouched.

## Task 10 review follow-up (2026-08-30)

- MCP remember, append, and compact now construct writes through `MemoryWrite.Create`, so nullable
  content is categorized by shared `InvalidWriteContent` validation before UTF-8 encoding or store
  mutation.
- `IPluginStore` and `MemoryVectorIndexer` no longer publish raw logical-address parsing/removal
  routes; physical vector identity remains private, while plugin resolution is typed.
- Focused validation and obsolete-surface tests pass (14 tests). The untracked server and Task 11
  conformance files were not modified.

## Task 10 completion migration (2026-08-30)

- Removed the internal raw `(Name, Scope, Content)` `MemoryWrite` constructor. All live production,
  test, and benchmark construction sites now use `MemoryWrite.Create` and typed `MemoryAddress`.
- Removed raw logical-name members from `IPluginStore`; concrete parsing and scope-only helpers are
  internal, while typed address resolution remains the plugin boundary. Physical vector removal is
  private and only the typed indexer route is public.
- Added reflection conformance coverage for Core contracts, store/plugin routes, and physical helper
  visibility. Nullable MCP content continues through categorized `InvalidWriteContent` validation.
- Verification: focused address conformance tests pass (6); solution build passes with existing
  analyzer warnings. Historical documentation and the untracked server/Task 11 files were left
  untouched.
