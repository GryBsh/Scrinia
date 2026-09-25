# P20 — Append-Journal Durable-Delete Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-24-p20-append-journal-delete-seam.md`
**Phase:** P20 (twentieth god-store seam extraction; durable-append-write pass continued).
**Shape:** add one leaf + one injected hook to the existing `AppendJournalWriter` owner (P19) + two store edits. Single wave, sequential.

## Workstreams

- W1 — add `TryDeleteAppendJournal` + `InjectAppendFault` hook to `AppendJournalWriter`.
- W2 — store wiring: construction (pass fault delegate) + forwarder.
- W3 — build + seam test verification.
- W4 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Extend `AppendJournalWriter`

Edit `src/Scrinia.Core/AppendJournalWriter.cs` (created P19):

1. Add the `_injectAppendFault` field and constructor parameter (after `_storeDirForScope`):

```csharp
private readonly Func<AppendFaultPoint, string, string, Exception?> _injectAppendFault;

public AppendJournalWriter(
    Func<string, string> storeDirForScope,
    Func<AppendFaultPoint, string, string, Exception?> injectAppendFault)
{
    _storeDirForScope = storeDirForScope;
    _injectAppendFault = injectAppendFault;
}
```

2. Append the `TryDeleteAppendJournal` leaf (verbatim body, `InjectAppendFault` → `_injectAppendFault`):

```csharp
    /// <summary>Delete an append journal from <c>versions/</c>, guarded by the append journal-delete
    /// fault point. Pure durable-delete leaf: no recovery verification, no rollback bookkeeping.</summary>
    public void TryDeleteAppendJournal(string path, string operationId)
    {
        Exception? failure = _injectAppendFault(AppendFaultPoint.JournalDelete, operationId, path);
        if (failure is not null)
            throw failure;
        File.Delete(path);
    }
```

## W2 — Store wiring (`FileMemoryStore.cs`)

Two edits:

1. **Construction.** Update the `_appendJournalWriter` construction (added in P19) to pass the fault-injection delegate:

```csharp
_appendJournalWriter = new AppendJournalWriter(
    scope => GetStoreDirForScope(scope),
    InjectAppendFault);
```

2. **Forwarder.** Replace the `TryDeleteAppendJournal` body (:3304–:3310) with a one-line forwarder:

```csharp
private static void TryDeleteAppendJournal(string path, string operationId) =>
    _appendJournalWriter.TryDeleteAppendJournal(path, operationId);
```

Leave `InjectAppendFault` (:97) resident (it is also called by `DurableFile.ThrowAppendFault` and `Scrinia.Mcp/MemoryTools.Core.cs`).

## W3 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~TryDeleteAppendJournal|FullyQualifiedName~AppendJournalWriter|FullyQualifiedName~AppendJournal|FullyQualifiedName~RecoverAppend"
   ```
   Confirm `Passed!` / 0 failed.

## W4 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `AppendJournalWriter` owns the append-journal durable delete; `FileMemoryStore.TryDeleteAppendJournal` is a one-line forwarder; its 3 internal call sites unchanged.
- No new public API; `IMemoryStore` surface unchanged.
- Full suite green.
