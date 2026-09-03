# Shared Memory and Global Tool — Corrected Five-Plan Coverage Matrix

**Governing specification:** [`../specs/2026-08-31-shared-memory-global-tool-design.md`](../specs/2026-08-31-shared-memory-global-tool-design.md) — `Proposed — reviewer approval pending`.

**Review record:** [`../reviews/2026-08-31-shared-memory-global-tool-spec-review.md`](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md) — `Proposed — reviewer approval pending`. A closed ruling records incorporation into the proposal, not implementation or external approval.

This matrix covers the five corrected plans. Every numbered specification section and every one of the 14 acceptance items has one owning plan task and one named test or exit gate. A handoff is a dependency, not a second owner.

## Plan key

| Phase | Plan | Exclusive responsibility |
|---|---|---|
| P1 | `2026-09-01-merge-retirement.md` | Retire active merge/reconcile behavior and surfaces; preserve only the Phase 2 migration-recognition allowlist. |
| P2 | `2026-09-01-config-tracking-shared-bases.md` | Produce Core user-home, config, tracking, migration, classifier, and shared-base contracts and implementations; keep `Scrinia` as CLI adapter/host. |
| P3 | `2026-09-01-shared-runtime-and-mcp.md` | Consume P2 Core contracts and produce the shared server, eleven MCP tools, DTOs, envelopes, runtime, freshness, manifests, and shared technical guidance. |
| P4 | `2026-09-01-hints-and-onboarding.md` | Consume P3’s frozen response contract; own hint transport, hooks, agent artifacts, `scrinia.ts`, and multi-user/onboarding replacement. |
| P5 | `2026-09-01-global-tool-packaging.md` | Consume P2 `UserHome` and P3’s shared server; own package, installation, mutable model/plugin placement, and plugin/model diagnostics and docs. |

Task references below are the numbered tasks in these five plans. A plan’s independent exit task is used only for that plan’s final gate; feature rows retain their own named tests.

## Project direction and dependency chain

```text
P1 merge retirement
  → P2 Core contracts and implementations
  → P3 shared runtime and final response contract
  → P4 hooks/onboarding and scrinia.ts
  → P5 global-tool packaging and model/plugin placement
```

| Handoff | Producer | Consumer | Required evidence |
|---|---|---|---|
| P1 → P2 | P1-T1/T6 | P2-T6/T10 | Only `LegacyMergeRecognition` and its tests remain on the migration-only allowlist; no active merge driver, reconcile, multi-user merge setup, or Git process remains. |
| P2 → P3 | P2-T1–T10 | P3-T1/T3/T4/T6/T9 | Core supplies `UserHome`, typed home config, flat workspace config, tracking/migration results, `ISharedBaseRegistry`, `SharedClassifier`, managed-block validation, containment, and declared errors. P3 consumes these contracts directly. |
| P3 → P4 | P3-T12 | P4-T3/T5/T6 | P3 freezes the eleven-tool DTOs, YAML envelopes, provenance/privacy rules, operation-based guidance, and no-adapter boundary. P4 consumes the final response contract unchanged. |
| P2 → P5 | P2-T1/T2 | P5-T1/T2/T3/T8 | P5 consumes Core `UserHome` and the Phase 2 config/precedence contract; it does not redefine or reorder either. |
| P3 → P5 | P3-T11/T12 | P5-T5/T8 | P5 consumes the final `scri shared serve` command and shared-server handshake; package smoke tests exercise both local and shared modes. |

## Exclusive ownership rules

