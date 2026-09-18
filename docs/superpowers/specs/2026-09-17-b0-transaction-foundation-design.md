# B0 Transaction Foundation and Compatibility Harness — Design Specification

**Status:** DRAFT
**Date:** 2026-09-17
**Predecessor:** `docs/superpowers/specs/2026-09-10-phase-b-roadmap-design.md`
**Scope authority:** Phase B umbrella roadmap §4 (B0 boundary and deliverable), §5 (dependencies), §8 (acceptance gates), §9 (evidence protocol), §10 (split criteria), §11 (Phase B blocker checklist).

This is the B0 child spec under the Phase B roadmap. It defines the reusable transaction
foundation that B1–B6 consume. B0 adds abstractions and test infrastructure only. It does not change
the behavior of `remember`, `compact`, import, migration, bundle writes, sidecar mutation, or
append. The roadmap §4 B0 boundary is the authority for what B0 may and may not do.

## 1. Goal, authority, and scope

B0 provides the shared transaction primitives that every later subproject builds on: one canonical
lifecycle enum, one result vocabulary, one durable-format registry, one fault-seam contract, and one
compatibility harness. B1–B6 each consume these primitives. They must not redefine them. The roadmap
§5 dependency graph places B0 at the root; B1 and B2 follow B0 and may run in parallel only after B0's
contract and fixtures are accepted.

Scope authority for this spec is the Phase B roadmap. Every non-goal in Section 2, every compatibility
constraint in Section 11, and every acceptance gate in Section 13 restates a roadmap clause. B0 does
not extend the roadmap. It makes the roadmap's B0 boundary executable.

## 2. Goal and non-goals

### Goal

Define the reusable transaction foundation that B1–B6 consume, without changing any existing
behavior. B0 introduces:

- one canonical recovery lifecycle enum and a read-only mapper that projects it;
- one unified Core result vocabulary (`B0Result`) and thin wrappers over the existing append,
  whole-memory, multi-memory, and recovery results;
- one centralized `DurableFormats` registry that all consumers import;
- one `B0FaultSeam` contract that unifies the existing test-only injection seams;
- a compatibility harness and golden fixtures that prove current evidence still loads.

B0 adds internal abstractions and deterministic fault-injection test infrastructure. It changes no
public MCP or CLI contract, adds no durable file, journal, state, migration, or archive rule, and
introduces no new recovery outcome.

### Non-goals

- **Zero behavior change except one justified change.** The only permitted behavior change is the
  typed member carrier in Section 6 (justified in Section 12). Every other section preserves existing
  behavior exactly.
- **No public MCP/CLI contract change.** `McpResponse`, its `ErrorCodes`, the CLI facade, and all
  public result shapes remain unchanged except for the thin `B0Result` wrappers defined in Section 6.
- **No new durable file, journal, state, migration, archive rule, or recovery outcome.** The sole
  parser boundary in Section 4 (B5-owned) is untouched.
- **No cross-scope, remote, plugin, HTTP, or gateway work.** B0 stays inside Core and its test
  project. It adds no server surface, no remote CLI, no API-key, and no plugin guarantee.
- **No rewrite of existing state machines.** Section 5 adds a mapper; it does not change the append,
  whole-memory, multi-memory, or restore state transitions that B1–B6 already depend on.
- **No format evolution.** NMP/2, sidecar schemas, and archive layout remain unchanged. That work
  belongs to B6 and is explicitly out of scope for B0.
- **No reuse of B1–B6 or shared-base contracts.** B0 defines the primitives; it does not consume
  results from the children it feeds.

## 3. Locked decisions

The following five decisions are locked and user-approved. B0 implements them exactly. Any deviation
requires a new approved decision.

1. **Both shared vocabulary and fixtures/harness.** B0 delivers the shared transaction vocabulary
   (Sections 5, 6, 7) *and* the compatibility harness with golden fixtures (Section 9). Both are
   required, not optional.
2. **Behavior lock with one justified change.** B0 preserves existing behavior everywhere except the
   typed member carrier in Section 6. The justification lives in Section 12. No other change is
   permitted without separate user approval.
3. **One lifecycle enum.** `RecoveryLifecycleState` (RecoveryContracts.cs:15–24) is the canonical
   lifecycle enum. Section 5 adds a mapper that projects it. No second lifecycle enum is created.
4. **Move to typed member carrier.** The result vocabulary carries typed members instead of raw
   snapshots. This is the single justified behavior change.
5. **Centralize DurableFormats registry.** Section 7 standardizes the existing centralized
    DurableFormats registry (Formats/DurableFormats.cs:15) as the single registry owner and routes
    every consumer through it; FormatContract, FormatDisposition, and FormatVersion.Evaluate each
    already have one definition in Formats/ and are not duplicated or relocated. No second registry
    exists.

## 4. Current foundation map

The table below is the authoritative evidence inventory for B0's base. Line numbers are review
anchors; symbol names and call purpose are authoritative. All coordinators and journals live in the
`Scrinia.Core` namespace.

### 4.1 Append foundation

| Symbol | Location | Evidence |
|---|---|---|
| `AppendTransaction` | `src/Scrinia.Core/AppendTransaction.cs` | Frozen pre-publication record, lines 102–122: `OperationId`, `MemoryAddress Address`, `Scope`, `IsCreate`, `RawMemberSnapshot` `OldArtifact`/`OldSidecar`/`NewArtifact`/`NewSidecar`, `ArchivePlan`, `StartedAt`. `RawMemberSnapshot` enforces SHA-256 at capture. |
| `AppendJournal` | `src/Scrinia.Core/AppendJournal.cs` | `AppendJournalState` enum lines 11–29: `Prepared`, `ArchiveInProgress`, `Archived`, `NewPairInProgress`, `Committed`, `CleanupPending`, `RecoveryRequired`, `RolledBack`. `AppendJournalMember` lines 94–131: `path`, `existed`, `payload` base64, `sha256`. `AppendJournalCodec.IsLegalTransition` lines 405–415; `WithState` lines 242–256 throws `AppendJournalException(Malformed)` on illegal move; `ConsumeParsed` line 190; `CreatePrepared` line 199; `ToEnvelope` line 224; `Serialize` line 227; `ValidateForRecovery` line 258. |

### 4.2 Whole-memory foundation

