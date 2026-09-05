# Shared Server and MCP Surface Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Implement a separate `scri shared serve` MCP server with exactly eleven prefixed tools, persistent-only shared bases, fail-closed freshness, exact DTO/YAML contracts, and no dependency from `Scrinia.Mcp` on the executable `Scrinia` project.

**Architecture:** `Scrinia.Core` owns every shared configuration, registry, base identity, runtime contract, snapshot, fingerprint, classifier, and operation-result abstraction consumed by `Scrinia.Mcp`. `Scrinia.Mcp` references `Scrinia.Core` only and implements the shared MCP handlers and envelope serialization; it never references `Scrinia`. The executable supplies a CLI host adapter that resolves environment/workspace inputs, constructs Core implementations, registers them with the MCP host, and owns command/transport startup only. The existing local `scri serve` surface and local tool implementations remain unchanged.

**Tech Stack:** .NET 10 (`net10.0`), xUnit, `System.Text.Json` source generation, YAML response serialization already used by `McpResponseExtensions`, `DurableFile`, `PathContainment`, `dotnet build`, and `dotnet test`. No new dependencies.

**Spec:** `docs/superpowers/specs/2026-08-31-shared-memory-global-tool-design.md` §§3.1–3.4, 4.1–4.5, 5.1–5.3, 6.1–6.3, 7.1–7.3, 11, 13, 14. Review record: `docs/superpowers/reviews/2026-08-31-shared-memory-global-tool-spec-review.md` rulings R1-01, R2-01, R2-02, R3-01, R3-03, R3-04, R3-05, R4-01, R4-02, R5-01, R5-02, R5-03, R6-01, R6-02, R6-03, R6-05, R6-06.

## Progress / Phase Tracking

| Phase | Status | Integration | SHA | Date |
|-------|--------|-------------|-----|------|
| Phase 3 — shared runtime / MCP surface | COMPLETE | fast-forward into `main` from `sdd/shared-runtime-and-mcp` | `f8ae23a` | 2026-09-05 |

### Completion log

#### Phase 3 — COMPLETE (2026-09-05, `f8ae23a`)

Phase 3 (shared runtime/MCP surface) is COMPLETE and integrated into `main` at `f8ae23a` via fast-forward from `sdd/shared-runtime-and-mcp`.

All 12 tasks (1-11 plus 7a, 12) were implemented, reviewed, and approved via subagent-driven development.

The Phase 3 independent exit gate PASSED: `dotnet build Scrinia.sln` green (0 errors); `dotnet test Scrinia.sln` (benchmarks excluded) 0 failed / 2567 passed / 19 skipped; all 21 named shared test classes pass (193 shared tests); `Scrinia.Mcp` references `Scrinia.Core` only with no reference to the executable `Scrinia`; no Git process APIs in the shared graph; no `cmds.txt` tracked.

The five exit-gate failures that were resolved as non-regressions: 3 shared-envelope goldens were a local `core.autocrlf` CRLF-checkout artifact (restored to committed LF); 4 historical-file SHA256 goldens in `PackagingAndDocsMergeRetirementTests.cs` were stale constants recorded incorrectly at test introduction (the docs are byte-identical to HEAD and were not modified); 1 leftover ignored build-output dir (`tests/Scrinia.Merge.Tests`, `src/Scrinia.Merge`) from the retired merge work was removed. All corrections were test-only.

## Global Constraints

