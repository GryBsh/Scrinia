# B4 Plugin and Event Transaction Boundary Implementation Plan

> Documentation-only implementation plan. This plan does not authorize source changes outside
> the path inventory below, and does not add an MCP tool, CLI command, endpoint, journal, or
> coordinator.

## Goal and fixed boundary

B4 adds one explicit, trusted, in-process transactional participant contract for plugins and one
host adapter into the existing B3 coordinator. B3 remains the sole scope-lock owner, decision
writer, recovery authority, and aggregate-result producer. `IMemoryEventSink`,
`IMemoryOperationHook`, `McpPluginHost`, and the out-of-process embeddings plugin remain observers;
none can veto or participate in a transaction.

No public MCP/CLI surface, response field, exit code, remote transport, NMP/2 format, ordinary
sidecar format, vector/index contract, archive/GC path, or multi-scope transaction is in scope.

## B3 dependency gate

The checked-in B3 acceptance record is `docs/superpowers/ledgers/2026-09-13-b3-final-acceptance.md`.
It is accepted and unblocks B4 because it links the authoritative transcript and independent review;
those linked artifacts are the B3 dependency evidence. This follows the Phase B roadmap requirement
that B4 consumes accepted B1–B3 coordination contracts
(`docs/superpowers/specs/2026-09-10-phase-b-roadmap-design.md:123-133,171-177`). Do not replace
those linked artifacts with summary counts during B4.

The reconciled B3 implementation/test boundary is the exact inclusive range
`986fce0a^..1a10c0f77316fa0717c9ad4307a162a8176c1041`, as recorded by the B3
acceptance ledger and independent review. `63befd20` is a later documentation-only path-inventory
correction; `4a15e329` is evidence documentation, and `5f17ae42` is B4 planning/specification
documentation. Neither documentation/evidence commit is part of the B3 implementation/test
boundary.

## Existing B3 contract to consume

| Concern | Current symbol | B4 rule |
|---|---|---|
| Record | `internal sealed record MultiMemoryTransactionRecord` in `src/Scrinia.Core/MultiMemoryTransactionRecord.cs:7`; constructor at `:9-18`; properties at `:41-50` | Add only the optional `TransactionalParticipants` extension described below. |
| Manifest | `internal sealed record MultiMemoryParticipantManifest(string subject, string operationId, string kind)` in `src/Scrinia.Core/MultiMemoryTransaction.cs:117-136` | Do not alter the identity tuple. |
| Adapter | `internal interface IMultiMemoryParticipantAdapter` in `MultiMemoryTransaction.cs:249-263` | The host adapter implements this seam. |
| Coordinator | `MultiMemoryTransactionCoordinator`; current constructors at `:15-31`; lifecycle methods include `ExecuteAsync`, `RecoverUnderLockAsync`, `FinishDecisionAsync`, and `RollbackBeforeDecisionAsync` | Inject the Core factory through a new overload; preserve the existing constructor for non-plugin tests. |
| Codec | `MultiMemoryTransactionRecordCodec` in `MultiMemoryTransactionRecord.cs:126-340` | Extend the existing codec surface only with the named immutable participant-update and expected-write APIs below. |
| Existing format | `FormatName = "scrinia.multi-memory-transaction"`; `FileName = ".multi-memory-transaction.json"`; version `1`; document `1 MiB`; participants `4096`; identity text `512` | Do not change these values. |
| JSON context | `MultiMemoryTransactionJsonContext`, camel case, `JsonUnmappedMemberHandling.Disallow` | Register every new directly serialized root here; unknown fields fail closed. |
| Loading | `WorkspaceSetup.LoadPluginsAsync` and `TryLoadVulkanPluginAsync` in `src/Scrinia/Commands/WorkspaceSetup.cs` | Explicit in-process participant registration occurs during `LoadPluginsAsync`; `McpPluginHost` is never registered. |
| Sanitizer | Duplicated private `SafeMultiMemoryDetail` and `IsStableDetailCharacter` in `MemoryTools.Core.cs:127-145` and `ScriniaCommands.Memory.cs:122-140` | Extract one Core helper with the exact signature below and make both facades call it. |

## Public plugin contract (compile-ready)

Add these public types to `src/Scrinia.Plugin.Abstractions/ITransactionalMemoryParticipant.cs`.
They may reference the already-public `Scrinia.Core.MemoryAddress` only where shown; no internal
Core type crosses the public boundary.

```csharp
public interface ITransactionalMemoryParticipant : IScriniaPlugin
{
    TransactionalParticipantDescriptor Descriptor { get; }
    Task<TransactionalPrepareResult> PrepareAsync(
        TransactionalPrepareContext context, CancellationToken cancellationToken = default);
    Task<TransactionalParticipantResult> CommitAsync(
        TransactionalCommitContext context, CancellationToken cancellationToken = default);
    Task<TransactionalParticipantResult> RollbackAsync(
        TransactionalRollbackContext context, CancellationToken cancellationToken = default);
    Task<TransactionalCommitStatusResult> GetCommitStatusAsync(
        TransactionalCommitContext context, CancellationToken cancellationToken = default);
}

public sealed record TransactionalParticipantDescriptor
{
    public TransactionalParticipantDescriptor(
        string participantIdentity, int contractVersion,
        IReadOnlyList<string> capabilities, TransactionalRetryPolicy retryPolicy,
        string orderingKey);
    public string ParticipantIdentity { get; }
    public int ContractVersion { get; }
    public IReadOnlyList<string> Capabilities { get; }
    public TransactionalRetryPolicy RetryPolicy { get; }
    public string OrderingKey { get; }
}

public sealed record TransactionalRetryPolicy
{
    public TransactionalRetryPolicy(int maxAttempts = 3,
        TimeSpan? maxElapsed = null, TimeSpan? initialDelay = null,
        TimeSpan? maximumDelay = null);
    public int MaxAttempts { get; }
    public TimeSpan MaxElapsed { get; }
    public TimeSpan InitialDelay { get; }
    public TimeSpan MaximumDelay { get; }
}

public sealed record TransactionalPrepareContext(
    string TransactionId, string Scope, MemoryAddress Address,
    string OperationId, string Kind,
    IReadOnlyList<TransactionalMemoryManifestEntry> Manifest);

public sealed record TransactionalCommitContext(
    string TransactionId, string Scope, MemoryAddress Address,
    string OperationId, string Kind, TransactionalPreparedHandle PreparedHandle);

public sealed record TransactionalRollbackContext(
    string TransactionId, string Scope, MemoryAddress Address,
    string OperationId, string Kind, TransactionalPreparedHandle PreparedHandle);

public sealed record TransactionalMemoryManifestEntry(
    MemoryAddress Address, string OperationId, string Kind);

public sealed record TransactionalPreparedHandle(string Reference, string Version);

public sealed record TransactionalParticipantFailure(
    TransactionalFailureCode Code, string Detail, bool StateMayHaveChanged);

public sealed record TransactionalRecoveryEvidence(
    string EvidenceReference, string EvidenceKind, string EvidenceVersion);

public enum TransactionalParticipantState { Committed, RolledBack, Changed, Ambiguous }
public enum TransactionalCommitStatus { Unknown, Prepared, Committed }
public enum TransactionalFailureCode
{
    Veto, DeclaredTransient, Permanent, UnknownTransient, Cancelled, InvalidResponse,
    CommitFailed, VerificationFailed, RollbackFailed, EvidenceInvalid,
    RetryMonotonicClockFailed, RetryMonotonicClockMovedBackwards, RetryElapsedOverflow
}
```

The earlier positional declarations of `TransactionalPrepareResult`,
`TransactionalParticipantResult`, and `TransactionalCommitStatusResult` in this plan are
**superseded**. The explicit validating declarations in the next section are the only authoritative
construction signatures; no positional record constructor, object initializer, or parallel result
shape may be introduced in production, fixtures, or tests.

All records are immutable, copy collection arguments, reject null elements, and validate in their
constructors. Text fields are non-null, nonblank, maximum 512 UTF-16 code units. `OrderingKey` is
non-null and must already be canonical: 1–64 ASCII characters matching
`[a-z0-9][a-z0-9-]{0,63}`. It is compared with `StringComparer.Ordinal`; no trimming, case-folding,
or other normalization is applied. `ContractVersion`
is exactly `1`; capabilities are nonblank, ordinal-distinct, at most 32 entries, each at most 64
characters. `MaxAttempts` is `1..8`; null `MaxElapsed` defaults to `00:00:30`, null
`InitialDelay` defaults to `00:00:00.100`, and null `MaximumDelay` defaults to `00:00:02`.
`MaxElapsed` is `1..00:05:00`; delays are nonnegative, and
`InitialDelay <= MaximumDelay <= 00:00:30`. `Manifest` contains `1..4096` entries. A prepare
success is exactly `(Handle != null, Failure == null)`; prepare failure is exactly
`(Handle == null, Failure != null)`. A participant success is `Committed` or `RolledBack` with
no failure; `Changed` or `Ambiguous` requires failure and evidence. Any other combination throws
`ArgumentException` and is mapped to `InvalidResponse` before persistence. Evidence references are
safe relative paths using the existing B3 predicate; no payload, exception, URI, absolute path, or
serialized handle is accepted.

### Result construction is an explicit validation boundary

Replace the positional result records above with explicit sealed records/classes. No production code,
fixture, or test may use a positional constructor or object initializer that bypasses these
invariants. The only construction signatures are:

```csharp
public sealed record TransactionalPrepareResult
{
    public TransactionalPrepareResult(
        TransactionalPreparedHandle? handle,
        TransactionalParticipantFailure? failure,
        TransactionalRecoveryEvidence? evidence);
    public TransactionalPreparedHandle? Handle { get; }
    public TransactionalParticipantFailure? Failure { get; }
    public TransactionalRecoveryEvidence? Evidence { get; }
}

public sealed record TransactionalParticipantResult
{
    public TransactionalParticipantResult(
        TransactionalParticipantState state,
        TransactionalParticipantFailure? failure,
        TransactionalRecoveryEvidence? evidence);
    public TransactionalParticipantState State { get; }
    public TransactionalParticipantFailure? Failure { get; }
    public TransactionalRecoveryEvidence? Evidence { get; }
}

public sealed record TransactionalCommitStatusResult
{
    public TransactionalCommitStatusResult(
        TransactionalCommitStatus state,
        TransactionalRecoveryEvidence? evidence,
        TransactionalParticipantFailure? failure);
    public TransactionalCommitStatus State { get; }
    public TransactionalRecoveryEvidence? Evidence { get; }
    public TransactionalParticipantFailure? Failure { get; }
}
```

