# B4 Plugin and Event Transaction Boundary — Design Specification

**Status:** Approved design
**Date:** 2026-09-13
**Predecessor:** `docs/superpowers/specs/2026-09-12-multi-memory-transaction-design.md`
**Roadmap:** `docs/superpowers/specs/2026-09-10-phase-b-roadmap-design.md`
**Scope:** Transaction participation for trusted, in-process plugin components

## 1. Goal

B4 defines the narrow boundary between Scrinia's local transaction coordinator and plugin/event
components. It makes participation explicit without changing the meaning of existing event hooks:
ordinary hooks remain observers, while a plugin that needs commit-veto or rollback guarantees must
opt into a separate participant contract.

The design provides a deterministic, fail-closed lifecycle for participants that run in the same
process as the coordinator. It reuses B3's coordinator and participant-adapter seams rather than
creating a second transaction engine, journal, lock, or public aggregate command.

## 2. Boundary and invariants

The following decisions are normative:

- Only in-process plugin participants are supported. Remote, child-process, HTTP, and otherwise
  out-of-process participants are not transactional participants.
- Existing event hooks and sinks remain observers. They never acquire veto power, and their failures
  warn and allow the local transaction to continue.
- Transactional behavior requires explicit plugin opt-in. Implementing an observer interface,
  loading a plugin, or being present in the event-sink composite does not imply participation.
- B4 uses a coordinator adapter layer over B3. It does not duplicate B3 decision records,
  participant payload journals, scope locks, recovery barriers, or aggregate result contracts.
- Participant lifecycle is `Prepare`, `Commit`, and `Rollback`. Rollback is legal only before the
  B3 durable commit decision; after that decision, recovery is commit/verification only.
- Participant identity and capability metadata are stable, validated, and included in the durable
  participant manifest. Registration and invocation order are deterministic.
- Duplicate participant identity is rejected before any participant mutation or durable decision.
- Retries are bounded and apply only to failures the participant declares transient. Vetoes,
  permanent failures, and exhausted transient failures fail closed and trigger rollback when the
  B3 decision is not committed.
- Transaction and participant recovery is idempotent. Rollback failure is retained as evidence,
  and the participant is quarantined from further transactional admission until explicitly cleared
  by a later approved operational design.
- Observer failures warn and continue. Observer diagnostics are sanitized and do not alter the
  transaction outcome.
- No new public MCP or CLI aggregate command is introduced by B4 unless separately approved.
- B4 does not include remote participants, NMP/2 or sidecar changes, archive or garbage collection,
  vector/index contract changes, or automatic conversion of observers into participants.

## 3. Architecture

### 3.1 Existing layers retained

The local store and B3 coordinator remain authoritative for memory state, scope locking, durable
decision publication, read visibility, and recovery. B2 adapters remain authoritative for each
memory participant's payload journal and proof. `IMemoryEventSink` and the published operation-hook
contract continue to represent post-operation observation paths; their existing observer semantics
are not strengthened by this specification.

### 3.2 B4 participant contract

B4 adds an internal Core-facing participant abstraction and a plugin-facing opt-in capability. The
contract is intentionally narrower than a memory store and does not expose raw journal bytes,
filesystem paths, sidecar contents, or coordinator internals. Conceptually, each participant
provides:

```text
Stable identity
Version/capability metadata
Prepare(transaction context) -> prepared handle or typed failure
Commit(prepared handle, recovery context) -> committed/proof result or typed failure
Rollback(prepared handle, recovery context) -> rolled-back/proof result or typed failure
Failure classification and bounded retry policy
```

The exact public surface belongs in the implementation plan and must remain source-generated and
typed where serialized. The participant receives a validated transaction identity, operation ID,
scope, ordered memory manifest, and only the capability-approved data it needs. It does not receive
an unbounded exception, arbitrary object payload, or an authority to mutate Scrinia's store outside
the coordinator path.

The descriptor has the exact property `string OrderingKey { get; }`. The transactional request DTO
and `B4ParticipantRecoveryState` each have the exact non-null property
`string OrderingKey { get; }`. The persisted JSON property is exactly `orderingKey`; it is required
for every B4 state and absent only when no B4 state exists. Construction and parse/rehydration
validation enforce the canonical format, bounds, uniqueness, and ordinal descriptor match before
admission or callback.

