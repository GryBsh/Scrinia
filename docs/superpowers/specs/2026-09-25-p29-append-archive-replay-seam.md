# P29 — Append-Archive-Replay Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P28 complete; append recovery-orchestration phase: P24 `AppendRollbackJournalReader`, P25 `AppendRecoveryBuilder`, P26 `AppendJournalWriter` forwarder consolidation, P27 `AppendRollbackExecutor`, P28 `AppendMemberVerifier`).

**Phase:** P29 (twenty-ninth seam extraction; append recovery-orchestration phase, sixth sub-seam).
**Goal:** extract the archive-replay sub-conductor (`WriteArchiveMember` + `ReplayArchiveMembers`) off the store into a shared owner `AppendArchiveReplayer`, the first cut into the top-level conductor `RecoverAppendUnsafeAsync`.

## Why this phase

`RecoverAppendUnsafeAsync` (`FileMemoryStore.cs` :3139–:3257) is the top-level recovery state-machine. It cannot be moved wholesale in a single phase: two of the leaves it drives are **shared** with the store's publish/write paths and so cannot be orphaned into the recovery owner:

- `WriteArchiveMember` (:2981) — the durable archive-write leaf, called by the **publish** path (`PublishFrozenArchiveAsync` :2966/:2973) AND the **recovery** path (`ReplayArchiveMembers` :3268/:3278).
- `PruneCommittedArchives` (:3045) — the post-commit prune conductor, called by the **write** path (:2851) AND the **recovery** path (:3217).

Untangling the conductor therefore proceeds from the shared durable-write leaf outward. `WriteArchiveMember` + `ReplayArchiveMembers` form one cohesive "archive replay" seam: `ReplayArchiveMembers` is the recovery replay conductor and `WriteArchiveMember` is its durable-write leaf (shared with the publish path). Moving both into a single owner makes the durable-write leaf owned in one place, reachable from both the publish path (via a store hook) and the recovery path (via the owner).

### `WriteArchiveMember` (:2981–:3036) — pure durable-write leaf

Returns the post-write status of one archive member. Dependencies:

- `AppendFaultPoint` — frozen enum.
- `InjectAppendFault` — the store's **static** fault seam (:97); the owner routes it through an injected `_injectAppendFault` hook (same shape as `AppendRollbackExecutor`/`AppendJournalWriter`).
- `File.Exists` — system.
- `RecoveryEvidenceVerifier.VerifySingleAsync` — frozen static.
- `RecoveryScopeBinding` / `RecoveryEvidenceKind` / `RecoveryExpectedHash` / `MaintenanceLimits.Hard` / `RecoveryVerificationOutcome` / `RecoveryVerificationResult` — frozen types.
- `DurableFile.AtomicWriteAllBytesForAppend(...)` — frozen durable-write leaf. Its append-fault seam routes through the store's static `InjectAppendFault` (via `DurableFile.cs:27`); the **write-point / flush-point faults flow through that existing static seam unchanged — no `DurableFile` refactor**.
- `AppendStoreException` / `AppendStoreErrorCode` / `AppendRecoveryInfo` — frozen (in `AppendContracts.cs`).
- `ArchiveMemberStatus` — frozen enum.
- `AppendJournalCodec.GetJournalPath` — frozen static.

No `FileMemoryStore` instance state. The only seam the owner must inject is `_injectAppendFault`.

### `ReplayArchiveMembers` (:3259–:3284) — recovery replay conductor

Replays recorded archive members during recovery and persists an `ArchiveInProgress` journal transition. Dependencies:

- `WriteArchiveMember` — moves with it into the owner.
- `_appendJournalWriter.PersistAppendJournal` — owned (`AppendJournalWriter`, P19/P20/P26). The owner holds a reference to the `AppendJournalWriter` instance (referenced seam owner, the same allowance `SearchService` exercises for `ListingService` in P7).
- `ArchivePlan.WithStatuses` — frozen `ArchivePlan` method.
- `AppendTransaction` / `ArchivePlan` / `ArchiveMemberStatus` — frozen types.

No `FileMemoryStore` instance state.

## The seam

### New owner: `AppendArchiveReplayer` (`src/Scrinia.Core/AppendArchiveReplayer.cs`)

Create `src/Scrinia.Core/AppendArchiveReplayer.cs` (new file) with the verbatim instance owner below. `WriteArchiveMember` replaces `InjectAppendFault(...)` with `_injectAppendFault(...)`; its `DurableFile.AtomicWriteAllBytesForAppend(...)` call is kept verbatim. `ReplayArchiveMembers` replaces `_appendJournalWriter.PersistAppendJournal(...)` with the injected `appendJournalWriter.PersistAppendJournal(...)`.

