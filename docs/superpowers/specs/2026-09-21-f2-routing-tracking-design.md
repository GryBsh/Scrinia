# F2 Routing/Tracking Design Spec (Approach A: Sunset Shim then Break, with Auto-Migrate on Touch)

> Design approved 2026-09-21. Approach A (sunset shim then break), revised with auto-migrate on touch. NO warning log.

## Context

F2 (routing/tracking) owns two coupled surfaces in `Scrinia.Core`:

- **Routing:** `MemoryNaming`, `PathParser`, `PathRouter`, `MemoryAddress` — construction vs. resolution of store dirs and file-entity paths, plus the legacy `local-topic:` read-compat branch.
- **Tracking:** `EffectivePolicy` / Inspect / Migrate / Set — resolving the effective tracking policy from environment overrides vs. the workspace file value, and the `tracking migrate-legacy` sweep.

Current problems: construction and resolution are split across multiple authorities (callers can build paths without going through the resolver, and vice versa); tracking precedence is spread across Inspect/Migrate/Set with env-awareness backed ad hoc by `WorkspaceSetup.GetConfigValue`; `/agent/` file-entity semantics diverge from `/skill/` semantics (disk-miss + legacy `local:agent` hit is a dead end instead of a promotion).

## Decision

**Approach A: sunset shim then break, with auto-migrate on touch — NO warning log.**

- Keep the `local-topic:` branch as a read-only recovery-compat shim routed through the single authority, marked `Obsolete`, then break (remove) it at the sunset date.
- `/agent/` is unified to `/skill/` semantics: on disk-miss + legacy `local:agent` hit, auto-promote in two ordered steps — (1) `FileEntityTransactionCoordinator.ExecuteAsync(new FileEntityMutation(...))` for file + sidecar publish only, (2) on `Created` / `Replaced` only delete legacy `local:agent` NMP/2 entry via the store; on `RecoveryRequired` / throw leave legacy intact. Idempotent. No warning log beyond the existing migration trace.
- Tracking precedence is centralized in a new `EffectivePolicy.ResolveEffective(envSingle, envDouble, fileValue)` with a single-underscore-wins rule, consumed by env-aware Inspect/Migrate/Set.
- The approved breaking legacy removal is noted in `CHANGELOG.md` Unreleased / Breaking section.

Alternatives rejected: F1 (freeze legacy forever), F3 (eager bulk rewrite on startup), F4 (dual-write). See Out of Scope.

---

## 1. Single Authority: Construction vs. Resolution

### Architecture

- `MemoryNaming.TryBuild` is the sole constructor: every memory path is constructed through it. No caller builds store dirs or file-entity paths by string concatenation.
- `PathRouter.ToStoreDir` / `PathRouter.TryToFileEntityPath` is the sole resolver: every store-dir or file-entity-path lookup goes through it.
- `PathParser` becomes store-internal (parses persisted layout, never constructs new addresses).
- `MemoryAddress` becomes facade-boundary (public address shape, delegates construction to `TryBuild`).
- The `local-topic:` branch is kept read-only, marked `Obsolete` (recovery-compat only), and routed via `TryBuild` — never via a parallel code path.
- Rename `Reserved` to `ReservedTopicPrefixes` (pure rename, no behavior change).
- Existing colon guards and `Shared:Enabled` env-ignore behavior stay unchanged.

### Components

| Component | Role after change |
|---|---|
| `MemoryNaming.TryBuild` | Sole constructor for memory paths |
| `PathRouter.ToStoreDir` / `TryToFileEntityPath` | Sole resolver for store dirs / file-entity paths |
| `PathParser` | Store-internal parser (no construction) |
| `MemoryAddress` | Facade-boundary address (calls `TryBuild`) |
| `local-topic:` branch | Read-only, `Obsolete`, recovery-compat, routed via `TryBuild` |
| `ReservedTopicPrefixes` (renamed from `Reserved`) | Same set, same semantics, clearer name |

### Data flow

1. Caller supplies a logical memory path.
2. `MemoryNaming.TryBuild` constructs the canonical address (including the `Obsolete` `local-topic:` recovery-compat branch when the input is a legacy journal entry).
3. `PathRouter.ToStoreDir` / `TryToFileEntityPath` resolves the address to a store dir / file-entity path.
4. `PathParser` (store-internal) and `MemoryAddress` (facade) both delegate to `TryBuild`; neither constructs independently.

### Error handling

- `TryBuild` returns false (no throw) for unparseable input; callers map to `Unknown` scope.
- `TryToFileEntityPath` returns false for non-file entities; callers fall through to NMP/2 artifact handling.
- The `local-topic:` shim is read-only: writes through it are rejected (mapped to `Unknown` / invalid), reads succeed for recovery only.
- Sunset expiry (see Section 4): after the sunset date the shim is removed and legacy inputs resolve to `Unknown` scope.

