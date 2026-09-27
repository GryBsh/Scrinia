# P32 — Append-Recovery-Required Enforcer Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-25-p31-append-recovery-required-enforcer.md`
**Phase:** P32 (thirty-first... [sic: thirty-second] god-store seam extraction; append recovery-orchestration phase).
**Shape:** new owner `AppendRecoveryEnforcer` + field + constructor wiring + one method deletion + three call-site repoints. Single wave, sequential.

## Workstreams

- W1 — create `src/Scrinia.Core/AppendRecoveryEnforcer.cs` with the owner (holds `_appendJournalWriter`, `PersistRecoveryRequiredOrThrow`).
- W2 — declare the `_recoveryEnforcer` field in `FileMemoryStore.cs`.
- W3 — construct `_recoveryEnforcer` in the store constructor.
- W4 — delete `PersistRecoveryRequiredOrThrow` from the store.
- W5 — repoint the three call sites in `RecoverAppendUnsafeAsync`.
- W6 — build + seam test verification.
- W7 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 → W5 → W6 → W7 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Create AppendRecoveryEnforcer

Create `src/Scrinia.Core/AppendRecoveryEnforcer.cs` (new file) with the verbatim owner class from the parent spec (holds the `_appendJournalWriter` referenced seam owner; `PersistRecoveryRequiredOrThrow` moved verbatim; no fault-injection hook — its durable-write fault is transitive through `AppendJournalWriter.PersistAppendJournal`).

## W2 — Declare the field

Edit `src/Scrinia.Core/FileMemoryStore.cs`. Near the other append seam-owner fields (around :155), declare:

```csharp
    private readonly AppendRecoveryEnforcer _recoveryEnforcer;
```

## W3 — Construct in the store constructor

Edit `src/Scrinia.Core/FileMemoryStore.cs`. Near the `_appendJournalWriter` construction (around :323), add:

```csharp
        _recoveryEnforcer = new AppendRecoveryEnforcer(_appendJournalWriter);
```

## W4 — Delete the method

Delete `PersistRecoveryRequiredOrThrow` (:3143–:3157) from `src/Scrinia.Core/FileMemoryStore.cs`.

## W5 — Repoint the call sites

Three edits in `src/Scrinia.Core/FileMemoryStore.cs`, all inside `RecoverAppendUnsafeAsync`:
- :3079 → `_recoveryEnforcer.PersistRecoveryRequiredOrThrow(transaction, journalPath, "...", exception, operationId)`
- :3125 → `_recoveryEnforcer.PersistRecoveryRequiredOrThrow(transaction, journalPath, "...", null, operationId)`
- :3136 → `_recoveryEnforcer.PersistRecoveryRequiredOrThrow(transaction, journalPath, "...", exception, operationId)`

Confirm via grep that no remaining bare `PersistRecoveryRequiredOrThrow(` call site is left in the store after the edit.

## W6 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Append"`.
   Confirm `Passed!` / 0 failed.

## W7 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `AppendRecoveryEnforcer` owns `PersistRecoveryRequiredOrThrow`; `FileMemoryStore` retains only the three repointed call sites within `RecoverAppendUnsafeAsync`.
- No new public API; `IMemoryStore` surface unchanged.
- No `FileMemoryStore` instance state referenced by the owner; `DurableFile` unchanged.
- Full suite green.