| Symbol | Location | Evidence |
|---|---|---|
| `WholeMemoryTransaction` | `src/Scrinia.Core/WholeMemoryTransaction.cs` | Lines 6–41: `operationId` caller-synthesized GUID, `scope`, `target`, `isEphemeral`, captured old/new `RawMemberSnapshot`, `ArchivePairPlan`, `StartedAt`. `WholeMemoryOutcome` lines 100–106: `Created`, `Replaced`, `SuccessWithWarning`, `RecoveryRequired`. `RecoveryInfo` line 192. `FaultPoint` lines 298–309. `WholeMemoryErrorCode` line 170. |
| `WholeMemoryTransactionCoordinator` | `src/Scrinia.Core/WholeMemoryTransactionCoordinator.cs` | `ExecuteAsync` lines 16–119 drives `Prepared→ArchiveInProgress→Archived→NewPairInProgress→Committed` via `PersistState`. `TryRollback` line 142. `FailOnceAt` / `ThrowIfFault`. |

### 4.3 Multi-memory foundation

| Symbol | Location | Evidence |
|---|---|---|
| `MultiMemoryTransaction` | `src/Scrinia.Core/MultiMemoryTransaction.cs` | `MultiMemoryTransactionState` lines 6–11: `Prepared`, `Committed`, `RecoveryRequired`. `MultiMemoryOutcome` lines 13–20: `Committed`, `CommittedCleanupPending`, `RolledBack`, `RecoveryRequired`, `Rejected`. `MultiMemoryFailure` lines 22–38: `Admission`, `Prepare`, `DecisionPublication`, `ParticipantCommit`, `Verification`, `Rollback`, `Cleanup`, `Malformed`, `UnsupportedVersion`, `Unsafe`, `Inconsistent`, `Ambiguous`, `ReplayIoFailed`, `LockTimeout` (14 members). `MultiMemoryTransactionTransition` lines 40–46: `PreparedPublished`, `CommittedPublished`, `CompletionProofPublished`, `RecordDeleted`. `MultiMemoryAggregateResult` lines 394–600. |
| `MultiMemoryTransactionRecord` | `src/Scrinia.Core/MultiMemoryTransactionRecord.cs` | Coordinator record lines 7–64: `format`, `version`, `transactionId`, `scope`, `state`, `participants`, `completedParticipants`, `transactionalParticipants` `B4ParticipantRecoveryState[]`, `rejectedTransactionalParticipants` `B4ParticipantRequestMetadata[]`. `MultiMemoryTransactionStateJsonConverter` lines 119–145 wire strings `prepared`/`committed`/`recoveryRequired`. `MultiMemoryTransactionRecordCodec.Validate` lines 311–400. `FailureInjector` `AsyncLocal` line 14. `ThrowIfInjected` lines 927–932. |

### 4.4 File-entity foundation

| Symbol | Location | Evidence |
|---|---|---|
| `FileEntityTransactionCoordinator` | `src/Scrinia.Core/FileEntityTransactionCoordinator.cs` | `ExecuteAsync` lines 24–133: capture → write-prepared-evidence → archive → publish → verify → write-committed-evidence → delete-evidence. `_faultPoints` `Queue` lines 11–22. `FailOnceAt` line 18. `ThrowIfFault` lines 432–442. `ExtractOperationId` line 409. `FileEntityFaultPoint` (`WholeMemoryTransaction.cs:298`): `RawBodyRead`, `RawMetaRead`, `ArchiveBodyWrite`, `ArchiveMetaWrite`, `BodyWrite`, `MetaWrite`, `VerificationRead`, `AfterBodyDelete`, `RollbackWrite`, `EvidenceCleanup`. |

### 4.5 Recovery vocabulary

| Symbol | Location | Evidence |
|---|---|---|
| `RecoveryContracts` | `src/Scrinia.Core/RecoveryContracts.cs` | `RecoveryOperation` lines 7–12: `Restore`, `Append`, `FileEntity`. `RecoveryLifecycleState` lines 15–24: `Prepared`, `Applying`, `Committed`, `CleanupPending`, `Cleaned`, `RecoveryRequired`, `RolledBack` (wire strings lowercase). `RecoveryBlock` lines 80–112. `RecoveryBlockReason` lines 69–78: `Malformed`, `Unsafe`, `UnsupportedVersion`, `Ambiguous`, `Inconsistent`, `ReplayIoFailed`, `WrongScope`. `RecoveryOutcome` lines 229–266; constructor invariant: a blocked outcome must carry a `RecoveryBlock`. `RecoveryCandidate` lines 47–57. `RecoveryDiscoveryResult` line 114. `IRecoveryAdapter` lines 206–222: `Operation`, `IsCandidateFile`, `ConsumeParsed(in RecoveryVerifiedEnvelope)`, `ValidateForMutation(...)`, `RecoverOrCleanup(...)`, `Admit(...)`. |
| `RecoveryEnvelope` | `src/Scrinia.Core/RecoveryEnvelope.cs` | `RecoveryEnvelopeDocument` lines 28–45: `Format`, `Version`, `Operation`, `BodyVersion`, `OperationId`, `Scope`, `Address?`, `State` (`RecoveryLifecycleState`), `CreatedAt`, `UpdatedAt`, `Body` (raw `JsonElement`), two `[JsonIgnore]` derived members `IsLegacy` and `TypedBody` (`RecoveryBody?`). `RecoveryBody` subtypes: `AppendRecoveryBody` lines 38–51, `RestoreRecoveryBody` lines 53–56, `FileEntityRecoveryBody` lines 64–73, `RecoveryMemberSnapshot` lines 58–62. All extend abstract `RecoveryBody`. `RecoveryEnvelopeCodec.ParseTypedBodyDocument` line 204 (Parse and ParseTypedBody removed). |

### 4.6 Sole parser boundary (B5-owned, do NOT change)

The sole parser boundary is `RecoveryEnvelopeCodec.ParseVerifiedEnvelope` in
`src/Scrinia.Core/RecoveryVerification.cs` lines 297–343. It takes six arguments and returns
`RecoveryVerifiedEnvelope`:

```csharp
RecoveryVerifiedEnvelope ParseVerifiedEnvelope(
    in RecoveryVerificationContent content,
    in RecoveryScopeBinding expectedScope,
    string evidencePath,
    string expectedEvidenceDirectory,
    RecoveryEvidenceKind evidenceKind,
    in RecoveryVerifiedFileMetadata metadata)
```

Typed consumers perform no I/O, parse, or validation. They receive the already-parsed envelope:
`AppendJournalCodec.ConsumeParsed` (AppendJournal.cs:190), `RestoreJournalCodec.ConsumeParsed`
(RestoreJournal.cs:111), `FileEntityRecoveryAdapter.ConsumeParsed` (RecoveryEnvelope.cs:794),
`AppendRecoveryAdapter.ConsumeParsed` (RecoveryEnvelope.cs:567), `RestoreRecoveryAdapter.ConsumeParsed`
(RecoveryEnvelope.cs:706), and `RecoveryVerifiedEnvelope.TypedBody` (RecoveryVerification.cs:294).