### 3.3 Coordinator adapter layer

The adapter translates B4 lifecycle calls into B3's typed participant handles and aggregate outcomes.
It is the sole bridge between plugin participants and `MultiMemoryTransactionCoordinator`. The
adapter:

1. validates participant metadata and capability compatibility;
2. maps the registered participant to one deterministic B3 participant entry;
3. invokes lifecycle methods under B3's existing scope barrier and lock;
4. maps typed participant outcomes into B3's aggregate result;
5. preserves B3's durable decision and recovery rules; and
6. prevents a plugin from publishing a competing decision or payload journal.

There is one coordinator and one transaction decision. A plugin participant is an additional
participant in that coordinator, not a nested coordinator and not an observer promoted at runtime.

## 4. Registration, identity, and capabilities

Plugins must explicitly register a transactional participant. Registration is opt-in and must be
performed during deterministic host setup, before transactions are admitted. A plugin that exposes
only `IMemoryEventSink` or `IMemoryOperationHook` is registered as an observer only.

Each participant declares:

- a stable identity unique within the host's participant registry;
- a participant contract version;
- supported operation kinds and lifecycle capabilities;
- a declared retry policy and transient-failure vocabulary; and
- a participant-declared ordering key used after registration validation.

Identity is opaque to the coordinator but compared ordinally. It must be non-empty, bounded, and
stable across process restarts and plugin instances. Display names are not identity unless the
plugin contract explicitly makes them stable. Version and capability metadata are validated before
registration and persisted in the B3 participant manifest sufficiently to validate recovery.

`OrderingKey` is a required, non-null `string` property on `TransactionalParticipantDescriptor`.
Its canonical form is 1–64 ASCII characters matching `[a-z0-9][a-z0-9-]{0,63}`; it is already
lowercase, contains no surrounding whitespace, and is compared with `StringComparer.Ordinal`.
The descriptor rejects any non-canonical value rather than trimming or normalizing it. Ordering keys
are unique within the host registry: a duplicate key, like a duplicate identity, is rejected before
any callback or durable decision. A participant request and its persisted recovery state carry the
same key and must match the registered descriptor ordinally; a missing, changed, or duplicate key is
a registration/admission or recovery mismatch and never falls back to identity ordering.

The exact global comparator for every admission, prepare, commit, rollback, recovery, and aggregate
projection is the ascending tuple
`(OrderingKey, ParticipantClass, CanonicalIdentity, OperationId, Kind)` using ordinal string
comparison and ascending integer comparison. B4 uses its declared `OrderingKey` and
`ParticipantClass = 1`; B3 has a null ordering key, `ParticipantClass = 0`, and its resolved subject
as `CanonicalIdentity`. Null sorts before non-null. Therefore **B3-before-B4 is intentional**:
every B3 tuple precedes every B4 tuple, with the remaining fields providing deterministic ordering
within each class. A complete tuple collision is inconsistent and rejected, never resolved by list
position. The accepted set is sorted by this comparator, and rollback runs in reverse order.
Caller registration or request order must never affect lifecycle order.

### 4.1 One global ordinal invariant and authoritative projection

The coordinator assigns one zero-based `Ordinal` after the complete requested set is sorted by the
comparator above. The ordinal domain is the union of B3 outcomes, admitted B4 outcomes, and
non-admitted B4 outcomes: for `N` requested participants it is exactly the contiguous set `0..N-1`,
with no duplicates. A rejected/non-admitted B4 request keeps the position it occupied in that sorted
requested sequence; admission does not renumber survivors.

Each of the three aggregate projections is individually ascending by this ordinal. A projection may
contain gaps, but the union of `Participants`, `TransactionalParticipants`, and
`NonAdmittedParticipants` must contain every ordinal exactly once and reconstruct the sorted
requested sequence.

Core owns one authoritative builder:

