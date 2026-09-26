# P25 — Append Recovery-Required Exception Builder Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P24 complete; durable-write leaves P9–P16, archive-version P17, entry-identity P18, append-journal write/delete P19–P20, verification predicates + committed-hash proof P21–P22, rollback-journal reader P24 in `AppendRollbackJournalReader`).
**Phase:** P25 (twenty-fifth seam extraction; append recovery-orchestration phase, second sub-seam).
**Goal:** move the append `RecoveryRequired` exception/info builder off the store into the append-contract file `AppendContracts.cs`, leaving the fault-injecting recovery orchestration resident.

## Why this phase

`RecoveryRequired` (`FileMemoryStore.cs` :3418–:3426) builds the append recovery-blocked exception:

```csharp
private static AppendStoreException RecoveryRequired(string journalPath, string detail,
    Exception? inner = null, string operationId = "recovery") =>
    new(AppendStoreErrorCode.RecoveryRequired, operationId,
        $"Append recovery is required at '{journalPath}': {detail}", inner,
        new AppendRecoveryInfo(
            journalPath,
            AppendJournalState.RecoveryRequired.ToString(),
            "resolve the retained append journal before retrying",
            detail));
```

It is a pure, self-contained exception factory:

- **Static, no instance state** — takes only `(string, string, Exception?, string?)`.
- **No fault injection** — unlike `VerifyAppendMemberAsync`/`ReconcileArchiveMember`/`RestoreAppendMember` (which carry `InjectAppendFault`), `RecoveryRequired` constructs no fault-injectable operation.
- **No durable write, no journal-state transition, no rollback bookkeeping, no orchestration.**
- **Cohesive contract owner** — it constructs exactly `AppendStoreException` + `AppendRecoveryInfo` (via `AppendStoreErrorCode.RecoveryRequired` and `AppendJournalState.RecoveryRequired`), the same frozen types `AppendContracts.cs` already declares (lines 46–91, 121–127, 129–138). It belongs beside them.

It has **15 call sites** within `FileMemoryStore.cs` spanning the write path (`StorePersistentAsync` :2290/:2302, `PublishFrozenArchiveAsync` :2750/:2764/:2780/:2785, `PublishLiveAppendAsync` :2808/:2822) and the recovery path (`RecoverAppendUnsafeAsync` :3170/:3201/:3206/:3213/:3223/:3231, `PersistRecoveryRequiredOrThrow` :3293). All use the same parameter names and defaults, so repointing is a pure receiver qualification — no argument changes.

The surrounding recovery-orchestration (`RecoverAppendUnsafeAsync`, `ReplayArchiveMembers`, `PersistRecoveryRequiredOrThrow`, `VerifyAppendMemberAsync`, `AreAppendMembersCurrent`, `TryRestoreAppendTransaction`, `TryRemoveCreatedArchiveMembers`, `ReconcileArchiveMember`, `RestoreAppendMember`) remains resident — it is genuinely entangled with fault injection, durable writes, and rollback bookkeeping, a larger later phase. `AreAppendMembersCurrent` (:3325) is a one-line AND over the already-extracted `AppendRecoveryVerifier.MatchesAppendMember` predicates and is correctly NOT extracted (low-value churn).

## The seam

Add the builder to `AppendContracts.cs` (`src/Scrinia.Core/AppendContracts.cs`) and repoint the 15 call sites.

### Extend `AppendContracts.cs` (`src/Scrinia.Core/AppendContracts.cs`)

Append a new `internal static` owner class (verbatim body, no injected hooks) at the end of the file:

```csharp
/// <summary>Factory for the append recovery-blocked exception, co-located with the
/// <see cref="AppendStoreException"/> / <see cref="AppendRecoveryInfo"/> / <see cref="AppendStoreErrorCode"/>
/// contract it builds. Pure builder: no fault injection, no durable write, no rollback bookkeeping.</summary>
internal static class AppendRecoveryBuilder
{
    /// <summary>Builds the <see cref="AppendStoreException"/> that signals an append needs operator
    /// recovery before it can be retried. <paramref name="operationId"/> defaults to "recovery";
    /// <paramref name="inner"/> defaults to null.</summary>
    public static AppendStoreException RecoveryRequired(string journalPath, string detail,
        Exception? inner = null, string operationId = "recovery") =>
        new(AppendStoreErrorCode.RecoveryRequired, operationId,
            $"Append recovery is required at '{journalPath}': {detail}", inner,
            new AppendRecoveryInfo(
                journalPath,
                AppendJournalState.RecoveryRequired.ToString(),
                "resolve the retained append journal before retrying",
                detail));
}
```

### Store wiring (`FileMemoryStore.cs`)

1. **Remove** the `RecoveryRequired` builder body (:3418–:3426).
2. **Repoint** the 15 call sites from `RecoveryRequired(` to `AppendRecoveryBuilder.RecoveryRequired(`, keeping every argument unchanged (the builder signature and defaults are identical): :2290, :2302, :2750, :2764, :2780, :2785, :2808, :2822, :3170, :3201, :3206, :3213, :3223, :3231, :3293.

Confirm via grep that no remaining bare `RecoveryRequired(` call site is left in `FileMemoryStore.cs` after the edit (the `AppendStoreErrorCode.RecoveryRequired` enum references and `AppendJournalState.RecoveryRequired` enum references are NOT call sites and stay untouched).

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier, `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue, `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`B0FaultSeam`/`RecoveryEvidenceVerifier`/`RecoveryEnvelopeCodec`/`AppendJournalCodec`/`AppendRecoveryAdapter` (all frozen — the builder uses these directly, unchanged), the `DurableWriter` durable-write leaves (P9–P16), `ArchiveService` (P17), `ArtifactIdentityMapper` (P18), `AppendJournalWriter` (P19/P20), `AppendRollbackJournalReader` (P24 — do NOT re-edit that owner), the P21/P22 verification predicates (`MatchesAppendMember`/`MatchesArchiveBytes`/`MatchesAppendState`/`HasCommittedHash`) in `AppendRecoveryVerifier`, NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, the read-side seam owners (`ListingService`, `SearchService`), and the recovery-orchestration methods (`RecoverAppendUnsafeAsync`, `ReplayArchiveMembers`, `PersistRecoveryRequiredOrThrow`, `VerifyAppendMemberAsync`, `AreAppendMembersCurrent`, `TryRestoreAppendTransaction`, `TryRemoveCreatedArchiveMembers`, `ReconcileArchiveMember`, `RestoreAppendMember`, `DeleteAppendJournal`, `PruneCommittedArchives`).

## Acceptance

- `AppendRecoveryBuilder` owns the `RecoveryRequired` exception factory; `FileMemoryStore` retains only the repointed call sites within its write/recovery orchestration.
- No new public `IMemoryStore` API; the builder is `internal` on `AppendContracts.cs`.
- No `FileMemoryStore` instance state referenced by the builder.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendRecoveryBuilder|FullyQualifiedName~RecoveryRequired"
   ```
   Confirm `Passed!` / 0 failed.
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
