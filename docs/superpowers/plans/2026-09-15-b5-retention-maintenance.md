---
status: in-progress
phase: 1
updated: 2026-09-15
---

# B5 Retention, Pruning, and Recovery Maintenance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add one bounded, Core-owned maintenance path that prunes eligible archive pairs and removes only verified terminal recovery evidence through the existing scope lock, while sharing identical typed results between the local CLI command and the background host adapter.

**Architecture:** `FileMemoryMaintenanceService` owns policy application, deterministic candidate planning, protection/reference classification, lock-scoped revalidation, deletion, and typed aggregate results. `FileMemoryStore` supplies the existing `WithExclusiveScopeAdmissionAsync` boundary and performs scope-local cache/index/facts invalidation after successful archive deletion; recovery adapters expose a typed terminal-cleanup decision without changing their wire formats. The CLI and the bounded background scheduler are transport callers of this service, not deletion or classification engines.

**Tech Stack:** C# on `net10.0`, nullable reference types, existing `FileLock`/`WithExclusiveScopeAdmissionAsync`, `DurableFile` conventions, `FileRetry`, source-generated `System.Text.Json`, xUnit, FluentAssertions, `TestHelpers.StoreScope`, ConsoleAppFramework, and `BackgroundWork`.

**Spec:** `docs/superpowers/specs/2026-09-15-b5-retention-maintenance-design.md`

## Global Constraints

- Retention configuration remains the flat `.scrinia/config.json` dictionary; no nested policy document or new metadata file is added.
- `Scrinia:Retention:MaxArchivedVersions` defaults to `10` and is a decimal integer in `1..100000`.
- `Scrinia:Retention:MinimumAgeDays` defaults to `30` and is a decimal integer in `0..36500`.
- `Scrinia:Maintenance:JournalCleanupBatchSize` defaults to `50` and is a decimal integer in `1..10000`.
- `Scrinia:Maintenance:TimeBudgetSeconds` defaults to `5` and is a decimal integer in `1..3600`.
- `Scrinia:Maintenance:BackgroundEnabled` defaults to lowercase `true` and accepts only lowercase `true` or `false`.
- These are the complete five B5 policy keys. There is no unresolved-evidence policy key: the approved rule is fixed, not configurable—unresolved, malformed, ambiguous, unsupported, inaccessible, path-unsafe, incomplete, or unverifiable evidence is retained indefinitely.
- Parsing uses `NumberStyles.None` and `CultureInfo.InvariantCulture`; present empty, signed, whitespace-padded, non-decimal, overflowed, negative, zero-when-positive, or otherwise out-of-range values invalidate the complete B5 policy and cause zero deletion. Boolean parsing accepts only the exact lowercase strings `true` and `false`.
- The effective policy is read once per batch; a configuration change affects the next batch only.
- Unresolved, malformed, ambiguous, unsupported, inaccessible, path-unsafe, incomplete, or unverifiable evidence is retained indefinitely.
- Current, active, referenced, explicitly protected, unresolved, and unsafe/unverifiable candidates are never deleted.
- Maintenance processes one resolved persistent scope at a time; workspace selection is a sequence of independent scope batches, never a workspace transaction.
- All candidate decisions are made and revalidated under the existing scope-local exclusive file lock and in-process index write lock.
- The implementation must use `FileMemoryStore.WithExclusiveScopeAdmissionAsync`; it must not acquire a raw lock and call another operation that reacquires it.
- The existing B2/B3/B4 typed recovery contracts remain the only source of recovery state; B5 does not parse raw JSON or exception messages.
- Archive deletion preserves artifact/sidecar pairing, never deletes current state, and never repairs an incomplete pair.
- Verified terminal recovery evidence may be deleted only through a typed cleanup decision; B5 never manufactures terminal state.
- A deletion failure retains the failed candidate, reports a sanitized warning, and does not undo successful deletions in the same batch.
- Batch size, time budget, cancellation, and scope boundaries stop work without creating one task per candidate or an unbounded queue.
- Ephemeral memories are outside maintenance.
- No remote API, HTTP/REST service, remote CLI, daemon, fleet reporting, MCP maintenance tool, workspace-wide lock, archive index, protection file, archive manifest, migration, or new format is added.
- NMP/2 artifacts, ordinary sidecars, archive naming/layout, B2 journal locations, the B3 coordinator record, B4 participant state, plugin lifecycle, and observer semantics remain unchanged.
- New durable or CLI-serialized types use source-generated JSON metadata; no reflection serializer overload is introduced.
- Existing memory, append, restore, file-entity, bundle, compact, import, migration, MCP, and CLI semantics remain unchanged except for the explicitly added local maintenance command.
- The plan is implementation-only; its execution must not stage or commit the plan/spec/roadmap/ledger files.

---

## Context And Decisions

| Decision | Rationale | Source |
|---|---|---|
| Put policy parsing in `Scrinia.Core.Configuration` and keep it flat | The workspace config is a case-insensitive dictionary with durable atomic replacement; B5 adds scalar keys only | `AGENTS.md:92-113`; B5 spec §3, §10 |
| Use one service behind CLI and background callers | The approved design explicitly forbids separate deletion rules | B5 spec §1, §2, §6 |
| Reuse `WithExclusiveScopeAdmissionAsync` | It already combines cross-process scope locking, in-process write locking, and recovery admission | `FileMemoryStore.cs:801-818`; B5 spec §2, §7 |
| Keep recovery cleanup typed and adapter-owned | B2/B3/B4 evidence must not be reinterpreted from filenames, JSON, or exception text | `RecoveryContracts.cs:119-136`; `RecoveryDiscovery.cs:145-168`; B5 spec §4-§6 |
| Treat incomplete pairs and uncertain deletion as protected | A partial delete must converge conservatively after restart | B5 spec §4, §5, §7 |
| Use bounded work admission rather than a per-candidate task model | `BackgroundWork` owns detached work and drain behavior, but B5 itself must remain one bounded batch | `BackgroundWork.cs:82-126`; `Program.cs:25-33` (current drain call); B5 spec §6 |
| Keep maintenance fault ownership in the scheduler wrapper, but keep global drain ownership in `Program.cs` | `BackgroundWork.Fire` is a void, fire-and-forget boundary; the scheduler can report its own invocation faults through a callback/result channel without changing that API, while the top-level runner can type and sanitize the independent global drain failure | `BackgroundWork.cs:81-126,129-155`; `Program.cs:23-33` |
| Never prune from a partial subject page | A subject is deletable only after a complete bounded snapshot of that subject has been discovered; otherwise its candidates remain protected and the cursor advances without deletion | B5 spec §5, §7 |
| Continuation tokens are authenticated, expiring request cursors | Repeated bounded invocations must converge without accepting a cursor from another workspace, scope set, or policy run | B5 spec §5, §8, §9 |

## Phase 1: Contracts And Policy [IN PROGRESS]

### Task 1: Define the exact maintenance contract and source-generated JSON vocabulary ← CURRENT

**Files:**
- Create: `src/Scrinia.Core/MaintenanceContracts.cs`
- Create: `src/Scrinia.Core/MaintenanceJsonContext.cs`
- Create: `tests/Scrinia.Tests/MaintenanceContractTests.cs`

**Interfaces:**
- Consumes: `MemoryAddress`, `RecoveryOperation`, `RecoveryLifecycleState`, `RecoveryBlockReason`, `FileMemoryStore` scope labels, and existing configuration exception/result conventions.
- Produces these internal immutable types, which are the only cross-task maintenance contract:

