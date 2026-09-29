---
status: not-started
phase: 1
updated: 2026-09-09
---

# Phase A Completion Remediation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore the complete solution to a deterministic, fully green Phase A state relative to the last green baseline, with every recorded failure fixed and no test-host hang.

**Architecture:** Treat `90827cd` as the last known-good contract, `8d18c8c` as a separately non-green clean baseline, and the current uncommitted Phase A changes as a third delta. Remediate each failure at its owning boundary: append recovery and result mapping in Core/MCP/CLI, compact behavior at its existing lifecycle owner, package/release/retirement contracts in their production artifacts, shared-envelope output in the shared DTO renderer, global-tool/model relocation at `WorkspaceSetup` and test isolation, and test-host stability in the test/build harness. Do not weaken, skip, reclassify, or rebaseline failures.

**Tech Stack:** C#/.NET 10 preview SDK, xUnit, FluentAssertions, source-generated `System.Text.Json`, YamlDotNet, ConsoleAppFramework, filesystem-backed `FileMemoryStore`, NuGet global-tool packaging, PowerShell/Bash release scripts, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-08-append-hardening-design.md` and `docs/superpowers/plans/2026-09-08-append-hardening.md`

## Global Constraints

- Phase A acceptance is the entire default solution suite green: `dotnet test Scrinia.sln` must complete with zero failures and no hang.
- The last recorded green baseline is commit `90827cd`: 2,075 passed, 0 failed, 20 skipped.
- Clean commit `8d18c8c` is not green: its 14 deterministic regressions are in scope and must be remediated before Phase B.
- The current uncommitted tree is diagnosed at 2,848 passed, 28 failed, 20 skipped relative to the green baseline; the 28 failures are all in scope.
- Do not convert a failure to a skip, broaden an allowlist, weaken a contract assertion, update a golden without proving the intended contract, or call a failure contamination to remove it from acceptance.
- Preserve the current uncommitted work carefully. Do not reset, checkout, stash destructively, amend, or overwrite user/agent changes; use path-scoped diffs and `git show`/temporary copies for comparison.
- Each remediation task requires a fresh implementer, a focused red test, a minimal fix, a focused green run, mutation/falsification where practical, and an independent reviewer before the next task.
- Do not create commits if repository or execution policy prohibits them. If commits are permitted, stage only the task's intended files and record the commit; never stage unrelated work.
- Preserve intentional merge retirement. Do not restore `src/Scrinia.Merge/**`, `tests/Scrinia.Merge.Tests/**`, or `src/Scrinia.Mcp/skills/merge-safety.md` merely because they were deleted; restore a deleted tracked file only after proving it is a required current contract or test dependency.
- Historical planning/review artifacts remain byte-identical; fix living source/docs and test expectations only when the current contract requires it.
- No Phase B redesign: do not generalize append journaling to remember/compact/import/migration, add a workspace-wide transaction gate, restore a retired merge surface, or add a server/remote surface.

## Goal
Bring all Core, MCP, CLI, packaging, shared-envelope, onboarding, model-relocation, merge-retirement, and test-host contracts back to green, then prove the whole solution completes without a hang.

## Context & Decisions
| Decision | Rationale | Source |
|---|---|---|
| Use `90827cd` as the behavioral baseline and count all 28 current regressions | It is the last recorded 2,075-pass/0-fail baseline; `8d18c8c` is explicitly non-green | `.superpowers/sdd/append-hardening/context.md`; user-verified baseline |
| Keep the 14 post-`90827cd` regressions separate from the 14 current Phase A regressions | Separate ownership and dependency order prevents either set being hidden by the other | `.superpowers/sdd/append-hardening/remaining-full-suite-failures.md`; user-verified diagnostic |
| Retained invalid restore journals block every persistent route in the affected scope | The approved append plan requires durable invalid evidence to remain blocking, not silently bypassed | `docs/superpowers/plans/2026-09-08-append-hardening.md:17-24,299-311`; `tests/Scrinia.Tests/{Cli,Mcp}RecoveryBlockedRouteTests.cs` |
| CLI and MCP must be fixed as separate adapters | CLI calls internal `MemoryTools.Core` methods directly and does not pass through the MCP facade | Scrinia memory pattern `/patterns/cli-bypasses-mcp-facade`; `src/Scrinia/Commands/ScriniaCommands.Memory.cs` |
| Restore required deleted contract files explicitly, not under a contamination label | `NoObsoleteAddressSurfaceTests` currently fails because `failure-classification.md` is absent; the file must be checked against `90827cd` and restored if it is the accepted classification source | `.superpowers/sdd/append-hardening/fresh-full-suite-evidence.txt:411-419`; `tests/Scrinia.Tests/NoObsoleteAddressSurfaceTests.cs:87-103` |
| Keep `MonitoringInProcessConfig.cs` | BenchmarkDotNet out-of-process project discovery can collide with generated worktrees, and monitoring is load-bearing for on-disk benchmark iterations | Scrinia memory pattern `/patterns/agent-worktrees-recur-keep-the-inprocess-config` |
| Use a serialized/isolated final verification in addition to normal verification | Parallel MSBuild currently produced child-node premature exits and the normal run exceeded the timeout; this is a diagnostic finding, not a waiver | Fresh command run on 2026-09-09; `MSB4166` output |

## Phase 1: Inventory and Safety [IN PROGRESS]
- [ ] **1.1 Map all baseline and working-tree files and establish the exact 28-test ledger** ← CURRENT
- [ ] 1.2 Verify deleted tracked files against `90827cd` and restore required contract inputs
- [x] 1.3 Define task interfaces, dependency order, and per-task review/evidence records