```text
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

`BuildAggregateProjection` is the only producer of aggregate ordinals. It sorts the requested
sequence by `ParticipantOrderKey`, assigns `0..N-1`, validates every outcome against the requested
tuple, and returns three copied ascending lists plus the canonical `GlobalOrdinalUnion`. The union
contains every requested participant, including rejected B4 requests. The validating aggregate
constructor receives the expected requested sequence and the private projection above. The projection
type is nested and private, its constructor is private, and the builder is private to the Core aggregate
result type. The internal `Create` factory is the only production entry point and calls the builder;
there is no public/internal projection type, token factory, or aggregate overload that a caller or test
can forge. Facade mappers may neither sort nor renumber.

The constructor rechecks projection ownership, sequence identity, list membership, class/admitted flag,
strict ordering, and contiguous union. Missing, extra, mismatched, duplicate, gapped, or
out-of-order outcomes throw the typed `Inconsistent` aggregate-contract error before MCP/CLI
mapping. Tests cover missing B3, extra B4, mismatched identity/operation/kind, duplicate ordinal,
gap, out-of-order list, wrong class, wrong admitted flag, every permutation, and forged/stale token.

A participant that changes its identity, contract version, capabilities, or declared recovery
behavior without a compatible registration record is not admitted. It is treated as unavailable or
incompatible, not silently substituted.

## 5. Data flow and lifecycle

### 5.1 Admission

The operation resolves and validates the persistent scope and memory participants through B3.
Before mutation, the coordinator validates the complete transactional participant set, rejects
duplicate identities, and rejects any participant lacking the capability required by the operation.
Existing recovery barriers and B3 admission checks run first. No plugin callback is made for a
rejected registration or an unsafe transaction.

### 5.2 Prepare

Under B3's existing scope lock and recovery barrier, the coordinator invokes `Prepare` for each
accepted participant in deterministic order. Prepare may validate external-to-the-plugin state and
create participant-owned provisional state, but it must not publish an irreversible effect.

Successful preparation returns an opaque, replayable handle and operation correlation. The B3
coordinator then publishes its existing `prepared` decision record containing the complete ordered
manifest, including participant identity and capability metadata needed for recovery.

A participant may return a typed veto, permanent failure, transient failure, cancellation, or
success. A veto is a deliberate refusal to commit, not an observer error. Any non-success before
the B3 decision causes reverse-order rollback of already prepared participants.

### 5.3 Commit

The B3 durable `committed` decision remains the linearization point. After it succeeds, the
coordinator invokes `Commit` in deterministic order and verifies each participant's completion
proof. No participant, including one that previously returned a veto or failure, may cause the
already-committed transaction to roll back.

Derived local state follows B3: Scrinia cache and facts invalidation occurs only after local memory
participant bytes are verified. A plugin participant must not require vector or embedding contract
changes as part of B4.

### 5.4 Rollback

Before the B3 decision, a failed prepare or failed rollback path invokes `Rollback` in reverse
order for participants that may have changed state. Rollback must be safe to repeat using the
transaction ID, participant identity, and operation ID. A participant must report whether it proves
rolled back, remains changed, or is ambiguous.

If every participant proves rollback, B3 returns its existing rolled-back classification. If any
participant cannot prove rollback, the coordinator retains evidence and returns recovery-required;
the affected participant is quarantined and excluded from new transactional admission. Rollback
failure is never downgraded to an observer warning.

## 6. Failure and retry semantics

Participants classify failures as exactly one of:

| Class | Meaning | Coordinator behavior |
|---|---|---|
| `transient` | A declared, retryable condition | Retry within the participant's bounded policy; then fail closed if exhausted |
| `veto` | Participant refuses the proposed commit | Stop pre-decision admission/lifecycle and roll back |
| `permanent` | The operation cannot safely succeed | Stop and roll back before decision |
| `cancelled` | Cancellation was requested | Treat as non-committed failure before decision; recover conservatively after decision |
| `ambiguous` | Final state cannot be proven | Fail closed, retain evidence, and quarantine as applicable |

Only `transient` failures are retried. The retry budget is finite, declared, and bounded by both
attempt count and elapsed time. Elapsed time is measured only by an injected monotonic provider
with the exact API `TimeSpan GetElapsed()`; wall-clock time is never used for retry enforcement.
Persisted `CreatedAt`, `UpdatedAt`, and other audit timestamps use a separate injected
`IWallClock { DateTimeOffset UtcNow { get; } }`. Backoff and jitter, if used, are deterministic
under tests and do not permit unbounded waiting. Cancellation and shutdown interrupt waiting without
converting an unproven state into success.

`RetryResult<T>` is an explicit validating result, not an exception-shaped convention. Each callback
also uses this typed classifier API; retry code must not infer disposition from exception text or from
an untyped success predicate:

```text
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
```

The adapter creates a classifier for each result type with typed extractors. The returned-result
overload validates the complete result, extracts its `Failure.Code`, `Failure.StateMayHaveChanged`,
evidence, and success state, and marks only `DeclaredTransient` retryable. Veto, `Permanent`,
`UnknownTransient`, `Cancelled`, `InvalidResponse`, and all other non-success codes are terminal; an
invalid result combination maps to `InvalidResponse` before persistence. The exception overload only
extracts a code/flag from a typed `TransactionalParticipantException` carrying a validated
`TransactionalParticipantFailure`; cancellation uses `callbackStarted`, and every other exception
maps to `Permanent` when unchanged state is proven or to ambiguous changed state when it is not.
Exception text is never inspected. Returned failures and thrown exceptions have separate classifier
tests. `RetryBudget` converts a retryable classification that reaches its bound to
`DeclaredTransientExhausted`, while cancellation and the three clock conditions produce their exact
terminal rows below.

`RetryResult<T>` is an explicit validating result, not an exception-shaped convention:

```text
RetryResult<T>(Value, Attempts, Elapsed, FailureCode, StateMayHaveChanged,
               Ambiguous, Exhausted, Cancelled)
