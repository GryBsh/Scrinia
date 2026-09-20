# WorkspaceSetup Split — Design Spec

> Design approved 2026-09-20. Implementation plan authored from this spec.

## Goal
Split the `WorkspaceSetup` static facade (the CLI command composition root in `src/Scrinia/Commands/WorkspaceSetup.cs`) into a thin workspace-identity root plus focused static helpers, so each type has one responsibility and is independently testable — without changing any public API, wire format, or the ~100 call sites' call shape.

## Context
`WorkspaceSetup` is a static facade (~862 lines) that currently holds five unrelated concerns behind one namespace. The design review flagged it as a "god object" (intuitive type names + responsibilities mixed). It is the CLI command composition root: every command calls `WorkspaceSetup.Configure(root)` at entry, reads `WorkspaceSetup.Root` / `WorkspaceSetup.Store`, and on the serve path calls `LoadPluginsAsync`, `StartMaintenanceScheduler`, `WarnOnOrphanStores`, `OverrideEmbeddingProviderForTest`.

Current concern breakdown:
1. **Workspace identity** — `Root`, `OverrideRoot`, `Store`, `Configure(string?)`, `Configure(string?, IMultiMemoryParticipantAdapterFactory?)`. Owns `_root` (process-wide root cache) and the `MemoryStoreContext` install.
2. **Embeddings + plugins** (~550 lines) — `LoadPluginsAsync`, `BuildEmbeddingOptions`, `LoadExplicitHttpEmbeddings`, `LoadBuiltInEmbeddings`, `MaybeReindexAfterModelSwitch`, `ForceReindexAsync`, `InstallEmbeddingEventSinks`, `TryLoadVulkanPluginAsync`, `ResolveEmbeddingsPluginExe`, `ResolvePluginDirectory`, `SelectCompatibleVersion`, `SelectRidDirectory`, `IsHttpEmbeddingsProvider`, `IsEmbeddingsPluginInstalled`, `GetPluginName`. Holds plugin runtime state `_pluginHost`, `_embeddingProvider`, the `AsyncLocal<EmbeddingProviderOverride?> _embeddingProviderOverride`, and the test-only `OverrideEmbeddingProviderForTest` + nested `EmbeddingProviderOverride`/`RestoreEmbeddingProviderOverride` records.
3. **Orphan-store warnings** — `WarnOnOrphanStores`, `FormatOrphanWarning`, `ListTopics`, `s_legacyNamespaceDirs`.
4. **Maintenance scheduler** — `StartMaintenanceScheduler`, `StopMaintenanceSchedulerAsync`, `ActiveMaintenanceScheduler` (owns the singleton `MaintenanceScheduler?` field).
5. **Config access** — `GetConfigValue` (2 overloads).

## Design

### Keep: `WorkspaceSetup` (thin composition root)
After the split this type retains ONLY:
- `Root` / `OverrideRoot`
- `Store`
- `Configure(string?)` / `Configure(string?, IMultiMemoryParticipantAdapterFactory?)` — now delegates store construction to `StoreFactory.Create(...)` and no longer builds `FileMemoryStore` inline.
- `GetConfigValue` / `GetPluginName` (config seam — stays here because it is the universal config accessor every concern shares; extracting it would fan out to every helper).

`Configure` becomes pure wiring: resolve root, build `RankerOptions` via `GetConfigValue`, create store via `StoreFactory`, install into `MemoryStoreContext.Current` and `MemoryStoreContext.Default`.

### Extract: `Scrinia.Core.StoreFactory` (static)
`internal static class StoreFactory` in Core, next to `FileMemoryStore`.
- `Create(storeRoot: string, RankerOptions rankerOptions, IMultiMemoryParticipantAdapterFactory? participantAdapterFactory = null) => FileMemoryStore`
Tested in `Scrinia.Tests` via `MemoryStoreContext` install parity.

