# WorkspaceSetup Split — Completion Report

**Date:** 2026-09-20
**Spec:** docs/superpowers/specs/2026-09-20-workspace-setup-split-design.md
**Plan:** docs/superpowers/plans/2026-09-20-workspace-setup-split.md
**Review item:** E — "WorkspaceSetup god object" (intuitive type names + responsibilities mixed)

## Summary
Split the ~862-line `WorkspaceSetup` static CLI composition root into a thin workspace-identity
root plus four focused static helpers, each with one clear responsibility. No public API, wire
format, or call-site call shape changed — all moved members stayed `internal static`.

## What changed

### New types
| Type | Assembly | Responsibility | Lines |
| ---- | -------- | -------------- | ----- |
| `StoreFactory` | `Scrinia.Core` | `FileMemoryStore` construction (single- + participant-adapter overloads) | 20 |
| `EmbeddingsHost` | `Scrinia.Commands` | Embeddings/plugins lifecycle: provider selection, Vulkan plugin, reindex, sinks | ~600 |
| `OrphanStoreReporter` | `Scrinia.Commands` | Orphan-store detection + migration-warning text | ~141 |
| `MaintenanceSchedulerHost` | `Scrinia.Commands` | Bounded background maintenance scheduler lifecycle | ~60 |

### `WorkspaceSetup.cs`: 862 → 96 lines
Retained only: `Root`, `OverrideRoot`, `Store`, `Configure`, `GetConfigValue`, `GetPluginName`, and
the `StoreFactory.Create(...)` call inside `Configure`.

### Signature changes (explicit root injection)
- `LoadPluginsAsync(string workspaceRoot, CancellationToken ct = default)`
- `ForceReindexAsync(string workspaceRoot, CancellationToken ct = default)`
- `TryLoadVulkanPluginAsync(string workspaceRoot, CancellationToken ct = default)`
- `WarnOnOrphanStores(string workspaceRoot)`
- `StartMaintenanceScheduler(string workspaceRoot)`

### Call-site migration (~20 sites across 6 command files)
`WorkspaceSetup.LoadPluginsAsync(ct)` → `EmbeddingsHost.LoadPluginsAsync(WorkspaceSetup.Root, ct)`, etc.
`WorkspaceSetup.GetConfigValue` / `GetPluginName` / `Configure` / `Root` / `Store` untouched.

## Verification
- `dotnet build Scrinia.sln`: 13 projects, **0 errors**.
- `dotnet test Scrinia.sln`: **3995 passed, 0 failed, 20 skipped** (20 are expected Model2Vec/embeddings skips).
- New tests added: `StoreFactoryTests` (2), `EmbeddingsHostTests` (3), `OrphanStoreReporterTests` (2),
  `MaintenanceSchedulerHostTests` (2). One pre-existing source-invariant test
  (`MaintenanceSchedulerTests.WorkspaceSetup_OwnsSchedulerSetupWithoutDrain`) was updated to point at
  `MaintenanceSchedulerHost.cs` since the scheduler moved.

## Commits
- `42bea3fe` core: extract StoreFactory from WorkspaceSetup Configure
- `b8694417` commands: extract embeddings cluster from WorkspaceSetup into EmbeddingsHost
- `68d1ec58` commands: extract OrphanStoreReporter from WorkspaceSetup
- `3762059b` commands: extract MaintenanceSchedulerHost from WorkspaceSetup
- `52b9c849` test: update maintenance scheduler source-invariant to MaintenanceSchedulerHost

## Deviations from plan
- `TryLoadVulkanPluginAsync` parameter order fixed to `(string workspaceRoot, CancellationToken ct)` to
  avoid CA1068 ("CancellationToken must be last"); the plan's suggested `(ct, workspaceRoot)` order
  would have broken the build.
- `IsHttpEmbeddingsProvider` / `IsEmbeddingsPluginInstalled` promoted `private` → `internal` in
  `EmbeddingsHost` for unit-testability (InternalsVisibleTo is already configured).
- `MaintenanceSchedulerHost` uses the codebase's established
  `using CoreWorkspaceConfig = Scrinia.Core.Configuration.WorkspaceConfig;` alias (matches
  `ScriniaCommands.Maintenance.cs` etc.) rather than a bare `using`.

## Held out (per prior decision)
- **H** — Rename `ServiceRegistry` facade (cosmetic, touches published MCP surface). Not done.