The constructors enforce these complete truth tables. `TransactionalPrepareResult` accepts success
only as `(handle != null, failure == null, evidence == null)` and failure only as
`(handle == null, failure != null, evidence == null or non-null)`; `(null, null, *)` and
`(non-null, non-null, *)` throw. `TransactionalParticipantResult` accepts `Committed` and
`RolledBack` only with `(failure == null, evidence == null)`, and accepts `Changed` and `Ambiguous`
only with `(failure != null, evidence != null)`; every other state/failure/evidence combination
throws. `TransactionalCommitStatusResult` accepts `Prepared` and `Committed` only with
`(evidence == null, failure == null)`, and `Unknown` only with `(evidence != null, failure != null)`;
all other combinations throw. Enum values outside the declared sets also throw. The constructors
copy/validate nested values before assignment, so `ValidatePrepareResult` and
`ValidateParticipantResult` only classify an exception raised by a participant or adapter and never
serve as a second construction path.

`TransactionalParticipantContractTests` must enumerate every invalid combination: prepare null/null,
prepare handle+failure, prepare handle+evidence, each participant state with null/non-null failure
and evidence (including both missing for `Changed`/`Ambiguous`), each commit-status state with all
four failure/evidence presence combinations, unknown enum values, null nested values, and oversized
text. It must also assert every valid row in the three truth tables and that invalid callback
responses map to `InvalidResponse` before any durable write.

## Core-owned injection seam

Add the following **internal** interface to `src/Scrinia.Core/MultiMemoryTransaction.cs`; it is
the only injection type owned by Core:

```csharp
internal interface IMultiMemoryParticipantAdapterFactory
{
    Task<IMultiMemoryParticipantAdapter> CreateAsync(
        MultiMemoryParticipantRequest request, CancellationToken cancellationToken);
    Task<IMultiMemoryParticipantAdapter> RehydrateAsync(
        B4ParticipantRehydrationRequest request,
        CancellationToken cancellationToken);
}
```

`participantIdentity` and `contractVersion` are explicit fields on `B4ParticipantRecoveryState`, not
values inferred from `manifest.Kind` or a plugin lookup. The factory requires ordinal equality between
the state identity and the registry key and requires `ContractVersion == 1`.
Unknown identity, missing registry entry, duplicate identity, or unsupported contract version
returns a typed recovery mismatch; it does not select a similarly named plugin, invoke a callback,
or delete evidence. The coordinator maps that result to scope-local `RecoveryRequired` and
`Quarantined` state. Admission rejects an unknown or quarantined identity before callbacks or a
durable decision.

Add `InternalsVisibleTo Include="Scrinia"` to `src/Scrinia.Core/Scrinia.Core.csproj`; add a
`ProjectReference Include="..\Scrinia.Plugin.Abstractions\Scrinia.Plugin.Abstractions.csproj"`
to `src/Scrinia/Scrinia.csproj`. The CLI is then the only assembly allowed to implement the
factory; Core still has no reference to plugin abstractions.

Add these exact overloads while retaining the current constructors:

```csharp
internal FileMemoryStore(string workspaceRoot, RankerOptions? rankerOptions,
    MemoryValidationOptions? writeValidationOptions,
    IMultiMemoryParticipantAdapterFactory? participantFactory);

internal MultiMemoryTransactionCoordinator(FileMemoryStore store,
    IMultiMemoryParticipantAdapterFactory? participantFactory,
    TimeSpan? lockTimeout = null);
```

The existing `FileMemoryStore(string, RankerOptions? = null, MemoryValidationOptions? = null)`
delegates with `participantFactory: null`. The coordinator's existing delegate constructors remain
for B3 tests. When the factory is non-null, `FileMemoryStore` passes `CreateAsync` for admission
and `RehydrateAsync` for restart; otherwise the current B3 resolver remains the sole path. The
admission delegate is composed as described below: B3 requests use the original resolver and
transactional bridge requests use the factory `CreateAsync` branch.

`WorkspaceSetup.LoadPluginsAsync` must first build an ordinally sorted registry of explicitly
loaded in-process `ITransactionalMemoryParticipant` instances, reject duplicate descriptor
identities, construct `TransactionalParticipantAdapterFactory`, then construct or replace the
workspace store with the factory before any operation calls `LoadPluginsAsync`'s return path.
`TryLoadVulkanPluginAsync` remains child-process observer setup and never enters this registry.
The host adapter file is `src/Scrinia/Services/TransactionalParticipantAdapter.cs`; registry
construction is `src/Scrinia/Services/TransactionalParticipantRegistry.cs`.

Admission composes with B3 rather than bypassing it. The exact existing store entry point remains
`FileMemoryStore.ExecuteMultiMemoryAsync(MultiMemoryTransactionRequest request,
Func<MultiMemoryParticipantRequest, CancellationToken, Task<IMultiMemoryParticipantAdapter>>
admitParticipant, CancellationToken cancellationToken = default)`, which forwards to the exact
coordinator entry point
`MultiMemoryTransactionCoordinator.ExecuteAsync(MultiMemoryTransactionRequest request,
Func<MultiMemoryParticipantRequest, CancellationToken, Task<IMultiMemoryParticipantAdapter>>
admitParticipant, CancellationToken cancellationToken = default)`. The supplied delegate remains
the B3 file-participant resolver (`ResolveMultiMemoryParticipantAsync`); it is called once for each
ordered B3 request and retains B3 address/scope/subject/operation/kind validation.
The coordinator then asks the injected registry for explicitly requested B4 participant adapters
inside the same `WithExclusiveScopeAdmissionAsync` lock and recovery barrier. It appends those
adapters to the single admission result, validates the combined 4096 limit and duplicate
identities, and runs one prepare/decision lifecycle. It must not call the B3 delegate twice, scan
the store separately, acquire another lock, or invoke plugin admission outside
`AdmitUnderLockAsync`.

The exact B4 extension on `MultiMemoryTransactionRequest` is:

```csharp
internal MultiMemoryTransactionRequest(
    string scope,
    IReadOnlyList<MultiMemoryParticipantRequest> participants,
    IReadOnlyList<MultiMemoryTransactionalParticipantRequest>? transactionalParticipants = null);

internal IReadOnlyList<MultiMemoryTransactionalParticipantRequest> TransactionalParticipants { get; }

internal sealed record MultiMemoryTransactionalParticipantRequest
{
    internal MultiMemoryTransactionalParticipantRequest(
        MemoryAddress address, string participantIdentity, int contractVersion,
        string orderingKey, string operationId, string kind,
        IReadOnlyDictionary<string, string> payload,
        IReadOnlyDictionary<string, string> metadata);

    internal MemoryAddress Address { get; }
    internal string ParticipantIdentity { get; }
    internal int ContractVersion { get; }
    internal string OrderingKey { get; }
    internal string OperationId { get; }
    internal string Kind { get; }
    internal IReadOnlyDictionary<string, string> Payload { get; }
    internal IReadOnlyDictionary<string, string> Metadata { get; }
}
```

The existing `MultiMemoryParticipantRequest` receives this internal bridge API without changing its
B3 constructor:

```csharp
internal static MultiMemoryParticipantRequest ForTransactionalParticipant(
    string scope, MultiMemoryTransactionalParticipantRequest participant);
internal string? ParticipantIdentity { get; }
internal int? ContractVersion { get; }
internal string? OrderingKey { get; }
internal IReadOnlyDictionary<string, string>? Payload { get; }
internal IReadOnlyDictionary<string, string>? Metadata { get; }
```

For B3 instances these five properties are null; the bridge sets all five and retains the exact B4
address, operation ID, and kind. `CreateAsync` is called only for a bridge whose five B4 properties are
non-null; the existing B3 delegate is called only for instances produced by the original constructor.

`TransactionalParticipants` is never null (a null constructor argument becomes an empty,
read-only array), and existing B3 callers therefore retain identical behavior. The item constructor
copies both dictionaries into ordinal, read-only dictionaries, rejects null keys/values, requires
`Address` must be valid and persistent; `ParticipantIdentity` must be nonblank and at most 512 UTF-16 code units, requires
`ContractVersion == 1`, requires nonblank `OperationId` and `Kind` (each at most 512 UTF-16 code
units), and permits at most 64 payload keys and 64 metadata keys. Keys are nonblank,
ordinal-distinct, and at most 128 characters; values are nonblank and at most 4096 characters for
payload and 512 characters for metadata. The request rejects null items, duplicate identities
(ordinal), more than 4096 combined B3 and B4 participants, and duplicate global
`OrderingKey` values and complete `ParticipantOrderKey` collisions. B4 identity is the registry
selection key; no display name or manifest
field is used as a substitute. The coordinator performs the one combined sort at the Core admission
boundary; callers are not required to pre-sort B4 items and no MCP or CLI facade is the validation
location.

`FileMemoryStore.ExecuteMultiMemoryAsync` passes the unchanged existing B3 admission delegate to
`MultiMemoryTransactionCoordinator.ExecuteAsync`. Under the one existing
`WithExclusiveScopeAdmissionAsync` lock, the coordinator builds one combined ordered sequence using
`ParticipantOrderKey`. For each item it calls exactly one resolver: the existing delegate for B3
items and the injected factory's `CreateAsync` for B4 items, using the exact internal adapter bridge
`MultiMemoryParticipantRequest.ForTransactionalParticipant(string scope,
MultiMemoryTransactionalParticipantRequest participant)`. That bridge has the participant's `Address`,
`OperationId`, `Kind`, `ParticipantIdentity`, `ContractVersion`, `OrderingKey`, `Payload`, and
`Metadata`; the B3
resolver rejects a transactional bridge, so it cannot accidentally admit a B4 item. The coordinator
invokes the factory through the same supplied admission-delegate composition (the factory branch is
selected only when `ParticipantIdentity` is present), never through a second public entry point. The
factory performs ordinal identity plus exact version and ordering-key lookup in its sorted registry
and returns the adapter only when the adapter's scope, identity, ordering key, operation ID, and kind
match the bridge. B3 and B4
adapters are merged and sorted by the single `ParticipantOrderKey` rule below, then the combined list
is checked for duplicate adapter identities and the single 4096 bound before any prepare callback.
No new MCP or CLI command, second delegate,
second lock, store scan, or out-of-lock plugin admission is added.

## Durable B3 extension and codec rules

Extend `MultiMemoryTransactionRecord` with this final constructor parameter and property:

```csharp
IReadOnlyList<B4ParticipantRecoveryState>? transactionalParticipants = null;
public IReadOnlyList<B4ParticipantRecoveryState>? TransactionalParticipants { get; }
public IReadOnlyList<B4ParticipantRequestMetadata>? RejectedTransactionalParticipants { get; }
```

The complete constructor remains:

```csharp
public MultiMemoryTransactionRecord(
    string format, int version, string transactionId, string scope,
    MultiMemoryTransactionState state,
    IReadOnlyList<MultiMemoryParticipantManifest> participants,
    DateTimeOffset createdAt, DateTimeOffset updatedAt,
    IReadOnlyList<MultiMemoryParticipantCompletion>? completedParticipants = null,
    IReadOnlyList<B4ParticipantRecoveryState>? transactionalParticipants = null,
    IReadOnlyList<B4ParticipantRequestMetadata>? rejectedTransactionalParticipants = null);
```

The constructor copies the list, rejects null elements, and accepts null as “no B4 participants”.
The DTO is the complete persisted B4 lifecycle snapshot: identity/version, transaction and
operation correlation, handle/evidence, retry counters, lifecycle state, and quarantine state are
rewritten together whenever one of those values changes. `ParticipantIdentity` maps directly to
the factory's `participantIdentity` parameter and `ContractVersion` maps directly to
`contractVersion`; no DTO field is reconstructed from a display name or manifest kind.
Add the internal immutable DTO:

```csharp
internal sealed record B4ParticipantRecoveryState(
    string TransactionId, string OperationId, string Scope, string Address,
    string ParticipantIdentity, int ContractVersion, string OrderingKey, string Kind,
    IReadOnlyList<string> Capabilities,
    IReadOnlyDictionary<string, string> Payload,
    IReadOnlyDictionary<string, string> Metadata,
    TransactionalParticipantLifecycleState LifecycleState,
    string? PreparedHandleReference, string? PreparedHandleVersion,
    int RetryAttempt, long RetryElapsedMilliseconds, bool StateMayHaveChanged, bool Ambiguous,
    TransactionalFailureCode? LastFailureCode,
    string? EvidenceReference, string? EvidenceKind, string? EvidenceVersion,
    TransactionalQuarantineState QuarantineState, string? QuarantineReason);

internal sealed record B4ParticipantRehydrationRequest(
    B4ParticipantRecoveryState State, MultiMemoryParticipantManifest Manifest,
    IReadOnlyList<string> Capabilities);

internal sealed record B4ParticipantRequestMetadata(
    string TransactionId, string Scope, string Address, string ParticipantIdentity,
    int ContractVersion, string OrderingKey, string OperationId, string Kind,
    TransactionalFailureCode RejectionCode, string RejectionDetail);
```

`B4ParticipantRecoveryState` is the persisted and rehydration DTO. It carries transaction ID,
operation ID, scope, address, participant identity/version, participant-declared ordering key, kind, persisted capability metadata,
copied payload/metadata, lifecycle,
prepared opaque handle reference/version, retry attempt/elapsed metadata, failure/evidence, and
quarantine. Payload and metadata are never reconstructed from the manifest. Handle references are
safe opaque registry tokens, not serialized handles or object data. Handle reference/version are both
null before prepare or both nonblank after prepare. Evidence reference/kind/version are all null or
all present and use the existing safe-relative-reference predicate. Quarantine is `None` or
`Quarantined`; the latter requires a safe reason and the former requires null. Constructors and
`ParseAndValidate` reject inconsistent lifecycle, handle, failure, evidence, capability, ordering key, bounds,
correlation, enum, or version values and copy both dictionaries. No compatibility alias is introduced.

`RejectedTransactionalParticipants` is the durable non-executable request projection. Its JSON name
is `rejectedTransactionalParticipants`; members are `transactionId`, `scope`, `address`,
`participantIdentity`, `contractVersion`, `orderingKey`, `operationId`, `kind`, `rejectionCode`, and
`rejectionDetail`. The list is at most 4096 entries, sorted by `ParticipantOrderKey` with unique
complete tuples; strings use the existing 512 UTF-16-code-unit bound, addresses are persistent safe
addresses, keys are canonical, and `ContractVersion` is `1`. It contains no payload, metadata,
handle, or callback data. Admission publishes it before returning a rejected aggregate and every
record update preserves it. Recovery unions B3 manifests, admitted B4 states, and this list to
reconstruct the requested sequence and ordinals, but never sends these records to an adapter or
plugin callback.
`B4ParticipantRecoveryState` also persists `stateMayHaveChanged` and `ambiguous`; parsing rejects
contradictory boolean/lifecycle combinations. Contract tests cover their exact values for all seven
retry terminal rows and their source-generated JSON round trip.

`Capabilities` is exactly `IReadOnlyList<string>` in both DTOs and is copied into an ordinal,
read-only list. Each value is trimmed once at construction, must be nonblank after trimming, and
must be at most 64 UTF-16 code units; the canonical list is ordinal-sorted and ordinal-distinct
with `1..32` entries. A persisted list that is not already in this canonical form is malformed,
not silently rewritten during parse. `B4ParticipantRehydrationRequest.Capabilities` is an independent
canonical copy with ordinal-equal contents to `State.Capabilities`; the coordinator creates it from
the parsed state and never accepts a caller-supplied replacement. This is persisted capability
metadata, not a display label or a capability requested during recovery. The
`TransactionalParticipantDescriptor` constructor applies the same canonicalization and stores an
immutable copy, so the registry comparison has one definition on both sides of the boundary.

The exact registry rehydration contract is:

```csharp
internal Task<IMultiMemoryParticipantAdapter> RehydrateAsync(
    B4ParticipantRehydrationRequest request,
    CancellationToken cancellationToken);
```

The registry verifies `request.State` scope, transaction/operation correlation, persistent address,
and `request.Manifest` correlation, plus ordinal identity and exact contract version before lookup.
It canonicalizes the currently registered descriptor's `Capabilities` with the same trim,
ordinal-sort, and ordinal-distinct rules, then requires an exact ordinal sequence match with
`request.Capabilities` and `request.State.Capabilities`. A missing, malformed, or mismatched
capability set is a typed recovery mismatch. This comparison occurs before adapter construction,
status lookup, or any plugin callback. Unknown identity/version, duplicate registry identity,
missing or invalid handle reference/version, invalid evidence, or invalid payload/metadata returns a
typed recovery mismatch before plugin code runs. For a valid state it creates
`TransactionalPreparedHandle(request.State.PreparedHandleReference!, request.State.PreparedHandleVersion!)`, parses
the address, and reconstructs `TransactionalCommitContext(request.State.TransactionId, request.State.Scope, address,
request.State.OperationId, request.State.Kind, handle)` and the equivalent `TransactionalRollbackContext`. The host
adapter retains those contexts and uses the commit context for status/commit/verification and the
rollback context for rollback. Quarantined state is never invoked. Every mismatch or registry
exception preserves record/evidence, marks the scope `RecoveryRequired`/`Quarantined`, and does not
select a fallback participant, delete evidence, or call plugin recovery.

The JSON property names are exactly `transactionalParticipants`, `rejectedTransactionalParticipants`, `transactionId`, `operationId`,
`scope`, `address`, `participantIdentity`, `contractVersion`, `orderingKey`, `kind`, `capabilities`, `payload`, `metadata`,
`lifecycleState`, `preparedHandleReference`, `preparedHandleVersion`, `retryAttempt`,
`retryElapsedMilliseconds`, `stateMayHaveChanged`, `ambiguous`, `lastFailureCode`, `evidenceReference`, `evidenceKind`,
`evidenceVersion`, `quarantineState`, and `quarantineReason`. Nullable references are omitted only
when null under the existing null-ignore policy. `TransactionalParticipantLifecycleState` is exactly `Prepared`,
`Committed`, `RolledBack`, `Changed`, `Ambiguous`; `TransactionalQuarantineState` is exactly
`None`, `Quarantined`. Their converters write lower camel strings and reject unknown strings.

Register `MultiMemoryTransactionRecord`, `B4ParticipantRecoveryState[]`, `B4ParticipantRequestMetadata[]`, `string[]`,
`Dictionary<string, string>`, both enums, and any converter roots in
`MultiMemoryTransactionJsonContext`; do not create a second context or use reflection. Add
`WithState` and `WithCompletionProof` construct a new record. Add these immutable update APIs;
each returns a new record and never mutates the input:

```csharp
internal static MultiMemoryTransactionRecord WithTransactionalParticipant(
    MultiMemoryTransactionRecord record,
    B4ParticipantRecoveryState participant,
    DateTimeOffset updatedAt);
internal static MultiMemoryTransactionRecord WithTransactionalParticipants(
    MultiMemoryTransactionRecord record,
    IReadOnlyList<B4ParticipantRecoveryState> participants,
    DateTimeOffset updatedAt);
```

`TransactionalParticipantDescriptor` is request-neutral: operation ID and kind belong to each
`MultiMemoryTransactionalParticipantRequest` and to its prepare/commit/rollback contexts, not to
the descriptor. Every descriptor construction, fixture, registry registration, and assertion uses
the five-argument signature shown above; no descriptor `OperationId` or `Kind` property exists.

`WithState` and `WithCompletionProof` always preserve the complete B4 participant snapshot. Their
implementations must use these complete constructor calls, including the final
`record.TransactionalParticipants` argument, never a constructor overload that defaults that
argument to null:

```csharp
internal static MultiMemoryTransactionRecord WithState(
    MultiMemoryTransactionRecord record, MultiMemoryTransactionState state,
    DateTimeOffset updatedAt)
    => new(
        record.Format, record.Version, record.TransactionId, record.Scope, state,
        record.Participants, record.CreatedAt, updatedAt, record.CompletedParticipants,
        record.TransactionalParticipants, record.RejectedTransactionalParticipants);

internal static MultiMemoryTransactionRecord WithCompletionProof(
    MultiMemoryTransactionRecord record,
    IReadOnlyList<MultiMemoryParticipantCompletion> completions,
    DateTimeOffset updatedAt)
    => new(
        record.Format, record.Version, record.TransactionId, record.Scope, record.State,
        record.Participants, record.CreatedAt, updatedAt, completions,
        record.TransactionalParticipants, record.RejectedTransactionalParticipants);
```

Both helpers still copy and validate their inputs through the record constructor. Lifecycle code
must call `WithTransactionalParticipant(s)` after every retry attempt change, prepare result,
commit decision, commit result, rollback result, or quarantine change; those updates also retain
the other B4 DTOs. Regression tests must exercise B4 preservation through every B3 transition:
initial admission, prepare, committed decision, post-decision completion, pre-decision rollback,
and recovery/quarantine, using both helpers where each B3 transition uses them.

The codec APIs include the existing methods plus the two named B4 update/publication methods:

```csharp
internal static byte[] Serialize(MultiMemoryTransactionRecord record);
internal static MultiMemoryTransactionRecord ParseAndValidate(
    ReadOnlySpan<byte> json, string expectedScope, string recordPath,
    string? expectedStoreDirectory = null);
internal static MultiMemoryTransactionRecord WithState(
    MultiMemoryTransactionRecord record, MultiMemoryTransactionState state,
    DateTimeOffset updatedAt);
internal static MultiMemoryTransactionRecord WithCompletionProof(
    MultiMemoryTransactionRecord record,
    IReadOnlyList<MultiMemoryParticipantCompletion> completions,
    DateTimeOffset updatedAt);
internal static MultiMemoryTransactionRecord Read(string scopeDirectory, string expectedScope);
internal static void Write(string scopeDirectory, MultiMemoryTransactionRecord record);
internal static void WriteExpected(string scopeDirectory,
    MultiMemoryTransactionRecord expected, MultiMemoryTransactionRecord replacement);
internal static void Delete(string scopeDirectory);
internal static string GetPath(string scopeDirectory);
```

`WriteExpected` is the lifecycle publication API: under B3's already-held scope lock, it rereads
and validates the current record, then compares the current record with `expected` by exact
canonical source-generated JSON bytes. The comparison covers every persisted identity and content
field, including format/version, transaction and scope, state, timestamps, ordered B3 participants,
completion proof, and the full ordered B4 DTO snapshot; `UpdatedAt` is not a concurrency token and
is not compared on its own. Only after an exact match does it serialize the validated immutable
replacement and perform one same-directory atomic replacement through
`DurableFile.AtomicWriteAllBytes`. A mismatch fails with `Inconsistent` and retains evidence.
There is no last-writer-wins update, partial DTO write, or unlocked lifecycle write. The coordinator
performs one update/publication per logical transition:
prepare result and retry counters are written before the prepare decision; the durable B3 commit
decision is published with the current B4 DTO snapshot; each commit/verification or rollback result
updates that participant's DTO; and quarantine is published in the same immutable update as the
failure classification. Only an atomic write known not to have started may be retried. An uncertain
write result is `RecoveryRequired`, never a competing second record.

The coordinator owns one private lifecycle write funnel with this exact shape:

```csharp
private Task<MultiMemoryTransactionRecord> UpdateParticipantLifecycleAsync(
    string scopeDirectory,
    MultiMemoryTransactionRecord expected,
    B4ParticipantRecoveryState participant,
    CancellationToken cancellationToken);
```

It validates that the DTO belongs to `expected.TransactionId`, replaces exactly one matching
`ParticipantIdentity` through `WithTransactionalParticipant`, and calls `WriteExpected`; callers
must use this funnel for prepare-attempt accounting, prepare completion, the committed-decision
snapshot, commit/verification completion, rollback completion, and quarantine. The method returns
the published replacement so the next transition's expected record is the value just written.
`UpdateParticipantLifecycleAsync` runs only under `ExecuteUnderLockAsync` or
`RecoverUnderLockAsync`, never invokes plugin code, and maps an exact-content comparison mismatch or uncertain
write to retained `RecoveryRequired` evidence. This makes every durable lifecycle update atomic,
ordered, and concurrency-checked by exact persisted identity/content rather than by a potentially
colliding timestamp. `UpdatedAt` remains audit metadata and is updated for each replacement, but it
does not establish freshness.

## Coordinator recovery algorithm for B4 participants

Add this recovery task to the existing `MultiMemoryTransactionCoordinator`, not to the existing
discovery/parser boundary in `src/Scrinia.Core/RecoveryDiscovery.cs`. That boundary remains
discovery-only and does not own Core transaction lifecycle behavior. The coordinator receives the
already-created Core factory through this constructor and stores it as `_participantFactory`:

```csharp
private readonly IMultiMemoryParticipantAdapterFactory? _participantFactory;

internal MultiMemoryTransactionCoordinator(
    FileMemoryStore store,
    IMultiMemoryParticipantAdapterFactory? participantFactory,
    TimeSpan? lockTimeout = null)
    : this(store, lockTimeout)
{
    _participantFactory = participantFactory;
}
```

The existing delegate constructors remain unchanged for B3-only callers. `FileMemoryStore` passes
the factory to this overload when the workspace registry is enabled; it never calls the recovery
method directly. The exact invocation from the existing locked path is:

```csharp
// MultiMemoryTransactionCoordinator.RecoverUnderLockAsync, after B3 reconstruction.
if (record.TransactionalParticipants is { Count: > 0 }
    || record.RejectedTransactionalParticipants is { Count: > 0 })
{
    if (record.TransactionalParticipants is { Count: > 0 } && _participantFactory is null)
        return RecoveryRequired(record, b3Adapters, MultiMemoryFailure.Ambiguous,
            "Transactional participant recovery is unavailable; evidence is retained.", directory);

    return await RecoverTransactionalParticipantsAsync(
        record, b3Adapters, directory, _participantFactory, cancellationToken)
        .ConfigureAwait(false);
}
```

The method is a private coordinator method. No call to a private method on another type is required;
the only refactor is moving the proposed recovery body out of the discovery-type location and into
the owner that already controls `RecoverUnderLockAsync`, `AbortPreparedAsync`, `FinishDecisionAsync`,
and `UpdateParticipantLifecycleAsync`.

```csharp
private Task<MultiMemoryAggregateResult> RecoverTransactionalParticipantsAsync(
    MultiMemoryTransactionRecord record,
    IReadOnlyList<IMultiMemoryParticipantAdapter> b3Adapters,
    string scopeDirectory,
    IMultiMemoryParticipantAdapterFactory? participantFactory,
    CancellationToken cancellationToken);
```

The nullable factory parameter is intentional: a record containing only
`RejectedTransactionalParticipants` is recoverable through Core projection and must be expressible
without constructing or passing a registry. The method requires `participantFactory is not null`
only when `record.TransactionalParticipants` is non-empty; with admitted states absent, it performs
no adapter lookup and projects the rejected metadata directly. The factory-free call is therefore
mechanically valid:

```csharp
await RecoverTransactionalParticipantsAsync(
    record, b3Adapters, directory, participantFactory: null,
    cancellationToken: cancellationToken)
    .ConfigureAwait(false);
```

`RecoverUnderLockAsync` always calls this one coordinator-owned method with `_participantFactory`;
it does not branch to the discovery/parser boundary. Tests must instantiate
`new MultiMemoryTransactionCoordinator(store, participantFactory: null)` (or use the retained
B3-only constructor) for rejected-only recovery and assert that the factory, `CreateAsync`, and
`RehydrateAsync` are never invoked. The named regression test is
`TransactionalParticipantRecoveryTests.RejectedOnlyRecoveryBuildsProjectionWithoutFactoryOrAdapterInvocation`.
A separate mixed-record test passes a non-null factory and asserts that only admitted states call
`RehydrateAsync` while rejected metadata remains direct projection data; its name is
`TransactionalParticipantRecoveryTests.MixedAdmittedAndRejectedRecoveryPreservesBothMetadataLists`.

The method receives the already-read record and already-held scope lock. It does not rediscover
the file, acquire a second lock, or admit through `ExecuteAsync`. It must:

1. Read both `record.TransactionalParticipants ?? Array.Empty<B4ParticipantRecoveryState>()` and
    `record.RejectedTransactionalParticipants ?? Array.Empty<B4ParticipantRequestMetadata>()`, make no
    mutation to either persisted list, and validate each admitted B4 state and rejected metadata item
    against the global
    `ParticipantOrderKey` projection (`OrderingKey`, class `1`, `ParticipantIdentity`, `OperationId`,
    `Kind`) using
   `StringComparer.Ordinal`. For each state create
   `new MultiMemoryParticipantManifest(state.Address, state.OperationId, state.Kind)` and
   `new B4ParticipantRehydrationRequest(state, manifest, state.Capabilities)`. Identity,
   capabilities, payload, metadata, and operation data are never inferred from another field.
2. Convert every rejected metadata item directly into a non-admitted `Rejected` outcome for the
    aggregate projection. This is Core-only projection data: do not call `CreateAsync`,
    `RehydrateAsync`, or any adapter/plugin callback for it. A record containing only rejected metadata
    therefore still reconstructs the complete requested sequence, ordinal union, and aggregate result.
    For mixed records, only admitted states are rehydrated and rejected outcomes are merged into the
    same sorted projection; both persisted lists remain present through every `WriteExpected` update.
3. When admitted states exist, require the non-null factory and call
   `IMultiMemoryParticipantAdapterFactory.RehydrateAsync(B4ParticipantRehydrationRequest,
   CancellationToken)` once per admitted state in that order. When only rejected metadata exists,
   skip factory resolution entirely. The registry compares persisted capabilities
   with the current descriptor before adapter construction or plugin code. Unknown identity,
   duplicate registration, unsupported version, malformed/mismatched capabilities, bad
   correlation, or quarantined state creates retained `RecoveryRequired`/`Quarantined` evidence
   without a callback.
4. Merge successfully rehydrated B4 adapters with the B3 adapters and sort the one recovery
   lifecycle list by the global `ParticipantOrderKey` below. Do not call the B3 resolver again,
   admit duplicate identities, or acquire a second lock. The combined list is the sole recovery
   lifecycle list.
5. For a record with no committed decision (`Prepared`), execute prepared-abort by calling
   `RollbackAsync` on the combined list in reverse deterministic order. For a committed record,
   execute committed roll-forward through `GetCommitStatusAsync`, then `CommitAsync` and the
   existing verification path in deterministic forward order. Never roll back after commitment.
6. After every retry-accounting change, status result, rollback/commit result, verification result,
   mismatch, or quarantine, call
   `UpdateParticipantLifecycleAsync(scopeDirectory, expected, updatedParticipant,
   cancellationToken)` and use its returned record as the next exact expected value. Each write
   preserves the complete ordered B3 manifest list, complete ordered B4 DTO list, prior evidence,
   and aggregate state.
7. Return the existing internal `MultiMemoryAggregateResult` with separate B3, admitted B4, and
   non-admitted B4 outcome lists while preserving the existing aggregate classification. This is
   not exposed through MCP or CLI. A B4 failure or quarantine appends evidence and escalates
   only the affected scope to `RecoveryRequired`; it never erases unrelated B3 failures. Each B4
   outcome includes identity, operation ID, ordinal position, lifecycle action, final state,
   quarantine status/reason, and evidence reference, including non-admitted participants.

Named tests are `TransactionalParticipantRecoveryTests.PreparedRecoveryRollsBackB4InDeterministicOrder`,
`CommittedRecoveryRollsForwardB4InDeterministicOrder`,
`MixedB3AndB4RecoveryPreservesOrdinalOrderingAndSingleAdmission`,
`RejectedOnlyRecoveryBuildsProjectionWithoutFactoryOrAdapterInvocation`,
`MixedAdmittedAndRejectedRecoveryPreservesBothMetadataLists`,
`UnknownOrMismatchedB4IsQuarantinedWithoutCallback`, and
`RecoveryAggregatePreservesB3AndB4Evidence`. They assert exact-content `WriteExpected` for every
transition, no stale overwrite, no callback on mismatch, and preservation of unrelated B3 outcomes.

