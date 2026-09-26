# P26 — Append-Journal Forwarder Consolidation Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-25-p26-append-journal-forwarder-consolidation-seam.md`
**Phase:** P26 (twenty-sixth god-store seam extraction; append recovery-orchestration phase).
**Shape:** add one method to `AppendJournalWriter` + delete three store forwarders + repoint ~29 internal call sites. Single wave, sequential.

## Workstreams

- W1 — add `DeleteAppendJournal` to `AppendJournalWriter`.
- W2 — delete the three store forwarders (`PersistAppendJournal`/`DeleteAppendJournal`/`TryDeleteAppendJournal`).
- W3 — repoint the call sites (`PersistAppendJournal` ~22 sites, `DeleteAppendJournal` 5 sites, `TryDeleteAppendJournal` 2 sites) to `_appendJournalWriter.*`.
- W4 — build + seam test verification.
- W5 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 → W5 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Extend `AppendJournalWriter`

Edit `src/Scrinia.Core/AppendJournalWriter.cs` (P19/P20 owner). Append the `DeleteAppendJournal` lifecycle method (verbatim path derivation; uses the owner's existing `_storeDirForScope` hook + `TryDeleteAppendJournal`, no new fields/hooks):

```csharp
    /// <summary>Derive the <c>versions/</c> append-journal path for a transaction and delete it through
    /// the fault-guarded durable-delete leaf. Pure lifecycle method: no recovery verification, no
    /// rollback bookkeeping.</summary>
    public void DeleteAppendJournal(AppendTransaction transaction)
    {
        string path = AppendJournalCodec.GetJournalPath(Path.Combine(_storeDirForScope(transaction.Scope), "versions"));
        TryDeleteAppendJournal(path, transaction.OperationId);
    }
```

## W2 — Delete the store forwarders

Delete the three private forwarders from `src/Scrinia.Core/FileMemoryStore.cs` (:3296–:3307): `PersistAppendJournal`, `DeleteAppendJournal`, `TryDeleteAppendJournal`.

## W3 — Repoint the call sites

Repoint all internal call sites in `src/Scrinia.Core/FileMemoryStore.cs` (the forwarders are `private`; no external/test owners):

- `PersistAppendJournal(...)` → `_appendJournalWriter.PersistAppendJournal(...)` (~22 sites)
- `DeleteAppendJournal(transaction)` → `_appendJournalWriter.DeleteAppendJournal(transaction)` (5 sites)
- `TryDeleteAppendJournal(path, operationId)` → `_appendJournalWriter.TryDeleteAppendJournal(path, operationId)` (2 sites)

Preserve argument order, names, and defaults on every repoint. Confirm via grep that no remaining bare `PersistAppendJournal(` / `DeleteAppendJournal(` / `TryDeleteAppendJournal(` call site is left in the store after the edit.

## W4 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendJournalWriter|FullyQualifiedName~DeleteAppendJournal"
   ```
   Confirm `Passed!` / 0 failed. (VSTest `--filter` does not accept `|`; if the two-sided filter matches zero tests, run `--filter "FullyQualifiedName~AppendJournalWriter"` alone or the behavioral `--filter "FullyQualifiedName~Append"` instead.)

## W5 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `AppendJournalWriter` owns the full append-journal lifecycle (`PersistAppendJournal`/`DeleteAppendJournal`/`TryDeleteAppendJournal`); `FileMemoryStore` retains no append-journal forwarders.
- No new public API; `IMemoryStore` surface unchanged.
- Zero logic moved — every repoint is a byte-identical pass-through to an existing owner method.
- Full suite green.