| Surface | Sole owner and boundary |
|---|---|
| Merge projects, active merge code, reconcile, merge skill, setup merge paths, package/workflow references | P1. The only P2 exception is `src/Scrinia.Core/Migration/LegacyMergeRecognition.cs` and `tests/Scrinia.Tests/LegacyMergeRecognitionTests.cs`, containing pure recognition, exact legacy bytes/lines, and static unset argv only. |
| `docs/multi-user-setup.md` | P4 whole-file replacement. P1 does not edit it; P5 does not edit it. |
| `docs/troubleshooting.md` | P1 owns only `Manual Git conflict cleanup after merge retirement`. P5 owns only `Model not found`, `Semantic search not working`, `Vulkan plugin not loading`, `Plugin crashes / degraded to BM25-only`, `Plugin not found despite being installed`, and `Access denied when publishing`. |
| README, getting started, security, CLI reference | P2 owns config/tracking/shared-base subjects; P4 owns shared onboarding/threat subjects; P5 owns installation/global-tool/model/plugin/setup/diagnostic subjects. Ownership is by heading or line subject, never by broad file claim. |
| Architecture and plugin docs | P3 owns shared-MCP technical subjects in `architecture/overview.md`, `architecture/cli.md`, and `architecture/core.md`; P5 owns only the plugin/model headings in `architecture/cli.md` and `docs/plugin-authoring.md`. |
| `src/Scrinia/Commands/WorkspaceSetup.cs` | P2 owns `Root`, `Configure`, `GetConfigValue`, and the config-resolver/shared-registry region. P5 owns only `LoadPluginsAsync`, `BuildEmbeddingOptions`, `GetPluginName`, `ResolveEmbeddingsPluginExe`, `TryLoadVulkanPluginAsync`, `IsHttpEmbeddingsProvider`, `LoadExplicitHttpEmbeddings`, `LoadBuiltInEmbeddings`, `MaybeReindexAfterModelSwitch`, `ForceReindexAsync`, `InstallEmbeddingEventSinks`, and the `{exeDir}/models`/`{exeDir}/plugins` relocation. |
| `src/Scrinia.Mcp/plugins/opencode/scrinia.ts` | P4-T4 exclusively: replace `scri save --hook` with `scri hint --client opencode`. P5 must not touch it. |
| `McpResponse.cs`, shared DTOs, schemas, envelopes, response wording | P3 exclusively. P4 consumes the frozen contract; P5 packages and tests it but does not revise it. |

## Core, CLI, and MCP boundary

- **Phase 2 produces reusable Core contracts.** `Scrinia.Core` owns `UserHome`, typed `SCRINIA_HOME/config.json`, flat workspace-config parsing, tracking/migration state, `IHomeConfigStore`, `IWorkspaceConfig`, `ISharedBaseRegistry`, `SharedClassifier`, managed blocks, containment, and typed results/errors. `Scrinia` binds arguments/environment and presents Core results; it does not own those algorithms.
- **Phase 3 consumes Core contracts.** `Scrinia.Mcp` references `Scrinia.Core` only. Its shared server and eleven tools consume Core registry/config/runtime contracts and own MCP DTO/YAML serialization. The executable is the host adapter and transport composition root.
- **Phase 4 consumes P3’s final response contract.** It owns no shared DTO, envelope, error-code, manifest, or response wording. Its `hint` path is hook-only and independent of MCP.
- **Phase 5 consumes P2 and P3.** It calls the Core `UserHome` resolver unchanged and packages the P3 shared server unchanged. Models/plugins/state move below `SCRINIA_HOME`; the installed package remains immutable.

## Specification-section matrix

Each row has exactly one owning task. The gate names the evidence required for that task; no row is accepted from a proposal example alone.