Supporting types (all in `RecoveryVerification.cs` unless noted): `RecoveryVerificationContent`
lines 22–40 (`byte[] Bytes` + `Metadata`, disposed via `CryptographicOperations.ZeroMemory`),
`RecoveryVerificationResult` lines 65–75, `RecoveryVerificationOutcome` lines 42–54,
`RecoveryEvidenceKind` lines 9–15 (`AppendJournal=1`, `RestoreJournal=2`, `FileEntityJournal=3`,
`MultiMemoryTransactionRecord=4`), `RecoveryScopeBinding` line 7 (value struct with `Scope` and
`CanonicalScopeDirectory`).

### 4.7 DurableFormats registry

`src/Scrinia.Core/Formats/DurableFormats.cs` line 15 holds the single registry. Current values:
`ArtifactSidecar=1`, `EntityFacts=3`, `Nmp2Body=2`, `BundleEntry=(ArtifactSidecar.Current)`,
`BundleManifest=2`, `LegacyIndex=3`, `SkillMeta=1`, `AgentMeta=1`, `RestoreJournal=1`,
`AppendJournal=1`, `RecoveryEnvelope=1`, `MultiMemoryTransaction=1`.

`FormatContract` (`Formats/FormatContract.cs:18`): `Name`, `Current`, `AbsentMeans`,
`FormatDisposition OnOlder`, `FormatDisposition OnNewer`. `FormatDisposition`
(`Formats/FormatDisposition.cs:5`): `Load`, `LoadReadOnly`, `Rebuild`, `Refuse`.
`FormatVersion.Evaluate` (`Formats/FormatVersion.cs:15`): an `AbsentMeans` of `null` ⇒ `Refuse`.
All recovery, journal, envelope, and coordinator contracts use `Refuse` for unknown versions.

### 4.8 DurableFile write phase

`src/Scrinia.Core/DurableFile.cs` line 147 declares `DurableWritePhase`: `BeforeWrite`,
`BeforeFlush`, `BeforeMove`. This is the foundational durable-write seam that the unified fault seam
in Section 8 extends.

### 4.9 Existing fault seams (all test-only, `AsyncLocal`, production-null)

| Seam | Location | Members |
|---|---|---|
| `DurableFile.FailureInjector` | `DurableFile.cs:13–20` | `AsyncLocal<Func<DurableWritePhase,string,Exception?>?>` + internal static `Func`. `ThrowInjected` line 127; consumed at lines 92, 117, 123, 127–131. |
| `FileMemoryStore.AppendFaultInjector` | `FileMemoryStore.cs:45–52` | `AsyncLocal<Func<AppendFaultPoint,string,string,Exception?>?>`. `FailOnceAt` line 81; `PreCommitFaultPoints` line 60. |
| `FileMemoryStore.WholeMemoryFaultInjector` | `FileMemoryStore.cs:46–57` | `AsyncLocal<Func<WholeMemoryFaultPoint,string,Exception?>?>`. `FailOnceAt` line 90. |
| `FileEntityTransactionCoordinator._faultPoints` | `FileEntityTransactionCoordinator.cs:11–22` | `Queue<FileEntityFaultPoint>`. `FailOnceAt` line 18; `ThrowIfFault` lines 432–442. |
| `RestoreJournalFailureInjector` | `FileMemoryStore.cs:124` | `AsyncLocal<Func<RestoreJournalTransition,string,Exception?>?>`. `ThrowIfRestoreJournalTransitionFails` line 4942. |
| `RestoreJournal.DecodeObserver` | `RestoreJournal.cs:118` | `AsyncLocal<Action?>`. |
| `MultiMemoryTransactionCoordinator.FailureInjector` | `MultiMemoryTransactionCoordinator.cs:14` | `AsyncLocal<Func<MultiMemoryTransactionTransition,string,Exception?>?>`. `ThrowIfInjected` lines 927–932. |
| `AppendFaultPoint` | `AppendContracts.cs:140` | 24 members: `RawArtifactRead`, `RawSidecarRead`, `JournalWrite`, `JournalFlush`, `JournalParse`, `JournalDelete`, `ArchiveArtifactWrite`, `ArchiveArtifactFlush`, `ArchiveSidecarWrite`, `ArchiveSidecarFlush`, `LiveArtifactWrite`, `LiveArtifactFlush`, `LiveSidecarWrite`, `LiveSidecarFlush`, `VerificationRead`, `RollbackWrite`, `RollbackDelete`, `ArchiveCleanup`, `PruningDelete`, `BeforeEvent`, `AfterEvent`. |
| `WholeMemoryFaultPoint` | `WholeMemoryTransaction.cs:176` | `RawArtifactRead`, `RawSidecarRead`, `EvidenceWrite`, `ArchiveArtifactWrite`, `ArchiveSidecarWrite`, `LiveArtifactWrite`, `LiveSidecarWrite`, `VerificationRead`, `RollbackWrite`, `EvidenceCleanup`, `ArchiveCleanup`, `CleanupStateWrite`. |
| `RestoreJournalTransition` | `FileMemoryStore.cs:17` | `BeforeRecoveryBarrierRead`, `BeforePreparedWrite`, `AfterPreparedWrite`, `BeforeReplayBody`, `BeforeReplaySidecar`, `AfterReplayDestinations`, `BeforeCompletedWrite`, `AfterCompletedWrite`, `BeforeCleanupDelete`. |
| `MultiMemoryTransactionTransition` | `MultiMemoryTransaction.cs:40` | `PreparedPublished`, `CommittedPublished`, `CompletionProofPublished`, `RecordDeleted`. |

### 4.10 Public result vocabulary divergence (Core → MCP → CLI)

The result vocabulary is currently duplicated across three layers. B0 unifies the Core surface behind
`B0Result` without changing the MCP or CLI passthrough.