### Testing

- Recovery-compat test: legacy `local-topic:` journal entries still resolve (read-only) before sunset.
- Sunset-expiry test: after shim removal, the same inputs resolve to `Unknown` scope (test flips).
- Rename test: `ReservedTopicPrefixes` carries the identical set (no behavior change).
- Colon-guard and `Shared:Enabled` env-ignore tests stay green unchanged.

---

## 2. Tracking: Centralized Effective-Policy Resolution

### Architecture

- New `EffectivePolicy.ResolveEffective(envSingle, envDouble, fileValue)` is the single precedence function for tracking policy.
  - Exact spellings: `envSingle` = `SCRINIA_WORKSPACE_TRACKING`, `envDouble` = `SCRINIA__WORKSPACE__TRACKING`, `fileValue` = `Scrinia:Workspace:Tracking`.
- Inspect / Migrate / Set accept an `ITrackingPolicyStore` that is env-aware, backed by `WorkspaceSetup.GetConfigValue` for file reads. Note: `ITrackingPolicyStore` is a new env-aware seam — the current `TrackingPolicyStore` is file-only.
- Precedence per `WorkspaceSetup.GetConfigValue`: single-underscore environment wins over double-underscore environment, which wins over the workspace file value, which wins over the default `Unset`.
- `Shared:Enabled` environment spellings remain ignored (workspace-only), unchanged.

### Components

| Component | Role after change |
|---|---|
| `EffectivePolicy.ResolveEffective(envSingle, envDouble, fileValue)` | Sole precedence function (new) |
| `ITrackingPolicyStore` (env-aware) | Config seam consumed by Inspect / Migrate / Set |
| `WorkspaceSetup.GetConfigValue` | File-value backing for the store |
| Inspect / Migrate / Set | Thin consumers; no inline precedence logic |

### Data flow

1. Inspect / Migrate / Set query `ITrackingPolicyStore` for `(envSingle, envDouble, fileValue)`.
2. `EffectivePolicy.ResolveEffective` applies single-underscore-wins precedence and returns the effective policy.
3. Callers act on the effective policy (display, migrate, or persist the file value).

### Error handling

- Missing env vars and missing file value resolve to the default `Unset` (no error).
- Malformed file value throws `ScriniaConfigurationException` via `WorkspaceConfig.ParseTracking`; malformed env value throws the same exception for consistency (explicit decision: throw, not silent fallback).
- Config mutations keep the existing interprocess lock + same-directory `DurableFile` replacement; `CONFIG_RECOVERY_REQUIRED` on failed compensation/recovery, unchanged.

### Testing

- New Inspect-level precedence test: single-underscore beats double-underscore beats file beats default (via `TestHelpers.StoreScope`).
- Existing tracking-status / tracking-set / migrate-legacy tests stay green against the new seam.

---

## 3. File Entities (REVISED): `/agent/` Unified to `/skill/` Semantics with Auto-Promote on Touch

### Architecture

- `/agent/` is unified to `/skill/` semantics: same disk layout contract (file + sidecar meta via `ReadSidecarMeta<T>` / `WriteSidecarMeta<T>`), same lookup chain (disk first, then legacy NMP/2 entry, then embedded built-in where applicable).
- On disk-miss + legacy `local:agent` hit, auto-promote in two ordered steps: (1) `FileEntityTransactionCoordinator.ExecuteAsync(new FileEntityMutation(...))` publishes file + sidecar only, (2) on `Created`/`Replaced` only delete the legacy `local:agent` NMP/2 entry via the store; on `RecoveryRequired` / throw leave legacy intact. Idempotent — a second read finds the disk file and skips promotion; a crash between steps re-derives the same bytes from the surviving legacy entry.
- No warning log is emitted for the promotion beyond the existing migration trace.
- Disk-hit reads are lock-free; promotion holds the coordinator per-entity exclusive `.lock`; concurrent promotion converges benign (same source bytes, last-writer-wins).

### Components

| Component | Role after change |
|---|---|
| `/agent/` handler | Same semantics as `/skill/` handler |
| `FileEntityTransactionCoordinator.ExecuteAsync(new FileEntityMutation(...))` | Step 1: publish file + sidecar only (returns `Created` / `Replaced` / `RecoveryRequired`) |
| Store delete of legacy `local:agent` NMP/2 entry | Step 2: runs only on `Created` / `Replaced`; skipped on `RecoveryRequired` / throw |
| Existing migration trace | Sole observability for the promotion (no new warning log) |