| Spec section | Owner | Required test or exit gate |
|---|---|---|
| §1 Purpose and governing boundary | P3-T12 | `SharedLocalDisjointnessTests.Local_serve_registration_and_store_are_unchanged_and_shared_registration_is_disjoint`; P3 exit gate also checks no Git process and Core-only MCP reference. |
| §2 Goals and non-goals | P5-T8 | `GlobalToolInstallTests` checks both modes and immutable installation; final cross-phase forbidden-surface scan checks no HTTP/daemon/copy/implicit-target addition. |
| §3.1 User home and typed registry | P2-T1 | `UserHomeResolverTests` plus `HomeConfigSerializationTests` and `HomeConfigParserValidationTests`; P2-T10 verifies Core consumption by P3/P5. |
| §3.2 Flat workspace configuration and precedence | P2-T2 | `WorkspaceConfigScalarTests`, `TrackingPrecedenceTests`, and `NoSharedEnabledEnvTests`. |
| §3.3 Base names, roots, and visibility | P2-T4 | `SharedBaseRegistryTests` and `SharedBasePrivacyTests`, including direct `.git`, exact `gitdir:`, containment, collision, and disabled-name privacy. |
| §3.4 Startup with no usable bases | P3-T4 | `NoBaseStartupTests.No_enabled_key_fails_before_transport_with_NO_SHARED_BASES_ENABLED`; `SharedPrivacyTests` checks unavailable enabled names remain representable. |
| §4.1 Classification | P2-T5 | `DerivedClassifierEquivalenceTests` covers ordered first match, recursive `.tmp`, root-child `.lock`, ignore/fingerprint/cleanup set equality, and reserved-suffix rejection. |
| §4.2 Fingerprint inventory | P3-T7 | `SharedFreshnessTests` verifies length-delimited UTF-8 path/content hashing, authoritative-only inventory, ordering, and derived-only exclusion. |
| §4.3 Cache isolation and collision rules | P3-T7a | `SharedCacheIsolationTests` proves root-plus-base cache keys and no cross-base/package writes. |
| §4.4 Authoritative durable-format matrix | P3-T6 | `SharedDurableMatrixTests.Covers_every_spec_4_4_row_and_spec_4_5_default` and `SharedConflictMarkerTests.Detects_only_ordered_line_start_marker_triplets_in_utf8_authoritative_files`. |
| §4.5 Exact durable compatibility | P3-T6 | `SharedDurableMatrixTests` and `SharedEnvelopeGoldensTests.Preserves_nullable_timestamps_and_digests` verify defaults, casing, `fields`, unknown fields, null timestamps, newer read-only data, and exact bytes. |
| §5 Shared server and exact MCP surface | P3-T2 | `SharedToolCountTests.Registers_exactly_eleven_prefixed_methods_and_no_forbidden_methods`. |
| §5.1 Common schema vocabulary and limits | P3-T2 | `SharedSchemaTests.Schemas_reject_unknown_properties_and_enforce_all_ranges_defaults_and_conditional_facts_arguments` and `SharedEphemeralRefusalTests`. |
| §5.2 Eleven standalone tool schemas | P3-T2 | Source-generated schema goldens for all eleven tools; reflected public method count is exactly eleven and shared skill operations have no built-in fallback. |
| §5.3 Aggregation, provenance, and exact envelopes | P3-T5 | `SharedEnvelopeGoldensTests.Serializes_recall_mutation_refresh_partial_and_zero_success_error_exactly` plus `SharedErrorPrecedenceTests.Omits_rejected_base_and_mutation_path`. |
| §6 Freshness, operations, and refresh | P3-T7 | `SharedFreshnessTests` covers leases, F0 stale refusal, post-write invalidation, `unknown`, blocked writes, atomic refresh, and the documented unsupported F0→F1 race. |
| §6.1 Open and snapshots | P3-T6 | Durable matrix/conflict tests verify ordered validation, inventory, conflict scan, format validation, fingerprint, and stable snapshot publication. |
| §6.2 Per-base operation gate | P3-T7 | `SharedFreshnessTests` covers serialized mutation, refresh exclusion, pre-write revalidation, and `none` versus `unknown`. |
| §6.3 Explicit refresh | P3-T7 | `SharedFreshnessTests.Refresh_swaps_snapshot_and_fingerprint_atomically` and refresh failure/warning cases. |
| §7 Git boundary and prompt advisory transport | P4-T6 | P4 gate checks hint-only hook transport while P3’s final gate checks no Git process; no phase claims Git state. |
| §7.1 Categorical prohibition | P1-T4 | `SetupMergeRetirementTests` and P1 repository-wide process scan; only filesystem `.git` evidence remains. |
| §7.2 Authoritative mutation manifest and Git guidance | P3-T8 | `SharedManifestTests.Manifest_contains_only_exact_authoritative_files_for_each_mutation` and `SharedGitGuidanceTests.Git_guidance_is_operation_based_and_shell_quotes_each_manifest_path`. |
| §7.3 Prompt transport | P4-T1 | `PromptHintTransportTests` covers positional/stdin, 65,536/65,537 bytes, strict UTF-8, timeout, JSON, NFKC, tokens, negation, near misses, and silence boundaries. |
| §8 Workspace tracking and migration | P2-T6 | `LegacyMigrationStateMachineTests` and `LegacyMigrationErrorTests` cover all states, transitions, exact cleanup, precedence, rollback, and `UNVERIFIED`. |
| §8.1 Configuration CLI | P2-T7 | `SharedBaseTransactionTests`, `SharedEnableTests`, and `SharedConfigJsonOutputTests` cover add/remove/list, enable/disable, atomicity, ordering, and idempotency. |
| §8.1.1 Exhaustive CLI failures | P2-T8 | `CliErrorPrecedenceTests` covers every declared command/code/precedence/exit-class row, including `LEGACY_MIGRATION_REQUIRED`, `CONFIG_WRITE_FAILED`, and `IO_FAILURE`. |
| §8.2 Policies and ignore files | P2-T5 | `ManagedIgnoreBlockTests` and `DerivedClassifierEquivalenceTests` cover exact bytes, marker ownership, drift refusal, overlap, and static cleanup. |
| §8.3 Legacy migration state machine | P2-T6 | `LegacyMigrationStateMachineTests` proves recognized legacy state requires explicit migration and `tracking set` performs no write. |
| §9 Merge-subsystem retirement | P1-T2/T5 | `MergeRetirementAbsenceTests`, `ReconcileRemovalTests`, and `PackagingAndDocsMergeRetirementTests`; P1 gate preserves dated history and leaves `docs/multi-user-setup.md` for P4. |
| §10 Packaging, models, and plugins | P5-T8 | `PackageContentTests`, `ModelRelocationTests`, `PluginDiscoveryDiagnosticTests`, and `GlobalToolInstallTests`; P5 gate checks immutable package and both handshakes. |
| §10.1–§10.3 Packaging acceptance | P5-T4/T5 | Package allowlist/denylist plus isolated local-feed lifecycle test: install, version, both servers, restart, update, resource lookup, and uninstall. |
| §11 Stable error and side-effect contract | P3-T5 | `SharedErrorPrecedenceTests` and envelope goldens verify stable codes, safe diagnostics, no error result DTO, and `applied|none|unknown`. |
| §12 Security and privacy | P3-T4 | `SharedPrivacyTests`, containment/root tests, no-Git tests, and `SharedFreshnessTests.Documents_F0_to_F1_race_as_unsupported_not_prevented`. |
| §13 Delivery phases and boundaries | P1-T6 | This matrix, all five plan ownership sections, handoff gates, and P1 historical-hash gate; status remains pending. |
| §13.1 Exclusive artifact ownership | P4-T5 | `OnboardingOwnershipTests` checks `multi-user-setup.md`, troubleshooting headings, `WorkspaceSetup` regions, and P4/P5 non-overlap. |
| §13.5 Phase 5 boundary | P5-T7 | `PluginDiscoveryDiagnosticTests` plus stale-path scan over living docs/source/help; dated planning/review history is exempt. |
| §14 Acceptance-test matrix | P3-T12 | The 14-item matrix below and each named phase gate; no item is treated as evidenced without its named test output. |
| §15 Decision ledger and review trace | P4-T5 | `OnboardingOwnershipTests` and citation/link validation; source labels and pending status remain explicit. |
| §16 Historical-document policy | P1-T5 | SHA-256 preservation manifest for dated `docs/superpowers/**`, `docs/planning/**`, `docs/reviews/**`, and released changelog entries. |
| §17 Spec self-review | P5-T8 | Cross-phase final scan for placeholders, empty-object payloads, modal wording, schema mismatches, undeclared codes, contradictions, stale paths, and approval claims. |