### Extract: `EmbeddingsHost` (static)
`internal static class EmbeddingsHost` in `src/Scrinia/Commands/` (same namespace as WorkspaceSetup, stays in the CLI assembly — it references `McpPluginHost`, `EntityGraphScoreContributor`, `EmbeddingProviderFactory`, `VectorStore`, `HybridReranker`, `CoreEmbeddingEventHandler`, `EntityTypingEventHandler`, `MaintenanceEventSink`, `CompositeSearchScoreContributor`, `PathSegmentScoreContributor`, all CLI/assembly-local).
- Moves every embeddings/plugins member verbatim from `WorkspaceSetup`: the full list above, plus `_pluginHost`, `_embeddingProvider`, `_embeddingProviderOverride`, `OverrideEmbeddingProviderForTest`, nested override records, `ResolvePluginDirectory`, `PluginDirectoryResult`, `IsSafeSegment`, `CurrentRid`, `SelectCompatibleVersion`, `SelectRidDirectory`, `BuildEmbeddingOptions`, `IsHttpEmbeddingsProvider`.
- **New signature:** `LoadPluginsAsync` gains a leading `string workspaceRoot` parameter (was `Root`); `ForceReindexAsync` gains `string workspaceRoot`; `MaybeReindexAfterModelSwitch` already takes what it needs. `TryLoadVulkanPluginAsync` uses the passed `workspaceRoot` for `dataDir` (`Path.Combine(workspaceRoot, ".scrinia` instead of `Root`.
- Test-only `OverrideEmbeddingProviderForTest` moves to `EmbeddingsHost` (renamed `EmbeddingsHost.OverrideEmbeddingProviderForTest`), and the call site in `ScriniaCommands.Maintenance.cs:90` updates accordingly.
- `PluginDirectoryResult` and `ResolvePluginDirectory` become `internal static` on `EmbeddingsHost`; `WorkspaceSetup.ResolveEmbeddingsPluginExe` becomes `EmbeddingsHost.ResolveEmbeddingsPluginExe`.

### Extract: `OrphanStoreReporter` (static)
`internal static class OrphanStoreReporter` in `src/Scrinia/Commands/`.
- Moves `WarnOnOrphanStores`, `FormatOrphanWarning`, `ListTopics`, `s_legacyNamespaceDirs`.
- **New signature:** `WarnOnOrphanStores(string workspaceRoot)` (reads `Directory.GetCurrentDirectory()` for the orphan scan as before, uses `workspaceRoot` for `Root` in the warning text).

### Extract: `MaintenanceSchedulerHost` (static)
`internal static class MaintenanceSchedulerHost` in `src/Scrinia/Commands/`.
- Moves `StartMaintenanceScheduler`, `StopMaintenanceSchedulerAsync`, `ActiveMaintenanceScheduler`, and the `MaintenanceScheduler? _maintenanceScheduler` field.
- **New signature:** `StartMaintenanceScheduler(string workspaceRoot)` (loads `WorkspaceConfig.Load(workspaceRoot)` instead of `Root`).
- `StopMaintenanceSchedulerAsync()` and `ActiveMaintenanceScheduler` unchanged.

### Call-site churn (mechanical, ~20 sites across 6 command files)
- `WorkspaceSetup.LoadPluginsAsync(cancellationToken)` → `EmbeddingsHost.LoadPluginsAsync(WorkspaceSetup.Root, cancellationToken)`.
- `WorkspaceSetup.WarnOnOrphanStores()` → `OrphanStoreReporter.WarnOnOrphanStores(WorkspaceSetup.Root)`.
- `WorkspaceSetup.StartMaintenanceScheduler()` → `MaintenanceSchedulerHost.StartMaintenanceScheduler(WorkspaceSetup.Root)`.
- `WorkspaceSetup.StopMaintenanceSchedulerAsync()` → `MaintenanceSchedulerHost.StopMaintenanceSchedulerAsync()`.
- `WorkspaceSetup.ForceReindexAsync(ct)` → `EmbeddingsHost.ForceReindexAsync(WorkspaceSetup.Root, ct)`.
- `WorkspaceSetup.MaybeReindexAfterModelSwitch(...)` → `EmbeddingsHost.MaybeReindexAfterModelSwitch(...)`.
- `WorkspaceSetup.ResolveEmbeddingsPluginExe()` → `EmbeddingsHost.ResolveEmbeddingsPluginExe()`.
- `WorkspaceSetup.GetPluginName(...)` → unchanged (stays in WorkspaceSetup).
- `WorkspaceSetup.OverrideEmbeddingProviderForTest(...)` (ScriniaCommands.Maintenance.cs:90) → `EmbeddingsHost.OverrideEmbeddingProviderForTest(...)`.
- `WorkspaceSetup.Configure(...)` → unchanged (stays in WorkspaceSetup).
- `WorkspaceSetup.Store` / `WorkspaceSetup.Root` → unchanged.
- `new FileMemoryStore(...)` inline in `Configure` → `StoreFactory.Create(...)` (only inside WorkspaceSetup.Configure).

### Cross-assembly note
No `using` changes needed for the new types — they live in `Scrinia.Commands` (same namespace) or `Scrinia.Core` (`StoreFactory`). Existing `using Scrinia.Core;` in the command files already covers `StoreFactory`.

## Testing
- `dotnet test Scrinia.sln` must stay green (3681+ core tests today).
- Add focused unit tests in `tests/Scrinia.Tests/`:
  - `StoreFactoryTests` — store construction parity (root, ranker wiring, participant-adapter path).
  - `EmbeddingsHostTests` — provider selection via `OverrideEmbeddingProviderForTest`, plugin-exe resolution.
  - `OrphanStoreReporterTests` — `ListTopics` legacy-namespace classification, `FormatOrphanWarning` wording.
  - `MaintenanceSchedulerHostTests` — start/stop lifecycle.
-