```csharp
internal enum MaintenanceCandidateKind
{
    ArchivePair,
    CompletedRecoveryEvidence
}

internal enum MaintenanceProtectionReason
{
    Current,
    Active,
    Referenced,
    ExplicitlyProtected,
    UnresolvedRecovery,
    IncompletePair,
    Malformed,
    UnsupportedVersion,
    Ambiguous,
    Unsafe,
    Inaccessible,
    Unverifiable,
    YoungerThanMinimumAge,
    WithinRetentionLimit,
    MissingExpectedMember,
    InvalidContinuation,
    DeletionFailed
}

internal sealed record MaintenanceRequest(
    IReadOnlyList<string>? Scopes,
    int? BatchSizeOverride,
    IReadOnlySet<string>? ExplicitlyProtectedRelativePaths,
    string? ContinuationToken);

// Exact request conventions used by every caller:
// Scopes is null only for the resolved workspace scope set; otherwise it is a non-null,
// ordinally unique IReadOnlyList<string> of 1..128 persistent scope names. The scheduler
// passes [MaintenanceScopes.DefaultLocalScope], never a bare string.
internal static class MaintenanceScopes
{
    internal const string DefaultLocalScope = "local";
    internal static IReadOnlySet<string> EmptyProtectionSet { get; } = FrozenSet<string>.Empty;
}

internal sealed record MaintenanceCandidate(
    string Scope,
    string Subject,
    MaintenanceCandidateKind Kind,
    string BodyPath,
    string? SidecarPath,
    DateTimeOffset? CandidateTimestamp,
    string? OperationId,
    MaintenanceProtectionReason? Protection,
    string RelativePath);

internal sealed record MaintenanceCandidateResult(
    MaintenanceCandidate Candidate,
    bool Deleted,
    MaintenanceProtectionReason? Reason,
    string? Warning);

internal sealed record MaintenanceScopeResult(
    string Scope,
    int Inspected,
    int DeletedArchives,
    int DeletedRecoveryEvidence,
    int Retained,
    bool LockContended,
    bool MoreWork,
    string? ContinuationToken,
    IReadOnlyList<MaintenanceCandidateResult> Candidates,
    IReadOnlyList<string> Warnings);

internal sealed record MaintenanceResult(
    int Inspected,
    int DeletedArchives,
    int DeletedRecoveryEvidence,
    bool Completed,
    bool MoreWork,
    bool RecoveryRetained,
    bool LockContended,
    bool PolicyInvalid,
    bool PartialFailure,
    bool HasWarnings,
    string? ContinuationToken,
    IReadOnlyList<MaintenanceScopeResult> Scopes,
    IReadOnlyList<string> Warnings,
    string? PolicyError);
```

The records copy collection inputs, reject null elements, require safe nonblank scope/subject/relative path values, and expose no content, absolute path, raw JSON, exception text, sidecar bytes, or secrets. There is no scalar outcome class: orthogonal flags may coexist. `Completed` means no continuation remains; `MoreWork` means unscanned work remains; `RecoveryRetained` means recovery evidence was retained; `LockContended` identifies a scope admission timeout; `PolicyInvalid` means no enumeration/deletion occurred; `PartialFailure` means an attempted deletion failed; and `HasWarnings` equals `Warnings.Count > 0`. JSON emits these booleans and `continuationToken`; CLI emits the same stable flag names and exits 0 only for completed, warning-free work. Add exact flag-combination and invariant tests.

`MaintenanceRequest` is an immutable sealed record class with the constructor signature shown above:
`Scopes` is nullable only when the caller requests the resolved workspace set; when present it is
copied to an ordinal `IReadOnlyList<string>`, contains 1..128 nonblank persistent scope names, and
has no duplicates. `BatchSizeOverride` is nullable or `1..10000`; `ExplicitlyProtectedRelativePaths`
is nullable only at the Core boundary and is normalized to `MaintenanceScopes.EmptyProtectionSet`,
otherwise copied to an ordinal read-only set with at most 4096 nonblank, relative, non-URI paths;
`ContinuationToken` is nullable or a nonblank token of at most 32 KiB with no whitespace/control
characters. Null elements and all invalid values throw `ArgumentException` before service execution.
The scheduler uses the exact non-null values `[MaintenanceScopes.DefaultLocalScope]` and
`MaintenanceScopes.EmptyProtectionSet`.

Also define the internal source-generated token payload used by Task 5:

```csharp
internal sealed record MaintenanceContinuationPayload(
    int Version,
    string WorkspaceBinding,
    string RequestedScopesBinding,
    string Scope,
    string Phase,
    string Subject,
    string LastRelativePath,
    IReadOnlyList<MaintenanceSnapshotCandidate> SnapshotCandidates,
    long SnapshotBytes,
    DateTimeOffset ExpiresAt);

internal sealed record MaintenanceSnapshotCandidate(
    string Subject, string RelativePath, DateTimeOffset CandidateTimestamp,
    string PairIdentity, bool Eligible, MaintenanceProtectionReason? Protection);
```

### Task 2: Add policy parsing with exact defaults and lexical/range validation

**Files:**
- Create: `src/Scrinia.Core/Configuration/MaintenancePolicy.cs`
- Create: `tests/Scrinia.Tests/MaintenancePolicyTests.cs`

**Exact API:**

```csharp
public sealed record MaintenancePolicy(
    int MaxArchivedVersions,
    int MinimumAgeDays,
    int JournalCleanupBatchSize,
    int TimeBudgetSeconds,
    bool BackgroundEnabled)
{
    public const int DefaultMaxArchivedVersions = 10;
    public const int DefaultMinimumAgeDays = 30;
    public const int DefaultJournalCleanupBatchSize = 50;
    public const int DefaultTimeBudgetSeconds = 5;
    public const bool DefaultBackgroundEnabled = true;

    public static MaintenancePolicyLoadResult Load(IWorkspaceConfig config);
}

public sealed record MaintenancePolicyLoadResult(
    MaintenancePolicy? Policy, IReadOnlyList<MaintenancePolicyError> Errors)
{
    public bool IsValid => Policy is not null && Errors.Count == 0;
}

public sealed record MaintenancePolicyError(string Key, string Code);

internal static class MaintenancePolicyLoader
{
    internal static MaintenancePolicyLoadResult Load(IWorkspaceConfig config);
}
```

`Load` reads exactly the five B5 keys through `IWorkspaceConfig.GetValue`; absent keys use defaults. The exact bounds are `MaxArchivedVersions 1..100000`, `MinimumAgeDays 0..36500`, `JournalCleanupBatchSize 1..10000`, and `TimeBudgetSeconds 1..3600`. Parse integers with `NumberStyles.None` and `CultureInfo.InvariantCulture`; reject `+1`, whitespace, decimal text, overflow, negative values, zero for positive fields, and values above those bounds. Parse booleans by ordinal equality to lowercase `true`/`false`. Aggregate invalid fields into `MaintenancePolicyLoadResult` before any lock or enumeration. `MaintenancePolicyLoader.Load(IWorkspaceConfig)` is the sole loader used by the Core service, CLI, and scheduler; all callers convert invalid results to the same typed `MaintenanceResult` with `PolicyInvalid=true` and zero deletion, never divergent uncaught parser errors. `MaintenancePolicy.Load` is a forwarding convenience only and is not called by transports. Tests cover defaults, both numeric bounds, overflow/zero/negative behavior, malformed values, unknown-key tolerance, and one snapshot per invocation.

Use this authoritative source-generation registration list. `src/Scrinia.Core/MaintenanceJsonContext.cs` registers every directly serialized Core root and every collection element: `MaintenanceContinuationPayload`, `MaintenanceSnapshotCandidate` (including the nested `SnapshotCandidates` element type), `MaintenanceResult`, `MaintenanceScopeResult`, `MaintenanceCandidateResult`, `MaintenanceCandidate`, `MaintenanceCandidateKind`, `MaintenanceProtectionReason`, `MaintenancePolicy`, `MaintenancePolicyLoadResult`, and `MaintenancePolicyError`. `MaintenanceJsonContext` is the only serializer used for continuation payloads and Core result JSON. `src/Scrinia/Commands/CliJsonContext.cs` registers the same result graph explicitly—`MaintenanceResult`, `MaintenanceScopeResult`, `MaintenanceCandidateResult`, `MaintenanceCandidate`, `MaintenanceCandidateKind`, and `MaintenanceProtectionReason`—even where the generator could infer nested members. `MaintenanceSnapshotCandidate` and policy-load types are not CLI output types. No other B5 type is serialized. Tests assert this exact list and fail if either path falls back to reflection.