```

The following table is exhaustive. `Attempts` is the number of callback invocations; `Elapsed` is
the last validated monotonic reading (zero if no reading exists). `Value` is null for every failure.

| Terminal state | Exact fields | Failure mapping | Durable/recovery lifecycle | Aggregate and observer behavior |
|---|---|---|---|---|
| Success | `FailureCode=null`, `StateMayHaveChanged=false`, `Ambiguous=false`, `Exhausted=false`, `Cancelled=false`, non-null `Value` | no failure | persist proof; continue | normal aggregate; observer failures warn/continue |
| Declared transient exhausted | `FailureCode=DeclaredTransient`, `StateMayHaveChanged` is the participant declaration, `Ambiguous=false`, `Exhausted=true`, `Cancelled=false` | exact `DeclaredTransient` | persist counters/failure; pre-decision reverse rollback, post-decision commit/status verification; quarantine without proof | failed or recovery-required; observer warnings do not alter it |
| Cancellation before callback | `Attempts` unchanged, `FailureCode=Cancelled`, `StateMayHaveChanged=false`, `Ambiguous=false`, `Exhausted=false`, `Cancelled=true` | `Cancelled(false)` | persist cancellation; no callback evidence; rollback already-prepared participants before decision | cancelled/non-committed; observer failures still warn/continue |
| Cancellation after callback start | `Attempts` includes callback, `FailureCode=Cancelled`, `StateMayHaveChanged=true`, `Ambiguous=true`, `Exhausted=false`, `Cancelled=true` | `Cancelled(true)` | persist immediately with safe evidence; conservative rollback before decision, commit/status verification after; quarantine without proof | recovery-required/ambiguous; observer warnings never downgrade it |
| Monotonic clock failure | `FailureCode=RetryMonotonicClockFailed`, `StateMayHaveChanged=true`, `Ambiguous=true`, `Exhausted=false`, `Cancelled=false`, attempts already invoked | exact clock code | one immediate lifecycle write with evidence; rollback before decision, commit/status after; quarantine without proof | recovery-required/ambiguous; observer warning is separate |
| Negative movement | same flags as clock failure, with `FailureCode=RetryMonotonicClockMovedBackwards` | exact clock code | same immediate persistence and decision-state action | same recovery-required/ambiguous result; observers warn/continue |
| Overflow | same flags as clock failure, with `FailureCode=RetryElapsedOverflow` | exact clock code | same immediate persistence and decision-state action | same recovery-required/ambiguous result; observers warn/continue |

`TransactionalParticipantFailure` contains the exact code, sanitized detail, and
`StateMayHaveChanged`; the internal `Ambiguous` value is preserved in recovery state and outcome.

Accordingly, `B4ParticipantRecoveryState` persists the exact booleans
`StateMayHaveChanged` and `Ambiguous`, with JSON names `stateMayHaveChanged` and `ambiguous`, beside
`RetryAttempt`, `RetryElapsedMilliseconds`, `LastFailureCode`, and the complete evidence triple.
The parser rejects a boolean/lifecycle combination that contradicts the terminal table.

For every row, `B4ParticipantRecoveryState.RetryAttempt` equals `Attempts` and
`RetryElapsedMilliseconds` equals `Elapsed` after validated conversion. Success persists
`LifecycleState=Committed` or `RolledBack`, with null failure and evidence. Declared-transient
exhaustion and cancellation before callback persist the current lifecycle with the exact failure
code, no new evidence, and `QuarantineState=None` unless later rollback lacks proof. Cancellation
after callback start and all clock rows persist `LifecycleState=Changed` when change is possible or
`Ambiguous` when it cannot be proven, with complete evidence fields and the exact code. Every row is
published by one immutable `WriteExpected` replacement before its aggregate is returned.

The three retry-clock failures have dedicated codes and explicit state mapping:

The codes are `RetryMonotonicClockFailed`, `RetryMonotonicClockMovedBackwards`, and
`RetryElapsedOverflow`; they are distinct values in `TransactionalFailureCode`, not aliases for
`ambiguous`, `UnknownTransient`, or `Permanent`.

| Condition | `FailureCode` | `StateMayHaveChanged` | `Ambiguous` | Durable/recovery behavior |
|---|---|---:|---:|---|
| `GetElapsed()` throws or returns an unusable value | `RetryMonotonicClockFailed` | `true` | `true` | Persist failure/evidence immediately; before the B3 decision roll back conservatively, after it recover by commit/status verification; quarantine if proof is unavailable. |
| elapsed value is less than the prior observed value | `RetryMonotonicClockMovedBackwards` | `true` | `true` | Same immediate persistence and decision-state recovery; never retry or reset the origin. |
| elapsed-plus-delay or elapsed computation overflows `TimeSpan`/integer bounds | `RetryElapsedOverflow` | `true` | `true` | Same immediate persistence and decision-state recovery; never retry or clamp the value. |

For the three clock rows, `Value = null`, `Exhausted = false`, `Cancelled = false`, and `Attempts`
is the number of callbacks already invoked. Tests assert every field in every row, both cancellation
timing points, exact persistence count, evidence, rollback versus no-rollback, quarantine, aggregate
classification, observer behavior, and no retry.

The retry constructor is `RetryBudget(TransactionalRetryPolicy policy, IMonotonicClock clock,
Func<TimeSpan, CancellationToken, ValueTask> delayScheduler)`. Each attempt receives the caller's
token. `MaxElapsed` is inclusive from the first attempt's monotonic origin: no new attempt or delay
may begin when elapsed exceeds `MaxElapsed`, and a delay is scheduled only when
`elapsed + delay <= MaxElapsed`. Clock failures, negative movement, or overflow produce the exact
failure rows above and never retry. A deterministic fake clock advances only when the
test directs it, including during a recorded delay; injected scheduling makes cancellation,
zero-delay exhaustion, and max-elapsed cases complete without sleeping or hanging.

Veto, permanent, and exhausted transient failures fail closed. Before the B3 decision they invoke
rollback; after the decision they invoke only commit/verification recovery. A participant that
throws an unclassified exception is mapped to a permanent or ambiguous typed failure according to
whether its state can be proven, never inferred from exception text.

Observer failures are outside this table: the existing event dispatch catches them, emits a
sanitized warning, and continues to the next observer and to the local transaction result.

## 7. Recovery and quarantine

Recovery discovers the existing B3 coordinator record and never rereads the original request. The
coordinator enters the B4 recovery path when either `TransactionalParticipants` or
`RejectedTransactionalParticipants` is non-empty; checking only admitted state is incorrect.
Participant recovery uses the persisted transaction ID, operation ID, identity, capability
metadata, and opaque participant handle/evidence needed by the adapter.

The coordinator record also persists an internal immutable
`RejectedTransactionalParticipants` list containing one `B4ParticipantRequestMetadata` for every
B4 request that reached admission but received no adapter:

```text
internal sealed record B4ParticipantRequestMetadata(
    string TransactionId, string Scope, string Address, string ParticipantIdentity,
    int ContractVersion, string OrderingKey, string OperationId, string Kind,
    TransactionalFailureCode RejectionCode, string RejectionDetail);