### Data flow

1. Read `/agent/{name}`: check disk (file + sidecar).
2. On disk-hit, return directly (lock-free).
3. On disk-miss, check legacy `local:agent` NMP/2 entry.
4. On legacy-hit, step 1: `FileEntityTransactionCoordinator.ExecuteAsync(new FileEntityMutation(...))` publishes file + sidecar (holds the per-entity exclusive `.lock`).
5. On legacy-hit, step 2: on `Created` / `Replaced` only, delete the legacy `local:agent` NMP/2 entry via the store and return promoted; on `RecoveryRequired` / throw, leave legacy intact.
6. On legacy-miss, fall through to built-in / not-found as today.

### Error handling

- Step 1 failure (`RecoveryRequired` / throw) leaves the legacy entry intact (read still succeeds from legacy on retry); the legacy delete in step 2 never runs.
- Step 2 runs only on `Created` / `Replaced`; no partial-delete of file + sidecar is exposed (coordinator guarantees atomic-feeling publish).
- Idempotency: re-execution after a crash either finds the disk file (skip) or re-derives the same bytes from the surviving legacy entry (crash between steps re-derives identical content).
- No new error surface: failures surface through the existing migration-trace / Core result contracts.

### Testing

- Auto-promote idempotency test (via `TestHelpers.StoreScope`): first read promotes (file + sidecar exist, legacy gone), second read returns disk content unchanged.
- Disk-hit test: no legacy touch, no trace beyond normal read.
- Legacy-miss test: built-in / not-found path unchanged.

---

## 4. Migration + Testing

### Architecture

- Two promotion paths, one coordinator (two ordered steps: file + sidecar publish via `ExecuteAsync(new FileEntityMutation(...))`, then legacy `local:agent` delete on `Created` / `Replaced` only):
  - **Demand-driven:** read-time auto-promote on touch (Section 3).
  - **Bulk:** `tracking migrate-legacy` sweep also promotes `local:agent` legacy entries (same two-step coordinator sequence).
- `local-topic:` journals are rewritten (migrated) before the sunset date; then the sunset test flips the shim branch to `Unknown` scope and the `Obsolete` shim is removed.

### Components

| Component | Role after change |
|---|---|
| `tracking migrate-legacy --to` | Bulk sweep covers `local:agent` promotion in addition to existing tracking migration |
| Read path | Demand-driven promotion on touch |
| `local-topic:` journals | Rewritten pre-sunset; post-sunset resolve to `Unknown` |

### Data flow

1. Steady state: reads auto-promote `local:agent` entries on touch.
2. Operator runs `tracking migrate-legacy --to` for a bulk sweep of remaining `local:agent` entries.
3. Pre-sunset: `local-topic:` journals are rewritten to canonical form via `tracking migrate-legacy` / explicit journal rewrite — never via shim writes (shim writes stay rejected per Section 1).
4. Sunset: shim removed; sunset test asserts `Unknown` scope for legacy inputs.

### Error handling

- Bulk sweep is per-entry fault-isolated: one bad entry does not abort the sweep (existing migrate-legacy semantics).
- Sunset removal is a breaking change: noted in `CHANGELOG.md` Unreleased / Breaking section (approved breaking legacy removal).

### Testing

- All new/changed tests run via `TestHelpers.StoreScope` (workspace, store dir, ephemeral store, `SessionBudget` isolation):
  - Tracking precedence (single-underscore wins) — Inspect level.
  - Auto-promote idempotency (`/agent/` disk-miss + legacy hit).
  - Recovery compat (`local-topic:` reads succeed pre-sunset).
  - Sunset expiry (`local-topic:` resolves to `Unknown` post-removal).
- Gate: `dotnet test Scrinia.sln` green.
- `CHANGELOG.md` Unreleased / Breaking entry records the approved breaking legacy removal.

---

## Out of Scope

- **F1 (freeze legacy forever):** rejected — the shim sunsets and breaks at the sunset date.
- **F3 (eager bulk rewrite on startup):** rejected — promotion is demand-driven on touch plus the explicit bulk `migrate-legacy` sweep.
- **F4 (dual-write):** rejected — promotion writes file + sidecar once and deletes legacy; no ongoing dual-write.
- **`DurableFile` internals:** unchanged (same-directory replacement + interprocess lock already in place).
- **Colon guards:** unchanged.
- **`Shared:Enabled` env-ignore:** unchanged (workspace-only).

## Open Items

- [ ] Sunset date for `local-topic:` shim removal: **TBD — resolve in review** (`<!-- SUNSET-DATE-TBD -->`).