### Task 3: Run the contract and policy red tests, implement the minimum code, and commit

Run:

```bash
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~MaintenanceContractTests|FullyQualifiedName~MaintenancePolicyTests" --no-restore
```

Expected before implementation: FAIL because B5 contracts and policy parser are absent. Expected after implementation: PASS with no source-generation warnings.

Commit only the Phase 1 source/test paths:

```bash
git add src/Scrinia.Core/MaintenanceContracts.cs src/Scrinia.Core/MaintenanceJsonContext.cs src/Scrinia.Core/Configuration/MaintenancePolicy.cs tests/Scrinia.Tests/MaintenanceContractTests.cs tests/Scrinia.Tests/MaintenancePolicyTests.cs
git commit -m "feat: define b5 maintenance policy and results"
```

## Phase 2: Typed Candidates And Recovery Protection [PENDING]

### Task 4: Add typed B2, B3, and B4 maintenance projections and adapters

**Files:**
- Modify: `src/Scrinia.Core/RecoveryContracts.cs`
- Modify: `src/Scrinia.Core/RecoveryDiscovery.cs`
- Modify: `src/Scrinia.Core/AppendJournal.cs`
- Modify: `src/Scrinia.Core/RestoreJournal.cs`
- Modify: `src/Scrinia.Core/RecoveryEnvelope.cs` (adapter-facing recovery projection ownership; no wire-shape change)
- Modify: `src/Scrinia.Core/FileEntityTransactionCoordinator.cs`
- Modify: `src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs` (projection delegation only)
- Modify: `src/Scrinia.Core/MultiMemoryTransactionRecord.cs` (no wire-shape change)
- Modify: `src/Scrinia.Core/TransactionalParticipantSerialization.cs` (adapter projection only)
- Create: `tests/Scrinia.Tests/MaintenanceRecoveryCandidateTests.cs`

Add Core-owned, non-serialized maintenance projections. Cleanup receives projections, never a `RecoveryEnvelopeDocument`, and never parses, rewrites, or authorizes deletion from coordinator records. B2 owns `B2RecoveryMaintenanceProjection` and `IB2RecoveryMaintenanceAdapter`; B3 owns `B3CoordinatorMaintenanceProjection` and `IB3CoordinatorMaintenanceAdapter`; B4 owns `B4ParticipantMaintenanceProjection`, `B4RejectedParticipantMaintenanceProjection`, `B4QuarantineMaintenanceProjection`, and `IB4ParticipantMaintenanceAdapter`.

```csharp
internal sealed record B2RecoveryMaintenanceProjection(
    RecoveryOperation Operation,
    string Scope,
    string EvidencePath,
    string CanonicalSubject,
    string OperationId,
    RecoveryLifecycleState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsTerminalAndVerified,
    bool IsReferenced,
    bool IsQuarantined,
    bool IsAmbiguous,
    bool IsPathSafe);

internal interface IB2RecoveryMaintenanceAdapter
{
    IReadOnlyList<B2RecoveryMaintenanceProjection> Project(
        RecoveryDiscoveryResult discovery, string expectedScope);

    RecoveryCleanupDecision EvaluateCleanup(
        B2RecoveryMaintenanceProjection view,
        IReadOnlySet<string> referencedRelativePaths);
}

internal sealed record B3CoordinatorMaintenanceProjection(
    string Scope, string TransactionId, MultiMemoryTransactionState State,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<MultiMemoryParticipantManifest> Participants,
    IReadOnlyList<MultiMemoryParticipantCompletion> CompletedParticipants,
    IReadOnlyList<B4ParticipantMaintenanceProjection> TransactionalParticipants,
    IReadOnlyList<B4RejectedParticipantMaintenanceProjection> RejectedParticipants,
    bool IsTerminalAndVerified, bool IsPathSafe);

internal interface IB3CoordinatorMaintenanceAdapter
{
    B3CoordinatorMaintenanceProjection Project(
        MultiMemoryTransactionRecord record, string expectedScope);
    RecoveryCleanupDecision EvaluateCleanup(
        B3CoordinatorMaintenanceProjection projection,
        IReadOnlySet<string> referencedRelativePaths);
}

internal sealed record B4ParticipantMaintenanceProjection(
    string TransactionId, string OperationId, string Scope, string Address,
    string ParticipantIdentity, string OrderingKey, string Kind,
    TransactionalParticipantLifecycleState LifecycleState,
    TransactionalQuarantineState QuarantineState, string? EvidenceReference,
    string? EvidenceKind, string? EvidenceVersion, bool StateMayHaveChanged,
    bool Ambiguous, bool IsTerminalAndVerified, bool IsPathSafe);

internal sealed record B4RejectedParticipantMaintenanceProjection(
    string TransactionId, string OperationId, string Scope, string Address,
    string ParticipantIdentity, string OrderingKey, string Kind,
    TransactionalFailureCode RejectionCode, bool IsPathSafe);

internal sealed record B4QuarantineMaintenanceProjection(
    string TransactionId, string OperationId, string Scope, string Address,
    TransactionalQuarantineState State, string? Reason, bool IsPathSafe);

internal interface IB4ParticipantMaintenanceAdapter
{
    B4ParticipantMaintenanceProjection Project(B4ParticipantRecoveryState state);
    B4RejectedParticipantMaintenanceProjection ProjectRejected(
        B4ParticipantRequestMetadata metadata);
    B4QuarantineMaintenanceProjection ProjectQuarantine(B4ParticipantRecoveryState state);
    RecoveryCleanupDecision EvaluateCleanup(
        B4ParticipantMaintenanceProjection projection,
        IReadOnlySet<string> referencedRelativePaths);
}

internal sealed record RecoveryCleanupDecision(
    bool Eligible,
    bool Protected,
    MaintenanceProtectionReason? Protection,
    string SafeRelativePath,
    string? Warning);
```

`MultiMemoryTransactionCoordinator` remains the recovery owner and `MultiMemoryTransactionRecord` remains the persisted B3 record owner; the coordinator passes the record only to `IB3CoordinatorMaintenanceAdapter.Project`. `RecoveryEnvelope.cs` owns the exact adapter boundary for append, restore, and file-entity recovery projections: it may expose `RecoveryEnvelopeDocument` to the B2 adapter and return non-serialized `B2RecoveryMaintenanceProjection` data, but no maintenance caller may parse or serialize that document. `B4ParticipantRecoveryState` and `B4ParticipantRequestMetadata` remain B4 persistence models; rejected metadata is projected and never treated as terminal proof. B4 lifecycle, quarantine, and evidence fields are all carried into the projection. B2 envelopes remain private to `IB2RecoveryMaintenanceAdapter`. Terminal proof requires committed/rolled-back completion, verified live bytes/index consequences, required sidecar pairing, and no references. Prepared, applying, recovery-required, malformed, unsupported, ambiguous, quarantined, inaccessible, or cleanup-pending-without-proof remains protected indefinitely. `RecoveryDiscoveryCoordinator.DiscoverForMaintenance(string scope, string storeDirectory, MaintenanceDiscoveryCursor cursor, int candidateBudget, TimeSpan deadline, CancellationToken cancellationToken)` returns a bounded page and cursor without rewriting bytes. Tests cover all B2 locations, B3 coordinator records, B4 admitted/rejected/quarantined states, and no raw JSON/exception-message dependency.

### Task 5: Add bounded two-pass per-subject snapshots and reference/protection planning