Use one global deterministic ordering rule for admission, prepare, rollback, commit, recovery, and
aggregate projection. Define an internal `ParticipantOrderKey` with
`OrderingKey`, `ParticipantClass`, `CanonicalIdentity`, `OperationId`, and `Kind`; for B3,
`OrderingKey` is null, `CanonicalIdentity` is the resolved subject, and `ParticipantClass` is `0`;
for B4, `OrderingKey` is the participant-declared key, `CanonicalIdentity` is `ParticipantIdentity`,
and `ParticipantClass` is `1`. Compare exactly that tuple in that order, using null-before-non-null
and `StringComparer.Ordinal` for strings and ascending integer comparison for `ParticipantClass`.
The null B3 key makes **B3-before-B4 intentional**, not an accidental partition or a fallback to
list order: all B3 tuples precede all B4 tuples, then the remaining tuple fields order participants
within each class.
The aggregate lifecycle order
is the ascending combined B3+B4 sequence; prepared rollback traverses it in reverse and committed
roll-forward traverses it forward. A B3/B4 key collision is rejected as inconsistent rather than
resolved by list position. B4 input and persisted DTO lists are normalized to this rule at their
boundary; the resulting B3-then-B4 order is intentional. Duplicate B4 ordering keys
are rejected within the registry/request and on rehydration before callbacks; a complete tuple
collision is `Inconsistent`, never list-order tie-breaking.

The coordinator assigns one zero-based global `Ordinal` over the complete sorted requested sequence,
including B3, admitted B4, and non-admitted B4 outcomes. For N requested participants, ordinals are
exactly contiguous `0..N-1`, unique, and never renumbered when a B4 request is rejected. Each of the
three aggregate lists is ascending by the same global ordinal; individual projections may have gaps,
but their union must contain every ordinal exactly once. The Core admission/projection builder assigns
the values, and the aggregate constructor is the final validation boundary. A duplicate, missing,
negative, out-of-range, or out-of-order ordinal, wrong admitted flag, or outcome not in the requested
sequence is malformed: throw the typed `Inconsistent` aggregate-contract error before facade mapping,
retain safe evidence, and do not publish success. Tests cover empty, all-B3, all-admitted-B4, mixed,
every permutation, and each malformed projection. The admission bridge, `RecoverTransactionalParticipantsAsync`, `AbortPreparedAsync`,
`FinishDecisionAsync`, and all ordering tests use the same `ParticipantOrderKey` comparer.

The combined participant bound is 4096 entries across the existing B3 `Participants` manifest and
the B4 `TransactionalParticipants` list; it is not 4096 per list. Collections contain no null
elements and B3 contains at least one participant; B4 may contain zero. Every identity,
transaction ID, scope, operation ID, kind, handle reference, evidence reference, evidence kind,
evidence version, and safe-evidence string is nonblank and at most 512 UTF-16 code units.
Capabilities are `1..32` entries, each trimmed, nonblank, at most 64 UTF-16 code units, and
persisted in ordinal-sorted, ordinal-distinct canonical order. `RetryAttempt` is
`0..8`, `RetryElapsedMilliseconds` is `0..300000`, and the serialized document is at most 1 MiB.
Public DTO/context constructors validate shape and string bounds; `MultiMemoryTransactionRecordCodec.ParseAndValidate`
revalidates the combined count, ordering, correlation, enum/version, and evidence rules after JSON
deserialization; coordinator admission repeats only live-registry/quarantine checks before
callbacks. No facade is the sole validation location.
Unknown fields, format/version values other than the existing format and version 1, unknown enum
values, duplicate identities, mismatched transaction IDs, invalid evidence, unsupported contract
versions, malformed capability lists or capability mismatches, and out-of-order DTOs throw
`MultiMemoryTransactionRecordException` with
`Malformed`, `UnsupportedVersion`, `Unsafe`, or `Inconsistent` as appropriate. The record remains
on disk and only its scope becomes `RecoveryRequired`; no unknown data is discarded.

## Aggregate result shape and mapper/test updates

Do not overload the existing `Participants` list. It remains the B3 outcome list for compatibility
with the existing internal mappers and tests. Change `MultiMemoryAggregateResult` in
`src/Scrinia.Core/MultiMemoryTransaction.cs` to this explicit constructor and property set:

```csharp
private MultiMemoryAggregateResult(
    string transactionId,
    string scope,
    MultiMemoryOutcome outcome,
    IReadOnlyList<MultiMemoryParticipantRequest> expectedParticipants,
    ValidatedMultiMemoryAggregateProjection projection,
    MultiMemoryFailure? failure,
    string? warning,
    string? evidencePath);

internal static MultiMemoryAggregateResult Create(
    string transactionId,
    string scope,
    MultiMemoryOutcome outcome,
    IReadOnlyList<MultiMemoryParticipantRequest> expectedParticipants,
    MultiMemoryAggregateProjectionInput input,
    MultiMemoryFailure? failure,
    string? warning,
    string? evidencePath);

internal IReadOnlyList<MultiMemoryParticipantOutcome> Participants { get; } // B3 only
internal IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> TransactionalParticipants { get; }
internal IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> NonAdmittedParticipants { get; }
```

`MultiMemoryParticipantOutcome` (the existing B3 outcome type) gains the same internal, zero-based
`Ordinal` property; its existing facade shape is unchanged. `MultiMemoryTransactionalParticipantOutcome`
is an internal explicit validating class with constructor
`(string? orderingKey, string participantIdentity, string operationId, string kind, int ordinal,
TransactionalLifecycleAction action, TransactionalOutcomeState state, bool admitted,
 bool quarantined, string? quarantineReason, string? evidencePath)`. B4 outcomes require a canonical
non-null `orderingKey`; B3 outcomes use null. Identity, operation ID, and kind
are nonblank and at most 512 UTF-16 code units; `ordinal` is nonnegative; `admitted` must be true
for `TransactionalParticipants` and false for `NonAdmittedParticipants`; quarantined outcomes
require a safe quarantine reason; and evidence paths use `IsSafeEvidencePath`. The class exposes the
same names as read-only properties. `TransactionalLifecycleAction` is `Prepare`, `Commit`,
`Rollback`, or `Recovery`; `TransactionalOutcomeState` is `Committed`, `RolledBack`, `Changed`,
`Ambiguous`, `Rejected`, or `Quarantined`.

Core must first call the authoritative builder with the requested participant sequence and
`MultiMemoryAggregateProjectionInput`:

```csharp
internal sealed record MultiMemoryAggregateProjectionInput(
    IReadOnlyList<MultiMemoryParticipantOutcome> B3Outcomes,
    IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> AdmittedB4Outcomes,
    IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> NonAdmittedB4Outcomes);

private sealed class ValidatedMultiMemoryAggregateProjection
{
    private ValidatedMultiMemoryAggregateProjection(
        IReadOnlyList<MultiMemoryParticipantOutcome> participants,
        IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> transactionalParticipants,
        IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> nonAdmittedParticipants,
        IReadOnlyList<ParticipantOrderKey> globalOrdinalUnion);
    private IReadOnlyList<MultiMemoryParticipantOutcome> Participants { get; }
    private IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> TransactionalParticipants { get; }
    private IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> NonAdmittedParticipants { get; }
    private IReadOnlyList<ParticipantOrderKey> GlobalOrdinalUnion { get; }
}

private static ValidatedMultiMemoryAggregateProjection BuildAggregateProjection(
    IReadOnlyList<MultiMemoryParticipantRequest> requestedParticipants,
    MultiMemoryAggregateProjectionInput input);

private MultiMemoryAggregateResult(
    string transactionId, string scope, MultiMemoryOutcome outcome,
    IReadOnlyList<MultiMemoryParticipantRequest> expectedParticipants,
    ValidatedMultiMemoryAggregateProjection projection,
    MultiMemoryFailure? failure, string? warning, string? evidencePath);

internal static MultiMemoryAggregateResult Create(
    string transactionId, string scope, MultiMemoryOutcome outcome,
    IReadOnlyList<MultiMemoryParticipantRequest> expectedParticipants,
    MultiMemoryAggregateProjectionInput input,
    MultiMemoryFailure? failure, string? warning, string? evidencePath);
```

The builder sorts the requested sequence by the exact tuple comparator, assigns contiguous ordinals,
and validates each outcome against the requested tuple. The validating aggregate constructor receives
the expected requested sequence and this builder-produced projection, never three caller-owned lists.
`ValidatedMultiMemoryAggregateProjection` is a private nested sealed class with a private constructor
and private read-only fields. The private builder is its only minting site; the internal `Create` factory
is the only production aggregate entry point and passes it to the private aggregate constructor. There
is no public/internal `MultiMemoryAggregateProjection`, token factory, or overload that a caller or test
can forge. It rechecks token ownership, list membership,
strict per-list ordering, class/admitted flags, and the complete union. Missing, extra, mismatched,
duplicate, gapped, negative, out-of-range, or out-of-order outcomes throw typed `Inconsistent` before
facade mapping. Tests are concrete: missing B3, extra B4, mismatched identity/operation/kind,
duplicate ordinal, gap, out-of-order list, wrong class, wrong admitted flag, every permutation, and
forged/stale token. Every non-admitted outcome must have `action == Recovery` or `Prepare` with state
`Rejected`.
`RecoveryRequired` still requires the existing safe aggregate evidence path. All existing B3 call
sites are updated to pass `Array.Empty<MultiMemoryTransactionalParticipantOutcome>()` for both new
lists; recovery/admission paths populate the appropriate list, including every rejected or
quarantined B4 request that never receives an adapter.

The exact internal JSON names, if a diagnostic/test serializer is used, are `transactionId`,
`scope`, `outcome`, `participants`, `transactionalParticipants`, `nonAdmittedParticipants`,
`failure`, `warning`, and `evidencePath`; outcome members use `orderingKey`, `participantIdentity`, `operationId`,
`kind`, `ordinal`, `action`, `state`, `admitted`, `quarantined`, `quarantineReason`, and
`evidencePath`. These names are not registered in `CliJsonContext`, `ScriniaMcpJsonContext`, or
any MCP/CLI response: `MemoryTools.Core.cs:24-94` and `ScriniaCommands.Memory.cs` continue to map
only the existing B3 `Participants` summaries. Add assertions in
`MultiMemoryTransactionFacadeTests` and `CliMultiMemoryTransactionTests` proving that the new
internal lists do not appear in MCP, CLI, or JSON response output. Add contract tests for null,
duplicate/missing/gapped/out-of-order ordinals, admitted-list mismatches, wrong B3/B4 class,
unrequested outcomes, unsafe evidence/reason, and every
non-admitted state/action combination.