| Layer | Append | Whole-memory | Multi-memory | Recovery |
|---|---|---|---|---|
| Core | `AppendStoreResult` (`AppendContracts.cs:11–122`), `AppendStoreOutcome` (line 19: `Created`, `Appended`, `SuccessWithWarning`, `RecoveryRequired`), `AppendStoreWarning` (line 34), `AppendWarningCode` (line 37), `AppendStoreException` (line 46), `AppendStoreErrorCode` (line 121), `AppendRecoveryInfo` (line 134), `AppendOperationCanceledException` (line 107), `AppendPostCommitCleanupException` (line 93) | `WholeMemoryResult` internal record (line 108), `WholeMemoryOutcome` (line 100: `Created`, `Replaced`, `SuccessWithWarning`, `RecoveryRequired`), `WholeMemoryTransactionException` (line 145), `WholeMemoryErrorCode` (line 170), `RecoveryInfo` (line 192: `EvidencePath`, `State`, `Action`, `Detail`) | `MultiMemoryAggregateResult` (line 394), `MultiMemoryOutcome` (line 13), `MultiMemoryFailure` (line 22, 14 members) | `RecoveryBlock` (line 80), `RecoveryBlockReason` (line 69), `RecoveryOutcome` (line 229), `RecoveryCandidate` (line 47), `RecoveryDiscoveryResult` (line 114) |
| Envelope failures | `AppendJournalFailure` (`AppendJournal.cs:31`) | — | `TransactionalFailureCode` (`MultiMemoryTransaction.cs:57`) | `RecoveryEnvelopeFailure` (`RecoveryEnvelope.cs:9`), `RestoreRecoveryFailure` (`RestoreJournal.cs:41`) |
| MCP | `McpResponse.ErrorCodes` (`McpResponse.cs:10`): `InvalidParameter`, `InvalidAction`, `InvalidPath`, `NotFound`, `DurableWriteFailed`, `RecoveryBlocked`, `RecoveryRequired`, `InvalidBundle`, `Internal`, `AppendCancelled`, `NotSupported`, `UnsupportedInputForm`, `MalformedAddress` | `McpResponse` (`McpResponse.cs:57`): `Status`, `IsError`, `IsWarning`, `Action`, `Path`, `PersistedPath`, `OperationId`, `Scope`, `JournalPath`, `Reason`, `Instruction`, `ActionNeeded[]`, `Info[]`, `FollowUp[]`, `Content`, `Error`, `ErrorCode` | |
| CLI | `CliJsonContext` roots (`CliJsonContext.cs:41`) — `CliMcpOutput` is the append/whole-memory/multi-memory passthrough carrying raw YAML from MCP. `ScriniaMcpJsonContext` registers `AppendStoreResult` + sub-types only (`MemoryTools.Skills.cs:49–54`). `MultiMemoryAggregateResult` and `RecoveryBlock`/`RecoveryBlockReason` cross Core → MCP → CLI. | | |

### 4.11 Test fault seams for reference

`FileMemoryStore.InjectWholeFault` / `InjectAppendFault` (`FileMemoryStore.cs:113`, `110`).
`LegacyMigrationState` lines 400–450: `WriteJournal` writes `prepared` first, `Recover` treats bare
`complete` as clean completion, lines 424–425 reject `complete` without `prepared`. The closest
existing bare-complete test is `tests/Scrinia.Tests/LegacyMigrationServiceTests.cs` lines 161–184
(`CompletedJournalWithTrailingContentIsRejectedByInspectAndMigrate`; `InlineData` includes `complete`
at line 162, `complete\nnot-base64|-` at line 163, `complete\nprepared` at line 164).

### 4.12 B1 scope-contamination audit paths

The following paths appear in the B1 scope-contamination audit (CONTEXT.md, none in the B2 Task 1–9
file map). B0 must not introduce new entries on this list:

- `docs/superpowers/plans/2026-09-11-journal-convergence.md` (B2 plan — planning artifact)
- `docs/superpowers/specs/2026-09-15-b5-retention-maintenance-design.md` (B5 design — planning artifact, not B2)
- `src/Scrinia.Core/AppendContracts.cs` (production code — B0/append-hardening epoch)
- `src/Scrinia.Core/WholeMemoryTransactionCoordinator.cs` (production code — whole-memory-transactions phase, excluded from B2)
- `tests/Scrinia.Tests/Formats/FormatVersionTests.cs` (test code — durable-format-versioning)
- `tests/Scrinia.Tests/Formats/RegisteredFormatsTests.cs` (test code — durable-format-versioning)
- `tests/Scrinia.Tests/RecoveryBlockedFailureClassCoverageTests.cs` (test code — B1 recovery-worktree)
- `tests/Scrinia.Tests/TestHelpers.cs` (test infra — all phases)

### 4.13 B2 parked bare-complete finding

This finding lives in `docs/superpowers/ledgers/2026-09-17-b2-gap-closure-acceptance.md` line 26 and
`CONTEXT.md` lines 52–61. It is **not** in `AGENTS.md`. The closest existing test is
`LegacyMigrationServiceTests.CompletedJournalWithTrailingContentIsRejectedByInspectAndMigrate`. B0
does not touch this finding; it preserves the migration behavior it documents.

## 5. Design — Section 1: canonical lifecycle enum and mapper

`RecoveryLifecycleState` (`RecoveryContracts.cs:15–24`) is the canonical lifecycle enum. Its seven
values are `Prepared`, `Applying`, `Committed`, `CleanupPending`, `Cleaned`, `RecoveryRequired`, and
`RolledBack`. Wire strings are lowercase; the converter lives in `RecoveryEnvelope.cs:96`.

B0 adds `RecoveryLifecycleStateMapper`, a read-only projection from the canonical enum to every
string and to the four existing machine-specific lifecycle surfaces. The mapper does **not** rewrite
any existing state machine. The following state machines remain authoritative and unchanged:

- `AppendJournalState` (`AppendJournal.cs:11–29`);
- `WholeMemoryOutcome` (`WholeMemoryTransaction.cs:100–106`) with `RecoveryInfo` (line 192);
- `MultiMemoryTransactionState` (`MultiMemoryTransaction.cs:6–11`) and `MultiMemoryOutcome` (lines
  13–20);
- `RecoveryLifecycleState` wire strings (`RecoveryEnvelope.cs:96`).

The mapper's contract is:

```csharp
internal static class RecoveryLifecycleStateMapper
{
    // Canonical enum -> lowercase wire string (matches RecoveryEnvelope.cs:96 converter).
    public static string ToWireString(RecoveryLifecycleState state);

    // Canonical enum -> the closest existing machine-specific lifecycle value, when one exists.
    public static string? ToAppendJournalState(RecoveryLifecycleState state);
    public static string? ToWholeMemoryOutcome(RecoveryLifecycleState state);
    public static string? ToMultiMemoryState(RecoveryLifecycleState state);
}
```

Each mapping is a pure function of the input enum. The mapper contains no I/O, no write path, no
transition logic, and no mutation of any coordinator. It is read-only at the Core boundary. It exists
so that B1–B6 can translate a canonical lifecycle into the machine-specific vocabulary without each
child re-implement the translation. The roadmap deferred areas (§3 items 1 and 2 — apply the append
protocol to `remember`/`compact`/import/migration/bundle, and unify append/restore journals) depend
on this mapper but do not execute it. Section 12 reasserts that the mapper changes no behavior.

## 6. Design — Section 2: unified B0Result vocabulary and typed RecoveryMember carrier

### 6.1 B0Result

B0 introduces `B0Result`, one Core result record that wraps the four existing Core results. The
existing records (`AppendStoreResult`, `WholeMemoryResult`, `MultiMemoryAggregateResult`,
`RecoveryOutcome`) keep their current shapes and semantics. `B0Result` is a thin, unifying facade
over them; it does not replace or re-shape any of them.