**Files:**
- Create: `src/Scrinia.Core/MaintenancePlanner.cs`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` only for narrow internal read/invalidation seams
- Create: `tests/Scrinia.Tests/MaintenancePlannerTests.cs`

Add:

```csharp
internal sealed class MaintenancePlanner
{
    internal MaintenanceDiscoveryPage DiscoverArchiveCandidates(
        FileMemoryStore store, string scope, MaintenancePolicy policy,
        DateTimeOffset now, IReadOnlySet<string> explicitlyProtected,
        MaintenanceDiscoveryCursor cursor, int candidateBudget, int metadataBudget,
        TimeSpan deadline,
        CancellationToken cancellationToken);

    internal IReadOnlySet<string> BuildReferencedRelativePaths(
        string scope, IReadOnlyList<B2RecoveryMaintenanceProjection> recoveryViews,
        IReadOnlyList<MaintenanceCandidate> archiveCandidates);

    internal MaintenanceCandidate ClassifyArchiveCandidate(
        string scope, string versionsDirectory, string bodyPath, string? sidecarPath,
        MaintenancePolicy policy, DateTimeOffset now,
        IReadOnlySet<string> referencedRelativePaths,
        IReadOnlySet<string> explicitlyProtected);

    internal IReadOnlyList<MaintenanceCandidate> OrderCandidates(
        IReadOnlyList<MaintenanceCandidate> candidates);
}

internal sealed record MaintenanceDiscoveryCursor(
    int Version,
    string WorkspaceBinding,
    string RequestedScopesBinding,
    string Scope,
    string Phase,
    string Subject,
    string LastRelativePath,
    IReadOnlyList<MaintenanceSnapshotCandidate> SnapshotCandidates,
    long SnapshotBytes,
    DateTimeOffset ExpiresAt);
internal sealed record MaintenanceDiscoveryPage(
    IReadOnlyList<MaintenanceCandidate> Candidates,
    MaintenanceDiscoveryCursor? Continuation, int Scanned, int MetadataInspected,
    int Skipped,
    bool Exhausted, IReadOnlyList<string> SkippedReasons);
```

Discovery is genuinely bounded and never makes a quota decision from a partial page. The cursor is an immutable state machine: `(Version, WorkspaceBinding, RequestedScopesBinding, Scope, Phase, Subject, LastRelativePath, SnapshotCandidates, SnapshotBytes, ExpiresAt)`, where `Phase` is `DiscoverSubject`, `EvaluateSubject`, or `DiscoverScope`; `SnapshotCandidates` contains only canonical relative paths, timestamps, pair identities, protection facts, and eligibility metadata for one subject. Copy `versions/` names in ordinal order, resume strictly after `LastRelativePath`, inspect at most `candidateBudget` names and `metadataBudget` pair/metadata reads per invocation, and stop at `deadline`, whichever comes first. Hard bounds are 50,000 names, 4,096 snapshot candidates, 4 MiB snapshot bytes, 128 scopes, and 32 KiB encoded token size; exceeding a subject bound marks that subject `Unverifiable` and retains it rather than deleting from it. The scan order is `(scope ordinal, relative directory ordinal, filename ordinal)`; subject candidates are ordered `(subject ordinal, archive timestamp ascending, complete relative path ordinal)`.

For each subject, pass one accumulates the complete snapshot across continuation invocations. It does not delete. Only after the scan reaches the subject's end (`SubjectComplete=true`) may pass two sort the complete snapshot, count all eligible pairs, and mark only the oldest eligible pairs beyond `MaxArchivedVersions` for deletion. Deletion is permitted only in `EvaluateSubject`, under the scope lock, after the complete snapshot and all newer eligible pairs are known; each pair is then revalidated immediately before deletion. If the budget, deadline, cancellation, malformed pair, bound, or token expiry interrupts discovery, no candidate for that subject is deleted and the continuation preserves the page state. A completed subject may be deleted before discovery continues to a later subject because subjects are independent and the lock remains held for that scope batch. `MoreWork` is true whenever any subject or scope remains undiscovered.

Return an opaque source-generated continuation token. Its exact encoding is `b5ct1.` + Base64URL-without-padding(UTF-8 canonical JSON payload) + `.` + Base64URL-without-padding(HMAC-SHA256 of the ASCII prefix and payload). The signing key is the existing `Scrinia:Embeddings:OpenAiApiKey` secret: `WorkspaceConfig` owns and stores that value in the existing flat `.scrinia/config.json`; `IWorkspaceConfig.GetValue("Scrinia:Embeddings:OpenAiApiKey")` is the only access API, and the value is never copied into a result, token payload, log, exception, or command line. This reuses an existing secret and adds no policy key or storage. If the key is absent or empty, token issuance and supplied-token validation fail closed with `InvalidContinuation`; a request that completes without more work needs no token. The key lifecycle is operator-managed with the existing config lifecycle; changing or unsetting it invalidates all outstanding tokens. The payload binds the canonical workspace-root identity, the ordinal requested-scope set, the current scope, phase, cursor fields, and `ExpiresAt`; it expires 24 hours after issuance. Reject unknown version, malformed Base64URL/JSON, missing fields, invalid bounds, absent signing key, bad MAC, expired tokens, mismatched workspace/scope binding, or a token whose snapshot exceeds bounds with a typed invalid-continuation result and zero enumeration/deletion. Tokens are never logged. `Skipped` and stable `SkippedReasons` report budget/deadline/cancellation, unsafe or unreadable metadata, excluded controls, unscanned work, and subject-bound protection. Enumerate only established archive names; exclude journals, locks, temporary files, coordinator records, restore/file-entity evidence, and unknown files. Resolve every candidate with `Path.GetFullPath` and `PathContainment.IsWithin` before opening it. Missing or mismatched `.nmp2`/`.meta.json` pairs, current state, references, explicit protection, reparse points, unexpected kinds, and identity mismatches are protected.

Retention is per subject: protected/young pairs are additional and never count toward or displace the quota. The complete-subject snapshot is the proof that no older pair is deleted before all newer eligible pairs for that subject are known. Retain the newest `MaxArchivedVersions` eligible pairs; delete older eligible pairs only when revalidated age is `>= MinimumAgeDays`. Recompute age and eligibility immediately before each deletion, delete oldest eligible over-limit pairs first, and never delete a protected/young pair to satisfy the count. Add tests that force a subject across multiple pages and assert zero deletion until `SubjectComplete`, then assert deterministic oldest-first pruning and convergence across repeated invocations.

### Task 6: Run focused candidate tests and commit the typed protection layer

Run:

```bash
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~MaintenanceRecoveryCandidateTests|FullyQualifiedName~MaintenancePlannerTests" --no-restore
```

Expected: PASS for all typed B2/B3/B4 protection and archive candidate cases, with retained evidence bytes unchanged.

```bash
git add src/Scrinia.Core/RecoveryContracts.cs src/Scrinia.Core/RecoveryDiscovery.cs src/Scrinia.Core/RecoveryEnvelope.cs src/Scrinia.Core/AppendJournal.cs src/Scrinia.Core/RestoreJournal.cs src/Scrinia.Core/FileEntityTransactionCoordinator.cs src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs src/Scrinia.Core/MultiMemoryTransactionRecord.cs src/Scrinia.Core/TransactionalParticipantSerialization.cs src/Scrinia.Core/MaintenancePlanner.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/MaintenanceRecoveryCandidateTests.cs tests/Scrinia.Tests/MaintenancePlannerTests.cs
git commit -m "feat: classify b5 maintenance candidates safely"
```

## Phase 3: Lock-Scoped Maintenance Execution [PENDING]

### Task 7: Implement the one Core service and store boundary

**Files:**
- Create: `src/Scrinia.Core/FileMemoryMaintenanceService.cs`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs`
- Create: `tests/Scrinia.Tests/FileMemoryMaintenanceTests.cs`

Add the exact service API:

```csharp
internal sealed class FileMemoryMaintenanceService
{
    internal FileMemoryMaintenanceService(FileMemoryStore store, MaintenancePlanner planner);

    internal Task<MaintenanceResult> ExecuteAsync(
        MaintenanceRequest request, CancellationToken cancellationToken = default);
}
```