- `scri shared serve` exposes exactly these eleven tools: `shared_bases`, `shared_search`, `shared_list`, `shared_recall`, `shared_facts`, `shared_remember`, `shared_append`, `shared_forget`, `shared_compact`, `shared_link`, and `shared_refresh`.
- There is no `shared_guide`, `shared_restore`, `shared_hint`, `shared_reconcile`, unprefixed alias, `scopes` parameter, ambient restore, cross-base link, or implicit mutation target. Local tools are not moved or renamed.
- `Scrinia.Mcp` has a project reference to `Scrinia.Core` and no project or source reference to executable `Scrinia`. Core has no reverse reference. The executable is a host adapter, not the owner of shared abstractions.
- Shared bases are persistent-only. Every Ephemeral result from `MemoryAddress`/`PathParser` for `path`, `destination`, or `pathPrefix` fails with `SHARED_EPHEMERAL_NOT_SUPPORTED` before lookup, cache access, fingerprinting, or I/O.
- Enabled-only privacy is mandatory: disabled registry names and roots never appear in shared results, warnings, errors, diagnostics, or logs. `SharedPrivacyTests` is created and run as an explicit gate.
- An absent `Scrinia:Shared:Enabled` key means zero bases and makes `scri shared serve` fail before MCP transport with `NO_SHARED_BASES_ENABLED`; an empty scalar remains invalid.
- Shared tracking is always `project-git`; `.git` validation is filesystem-only. Scrinia never launches Git or inspects the index. Git guidance prints exact commands only.
- Durable inventory, fingerprints, ignore rules, cleanup pathspecs, and manifests use the exact authoritative/derived classifier in spec §4.1. UTF-8 without BOM and LF endings are required where specified.
- Every claimed gate is implemented as a bite-sized TDD task with one named test file and one or more named test cases. The coverage matrix below is one-task/one-test backed.
- Phase 3 owns all shared response wording and removes every temporary adapter before its exit gate. Phase 4 consumes this final contract and must not change shared response wording (spec §13; R3-01).

---

## File and Ownership Map

### Core-owned shared abstractions (create)

- `src/Scrinia.Core/Configuration/UserHome.cs` — existing Phase 2 contract: `public static string Resolve()` and `public static string Resolve(string? scriniaHome, string localApplicationData)`.
- `src/Scrinia.Core/Configuration/HomeConfig.cs` — `HomeConfig`, `SharedBaseEntry`, `IHomeConfigStore`, and `HomeConfigStore`.
- `src/Scrinia.Core/Configuration/WorkspaceConfig.cs` — `IWorkspaceConfig`, `WorkspaceConfig`, `GetValue`, `SetValue`, `UnsetValue`, `GetEnabledBases`, `GetTrackingPolicy`, and `Save`.
- `src/Scrinia.Core/Configuration/SharedBaseRegistry.cs` — `ISharedBaseRegistry`, `SharedBaseRegistry`, canonical names, root evidence, containment, collision detection, and enabled filtering.
- `src/Scrinia.Core/Configuration/SharedBaseRuntimeContracts.cs` — the Core-only runtime boundary consumed by MCP:

```csharp
public interface ISharedBaseRuntimeFactory
{
    SharedBaseRuntime Open(SharedBaseEntry entry);
}

public interface ISharedBaseRuntime
{
    string BaseName { get; }
    string RegisteredRoot { get; }
    SharedBaseSnapshot Snapshot { get; }
    SharedBaseOperationLease AcquireReadLease();
    SharedBaseMutationLease AcquireMutationLease();
    SharedBaseRefreshLease AcquireRefreshLease();
    SharedMutationPreflight RevalidateBeforeMutation(string? canonicalPath, string? canonicalDestination);
    SharedMutationCommit CommitMutation(SharedMutationPlan plan);
    SharedRefreshCommit Refresh();
}

public sealed class SharedBaseRuntimeFactory : ISharedBaseRuntimeFactory { }
public sealed class SharedBaseRuntime : ISharedBaseRuntime { }
```

  `SharedBaseSnapshot`, `SharedBaseOperationLease`, `SharedBaseMutationLease`, `SharedBaseRefreshLease`, `SharedMutationPreflight`, `SharedMutationPlan`, `SharedMutationCommit`, and `SharedRefreshCommit` are Core result/value types. Their APIs carry canonical base identity, snapshot generation, warnings, fingerprints, availability, mutation certainty, and authoritative-file manifests; they do not carry MCP/YAML types or executable types.
- `src/Scrinia.Core/Configuration/ManagedIgnoreBlock.cs` and `src/Scrinia.Core/Configuration/SharedClassifier.cs` — one ordered classifier and exact managed block.
- `src/Scrinia.Core/Configuration/SharedManifest.cs` — deterministic authoritative path manifest and ordinal-byte sorting.
- `src/Scrinia.Core/Configuration/SharedErrors.cs` — stable shared error codes and typed operation failures.
- `src/Scrinia.Core/Configuration/HomeConfigJsonContext.cs` — source-generated home-config DTOs.