## Phase 2: Append Route Recovery [PENDING]
- [ ] 2.1 Repair retained-invalid-journal behavior in MCP persistent routes
- [ ] 2.2 Repair retained-invalid-journal behavior in CLI persistent routes

## Phase 3: Existing Lifecycle Contracts [PENDING]
- [ ] 3.1 Restore compact no-op warning, exit code, notification, and golden contract
- [ ] 3.2 Restore generic MCP no-result/no-op warning status

## Phase 4: Published Surface and Shared Output [PENDING]
- [ ] 4.1 Repair release manifest/workflow and retired merge packaging guards
- [ ] 4.2 Restore onboarding and obsolete-address scan contracts
- [ ] 4.3 Repair the three shared-envelope golden contracts

## Phase 5: Package and Global Tool [PENDING]
- [ ] 5.1 Remediate all eleven package-content tests
- [ ] 5.2 Remediate the global-tool lifecycle test
- [ ] 5.3 Remediate and repeatedly verify model relocation without classifying setup failures away

## Phase 6: Hang Diagnosis and Acceptance [PENDING]
- [ ] 6.1 Diagnose and isolate the test-host/MSBuild hang deterministically
- [ ] 6.2 Run focused suites, full serialized suite, normal full suite, and final source/worktree review
- [ ] 6.3 Record Phase A completion evidence and explicitly block Phase B until the gate passes

---

## 1.1 Complete Inventory and File Map