## Acceptance-item matrix — all 14

Each item has exactly one owning task and one named gate.

| # | Acceptance item | Owner | Named test / exit gate |
|---:|---|---|---|
| 1 | Exactly eleven shared MCP tool names after reconcile removal | P3-T2 | `SharedToolCountTests.Registers_exactly_eleven_prefixed_methods_and_no_forbidden_methods`. |
| 2 | One shared base path via `SCRINIA_HOME` | P2-T1 | `UserHomeResolverTests` and `HomeConfigSerializationTests`; P5 consumes the resolver unchanged. |
| 3 | One name→description→schema→error-code mapping | P3-T5 | `SharedEnvelopeGoldensTests` plus `SharedSchemaTests` and `SharedErrorPrecedenceTests`. |
| 4 | Commit/push intent advisory parser | P4-T1 | `PromptHintTransportTests` full boundary table; P4-T6 confirms fail-silent exit 0. |
| 5 | OpenCode onboarding invokes `hint --client opencode` | P4-T4 | `PluginOnboardingTemplateTests` proves `scrinia.ts` contains `hint` and no `scri save`. |
| 6 | No merge driver, reconcile, or sidecar merges | P1-T3 | `ReconcileRemovalTests`, `MergeRetirementAbsenceTests`, and P1 process/package/help scans. |
| 7 | `config.json` is the single workspace-metadata source | P2-T2 | `WorkspaceConfigScalarTests` and `McpCoreReferenceTests`; flat workspace dictionary remains distinct from typed home config. |
| 8 | Existing `scratch:` migration to `/temp` | P1-T6 | P1 exit gate verifies `/temp/scratch` migration and ephemeral-addressing regression tests; no merge-scratch class remains. |
| 9 | `config.json` backward compatibility | P2-T3 | `AtomicConfigWriteTests` and `HomeConfigParserValidationTests`; unrelated flat keys survive and malformed/newer home config fails closed. |
| 10 | Shared-runtime migration entry | P3-T11 | `NoBaseStartupTests`, shared command registration, `SharedNoAdapterTests`, and the P3 no-adapter exit gate. |
| 11 | `{exeDir}/` to `SCRINIA_HOME` base-path migration | P5-T1/T3 | `ModelRelocationTests` covers models, plugins, one-time migration, containment, and no package-directory writes. |
| 12 | No per-executable local state | P5-T5 | `GlobalToolInstallTests` immutable package-tree snapshot and isolated `SCRINIA_HOME` asset assertions. |
| 13 | CLI and MCP server expose identical contracts | P3-T5 | `SharedEnvelopeGoldensTests`, `SharedSchemaTests`, `McpCoreReferenceTests`, and local/shared disjointness tests. |
| 14 | Living docs updated for the global tool | P5-T7 | Exact-heading documentation tests, package-readme checks, and stale `{exeDir}/models`/`{exeDir}/plugins` scan. |