### MCP-owned shared surface (create/modify)

- `src/Scrinia.Mcp/Shared/SharedDtoContracts.cs` — exact §5.2 DTOs, enums, and discriminated facts results.
- `src/Scrinia.Mcp/Shared/SharedErrorCodes.cs` — only if the existing Core error contract cannot be used directly; values must remain the exact Core-declared codes.
- `src/Scrinia.Mcp/Shared/SharedServer.cs` — MCP-facing orchestration over `IWorkspaceConfig`, `ISharedBaseRegistry`, and `ISharedBaseRuntimeFactory`; selectors, privacy filtering, aggregation, precedence, and DTO construction.
- `src/Scrinia.Mcp/Shared/SharedTools.cs` — exactly eleven public `[McpServerTool]` methods; all helpers are `internal` or `private`.
- `src/Scrinia.Mcp/McpResponse.cs` — shared envelope/YAML builders, without changing local builders.
- `src/Scrinia.Mcp/MemoryTools.Skills.cs` — source-generated registrations for shared DTOs.

### Executable host adapter (create/modify)

- `src/Scrinia/Commands/ScriniaCommands.SharedServe.cs` — parses `shared serve`, resolves `SCRINIA_HOME` and workspace inputs through Core-facing adapters, rejects no-base startup before transport, and registers Core services plus `SharedTools`.
- `src/Scrinia/Commands/SharedServeHostAdapter.cs` — executable-only composition root implementing host acquisition and DI wiring; it calls Core `UserHome`, `IHomeConfigStore`, `IWorkspaceConfig`, `ISharedBaseRegistry`, and `ISharedBaseRuntimeFactory` and contains no registry, selector, fingerprint, DTO, or envelope algorithm.
- `src/Scrinia/Commands/ScriniaCommands.Serve.cs` — add the subcommand while leaving the local `serve` construction and local `WithTools<ScriniaMcpTools>` untouched.

### Tests (create)

All files below live under `tests/Scrinia.Tests/Shared/` unless noted. Existing local tests are not moved.

---

## Implementation Tasks

### Task 1: Establish the Core/MCP/executable dependency boundary

**Files:**
- Create: `src/Scrinia.Core/Configuration/SharedBaseRuntimeContracts.cs`
- Modify: `src/Scrinia.Core/Configuration/SharedBaseRegistry.cs`, `src/Scrinia.Mcp/*.csproj`
- Create: `src/Scrinia/Commands/SharedServeHostAdapter.cs`
- Test: `tests/Scrinia.Tests/Shared/McpCoreReferenceTests.cs`

**Interfaces:**
- Produces the exact Core interfaces and implementations listed in the File and Ownership Map.
- The executable adapter consumes those interfaces; `SharedServer` will consume them in Task 5.

