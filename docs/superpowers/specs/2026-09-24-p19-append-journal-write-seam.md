# P19 — Append-Journal Durable-Write Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P18 complete; durable-write/delete leaves in `DurableWriter` (P9–P16), archive-version leaf in `ArchiveService` (P17), entry-identity/normalization group in `ArtifactIdentityMapper` (P18)).
**Phase:** P19 (nineteenth seam extraction; durable-append-write pass).
**Goal:** move the append-journal durable-write leaf (`PersistAppendJournal`) off the store into a dedicated `AppendJournalWriter` owner, leaving the store's `PersistAppendJournal` forwarder intact so its ~12 internal call sites are unchanged.

## Why this phase

`PersistAppendJournal` (`FileMemoryStore.cs` :3292–:3328) serializes an `AppendTransaction` into a recovery-journal envelope and writes it atomically to `versions/` under the append fault path. Its ONLY dependency on the store is `GetStoreDirForScope(transaction.Scope)`; every other dependency is a frozen static class or model:

- `AppendJournalCodec` (frozen static: `GetJournalPath`, `CreatePrepared`, `WithState`)
- `AppendRecoveryAdapter` (frozen; created as a local `new AppendRecoveryAdapter()`)
- `RecoveryEvidenceVerifier` (frozen static: `VerifySingleAsync`)
- `RecoveryEnvelopeCodec` (frozen static: `ParseVerifiedEnvelope`, `Serialize`)
- `RecoveryScopeBinding` / `RecoveryVerificationResult` / `RecoveryVerificationOutcome` / `RecoveryVerifiedFileMetadata` / `RecoveryVerifiedEnvelope` / `RecoveryVerificationContent` (frozen models)
- `MaintenanceLimits.Hard` (frozen)
- `AppendFaultPoint` (frozen enum)
- `DurableFile.AtomicWriteAllBytesForAppend` (frozen static)

The durable-append fault injection for this write happens *inside* `DurableFile.AtomicWriteAllBytesForAppend` → `ThrowAppendFault` → the store's `InjectAppendFault` forwarder (which stays resident), so moving the body does not disturb the append fault seam. This is the cleanest durable-append write leaf and the natural continuation of the durable-write phase (P9–P16) into the append path.

The remaining append-path methods (`RecoverAppendUnsafeAsync`, `ReplayArchiveMembers`, `PruneCommittedArchives`, `TryDeleteAppendJournal`, `ReconcileArchiveMember`, `RestoreAppendMember`, `HasCommittedHash`, `VerifyAppendMemberAsync`, `TryRestoreAppendTransaction`, `PersistRecoveryRequiredOrThrow`) are recovery-orchestration — entangled with fault injection, recovery verification, and rollback bookkeeping — a larger, later phase.

## The seam

Move `PersistAppendJournal` (:3292–:3328) verbatim into `AppendJournalWriter`, replacing the store's `GetStoreDirForScope(...)` with the injected `_storeDirForScope` hook.

### New owner: `AppendJournalWriter` (`src/Scrinia.Core/AppendJournalWriter.cs`)

A sealed owner holding only the injected `_storeDirForScope` hook (no `FileMemoryStore` instance). It calls frozen static codecs/`DurableFile` directly.