## Review-ruling citations

| Ruling | Ownership or verification consequence |
|---|---|
| R1-01, R3-04 | P3-T2/T5 owns standalone eleven-tool schemas and exact envelopes; no “same as local” or empty-object payload. |
| R1-02 | P2-T2 owns absent Enabled key as zero and final-disable removal; P3-T4 owns startup failure. |
| R1-03 | P4-T1/T2/T3 owns deterministic, fail-silent hint transport; shared MCP has no prompt/correlation field. |
| R2-01 | P3-T7 tests detectable drift and post-write invalidation and records the unsupported F0→F1 race without a prevention claim. |
| R2-02, R3-03 | P3-T6 owns UTF-8 authoritative marker scanning before parse, with only the ordered line-start triplet producing conflict. |
| R2-03, R4-01 | P1/P2/P3 remove Git process/index access; P2 owns fixed shared `project-git`, transactional registration, and static cleanup policy. |
| R3-01 | P3 owns final response wording and removes adapters; P4 consumes it unchanged; P5 owns only package/model/plugin subjects. |
| R3-02, R6-06 | The spec and review remain proposed/pending; source labels and links are cited without invented approval. |
| R3-05 | Every acceptance domain has boundary tests, ownership checks, and a final contradiction/placeholder scan. |
| R4-02, R4-03 | P3 owns error-stage/provenance envelopes; P2 owns exhaustive compatibility defaults and CLI error precedence. |
| R4-04 | P4 owns onboarding replacement; P5 owns plugin/global-tool/model/setup diagnostics and heading-disjoint documentation. |
| R5-01 | P3-T8 owns exact authoritative manifests; no broad store staging or pretracked derived path enters guidance. |
| R5-02, R6-03 | P2-T5 owns one ordered classifier and exact derived-set equivalence, including recursive temporary/lock trees. |
| R5-03 | P3-T6 hides corrupt memory entries with missing/null/blank/invalid identity using `SHARED_ENTRY_CORRUPT`. |
| R5-04, R6-04 | P2-T6 owns explicit legacy migration and every tracking transition cell; recognized legacy state is not silently accepted by `tracking set`. |
| R6-01 | P3-T5/T6 preserves nullable durable timestamps and adds serialization/YAML golden coverage. |
| R6-02 | P3-T3 refuses every parser-classified ephemeral path, destination, and prefix before lookup or I/O. |
| R6-05 | Existing registered-base policy drift is fail-closed; manual exact restoration followed by refresh/restart is guidance, not an owned repair command. |

## Verification checklist

- [ ] Verify the specification and review record still say `Proposed — reviewer approval pending`; do not add approval language.
- [ ] Verify each specification-section row and each acceptance item has one owner, one task, and one named gate.
- [ ] Verify P1’s active merge retirement and P2’s migration-only recognition allowlist are disjoint.
- [ ] Verify P2 produces Core `UserHome`/config/base interfaces and P3 consumes them without an executable reference; verify P5 consumes P2 `UserHome` unchanged.
- [ ] Verify P3’s final response/envelope/manifest contract is frozen before P4, and P4 does not edit shared response wording.
- [ ] Verify P4 owns `scrinia.ts` and `docs/multi-user-setup.md`; verify P5 owns plugin/model documentation and diagnostics only.
- [ ] Verify the disjoint `WorkspaceSetup.cs` regions and all named troubleshooting headings.
- [ ] Verify no living source/help/doc path contains `{exeDir}/models` or `{exeDir}/plugins`; dated planning/review history is excluded from that stale-path check.
- [ ] Verify named feature-test filters, each independent phase build/test gate, package allowlist/denylist, both server handshakes, and historical hashes when implementation begins.
- [ ] Record observed evidence only after tests produce it. This matrix records planned gates and makes no implementation-complete or approval claim.
