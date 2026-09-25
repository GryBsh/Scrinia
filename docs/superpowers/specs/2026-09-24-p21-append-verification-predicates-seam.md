# P21 — Append-Member Verification Predicates Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P20 complete; durable-write/delete leaves in `DurableWriter` (P9–P16), archive-version leaf in `ArchiveService` (P17), entry-identity/normalization group in `ArtifactIdentityMapper` (P18), append-journal durable write in `AppendJournalWriter` (P19), append-journal durable delete in `AppendJournalWriter` (P20)).
**Phase:** P21 (twenty-first seam extraction; append recovery-orchestration phase, first sub-seam).
**Goal:** move the pure append-member verification predicate group (`MatchesAppendMember`, `MatchesArchiveBytes`, `MatchesAppendState`) off the store into a new static owner `AppendRecoveryVerifier`, leaving the store's recovery-orchestration methods intact and repointing the 6 internal call sites.

## Why this phase

The append recovery region (`RecoverAppendUnsafeAsync` and its helpers, `FileMemoryStore.cs` :3129–:3533) is a large, cohesive orchestration unit intermixing fault injection (`InjectAppendFault`), recovery verification (`RecoveryEvidenceVerifier`), journal state transitions, and rollback bookkeeping. It is not a single clean leaf. The cleanest self-contained sub-seam within it is the **append-member verification predicate group** — three pure static predicates with a single cohesive responsibility: *verify an append member's integrity against its expected hash for recovery/rollback decisions*.

- `MatchesAppendMember` (:3350) — verify one live append member (NewArtifact/NewSidecar).
- `MatchesArchiveBytes` (:3446) — verify one archived member's bytes.
- `MatchesAppendState` (:3487) — verify a member's append state (delegates to `MatchesAppendMember`).

They own **no** instance state and depend only on frozen primitives: `RecoveryEvidenceVerifier.VerifySingleAsync` (frozen static), `RecoveryScopeBinding` (frozen model), `RecoveryEvidenceKind` (frozen), `MaintenanceLimits` (frozen), `RecoveryVerificationOutcome` (frozen), `RecoveryVerificationResult`/`RecoveryExpectedHash`/`RawMemberSnapshot` (frozen models), `File`/`Path` (system). They perform **no** durable write, **no** fault injection (`InjectAppendFault`), and **no** rollback bookkeeping. This is the pure verification core of the recovery region.

The remaining recovery methods are correctly excluded:
- `RecoverAppendUnsafeAsync` (:3133) — recovery orchestration (journal verify, parse, state transitions, fault injection).
- `ReplayArchiveMembers` (:3253), `PersistRecoveryRequiredOrThrow` (:3280) — recovery orchestration.
- `VerifyAppendMemberAsync` (:3309) — recovery verification with `InjectAppendFault(VerificationRead)`.
- `AreAppendMembersCurrent` (:3325) — pure boolean combiner over `MatchesAppendMember`; stays resident (a thin wrapper, companion to the predicates — extract in a later phase).
- `HasCommittedHash` (:3328) — recovery verification + `SHA256` hash comparison; more stateful, a later phase.
- `TryRestoreAppendTransaction` (:3362) — rollback orchestration.
- `ReconcileArchiveMember` (:3412) — rollback entangled with `InjectAppendFault(RollbackDelete)` + recovery verification.
- `RestoreAppendMember` (:3457) — rollback dual-path (durable write + delete + recovery verification).
- `RecoveryRequired` (:3490) — frozen exception builder.

## The seam

Move the three predicates verbatim into a new static owner `AppendRecoveryVerifier`. The bodies are unchanged (no `FileMemoryStore` instance state; `RecoveryScopeBinding` is constructed with a string scope + path, both already available as parameters).

### New owner: `AppendRecoveryVerifier` (`src/Scrinia.Core/AppendRecoveryVerifier.cs`)

A static owner holding only the three pure verification predicates. No `FileMemoryStore` instance, no injected hooks (frozen primitives only):