```

JSON names are `rejectedTransactionalParticipants`, `transactionId`, `scope`, `address`,
`participantIdentity`, `contractVersion`, `orderingKey`, `operationId`, `kind`, `rejectionCode`,
and `rejectionDetail`. Strings use the existing 512-code-unit bound; the address is persistent and
safe, the ordering key is canonical, `ContractVersion` is `1`, and the list is at most 4096 entries,
sorted by the global comparator with unique complete tuples. It contains no payload, metadata,
handle, or callback data and is never passed to an adapter. The list is atomically published before
returning a rejected aggregate and preserved through every B3/B4 record transition. Recovery unions
B3 manifests, admitted B4 states, and rejected metadata to reconstruct the exact requested sequence
and ordinal union, emitting a rejected outcome without invoking `CreateAsync`, `RehydrateAsync`, or
plugin code.

The exact coordinator path is `MultiMemoryTransactionCoordinator.RecoverUnderLockAsync` calling its
private `RecoverTransactionalParticipantsAsync` whenever that either-list predicate is true. That
method builds the `MultiMemoryAggregateProjectionInput` from B3 outcomes, admitted states, and
rejected metadata. Rejected metadata is converted directly to non-admitted `Rejected` outcomes in
the Core builder; it is never sent to `CreateAsync`, `RehydrateAsync`, or any adapter. A record with
only rejected metadata therefore still produces a complete aggregate projection and persists state
transitions without invoking a plugin. Mixed records rehydrate only admitted states, merge the direct
rejected outcomes, and use the same global ordinals. Both lists are preserved on every
`WriteExpected` replacement.

- A `prepared` record takes the pre-decision abort path. Participants are rolled back in reverse
  order, with bounded retries only for declared transient failures.
- A `committed` record takes the roll-forward path. Participants are committed and verified in
  deterministic order; rollback is forbidden.
- A missing, malformed, inconsistent, unsupported, or capability-mismatched record fails closed
  for the affected scope under B3's existing recovery semantics.
- Rehydration validates the persisted `orderingKey` against the descriptor and re-sorts the combined
  B3/B4 list with the exact global comparator before invoking any participant. A duplicate key or
  complete comparator tuple is malformed/inconsistent evidence, not a tie resolved by persistence
  order.
- Replaying a verified commit or rollback is a no-op with proof; it must not repeat the business
  effect.
- Unresolved rollback or ambiguous participant state retains B3 evidence and quarantines the
  participant identity. Quarantine prevents new B4 transactions from admitting that participant,
  but does not block unrelated observer delivery or unrelated scopes.
- Participant quarantine is evidence, not archive or garbage-collection policy. B5 may later define
  operator reporting and retention; B4 does not delete or compact the evidence.

Recovery and quarantine diagnostics identify only sanitized transaction/participant IDs, typed
  classifications, and safe evidence references. Raw plugin payloads, secrets, exception messages,
  sidecar content, vector data, and foreign paths are excluded.

## 8. Compatibility

Existing single-memory operations, B1/B2 behavior, B3 coordinator records, B2 journal locations,
NMP/2 artifacts, ordinary sidecars, archives, caches, facts projections, MCP tool names, CLI
command names, response wrappers, and exit classes remain unchanged.

Existing observers continue to receive the same event notifications with the same warn-and-continue
behavior. A component implementing both an observer and the explicit participant contract must use
the host's registration rules to avoid duplicate business effects; observer delivery is never used
as a hidden fallback for a failed participant lifecycle call.

B4 adds no public aggregate MCP tool or CLI command. If a separately approved surface later exposes
an aggregate result, it must map the existing typed B3 outcome and sanitized participant details;
it must not invent a second lifecycle or infer state from journal presence.

No remote participant transport, NMP/2 revision, sidecar schema, archive/GC service, vector/index
format or ranking change, or automatic observer conversion is authorized by this specification.

## 9. Security and privacy

In-process is a trust boundary, not a claim that plugins are harmless. Registration validates
identity, capabilities, bounded metadata, and ordering. The adapter exposes only typed, least-
privilege context and keeps coordinator records, B2 evidence, payloads, and secrets private.

Plugin-provided identities, reasons, diagnostics, paths, and capability labels are sanitized before
logging or crossing a facade. Control characters, raw exception text, rooted or traversal paths,
URIs, foreign roots, secrets, and content-bearing payloads are redacted or omitted. Evidence paths
are canonicalized and contained by the expected scope before use.

A participant cannot widen its capabilities during a transaction, acquire another scope lock,
replace the coordinator decision, or force a successful result after an unproven lifecycle outcome.
The fail-closed rule applies equally to malicious, buggy, unavailable, and outdated participants.

## 10. Testing matrix

| Area | Required coverage |
|---|---|
| Registration | Explicit opt-in; observer-only components stay observers; stable identity validation; capability validation; duplicate rejection; incompatible metadata rejection |
| Ordering | Shuffled registration produces identical prepare/commit order; canonical ordering-key validation; duplicate key rejection; persisted-key rehydration validation; intentional null-B3-before-B4 ordering; one contiguous unique global ordinal range across B3/admitted-B4/non-admitted-B4 projections; reverse rollback order; complete-tuple collision rejection; concurrent registration/admission behavior |
| Lifecycle | Prepare success; veto; permanent failure; declared transient retry; exhausted retry; commit and proof; pre-decision rollback; post-decision no-rollback |
| Recovery | Crash/fault at each lifecycle boundary; prepared abort; committed roll-forward; idempotent replay; missing/ambiguous evidence; capability mismatch; rejected-only recovery without adapter invocation; mixed admitted/rejected recovery; participant quarantine and scope isolation |
| Retry/cancellation | Separate returned-result and thrown-exception classifier tests; exhaustive success, declared-transient-exhausted, pre-callback cancellation, post-callback-start cancellation, monotonic-clock failure, backward movement, and overflow truth-table tests; exact fields/codes/attempts/delays; persistence, decision-state recovery, quarantine, observer warning behavior; no retry for veto/permanent/ambiguous/clock failures; deterministic no-hang tests |
| Observers | Observer failure warns and continues; observer order remains deterministic; observer failure cannot veto or roll back; observer/participant dual registration does not duplicate participant lifecycle |
| Compatibility | Existing B1/B2/B3 single- and multi-memory tests; existing event-sink and operation-hook behavior; unchanged MCP/CLI mappings; NMP/2, sidecar, archive, cache, facts, and vector regressions |
| Security/privacy | Sanitized IDs/reasons/diagnostics; control-character and path redaction; no payload/secret leakage; scope/path containment; malicious metadata and callback failures |
| Serialization | Any durable B4 metadata uses source-generated JSON; exact B3 record compatibility; trimming/AOT coverage; unknown metadata/version behavior fails closed |
| Projection/rejected requests | `BuildAggregateProjection(requestedParticipants, input)` assigns the sole canonical ordinal union; validating aggregate construction receives the expected sequence/projection; missing, extra, mismatched, duplicate, gap, and out-of-order outcomes fail as `Inconsistent`; bounded `RejectedTransactionalParticipants` round-trips and is included in recovery without adapter execution |
| Scope discipline | No remote participant tests, no format migration, no archive/GC implementation, no vector contract change, and no new public aggregate command unless separately approved |

## 11. Explicit non-goals and open decisions

The following are intentionally outside B4: remote or child-process transactional participants;
NMP/2 or sidecar evolution; archive, pruning, or journal garbage collection; vector/index changes;
automatic observer conversion; workspace-wide or cross-scope transactions; and a new public MCP or
CLI aggregate command.

The implementation plan must choose the concrete interface names, capability vocabulary, metadata
size limits, retry defaults, quarantine-clearance authority, and exact evidence representation while
preserving every invariant above. Those choices are implementation details, not permission to widen
the boundary.

## 12. Acceptance criteria

This specification is accepted for implementation only when the child plan and implementation
demonstrate all of the following:

1. Only explicitly opted-in in-process participants enter the B3 coordinator; existing hooks remain
   observers with warn-and-continue failure behavior.
2. B4 is implemented as one adapter layer over B3, with no competing coordinator, journal, lock,
   durable decision, or aggregate lifecycle.
3. Stable identity/capability metadata is validated and persisted as needed for recovery;
   duplicate identities are rejected before mutation.
4. Registration, prepare, commit, and reverse rollback order are deterministic and independent of
   caller registration order.
5. Retries are bounded and restricted to declared transient failures; veto, permanent, exhausted,
   cancellation, and ambiguous outcomes fail closed according to decision state.
6. Pre-decision failures roll back safely; post-decision failures never roll back and recover by
   commit/verification only.
7. Transaction and participant recovery are idempotent; unresolved rollback is retained as evidence
   and quarantines the participant without blocking unrelated scopes or observers.
8. Observer failures cannot change transaction outcome, and all plugin diagnostics crossing logs or
   facades are sanitized.
9. Existing B1/B2/B3, event, MCP, CLI, artifact, sidecar, cache, facts, and vector behavior remains
   compatible, with focused, restart, cancellation, no-hang, privacy, serialization, and regression
   tests covering the matrix above.
10. No remote participant, NMP/2/sidecar, archive/GC, vector change, automatic observer conversion,
    or unapproved public MCP/CLI aggregate command is included.

## 13. Self-review

The review gate specifically checks that the seven `RetryResult<T>` terminal rows are exhaustive,
that returned failures and thrown exceptions use separate exact `IRetryClassifier<T>.Classify`
overloads with typed code/flag extraction and no exception-text inference,
that cancellation distinguishes before-callback from after-callback-start, and that each row names
fields, failure code, persistence, recovery, aggregate, and observer behavior. It also checks that
`BuildAggregateProjection(requestedParticipants, input)` is the sole ordinal producer, that the
validating aggregate receives its expected sequence and only the private
`ValidatedMultiMemoryAggregateProjection` minted by `BuildAggregateProjection` through `Create`, and that
missing, extra, mismatched, duplicate, gap, and out-of-order outcomes cannot cross the Core boundary.
It verifies `RejectedTransactionalParticipants` is bounded durable metadata only and is never treated
as an executable adapter.

The document was reviewed for placeholders, contradictions, and scope drift. `Prepare`, `Commit`,
and `Rollback` are consistently defined around B3's durable decision: rollback is pre-decision only,
while post-decision recovery is commit/verification. Observer failures consistently warn and
continue, whereas participant failures are typed, bounded, and fail closed. The specification
reuses B3 rather than introducing a second coordinator, explicitly preserves current hooks, and
repeats the exclusions in the boundary, compatibility, testing, and acceptance sections to prevent
remote, format, archive, vector, or public-surface expansion. The participant-declared ordering key,
its persisted `orderingKey` representation, duplicate rejection, rehydration check, intentional
B3-before-B4 null-key ordering, contiguous global ordinal invariant, and exact global comparator are
consistent across registration, lifecycle, recovery, and aggregate projection. Retry elapsed
enforcement uses only monotonic `GetElapsed()`; `IWallClock.UtcNow` is limited to persisted audit
timestamps, with exact clock-failure codes, state-change/ambiguity fields, persistence/recovery
mapping, explicit cancellation, overflow, failure, fake-clock, and no-hang behavior. The explicit
validating result constructors are the sole authoritative construction boundary; any earlier
positional declarations are superseded.