- [x] Write `Mcp_project_references_Core_and_not_executable`, `Core_has_no_reference_to_executable`, and `Executable_shared_host_contains_no_shared_algorithms`.
- [x] Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~McpCoreReferenceTests`; expect failure until the boundary exists.
- [x] Move/define all shared config/base/runtime contracts in Core, add the adapter direction, and remove any planned MCP→Scrinia reference.
- [x] Rerun the named filter and require PASS.

### Task 2: Define the exact eleven tool surface and standalone schemas

**Files:**
- Create: `src/Scrinia.Mcp/Shared/SharedDtoContracts.cs`, `src/Scrinia.Mcp/Shared/SharedTools.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.Skills.cs`
- Test: `tests/Scrinia.Tests/Shared/SharedToolCountTests.cs`, `tests/Scrinia.Tests/Shared/SharedSchemaTests.cs`

**Interfaces:**
- `SharedTools` exposes exactly `shared_bases`, `shared_search`, `shared_list`, `shared_recall`, `shared_facts`, `shared_remember`, `shared_append`, `shared_forget`, `shared_compact`, `shared_link`, and `shared_refresh`.
- DTO signatures exactly match spec §5.2: `SharedBaseSummary`, `SearchHit`, `ListItem`, `RecallResult`, `FactsEntitiesResult`, `FactsProfileResult`, `FactsIntersectResult`, `FactsResult`, `MutationResult`, `RefreshResult`, and `SharedWarning`.

- [x] Write `Registers_exactly_eleven_prefixed_methods_and_no_forbidden_methods` and `Schemas_reject_unknown_properties_and_enforce_all_ranges_defaults_and_conditional_facts_arguments`.
- [x] Run both named test files; expect failure.
- [x] Implement standalone signatures and source-generated registrations. Do not inherit input/output members from local tools.
- [x] Rerun both named test files and require PASS.

### Task 3: Implement selectors, provenance, and persistent-only address guards

**Files:**
- Modify: `src/Scrinia.Mcp/Shared/SharedServer.cs`
- Test: `tests/Scrinia.Tests/Shared/SharedSelectorProvenanceTests.cs`, `tests/Scrinia.Tests/Shared/SharedEphemeralRefusalTests.cs`

**Interfaces:**
- `SharedServer` consumes `IWorkspaceConfig.GetEnabledBases()`, `ISharedBaseRegistry.Lookup(string)`, and `ISharedBaseRuntimeFactory.Open(SharedBaseEntry)`.
- Every selected result/warning carries the canonical logical `base`; targeted envelopes echo scalar `base` only after syntax and membership succeed.

- [x] Write `Selectors_are_enabled_only_and_preserve_base_provenance` and `Ephemeral_path_destination_and_prefix_are_refused_before_store_or_fingerprint_for_every_supported_spelling` covering `~name`, `/temp/name`, canonical aliases, and every parser-classified Ephemeral value.
- [x] Run both named test files; expect failure.
- [x] Implement exact selector resolution and invoke `MemoryAddress`/`PathParser` before any runtime lookup; exclude Ephemeral inventory results.
- [x] Rerun both named test files and require PASS.

### Task 4: Enforce enabled-only privacy and no-base startup

**Files:**
- Modify: `src/Scrinia/Commands/ScriniaCommands.SharedServe.cs`, `src/Scrinia/Commands/SharedServeHostAdapter.cs`, `src/Scrinia.Mcp/Shared/SharedServer.cs`
- Test: `tests/Scrinia.Tests/Shared/SharedPrivacyTests.cs`, `tests/Scrinia.Tests/Shared/NoBaseStartupTests.cs`

**Interfaces:**
- The host adapter fails before MCP transport with `NO_SHARED_BASES_ENABLED` when the Enabled key is absent.
- `shared_bases` returns every enabled name, including unavailable/stale/conflicted entries, and never a disabled registry entry.

- [x] Create and run `SharedPrivacyTests`: assert a disabled name and root are absent from every aggregate result, targeted error, warning, diagnostic, instruction, follow-up, and captured log; assert explicit registry enumeration is the only private registry surface.
- [x] Write `No_enabled_key_fails_before_transport_with_NO_SHARED_BASES_ENABLED`.
- [x] Run both named test files; expect failure.
- [x] Implement the privacy filter at the Core-backed selection boundary and perform the absent-key check before transport construction.
- [x] Rerun both named test files and require PASS.

### Task 5: Implement exact envelope serialization and validation/error precedence

**Files:**
- Modify: `src/Scrinia.Mcp/McpResponse.cs`, `src/Scrinia.Mcp/Shared/SharedServer.cs`
- Test: `tests/Scrinia.Tests/Shared/SharedEnvelopeGoldensTests.cs`, `tests/Scrinia.Tests/Shared/SharedErrorPrecedenceTests.cs`

**Interfaces:**
- Shared envelopes always contain `status`, `warnings`, `instruction`, `followUp`, and `mutation`; error envelopes contain `errorCode` and `error`, never `result`/`results`; aggregate envelopes never contain scalar `base`.
- Apply the exact precedence: parameter/schema, base syntax, enabled membership, registry, Git root, logical/physical path, tracking block, availability/conflict, freshness/format, operation, storage.

- [x] Write golden cases `Serializes_recall_mutation_refresh_partial_and_zero_success_error_exactly`, `Preserves_nullable_timestamps_and_digests`, and `Omits_rejected_base_and_mutation_path`; write precedence cases for each adjacent stage and disabled-name non-disclosure.
- [x] Run both named files; expect failure.
- [x] Implement source-generated DTO serialization and exact YAML success/partial/error envelopes, including `page`, `truncated`, conditional `base`, and `mutation: applied|none|unknown`.
- [x] Implement the fixed precedence table and safe diagnostics.
- [x] Rerun both named files and require PASS.

### Task 6: Implement durable inventory, compatibility matrix, and UTF-8 conflict scanning

**Files:**
- Modify: `src/Scrinia.Core/Configuration/SharedBaseRuntimeContracts.cs`, `src/Scrinia.Core/Configuration/SharedClassifier.cs`, `src/Scrinia.Mcp/Shared/SharedServer.cs`
- Test: `tests/Scrinia.Tests/Shared/SharedDurableMatrixTests.cs`, `tests/Scrinia.Tests/Shared/SharedConflictMarkerTests.cs`

**Interfaces:**
- `SharedBaseRuntime.Open` and `Refresh` scan authoritative UTF-8 NMP/JSON/Markdown/sidecars before decode/parse, apply spec §§4.4–4.5, and publish one snapshot.
- Unsupported/corrupt/missing/newer entries follow the exact availability and mutability matrix; no automatic numeric upgrade.

- [x] Write one case for every §4.4 row, including newer read-only sidecars, invalid identity, malformed skill/agent metadata, and valid bodies with null durable timestamps.
- [x] Write `Detects_only_ordered_line_start_marker_triplets_in_utf8_authoritative_files` and `Ignores_embedded_incomplete_out_of_order_and_non_authoritative_markers`; cover LF and CRLF separator recognition and UTF-8-without-BOM bytes.
- [x] Run both named files; expect failure.
- [x] Implement the ordered marker-triplet scan before format parsing and the complete compatibility defaults/visibility rules.
- [x] Rerun both named files and require PASS.

### Task 7: Implement freshness, fingerprinting, leases, stale, unknown, and refresh

**Files:**
- Modify: `src/Scrinia.Core/Configuration/SharedBaseRuntimeContracts.cs`, `src/Scrinia.Mcp/Shared/SharedServer.cs`
- Test: `tests/Scrinia.Tests/Shared/SharedFreshnessTests.cs`

**Interfaces:**
- `ISharedBaseRuntime` provides read, mutation, and exclusive refresh leases; fingerprints use the exact length-prefixed UTF-8 path/content sequence from spec §4.2.

- [x] Write `Detects_external_fingerprint_drift_before_mutation`, `Detects_changed_canonical_target_as_unsafe_path`, `Returns_unknown_and_blocks_writes_after_post_write_invalidity`, `Refresh_swaps_snapshot_and_fingerprint_atomically`, `Refresh_publishes_valid_entries_with_warnings`, and `Documents_F0_to_F1_race_as_unsupported_not_prevented`.
- [x] Run the named file; expect failure.
- [x] Implement per-base serialization, F0 revalidation, `SHARED_BASE_STALE`, `SHARED_BASE_UNSAFE_PATH`, post-write F1 validation, `mutation: unknown`, stale/conflicted blocking, and explicit refresh. Do not claim portable CAS or race prevention.
- [x] Rerun the named file and require PASS.

### Task 7a: Prove cache isolation across shared-base identities

**Files:**
- Modify: `src/Scrinia.Core/Configuration/SharedBaseRuntimeContracts.cs`, `src/Scrinia.Mcp/Shared/SharedServer.cs`
- Test: `tests/Scrinia.Tests/Shared/SharedCacheIsolationTests.cs`

**Interfaces:**
- Cache, index, facts, vector, lock, and generation consumers use the canonical physical root identity plus canonical base name as their namespace; logical memory addresses remain local to that namespace.
- Registry refresh/repoint and server restart construct state for the newly resolved root instead of reopening state from the previous root.

- [x] Write `Cache_keys_include_canonical_root_and_base_name`, `Registry_root_repoint_requires_restart_or_refresh_and_does_not_reuse_prior_generation`, and `Equal_logical_paths_in_two_bases_do_not_share_index_facts_vectors_or_cache_state`.
- [x] In the tests, create two distinct validated shared roots with the same canonical logical memory path, seed different content/facts/vectors in each, warm every read/cache layer, repoint the same registry name to the second root, and assert that restart or explicit refresh returns only the second root's generation and derived state. Assert that no cache/index/facts/vector handle or lock created for the first canonical root is reused for the second.
- [x] Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~SharedCacheIsolationTests`; expect failure until every cache namespace includes both canonical root and base name and repoint handling is isolated.
- [x] Implement the minimal root-plus-name cache identity and restart/refresh invalidation/reconstruction path; do not use memory address alone as a cross-base key and do not open a derived cache file from another root.
- [x] Rerun `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~SharedCacheIsolationTests` and require PASS.

