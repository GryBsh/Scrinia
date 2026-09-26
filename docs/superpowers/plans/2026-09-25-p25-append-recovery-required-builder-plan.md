# P25 — Append Recovery-Required Exception Builder Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-25-p25-append-recovery-required-builder-seam.md`
**Phase:** P25 (twenty-fifth god-store seam extraction; append recovery-orchestration phase).
**Shape:** new owner class in `AppendContracts.cs` + one builder removal + 15 call-site repoints. Single wave, sequential.

## Workstreams

- W1 — add `AppendRecoveryBuilder` (owning `RecoveryRequired`) to `AppendContracts.cs`.
- W2 — remove the store's `RecoveryRequired` builder body.
- W3 — repoint the 15 call sites to `AppendRecoveryBuilder.RecoveryRequired`.
- W4 — build + seam test verification.
- W5 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 → W5 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Extend `AppendContracts.cs`

Edit `src/Scrinia.Core/AppendContracts.cs` (append at end of file). Add the `AppendRecoveryBuilder` owner class with the verbatim `RecoveryRequired` builder (no injected hooks).

## W2 — Remove the store builder

Delete the `RecoveryRequired` builder body from `src/Scrinia.Core/FileMemoryStore.cs` (:3418–:3426).

## W3 — Repoint the call sites

Fifteen edits in `src/Scrinia.Core/FileMemoryStore.cs`. In each call site, qualify the receiver with `AppendRecoveryBuilder.` and leave every argument (including named `operationId:` args and default `inner`) unchanged: :2290, :2302, :2750, :2764, :2780, :2785, :2808, :2822, :3170, :3201, :3206, :3213, :3223, :3231, :3293.

Confirm via grep that no remaining bare `RecoveryRequired(` call site is left in the file after these edits (the `AppendStoreErrorCode.RecoveryRequired` / `AppendJournalState.RecoveryRequired` enum references are not call sites and must stay).

## W4 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendRecoveryBuilder|FullyQualifiedName~RecoveryRequired"
   ```
   Confirm `Passed!` / 0 failed.

## W5 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `AppendRecoveryBuilder` owns the `RecoveryRequired` exception factory; `FileMemoryStore` retains only the repointed call sites within its write/recovery orchestration.
- No new public `IMemoryStore` API; the builder is `internal` on `AppendContracts.cs`.
- Full suite green.
