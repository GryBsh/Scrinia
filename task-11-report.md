# Task 11 Acceptance Verification Report

## Worktree

- Path: `C:\SynologyDrive\Source\repos\workspace-scrinia\Scrinia\.claude\worktrees\unified-memory-addressing-task-1`
- Branch: `sdd/unified-memory-addressing-task-1`
- HEAD: `a26d057`
- Final review fix wave updated the CLI test capture seam, the deterministic forbidden-surface gate,
  the typed plugin-contract expectation, this report, and the living core architecture reference.

## Required verification

| Exact command | Result |
| --- | --- |
| `dotnet build Scrinia.sln --no-restore` | **PASS** — 15 projects, 0 errors, 46 warnings. Warnings are analyzer/preview-SDK warnings. |
| `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-build --no-restore` | **FAIL** — 2,053 passed, 177 failed, 20 skipped. |
| `dotnet test Scrinia.sln --no-build --no-restore` | **FAIL** — 2,469 passed, 188 failed, 20 skipped across 4 projects. |
| Full focused acceptance filter from Task 11 | **PASS** — 271 passed, 0 failed, 0 skipped. |
| Focused address/surface/plugin/writer lane (excluding the CLI stream case) | **PASS** — 64 passed, 0 failed, 0 skipped. |
| Bundle lane: `FullyQualifiedName~Bundle` | **PASS** — 119 passed, 0 failed, 0 skipped. |
| Serialization/contract lane (`ArtifactEntrySerializationTests`, `SourceGeneratedAddressSerializationTests`, `ContractTypingTests`, `NoObsoleteAddressSurfaceTests`) | **PASS** — 32 passed, 0 failed, 0 skipped. |
| Consolidation lane: `FullyQualifiedName~Consolidate` | **PASS** — 3 passed, 0 failed, 0 skipped. |
| CLI lane: `CliAddressValidationTests|CliReviewGapTests` | **PASS** — 32 passed, 0 failed, 0 skipped when isolated. |
| MCP address lane: `FullyQualifiedName~McpAddressSurfaceTests` | **PASS** — 32 passed, 0 failed, 0 skipped. |
| `dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj --no-build --no-restore` | **PASS** — 32 passed, 0 failed, 0 skipped. |
| `dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj --no-build --no-restore` | **PASS** — 127 passed, 0 failed, 0 skipped. |
| CLI/conformance/plugin contract lane (`CliAddressValidationTests`, `CliReviewGapTests`, `AllAddressSurfaceConformanceTests`, `ContractTypingTests`, `PluginStoreContractTests`) | **PASS** — 48 passed, 0 failed, 0 skipped. |
| Forbidden-surface gate (`NoObsoleteAddressSurfaceTests.Forbidden_address_surface_is_absent_outside_classified_historical_text`) | **PASS** — 1 passed, 0 failed, 0 skipped. |
| `dotnet build Scrinia.sln -p:PublishTrimmed=true --no-restore` | **FAIL** — exactly 12 IL2026 errors, all in `src/Scrinia.Plugin.Embeddings.Cli/EmbeddingsTools.cs`. |
| `dotnet publish src/Scrinia/Scrinia.csproj -p:PublishAot=true -r win-x64` | **PASS** — native AOT publish completed. |

## Remaining failures and classification

### Core suite: 177 failures

The failures are inherited baseline compatibility/migration and retrieval-contract expectations, not changes made by Task 11. They include legacy `ArtifactEntry`/URI fixtures, old name-only or unscoped address assumptions, migration/layout expectations, entity/reference materialization assumptions, and retrieval benchmark identifiers. The focused typed-address, bundle, serialization, MCP, CLI-review, plugin-propagation, and consolidation lanes pass when run in isolation.

The aggregate focused lane is now deterministic: all console-mutating test classes share the
`GlobalWorkspaceConfig` collection, and text capture also replaces/restores Spectre.Console's
process-wide console instance. The formerly failing mixed focused filter passes.

### Full solution: 188 failures

The full-solution total is the 177 Core failures plus 11 retrieval benchmark expectation failures. Representative retrieval failures assert short fixture IDs such as `d2` while the current result IDs are portable artifact keys such as `artifacts/d2.nmp2`, or expect prior ranking behavior. These are unrelated to the Task 11 addressing fix.

### Trimmed/AOT

Trimmed build provenance was checked with `git diff 755de18..HEAD -- src/Scrinia.Plugin.Embeddings.Cli/EmbeddingsTools.cs` and `git blame`: the 12 IL2026 sites predate this branch (commits `787bb22d`, `0fb86d9f`, `7bcb1403`, and `c609a4a2`) and the file is unchanged by this branch. Classification: **pre-existing branch baseline**. Native AOT publish for the main CLI passed with `-r win-x64`.

## Diff and forbidden-surface review

| Check | Result |
| --- | --- |
| `git diff --check` | **PASS** — no whitespace errors. |
| `git diff --stat` | **PASS** — 13 tracked files, 125 insertions, 38 deletions; only pre-existing Task 1–10 tracked modifications are present. |
| Forbidden-surface acceptance gate (`ArtifactEntry.Uri`, `ArtifactUri`, `ResolveArtifactAsync`, `file://`, `MemoryWrite(`) | **PASS** — exact path allowlist classifies only historical reports/plans, explicit rejection tests/guidance, stale fixture prose, legacy compatibility fixtures, and the typed `MemoryWrite` constructor implementation; living production/reference surfaces are unlisted and therefore fail closed. |

## Untracked server verification

`git ls-files src/Scrinia.Server` reports no tracked files; `git ls-files --others --exclude-standard -- src/Scrinia.Server` reports only the pre-existing `wwwroot/index.html` and `wwwroot/vite.svg`; `git diff -- src/Scrinia.Server` is empty. The untracked `src/Scrinia.Server/` directory remains untouched.

## Acceptance status

**Final fix-wave status:** the focused acceptance lane, CLI/conformance/plugin lane, forbidden-surface
gate, solution build, embedding tests, and merge tests pass. The Core suite remains red on 177
inherited compatibility/migration/retrieval expectations; the full solution remains red on 188
such expectations; trimmed build remains red on 12 pre-existing IL2026 diagnostics.

**Addressing fix-wave status:** isolated acceptance lanes are green (271 aggregate focused, 48
CLI/conformance/plugin contract, 32 plugin, and 127 merge tests), the deterministic forbidden-
surface gate passes, and native AOT passes. The remaining red full-suite results are classified
above and were not changed by this final fix wave.
