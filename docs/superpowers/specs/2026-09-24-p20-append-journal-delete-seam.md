# P20 — Append-Journal Durable-Delete Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P19 complete; durable-write/delete leaves in `DurableWriter` (P9–P16), archive-version leaf in `ArchiveService` (P17), entry-identity/normalization group in `ArtifactIdentityMapper` (P18), append-journal durable WRITE in `AppendJournalWriter` (P19)).
**Phase:** P20 (twentieth seam extraction; durable-append-write pass continued).
**Goal:** move the append-journal durable-delete leaf (`TryDeleteAppendJournal`) into the existing `AppendJournalWriter` owner (P19), leaving the store's `TryDeleteAppendJournal` forwarder intact so its 3 internal call sites are unchanged.

## Why this phase

`TryDeleteAppendJournal` (`FileMemoryStore.cs` :3304–:3310) is the clean durable-append DELETE leaf — its entire body is a single append fault-point guard plus a `File.Delete`:

```csharp
private static void TryDeleteAppendJournal(string path, string operationId)
{
    Exception? failure = InjectAppendFault(AppendFaultPoint.JournalDelete, operationId, path);
    if (failure is not null)
        throw failure;
    File.Delete(path);
}
```

It is called by exactly 3 internal store sites (:3217 recovery committed-cleanup, :3241 rollback, :3301 `DeleteAppendJournal`), none of which need to change — they keep calling the store's forwarder. Its only dependencies are `InjectAppendFault` (the append fault seam, which stays resident as a forwarder) and `File.Delete`. It is the mirror image of `PersistAppendJournal` (P19) and belongs in the same append-journal owner.

The other append-path durable primitives are NOT clean leaves and are correctly excluded:
- `WriteArchiveMember` (~:2973) — recovery-entangled (`RecoveryEvidenceVerifier.VerifySingleAsync` on pre-existing destinations + `AppendStoreException` wrapping + archive-member status bookkeeping).
- `ReconcileArchiveMember` (~:3412) / `RestoreAppendMember` (~:3460) — rollback orchestration entangled with `MatchesArchiveBytes`/`MatchesAppendMember` recovery verification.
- `DeleteAppendJournal` (~:3298) — high-level delegator (computes the journal path, delegates to `TryDeleteAppendJournal`).

## The seam

Add `TryDeleteAppendJournal` to the existing `AppendJournalWriter` owner (P19). The owner gains one injected hook: `InjectAppendFault` as a `Func<AppendFaultPoint, string, string, Exception?>` delegate. The store passes its resident `InjectAppendFault` (:97) in.

### Update `AppendJournalWriter` (`src/Scrinia.Core/AppendJournalWriter.cs`)

1. Add the injected fault-injection field and constructor parameter:

```csharp
private readonly Func<string, string> _storeDirForScope;
private readonly Func<AppendFaultPoint, string, string, Exception?> _injectAppendFault;

public AppendJournalWriter(
    Func<string, string> storeDirForScope,
    Func<AppendFaultPoint, string, string, Exception?> injectAppendFault)
{
    _storeDirForScope = storeDirForScope;
    _injectAppendFault = injectAppendFault;
}
```

2. Append the leaf (verbatim body, `InjectAppendFault` → `_injectAppendFault`):

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

### Store wiring (`FileMemoryStore.cs`)

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

Leave `InjectAppendFault` (:97) resident as the append fault seam (it is also called by `DurableFile.ThrowAppendFault` and `Scrinia.Mcp/MemoryTools.Core.cs`). Leave `DeleteAppendJournal` (:3298), `RecoverAppendUnsafeAsync`, and the recovery-orchestration methods untouched.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier, `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue, `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`B0FaultSeam`/`RecoveryEvidenceVerifier`/`RecoveryEnvelopeCodec`/`AppendJournalCodec`/`AppendRecoveryAdapter` (all frozen), the `DurableWriter` durable-write leaves (P9–P16), `ArchiveService` (P17), `ArtifactIdentityMapper` (P18), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, the read-side seam owners (`ListingService`, `SearchService`), and the append-path recovery orchestration methods (`RecoverAppendUnsafeAsync`, `ReplayArchiveMembers`, `WriteArchiveMember`, `PruneCommittedArchives`, `ReconcileArchiveMember`, `RestoreAppendMember`, `HasCommittedHash`, `VerifyAppendMemberAsync`, `TryRestoreAppendTransaction`, `PersistRecoveryRequiredOrThrow`, `AreAppendMembersCurrent`, `MatchesAppendMember`, `MatchesArchiveBytes`, `MatchesAppendState`, `DeleteAppendJournal`).

## Acceptance

- `AppendJournalWriter` owns the append-journal durable delete; `FileMemoryStore.TryDeleteAppendJournal` is a one-line forwarder; its 3 internal call sites are unchanged.
- No new public API; `IMemoryStore` surface unchanged.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~TryDeleteAppendJournal|FullyQualifiedName~AppendJournalWriter|FullyQualifiedName~AppendJournal|FullyQualifiedName~RecoverAppend"
   ```
   Confirm `Passed!` / 0 failed.
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final summary per project. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