```csharp
internal sealed record B0Result(
    B0Outcome Outcome,
    B0Failure? Failure,
    ArtifactEntry? Artifact,
    string OperationId,
    string CorrelationId,
    RecoveryInfo? Recovery,
    string? Warning);
```

`B0Outcome` unions the existing outcome enums into one surface:

| Union member | Adopted from |
|---|---|
| `Created` | `AppendStoreOutcome.Created`, `WholeMemoryOutcome.Created` |
| `Appended` | `AppendStoreOutcome.Appended` |
| `Replaced` | `WholeMemoryOutcome.Replaced` |
| `Committed` | `MultiMemoryOutcome.Committed` |
| `CommittedCleanupPending` | `MultiMemoryOutcome.CommittedCleanupPending` |
| `RolledBack` | `MultiMemoryOutcome.RolledBack` |
| `Rejected` | `MultiMemoryOutcome.Rejected` |
| `SuccessWithWarning` | `AppendStoreOutcome.SuccessWithWarning`, `WholeMemoryOutcome.SuccessWithWarning` |
| `RecoveryRequired` | all four `RecoveryRequired` variants |

`B0Failure` unifies the four failure taxonomies into one discriminated container. Each existing
taxonomy keeps its members; `B0Failure` records which taxonomy produced it.

| Source taxonomy | Adopted members |
|---|---|
| `AppendStoreErrorCode` (`AppendContracts.cs:121`) | `RecoveryRequired`, `Internal` |
| `WholeMemoryErrorCode` (`WholeMemoryTransaction.cs:170`) | `RecoveryRequired`, `Internal` |
| `MultiMemoryFailure` (`MultiMemoryTransaction.cs:22`) | `Admission`, `Prepare`, `DecisionPublication`, `ParticipantCommit`, `Verification`, `Rollback`, `Cleanup`, `Malformed`, `UnsupportedVersion`, `Unsafe`, `Inconsistent`, `Ambiguous`, `ReplayIoFailed`, `LockTimeout` |
| `RecoveryBlockReason` (`RecoveryContracts.cs:69`) | `Malformed`, `Unsafe`, `UnsupportedVersion`, `Ambiguous`, `Inconsistent`, `ReplayIoFailed`, `WrongScope` |

`B0Result` unifies correlation and recovery detail:

- `OperationId` is the single correlation field. It carries `AppendTransaction.OperationId`
  (`AppendTransaction.cs:102`), `WholeMemoryTransaction.operationId` (`WholeMemoryTransaction.cs:6`),
  or `MultiMemoryTransactionRecord.transactionId` (`MultiMemoryTransactionRecord.cs:7`). B0 does not
  invent a new identifier; it accepts whichever the owning operation produced.
- `TransactionId` collapses to the same field as `OperationId` for append and whole-memory, and to
  `MultiMemoryTransactionRecord.transactionId` for multi-memory. `B0Result` exposes one accessor,
  `CorrelationId`, so children read a single field.
- `RecoveryInfo` is the shared recovery-detail record (`WholeMemoryTransaction.cs:192`: `EvidencePath`,
  `State`, `Action`, `Detail`). It is already shared by whole-memory and file-entity; B0 extends it to
  append and multi-memory via thin wrappers.

The four existing results map into `B0Result` through thin, read-only wrappers. Each wrapper copies
the outcome, carries any existing `AppendStoreWarning`/`WholeMemoryWarning` into `B0Result.Warning`,
and carries any existing `RecoveryInfo` into `B0Result.Recovery`. The wrappers perform no behavior
change; they re-express existing data in one vocabulary.

### 6.2 Typed member carrier (the one justified change)

The append and whole-memory records capture member evidence through `RawMemberSnapshot`:
`AppendTransaction.OldArtifact/OldSidecar/NewArtifact/NewSidecar` (`AppendTransaction.cs:102–122`) and
`WholeMemoryTransaction` old/new `RawMemberSnapshot` (`WholeMemoryTransaction.cs:6–41`). `RecoveryMemberSnapshot`
already exists as a `RecoveryBody` subtype (`RecoveryEnvelope.cs:58–62`) and the recovery envelope
carries `RecoveryMemberSnapshot` (`RecoveryEnvelope.cs:58–62`) via `FileEntityRecoveryBody`
(`RecoveryEnvelope.cs:64–73`).

B0 moves the result vocabulary from raw snapshots to a typed member carrier. `B0Result` and its thin
wrappers carry `RecoveryMember` (a typed member with address, scope, artifact bytes/hash, and sidecar
identity) instead of `RawMemberSnapshot`. This is the single behavior change approved under locked
decision 4. The existing `RawMemberSnapshot` fields remain authoritative inside `AppendTransaction`
and `WholeMemoryTransaction`; B0 does not remove them. It adds the typed carrier at the result
boundary only. The justification is in Section 12.

`RecoveryMember` is a new internal Core record (distinct from the existing `RecoveryMemberSnapshot` at
`RecoveryEnvelope.cs:58–62` and `RawMemberSnapshot` at `AppendTransaction.cs:7`). Its shape is:

```csharp
internal sealed record RecoveryMember(
    MemoryAddress Address,
    string Scope,
    byte[] ArtifactBytes,
    string ArtifactSha256,
    string? SidecarPath,
    string? SidecarSha256);
```

It is carried only by the new `B0Result` facade and its thin wrappers; it is never written to disk, so
it is not a durable format or recovery outcome.

### 6.3 Unified exception taxonomy

B0 exposes one Core exception surface over the existing typed exceptions. Each existing exception keeps
its current type and message. `B0Result` carries a single `B0Exception?` that wraps whichever existing
exception occurred:

| Source exception | Location |
|---|---|
| `AppendStoreException` | `AppendContracts.cs:46` |
| `AppendOperationCanceledException` | `AppendContracts.cs:107` |
| `AppendPostCommitCleanupException` (internal) | `AppendContracts.cs:93` |
| `WholeMemoryTransactionException` (internal, `: IOException`) | `WholeMemoryTransaction.cs:145` |
| `AppendJournalException` (`Malformed`) | `AppendJournal.cs` via `WithState` (`AppendJournal.cs:242`) |

`B0Result` does not create new exception types. It surfaces the existing taxonomy through one accessor
so children read failure cause from a single field. Section 12 confirms this changes no exception
behavior.

## 7. Design — Section 3: centralized DurableFormats registry

### 7.1 Standardize the registry owner