Add the store entry point:

```csharp
internal Task<MaintenanceScopeResult> ExecuteMaintenanceScopeAsync(
    string scope,
    MaintenanceRequest request,
    MaintenancePlanner planner,
    DateTimeOffset startedAt,
    CancellationToken cancellationToken = default);
```

`ExecuteAsync` calls `MaintenancePolicyLoader.Load` exactly once, resolves requested scopes to persistent scopes, orders them ordinally, validates any continuation token against the exact workspace/requested-scope binding and bounds, and runs one bounded scope at a time. `BatchSizeOverride` may only lower the configured cap. Invalid requests, invalid/expired/mismatched tokens, or invalid policy return typed results before enumeration. `ExecuteMaintenanceScopeAsync` calls `WithExclusiveScopeAdmissionAsync` exactly once (`FileMemoryStore.cs:801-818`), allowing recovery first. Inside the held boundary it obtains typed B2/B3/B4 projections, resumes the cursor state machine, completes the per-subject snapshot before evaluating any quota, builds references/protection, and rechecks containment, existence, pair membership, current/live state, references, explicit protection, terminal proof, age, and retention count immediately before deletion. It never parses or rewrites coordinator records or envelopes. A subject that is not complete at the end of the page has zero deletion decisions; its snapshot and cursor are returned for the next invocation.

Deletion rules are exact: archive body and sidecar are one logical pair; a partial failure retains the remaining member. Verified terminal evidence is deleted only through `RecoveryCleanupDecision`; B5 never deletes locks, temporary files, coordinator records, or unresolved evidence. After successful archive deletion, call only the affected-scope invalidation (`InvalidateScopeCacheAfterRollback`, `FileMemoryStore.cs:1647-1655`); it removes the scope index and artifact cache, clears local topics, and invalidates entity facts. `InvalidateCommittedScope` (`FileMemoryStore.cs:1662-1670`) has the same effects for committed publication but is not called for recovery-evidence-only deletion. A lock timeout produces `LockContended` for that scope with zero deletion; independent scopes continue.

### Task 8: Add failure, race, cancellation, idempotency, and restart tests before implementation

Tests in `FileMemoryMaintenanceTests.cs` must cover:

- policy invalidation with zero filesystem deletion;
- one policy snapshot despite a config mutation during execution;
- current, active, referenced, explicit, unresolved, malformed, unsupported, ambiguous, unsafe, inaccessible, and incomplete candidates retained indefinitely;
- archive over-limit pruning with `MinimumAgeDays`, pair deletion, no body-without-sidecar deletion, no current-entry deletion, and the default ten-version behavior;
- verified terminal B2/B3/B4 evidence cleanup, cleanup retry, no filename-age-only cleanup, and unchanged physical locations/bytes for retained evidence;
- stale discovery defeated by a writer that creates/replaces live state before revalidation;
- concurrent maintenance serialization using one existing scope lock and independent progress in another scope;
- lock timeout as retryable typed non-success;
- permission/IO failure warning with failed candidate retained and successful candidates preserved;
- partial pair failure and process-stop simulation followed by a fresh store invocation that protects the incomplete pair;
- cancellation after the current filesystem operation returns, with no new candidate started;
- batch cap, time budget, continuation, deterministic repeated invocation, no per-candidate task creation, and no hang;
- scope-local cache/index/topic/facts invalidation only after relevant successful archive deletion.

### Task 9: Run red/green maintenance execution tests and commit

Run the red lane before implementation:

```bash
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~FileMemoryMaintenanceTests --no-restore
```

Expected before implementation: FAIL on service, lock/revalidation, deletion, and restart cases. Expected after implementation: PASS with deterministic no-hang completion.

Commit:

```bash
git add src/Scrinia.Core/FileMemoryMaintenanceService.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/FileMemoryMaintenanceTests.cs
git commit -m "feat: execute bounded b5 maintenance under scope locks"
```

## Phase 4: Shared Local CLI Maintenance Path [PENDING]

### Task 10: Add the explicit local `scri maintenance` command, continuation token, and shared result rendering

**Files:**
- Create: `src/Scrinia/Commands/ScriniaCommands.Maintenance.cs`
- Modify: `src/Scrinia/Commands/CliJsonContext.cs` (register the complete directly serialized `MaintenanceResult` graph)
- Create: `tests/Scrinia.Tests/CliMaintenanceTests.cs`

Add this command signature:

```csharp
public async Task<int> Maintenance(
     string? workspaceRoot = null,
     string? scopes = null,
     int? batchSize = null,
     string? protect = null,
     string? continuationToken = null,
     bool json = false,
     CancellationToken cancellationToken = default);
```

The handler calls `WorkspaceSetup.Configure`, then parses comma-separated scopes and protection paths. Trim tokens; reject empty tokens; canonicalize before exact duplicate removal; cap scopes at 128 tokens/256 characters and protections at 4096 tokens/512 characters. Scopes must be valid persistent scope names; protection paths must be non-rooted, non-URI, separator-normalized, relative to the selected scope's `versions/` or evidence directory, and pass `PathContainment`; `..`, escaping separators, and foreign roots fail closed with no service call. `--continuation-token` is the exact CLI spelling and maps to `continuationToken`; its value must be the exact `b5ct1.<payload>.<mac>` token described in Task 5, at most 32 KiB, and contain no whitespace/control characters. A supplied token is mutually exclusive with a new `--scopes`, `--batch-size`, or `--protect` value. Unknown version, malformed Base64URL/JSON, missing fields, bad MAC, expiry after 24 hours, mismatched workspace/requested-scope/current-scope binding, invalid phase, or any cursor/snapshot bound failure returns the typed invalid-continuation `MaintenanceResult` with zero service calls, enumeration, and deletion. The result's `continuationToken` field is the only output continuation field; human output labels it `continuationToken`, and JSON uses `CliJsonContext` source-generated metadata. The handler constructs `FileMemoryMaintenanceService` over `WorkspaceSetup.Store` and calls `ExecuteAsync`; it does not enumerate, parse evidence, classify, delete, or retry. `batchSize` only lowers the cap. Exit 0 is reserved for completed, warning-free work; all other flag/error combinations use the existing nonzero CLI failure class. Add parsing, output, invalid-policy, invalid-token, scope-binding, expiry, contention, and no-separate-path tests. Feed the returned token back unchanged until `Completed=true`; assert no candidate is skipped or deleted twice and that a failed deletion remains retryable.

### Task 11: Run CLI surface and compatibility tests, then commit

Run:

```bash
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~CliMaintenanceTests|FullyQualifiedName~CliSurfaceConformanceTests|FullyQualifiedName~CliConfigConformanceTests" --no-restore
```

Expected: PASS with the new local command visible and no MCP tool, remote option, or existing command/output regression.

```bash
git add src/Scrinia/Commands/ScriniaCommands.Maintenance.cs src/Scrinia/Commands/CliJsonContext.cs tests/Scrinia.Tests/CliMaintenanceTests.cs
git commit -m "feat: add local maintenance command"
```

## Phase 5: Bounded Background Scheduler And Host Shutdown [PENDING]

### Task 12: Implement the bounded background adapter using existing `BackgroundWork` ownership and typed startup warnings

**Files:**
- Create: `src/Scrinia/Services/MaintenanceScheduler.cs`
- Modify: `src/Scrinia/Commands/WorkspaceSetup.cs`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Serve.cs`
- Modify: `src/Scrinia/Program.cs` for the top-level `try/finally` drain boundary
- Create: `tests/Scrinia.Tests/MaintenanceSchedulerTests.cs`

Add these exact internal types:

```csharp
internal sealed record MaintenanceScheduleOptions(
    TimeSpan Interval,
    int BatchSize,
    TimeSpan TimeBudget,
    bool Enabled);