### Task 8: Implement authoritative mutation manifests and Git guidance

**Files:**
- Modify: `src/Scrinia.Core/Configuration/SharedManifest.cs`, `src/Scrinia.Mcp/Shared/SharedServer.cs`
- Test: `tests/Scrinia.Tests/Shared/SharedManifestTests.cs`, `tests/Scrinia.Tests/Shared/SharedGitGuidanceTests.cs`

**Interfaces:**
- `SharedManifest.Create(IEnumerable<string> changedAuthoritativePaths)` returns root-relative, `/`-separated, UTF-8-ordinal sorted, deduplicated files only.
- Applied remember/append list body and sidecar; forget lists deleted authoritative files; link lists both endpoints; compact lists rewritten body/sidecar and a version archive only if the classifier says versions are authoritative; skill/agent writes body plus sidecar.

- [x] Write `Manifest_contains_only_exact_authoritative_files_for_each_mutation`, `Never_contains_directory_wildcard_derived_or_pretracked_derived_paths`, and `Git_guidance_is_operation_based_and_shell_quotes_each_manifest_path`.
- [x] Run both named files; expect failure.
- [x] Implement exact manifests and print-only `git -C "<registered-root>" add -A -- '<path>' ...`, commit, and push guidance from §7.2; do not claim Git state.
- [x] Rerun both named files and require PASS.