```csharp
namespace Scrinia.Core;

/// <summary>
/// Owns the append archive-member durable-write leaf and the recovery archive-replay conductor moved
/// off <see cref="FileMemoryStore"/> in P29. Holds no <see cref="FileMemoryStore"/> instance — only the
/// injected append-fault hook the store exposes as a frozen static seam and the referenced
/// <see cref="AppendJournalWriter"/> owner (P19/P20/P26). <see cref="WriteArchiveMember"/> is shared with
/// the store's publish path (<c>PublishFrozenArchiveAsync</c>), so this owner is the single home for the
/// durable archive-write leaf; its fault points route through the same <c>DurableFile</c> static seam the
/// store used. No durable journal-state transition is owned here beyond the
/// <c>PersistAppendJournal</c> call that <see cref="ReplayArchiveMembers"/> already performs.
/// </summary>
internal sealed class AppendArchiveReplayer
{
    private readonly Func<AppendFaultPoint, string, string, Exception?> _injectAppendFault;
    private readonly AppendJournalWriter _appendJournalWriter;

    public AppendArchiveReplayer(
        Func<AppendFaultPoint, string, string, Exception?> injectAppendFault,
        AppendJournalWriter appendJournalWriter)
    {
        _injectAppendFault = injectAppendFault;
        _appendJournalWriter = appendJournalWriter;
    }

    /// <summary>Publicate one archive member to <paramref name="destination"/> under the archive-write
    /// fault seam, verifying an existing destination's bytes first. Shared durable-write leaf used by
    /// both the publish path and the recovery replay conductor.</summary>
    public static ArchiveMemberStatus WriteArchiveMember(
        string memberName, RawMemberSnapshot member, string destination, string operationId,
        AppendFaultPoint writePoint, AppendFaultPoint flushPoint, CancellationToken cancellationToken)
    {
        if (!member.Existed)
            return ArchiveMemberStatus.NotRequired;

        try
        {
            Exception? injected = _injectAppendFault(writePoint, operationId, destination);
            if (injected is not null)
                throw injected;

            if (File.Exists(destination))
            {
                string root = Path.GetDirectoryName(Path.GetDirectoryName(destination)!)!;
                RecoveryVerificationResult verification = RecoveryEvidenceVerifier.VerifySingleAsync(
                    new RecoveryScopeBinding("workspace", root), RecoveryEvidenceKind.AppendJournal,
                    destination, new RecoveryExpectedHash(member.Sha256), MaintenanceLimits.Hard,
                    cancellationToken).AsTask().GetAwaiter().GetResult();
                verification.SourceContent?.Dispose();
                if (verification.Outcome == RecoveryVerificationOutcome.Verified)
                    return ArchiveMemberStatus.AlreadyPresentVerified;

                throw new AppendStoreException(
                    AppendStoreErrorCode.RecoveryRequired,
                    operationId,
                    $"Archive {memberName} destination contains different bytes: {destination}",
                    recovery: new AppendRecoveryInfo(
                        AppendJournalCodec.GetJournalPath(Path.GetDirectoryName(destination)!),
                        AppendJournalState.RecoveryRequired.ToString(),
                        "resolve the retained append journal before retrying",
                        $"Archive {memberName} destination contains different bytes."));
            }

            DurableFile.AtomicWriteAllBytesForAppend(destination, member.Bytes, operationId, writePoint, flushPoint);
        }
        catch (AppendStoreException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AppendStoreException(
                AppendStoreErrorCode.RecoveryRequired,
                operationId,
                $"Archive {memberName} publication failed at '{destination}'.",
                exception,
                new AppendRecoveryInfo(
                    AppendJournalCodec.GetJournalPath(Path.GetDirectoryName(destination)!),
                    AppendJournalState.RecoveryRequired.ToString(),
                    "resolve the retained append journal before retrying",
                    exception.Message));
        }
        return ArchiveMemberStatus.Created;
    }

    /// <summary>Replay recorded archive members during append recovery, persisting an
    /// <c>ArchiveInProgress</c> journal transition after each member. Recovery replay conductor: no
    /// durable journal-state transition beyond the <c>PersistAppendJournal</c> it drives.</summary>
    public AppendTransaction ReplayArchiveMembers(AppendTransaction transaction, string journalPath,
        CancellationToken cancellationToken)
    {
        ArchivePlan plan = transaction.Archive;
        if (!plan.Required)
            return transaction;

        ArchiveMemberStatus artifactStatus = plan.ArtifactStatus;
        if (artifactStatus is not (ArchiveMemberStatus.AlreadyPresentVerified or ArchiveMemberStatus.Created))
            artifactStatus = WriteArchiveMember("artifact", transaction.OldArtifact, plan.ArtifactArchivePath!,
                transaction.OperationId, AppendFaultPoint.ArchiveArtifactWrite,
                AppendFaultPoint.ArchiveArtifactFlush, cancellationToken);
        transaction = transaction with { Archive = plan.WithStatuses(artifactStatus, plan.SidecarStatus) };
        _appendJournalWriter.PersistAppendJournal(transaction, AppendJournalState.ArchiveInProgress);

        ArchiveMemberStatus sidecarStatus = plan.SidecarStatus;
        if (sidecarStatus is not (ArchiveMemberStatus.AlreadyPresentVerified or ArchiveMemberStatus.Created))
        {
            sidecarStatus = transaction.OldSidecar.Existed && plan.SidecarArchivePath is not null
                ? WriteArchiveMember("sidecar", transaction.OldSidecar, plan.SidecarArchivePath, transaction.OperationId,
                    AppendFaultPoint.ArchiveSidecarWrite, AppendFaultPoint.ArchiveSidecarFlush, cancellationToken)
                : ArchiveMemberStatus.NotRequired;
        }

        return transaction with { Archive = plan.WithStatuses(artifactStatus, sidecarStatus) };
    }
}
```