## Stable-detail helper and crossing tests

Create `src/Scrinia.Core/MultiMemoryStableDetail.cs` with exactly:

```csharp
internal static class MultiMemoryStableDetail
{
    internal static string? Sanitize(string? detail);
    internal static bool IsStableDetailCharacter(char character);
}
```

`Sanitize` returns null for null/blank, length over 240, any character other than a Unicode letter,
Unicode digit, ASCII space, or `.,:;_()-`, rooted/path/URI-like text, leading `~`, `//`, `/`, `\`,
or text containing `System.`, `Exception`, `payload`, `sidecar`, or `secret` ordinal-insensitively.
It returns the original string otherwise; it never redacts into a replacement value.

Named tests must cover every crossing: participant→host (`TransactionalParticipantAdapterTests`),
host→Core (`TransactionalParticipantAdapterTests`), Core→record
(`MultiMemoryTransactionContractTests`), record→recovery (`TransactionalParticipantRecoveryTests`),
Core→MCP (`TransactionalObserverBoundaryTests`), and Core→CLI (`CliMultiMemoryTransactionTests`).
Each named suite covers CR, LF, tab, vertical tab, NEL, no-break space, figure space, other control
and non-printing whitespace, rooted/traversal/URI-like values, foreign paths, exception text, and
raw payloads. `MemoryTools.Core.cs` and `ScriniaCommands.Memory.cs` call only `Sanitize`.
Preserve the existing evidence-file filter separately as
`internal static string? SafeMultiMemoryEvidenceFilename(string? evidencePath)`: null/blank,
rooted, backslash, and colon-containing values return null; otherwise return
`Path.GetFileName(evidencePath)`. `TransactionalObserverBoundaryTests` and
`CliMultiMemoryTransactionTests` must cover those four rejected forms and a safe nested relative
path.

The Vulkan boundary is tested explicitly in
`tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingsPluginObserverBoundaryTests.cs`: Vulkan is loaded
only by the standalone `Scrinia.Plugin.Embeddings.Cli` child process through
`WorkspaceSetup.TryLoadVulkanPluginAsync`/`McpPluginHost.StartAsync` over MCP stdio
(`src/Scrinia/Commands/WorkspaceSetup.cs:651-666`, `docs/architecture/embeddings.md:17-23`).
There is no in-process registration path for `VulkanEmbeddingProvider`; the test proves that this
child process installs only observer/search capabilities and never an
`ITransactionalMemoryParticipant` or B4 adapter. An attempted child-process registration is
rejected as unsupported, not treated as a trusted in-process participant.

`MultiMemoryTransactionAdmissionTests` additionally cover an empty/null extension, null item,
invalid or ephemeral address, blank/oversized identity, non-1 contract version, null/oversized
payload or metadata, unsorted identities, ordinal duplicate identities, combined B3+B4 count 4097,
non-canonical/oversized ordering keys, duplicate ordering keys, a B4 ordering key changed between
request and descriptor, and deterministic registry selection when registrations differ only by
identity/version. `TransactionalParticipantRegistryTests` cover descriptor ordering-key uniqueness
and ordinal equality. `MultiMemoryTransactionLifecycleTests` and
`TransactionalParticipantRecoveryTests` cover the exact global tuple comparator across mixed B3/B4
participants, persisted `orderingKey` round trips, rehydration rejection of missing/changed/duplicate
keys, aggregate ordering keys and ordinals, and complete-tuple collisions before callbacks.
`TransactionalParticipantRegistryTests` cover exact payload/metadata propagation and
`RehydrateAsync` reconstruction of both contexts, including valid opaque handle/version pairs and
each fail-closed mismatch. `TransactionalParticipantRecoveryTests` verify every immutable DTO
transition is published with `WriteExpected`, preserves the complete B4 snapshot through every B3
transition, and never allows a stale expected record or partial B4 snapshot to overwrite the record.
A dedicated collision regression creates two distinct expected records with the same `UpdatedAt`,
changes persisted state or B4 content in the on-disk record, and proves the second `WriteExpected`
is rejected with `Inconsistent`; a matching canonical record remains accepted. These tests map to
the request/admission boundary (`src/Scrinia.Core/MultiMemoryTransaction.cs:81-115`,
`src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs:67-115`) and codec publication boundary
(`src/Scrinia.Core/MultiMemoryTransactionRecord.cs:180-245`).

## Retry dependencies, classification, and no-hang contract

Retry timing is injected; production retry code never reads wall-clock time directly and never calls
an unbounded delay. Add these internal Core contracts in
`src/Scrinia.Core/TransactionalRetry.cs`:

```csharp
internal interface IMonotonicClock
{
    TimeSpan GetElapsed();
}

internal interface IWallClock
{
    DateTimeOffset UtcNow { get; }
}

internal enum RetryTerminalState
{
    Success, DeclaredTransientExhausted, CancellationBeforeCallback,
    CancellationAfterCallbackStart, RetryMonotonicClockFailed,
    RetryMonotonicClockMovedBackwards, RetryElapsedOverflow
}

internal sealed record RetryClassification(
    RetryTerminalState State, TransactionalFailureCode? FailureCode,
    bool StateMayHaveChanged, bool Ambiguous, bool Retryable,
    bool Cancelled, string? Detail);

internal interface IRetryClassifier<T>
{
    RetryClassification Classify(T result, bool callbackStarted);
    RetryClassification Classify(Exception exception, bool callbackStarted);
}

internal sealed class RetryBudget
{
    internal RetryBudget(
        TransactionalRetryPolicy policy,
        IMonotonicClock clock,
        Func<TimeSpan, CancellationToken, ValueTask> delayScheduler);

    internal Task<RetryResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        IRetryClassifier<T> classifier,
        CancellationToken cancellationToken);
}