### Task 9: Prove the durable classifier matrix and Core registry provenance

**Files:**
- Modify: `src/Scrinia.Core/Configuration/ManagedIgnoreBlock.cs`, `src/Scrinia.Core/Configuration/SharedClassifier.cs`, `src/Scrinia.Core/Configuration/SharedBaseRegistry.cs`
- Test: `tests/Scrinia.Tests/Shared/SharedClassifierEquivalenceTests.cs`, `tests/Scrinia.Tests/Shared/SharedBaseRegistryTests.cs`

**Interfaces:**
- `SharedClassifier` is the sole source for managed ignore patterns, fingerprint exclusions, and static cleanup pathspecs; overlap in Git patterns is allowed while semantic classification remains first-match.
- `ISharedBaseRegistry` validates direct `.git` evidence, exact `gitdir:` worktree markers, containment/collisions, fixed `project-git`, and exact managed UTF-8/LF blocks.

- [x] Write `Classifier_sets_match_for_every_directory_file_and_descendant_case`, including root/deep `.tmp`, root-child `.lock`, deleted-but-indexed paths, and reserved-suffix rejection; write `Registry_accepts_git_directory_or_exact_utf8_worktree_marker_and_rejects_ancestor_only_nested_and_overlapping_roots`.
- [x] Run both named files; expect failure.
- [x] Implement the one ordered classifier and registry validation without Git invocation or index inspection.
- [x] Rerun both named files and require PASS.

### Task 10: Remove temporary adapters and lock local/shared disjointness

**Files:**
- Modify: `src/Scrinia.Mcp/Shared/SharedServer.cs`, `src/Scrinia/Commands/ScriniaCommands.Serve.cs`
- Test: `tests/Scrinia.Tests/Shared/SharedNoAdapterTests.cs`, `tests/Scrinia.Tests/Shared/SharedLocalDisjointnessTests.cs`

**Interfaces:**
- Shared code consumes only Core interfaces and shared DTOs. The local `ScriniaMcpTools` registration and local store path remain unchanged.