**Files to inspect, not modify in this task:**
- Baselines: `90827cd`, `8d18c8c`, current `HEAD`, `git status --short`, `git diff 90827cd`.
- Approved append artifacts: `docs/superpowers/specs/2026-09-08-append-hardening-design.md`, `docs/superpowers/plans/2026-09-08-append-hardening.md`, `.superpowers/sdd/append-hardening/ledger.md`, `.superpowers/sdd/append-hardening/remaining-full-suite-failures.md`, `.superpowers/sdd/append-hardening/fresh-full-suite-evidence.txt`.
- Core append ownership: `src/Scrinia.Core/FileMemoryStore.cs`, `src/Scrinia.Core/AppendContracts.cs`, `src/Scrinia.Core/AppendJournal.cs`, `src/Scrinia.Core/AppendTransaction.cs`, `src/Scrinia.Core/IMemoryStore.cs`, `src/Scrinia.Core/DurableFile.cs`.
- MCP/CLI ownership: `src/Scrinia.Mcp/MemoryTools.Core.cs`, `src/Scrinia.Mcp/MemoryTools.cs`, `src/Scrinia.Mcp/McpResponse.cs`, `src/Scrinia/Commands/ScriniaCommands.Memory.cs`, `src/Scrinia/Commands/ScriniaCommands.cs`, `src/Scrinia/Commands/CliJsonContext.cs`.
- Compact ownership: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`, `src/Scrinia.Mcp/McpResponse.cs`, `src/Scrinia/Commands/ScriniaCommands.Memory.cs`, `tests/Scrinia.Tests/Golden/__snapshots__/compact.yaml`.
- Packaging/release ownership: `src/Scrinia/Scrinia.csproj`, `src/Scrinia.Mcp/Scrinia.Mcp.csproj`, `src/Scrinia/package-readme.md`, `publish.projects.tsv`, `publish.ps1`, `publish.sh`, `.github/workflows/release.yml`.
- Retirement/onboarding ownership: `docs/getting-started.md`, `docs/cli-reference.md`, `docs/troubleshooting.md`, `docs/multi-user-setup.md`, `src/Scrinia.Mcp/prompts/guide.md`, `src/Scrinia.Mcp/skills/merge-safety.md`, `src/Scrinia/Commands/ScriniaCommands.Setup.cs`, `src/Scrinia/Commands/ScriniaCommands.Migration.cs`.
- Shared output ownership: `src/Scrinia.Mcp/Shared/SharedDtoContracts.cs`, `src/Scrinia.Mcp/Shared/SharedServer.cs`, `src/Scrinia.Mcp/Shared/SharedGitGuidance.cs`, applicable source-generated JSON context, `tests/Scrinia.Tests/Shared/SharedEnvelopeGoldensTests.cs`.
- Global/model ownership: `src/Scrinia/Commands/WorkspaceSetup.cs`, `src/Scrinia.Core/Configuration/UserHome.cs`, `src/Scrinia.Plugin.Embeddings/VulkanModelManager.cs`, `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs`, `tests/Scrinia.Tests/ModelRelocationTests.cs`, `tests/Scrinia.Tests/GlobalToolInstallTests.cs`, `tests/Scrinia.Tests/PackageContentTests.cs`.
- Test infrastructure: `Scrinia.sln`, project files, `tests/Scrinia.Tests/TestHelpers.cs`, `tests/Scrinia.Benchmarks/MonitoringInProcessConfig.cs`, `tests/Scrinia.Tests/GlobalUsings.cs`, `Directory.Build.props`.

**Interfaces and invariants to preserve:**
- `IMemoryStore.AppendAsync(MemoryWrite, CancellationToken) -> Task<AppendStoreResult>` remains the append owner; MCP/CLI only map results.
- `AppendStoreException` exposes only `RecoveryRequired` and `Internal`; retained malformed/zero-length/unsupported/hash-inconsistent journals remain blocking evidence.
- `McpResponse` warning status is `warning` for ordinary no-op/no-result operations and `success_with_warning` only for append post-commit warnings.
- CLI `EmitMcpResult` returns nonzero for warning/error cases whose established CLI contract requires failure, including compact no-op.
- `WorkspaceSetup` resolves `SCRINIA_HOME` through `UserHome.Resolve`; global-tool package runtime data stays outside the immutable package.
- Retired merge names must be absent from active packaging and living docs, while historical files remain byte-identical.
- Shared envelope YAML must preserve field order, null/empty sequence semantics, scalar quoting, and no duplicate keys.

**Inventory command and expected result:**
```text
git status --short
git diff --name-status 90827cd
git ls-tree -r --name-only 90827cd
dotnet test Scrinia.sln --no-restore -m:1 --logger "console;verbosity=minimal"
```
Expected: a checked-in ledger containing the exact 28 names below, their first failing message, owning production/test files, and whether the failure is deterministic, environment-sensitive, or host-instability-related. The diagnostic must not remove a test from the ledger because a later run happens to pass.

### Exact Failure Ledger

#### Fourteen post-`90827cd` regressions

1. `CliReviewGapTests.CompactWithoutEnoughChunks_ExitsOne` — CLI maps the compact no-op as exit 0 instead of the established warning/failure contract. Evidence: `tests/Scrinia.Tests/CliReviewGapTests.cs:177-187`; owner: `src/Scrinia/Commands/ScriniaCommands.Memory.cs`, `src/Scrinia/Commands/ScriniaCommands.cs`; TDD: assert red exit/status, fix `EmitMcpResult`/compact mapping without changing mutating compact.
2. `CompactNotificationTests.Compact_AlreadySingleChunk_DoesNotPublishAnEvent` — no-op message changed from the pinned wording, so the behavior assertion cannot establish the no-event contract. Evidence: `tests/Scrinia.Tests/CompactNotificationTests.cs:43-59`; owner: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`; TDD: pin exact no-op text and empty sink, then restore implementation wording.
3. `Golden.ResponseGoldenTests.Response_MatchesGolden(name: "compact")` — compact no-op response status, mutation info, and wording drift from `compact.yaml`. Evidence: `tests/Scrinia.Tests/Golden/__snapshots__/compact.yaml`, fresh evidence lines 392-409; owner: lifecycle response builder and golden normalizer; TDD: prove intended contract first, then make output byte-identical without using `SCRINIA_UPDATE_GOLDEN` as a substitute for diagnosis.
4. `ScriniaMcpToolsTests.NoResultAndNoOpOutcomes_AreWarningsWithoutErrorCodes` — generic no-result/no-op response currently reports `success` rather than `warning`. Evidence: fresh evidence lines 36-49; owner: `src/Scrinia.Mcp/McpResponse.cs` and no-op call sites; TDD: test all no-op cases, preserve no error code, and isolate append `success_with_warning`.
5. `PublishManifestTests.ReleaseWorkflow_UsesExactManifestPublisherInvocationsForEverySupportedTarget` — workflow contains direct `dotnet publish` commands outside the manifest-driven scripts. Evidence: `tests/Scrinia.Tests/PublishManifestTests.cs:816-869`, fresh evidence lines 18-33; owner: `.github/workflows/release.yml`, `publish.ps1`, `publish.sh`, `publish.projects.tsv`; TDD: make direct-publish detector red, route every target through approved manifest invocations, preserve win/linux/osx matrix.
6. `PackagingAndDocsMergeRetirementTests.PackagingManifestAndReleaseWorkflow_ContainNoMergeSurface` — release workflow still contains `Scrinia.Merge`/merge publication text. Evidence: `tests/Scrinia.Tests/PackagingAndDocsMergeRetirementTests.cs:25-40`, fresh evidence lines 58-236; owner: `.github/workflows/release.yml`, `publish.*`, `publish.projects.tsv`; TDD: assert absence and verify release trees still contain the supported CLI/plugin outputs.
7. `PackagingAndDocsMergeRetirementTests.Phase4MultiUserDocumentation_RemainsByteIdentical` — `docs/multi-user-setup.md` differs from its historical hash. Evidence: test lines 82-89 and fresh evidence lines 239-249; owner: deleted/modified documentation and any accidental editor rewrite; TDD: compare exact bytes to the recorded SHA-256, restore the approved historical bytes, and do not rewrite the document to satisfy new prose.
8. `PackagingAndDocsMergeRetirementTests.LivingDocumentation_ContainsNoRetiredActiveSurface` — active CLI/getting-started guidance retains retired reconcile, multi-user, or merge-driver vocabulary. Evidence: `tests/Scrinia.Tests/PackagingAndDocsMergeRetirementTests.cs:42-58`; owner: `docs/cli-reference.md`, `docs/getting-started.md`, active merge-safety surface; TDD: scan only living docs, remove retired active guidance, and preserve historical planning artifacts.
9. `PackagingAndDocsMergeRetirementTests.HistoricalPlanningAndReleaseFiles_RemainByteIdentical` — historical plan/spec/review/CHANGELOG hash guard must be run independently even when the current fresh run did not expose it. Evidence: test lines 91-99; owner: the four listed files and file restoration process; TDD: add isolated hash verification before modifying any living docs.
10. `NoObsoleteAddressSurfaceTests.Forbidden_address_surface_is_absent_outside_classified_historical_text` — required `failure-classification.md` is missing, causing `FileNotFoundException` before the scan can classify accepted historical vocabulary. Evidence: `tests/Scrinia.Tests/NoObsoleteAddressSurfaceTests.cs:79-103`, fresh evidence lines 411-419; owner: repository tracked-file contract and classification source; TDD: restore the baseline-required file or update only the production classification source proven by the baseline, then ensure no unclassified active match remains.
11. `HookAdvisoryOnboardingTests.MultiUserSetup_IsSharedBaseCollaborationNotMergeWorkflow` — onboarding still advertises retired `setup --multi-user` and merge-driver workflow. Evidence: fresh evidence lines 441-525; owner: `src/Scrinia.Mcp/prompts/guide.md`, `docs/getting-started.md`, `docs/multi-user-setup.md`, setup/help text; TDD: assert the current shared-base commands and absence of retired wording while preserving historical docs.
12. `SharedEnvelopeGoldensTests.Serializes_populated_follow_up_and_page_in_exact_order` — shared success envelope field ordering/shape must remain byte-exact. Evidence: `tests/Scrinia.Tests/Shared/SharedEnvelopeGoldensTests.cs:10-47`; owner: `SharedEnvelope.ToYaml` and DTO serialization; TDD: run this test against the clean baseline/current implementation, identify the first byte difference, then correct renderer ordering rather than weaken the expected YAML.
13. `SharedEnvelopeGoldensTests.Serializes_every_payload_family_and_one_files_sequence` — shared payload-family and mutation envelope rendering drift. Evidence: test lines 50-135; owner: shared DTO contract and YAML renderer; TDD: cover search/facts/mutation/error families and duplicate-key rejection, then restore exact snapshots.
14. `SharedEnvelopeGoldensTests.Serializes_present_empty_files_as_an_empty_sequence` — present empty `files` must serialize as `files: []`, not disappear or become null. Evidence: test lines 148-170; owner: `SharedEnvelope` and `ListItem` serialization; TDD: preserve empty-sequence distinction and parse the result without duplicate keys.

