# Wave 1 Task 5 Report — C5 MCP Tool Surface Leak

## Summary
Sealed MCP tool surface to exactly 11 public `[McpServerTool]` instance methods via visibility change; verified no public helper leaks via reflection test. `MatchesRefAttribute` spelling already correct (single `b`), no rename needed. `Bundle` was the sole leaking public instance helper and was made `internal`.

## Requirement vs Reality
- **Plan states:** exactly 12 tools (`guide`, `remember`, `recall`, `search`, `list`, `append`, `forget`, `compact`, `link`, `restore`, `reconcile`, `facts`)
- **Reality after merge retirement (Task 3, commit d792410):** `reconcile` removed from every agent-visible surface, pinned by `ReconcileRemovalTests` (MCP, CLI, guide, goldens). Live set is 11.
- **This task implements 11** — the correct post-retirement count. A 12-tool test would reintroduce `reconcile` and fail `ReconcileRemovalTests`. Report notes divergence for plan update.

## Changes
- `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`:
  - `Bundle` changed `public` → `internal` with updated XML remarks explaining `WithTools<T>` leak. Sole instance helper leak.
- `tests/Scrinia.Tests/Wave1/McpToolSurfaceTests.cs` (new):
  - 5 tests: `ExposesExactly11Tools`, `NoPublicHelperOutsideToolSet` (DeclaredOnly-scoped to avoid `System.Object` noise), `MatchesRefAttribute_SpellingIsSingleB`, `WithToolsRegistersExactly11`, `ReconcileIsNotATool`.
  - Expected names: `append`, `compact`, `facts`, `forget`, `guide`, `link`, `list`, `recall`, `remember`, `restore`, `search` (alphabetical comparison).

## Why Bundle was the leak
`WithTools<ScriniaMcpTools>` registers all public instance methods; an un-attributed public `Bundle` would be iterable as a tool by overly-broad hosts. History comment said "deliberately public for external embedders" — no external caller exists; CLI uses `BundleCommands`. Sealing to `internal` restores compiler-checked guarantee.

## Static helpers note
`FormatBytes` (`MemoryTools.Bundling.cs`) and `CreateToolSerializerOptions` (`MemoryTools.Skills.cs`) remain `public static`. They are required cross-assembly (`Scrinia` host and `scri serve` startup) and `WithTools` scans only instance methods, so they do not leak the tool surface. `rg "public "` still shows them; instance-only `rg` or reflection test is the correct gate.

## Verification
- `dotnet build Scrinia.sln -c Debug` — clean, 0 errors (warnings only from preview SDK + unrelated CA rules).
- `dotnet test tests/Scrinia.Tests --filter McpToolSurface` — 5/5 passed.
- `ReconcileRemovalTests` — still green (reconcile absent on CLI, MCP, guide, goldens).
- `rg "public " src/Scrinia.Mcp/MemoryTools*.cs` after fix:
  - Instance public = 11 `[McpServerTool]` methods only (`Guide`, `MemoryRemember`, `MemoryRecall`, `MemorySearch`, `MemoryList`, `MemoryAppend`, `MemoryForget`, `MemoryCompact`, `MemoryLink`, `MemoryRestore`, `Facts`).
  - Static public = 2 (`FormatBytes`, `CreateToolSerializerOptions`) — intentional, not tools.

## Checklist
- [x] Early Exit: test asserts guard before any filesystem touch (reflection only)
- [x] Parse Don’t Validate: scope/topic validated at construction, not here
- [x] Atomic Predictability: visibility change is pure, no mutation
- [x] Fail Fast: helper leak fails test immediately with collection dump
- [x] Intentional Naming: `MatchesRefAttribute` spelling pinned