internal sealed class MaintenanceScheduler : IAsyncDisposable
{
    internal MaintenanceScheduler(
        FileMemoryMaintenanceService service,
        MaintenanceScheduleOptions options,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delay,
        Action<string> warning);

    internal Task<MaintenanceSchedulerResult> StartAsync(CancellationToken cancellationToken);
    internal Task<MaintenanceSchedulerResult> StopAsync();
    internal ValueTask DisposeAsync();
}

internal static class MaintenanceScheduleOptionsLoader
{
    internal static MaintenanceScheduleLoadResult Load(IWorkspaceConfig config);
}

internal sealed record MaintenanceScheduleLoadResult(
    MaintenanceScheduleOptions? Options,
    MaintenanceResult? InvalidResult);

internal enum MaintenanceSchedulerErrorKind
{
    InvalidPolicy,
    LoopFault,
    Cancellation,
    FireFault
}

internal sealed record MaintenanceSchedulerError(
    MaintenanceSchedulerErrorKind Kind,
    string Code,
    string Warning);

internal sealed record MaintenanceSchedulerResult(
    bool Started,
    bool Stopped,
    bool Cancelled,
    MaintenanceSchedulerError? Error,
    MaintenanceResult? LastBatch);

internal enum ProgramLifecycleErrorKind
{
    BackgroundDrainFault
}

internal sealed record ProgramLifecycleError(
    ProgramLifecycleErrorKind Kind,
    string Code,
    string Warning);

internal sealed record ProgramLifecycleResult(
    bool Drained,
    ProgramLifecycleError? Error);
```

The scheduler reads only the approved five B5 keys through `MaintenancePolicyLoader`: `Scrinia:Maintenance:BackgroundEnabled`, `Scrinia:Maintenance:JournalCleanupBatchSize`, `Scrinia:Maintenance:TimeBudgetSeconds`, `Scrinia:Retention:MaxArchivedVersions`, and `Scrinia:Retention:MinimumAgeDays`. There are no `Scrinia:Maintenance:IntervalSeconds` or `Scrinia:Maintenance:Scopes` policy keys. Host scheduling uses fixed conservative defaults: a 15-minute interval and `MaintenanceScopes.DefaultLocalScope`; these are code constants, not workspace policy. `MaintenanceScheduleOptionsLoader.Load(IWorkspaceConfig)` returns `MaintenanceScheduleLoadResult`; invalid policy produces a `MaintenanceResult` with `PolicyInvalid=true`, zero deletion, `PolicyError`, and a stable warning, while `Options` is null. `scri serve` reports that result through the existing startup warning/structured output path and starts no scheduler; it does not silently construct an absent scheduler. Explicit CLI maintenance still invokes the Core service and reports the same typed result. Valid background mode admits one request immediately after startup and one per interval, one service invocation at a time, with `new MaintenanceRequest([MaintenanceScopes.DefaultLocalScope], options.BatchSize, MaintenanceScopes.EmptyProtectionSet, continuationToken)`. `Scopes` is an `IReadOnlyList<string>?`; the scheduler always supplies a one-element ordinal list, `BatchSizeOverride` is null or `1..10000`, the protection set is non-null and ordinal, and tokens are null or the exact validated token. Tokens are retained per local scope and returned in the next request; if the signing key is absent, continuation is not issued and the result remains fail-closed. Skipped work remains visible through flags/counts.

The scheduler uses one typed contract for every scheduler outcome. Invalid policy maps to `InvalidPolicy` and the shared `MaintenanceResult`; a loop exception maps to `LoopFault`; cancellation maps to `Cancellation`; and a maintenance invocation fault maps to `FireFault`. Each scheduler error has a stable code and sanitized warning (`MAINTENANCE_POLICY_INVALID`, `MAINTENANCE_LOOP_FAULT`, `MAINTENANCE_CANCELLED`, or `MAINTENANCE_FIRE_FAULT`) and contains no exception message, path, token, or secret. The CLI renders the shared maintenance result; background mode writes only the sanitized warning and keeps serving.

`BackgroundWork.Fire` remains the exact `void Fire(Func<CancellationToken, Task> work, string description)` API and is not treated as a result-returning call. The scheduler owns this exact wrapper and callback channel:

```csharp
private Task RunMaintenanceInvocationAsync(
    CancellationToken cancellationToken,
    Action<MaintenanceSchedulerError> reportFault,
    TaskCompletionSource<object?> completion);