The three shared-envelope golden failures are recorded as separate contract failures even if one renderer defect causes more than one to fail. `PackagingAndDocsMergeRetirementTests.Troubleshooting_ProvidesManualGuidanceOnly` and `SharedEnvelopeGoldensTests.Preserves_nullable_timestamps_and_digests` remain supporting coverage and must also pass, but are not counted among the fourteen user-identified post-baseline regressions.

#### Fourteen current Phase A regressions

1. `CliRecoveryBlockedRouteTests.PersistentCliRoutes_ReturnRecoveryBlocked_WhenTheirScopeHasARetainedInvalidJournal` — CLI direct internal calls bypass the retained restore-journal recovery gate and return exit 0. Evidence: test lines 15-43 and fresh evidence lines 280-300; owner: `src/Scrinia/Commands/ScriniaCommands.Memory.cs`, `src/Scrinia/Commands/ScriniaCommands.cs`, Core recovery boundary; TDD: assert all seven routes produce `RECOVERY_BLOCKED`, exit 1, journal path, and no mutation.
2. `McpRecoveryBlockedRouteTests.PersistentMcpRoutes_ReturnRecoveryBlocked_WhenTheirScopeHasARetainedInvalidJournal` — MCP route mapping does not preserve the recovery-blocked envelope for every persistent operation. Evidence: test lines 14-43 and fresh evidence lines 260-270; owner: `src/Scrinia.Mcp/MemoryTools.Core.cs`, lifecycle/facts/bundling route gates; TDD: table-drive all nine persistent routes and retain ephemeral availability.
3. `GlobalToolInstallTests.IsolatedGlobalToolLifecycle_PassesEveryStage` — installed package lifecycle, server handshake, immutable payload, isolated home, update, or uninstall fails as one global integration contract. Evidence: `tests/Scrinia.Tests/GlobalToolInstallTests.cs:125-208`; owner: `src/Scrinia/Scrinia.csproj`, `WorkspaceSetup`, package runtime paths, process/handshake helpers; TDD: run isolated lifecycle alone and split diagnostics by stage without weakening the single end-to-end assertion.
4-14. `PackageContentTests.Package_DeclaresScriToolId_AndDotnetToolType`, `PackageContentTests.Package_PutsScriCommandUnderTools_NotLib`, `PackageContentTests.Package_ContainsManagedDependencies`, `PackageContentTests.Package_IncludesEmbeddedPromptsSkillsSchemasAndSetupTemplates`, `PackageContentTests.Package_IncludesPackageReadme_AtRoot`, `PackageContentTests.Package_ExcludesMergeAndScriMerge`, `PackageContentTests.Package_ExcludesVulkanAndLLamaSharp`, `PackageContentTests.Package_ExcludesNativeRidAssets`, `PackageContentTests.Package_ExcludesDownloadedModels`, `PackageContentTests.Package_ExcludesRepositoryAndWorkspaceData`, and `PackageContentTests.Package_ExcludesMutableCaches` — package contents do not match the immutable `scrinia` DotnetTool allowlist/denylist. Evidence: `tests/Scrinia.Tests/PackageContentTests.cs:42-165`; owner: `src/Scrinia/Scrinia.csproj`, `src/Scrinia.Mcp/Scrinia.Mcp.csproj`, embedded-resource declarations, package readme, publish/package properties; TDD: pack to a fresh temp directory, inspect nuspec/zip/resources, fix package metadata/content, and verify all eleven assertions independently.

**Model-relocation evidence note:** The user diagnostic reports model-relocation UnauthorizedAccess failures were not reproducible and 10/10 currently pass. The ten names from the earlier inventory remain a mandatory stability investigation, not failures to delete:
`ModelRelocationTests.MigrateLegacyAssets_MovesExecutableRelativePluginsIntoVersionRidHierarchy`, `ModelRelocationTests.MigrateLegacyAssets_MoveFailure_RemainsNonFatal`, `ModelRelocationTests.MigrateLegacyAssets_MovesExecutableRelativeModelsIntoHome`, `ModelRelocationTests.MigrateLegacyAssets_RejectsTraversalInPluginVersion`, `ModelRelocationTests.ResolveEmbeddingsPluginExe_UsesMandatoryHierarchyUnderScriniaHome`, `ModelRelocationTests.Setup_ResolvesModel2VecUnderScriniaHome_NeverAppBaseDirectory`, `ModelRelocationTests.MigrateLegacyAssets_NeverWritesLegacyPackageDirectory`, `ModelRelocationTests.MigrateLegacyAssets_SecondRun_IsIdempotentNoOp`, `ModelRelocationTests.UserHome_ResolvesIsolatedHome_ExactAbsoluteOverride`, and `ModelRelocationTests.MigrateLegacyAssets_PreservesExistingDestinationFiles`. They must pass repeatedly in isolation and in the full suite; any new instability is investigated rather than skipped.

