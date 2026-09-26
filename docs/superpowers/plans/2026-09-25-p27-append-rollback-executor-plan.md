# P27 — Append-Rollback-Executor Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-25-p27-append-rollback-executor-seam.md`
**Phase:** P27 (twenty-seventh god-store seam extraction; append recovery-orchestration phase).
**Shape:** new instance owner + field + constructor wiring + four method deletions + six call-site repoints. Single wave, sequential.

## Workstreams

- W1 — create `src/Scrinia.Core/AppendRollbackExecutor.cs` with the instance owner (four rollback methods; `_injectAppendFault` hook).
- W2 — declare the `_rollbackExecutor` field in `FileMemoryStore.cs`.
- W3 — construct `_rollbackExecutor` in the store constructor.
- W4 — delete the four rollback methods from `FileMemoryStore.cs`.
- W5 — repoint the six external call sites to `_rollbackExecutor.*`.
- W6 — build + seam test verification.
- W7 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 → W5 → W6 → W7 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Create `AppendRollbackExecutor`

Create `src/Scrinia.Core/AppendRollbackExecutor.cs` (new file) with the verbatim instance owner class from the parent spec (the four rollback methods: `TryRestoreAppendTransaction`, `TryRemoveCreatedArchiveMembers`, `ReconcileArchiveMember`, `RestoreAppendMember`; the `_injectAppendFault` hook field + constructor; `ReconcileArchiveMember`/`RestoreAppendMember` repoint `InjectAppendFault(...)` → `_injectAppendFault(...)`; `RestoreAppendMember` keeps the `DurableFile.AtomicWriteAllBytesForAppend(...)` call verbatim).

## W2 — Declare the field

Edit `src/Scrinia.Core/FileMemoryStore.cs`. After the `_appendJournalWriter` field declaration (:155), declare:

```csharp
    private readonly AppendRollbackExecutor _rollbackExecutor;
```

## W3 — Construct in the store constructor

Edit `src/Scrinia.Core/FileMemoryStore.cs`. After the `_appendJournalWriter` construction block (:319–:321), add:

```csharp
        _rollbackExecutor = new AppendRollbackExecutor(
            InjectAppendFault);
```

## W4 — Delete the rollback methods

Delete the four rollback methods from `src/Scrinia.Core/FileMemoryStore.cs` (:3315–:3403): `TryRestoreAppendTransaction`, `TryRemoveCreatedArchiveMembers`, `ReconcileArchiveMember`, `RestoreAppendMember`.

## W5 — Repoint the external call sites

Repoint the six external call sites in `src/Scrinia.Core/FileMemoryStore.cs`:
- :2279 `if (!TryRestoreAppendTransaction(createTransaction))` → `if (!_rollbackExecutor.TryRestoreAppendTransaction(createTransaction))`
- :2733 `TryRemoveCreatedArchiveMembers(rollbackTransaction, CancellationToken.None);` → `_rollbackExecutor.TryRemoveCreatedArchiveMembers(rollbackTransaction, CancellationToken.None);`
- :2735 `TryRestoreAppendTransaction(rollbackTransaction);` → `_rollbackExecutor.TryRestoreAppendTransaction(rollbackTransaction);`
- :2797 `if (!TryRestoreAppendTransaction(transaction))` → `if (!_rollbackExecutor.TryRestoreAppendTransaction(transaction))`
- :3230 `if (!TryRestoreAppendTransaction(transaction))` → `if (!_rollbackExecutor.TryRestoreAppendTransaction(transaction))`
- :3234 `else if (!TryRestoreAppendTransaction(transaction))` → `else if (!_rollbackExecutor.TryRestoreAppendTransaction(transaction))`

Confirm via grep that no remaining bare `TryRestoreAppendTransaction(` / `TryRemoveCreatedArchiveMembers(` / `ReconcileArchiveMember(` / `RestoreAppendMember(` call site is left in the file after the edit.

## W6 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendRollbackExecutor"
   ```
   Confirm `Passed!` / 0 failed. (VSTest `--filter` does not accept `|`; if a two-sided filter is used, run each side separately or the behavioral `--filter "FullyQualifiedName~Append"` instead.)

## W7 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `AppendRollbackExecutor` owns the rollback decision trees + conductors; `FileMemoryStore` retains only the repointed call sites within its recovery/orchestration methods.
- No new public API; `IMemoryStore` surface unchanged.
- No `FileMemoryStore` instance state referenced by the owner; `DurableFile` unchanged.
- Full suite green.