```csharp
namespace Scrinia.Core;

/// <summary>
/// Owns the pure append-member verification predicates moved off <see cref="FileMemoryStore"/> in
/// P21. Holds no <see cref="FileMemoryStore"/> instance — no fault injection, no durable write, no
/// rollback bookkeeping. These predicates verify append member integrity against expected SHA-256
/// hashes (via the frozen <see cref="RecoveryEvidenceVerifier"/>) for recovery/rollback decisions.
/// </summary>
internal static class AppendRecoveryVerifier
{
    public static bool MatchesAppendMember(RawMemberSnapshot expected, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(expected.Path))
            return false;
        RecoveryVerificationResult verification = RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding("append", Path.GetDirectoryName(expected.Path)!), RecoveryEvidenceKind.AppendJournal,
            expected.Path, new RecoveryExpectedHash(expected.Sha256), MaintenanceLimits.Hard,
             cancellationToken).AsTask().GetAwaiter().GetResult();
        verification.SourceContent?.Dispose();
        return verification.Outcome == RecoveryVerificationOutcome.Verified;
    }

    public static bool MatchesArchiveBytes(string path, RawMemberSnapshot expected,
        CancellationToken cancellationToken = default)
    {
        RecoveryVerificationResult verification = RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding("append", Path.GetDirectoryName(path)!), RecoveryEvidenceKind.AppendJournal,
            path, new RecoveryExpectedHash(expected.Sha256), MaintenanceLimits.Hard,
            cancellationToken).AsTask().GetAwaiter().GetResult();
        verification.SourceContent?.Dispose();
        return verification.Outcome == RecoveryVerificationOutcome.Verified;
    }

    public static bool MatchesAppendState(RawMemberSnapshot expected) =>
        expected.Existed ? MatchesAppendMember(expected) : !File.Exists(expected.Path);
}
```

### Store wiring (`FileMemoryStore.cs`)

Four edits:

1. **Remove the three predicate bodies** (:3350–:3360 `MatchesAppendMember`, :3446–:3455 `MatchesArchiveBytes`, :3487–:3488 `MatchesAppendState`).

2. **Repoint `MatchesAppendMember`** — replace each call with `AppendRecoveryVerifier.MatchesAppendMember`:
   - :3326 `AreAppendMembersCurrent` (2 calls)
   - :3464 `RestoreAppendMember` (2 calls)
   - :3473 `RestoreAppendMember` (1 call)
   - (:3488 `MatchesAppendState`'s internal call disappears with the move of `MatchesAppendState`.)

3. **Repoint `MatchesArchiveBytes`** — replace each call with `AppendRecoveryVerifier.MatchesArchiveBytes`:
   - :3427 `ReconcileArchiveMember`
   - :3431 `ReconcileArchiveMember`

4. **Repoint `MatchesAppendState`** — replace each call with `AppendRecoveryVerifier.MatchesAppendState`:
   - :3369 `TryRestoreAppendTransaction` (2 calls)

Confirm via grep that no remaining bare `MatchesAppendMember(` / `MatchesArchiveBytes(` / `MatchesAppendState(` call site is left in the file after these edits.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier, `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue, `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`B0FaultSeam`/`RecoveryEvidenceVerifier`/`RecoveryEnvelopeCodec`/`AppendJournalCodec`/`AppendRecoveryAdapter` (all frozen), `AppendJournalWriter` (P19/P20), the `DurableWriter` durable-write leaves (P9–P16), `ArchiveService` (P17), `ArtifactIdentityMapper` (P18), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, the read-side seam owners (`ListingService`, `SearchService`), and the recovery-orchestration methods (`RecoverAppendUnsafeAsync`, `ReplayArchiveMembers`, `PersistRecoveryRequiredOrThrow`, `VerifyAppendMemberAsync`, `AreAppendMembersCurrent`, `HasCommittedHash`, `TryRestoreAppendTransaction`, `ReconcileArchiveMember`, `RestoreAppendMember`, `RecoveryRequired`, `DeleteAppendJournal`, `PruneCommittedArchives`).

## Acceptance

- `AppendRecoveryVerifier` owns the three pure verification predicates; `FileMemoryStore` retains only the repointed call sites within resident recovery-orchestration methods.
- No new public API; `IMemoryStore` surface unchanged.
- No `FileMemoryStore` instance state referenced by the moved predicates.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendRecoveryVerifier|FullyQualifiedName~MatchesAppendMember|FullyQualifiedName~MatchesArchiveBytes|FullyQualifiedName~MatchesAppendState"
   ```
   Confirm `Passed!` / 0 failed.
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final summary per project. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