- [x] Write `No_temporary_shared_adapter_or_executable_contract_reference_remains` and `Local_serve_registration_and_store_are_unchanged_and_shared_registration_is_disjoint`.
- [x] Run both named files; expect failure.
- [x] Delete temporary adapter seams, wire `SharedServeHostAdapter` to Core implementations, and verify `WithTools<SharedTools>` registers only the shared surface.
- [x] Rerun both named files and require PASS.

### Task 11: Add the command, prompt isolation, and complete named test run

**Files:**
- Modify: `src/Scrinia/Commands/ScriniaCommands.SharedServe.cs`, `src/Scrinia.Mcp/Shared/SharedTools.cs`
- Test: `tests/Scrinia.Tests/Shared/SharedNoPromptCorrelationTests.cs`, `tests/Scrinia.Tests/Shared/SharedNoGitProcessTests.cs`

**Interfaces:**
- Shared MCP inputs contain no prompt or correlation field; operation-based guidance is independent of prompt text.

- [x] Write `Shared_responses_are_independent_of_initiating_prompt_or_correlation` and `Shared_surface_never_starts_a_git_process`.
- [x] Run both named files; expect failure.
- [x] Implement `scri shared serve`, register eleven tools, remove all prompt transport from shared handlers, and audit the complete shared call graph for process launches.
- [x] Rerun both named files and require PASS.

### Task 12: Update only Phase 3 living documentation and perform the independent exit gate

**Files:**
- Modify only the shared-MCP technical sections of `docs/architecture/overview.md`, `docs/architecture/cli.md`, and `docs/architecture/core.md`.
- Test: `tests/Scrinia.Tests/Shared/SharedPhase3ExitGateTests.cs`

- [x] Write `Phase3_contract_is_complete_and_phase4_consumes_without_rewording`, asserting the eleven tool names, Core ownership, exact forbidden surfaces, exact error/envelope terms, and documentation ownership boundary.
- [x] Run the named test; expect failure.
- [x] Document the final Core/MCP/CLI direction, eleven-tool surface, selectors/provenance, persistent-only policy, refresh/freshness semantics, manifests, Git prohibition/guidance, and Phase 4 consumption rule. Do not edit Phase 4 onboarding/hints or Phase 5 packaging/plugin sections.
- [x] Run `dotnet build Scrinia.sln`.
- [x] Run `dotnet test Scrinia.sln` with benchmarks excluded.
- [x] Run every named shared test file from Tasks 1–12, explicitly including creation and execution of `SharedPrivacyTests`.
- [x] Search the dependency graph for `Scrinia.Mcp`→`Scrinia`, executable-owned duplicate shared abstractions, forbidden tool names, Git process APIs, temporary adapters, and disabled-name logging.
- [x] Verify no `cmds.txt` is tracked or modified and make no commit or push.

## Independent Exit Criteria

- Core owns `IHomeConfigStore`, `IWorkspaceConfig`, `ISharedBaseRegistry`, `ISharedBaseRuntimeFactory`, `ISharedBaseRuntime`, `HomeConfigStore`, `WorkspaceConfig`, `SharedBaseRegistry`, `SharedBaseRuntimeFactory`, and `SharedBaseRuntime`; `Scrinia.Mcp` references Core only; the executable contains host adapters only.
- `scri shared serve` starts only with at least one enabled name and exposes exactly eleven prefixed tools. Local `scri serve` remains unchanged and disjoint.
- `SharedPrivacyTests` passes: disabled names/roots are never disclosed in any shared output, warning, error, diagnostic, instruction, follow-up, or log.
- Selectors and base provenance pass; no scalar base appears before syntax and enabled-membership validation; no rejected target appears in an error result.
- Every Ephemeral spelling classified by the current parser is refused before storage, cache, fingerprint, or I/O; shared inventory contains persistent entries only.
- Exact DTOs, source-generated serialization, YAML goldens, nullable timestamps/digests, pagination, truncation, mutation certainty, and validation/error precedence pass.
- The durable matrix, exact defaults, UTF-8 Git marker triplet detection/non-detection, classifier equivalence, and no automatic format upgrade pass.
- Freshness tests pass for external drift, target/path changes, stale refusal, post-write unknown mutation, write blocking, refresh, and the documented F0-to-F1 residual race limitation.
- Mutation manifests contain only exact authoritative files; Git guidance is print-only, operation-based, shell-quoted, and never claims Git state. No Git process starts.
- No temporary shared adapter remains. Phase 4 consumes these final contracts and cannot alter shared response wording.
- Build and the full non-benchmark test suite pass. This gate is independent of Phase 4 and does not authorize implementation until the governing specification receives its external approval.

