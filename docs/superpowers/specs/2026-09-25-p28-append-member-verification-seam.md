# P28 — Append-Member-Verification Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P27 complete). Append recovery-orchestration phase: P24 (`AppendRollbackJournalReader`), P25 (`AppendRecoveryBuilder`), P26 (`AppendJournalWriter` forwarder consolidation), P27 (`AppendRollbackExecutor`).

**Phase:** P28 (twenty-eighth seam extraction; append recovery-orchestration phase, fifth sub-seam).
**Goal:** move the append member verification leaf (`VerifyAppendMemberAsync`) off the store into a new instance owner `AppendMemberVerifier` (mirroring the `AppendRollbackExecutor`/`AppendJournalWriter` fault-hook pattern), leaving the recovery conductor `RecoverAppendUnsafeAsync` resident.

## Why this phase

`VerifyAppendMemberAsync` (`FileMemoryStore.cs` :3299–:3313) proves a freshly published append member matches its recorded hash, injecting the `VerificationRead` fault point before the recovery verification:

```csharp
private static async Task VerifyAppendMemberAsync(
    RawMemberSnapshot expected, string operationId, CancellationToken cancellationToken)
{
    Exception? failure = InjectAppendFault(AppendFaultPoint.VerificationRead, operationId, expected.Path);
    if (failure is not null)
        throw failure;
    RecoveryVerificationResult verification = await RecoveryEvidenceVerifier.VerifySingleAsync(
        new RecoveryScopeBinding("append", Path.GetDirectoryName(expected.Path)!),
        RecoveryEvidenceKind.AppendJournal, expected.Path,
        new RecoveryExpectedHash(expected.Sha256), MaintenanceLimits.Hard, cancellationToken)
        .ConfigureAwait(false);
    verification.SourceContent?.Dispose();
    if (verification.Outcome != RecoveryVerificationOutcome.Verified)
        throw new IOException($"Append member verification failed after publication: {expected.Path}");
}
```

It is a pure, self-contained verification leaf:

- **One fault seam** — `AppendFaultPoint.VerificationRead`, which can be routed through an injected `_injectAppendFault` hook wired to the store's static `InjectAppendFault` (same shape as `AppendRollbackExecutor._injectAppendFault` and `AppendJournalWriter._injectAppendFault`).
- **Frozen primitives only** — `RecoveryEvidenceVerifier.VerifySingleAsync` (static), `RecoveryScopeBinding`, `RecoveryEvidenceKind`, `RecoveryExpectedHash`, `MaintenanceLimits.Hard`, `RecoveryVerificationOutcome.Verified`. Its sibling `AppendRecoveryVerifier` (P21) uses the identical `RecoveryEvidenceVerifier.VerifySingleAsync` call shape.
- **No durable journal-state transition, no rollback bookkeeping** — unlike the recovery conductors, it performs no durable write and no multi-member compensation.
- **Async, no instance state** — holds no store field; the fault hook is injected.

All 4 call sites are internal to `FileMemoryStore.cs` (the create path in `AppendAsync` at :2258–:2259 and `PublishLiveAppendAsync` at :3107–:3108); no test file references the private method, so no double needs updating.

`AreAppendMembersCurrent` (:3315) is a one-line `&&` over two already-extracted `AppendRecoveryVerifier` predicates plus a **dead** `cancellationToken` parameter — low-value churn, correctly NOT extracted as its own phase. `RecoverAppendUnsafeAsync` (:3136), `ReplayArchiveMembers` (:3256), and `PersistRecoveryRequiredOrThrow` (:3283) remain resident — they are recovery-state/error conductors entangled with durable writes and journal transitions.

## The seam

### New owner: `AppendMemberVerifier` (`src/Scrinia.Core/AppendMemberVerifier.cs`)

Create `src/Scrinia.Core/AppendMemberVerifier.cs` (new file) with the verbatim instance owner class:

```csharp
namespace Scrinia.Core;

/// <summary>
/// Owns the single-append-member recovery verification moved off <see cref="FileMemoryStore"/> in
/// P28. Holds no <see cref="FileMemoryStore"/> instance — only the injected append-fault hook the
/// store exposes. Every other dependency is a frozen seam owner (<see cref="AppendRecoveryVerifier"/>)
/// or a frozen recovery primitive (<see cref="RecoveryEvidenceVerifier"/>, <see cref="RecoveryScopeBinding"/>).
/// No durable journal-state transition and no rollback bookkeeping, so the owner stays free of the
/// recovery conductors that remain resident in the store.
/// </summary>
internal sealed class AppendMemberVerifier
{
    private readonly Func<AppendFaultPoint, string, string, Exception?> _injectAppendFault;

    public AppendMemberVerifier(Func<AppendFaultPoint, string, string, Exception?> injectAppendFault)
    {
        _injectAppendFault = injectAppendFault;
    }

    /// <summary>Prove a freshly published append member matches its recorded hash, injecting the
    /// <c>VerificationRead</c> fault point before the recovery verification. Pure verification leaf:
    /// no journal-state transition, no rollback bookkeeping.</summary>
    public async Task VerifyAsync(
        RawMemberSnapshot expected, string operationId, CancellationToken cancellationToken)
    {
        Exception? failure = _injectAppendFault(
            AppendFaultPoint.VerificationRead, operationId, expected.Path);
        if (failure is not null)
            throw failure;
        RecoveryVerificationResult verification = await RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding("append", Path.GetDirectoryName(expected.Path)!),
            RecoveryEvidenceKind.AppendJournal, expected.Path,
            new RecoveryExpectedHash(expected.Sha256), MaintenanceLimits.Hard, cancellationToken)
            .ConfigureAwait(false);
        verification.SourceContent?.Dispose();
        if (verification.Outcome != RecoveryVerificationOutcome.Verified)
            throw new IOException($"Append member verification failed after publication: {expected.Path}");
    }
}
```

### Store wiring (`FileMemoryStore.cs`)

1. **Declare** the field at :155 (after `_appendJournalWriter`):
   ```csharp
   private readonly AppendMemberVerifier _memberVerifier;
   ```
2. **Construct** it in the store constructor at :321 (after the `_appendJournalWriter` construction block):
   ```csharp
   _memberVerifier = new AppendMemberVerifier(
       InjectAppendFault);
   ```
3. **Delete** the `VerifyAppendMemberAsync` method (:3299–:3313).
4. **Repoint** the 4 call sites to `_memberVerifier.VerifyAsync(...)`, preserving arguments and the `await`:
   - :2258 `await VerifyAppendMemberAsync(createTransaction.NewArtifact, appendOperationId!, ct);` → `await _memberVerifier.VerifyAsync(createTransaction.NewArtifact, appendOperationId!, ct);`
   - :2259 `await VerifyAppendMemberAsync(createTransaction.NewSidecar, appendOperationId!, ct);` → `await _memberVerifier.VerifyAsync(createTransaction.NewSidecar, appendOperationId!, ct);`
   - :3107 `await VerifyAppendMemberAsync(transaction.NewArtifact, transaction.OperationId, ct);` → `await _memberVerifier.VerifyAsync(transaction.NewArtifact, transaction.OperationId, ct);`
   - :3108 `await VerifyAppendMemberAsync(transaction.NewSidecar, transaction.OperationId, ct);` → `await _memberVerifier.VerifyAsync(transaction.NewSidecar, transaction.OperationId, ct);`

Confirm via grep that no remaining bare `VerifyAppendMemberAsync(` call site is left in the file after the edit.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier, `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue, `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`B0FaultSeam`/`RecoveryEvidenceVerifier`/`RecoveryEnvelopeCodec`/`AppendJournalCodec`/`AppendRecoveryAdapter` (all frozen — the owner uses these directly, unchanged), the `DurableWriter` durable-write leaves (P9–P16), `ArchiveService` (P17), `ArtifactIdentityMapper` (P18), `AppendJournalWriter` (P19/P20/P26), `AppendRollbackExecutor` (P27 — do NOT re-edit that owner), the P21/P22 verification predicates (`MatchesAppendMember`/`MatchesArchiveBytes`/`MatchesAppendState`/`HasCommittedHash`) in `AppendRecoveryVerifier`, `AppendRecoveryBuilder` (P25), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, the read-side seam owners (`ListingService`, `SearchService`), and the recovery-orchestration methods (`RecoverAppendUnsafeAsync`, `ReplayArchiveMembers`, `PersistRecoveryRequiredOrThrow`, `AreAppendMembersCurrent`, `PruneCommittedArchives`).

## Acceptance

- `AppendMemberVerifier` owns the append member verification leaf; `FileMemoryStore` retains only the repointed call sites within its write-path methods.
- No new public API; `IMemoryStore` surface unchanged.
- No `FileMemoryStore` instance state referenced by the owner.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendMemberVerifier|FullyQualifiedName~VerifyAsync"
   ```
   Confirm `Passed!` / 0 failed. (Note: VSTest `--filter` does not accept `|`; if the two-sided filter matches zero tests, run each side separately or the behavioral `--filter "FullyQualifiedName~Append"` instead.)
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