B0 standardizes `DurableFormats` (`Formats/DurableFormats.cs:15`) as the single registry owner — one
`ImmutableDictionary<string, FormatContract>` `Registry` as the sole source of truth for every durable
format name, current version, `AbsentMeans`, and `OnOlder`/`OnNewer` disposition. `FormatContract`,
`FormatDisposition`, and `FormatVersion.Evaluate` each already have one definition in `Formats/`; B0
does not duplicate or relocate them — it routes every consumer through this one Registry. No consumer
declares a second copy or hard-codes a version disposition.

```csharp
// Single shared registry owner (one file, one type set).
internal static class DurableFormats            // Formats/DurableFormats.cs:15
{
    public static readonly ImmutableDictionary<string, FormatContract> Registry;
}

internal readonly record struct FormatContract(string Name, int Current,
    FormatAbsentMeans AbsentMeans, FormatDisposition OnOlder, FormatDisposition OnNewer);

internal enum FormatDisposition { Load, LoadReadOnly, Rebuild, Refuse }

// FormatVersion.Evaluate (Formats/FormatVersion.cs:25): AbsentMeans == null => Refuse.
```

### 7.2 Authoritative current values

The registry holds exactly these entries (Section 4.7 is the source of truth):

`ArtifactSidecar=1`, `EntityFacts=3`, `Nmp2Body=2`, `BundleEntry=(ArtifactSidecar.Current)`,
`BundleManifest=2`, `LegacyIndex=3`, `SkillMeta=1`, `AgentMeta=1`, `RestoreJournal=1`,
`AppendJournal=1`, `RecoveryEnvelope=1`, `MultiMemoryTransaction=1`.

### 7.3 Refuse disposition for unknown versions

All recovery, journal, envelope, and coordinator contracts use `Refuse` for unknown versions. The
centralized registry enforces this in one place. `FormatVersion.Evaluate` returns `Refuse` when
`AbsentMeans` is `null`. Consumers that previously hard-coded `Refuse` now import the registry value;
their behavior is unchanged; consumers already route through
`FormatVersion.Evaluate(DurableFormats.X, ...)`, and B0 keeps that single call path. Section 12 records
that this standardization is behavior-preserving.

## 8. Design — Section 4: one B0FaultSeam contract

### 8.1 Unified seam

B0 unifies the twelve existing test-only injection seams (Section 4.9) behind one contract,
`B0FaultSeam`. It defines a single `FaultPhase` enum that covers `DurableWritePhase` (Section 4.8)
plus every coordinator transition and fault-point phase in Section 4.9.

```csharp
internal static class B0FaultSeam
{
    // Single phase space: DurableWritePhase + all Append/Whole/Restore/MultiMemory/FileEntity phases.
    public enum FaultPhase { /* ... Section 8.2 ... */ }

    // The one injection function shape.
    public static Func<FaultPhase, string, Exception?>? Inject;

    public static void FailOnceAt(FaultPhase phase, string tag);
    public static void FailPersistentlyAt(FaultPhase phase, string tag);
    public static void ClearFaults();
    public static void ThrowIfInjected(FaultPhase phase, string tag);
}
```

`FailOnceAt` fails the first injection at a `(phase, tag)` pair and then clears it. `FailPersistentlyAt`
fails on every injection at that pair. `ClearFaults` resets all injected seams. `ThrowIfInjected`
throws the injected exception at a consuming call site. `Inject` is the shared function shape; every
existing seam maps onto it.

### 8.2 FaultPhase coverage

`FaultPhase` enumerates every phase the existing seams expose:

- **DurableFile** (`DurableWritePhase`): `BeforeWrite`, `BeforeFlush`, `BeforeMove`.
- **Append** (`AppendFaultPoint`, 24 members): `RawArtifactRead`, `RawSidecarRead`, `JournalWrite`,
  `JournalFlush`, `JournalParse`, `JournalDelete`, `ArchiveArtifactWrite`, `ArchiveArtifactFlush`,
  `ArchiveSidecarWrite`, `ArchiveSidecarFlush`, `LiveArtifactWrite`, `LiveArtifactFlush`,
  `LiveSidecarWrite`, `LiveSidecarFlush`, `VerificationRead`, `RollbackWrite`, `RollbackDelete`,
  `ArchiveCleanup`, `PruningDelete`, `BeforeEvent`, `AfterEvent`.
- **Whole-memory** (`WholeMemoryFaultPoint`): `RawArtifactRead`, `RawSidecarRead`, `EvidenceWrite`,
  `ArchiveArtifactWrite`, `ArchiveSidecarWrite`, `LiveArtifactWrite`, `LiveSidecarWrite`,
  `VerificationRead`, `RollbackWrite`, `EvidenceCleanup`, `ArchiveCleanup`, `CleanupStateWrite`.
- **Restore** (`RestoreJournalTransition`): `BeforeRecoveryBarrierRead`, `BeforePreparedWrite`,
  `AfterPreparedWrite`, `BeforeReplayBody`, `BeforeReplaySidecar`, `AfterReplayDestinations`,
  `BeforeCompletedWrite`, `AfterCompletedWrite`, `BeforeCleanupDelete`.
- **Multi-memory** (`MultiMemoryTransactionTransition`, 4 members): `PreparedPublished`,
  `CommittedPublished`, `CompletionProofPublished`, `RecordDeleted`.
- **File-entity** (`FileEntityFaultPoint`): `RawBodyRead`, `RawMetaRead`, `ArchiveBodyWrite`,
  `ArchiveMetaWrite`, `BodyWrite`, `MetaWrite`, `VerificationRead`, `AfterBodyDelete`, `RollbackWrite`,
  `EvidenceCleanup`.

### 8.3 Mapping existing seams onto the unified contract

Each existing seam becomes a thin adapter onto `B0FaultSeam.Inject`. `DurableFile.FailureInjector`
(`DurableFile.cs:13–20`) maps `DurableWritePhase` → `FaultPhase`. `FileMemoryStore.AppendFaultInjector`
(`FileMemoryStore.cs:45–52`) and `FileMemoryStore.WholeMemoryFaultInjector` (`FileMemoryStore.cs:46–57`)
map `AppendFaultPoint`/`WholeMemoryFaultPoint` → `FaultPhase`. `FileEntityTransactionCoordinator._faultPoints`
(`FileEntityTransactionCoordinator.cs:11–22`) maps `FileEntityFaultPoint` → `FaultPhase`.
`RestoreJournalFailureInjector` (`FileMemoryStore.cs:124`) maps `RestoreJournalTransition` →
`FaultPhase`. `MultiMemoryTransactionCoordinator.FailureInjector`
(`MultiMemoryTransactionCoordinator.cs:14`) maps `MultiMemoryTransactionTransition` → `FaultPhase`.
`RestoreJournal.DecodeObserver` (`RestoreJournal.cs:118`) remains an `AsyncLocal<Action?>` observation
hook; it does not inject exceptions, so it is not an injection phase.

