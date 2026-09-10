# Complete baseline-to-working-tree path disposition

Baseline: `90827cd4c65d1bb803a0d6d56a5208956ac39d4a`
Working tree status source: `verification/working-tree.status.txt`
Baseline diff source: `diff/baseline-to-working-tree.name-status.txt`

Every path reported by either source is listed below. Untracked paths, including `.agents/`, `.codex/`, the malformed filename, append production/tests, and `ProcessEnvironmentLock.cs`, are explicit rows. No content is deleted or silently omitted from this inventory.

## Counts

- **Phase A required: 302 rows** (284 implementation/contract rows + 18 append boundary rows)
- **user change preserved: 6 rows**
- **unrelated-but-retained: 40 rows** (15 existing branch/user rows + 25 intentional retirement rows)
- **evidence-only: 47 rows**
- **Total union: 395 rows; unique paths: 395; duplicate paths: 0**

The disposition subtotal is 303 + 6 + 40 + 46 = 395. The table below is the complete union:
every tracked or untracked source path appears exactly once.

## Dispositions

| Path | Baseline status | Working-tree status | Disposition | Basis |
|---|---|---|---|---|
| `"C\357\200\272SynologyDriveSourcereposworkspace-scriniaScrinia.superpowerssddphase-a-completiontask-5.2-exact.diff"` | `not-in-baseline` | `??` | evidence-only | Accidental evidence artifact corrected after commit; removed without affecting the canonical final-package diff. |
| `.agents/skills/ux-polish-review/SKILL.md` | `not-in-baseline` | `??` | user change preserved | Explicitly retained; not silently omitted from status/diff inventory. |
| `.claude/agent-memory/dotnet-engineering-expert/project_published_surface_coherence_closed.md` | `M` | `unchanged-in-status` | unrelated-but-retained | Existing branch/user content retained without narrowing scope. |
| `.codex/agents/documentation-guru.toml` | `not-in-baseline` | `??` | user change preserved | Explicitly retained; not silently omitted from status/diff inventory. |
| `.codex/agents/dotnet-engineering-expert.toml` | `not-in-baseline` | `??` | user change preserved | Explicitly retained; not silently omitted from status/diff inventory. |
| `.codex/agents/llm-tool-ergonomics-architect.toml` | `not-in-baseline` | `??` | user change preserved | Explicitly retained; not silently omitted from status/diff inventory. |
| `.codex/agents/ux-polish-reviewer.toml` | `not-in-baseline` | `??` | user change preserved | Explicitly retained; not silently omitted from status/diff inventory. |
| `.codex/config.toml` | `not-in-baseline` | `??` | user change preserved | Explicitly retained; not silently omitted from status/diff inventory. |
| `.github/workflows/checks.yml` | `A` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `.github/workflows/release.yml` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `.superpowers/sdd/2026-08-29-unified-memory-addressing-task-1/task-10-report.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `.superpowers/sdd/2026-08-29-unified-memory-addressing/task-7-report.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `AGENTS.md` | `M` | ` M` | unrelated-but-retained | Existing branch/user content retained without narrowing scope. |
| `CHANGELOG.md` | `M` | `unchanged-in-status` | unrelated-but-retained | Existing branch/user content retained without narrowing scope. |
| `README.md` | `M` | ` M` | unrelated-but-retained | Existing branch/user content retained without narrowing scope. |
| `Scrinia.sln` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/AgenticIngestHarness.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `benchmarks/Scrinia.Retrieval.Benchmarks/Core/AgenticScriniaRetriever.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `benchmarks/Scrinia.Retrieval.Benchmarks/Core/ScriniaRetriever.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `docs/architecture/cli.md` | `M` | ` M` | unrelated-but-retained | Existing branch/user content retained without narrowing scope. |
| `docs/architecture/core.md` | `M` | `unchanged-in-status` | unrelated-but-retained | Existing branch/user content retained without narrowing scope. |
| `docs/architecture/overview.md` | `M` | ` M` | unrelated-but-retained | Existing branch/user content retained without narrowing scope. |
| `docs/cli-reference.md` | `M` | ` M` | unrelated-but-retained | Existing branch/user content retained without narrowing scope. |
| `docs/getting-started.md` | `M` | `unchanged-in-status` | unrelated-but-retained | Existing branch/user content retained without narrowing scope. |
| `docs/installation.md` | `A` | `unchanged-in-status` | unrelated-but-retained | Existing branch/user content retained without narrowing scope. |
| `docs/multi-user-setup.md` | `M` | ` M` | unrelated-but-retained | Existing branch/user content retained without narrowing scope. |
| `docs/plugin-authoring.md` | `M` | `unchanged-in-status` | unrelated-but-retained | Existing branch/user content retained without narrowing scope. |
| `docs/reviews/ux-polish/2026-09-01-shared-memory-architecture.md` | `A` | `unchanged-in-status` | unrelated-but-retained | Existing branch/user content retained without narrowing scope. |
| `docs/security.md` | `M` | ` M` | unrelated-but-retained | Existing branch/user content retained without narrowing scope. |
| `docs/superpowers/briefs/2026-09-04-task8-shared-manifest-git-guidance.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/ledgers/2026-09-04-task11.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/ledgers/2026-09-04-task8-shared-manifest-git-guidance.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/ledgers/2026-09-04-task9-shared-classifier-registry.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/ledgers/2026-09-09-task-1.2.md` | `A` | `A ` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/phase-a-completion-task-1.2-deletion-audit.md` | `A` | `AM` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/plans/2026-09-01-config-tracking-shared-bases.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/plans/2026-09-01-global-tool-packaging.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/plans/2026-09-01-hints-and-onboarding.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/plans/2026-09-01-merge-retirement.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/plans/2026-09-01-shared-runtime-and-mcp.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/plans/2026-09-01-spec-coverage-matrix.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/plans/2026-09-06-wave-1-critical.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/plans/2026-09-06-wave-1-task-5-report.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/plans/2026-09-06-wave-1-task-6-report.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/plans/2026-09-06-wave-2-major.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/plans/2026-09-06-wave-3-minor.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/reports/2026-09-04-shared-runtime-task11-brief.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/reports/2026-09-04-shared-runtime-task9-brief.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/reports/2026-09-04-task8-shared-manifest-git-guidance.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/reports/2026-09-04-task9-shared-classifier-registry.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/reviews/2026-08-31-shared-memory-global-tool-spec-review.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/reviews/2026-09-04-shared-runtime-task11-review.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/specs/2026-08-31-shared-memory-global-tool-design.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/superpowers/specs/2026-09-06-review-remediation-design.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `docs/troubleshooting.md` | `M` | `unchanged-in-status` | unrelated-but-retained | Existing branch/user content retained without narrowing scope. |
| `failure-classification.md` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `publish.projects.tsv` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `publish.ps1` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `publish.sh` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/AppendContracts.cs` | `not-in-baseline` | `??` | Phase A required | Append hardening or its deterministic test/process boundary. |
| `src/Scrinia.Core/AppendJournal.cs` | `not-in-baseline` | `??` | Phase A required | Append hardening or its deterministic test/process boundary. |
| `src/Scrinia.Core/AppendTransaction.cs` | `not-in-baseline` | `??` | Phase A required | Append hardening or its deterministic test/process boundary. |
| `src/Scrinia.Core/AssemblyInfo.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Bundles/BundleFormatService.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Bundles/BundleLegacyKeys.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/CompositeEventSink.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Configuration/ConfigJsonContext.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Configuration/ConfigResults.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Configuration/ConfigurationLock.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Configuration/HomeConfig.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Configuration/HomeConfigJsonContext.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Configuration/ManagedIgnoreBlock.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Configuration/SharedBaseOperations.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Configuration/SharedBaseRegistry.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Configuration/SharedBaseRuntimeContracts.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Configuration/SharedClassifier.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Configuration/SharedManifest.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Configuration/TrackingPolicy.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Configuration/UserHome.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Configuration/WorkspaceConfig.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/DurableFile.cs` | `M` | ` M` | Phase A required | Append hardening or its deterministic test/process boundary. |
| `src/Scrinia.Core/DurableWriteException.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Embeddings/DownloadableAsset.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Embeddings/EmbeddingReindexer.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Embeddings/MemoryArtifactSource.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Embeddings/MemoryVectorIndexer.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Embeddings/Model2VecModelManager.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Embeddings/VectorStore.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Embeddings/VerifiedModelDownloader.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/EntityTypingEventHandler.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/FileLock.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/FileMemoryStore.cs` | `M` | ` M` | Phase A required | Append hardening or its deterministic test/process boundary. |
| `src/Scrinia.Core/Formats/DurableFormats.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Formats/ScopeReadOnlyException.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/ILocalStoreLayout.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/IMemoryEventSink.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/IMemoryStore.cs` | `M` | ` M` | Phase A required | Append hardening or its deterministic test/process boundary. |
| `src/Scrinia.Core/IPluginStore.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/MaintenanceEventSink.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/MemoryNaming.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/MemoryStoreAddressExtensions.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/MemoryStoreContext.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Merge/KeywordJaccard.cs` | `D` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Merge/SidecarAdmission.cs` | `D` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Merge/SidecarAttributeMerge.cs` | `D` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Merge/SidecarKeywordMerge.cs` | `D` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Merge/SidecarTermFrequencyMerge.cs` | `D` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Merge/SidecarTimestampMerge.cs` | `D` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Migration/LegacyMergeRecognition.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Migration/LegacyMigrationState.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Models/ArtifactEntry.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Models/EphemeralEntry.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Models/MemoryAddress.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Models/MemoryError.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Models/MemorySearchRequest.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Models/MemoryValidationOptions.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Models/MemoryWrite.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/PathContainment.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/RestoreJournal.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Scrinia.Core.csproj` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Search/ISearchScoreContributor.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Search/PluginPipeline.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Search/SearchPipeline.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/Search/SearchQuery.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Core/TextPreview.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Mcp/McpResponse.cs` | `M` | ` M` | Phase A required | Append hardening or its deterministic test/process boundary. |
| `src/Scrinia.Mcp/MemoryTools.Core.cs` | `M` | ` M` | Phase A required | Append hardening or its deterministic test/process boundary. |
| `src/Scrinia.Mcp/MemoryTools.Facts.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Mcp/MemoryTools.Skills.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Mcp/MemoryTools.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Mcp/Scrinia.Mcp.csproj` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Mcp/ScriniaMcpResources.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Mcp/ScriniaPrimer.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Mcp/Shared/SharedDtoContracts.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Mcp/Shared/SharedGitGuidance.cs` | `A` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Mcp/Shared/SharedServer.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Mcp/Shared/SharedTools.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Mcp/plugins/opencode/scrinia.ts` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Mcp/prompts/guide.md` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Mcp/skills/merge-safety.md` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `src/Scrinia.Merge/GitConfigRepair.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `src/Scrinia.Merge/KeywordJaccard.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `src/Scrinia.Merge/MergeConfig.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `src/Scrinia.Merge/MetaJsonMerger.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `src/Scrinia.Merge/Nmp2ConflictHandler.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `src/Scrinia.Merge/Program.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `src/Scrinia.Merge/Scrinia.Merge.csproj` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `src/Scrinia.Merge/SidecarAttributeMerge.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `src/Scrinia.Plugin.Abstractions/HookContexts.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Plugin.Embeddings.Cli/Program.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Plugin.Embeddings/Scrinia.Plugin.Embeddings.csproj` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia.Plugin.Embeddings/VulkanModelManager.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Commands/CliJsonContext.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Commands/ConfigJsonContext.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Commands/Hint.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Commands/IBoundedStdinReader.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Commands/PlatformQuoting.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Commands/PromptHintTransport.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Commands/ScriniaCommands.HintCommand.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Commands/ScriniaCommands.Memory.cs` | `M` | ` M` | Phase A required | Append hardening or its deterministic test/process boundary. |
| `src/Scrinia/Commands/ScriniaCommands.Migration.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Commands/ScriniaCommands.Setup.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Commands/ScriniaCommands.SharedServe.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Commands/ScriniaCommands.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Commands/SharedBaseCommands.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Commands/SharedEnableCommands.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Commands/SharedServeHostAdapter.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Commands/TrackingCommands.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Commands/WorkspaceConfig.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Commands/WorkspaceSetup.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Program.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Scrinia.csproj` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Services/CoreEmbeddingEventHandler.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Services/McpPluginHost.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/Services/PluginClientJsonContext.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `src/Scrinia/package-readme.md` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `task-1.2-report.md` | `A` | `AM` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `task-10-report.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `task-10-review-package.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `task-11-report.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `task-2-report.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `task-2.1-report.md` | `not-in-baseline` | `??` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `task-2.2-report.md` | `not-in-baseline` | `??` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `task-3-report.md` | `A` | ` M` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `task-3.2-report.md` | `not-in-baseline` | `??` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `task-4-report.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `task-4.1-report.md` | `not-in-baseline` | `??` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `task-4.2-report.md` | `not-in-baseline` | `??` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `task-4.3-report.md` | `not-in-baseline` | `??` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `task-5-report.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `task-5-review-package.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `task-5.2-report.md` | `not-in-baseline` | `??` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `task-6-report.md` | `A` | `unchanged-in-status` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `task-6.2-review-package.md` | `not-in-baseline` | `??` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `task-7-report.md` | `not-in-baseline` | `??` | evidence-only | Review/planning evidence retained; no production or test behavior disposition. |
| `tests/Scrinia.Benchmarks/HintReadPathBenchmarks.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Benchmarks/LoadIndexBenchmarks.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Benchmarks/WritePathBenchmarks.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Merge.Tests/ConflictDirContainmentTests.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `tests/Scrinia.Merge.Tests/GitMergeHarness.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `tests/Scrinia.Merge.Tests/GitMergeIntegrationTests.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `tests/Scrinia.Merge.Tests/KeywordJaccardParityTests.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `tests/Scrinia.Merge.Tests/KeywordJaccardTests.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `tests/Scrinia.Merge.Tests/MergeConfigTests.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `tests/Scrinia.Merge.Tests/MetaJsonMerger3WayTests.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `tests/Scrinia.Merge.Tests/MetaJsonMergerTests.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `tests/Scrinia.Merge.Tests/Nmp2ConflictHandlerTests.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `tests/Scrinia.Merge.Tests/SidecarAdmissionParityTests.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `tests/Scrinia.Merge.Tests/SidecarAttributeMergeParityTests.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `tests/Scrinia.Merge.Tests/SidecarKeywordMergeParityTests.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `tests/Scrinia.Merge.Tests/SidecarTermFrequencyMergeParityTests.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `tests/Scrinia.Merge.Tests/SidecarTimestampMergeParityTests.cs` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `tests/Scrinia.Merge.Tests/xunit.runner.json` | `D` | `unchanged-in-status` | unrelated-but-retained | Intentional merge retirement preserved as a current branch contract. |
| `tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingsPluginCliTests.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingsPluginIncrementalEmbedTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingsPluginInitTests.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingsPluginReindexTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingsPluginScoringTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Plugin.Embeddings.Tests/IndexerParityTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Plugin.Embeddings.Tests/ModelDownloadIntegrityTests.cs` | `A` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Plugin.Embeddings.Tests/PluginAddressPropagationTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticIngestHarnessTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Retrieval.Benchmarks.Tests/ScriniaRetrieverTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.TestDoubles/FakeMemoryStore.cs` | `M` | ` M` | Phase A required | Append hardening or its deterministic test/process boundary. |
| `tests/Scrinia.TestDoubles/ProcessEnvironmentLock.cs` | `not-in-baseline` | `??` | Phase A required | Append hardening or its deterministic test/process boundary. |
| `tests/Scrinia.TestDoubles/SilentEventSink.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/AllAddressSurfaceConformanceTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/AppendCleanupTests.cs` | `not-in-baseline` | `??` | Phase A required | Append hardening or its deterministic test/process boundary. |
| `tests/Scrinia.Tests/AppendContractTests.cs` | `not-in-baseline` | `??` | Phase A required | Append hardening or its deterministic test/process boundary. |
| `tests/Scrinia.Tests/AppendDurabilityTests.cs` | `not-in-baseline` | `??` | Phase A required | Append hardening or its deterministic test/process boundary. |
| `tests/Scrinia.Tests/AppendFacadeMappingTests.cs` | `not-in-baseline` | `??` | Phase A required | Append hardening or its deterministic test/process boundary. |
| `tests/Scrinia.Tests/AppendJournalTests.cs` | `not-in-baseline` | `??` | Phase A required | Append hardening or its deterministic test/process boundary. |
| `tests/Scrinia.Tests/AppendRecoveryTests.cs` | `not-in-baseline` | `??` | Phase A required | Append hardening or its deterministic test/process boundary. |
| `tests/Scrinia.Tests/AppendRollbackTests.cs` | `not-in-baseline` | `??` | Phase A required | Append hardening or its deterministic test/process boundary. |
| `tests/Scrinia.Tests/AppendTransactionTests.cs` | `not-in-baseline` | `??` | Phase A required | Append hardening or its deterministic test/process boundary. |
| `tests/Scrinia.Tests/ArtifactEntrySerializationTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/ArtifactKeyBoundaryTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/AtomicConfigWriteTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/AutoReviewWhenTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Benchmarks/ScriniaMemorySystem.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Benchmarks/UpdateCorrectnessBenchmarkTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/BundleArtifactKeyPortabilityTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/BundleExitCodeTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/BundleLegacyKeysTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/BundleRoundTripTotalityTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/BundleV2Tests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/CaseSensitiveMcpAppendTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/ChunkIndexingTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/CliAddressValidationTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/CliConfigConformanceTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/CliEphemeralRefusalTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/CliErrorPrecedenceTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/CliRecoveryBlockedRouteTests.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/CliReviewGapTests.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/CliSurfaceConformanceTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Commands/CommandLineQuotingTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/CompactCuratedKeywordTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/CompactNotificationTests.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/CompositeEventSinkTests.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/ConfigCommandDurabilityTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/ConsolidateReembedTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/ConsolidateSingleStoreTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/ContractTypingTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/CoreEmbeddingEventHandlerAppendTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/CoreEmbeddingEventHandlerEphemeralAppendTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/CoreSinkStoreTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/DerivedClassifierEquivalenceTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/DualModeParseTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/DurableFileFaultSeamTests.cs` | `not-in-baseline` | `??` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Embeddings/EmbeddingReindexerNamespacedScopeTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Embeddings/EmbeddingReindexerTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/EntityFactStorePersistenceTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/EntityTypingEventHandlerTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/EntityTypingWiringTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/EphemeralLifecycleTests.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/FileEntityNameTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/FileMemoryStoreTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/FileMemoryStoreWriteSurfaceTests.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Formats/FormatVersionTests.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Formats/SidecarVersioningTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/GlobalToolInstallTests.cs` | `A` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/GlobalUsings.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Golden/GoldenNormalizer.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Golden/ResponseGoldenTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Golden/__snapshots__/append.yaml` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Golden/__snapshots__/compact.yaml` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Golden/__snapshots__/error-missing-path.yaml` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Golden/__snapshots__/reconcile-scan.yaml` | `D` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/HintAdvisoryTransportTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/HintJsonOutputTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/HomeConfigParserValidationTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/HomeConfigSerializationTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/HookAdvisoryOnboardingTests.cs` | `A` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Hooks/OpenCodePluginWriterTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/LegacyMergeRecognitionTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/LegacyMigrationErrorTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/LegacyMigrationServiceTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/LegacyMigrationStateMachineTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/ListScopedCompletenessTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/MaintenanceEventSinkTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/ManagedIgnoreBlockTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Mcp/SkillPathRoutingTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Mcp/ToolFamilyConsistencyTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Mcp/TruncationAdviceTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/McpAddressSurfaceTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/McpCoreReferenceTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/McpPluginHostNegotiationTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/McpRecoveryBlockedRouteTests.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/MemoryAddressTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/MemoryEventSinkContextInstallDefaultTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/MemoryWriteValidationTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/MergeConfigRoundTripTests.cs` | `D` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/MergeRetirementAbsenceTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/MetaMergeSymmetricParityTests.cs` | `D` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/ModelRelocationTests.cs` | `A` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Naming/AddressSurfaceCoverageTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/NoObsoleteAddressSurfaceTests.cs` | `A` | `M ` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/NoSharedEnabledEnvTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/OnboardingOwnershipTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/PackageContentTests.cs` | `A` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/PackagingAndDocsMergeRetirementTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/PluginAddressPropagationTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/PluginDiscoveryDiagnosticTests.cs` | `A` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/PluginOnboardingTemplateTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/PluginStoreContractTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/PluginStoreOnlySinkTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/PromptHintTransportTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/PublishManifestTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/ReconcileConflictCopyTests.cs` | `D` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/ReconcileDurableFormatTests.cs` | `D` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/ReconcileMetaProvenanceTests.cs` | `D` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/ReconcileRemovalTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/ReconcileScanTests.cs` | `D` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/ReferenceTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/RememberWriteMetadataTests.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/ResponseStatusIsolationTests.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Scrinia.Tests.csproj` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/ScriniaMcpPromptsResourcesTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/SetupMergeRetirementTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/McpCoreReferenceTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/NoBaseStartupTests.cs` | `A` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedBaseRegistryTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedCacheIsolationTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedClassifierEquivalenceTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedConflictMarkerTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedDurableMatrixTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedEnvelopeGoldensTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedEphemeralRefusalTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedErrorPrecedenceTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedFreshnessTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedGitGuidanceTests.cs` | `A` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedLocalDisjointnessTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedManifestTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedNoAdapterTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedNoGitProcessTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedNoPromptCorrelationTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedPhase3ExitGateTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedPrivacyTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedSchemaTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedSelectorProvenanceTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Shared/SharedToolCountTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/SharedBasePrivacyTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/SharedBaseRegistryTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/SharedBaseTransactionTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/SharedConfigJsonOutputTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/SharedEnableTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/SourceGeneratedAddressSerializationTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Store/IndexCacheWritePathTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Store/JournaledRestoreRecoveryTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/StorePurityInvariantTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/TestHelpers.cs` | `M` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/TrackingPrecedenceTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/TrackingSetTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/UserHomeResolverTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/V2PathSearchTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Wave1/AsyncLocalBridgeTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Wave1/Crc32GoldenTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Wave1/FileLockRetryClassificationTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Wave1/LoadIndexImmutabilityTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Wave1/McpToolSurfaceTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Wave1/VectorStoreTraversalTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Wave2/BundleImportInvalidationTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Wave2/RetryHelperBehaviorTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Wave2/SearchAllSinglePathTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Wave2/Wave2RemainingTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Wave2/WorkspaceRootSevenSitesTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/Wave3/Wave3HygieneTests.cs` | `A` | ` M` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/WorkspaceConfigMutationTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/WorkspaceConfigScalarTests.cs` | `A` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
| `tests/Scrinia.Tests/WriterCutoverTests.cs` | `M` | `unchanged-in-status` | Phase A required | Tracked baseline-to-working-tree Phase A implementation, contract, harness, or acceptance surface. |