## 1.2 Deleted Tracked File Contract

**Files:**
- Inspect every `D` entry from `git status --short`, especially `failure-classification.md`, deleted task/report files, retired merge implementation/test files, and deleted snapshots.
- Restore only proven required files with `git show 90827cd:<path>` and `apply_patch`; do not reconstruct historical contents from memory.
- Test: `tests/Scrinia.Tests/NoObsoleteAddressSurfaceTests.cs`, packaging/history guards, solution build.

**Steps:**
- [ ] Run `git diff --name-status 90827cd` and `git ls-tree -r --name-only 90827cd`, then classify each deletion by current build/test dependency, intentional retirement contract, or historical record.
- [ ] Run the obsolete-surface test alone before restoration; expected: the current `FileNotFoundException` identifies `failure-classification.md`.
- [ ] Restore `failure-classification.md` byte-for-byte from `90827cd` if its contents are the accepted historical classification source; verify the file is tracked and the test reaches its actual scan assertions.
- [ ] Do not restore merge-retirement deletions unless a failing current build/test explicitly requires them; instead make the active no-merge guards pass.
- [ ] Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-restore --filter "FullyQualifiedName~NoObsoleteAddressSurfaceTests|FullyQualifiedName~PackagingAndDocsMergeRetirementTests"`; expected: no missing-file exception and only genuine contract mismatches remain.
- [ ] Have a fresh reviewer compare every restored file to `git show 90827cd` and confirm no deleted file was silently dismissed as unrelated.

## 1.3 Task Interface and Dependency Rules

Each task below must publish a short evidence record containing: exact failing test(s) and current pass state, root-cause proof, files changed, red command/output, green command/output, mutation/falsification result, reviewer result, and remaining failure count. For a task not yet executed, every field must be marked **pending**; no result may be inferred from the plan or invented. Dependencies are strict: inventory and restoration precede all fixes; after 1.2, tasks 4.1, 4.2, and 4.3 branch directly from 1.2 and may proceed in parallel, with no 3.2 prerequisite or 4.x-to-4.x edge; each later task depends on the interfaces required by its own task; append route fixes precede final full-suite runs; packaging and docs fixes must not alter historical hashes; model relocation and hang diagnosis must run after package changes so package tests observe final artifacts.

## 2.1 MCP Retained-Journal Recovery

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs`, `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`, `src/Scrinia.Mcp/MemoryTools.Bundling.cs` only where persistent route preflight is owned.
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` only if the shared recovery-boundary contract is missing; do not redesign the append journal.
- Test: `tests/Scrinia.Tests/McpRecoveryBlockedRouteTests.cs`, relevant response assertions.

**Interfaces:** Consume `AppendStoreException(RecoveryRequired, operationId, ...)` and restore-journal blocked results; produce a consistent `McpResponse` with `errorCode: RECOVERY_BLOCKED`, affected scope, journal path, and action-needed guidance. Ephemeral routes remain available when only a persistent scope is blocked.

**Steps:**
- [ ] Run the exact test alone and capture the first route that returns a missing `RECOVERY_BLOCKED` code.
- [ ] Add/strengthen a table-driven red assertion for remember, recall, search, list, append, forget, compact, link, and facts; assert journal bytes remain unchanged.
- [ ] Trace every persistent MCP route from its public facade to Core, not only existing `NameRefusal` or append call sites.
- [ ] Implement the smallest shared preflight/mapping correction; preserve the approved append-specific exception codes and do not block unrelated persistent scopes or ephemeral operations.
- [ ] Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-restore --filter "FullyQualifiedName~McpRecoveryBlockedRouteTests"`; expected: all tests pass.
- [ ] Mutate/remove the recovery gate temporarily and rerun the focused test; expected: red, then restore and rerun green.
- [ ] Fresh reviewer checks route enumeration, no-mutation behavior, journal retention, and no accidental workspace-wide gate.

## 2.2 CLI Retained-Journal Recovery

**Files:**
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs`, `src/Scrinia/Commands/ScriniaCommands.cs`, `src/Scrinia/Commands/CliJsonContext.cs` only as required for structured error output.
- Test: `tests/Scrinia.Tests/CliRecoveryBlockedRouteTests.cs`.

**Interfaces:** The CLI calls internal Core/MCP implementations directly, so it must explicitly map the same blocked result; produce exit `1`, JSON `RECOVERY_BLOCKED`, scope/journal details, and no mutation for search/store/show/forget/append/compact/link. `List`, `Facts`, and `Export` must preserve the MCP recovery envelope as their existing test requires.

**Steps:**
- [ ] Run the exact CLI route test alone and capture the first route returning exit 0.
- [ ] Add a red assertion that each route uses the direct CLI path and still receives the blocked result; do not route through a public MCP attribute facade as a workaround.
- [ ] Fix CLI preflight/result mapping at the direct-call boundary and register any changed DTO in `CliJsonContext`.
- [ ] Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-restore --filter "FullyQualifiedName~CliRecoveryBlockedRouteTests"`; expected: all route and evidence-retention assertions pass.
- [ ] Fresh reviewer verifies CLI/MCP parity and checks every address-bearing CLI command, not only `append`.