All seams stay `AsyncLocal`-scoped and test-only. Production `Inject` remains `null`. B0 does not add
a production fault surface. Section 12 records that the unification is behavior-preserving.

## 9. Design — Section 5: compatibility harness and golden fixtures

### 9.1 Harness contract

B0 ships a compatibility harness that loads current evidence through the unified foundation and asserts
it still parses, validates, and maps. The harness is test infrastructure in `tests/Scrinia.Tests/`. It
does not run in production. It consumes `B0FaultSeam` for deterministic fault injection, the
centralized `DurableFormats` registry for version checks, and `B0Result` for result mapping.

### 9.2 Golden fixtures

The harness ships golden fixtures for every current evidence kind. Each fixture is a byte-exact copy
of a representative current artifact so the harness proves the foundation still loads real evidence.

| Fixture class | Proves |
|---|---|
| Artifact fixtures | Current `ArtifactSidecar`/`EntityFacts`/`Nmp2Body` artifacts decode and re-encode identically. |
| Sidecar fixtures | Current ordinary and skill/agent sidecars load through `ReadSidecarMeta`/`WriteSidecarMeta`. |
| Archive fixtures | Current `versions/` archive pairs load with `BundleManifest`/`LegacyIndex` versions. |
| Ephemeral fixtures | `~name` and `/temp/name` remain in-process and outside persistent guarantees. |
| Public-facade fixtures | MCP `McpResponse` and CLI `CliMcpOutput` still map a `B0Result` without contract change. |
| Journal/envelope fixtures | Append, restore, file-entity, and multi-memory recovery envelopes parse through the sole parser boundary (Section 4.6). |
| Fault-injection fixtures | `B0FaultSeam.FailOnceAt` reproduces each existing seam's failure at the mapped `FaultPhase`. |

### 9.3 Compatibility matrix

The harness runs a compatibility matrix that cross-checks every current durable format against the
centralized registry and every current result vocabulary against `B0Result`. Every current version
must load. Any version the registry marks `Refuse` must fail closed, matching the existing
recovery/journal/envelope/coordinator behavior. The matrix proves that Section 7's standardization and
Section 8's unification leave current evidence intact.

### 9.4 Verify current evidence loads

Acceptance requires the harness to load current append, whole-memory, multi-memory, file-entity,
restore, and migration evidence and return `Verified`/`Committed`/`Created` outcomes through `B0Result`
without behavior change. The bare-complete migration case (Section 4.11) must still reject
`complete` without `prepared`. Section 12 confirms the harness loads current evidence; it does not
produce new evidence.

## 10. Public result mapping

B0 maps one Core result vocabulary across the three layers. The mapping is thin and behavior-preserving.

```text
Core:  B0Result  ──thin wrapper──▶  MCP: McpResponse (Status/IsError/IsWarning/Action/OperationId/Scope/...)
                                    MCP: McpResponse.ErrorCodes (unchanged)
CLI:   CliMcpOutput (passthrough raw YAML)  — JSON roots unchanged except B0Result registration
```

Rules:

- **ErrorCodes stay stable.** `McpResponse.ErrorCodes` (McpResponse.cs:10) is unchanged. `B0Result`
  does not add an error code; it re-expresses existing codes through the same vocabulary.
- **JSON roots unchanged except registration.** `CliJsonContext` roots (CliJsonContext.cs:41) keep
  `CliMcpOutput` as the append/whole-memory/multi-memory passthrough. `ScriniaMcpJsonContext`
  registers `B0Result` alongside the existing `AppendStoreResult` + sub-types
  (`MemoryTools.Skills.cs:49–54`). `B0Result` is a new registered root; no existing root changes.
- **No second facade.** `B0Result` is the single Core result contract. Neither MCP nor CLI derives or
  sequences writes independently. The roadmap §6 rule (no duplicate facade transaction or derivation)
  holds.
- **`MultiMemoryAggregateResult` and `RecoveryBlock`/`RecoveryBlockReason` still cross Core → MCP → CLI.**
  B0 exposes them through `B0Result` without changing their wire form.

## 11. Compatibility constraints

These restate roadmap §7 for B0. B0 preserves every constraint below.

- **NMP/2:** Preserve `nmp/2`, current encoding/decoding, headers, chunk semantics, and artifact
  readability. Format proposals belong only to B6.
- **Sidecars and migrations:** Preserve current sidecar fields, legacy `name`/`uri` handling,
  forward-fill and `fields`→`attributes` migration, skill/agent sidecar lookup, and malformed sidecar
  safety. Use `ReadSidecarMeta`/`WriteSidecarMeta` and source-generated contexts.
- **Archive layout:** Preserve existing version directories, names, collision rules, artifact/sidecar
  pairing, and retention semantics. Journals must remain distinguishable from restore evidence.
- **Ephemeral memories:** Keep `~name` and `/temp/name` in-process and outside persistent
  archive/journal guarantees. The persistent-only CLI continues to reject ephemeral addresses; the
  long-lived local stdio MCP process remains the ephemeral surface.
- **CLI/MCP:** Facades translate one Core result contract (`B0Result`). Public error codes, operation
  IDs, warnings, recovery details, and content privacy remain stable.
- **Source-generated JSON:** Register every new serialized type in the appropriate JSON context.
  Reflection-based `JsonSerializer` use is incompatible with trimming and is prohibited.
- **Deployment surface:** `scri serve` over local stdio is the only server surface. No HTTP/REST,
  remote CLI, API-key, web, or Aspire guidance.
- **Shared privacy and no Git:** Shared-base integration is filesystem-only, fixed to `project-git`,
  reports `UNVERIFIED` index state, never starts or inspects Git, and does not disclose disabled
  registry entries except through explicit `shared base list --all`.

## 12. Behavior lock and justified change

### 12.1 The behavior lock

Section 6 (Section 2 of the design) is the **only** justified behavior change. Everything else is
behavior-preserving:

- Section 5 (Section 1) adds a read-only mapper. It changes no transition.
- Section 7 (Section 3) standardizes the existing centralized durable-format registry. It changes no version behavior; `Refuse`
  for unknown versions is preserved.
- Section 8 (Section 4) unifies fault seams onto one function shape. All seams stay `AsyncLocal`-scoped
  and test-only; production `Inject` stays `null`.
- Section 9 (Section 5) adds a compatibility harness and golden fixtures. It loads current evidence;
  it produces no durable change.
- Section 10 maps one Core result vocabulary. It changes no public error code, operation ID, or JSON
  root except the additive `B0Result` registration.

### 12.2 The one justified change: typed member carrier