## One-Task/One-Test Coverage Matrix

| Requirement / ruling | Task | Single backing test gate |
|---|---:|---|
| Core ownership and no MCP→executable reference | 1 | `McpCoreReferenceTests.Mcp_project_references_Core_and_not_executable` |
| Exactly eleven prefixed tools | 2 | `SharedToolCountTests.Registers_exactly_eleven_prefixed_methods_and_no_forbidden_methods` |
| Complete standalone schemas and selectors | 2–3 | `SharedSchemaTests.Schemas_reject_unknown_properties_and_enforce_all_ranges_defaults_and_conditional_facts_arguments` |
| Selectors/base provenance | 3 | `SharedSelectorProvenanceTests.Selectors_are_enabled_only_and_preserve_base_provenance` |
| Persistent-only addresses | 3 | `SharedEphemeralRefusalTests.Ephemeral_path_destination_and_prefix_are_refused_before_store_or_fingerprint_for_every_supported_spelling` |
| Enabled-only privacy | 4 | `SharedPrivacyTests.Disabled_names_and_roots_never_appear_in_any_shared_surface` |
| No-base startup | 4 | `NoBaseStartupTests.No_enabled_key_fails_before_transport_with_NO_SHARED_BASES_ENABLED` |
| DTO/YAML envelopes and error precedence | 5 | `SharedEnvelopeGoldensTests.Serializes_recall_mutation_refresh_partial_and_zero_success_error_exactly` |
| Durable compatibility matrix and defaults | 6 | `SharedDurableMatrixTests.Covers_every_spec_4_4_row_and_spec_4_5_default` |
| UTF-8 marker triplet conflicts | 6 | `SharedConflictMarkerTests.Detects_only_ordered_line_start_marker_triplets_in_utf8_authoritative_files` |
| Refresh/fingerprint/stale/unknown mutation | 7 | `SharedFreshnessTests.Detects_external_fingerprint_drift_before_mutation` |
| Cache isolation and root-repoint collision safety | 7a | `SharedCacheIsolationTests.Equal_logical_paths_in_two_bases_do_not_share_index_facts_vectors_or_cache_state` |
| Exact authoritative manifests | 8 | `SharedManifestTests.Manifest_contains_only_exact_authoritative_files_for_each_mutation` |
| Operation-based Git guidance | 8 | `SharedGitGuidanceTests.Git_guidance_is_operation_based_and_shell_quotes_each_manifest_path` |
| Classifier/ignore/fingerprint/cleanup equivalence | 9 | `SharedClassifierEquivalenceTests.Classifier_sets_match_for_every_directory_file_and_descendant_case` |
| Git root/worktree and collision validation | 9 | `SharedBaseRegistryTests.Registry_accepts_git_directory_or_exact_utf8_worktree_marker_and_rejects_ancestor_only_nested_and_overlapping_roots` |
| Removal of temporary adapters | 10 | `SharedNoAdapterTests.No_temporary_shared_adapter_or_executable_contract_reference_remains` |
| Local/shared disjointness | 10 | `SharedLocalDisjointnessTests.Local_serve_registration_and_store_are_unchanged_and_shared_registration_is_disjoint` |
| No prompt correlation | 11 | `SharedNoPromptCorrelationTests.Shared_responses_are_independent_of_initiating_prompt_or_correlation` |
| No Git process | 11 | `SharedNoGitProcessTests.Shared_surface_never_starts_a_git_process` |
| Phase ownership and Phase 4 consumption | 12 | `SharedPhase3ExitGateTests.Phase3_contract_is_complete_and_phase4_consumes_without_rewording` |