## 3.1 Compact No-Op Contract

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`, `src/Scrinia.Mcp/McpResponse.cs`, `src/Scrinia/Commands/ScriniaCommands.Memory.cs`, `src/Scrinia/Commands/ScriniaCommands.cs` only as necessary.
- Modify: `tests/Scrinia.Tests/Golden/__snapshots__/compact.yaml` only after the intended output is restored and reviewed.
- Test: `tests/Scrinia.Tests/CliReviewGapTests.cs`, `tests/Scrinia.Tests/CompactNotificationTests.cs`, `tests/Scrinia.Tests/Golden/ResponseGoldenTests.cs`.

**Interfaces:** A compact request with one chunk is a no-op: no event, warning status, exact established content `Already a single chunk, nothing to compact.`, `mutation: none`, and CLI exit 1. A multi-chunk compact remains success, publishes one complete notification, and exits 0.

**Steps:**
- [ ] Run the three exact focused tests and save the current response/status/message/event mismatch.
- [ ] Write a red table-driven test covering MCP no-op, CLI JSON no-op, golden bytes, no event, and multi-chunk control behavior.
- [ ] Fix lifecycle result construction and CLI status-to-exit mapping without changing compact mutation sequencing or unrelated no-result operations.
- [ ] Update the compact snapshot only if the output now matches the approved contract; never use a golden update to conceal a status or wording regression.
- [ ] Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-restore --filter "FullyQualifiedName~CliReviewGapTests|FullyQualifiedName~CompactNotificationTests|FullyQualifiedName~Golden.ResponseGoldenTests"`; expected: all compact tests pass.
- [ ] Fresh reviewer confirms no-op has no event or mutation and ordinary append `success_with_warning` remains distinct.

## 3.2 Generic MCP Warning Mapping

**Files:**
- Modify: `src/Scrinia.Mcp/McpResponse.cs` and the no-result/no-op builders in `src/Scrinia.Mcp/MemoryTools*.cs`.
- Test: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs`, `tests/Scrinia.Tests/ResponseStatusIsolationTests.cs`.

**Steps:**
- [ ] Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-restore --filter "FullyQualifiedName~NoResultAndNoOpOutcomes_AreWarningsWithoutErrorCodes"`; expected: red status mismatch.
- [ ] Identify all generic no-result/no-op constructors and assert they produce `status: warning` with no error code while append post-commit warnings remain `success_with_warning`.
- [ ] Implement the minimal builder/call-site correction and preserve response field order/source-generated serialization.
- [ ] Run the focused response/status tests; expected: pass with no generic invocation error.
- [ ] Fresh reviewer enumerates every warning/error/success status producer before approving.

## 4.1 Release, Package Retirement, and History Guards

**Files:**
- Modify: `.github/workflows/release.yml`, `publish.projects.tsv`, `publish.ps1`, `publish.sh`.
- Modify living docs only as needed: `docs/getting-started.md`, `docs/cli-reference.md`, `docs/troubleshooting.md`.
- Preserve byte-identical: `docs/multi-user-setup.md`, `docs/superpowers/plans/2026-09-01-merge-retirement.md`, `docs/superpowers/specs/2026-08-31-shared-memory-global-tool-design.md`, `docs/superpowers/reviews/2026-08-31-shared-memory-global-tool-spec-review.md`, `CHANGELOG.md`.
- Test: `tests/Scrinia.Tests/PublishManifestTests.cs`, `tests/Scrinia.Tests/PackagingAndDocsMergeRetirementTests.cs`, `tests/Scrinia.Tests/OnboardingOwnershipTests.cs`.

**Interfaces:** Release workflow invokes only approved manifest publishers per RID; no `Scrinia.Merge`, `scri-merge`, or merge-driver release step remains. Retired active docs describe shared-base collaboration/manual cleanup only. Historical files retain recorded bytes.