The only behavior change is the typed member carrier in Section 6.2. `AppendTransaction` and
`WholeMemoryTransaction` continue to capture `RawMemberSnapshot` at their frozen-prepublication
boundaries (SHA-256 enforced at capture, `AppendTransaction.cs:102–122`; `WholeMemoryTransaction.cs:6–41`).
`B0Result` and its thin wrappers carry `RecoveryMember` at the result boundary instead of
`RawMemberSnapshot`. This gives children one typed member contract to read without weakening append or
whole-memory capture. It is the change approved under locked decision 4.

Concretely, this changes no append behavior the roadmap §4 protects: `AppendStoreResult`,
`AppendTransaction`, and `AppendJournal` keep their exact shapes and semantics; `OperationId`, outcome,
warnings, and recovery detail are all preserved. `RecoveryMember` appears only on the new `B0Result`
facade, not on any preserved result, so append's capture/commit/rollback/recovery semantics are
identical. The change is representation at the result boundary, not behavior.

### 12.3 Any other change needs separate justification

No other change is authorized by this spec. If implementation reveals a need beyond the typed member
carrier, that change requires a new user-approved decision recorded in Section 14 before it is made.
Section 12 exists to enforce the roadmap §10 split rule and the behavior lock in locked decision 2.

## 13. Test lanes and acceptance

Acceptance restates roadmap §8 gates. B0 passes only when all of the following hold.

1. **An approved child specification names goals, non-goals, invariants, ownership, compatibility
   impact, public result mapping, migration/recovery behavior, tests, and explicit decisions.** This
   document is that specification.
2. **A scoped implementation plan names exact files, call sites, test lanes, and exclusions.**
3. **Focused foundation tests pass.** They cover the lifecycle mapper, the `B0Result` wrappers, the
   centralized registry (including `Refuse` on unknown versions), the unified fault seam (each mapped
   phase, `FailOnceAt`, `FailPersistentlyAt`, `ClearFaults`, `ThrowIfInjected`), the typed member
   carrier, and the bare-complete migration rejection (Section 4.11).
4. **Compatibility/harness tests pass.** Golden fixtures for artifacts, sidecars, archives, ephemeral,
   public facades, and journals load through the unified foundation. The compatibility matrix proves
   every current durable format and result vocabulary maps without behavior change.
5. **`dotnet build Scrinia.sln` passes with zero errors and zero warnings attributable to B0.**
6. **`dotnet test Scrinia.sln` exits 0 with zero failures.** No test-host hang, crash, abort, or
   timeout is accepted. The final run records pass, skipped, failed, total, exit status, and
   no-hang evidence.
7. **Package, trimming/source-generation, CLI/MCP contract, and historical-doc checks pass.** `B0Result`
   is registered in the correct JSON context; no reflection JSON is introduced.
8. **A scoped diff check passes.** The B0 commit contains no unrelated source, test, evidence,
    generated, or historical-document change. B0's evidence lives under the roadmap §9 dated evidence
    directory and status ledger; the compatibility harness transcript is authoritative evidence.
9. **An independent review records APPROVE (or a resolved REQUEST_CHANGES cycle) against the exact B0
   diff and verification transcript.**

## 14. Decision record

The roadmap §4 requires five explicit decisions. The user approved all five (locked decisions in
Section 3).

| # | Decision | Approved answer |
|---|---|---|
| 1 | Shared vocabulary + fixtures/harness, both required | **Approved.** B0 delivers the vocabulary (Sections 5, 6, 7) and the harness with golden fixtures (Section 9). |
| 2 | Behavior lock | **Approved.** Only the typed member carrier (Section 6.2) changes behavior. Section 12 enforces it. |
| 3 | One lifecycle enum | **Approved.** `RecoveryLifecycleState` (RecoveryContracts.cs:15–24) is canonical. Section 5 adds a read-only mapper. |
| 4 | Move to typed member carrier | **Approved.** The single justified change. `B0Result` carries `RecoveryMember` at the result boundary; `RawMemberSnapshot` remains authoritative in append and whole-memory capture. |
| 5 | Centralize DurableFormats registry | **Approved.** One shared registry in `Formats/`. Section 7 standardizes the existing centralized `DurableFormats` registry (Formats/DurableFormats.cs:15) as the single registry owner and routes every consumer through it; `FormatContract`, `FormatDisposition`, and `FormatVersion.Evaluate` each already have one definition in `Formats/` and are not duplicated or relocated. |

## 15. Self-review

This is a DRAFT. The following placeholders must be filled before B0 is presented for review.

- **Contradictions:** none identified in draft. Confirm Section 6.2 (typed carrier) does not
  contradict Section 4.1 (raw-snapshot capture). The capture boundary stays raw; the result boundary
  becomes typed. State this explicitly in the final review.
- **Ambiguity:** `B0Result.CorrelationId` collapses `OperationId` and `TransactionId`. Confirm the
  multi-memory `transactionId` and the append/whole-memory `operationId` are truly interchangeable for
  B1–B6 correlation before finalizing the accessor.
- **Scope:** confirm Section 9's golden fixtures cover every durable format the registry currently
  declares (Section 7.2) and every recovery evidence kind in `RecoveryEvidenceKind`
  (RecoveryVerification.cs:9–15): `AppendJournal`, `RestoreJournal`, `FileEntityJournal`, and
  `MultiMemoryTransactionRecord`.
- **Placeholders:** acceptance gates in Section 13 and the decision record in Section 14 are present
  but not yet evidenced. No code is written; no transcript is attached.

## 16. Citations

- Phase B umbrella roadmap: `docs/superpowers/specs/2026-09-10-phase-b-roadmap-design.md` §3 (deferred
  areas), §4 (B0 boundary and deliverable), §5 (dependencies), §6 (ownership map), §7 (compatibility
  constraints), §8 (acceptance gates), §9 (evidence protocol), §10 (split criteria), §11 (blocker
  checklist), §12 (self-review).
- `CONTEXT.md` — B1 scope-contamination audit paths (Section 4.12).
- `docs/superpowers/reports/2026-09-12-multi-memory-task-4.md` through `-6` and the B2 task reports
  (referenced as `task-9-report.md` in the parent brief) — B2 planning artifacts (Section 4.12).
- `docs/superpowers/specs/2026-09-15-b5-retention-maintenance-design.md` — sole parser boundary
  (Section 4.6) is B5-owned and referenced here only as a do-not-change anchor.
- `docs/superpowers/ledgers/2026-09-17-b2-gap-closure-acceptance.md` (the B2 ledger) line 26 and
  `CONTEXT.md` lines 52–61 — B2 parked bare-complete finding (Section 4.13).
- `tests/Scrinia.Tests/LegacyMigrationServiceTests.cs` lines 161–184 — closest bare-complete test
  (Section 4.11).

(End of draft. No source or test changes are authorized by this file.)
