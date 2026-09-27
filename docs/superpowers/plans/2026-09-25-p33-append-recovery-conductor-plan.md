# P33 — Recovery-Orchestration Conductor Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-25-p33-append-recovery-conductor.md`
**Phase:** P33 (thirty-third god-store seam extraction; append recovery-orchestration phase, conductor goal).
**Shape:** new owner `AppendRecoveryConductor` (5 seam-owners + fault hook) + wholesale move of `RecoverAppendUnsafeAsync` + field + constructor wiring + method deletion + five call-site repoints. Single wave, sequential.

## Workstreams

- W1 — create `src/Scrinia.Core/AppendRecoveryConductor.cs` with the instance owner.
- W2 — declare the `_recoveryConductor` field in `FileMemoryStore.cs`.
- W3 — construct `_recoveryConductor` in the store constructor (pass `InjectAppendFault` + the five seam-owners).
- W4 — delete `RecoverAppendUnsafeAsync` from the store.
- W5 — repoint the five call sites (bind a `storeDirectory` local at the two `RecoverScopeUnsafe` sites; pass `GetStoreDirForScope(scope)` inline at the three others).
- W6 — build + seam test verification.
- W7 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 → W5 → W6 → W7 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Create AppendRecoveryConductor

Create `src/Scrinia.Core/AppendRecoveryConductor.cs` (new file) with the verbatim owner class from the parent spec (holds `_injectAppendFault` + the five append seam-owners; `RecoverAppendUnsafeAsync` moved verbatim with `storeDirectory` threaded in as a parameter instead of `GetStoreDirForScope(scope)`, `InjectAppendFault(...)` → `_injectAppendFault(...)`, and the five seam-owner calls using the injected fields).

## W2 — Declare the field

Edit `src/Scrinia.Core/FileMemoryStore.cs`. Near the other append seam-owner fields (:154–:159), declare:

```csharp
    private readonly AppendRecoveryConductor _recoveryConductor;
```

## W3 — Construct in the store constructor

Edit `src/Scrinia.Core/FileMemoryStore.cs`. Next to the other append seam-owner constructions (:318–:333), add:

```csharp
        _recoveryConductor = new AppendRecoveryConductor(
            InjectAppendFault, _archiveReplayer, _appendJournalWriter, _recoveryEnforcer, _archiveService, _rollbackExecutor);
```

## W4 — Delete the conductor

Delete `RecoverAppendUnsafeAsync` (:3024–:3142) from `src/Scrinia.Core/FileMemoryStore.cs`.

## W5 — Repoint the call sites

Five edits in `src/Scrinia.Core/FileMemoryStore.cs`:
- `StorePersistentAsync` (:2345–:2346) — bind `string storeDirectory = GetStoreDirForScope(scope);`, use it for both `RecoverScopeUnsafe(scope, storeDirectory, cancellationToken: cancellationToken)` and `_recoveryConductor.RecoverAppendUnsafeAsync(scope, storeDirectory, "store-recovery", cancellationToken)`.
- `AppendAsync` (:2550–:2551) — bind `string storeDirectory = GetStoreDirForScope(scope);`, use it for both `RecoverScopeUnsafe(scope, storeDirectory, cancellationToken: ct)` and `_recoveryConductor.RecoverAppendUnsafeAsync(scope, storeDirectory, operationId, ct)`.
- `AppendAsyncCore` (:2618) — `_recoveryConductor.RecoverAppendUnsafeAsync(scope, GetStoreDirForScope(scope), operationId, ct)`.
- `RecoverAppendAsync` (:3007) — `_recoveryConductor.RecoverAppendUnsafeAsync(scope, GetStoreDirForScope(scope), "recovery", cancellationToken)`.
- `RecoverAppendParticipantForCoordinatorAsync` (:3011) — `_recoveryConductor.RecoverAppendUnsafeAsync(scope, GetStoreDirForScope(scope), "coordinator-recovery", cancellationToken)`.

Confirm via grep that no remaining bare `RecoverAppendUnsafeAsync(` call site is left in the store after the edit.

## W6 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Append"`.
   Confirm `Passed!` / 0 failed.

## W7 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `AppendRecoveryConductor` owns `RecoverAppendUnsafeAsync`; `FileMemoryStore` retains only the five repointed call sites.
- No new public API; `IMemoryStore` surface unchanged.
- No `FileMemoryStore` instance state referenced by the owner.
- Full suite green.