internal sealed record RetryResult<T>
{
    internal RetryResult(T? value, int attempts, TimeSpan elapsed,
        TransactionalFailureCode? failureCode, bool stateMayHaveChanged,
        bool ambiguous, bool exhausted, bool cancelled);
    internal T? Value { get; }
    internal int Attempts { get; }
    internal TimeSpan Elapsed { get; }
    internal TransactionalFailureCode? FailureCode { get; }
    internal bool StateMayHaveChanged { get; }
    internal bool Ambiguous { get; }
    internal bool Exhausted { get; }
    internal bool Cancelled { get; }
}
```

The adapter supplies a classifier separately for each callback result type. The returned-result method
validates the complete result, extracts `Failure.Code`, `Failure.StateMayHaveChanged`, evidence, and
success state, and marks only `DeclaredTransient` retryable. The exception method extracts a code and
change flag only from a typed `TransactionalParticipantException` carrying a validated
`TransactionalParticipantFailure`; cancellation uses `callbackStarted`. Unrecognized exceptions map
to permanent when unchanged state is proven, otherwise ambiguous changed state. Exception text is
never inspected. Invalid returned combinations map to `InvalidResponse` before durable publication.
Returned failures and thrown exceptions have separate tests. `RetryBudget` turns a retryable result
that reaches either bound into `DeclaredTransientExhausted`; cancellation and clock failures produce
the exact seven terminal rows in the table, with no untyped `isSuccessful` or exception mapper left in
the API.

`RetryResult<T>` is the only retry outcome representation. `Attempts` counts callback invocations;
`Elapsed` is the last valid monotonic reading, or zero when none exists; `Value` is null for failures.
The exhaustive terminal table is:

| Terminal state | Exact fields | `TransactionalParticipantFailure` | Persisted B4 state/recovery | Aggregate/observer behavior |
|---|---|---|---|---|
| Success | null code, `StateMayHaveChanged=false`, `Ambiguous=false`, `Exhausted=false`, `Cancelled=false`, non-null value | none | proof/completion update; continue | normal result; observer failures warn/continue |
| Declared transient exhausted | code `DeclaredTransient`, `StateMayHaveChanged` from participant, `Ambiguous=false`, `Exhausted=true`, `Cancelled=false` | exact code/flag | write counters/failure; rollback before decision or commit/status after; quarantine without proof | failed/recovery-required; warning cannot alter result |
| Cancellation before callback | no new attempt, code `Cancelled`, `StateMayHaveChanged=false`, `Ambiguous=false`, `Exhausted=false`, `Cancelled=true` | `Cancelled(false)` | write cancellation; rollback already-prepared participants before decision | cancelled; observers warn/continue |
| Cancellation after callback start | attempt included, code `Cancelled`, `StateMayHaveChanged=true`, `Ambiguous=true`, `Exhausted=false`, `Cancelled=true` | `Cancelled(true)` | immediate write with evidence; conservative rollback before or verification-only after decision; quarantine without proof | recovery-required/ambiguous; observers cannot downgrade |
| Monotonic clock failure | code `RetryMonotonicClockFailed`, `StateMayHaveChanged=true`, `Ambiguous=true`, `Exhausted=false`, `Cancelled=false` | exact code/flag | one immediate write with evidence; decision-state recovery; quarantine without proof | recovery-required/ambiguous; separate observer warning |
| Negative movement | same flags, code `RetryMonotonicClockMovedBackwards` | exact code/flag | same immediate no-retry persistence/recovery | same; observers warn/continue |
| Overflow | same flags, code `RetryElapsedOverflow` | exact code/flag | same immediate no-retry persistence/recovery | same; observers warn/continue |

The constructor rejects every other combination. Tests assert all fields, exact attempt/delay counts,
callback token timing, one lifecycle write for each clock row, evidence, pre/post-decision action,
quarantine, aggregate classification, observer continuation, and no retry for clock failures.
For every row, `B4ParticipantRecoveryState.RetryAttempt == Attempts` and
`RetryElapsedMilliseconds == Elapsed` after validated conversion. Success writes `Committed` or
`RolledBack` with null failure/evidence; pre-callback cancellation and exhausted transient write the
current lifecycle and exact failure code; post-start cancellation and every clock failure write
`Changed` or `Ambiguous` with complete evidence. Each is one immutable `WriteExpected` replacement
before aggregate construction.

The adapter persists each such result immediately through `UpdateParticipantLifecycleAsync` with
the exact code and `StateMayHaveChanged = true`, retaining sanitized evidence. Before the B3 decision
it performs conservative rollback; after the decision it performs only commit/status verification.
Restart rehydrates the record and follows that same decision-state path; missing proof quarantines the
participant and leaves the scope `RecoveryRequired`. Tests assert all fields, exact attempt/delay
counts, one immutable write, evidence retention, rollback/no-rollback by decision state, quarantine,
and no retry for each clock failure.

The production constructor uses `SystemMonotonicClock : IMonotonicClock` and
`SystemWallClock : IWallClock`; persisted timestamps use only `IWallClock`. The delay remains
`(delay, token) => new(Task.Delay(delay, token))`; tests inject `FakeMonotonicClock`, `FakeWallClock`,
and a recording delay function. `RetryBudget` validates the existing policy defaults (`MaxAttempts =
3`, `MaxElapsed = 30 seconds`, `InitialDelay = 100ms`, `MaximumDelay = 2 seconds`) and uses only
`GetElapsed()`, exponential backoff capped at `MaximumDelay`, and a final attempt with no delay. The
operation receives the caller's cancellation token on every attempt. `MaxElapsed` is inclusive from
the first attempt: no new attempt or delay starts after it is exceeded, and a delay is scheduled only
when `elapsed + delay <= MaxElapsed`. Clock failure, negative movement, or overflow returns the exact
failure result above and never retries. Cancellation returns `Cancelled = true` without
scheduling another delay. The fake advances only when the test directs it, including during a
recorded delay, so all timing tests are deterministic and cannot hang.

Only `TransactionalFailureCode.DeclaredTransient` is retryable. The typed classifier overloads classify a participant
exception as declared transient only when it is the typed participant exception carrying exactly that
code; `UnknownTransient`, `Permanent`, `Veto`, `Cancelled`, `InvalidResponse`, and every unrecognized
exception are not retryable. A returned validated result carrying any other failure code is terminal.
`RetryResult<T>` is converted to the existing participant failure/evidence before the one immutable
lifecycle write, so every attempt and exhaustion state is persisted by
`UpdateParticipantLifecycleAsync`.

`TransactionalParticipantAdapterTests` add no-hang tests for zero-delay exhaustion, cancellation
during the delay, cancellation during the callback, an advancing fake clock that reaches
`MaxElapsed`, and `MaxAttempts == 1`; each asserts the exact attempt count, delay count, token
propagation, final retry code, and bounded completion. Tests also prove undeclared exceptions never
retry and declared transient codes retry only within both bounds. The failure matrix below names
`RetryBudget`, `IMonotonicClock`, `IWallClock`, and the injected delay scheduler as required dependencies rather than an
unspecified clock or static `Task.Delay`.

## Failure matrix implementation symbols and tests

Use `TransactionalParticipantAdapter.ClassifyReturnedFailure`,
`TransactionalParticipantAdapter.ClassifyThrownException`, `ValidatePrepareResult`,
`ValidateParticipantResult`, `RetryBudget`, `MultiMemoryTransactionCoordinator.ExecuteUnderLockAsync`,
`FinishDecisionAsync`, `RollbackBeforeDecisionAsync`, `MultiMemoryTransactionCoordinator.RecoverUnderLockAsync`,
`MultiMemoryTransactionCoordinator.RecoverTransactionalParticipantsAsync`,
`TransactionalParticipantRegistry.RehydrateAsync`, and
`MultiMemoryTransactionRecordCodec.WriteExpected` as the named implementation symbols.

| Phase/failure | Required behavior | Implementation symbols | Named test |
|---|---|---|---|
| Prepare veto | No decision; reverse rollback; retain unresolved evidence | `ClassifyReturnedFailure`, `ClassifyThrownException`, `RollbackBeforeDecisionAsync` | `TransactionalParticipantAdapterTests.PrepareVetoRollsBackInReverseOrder` |
| Prepare declared transient | Retry only declared code within `RetryBudget` | `RetryBudget`, `PrepareAsync` | `TransactionalParticipantAdapterTests.DeclaredTransientHonorsAttemptAndElapsedBounds` |
| Retry success | Return non-null value with all failure/cancellation flags false | `RetryBudget.ExecuteAsync` | `TransactionalParticipantAdapterTests.SuccessHasExactRetryResultFields` |
| Declared transient exhausted | Return exact exhausted result and persist before rollback/recovery | `RetryBudget`, `UpdateParticipantLifecycleAsync` | `TransactionalParticipantAdapterTests.ExhaustionHasExactFieldsAndRecoveryMapping` |
| Cancellation before callback | No callback, `Cancelled`, `StateMayHaveChanged=false`, `Ambiguous=false` | `RetryBudget.ExecuteAsync` | `TransactionalParticipantAdapterTests.CancellationBeforeCallbackHasExactFields` |
| Cancellation after callback start | Callback counted, `Cancelled`, `StateMayHaveChanged=true`, `Ambiguous=true` | `RetryBudget.ExecuteAsync`, `ClassifyThrownException` | `TransactionalParticipantAdapterTests.CancellationAfterCallbackStartIsAmbiguous` |
| Retry clock throws/unusable | Return `RetryMonotonicClockFailed`, `StateMayHaveChanged = true`, `Ambiguous = true`; persist once; no retry | `RetryBudget`, `UpdateParticipantLifecycleAsync` | `TransactionalParticipantAdapterTests.ClockFailureReturnsExactAmbiguousResult` |
| Retry clock moves backwards | Return `RetryMonotonicClockMovedBackwards`, `StateMayHaveChanged = true`, `Ambiguous = true`; persist once; no retry | `RetryBudget`, `UpdateParticipantLifecycleAsync` | `TransactionalParticipantAdapterTests.BackwardClockReturnsExactAmbiguousResult` |
| Retry elapsed overflows | Return `RetryElapsedOverflow`, `StateMayHaveChanged = true`, `Ambiguous = true`; persist once; no retry | `RetryBudget`, `UpdateParticipantLifecycleAsync` | `TransactionalParticipantAdapterTests.ElapsedOverflowReturnsExactAmbiguousResult` |
| Prepare undeclared transient | Map to `UnknownTransient`; never retry | `ClassifyReturnedFailure`, `ClassifyThrownException` | `TransactionalParticipantAdapterTests.UndeclaredTransientIsPermanent` |
| Returned participant failure | Extract typed code/flag from validated result; retry only declared transient | `IRetryClassifier<T>.Classify(T, bool)` | `TransactionalParticipantAdapterTests.ReturnedFailuresUseTypedClassification` |
| Thrown participant exception | Extract only typed failure; never inspect exception text | `IRetryClassifier<T>.Classify(Exception, bool)` | `TransactionalParticipantAdapterTests.ThrownExceptionsUseTypedClassification` |
| Prepare permanent/cancellation | No retry; non-cancellable cleanup before decision | `ExecuteUnderLockAsync`, `RollbackBeforeDecisionAsync` | `TransactionalParticipantRecoveryTests.PrepareCancellationRetainsUnresolvedEvidence` |
| Prepare invalid response | Reject before persistence; uncertain mutation retains evidence | `ValidatePrepareResult` | `TransactionalParticipantContractTests.InvalidPrepareCombinationsAreRejected` |
| Commit veto/transient/permanent | No rollback after decision; bounded declared retry only | `FinishDecisionAsync`, `RetryBudget` | `TransactionalParticipantRecoveryTests.PostDecisionFailuresNeverRollback` |
| Commit cancellation/invalid response | Retain committed record and quarantine identity | `FinishDecisionAsync`, `B4ParticipantRecoveryState` | `TransactionalParticipantRecoveryTests.CommittedCancellationQuarantinesParticipant` |
| Rollback veto/transient/permanent | Retain until every participant is proven rolled back | `RollbackBeforeDecisionAsync`, `RehydrateAsync` | `TransactionalParticipantRecoveryTests.RollbackRequiresProof` |
| Rollback cancellation/invalid response | No retry in callback; retain record for restart | `ClassifyThrownException`, `RehydrateAsync` | `TransactionalParticipantRecoveryTests.RollbackCancellationRehydrates` |
| Any rollback failure | Scope-local `RecoveryRequired`; unrelated observers/scopes continue | `RecoveryRequired`, `TransactionalParticipantRegistry.RehydrateAsync` | `TransactionalParticipantRecoveryTests.RollbackFailureIsScopeLocal` |
| Prepared recovery | Rehydrate B4 in global combined order, merge with B3 once, reverse-abort with rollback proof | `RecoverTransactionalParticipantsAsync`, `RehydrateAsync`, `RollbackBeforeDecisionAsync` | `TransactionalParticipantRecoveryTests.PreparedRecoveryRollsBackB4InDeterministicOrder` |
| Committed recovery | Rehydrate B4 in global combined order, roll forward only, never rollback | `RecoverTransactionalParticipantsAsync`, `FinishDecisionAsync`, `GetCommitStatusAsync` | `TransactionalParticipantRecoveryTests.CommittedRecoveryRollsForwardB4InDeterministicOrder` |
| Mixed B3/B4 recovery | One held lock and one admission; preserve `ParticipantOrderKey` combined ordering and aggregate | `RecoverTransactionalParticipantsAsync`, `AdmitUnderLockAsync` | `TransactionalParticipantRecoveryTests.MixedB3AndB4RecoveryPreservesOrdinalOrderingAndSingleAdmission` |
| Unknown/mismatched participant or capability | Retain exact state/evidence; quarantine without registry fallback or plugin callback | `RehydrateAsync`, `UpdateParticipantLifecycleAsync` | `TransactionalParticipantRecoveryTests.UnknownOrMismatchedB4IsQuarantinedWithoutCallback` |
| Rejected-only recovery | Trigger on rejected metadata alone; pass a nullable factory as null and build rejected outcomes without factory, adapter, or plugin calls | `MultiMemoryTransactionCoordinator.RecoverUnderLockAsync`, `MultiMemoryTransactionCoordinator.RecoverTransactionalParticipantsAsync`, `MultiMemoryAggregateResult.Create` | `TransactionalParticipantRecoveryTests.RejectedOnlyRecoveryBuildsProjectionWithoutFactoryOrAdapterInvocation` |
| Mixed admitted/rejected recovery | Rehydrate admitted states, directly project rejected metadata, preserve both lists across writes | `RecoverTransactionalParticipantsAsync`, `WriteExpected` | `TransactionalParticipantRecoveryTests.MixedAdmittedAndRejectedRecoveryPreservesBothMetadataLists` |
| Aggregate evidence | Preserve all B3 and B4 outcomes, quarantine, ordering, and scope-local classification | `MultiMemoryAggregateResult`, `RecoverTransactionalParticipantsAsync` | `TransactionalParticipantRecoveryTests.RecoveryAggregatePreservesB3AndB4Evidence` |
| Aggregate projection validation | Build from expected request sequence; reject missing, extra, mismatched, duplicate, gap, and out-of-order outcomes before mapping | `BuildAggregateProjection`, `MultiMemoryAggregateResult` validating constructor | `MultiMemoryTransactionAggregateProjectionTests.MissingExtraMismatchedDuplicateGapAndOutOfOrderAreInconsistent` |
| Unknown field/version/state/evidence/handle | Retain record; quarantine identity; never call plugin recovery | `ParseAndValidate`, `RehydrateAsync` | `MultiMemoryTransactionContractTests.UnknownB4DataFailsClosed` |
| Expected-record collision | Compare exact canonical persisted content under the held lock; reject same-`UpdatedAt` stale content | `WriteExpected`, `UpdateParticipantLifecycleAsync` | `TransactionalParticipantRecoveryTests.WriteExpectedRejectsSameTimestampContentCollision` |

## Task order and explicit inventory

1. Define the public contract, descriptor capability canonicalization, and constructor/result
   validation. This has no dependency on the coordinator.
2. Add the registry, source-generated metadata, durable DTO extension including persisted
   capabilities and bounded `RejectedTransactionalParticipants` metadata, exact-content expected-write
   concurrency, and fail-closed rehydration. This
   depends on task 1 and must land before recovery orchestration.
3. Implement the host adapter, `IRetryClassifier<T>` returned-result and exception overloads with
    explicit declared-code/`StateMayHaveChanged` extraction, `IMonotonicClock`, `IWallClock`, injected
    delay scheduler, `RetryBudget`, `RetryResult<T>`, separate returned-failure/exception tests, and
    no-hang tests. This depends on the contract and registry
   shapes from tasks 1-2.
4. Inject the Core factory into `FileMemoryStore` and the existing coordinator, add the private
    `ValidatedMultiMemoryAggregateProjection` builder/`MultiMemoryAggregateResult.Create` boundary, then implement the
   coordinator-owned `RecoverTransactionalParticipantsAsync` with the single-lock B3/B4 admission
   bridge, global combined ordering, prepared abort, committed roll-forward, exact lifecycle writes,
    quarantine, rejected-only and mixed admitted/rejected recovery, and the three-list aggregate result.
    This depends on tasks 1-3 and is the only task
    that changes recovery control flow; it must not add B4 recovery behavior to the discovery/parser
    boundary in `src/Scrinia.Core/RecoveryDiscovery.cs`.
5. Extract the sanitizer and prove observer/child-process exclusion. This is independent of the
   recovery algorithm but must retain the no-public-surface boundary.
6. Preserve MCP/CLI mappings and verify that no new public command, response field, or endpoint is
   introduced; this depends on tasks 1-5.
7. Run the focused contract, registry, recovery, ordering, aggregate, Release-build,
   self-contained-trimmed-publish, full-solution, and independent-review gates. Write the B4 ledger
   and report only after raw evidence is checked in.

The dependency and boundary citations are the accepted B3 ledger
(`docs/superpowers/ledgers/2026-09-13-b3-final-acceptance.md`), the B3 implementation/test range
`986fce0a^..1a10c0f77316fa0717c9ad4307a162a8176c1041`, and the Phase B roadmap
(`docs/superpowers/specs/2026-09-10-phase-b-roadmap-design.md:123-133,171-177`). The concrete
recovery entry points are the checked-in `MultiMemoryTransactionCoordinator` methods named in the
Existing B3 contract table. `src/Scrinia.Core/RecoveryDiscovery.cs` is retained only for the
discovery/parser boundary; it contains no B4 recovery implementation and is not an implementation
symbol for any B4 recovery row. The exact publication contract is the `WriteExpected` definition
above. These citations anchor B4 to the accepted B3
lock, decision, recovery, and aggregate contracts rather than to summary counts or a new authority.

The exact production paths are. Discovery remains in
`src/Scrinia.Core/RecoveryDiscovery.cs`; the recovery task belongs in the existing
`src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs` owner of `RecoverUnderLockAsync`; the record/DTO and exact-write changes
belong in `MultiMemoryTransactionRecord.cs` and `MultiMemoryTransaction.cs`; the registry/factory
boundary belongs in `src/Scrinia/Services/TransactionalParticipantRegistry.cs`; and the single-lock
orchestration remains in `MultiMemoryTransactionCoordinator.cs`. No new coordinator, journal, or
public surface is added.

```text
src/Scrinia.Plugin.Abstractions/ITransactionalMemoryParticipant.cs
src/Scrinia.Plugin.Abstractions/IScriniaPlugin.cs
src/Scrinia.Core/Scrinia.Core.csproj
src/Scrinia.Core/MultiMemoryTransaction.cs
src/Scrinia.Core/MultiMemoryTransactionRecord.cs
src/Scrinia.Core/TransactionalParticipantSerialization.cs
src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs
src/Scrinia.Core/FileMemoryStore.cs
src/Scrinia.Core/RecoveryDiscovery.cs                  # discovery/parser only; no B4 recovery owner
src/Scrinia.Core/TransactionalRetry.cs
src/Scrinia.Core/CompositeEventSink.cs
src/Scrinia.Core/IMemoryEventSink.cs
src/Scrinia.Core/MultiMemoryStableDetail.cs
src/Scrinia.Mcp/MemoryTools.Core.cs
src/Scrinia.Mcp/MemoryTools.Skills.cs
src/Scrinia/Services/TransactionalParticipantRegistry.cs
src/Scrinia/Services/TransactionalParticipantAdapter.cs
src/Scrinia/Services/McpPluginHost.cs
src/Scrinia/Commands/WorkspaceSetup.cs
src/Scrinia/Commands/ScriniaCommands.Memory.cs
src/Scrinia/Commands/CliJsonContext.cs
src/Scrinia/Scrinia.csproj
```

If a directly serialized B4 public result is introduced, register its exact root in
`src/Scrinia/Commands/CliJsonContext.cs`, `src/Scrinia.Mcp/MemoryTools.Skills.cs`, or
`src/Scrinia/Services/PluginClientJsonContext.cs` as its owner requires; otherwise do not touch
those contexts. The exact test paths are:

```text
tests/Scrinia.Tests/TransactionalParticipantContractTests.cs
tests/Scrinia.Tests/TransactionalParticipantRegistryTests.cs
tests/Scrinia.Tests/TransactionalParticipantAdapterTests.cs
tests/Scrinia.Tests/TransactionalParticipantRecoveryTests.cs
tests/Scrinia.Tests/TransactionalObserverBoundaryTests.cs
tests/Scrinia.Tests/MultiMemoryTransactionAdmissionTests.cs
tests/Scrinia.Tests/MultiMemoryTransactionContractTests.cs
tests/Scrinia.Tests/MultiMemoryTransactionAggregateProjectionTests.cs
tests/Scrinia.Tests/MultiMemoryTransactionLifecycleTests.cs
tests/Scrinia.Tests/MultiMemoryTransactionFacadeTests.cs
tests/Scrinia.Tests/CliMultiMemoryTransactionTests.cs
tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingsPluginObserverBoundaryTests.cs
```

Evidence paths are only
`docs/superpowers/ledgers/2026-09-13-b4-plugin-event-boundary.md` and
`docs/superpowers/reports/2026-09-13-b4-final-gate.md`.

No task may stage a broad `src`, `tests`, or docs pattern. Every task records the accepted B3
baseline as `git diff --name-only 986fce0a^..1a10c0f77316fa0717c9ad4307a162a8176c1041` and
records the B4 implementation/test diff separately. Documentation/evidence commits (`63befd20`,
`4a15e329`, and the B4 planning commit) are reported separately from implementation/test commits.
The final gate requires no unlisted production, test, or evidence path.

## Self-review

There are no TBD/TODO markers, guessed endpoints, second journals, second coordinators, untyped
payloads, reflection serializers, or post-decision rollback paths in this plan. Rehydration has the
exact `B4ParticipantRehydrationRequest`, identity/version/ordering-key checks, canonical persisted capabilities,
descriptor comparison before callbacks, and fail-closed quarantine behavior. Recovery has one
held-lock algorithm with one concrete owner (`MultiMemoryTransactionCoordinator`), an exact invocation
from `RecoverUnderLockAsync`, deterministic combined `ParticipantOrderKey` ordering, explicit
prepared-abort versus committed roll-forward, exact-content writes after every lifecycle transition,
and three-way aggregate evidence preservation. Admission names both existing delegate signatures and
the single composition path; bounds apply to the combined participant set; source-generated
registration is explicit; participant-declared ordering keys are canonical, unique, persisted, and
used by one global comparator; B3 null keys intentionally sort before B4 keys; and the Vulkan
child-process exclusion is tested. One contiguous zero-based ordinal range spans B3, admitted B4,
and non-admitted B4 outcomes; Core assigns it and the private aggregate constructor accepts only the
`ValidatedMultiMemoryAggregateProjection` minted by the private builder through `Create`, then rejects malformed
projections before facade mapping. The three result types have one validating construction boundary
with invalid-combination coverage, and the earlier positional declarations are explicitly superseded.
The retry table is exhaustive for success, declared-transient exhaustion, cancellation before callback,
cancellation after callback start, monotonic-clock failure, backward movement, and overflow; each row
has exact fields, failure mapping, durable transition, recovery action, aggregate classification, and
observer behavior with named tests. `BuildAggregateProjection` is the sole ordinal producer and the
aggregate construction is reachable only through `MultiMemoryAggregateResult.Create`; the test map
includes missing, extra, mismatched, duplicate, gap, and out-of-order projections at that boundary.
The recovery trigger checks both admitted and rejected metadata, and rejected-only recovery builds
outcomes without factory, adapter, or plugin calls. The exact recovery signature accepts
`IMultiMemoryParticipantAdapterFactory?`; `participantFactory: null` is a supported, tested
factory-free path, while admitted-state recovery rejects a null factory before rehydration.
The discovery/parser boundary is referenced only for discovery/parser responsibilities and remains
nowhere in the B4 recovery implementation; all B4 recovery is owned by
`MultiMemoryTransactionCoordinator`. Rejected request metadata is
bounded, source-generated, preserved across record transitions, and explicitly non-executable.
Retry behavior names `IMonotonicClock`, `IWallClock`, `RetryBudget`, the injected delay scheduler,
`IRetryClassifier<T>.Classify(T, bool)` and `Classify(Exception, bool)`, separate returned/exception
tests, cancellation propagation, declared-code/flag extraction, exact clock-failure codes and state flags,
persistence/recovery mapping, bounded results, and no-hang tests. The exact production and test path inventory
includes `TransactionalRetry.cs`, the coordinator owner, aggregate mappers, and every affected
contract/recovery/facade test. Delegation IDs are not valid repository-document citations in the
available review artifacts, so this plan uses exact roadmap, source symbol, test, and checked-in B3
evidence/review citations instead.