private void RecordFireFault(MaintenanceSchedulerError error);
```

For each interval the scheduler creates one `TaskCompletionSource<object?>` with `RunContinuationsAsynchronously`, installs its task as `_activeInvocation` under the scheduler state lock, and calls `BackgroundWork.Fire(ct => RunMaintenanceInvocationAsync(ct, RecordFireFault, completion), "maintenance");`; it never creates a task per candidate. The lambda therefore supplies the exact `Func<CancellationToken, Task>` work delegate, while `RecordFireFault` is the exact `Action<MaintenanceSchedulerError>` callback consumed by `RunMaintenanceInvocationAsync`. `RunMaintenanceInvocationAsync` awaits exactly one bounded `ExecuteAsync` call, stores the returned `LastBatch`, catches `OperationCanceledException` only when the scheduler token is cancelled, and catches every other `Exception` before it reaches `BackgroundWork.Fire`; the catch calls `reportFault` with `new MaintenanceSchedulerError(FireFault, "MAINTENANCE_FIRE_FAULT", "[scrinia:warn] Background maintenance invocation failed.")`. `RecordFireFault` records that already-sanitized typed error in the scheduler's first-fault slot; it never accepts or reads an exception and never reads or forwards `Message`, `StackTrace`, paths, tokens, or secrets. Its callback result is surfaced through the next `MaintenanceSchedulerResult` returned by `StartAsync`/`StopAsync`; it is not a second task result and does not alter the void `BackgroundWork.Fire` API. The callback is scheduler-owned and serialized under the scheduler state lock, and concurrent faults are retained in an ordinally stable first-fault slot. `StopAsync` cancels the linked current-work CTS and awaits `_activeInvocation`, so the void `Fire` call is never mistaken for an awaitable result. The wrapper reports cancellation separately and does not turn expected shutdown cancellation into `FireFault`. No fault is silently swallowed, retried recursively, or converted to success.

`DrainFault` is deliberately not a scheduler error. `MaintenanceScheduler.StopAsync()` closes admission, cancels the scheduler delay/current-work CTS, and awaits the loop and the one active invocation; it does not call `BackgroundWork.DrainAsync`, so it cannot claim ownership of unrelated detached work. `DisposeAsync()` is idempotent, calls the same stop path if necessary, and then releases scheduler resources. The top-level program owns the single global drain through this exact helper in `Program.cs`:

```csharp
internal static async Task<ProgramLifecycleResult> DrainBackgroundWorkAsync(TimeSpan timeout);
```

`DrainBackgroundWorkAsync` awaits `BackgroundWork.DrainAsync(timeout)`, returns `new(true, null)` on `true`, returns `new(false, null)` on timeout (the existing sanitized timeout warning is rendered by `Program.cs`), and catches every exception as `new ProgramLifecycleResult(false, new ProgramLifecycleError(BackgroundDrainFault, "MAINTENANCE_DRAIN_FAULT", "[scrinia:warn] Background work drain failed."))`. The exact top-level ordering is `try { await app.RunAsync(args); } finally { var drain = await DrainBackgroundWorkAsync(TimeSpan.FromSeconds(10)); RenderProgramLifecycleWarning(drain); }`; the `finally` runs for startup failure, host failure, normal cancellation, and command cancellation. For `serve`, the command's inner `finally` is first: `await scheduler.StopAsync(); await scheduler.DisposeAsync();`; only after `Serve` returns or faults does control reach the outer program drain. Thus scheduler Stop/Dispose owns scheduler lifecycle, Program owns the one global `BackgroundWork.DrainAsync` call, and a drain fault cannot skip disposal or be misreported as a scheduler fault.

`WorkspaceSetup.Configure` installs the resolved root/store (`WorkspaceSetup.cs:60-77`); `scri serve` then warns on orphans, performs optional model setup, and calls `LoadPluginsAsync` (`ScriniaCommands.Serve.cs:36-47`). The exact host control flow is changed in `ScriniaCommands.Serve.cs`, not presumed in `Program.cs`: after `LoadPluginsAsync`, construct the Core service/options, construct the scheduler, and enter `try { await scheduler.StartAsync(cancellationToken); await host.RunAsync(cancellationToken); } finally { await scheduler.StopAsync(); await scheduler.DisposeAsync(); }`. `StopAsync` first closes admission, cancels the scheduler delay/current-work CTS, and awaits the loop plus the active wrapper; it is idempotent and preserves the first stop/loop/fire failure. `Program.cs` remains the top-level command runner and the sole owner of global drain: wrap `await app.RunAsync(args)` in `try { ... } finally { var drain = await DrainBackgroundWorkAsync(TimeSpan.FromSeconds(10)); RenderProgramLifecycleWarning(drain); }`. `DrainBackgroundWorkAsync` maps a `BackgroundWork.DrainAsync` exception to the typed `ProgramLifecycleErrorKind.BackgroundDrainFault` result and maps timeout to `Drained=false` without throwing; both warnings are sanitized. Thus startup failure, host failure, normal cancellation, and command cancellation all execute exactly one top-level drain, while serve always stops/cancels/awaits/disposes the scheduler before control returns to `Program.cs`. The scheduler uses `BackgroundWork.Fire` for one bounded invocation, never one task per candidate, never recursive retry, and never plugin lifecycle callbacks. Tests cover defaults, scope selection, request construction, disabled mode, serialization, cancellation, one-invocation concurrency, callback/result-channel delivery, sanitized `FireFault` diagnostics, Stop/Dispose idempotency, active-invocation cancellation/awaiting, Program-level drain success/timeout/exception mapping, exact inner-then-outer finally ordering, startup failure, host failure, and plugin absence.

### Task 13: Run host, scheduler, typed-invalid-policy, and exception-safe shutdown tests and commit

Run:

```bash
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~MaintenanceSchedulerTests|FullyQualifiedName~BackgroundWorkTests|FullyQualifiedName~CliLifecycleFactFlushTests" --no-restore
```

Expected: PASS with approved-five-key-only scheduling, typed invalid-policy startup reporting, bounded scheduler shutdown, exact stop/cancel/await/drain ordering, and existing detached-work behavior unchanged.

```bash
git add src/Scrinia/Services/MaintenanceScheduler.cs src/Scrinia/Commands/WorkspaceSetup.cs src/Scrinia/Commands/ScriniaCommands.Serve.cs src/Scrinia/Program.cs tests/Scrinia.Tests/MaintenanceSchedulerTests.cs
git commit -m "feat: schedule bounded background maintenance"
```

## Phase 6: Full Compatibility And Acceptance Gate [PENDING]

### Task 14: Add explicit acceptance coverage without changing existing regression tests

**Files:**
- Create: `tests/Scrinia.Tests/MaintenanceSerializationAndCompatibilityTests.cs`
- Modify: `src/Scrinia.Core/MaintenanceJsonContext.cs` (assert Core registrations)
- Modify: `src/Scrinia/Commands/CliJsonContext.cs` (assert CLI registrations)
- Modify: only the B5 test files from Phases 1–5 when extending coverage for a concrete failure

Tests must assert that no maintenance DTO is reflection-serialized, no new durable file/format/index/protection field exists, flat config round-trips through the existing `WorkspaceConfig` path, all unresolved B2/B3/B4 evidence survives a fresh process indefinitely, and existing append/restore/file-entity/B3/B4/NMP/2/sidecar/facts/CLI behavior remains unchanged. Assert the authoritative Core registrations for `MaintenanceContinuationPayload`, `MaintenanceSnapshotCandidate`, `MaintenanceResult`, `MaintenanceScopeResult`, `MaintenanceCandidateResult`, `MaintenanceCandidate`, `MaintenanceCandidateKind`, `MaintenanceProtectionReason`, `MaintenancePolicy`, `MaintenancePolicyLoadResult`, and `MaintenancePolicyError`; assert the explicit CLI result graph registrations listed in Task 2. Include token encoding/size/MAC/expiry/scope-binding, missing/rotated `Scrinia:Embeddings:OpenAiApiKey`, and repeated-convergence tests, plus a privacy test rejecting content, secrets, arbitrary absolute paths, raw exception text, and foreign roots from results/logs.

### Task 15: Run focused lanes and the complete solution verification

Run:

```bash
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Maintenance" --no-restore -m:1 -v:minimal
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Append|FullyQualifiedName~WholeMemory|FullyQualifiedName~FileEntity|FullyQualifiedName~Recovery|FullyQualifiedName~TransactionalParticipant|FullyQualifiedName~Cli|FullyQualifiedName~ScriniaMcp" --no-restore -m:1 -v:minimal
dotnet build Scrinia.sln -c Release --no-restore -m:1 -v:minimal
dotnet publish src/Scrinia/Scrinia.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=true -p:SuppressTrimAnalysisWarnings=false -p:EnableTrimAnalyzer=true --no-restore -v:minimal
dotnet test Scrinia.sln --no-restore -m:1 -v:minimal
git diff --check
git status --short
git diff --name-only -- src tests
```

Expected: focused B5, regression, Release build, and full solution commands exit 0; the trimmed publish passes with no B5 source-generation/trimming warnings; the ordinary full suite records pass/skipped/failed/total counts, exit status, elapsed time, and no-hang evidence; `git diff --check` is clean; the scoped diff contains only the B5 implementation/test paths and no staged plan/spec/ledger/evidence files. The acceptance ledger separately records the implementation base commit and the complete historical path inventory obtained by comparing that recorded commit with `HEAD`; the command above is the working-tree scope check.

### Task 16: Perform independent self-review and scoped commit

Review the exact implementation diff against every B5 spec section and acceptance row. Reject any use of raw JSON/exception text, deletion outside a contained expected directory, deletion without revalidation, unresolved-evidence deletion, second lock acquisition, per-candidate task, recursive retry, plugin prerequisite, remote/index/format/lifecycle change, or separate CLI policy/deletion logic. Record evidence in the B5 ledger and commit only concrete final test/gate fixes:

```bash
git add src tests
git commit -m "test: close b5 maintenance acceptance gate"
```

## Dependencies

1. Phase 1 establishes all types consumed by later phases.
2. Phase 2 depends on accepted B2 recovery contracts, the accepted B3 coordinator record, and the accepted B4 participant/quarantine state. It must not alter those wire contracts.
3. Phase 3 depends on Phases 1–2 and owns the sole deletion decision path.
4. Phase 4 depends on Phase 3 and may only map the Core result through the local CLI.
5. Phase 5 depends on Phase 3 and the existing `BackgroundWork` drain semantics; it must not depend on an embedding plugin.
6. Phase 6 depends on all prior phases and is the final B5 acceptance gate.

## Exact Symbol Citations And Effects

- B2 envelope/discovery ownership: `src/Scrinia.Core/RecoveryContracts.cs:119-136`, `src/Scrinia.Core/RecoveryDiscovery.cs:145-168`, `src/Scrinia.Core/FileMemoryStore.cs:820-821`; envelopes are parsed only by the B2 adapter and retained evidence is never rewritten.
- B2 adapter projection boundary: `src/Scrinia.Core/RecoveryEnvelope.cs` (existing common envelope type); Task 4 owns only the non-serialized maintenance projection adapter and leaves the B2 wire locations unchanged (`B5 spec §2, §4, §10`).
- B3 ownership: `src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs:256-318` and `src/Scrinia.Core/MultiMemoryTransactionRecord.cs:7-63`; only the B3 adapter projects the record.
- B4 state/rejected metadata: `src/Scrinia.Core/TransactionalParticipantSerialization.cs:7-63,80-97`; rejected entries never become terminal proof.
- Invalidation: `src/Scrinia.Core/FileMemoryStore.cs:1647-1655` (`InvalidateScopeCacheAfterRollback`) and `:1662-1670` (`InvalidateCommittedScope`); both affect index, artifact cache, local topics, and entity facts. B5 uses the first after archive deletion only; evidence-only deletion has no live-view effect.
- Lock and lifecycle: `src/Scrinia.Core/FileMemoryStore.cs:801-818`; `src/Scrinia/Commands/WorkspaceSetup.cs:60-77`; `src/Scrinia/Commands/ScriniaCommands.Serve.cs:36-70`; `src/Scrinia/Program.cs:16-33` (top-level `try/finally` drain boundary); `src/Scrinia.Core/BackgroundWork.cs:62-126,129-154` (void Fire and global drain contracts).
- Bounded retention and continuation requirements: `docs/superpowers/specs/2026-09-15-b5-retention-maintenance-design.md:157-188,220-243,249-274` (complete eligibility/revalidation, bounded continuation, race handling, typed outcomes, and sanitized CLI behavior).
- Approved policy surface and host behavior: `docs/superpowers/specs/2026-09-15-b5-retention-maintenance-design.md:75-100,215-218` (exact five keys, fixed bounded scheduling, and existing drain ownership).

## Path Inventory And Scoped Commit Protocol

Permitted implementation paths are exactly these named B5 files: `src/Scrinia.Core/MaintenanceContracts.cs`, `src/Scrinia.Core/MaintenanceJsonContext.cs`, `src/Scrinia.Core/Configuration/MaintenancePolicy.cs`, `src/Scrinia.Core/RecoveryContracts.cs`, `src/Scrinia.Core/RecoveryDiscovery.cs`, `src/Scrinia.Core/RecoveryEnvelope.cs`, `src/Scrinia.Core/AppendJournal.cs`, `src/Scrinia.Core/RestoreJournal.cs`, `src/Scrinia.Core/FileEntityTransactionCoordinator.cs`, `src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs`, `src/Scrinia.Core/MultiMemoryTransactionRecord.cs`, `src/Scrinia.Core/TransactionalParticipantSerialization.cs`, `src/Scrinia.Core/MaintenancePlanner.cs`, `src/Scrinia.Core/FileMemoryMaintenanceService.cs`, `src/Scrinia/Commands/ScriniaCommands.Maintenance.cs`, `src/Scrinia/Commands/CliJsonContext.cs`, `src/Scrinia/Commands/WorkspaceSetup.cs`, `src/Scrinia/Commands/ScriniaCommands.Serve.cs`, `src/Scrinia/Program.cs`, `src/Scrinia/Services/MaintenanceScheduler.cs`, and the named B5 files under `tests/Scrinia.Tests/`. No `BackgroundWork.cs` change is permitted by B5. No other source, test, plan, spec, roadmap, ledger, or evidence file is an implementation path.

Each task stages only its explicit file list, verifies `git diff --name-only` against that list, and then creates the task's conventional commit. Never stage or commit this plan, the B5 spec, `.planning/`, or unrelated source; the final task may commit only concrete B5 source/test fixes.

## Final Verification And Evidence

- Policy defaults, lexical/range validation, invalid-policy zero-deletion, and one-snapshot behavior are covered by `MaintenancePolicyTests` and `FileMemoryMaintenanceTests`.
- Candidate, protection, reference, containment, pair, deterministic ordering, and indefinite unresolved-evidence behavior are covered by `MaintenanceRecoveryCandidateTests` and `MaintenancePlannerTests`.
- Lock/revalidation, races, partial failure, idempotency, restart, bounds, cancellation, continuation, cache locality, and typed warnings are covered by `FileMemoryMaintenanceTests`.
- Human/JSON CLI mapping and no-separate-deletion behavior are covered by `CliMaintenanceTests` and CLI conformance tests.
- Scheduler admission, disabled policy, one-invocation concurrency, exact `Func<CancellationToken, Task>` wrapper and `Action<MaintenanceSchedulerError>` callback signatures, typed callback/result-channel fault isolation, scheduler Stop/Dispose cancellation and idempotency, Program-level typed drain mapping, exact inner-then-outer shutdown ordering, and plugin independence are covered by `MaintenanceSchedulerTests` and its Program lifecycle cases.
- Source-generated serialization, privacy, compatibility, and no-format/index/remote/lifecycle changes are covered by `MaintenanceSerializationAndCompatibilityTests`, Release build, trimmed publish, and the full solution suite.
- `MaintenanceJsonContext` and `CliJsonContext` registrations cover the complete directly serialized B5 type set; continuation tokens are authenticated, bounded, expiring, scope-bound, and fail closed.
- The final acceptance ledger must retain raw command output, exit codes, elapsed/no-hang evidence, focused/full counts, exact base and commit inventory, independent review disposition, and a clean staged inventory.

## Self-Review Record

The plan was checked against B5 spec sections 1–14. Policy schema/defaults and exact numeric bounds are pinned in Phase 1; typed candidate/protection/reference modeling, `RecoveryEnvelope.cs` adapter ownership, and all B2/B3/B4 locations are pinned in Phase 2; complete per-subject snapshots, two-pass quota proof, one-lock admission, revalidation, deterministic bounds, pair deletion, invalidation, failure, idempotency, cancellation, and restart behavior are pinned in Phase 3; authenticated continuation-token CLI mapping using the existing `Scrinia:Embeddings:OpenAiApiKey` secret is pinned in Phase 4; approved-five-key-only scheduling, the unchanged void `BackgroundWork.Fire` boundary, scheduler-owned wrapper callback/result-channel `FireFault` reporting, Program-owned typed global drain mapping, and exact scheduler-stop/inner-finally then outer-finally ordering are pinned in Phase 5; and every testing/acceptance/privacy/source-generation/compatibility constraint is pinned in Phase 6.

No `TBD`, `TODO`, “implement later,” generic handling placeholder, scheduler policy alternative, or partial-page deletion path remains. Signatures are consistent: `MaintenancePolicyLoader.Load(IWorkspaceConfig)` feeds `FileMemoryMaintenanceService.ExecuteAsync(MaintenanceRequest, CancellationToken)`, which invokes `FileMemoryStore.ExecuteMaintenanceScopeAsync`; B2/B3/B4 adapters produce the named projections and `RecoveryCleanupDecision`; CLI and scheduler consume only `MaintenanceResult`; `MaintenanceScheduleOptionsLoader.Load(IWorkspaceConfig)` returns `MaintenanceScheduleLoadResult` with a typed invalid result; `BackgroundWork.Fire(Func<CancellationToken, Task>, string)` remains void; `RunMaintenanceInvocationAsync(CancellationToken, Action<MaintenanceSchedulerError>, TaskCompletionSource<object?>)` and `RecordFireFault(MaintenanceSchedulerError)` match the callback/result channel exactly; scheduler wrapper faults reach `MaintenanceSchedulerResult.Error` only through that scheduler callback channel; `MaintenanceScheduler.StopAsync()` never drains global work; and `Program.DrainBackgroundWorkAsync(TimeSpan)` is the sole typed mapper for `BackgroundWork.DrainAsync`. Discovery is cursor/budget bounded, complete per subject before deletion, flags are orthogonal, policy-invalid/token-invalid conversion is unified, and protected/young pairs are never displaced. The plan retains unresolved evidence indefinitely and does not rewrite or migrate any format.

The scope explicitly excludes remote/API/index/archive-manifest/format/lifecycle changes, new MCP surfaces, workspace-wide coordination, plugin participation, unbounded work, automatic repair, and changes to existing forget/compact/append/restore/import/migration/bundle behavior. The only new user-facing surface is the approved local operator maintenance command.