### Store wiring (`FileMemoryStore.cs`)

1. **Declare** the field at :155 (after `_appendJournalWriter`):
   ```csharp
   private readonly AppendArchiveReplayer _archiveReplayer;
   ```
2. **Construct** it in the store constructor at :321 (after the `_appendJournalWriter` construction block):
   ```csharp
   _archiveReplayer = new AppendArchiveReplayer(
       InjectAppendFault, _appendJournalWriter);
   ```
3. **Delete** `WriteArchiveMember` (:2981–:3036) and `ReplayArchiveMembers` (:3259–:3284) from the store.
4. **Repoint** the four call sites:
   - `PublishFrozenArchiveAsync` :2966 `WriteArchiveMember("artifact", ...)` → `_archiveReplayer.WriteArchiveMember("artifact", ...)`
   - `PublishFrozenArchiveAsync` :2973 `WriteArchiveMember("sidecar", ...)` → `_archiveReplayer.WriteArchiveMember("sidecar", ...)`
   - `RecoverAppendUnsafeAsync` :3189 `ReplayArchiveMembers(transaction, journalPath, ct)` → `_archiveReplayer.ReplayArchiveMembers(transaction, journalPath, ct)`

Confirm via grep that no remaining bare `WriteArchiveMember(` / `ReplayArchiveMembers(` call site is left in the store after the edit.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier, `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue, `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`B0FaultSeam`/`RecoveryEvidenceVerifier`/`RecoveryEnvelopeCodec`/`AppendJournalCodec`/`AppendRecoveryAdapter` (all frozen — **do NOT refactor `DurableFile`; the archive-write fault points flow through its existing static seam**), the `DurableWriter` durable-write leaves (P9–D16), `ArchiveService` (P17), `ArtifactIdentityMapper` (P18), `AppendJournalWriter` (P19/P20/P26 — do NOT re-edit that owner's `PersistAppendJournal` body, only pass the instance through), `AppendRollbackExecutor` (P27), the P21/P22 verification predicates (`MatchesAppendMember`/`MatchesArchiveBytes`/`MatchesAppendState`/`HasCommittedHash`) in `AppendRecoveryVerifier`, `AppendRecoveryBuilder` (P25), `AppendRollbackJournalReader` (P24), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, the read-side seam owners (`ListingService`, `SearchService`), and the recovery-orchestration methods (`RecoverAppendUnsafeAsync`, `PersistRecoveryRequiredOrThrow`, `AreAppendMembersCurrent`, `PruneCommittedArchives`, `PublishFrozenArchiveAsync`, `PublishLiveAppendAsync`).

## Acceptance

- `AppendArchiveReplayer` owns the durable archive-write leaf + the recovery replay conductor; `FileMemoryStore` retains only the repointed call sites within `PublishFrozenArchiveAsync` and `RecoverAppendUnsafeAsync`.
- No new public API; `IMemoryStore` surface unchanged.
- No `FileMemoryStore` instance state referenced by the owner; `DurableFile` unchanged.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendArchiveReplayer|FullyQualifiedName~WriteArchiveMember|FullyQualifiedName~ReplayArchiveMembers"
   ```
   Confirm `Passed!` / 0 failed. (Note: VSTest `--filter` does not accept `|`; if the two-sided filter matches zero tests, run each side separately or the behavioral `--filter "FullyQualifiedName~Append"` instead.)
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
