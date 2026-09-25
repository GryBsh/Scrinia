# P19 — Append-Journal Durable-Write Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-24-p19-append-journal-write-seam.md`
**Phase:** P19 (nineteenth god-store seam extraction; durable-append-write pass).
**Shape:** one new owner class (`AppendJournalWriter`) + three store edits. Single wave, sequential.

## Workstreams

- W1 — create `src/Scrinia.Core/AppendJournalWriter.cs` with the moved `PersistAppendJournal` leaf.
- W2 — store wiring: field + construction + forwarder.
- W3 — build + seam test verification.
- W4 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Create `AppendJournalWriter`

Create `src/Scrinia.Core/AppendJournalWriter.cs` (new file): the sealed owner holding the injected `_storeDirForScope` hook and the verbatim `PersistAppendJournal` leaf (the store's `GetStoreDirForScope(...)` call replaced with `_storeDirForScope(...)`). All other dependencies (`AppendJournalCodec`, `RecoveryEnvelopeCodec`, `AppendRecoveryAdapter`, `RecoveryEvidenceVerifier`, `DurableFile`, frozen models/enums) are called directly — no injected hooks needed.

Use the exact owner class and method body from the parent spec (verbatim).

## W2 — Store wiring (`FileMemoryStore.cs`)

Three edits:

1. **Field.** Add `private readonly AppendJournalWriter _appendJournalWriter;` immediately after the `_archiveService` field (line 154).

2. **Construction.** In the internal constructor, add after the `_search` construction (~:308–:311):

```csharp
_appendJournalWriter = new AppendJournalWriter(scope => GetStoreDirForScope(scope));
```

3. **Forwarder.** Replace the `PersistAppendJournal` body (:3292–:3328) with a one-line forwarder:

```csharp
private void PersistAppendJournal(AppendTransaction transaction, AppendJournalState state,
    string? committedHash = null, string? detail = null, CancellationToken? cancellationToken = null) =>
    _appendJournalWriter.PersistAppendJournal(transaction, state, committedHash, detail, cancellationToken);
```

Confirm (via grep) that no remaining caller references the store's former body directly — all ~12 call sites now route through the forwarder. Leave `InjectAppendFault` (the append fault seam, resident) untouched.

## W3 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~PersistAppendJournal|FullyQualifiedName~AppendJournalWriter|FullyQualifiedName~AppendJournal|FullyQualifiedName~RecoverAppend"
   ```
   Confirm `Passed!` / 0 failed.

## W4 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `AppendJournalWriter` owns the append-journal durable write; `FileMemoryStore.PersistAppendJournal` is a one-line forwarder; its ~12 internal call sites unchanged.
- No new public API; `IMemoryStore` surface unchanged.
- Full suite green.