**Steps:**
- [ ] Run each release/history test independently and record exact offending line/hash.
- [ ] Make red source scans explicit for direct publish, merge package names, merge-driver workflow text, and historical hashes.
- [ ] Remove direct workflow publishing in favor of the existing manifest scripts, remove retired merge publication, and correct only living docs/help text.
- [ ] Restore historical bytes from baseline where they drifted; do not edit historical planning/review artifacts to remove vocabulary.
- [ ] Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-restore --filter "FullyQualifiedName~PublishManifestTests|FullyQualifiedName~PackagingAndDocsMergeRetirementTests|FullyQualifiedName~OnboardingOwnershipTests"`; expected: all pass.
- [ ] Fresh reviewer checks the three supported RIDs, both script paths, plugin expansion, release archives, and exact historical hashes.

## 4.2 Onboarding and Obsolete Address Surface

**Files:**
- Modify: `src/Scrinia.Mcp/prompts/guide.md`, `docs/getting-started.md`, `docs/cli-reference.md`, `docs/plugin-authoring.md`, `docs/security.md`, and active source descriptions only where scans identify unsupported vocabulary.
- Restore if required by 1.2: `failure-classification.md`.
- Test: `tests/Scrinia.Tests/HookAdvisoryOnboardingTests.cs`, `tests/Scrinia.Tests/NoObsoleteAddressSurfaceTests.cs`.

**Steps:**
- [ ] Run both tests and the repository scan; record every unclassified path/token rather than weakening the regex or allowlist.
- [ ] Correct active onboarding to current shared-base commands and canonical address guidance; retain explicit negative examples where they are required.
- [ ] Re-run the scan over tracked text files and classify only legitimate historical/rejection/fixture text by exact path.
- [ ] Run focused tests and `git diff --check`; expected: pass with no unclassified active surface.
- [ ] Fresh reviewer verifies all four address-facing surfaces and confirms no historical artifact was rewritten.

## 4.3 Shared Envelope Goldens

**Files:**
- Modify: `src/Scrinia.Mcp/Shared/SharedDtoContracts.cs`, `src/Scrinia.Mcp/Shared/SharedGitGuidance.cs`, `src/Scrinia.Mcp/Shared/SharedServer.cs`, and the owning source-generated context only if serialization metadata is incomplete.
- Test: `tests/Scrinia.Tests/Shared/SharedEnvelopeGoldensTests.cs`, related shared schema/privacy/no-Git/prompt-isolation tests.

**Interfaces:** Preserve `SharedEnvelope` field order and wire names; use explicit null versus empty sequence values; quote dynamic scalars safely; never duplicate YAML keys; keep shared handlers prompt-independent and Git-process-free.

**Steps:**
- [ ] Run the three named golden tests individually and capture exact first diffs.
- [ ] Add a red serializer matrix for populated follow-up/page, every payload family, nullable timestamps/digests, and empty files.
- [ ] Fix DTO property order/omission/quoting at the single renderer boundary, not by weakening each expected string.
- [ ] Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-restore --filter "FullyQualifiedName~SharedEnvelopeGoldensTests|FullyQualifiedName~SharedSchemaTests|FullyQualifiedName~SharedPrivacyTests|FullyQualifiedName~SharedNoGitProcessTests|FullyQualifiedName~SharedNoPromptCorrelationTests"`; expected: pass.
- [ ] Fresh reviewer checks no Git process, prompt/correlation parameter, duplicate YAML key, or loss of null/empty distinction was introduced.

## 5.1 Package Content Allowlist/Denylist

**Files:**
- Modify: `src/Scrinia/Scrinia.csproj`, `src/Scrinia.Mcp/Scrinia.Mcp.csproj`, `src/Scrinia/package-readme.md`, embedded prompt/resource declarations, and package build properties only as required.
- Test: all eleven `PackageContentTests` listed in the failure ledger.

**Interfaces:** `dotnet pack src/Scrinia/Scrinia.csproj -c Release -o <temp>` produces exactly one `scrinia` `DotnetTool`; command assembly is under `tools/`; managed dependencies and offline resources/readme are present; merge/Vulkan/LLamaSharp/native/model/repository/cache assets are absent.

**Steps:**
- [ ] Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-restore --filter "FullyQualifiedName~PackageContentTests"` with a fresh temp pack output; record nuspec, zip entries, and extracted MCP resources.
- [ ] For each failing assertion, write or retain a focused red check against the exact package artifact, not the source tree.
- [ ] Correct package ID/type, output layout, project references/content inclusion, embedded resources, package readme, and denylist exclusions. Do not re-add retired merge or optional embedding payloads to make a dependency assertion pass.
- [ ] Pack twice to distinct temp directories and compare allowlist behavior; ensure source checkout and package payload remain immutable.
- [ ] Run the full package filter; expected: eleven passed, zero failed.
- [ ] Fresh reviewer inspects the actual `.nupkg` and verifies every allowlist/denylist item independently.

## 5.2 Global Tool Lifecycle

**Files:**
- Modify only the owning package/runtime files identified by the isolated diagnostic: `src/Scrinia/Scrinia.csproj`, `src/Scrinia/Commands/WorkspaceSetup.cs`, `src/Scrinia.Core/Configuration/UserHome.cs`, `src/Scrinia.Mcp` package resources, or `tests/Scrinia.Tests/GlobalToolInstallTests.cs` when its fixture is demonstrably invalid.
- Preserve: `tests/Scrinia.Benchmarks/MonitoringInProcessConfig.cs`.
- Test: `tests/Scrinia.Tests/GlobalToolInstallTests.cs`.

**Interfaces:** An isolated local feed can pack/install `scrinia` versions `0.7.0` and `0.8.0`, run `--version`, complete both `serve` handshakes, leave package bytes unchanged, resolve runtime assets under isolated `SCRINIA_HOME`, update, restart, and uninstall.

**Steps:**
- [ ] Run the lifecycle test alone with a unique temp feed/tool/home/workspace and stage markers around pack/install/version/serve/shared-serve/setup/update/restart/uninstall.
- [ ] If it fails, reproduce the exact stage with the smallest command and add a red assertion for that stage; never turn the whole test into a skip or extend timeouts without identifying the blocked process.
- [ ] Fix package/runtime ownership or process handshake disposal at the owning boundary; keep all runtime writes outside the package and preserve source-generated contracts.
- [ ] Run the lifecycle test three times sequentially and once with the package-content test; expected: four passes, no leaked process/temp state, immutable package tree.
- [ ] Fresh reviewer checks environment restoration, stdin/stdout/stderr draining, process exit, update semantics, and package immutability.

## 5.3 Model Relocation Stability

**Files:**
- Modify only if a reproducible failure identifies a defect: `src/Scrinia/Commands/WorkspaceSetup.cs`, `src/Scrinia.Core/Configuration/UserHome.cs`, `src/Scrinia.Plugin.Embeddings/VulkanModelManager.cs`, `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs`, `tests/Scrinia.Tests/ModelRelocationTests.cs`.
- Test: all ten model-relocation names recorded in the inventory.

**Steps:**
- [ ] Run the complete `ModelRelocationTests` filter alone 10 times with unique temp directories and no concurrent package/global tests; expected: 10/10 passes.
- [ ] Run the same filter under `-m:1` and normal MSBuild; record any difference as host/isolation evidence, not as a skip.
- [ ] Inspect the constructor/setup path at the reported `tests/Scrinia.Tests/bin/Debug/net10.0/models/...` location for stale directory/file collisions, concurrent package writes, or incorrect `SCRINIA_HOME` restoration.
- [ ] If source behavior fails, write the smallest red regression and fix mandatory hierarchy, traversal rejection, idempotence, nonfatal move behavior, or destination preservation. If only fixture state fails, make the fixture use unique isolated paths and prove the source contract remains tested.
- [ ] Fresh reviewer confirms `SCRINIA_HOME/models` and `SCRINIA_HOME/plugins` are the only runtime destinations and no legacy package directory is written.

## 6.1 Hang Diagnosis and Isolated Deterministic Verification

**Files:**
- Inspect/modify only the test/build harness proven responsible: `Scrinia.sln`, project files, `Directory.Build.props`, `tests/Scrinia.Tests/TestHelpers.cs`, process helpers, and test collection definitions.
- Do not remove `MonitoringInProcessConfig.cs` or reduce test coverage to avoid the hang.

**Steps:**
- [ ] Run the normal command with a hard external timeout and capture the complete log: `dotnet test Scrinia.sln --no-restore --logger "console;verbosity=minimal"`.
- [ ] Run serialized MSBuild: `dotnet test Scrinia.sln --no-restore -m:1 --logger "console;verbosity=minimal"`; expected: the test processes complete, or the exact remaining hang point is identified.
- [ ] Run projects independently in dependency order: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-restore`, `dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj --no-restore`, and `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --no-restore`; expected: each terminates with a result.
- [ ] Run suspicious filters independently: package/global, model relocation, shared, recovery, and append; expected: each terminates without process leakage.
- [ ] Inspect MSBuild failure diagnostics under the reported `MSBuildTemp...` directory and collect process trees/handles if a child node exits prematurely; distinguish MSBuild node failure, xUnit deadlock, child `scri serve` handshake, file lock, and test global-state race.
- [ ] Add a deterministic regression test or collection/isolation fix for the proven cause. Do not solve a hang by skipping tests, increasing an unbounded timeout, or serializing the entire solution without evidence.
- [ ] Repeat normal and `-m:1` solution runs three times; expected: each terminates and reports identical test totals and zero failures.
- [ ] Fresh reviewer verifies the diagnosis with an independent run and confirms the final command has no hang budget exception.

