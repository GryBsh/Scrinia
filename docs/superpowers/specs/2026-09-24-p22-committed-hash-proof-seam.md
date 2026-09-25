# P22 — Committed-Hash Proof Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P21 complete; durable-write/delete leaves in `DurableWriter` (P9–P16), archive-version leaf in `ArchiveService` (P17), entry-identity/normalization group in `ArtifactIdentityMapper` (P18), append-journal durable write in `AppendJournalWriter` (P19), append-journal durable delete in `AppendJournalWriter` (P20), append-member verification predicates in `AppendRecoveryVerifier` (P21)).
**Phase:** P22 (twenty-second seam extraction; append recovery-orchestration phase, second sub-seam).
**Goal:** move the committed-hash proof leaf (`HasCommittedHash`) off the store into the existing `AppendRecoveryVerifier` owner (P21), leaving the store's recovery orchestration intact.

## Why this phase

`HasCommittedHash` (`FileMemoryStore.cs` :3328–:3348) proves that a committed append's live artifact + sidecar bytes, recovered through the frozen `RecoveryEvidenceVerifier`, hash-combine to the journal's recorded `CommittedHash`. It is a pure, self-contained committed-integrity proof:

- **Static, no instance state** — takes only `(AppendTransaction, string? committedHash, CancellationToken)`.
- **No fault injection** — unlike `VerifyAppendMemberAsync` (which carries `InjectAppendFault(VerificationRead)`) or the rollback methods (which carry `AppendFaultPoint` write/delete seams), `HasCommittedHash` performs no injectable operation.
- **No durable write, no journal-state transition, no rollback, no multi-file orchestration.**
- **Single call site** — `RecoverAppendUnsafeAsync` (:3205), the top-level recovery orchestration method.

Its dependency footprint is identical to the P21 verification predicates: `RecoveryEvidenceVerifier.VerifySingleAsync` (frozen static), `RecoveryScopeBinding` (frozen model), `RecoveryEvidenceKind` (frozen), `MaintenanceLimits.Hard` (frozen), `RecoveryVerificationOutcome`/`RecoveryVerificationResult`/`RecoveryExpectedHash` (frozen models), `SHA256` (system). It belongs in the same `AppendRecoveryVerifier` owner.

The surrounding recovery orchestration (`RecoverAppendUnsafeAsync`, `ReplayArchiveMembers`, `PersistRecoveryRequiredOrThrow`, `VerifyAppendMemberAsync`, `TryRestoreAppendTransaction`, `TryRemoveCreatedArchiveMembers`, `ReconcileArchiveMember`, `RestoreAppendMember`) remains resident — it is genuinely entangled with fault injection, rollback bookkeeping, and journal-state transitions, a larger later phase. `AreAppendMembersCurrent` (:3325) is a thin boolean combiner over the already-extracted `AppendRecoveryVerifier.MatchesAppendMember` and is correctly NOT extracted (low-value churn).

## The seam

Move `HasCommittedHash` (:3328–:3348) verbatim into `AppendRecoveryVerifier`. The body is unchanged (no `FileMemoryStore` instance state).

### Update `AppendRecoveryVerifier` (`src/Scrinia.Core/AppendRecoveryVerifier.cs`)

Append the leaf (verbatim, no injected hooks):

```csharp
    /// <summary>Prove that a committed append's live artifact + sidecar bytes, recovered through the
    /// frozen verifier, hash-combine to the journal's recorded committed hash. Pure committed-integrity
    /// proof: no fault injection, no durable write, no rollback.</summary>
    public static bool HasCommittedHash(AppendTransaction transaction, string? committedHash,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(committedHash))
            return false;

        RecoveryVerificationResult artifact = RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding("append", Path.GetDirectoryName(transaction.NewArtifact.Path)!),
            RecoveryEvidenceKind.AppendJournal, transaction.NewArtifact.Path,
            new RecoveryExpectedHash(transaction.NewArtifact.Sha256), MaintenanceLimits.Hard,
            cancellationToken).AsTask().GetAwaiter().GetResult();
        RecoveryVerificationResult sidecar = RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding("append", Path.GetDirectoryName(transaction.NewSidecar.Path)!),
            RecoveryEvidenceKind.AppendJournal, transaction.NewSidecar.Path,
            new RecoveryExpectedHash(transaction.NewSidecar.Sha256), MaintenanceLimits.Hard,
            cancellationToken).AsTask().GetAwaiter().GetResult();
        string actualHash = Convert.ToHexStringLower(SHA256.HashData(
            (artifact.SourceContent?.Bytes.ToArray() ?? []).Concat(sidecar.SourceContent?.Bytes.ToArray() ?? []).ToArray()));
        artifact.SourceContent?.Dispose(); sidecar.SourceContent?.Dispose();
        return string.Equals(actualHash, committedHash, StringComparison.Ordinal);
    }
```

### Store wiring (`FileMemoryStore.cs`)

Two edits:

1. **Remove** the `HasCommittedHash` body (:3328–:3348).
2. **Repoint** the single call site (:3205, inside `RecoverAppendUnsafeAsync`) from `HasCommittedHash(...)` to `AppendRecoveryVerifier.HasCommittedHash(...)`.

Confirm via grep that no remaining bare `HasCommittedHash(` call site is left in the file after the edit.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier, `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue, `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`B0FaultSeam`/`RecoveryEvidenceVerifier`/`RecoveryEnvelopeCodec`/`AppendJournalCodec`/`AppendRecoveryAdapter` (all frozen), `AppendJournalWriter` (P19/P20), the `DurableWriter` durable-write leaves (P9–P16), `ArchiveService` (P17), `ArtifactIdentityMapper` (P18), the P21 verification predicates (`MatchesAppendMember`/`MatchesArchiveBytes`/`MatchesAppendState`) now in `AppendRecoveryVerifier`, NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, the read-side seam owners (`ListingService`, `SearchService`), and the recovery-orchestration methods (`RecoverAppendUnsafeAsync`, `ReplayArchiveMembers`, `PersistRecoveryRequiredOrThrow`, `VerifyAppendMemberAsync`, `AreAppendMembersCurrent`, `TryRestoreAppendTransaction`, `TryRemoveCreatedArchiveMembers`, `ReconcileArchiveMember`, `RestoreAppendMember`, `RecoveryRequired`, `DeleteAppendJournal`, `PruneCommittedArchives`).

## Acceptance

- `AppendRecoveryVerifier` owns the committed-hash proof; `FileMemoryStore` retains only the repointed call site within `RecoverAppendUnsafeAsync`.
- No new public API; `IMemoryStore` surface unchanged.
- No `FileMemoryStore` instance state referenced by `HasCommittedHash`.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendRecoveryVerifier|FullyQualifiedName~HasCommittedHash"
   ```
   Confirm `Passed!` / 0 failed.
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final summary per project. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
