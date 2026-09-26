# P24 — Append-Rollback-Journal-Reader Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-25-p24-append-rollback-journal-reader-seam.md`
**Phase:** P24 (twenty-fourth god-store seam extraction; recovery-orchestration phase).
**Shape:** new owner + one store forwarder. Single wave, sequential.

## Workstreams

- W1 — create `src/Scrinia.Core/AppendRollbackJournalReader.cs` with the verbatim reconstruction method.
- W2 — replace the store's `TryReadAppendTransactionForRollback` body with a one-line forwarder.
- W3 — build + seam test verification.
- W4 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Create `AppendRollbackJournalReader`

Create `src/Scrinia.Core/AppendRollbackJournalReader.cs` (new file) with the verbatim static owner method from the parent spec (the full `TryReadAppendTransactionForRollback` body at :3348–:3373; all frozen primitives).

## W2 — Replace the store forwarder

Edit `src/Scrinia.Core/FileMemoryStore.cs`. Replace the `TryReadAppendTransactionForRollback` method body (:3348–:3373) with a one-line forwarder:

```csharp
    private static AppendTransaction? TryReadAppendTransactionForRollback(string journalPath, string scope) =>
        AppendRollbackJournalReader.TryReadAppendTransactionForRollback(journalPath, scope);
```

## W3 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendRollbackJournalReader|FullyQualifiedName~TryReadAppendTransactionForRollback"
   ```
   Confirm `Passed!` / 0 failed.

## W4 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `AppendRollbackJournalReader` owns the journal→transaction reconstruction; `FileMemoryStore` retains only the one-line forwarder.
- No new public API; `IMemoryStore` surface unchanged.
- Full suite green.