```csharp
namespace Scrinia.Core;

/// <summary>
/// Owns the append-journal durable-write leaf moved off <see cref="FileMemoryStore"/> in P19.
/// Holds no <see cref="FileMemoryStore"/> instance — only the injected store-directory hook the
/// store exposes as a frozen primitive. Every other dependency is a frozen static codec or model.
/// </summary>
internal sealed class AppendJournalWriter
{
    private readonly Func<string, string> _storeDirForScope;

    public AppendJournalWriter(Func<string, string> storeDirForScope) => _storeDirForScope = storeDirForScope;

    public void PersistAppendJournal(AppendTransaction transaction, AppendJournalState state,
        string? committedHash = null, string? detail = null, CancellationToken? cancellationToken = null)
    {
        string storeDirectory = _storeDirForScope(transaction.Scope);
        Directory.CreateDirectory(Path.Combine(storeDirectory, "versions"));
        string path = AppendJournalCodec.GetJournalPath(Path.Combine(storeDirectory, "versions"));
        var adapter = new AppendRecoveryAdapter();
        AppendJournal current;
        if (!File.Exists(path))
        {
            current = AppendJournalCodec.CreatePrepared(transaction);
        }
        else
        {
            // Compensation is deliberately non-cancellable so rollback can establish a safe state.
            RecoveryVerificationResult verification = RecoveryEvidenceVerifier.VerifySingleAsync(
                new RecoveryScopeBinding(transaction.Scope, storeDirectory), RecoveryEvidenceKind.AppendJournal,
                path, default, MaintenanceLimits.Hard, cancellationToken.GetValueOrDefault()).AsTask().GetAwaiter().GetResult();
            if (verification.Outcome != RecoveryVerificationOutcome.Verified || verification.SourceContent is null)
                throw new IOException("Existing append journal could not be verified.");
            using RecoveryVerificationContent verifiedJournal = verification.SourceContent;
            RecoveryVerifiedFileMetadata metadata = verifiedJournal.Metadata;
            RecoveryScopeBinding scopeBinding = new(transaction.Scope, storeDirectory);
            RecoveryVerifiedEnvelope verifiedEnvelope = RecoveryEnvelopeCodec.ParseVerifiedEnvelope(
                in verifiedJournal, in scopeBinding, path, storeDirectory, RecoveryEvidenceKind.AppendJournal, in metadata);
            current = adapter.FromEnvelope(((IRecoveryAdapter)adapter).ConsumeParsed(in verifiedEnvelope), path);
        }
        current = current with
        {
            ArchiveArtifactStatus = transaction.Archive.ArtifactStatus,
            ArchiveSidecarStatus = transaction.Archive.SidecarStatus
        };
        AppendJournal journal = AppendJournalCodec.WithState(current, state, committedHash, detail);
        byte[] bytes = RecoveryEnvelopeCodec.Serialize(adapter.ToEnvelope(journal));
        DurableFile.AtomicWriteAllBytesForAppend(path, bytes, transaction.OperationId,
            AppendFaultPoint.JournalWrite, AppendFaultPoint.JournalFlush);
    }
}
```

### Store wiring (`FileMemoryStore.cs`)

Three edits:

1. **Field.** Add `private readonly AppendJournalWriter _appendJournalWriter;` immediately after the `_archiveService` field declaration (line 154).

2. **Construction.** In the internal constructor, add after the `_search` construction (~:308–:311), matching the `ListingService`/`SearchService`/`ArchiveService` hook-injection pattern:

```csharp
_appendJournalWriter = new AppendJournalWriter(scope => GetStoreDirForScope(scope));
```

3. **Forwarder.** Replace the `PersistAppendJournal` body (:3292–:3328) with a one-line forwarder:

```csharp
private void PersistAppendJournal(AppendTransaction transaction, AppendJournalState state,
    string? committedHash = null, string? detail = null, CancellationToken? cancellationToken = null) =>
    _appendJournalWriter.PersistAppendJournal(transaction, state, committedHash, detail, cancellationToken);
```

The ~12 internal call sites (write surface + `RecoverAppendUnsafeAsync` + `WriteAppendRecoveryRequiredJournalForTest`) are unchanged — they still call the store's `PersistAppendJournal` forwarder.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier, `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue, `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family (`WholeMemoryTransactionCoordinator`, `FileEntityTransactionCoordinator`, `MultiMemoryTransactionCoordinator`), `FileStoreJsonContext` (private nested), `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`B0FaultSeam`/`RecoveryEvidenceVerifier`/`RecoveryEnvelopeCodec`/`AppendJournalCodec`/`AppendRecoveryAdapter`/`RecoveryScopeBinding` (all frozen), the `DurableWriter` durable-write leaves (P9–P16), `ArchiveService` (P17), `ArtifactIdentityMapper` (P18), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, the read-side seam owners (`ListingService`, `SearchService`), and the append-path recovery orchestration methods (`RecoverAppendUnsafeAsync`, `ReplayArchiveMembers`, `PruneCommittedArchives`, `TryDeleteAppendJournal`, `ReconcileArchiveMember`, `RestoreAppendMember`, `HasCommittedHash`, `VerifyAppendMemberAsync`, `TryRestoreAppendTransaction`, `PersistRecoveryRequiredOrThrow`, `AreAppendMembersCurrent`, `MatchesAppendMember`, `MatchesArchiveBytes`, `MatchesAppendState`) — a distinct, later phase.

## Acceptance

- `AppendJournalWriter` owns the append-journal durable write; `FileMemoryStore.PersistAppendJournal` is a one-line forwarder; its ~12 internal call sites are unchanged.
- No new public API; `IMemoryStore` surface unchanged.
- `DurableFile`/`DurableWriter`/`FileStoreJsonContext` untouched.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~PersistAppendJournal|FullyQualifiedName~AppendJournalWriter|FullyQualifiedName~AppendJournal|FullyQualifiedName~RecoverAppend"
   ```
   Confirm `Passed!` / 0 failed.
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final summary per project. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