## 6.2 Final Verification Gate

**Commands and required results:**

```text
dotnet build Scrinia.sln
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj
dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj
dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj
dotnet test Scrinia.sln
```

Expected: every command terminates; build has zero errors; every default test project has zero failures; the final `dotnet test Scrinia.sln` has zero failures and retains only the repository-approved 20 skipped tests or fewer if an existing skip is removed by a real fix. The final solution run is the acceptance gate, not a focused suite, a serialized substitute, or a count extrapolation.

**Final review steps:**
- [ ] Re-run all 28 named tests and record PASS evidence for every row.
- [ ] Verify package-content eleven, global lifecycle one, and model-relocation ten-test stability separately.
- [ ] Run `git diff --check`, `git status --short`, `git diff --stat`, and targeted `git diff` for every changed file.
- [ ] Confirm no source/test change was made outside an owning task, no historical artifact was rewritten, no deleted required file remains absent, and no new generic MCP invocation error path exists.
- [ ] Have a fresh reviewer inspect the complete diff, failure ledger, test output, and hang evidence. Reviewer must explicitly state that no failure was scoped out or classified away.
- [ ] Write the completion evidence only after the normal `dotnet test Scrinia.sln` command has completed successfully with zero failures and no hang.

## 6.3 Phase B Blocker

Phase B is blocked until the final default solution command, `dotnet test Scrinia.sln`, completes with zero failures and no hang. A green append-focused suite, green serialized substitute, reproducible model tests, successful package lane, or an explanation that a failure is pre-existing is insufficient. Phase A is complete only when all 28 ledger rows are green, required tracked files are present, the package/global/model surfaces are stable, and the entire solution test suite passes end-to-end.

## Plan Self-Review

- **Coverage:** The plan inventories the approved append spec/plan, SDD ledger, current diagnosis, all 14 post-`90827cd` regressions, all 14 current Phase A regressions, the ten model-relocation stability names, deleted-file restoration, package contents, global lifecycle, shared envelopes, retirement/onboarding/docs, CLI/MCP recovery, compact/no-op status, and test-host hang.
- **No scope-widdling:** No failure is dismissed, skipped, removed from the ledger, or rebaselined. Model relocation is explicitly still in scope despite the current 10/10 result; the hang is explicitly a blocking defect.
- **No placeholders:** Every remediation task names files, interfaces, exact tests, commands, expected outcomes, and review evidence. No task says only to “write tests” or “handle edge cases.”
- **Type consistency:** Append tasks retain `Task<AppendStoreResult>`, `AppendStoreException`, `McpResponse`, and CLI `EmitMcpResult`; package tasks retain `DotnetTool`/`scrinia`; shared tasks retain `SharedEnvelope` and source-generated DTO serialization.
- **Dependency consistency:** Required-file inventory precedes route/lifecycle fixes; route/lifecycle fixes precede final gate; package/runtime fixes precede package lifecycle verification; hang diagnosis is performed in isolated and full modes before completion.
- **Historical safety:** The plan restores baseline-required tracked files byte-for-byte and preserves intentional merge-retirement deletions and historical documents rather than rewriting either category.
- **Acceptance:** The plan ends with the only acceptable Phase A result: normal `dotnet test Scrinia.sln` terminates with zero failures, followed by explicit Phase B blocking language.
