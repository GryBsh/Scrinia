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
- an ordering key used after registration validation.

Identity is opaque to the coordinator but compared ordinally. It must be non-empty, bounded, and
stable across process restarts and plugin instances. Display names are not identity unless the
plugin contract explicitly makes them stable. Version and capability metadata are validated before
registration and persisted in the B3 participant manifest sufficiently to validate recovery.

Registration is deterministic: candidates are validated, duplicate identities are rejected, and
the accepted set is sorted by ordinal ordering key followed by ordinal stable identity. The same
ordering is used for prepare and commit. Rollback runs in reverse accepted order, matching B3's
compensation convention. Caller registration order must never affect lifecycle order.

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
attempt count and elapsed time. Backoff and jitter, if used, are deterministic under tests and do
not permit unbounded waiting. Cancellation and shutdown interrupt waiting without converting an
unproven state into success.

Veto, permanent, and exhausted transient failures fail closed. Before the B3 decision they invoke
rollback; after the decision they invoke only commit/verification recovery. A participant that
throws an unclassified exception is mapped to a permanent or ambiguous typed failure according to
whether its state can be proven, never inferred from exception text.

Observer failures are outside this table: the existing event dispatch catches them, emits a
sanitized warning, and continues to the next observer and to the local transaction result.

## 7. Recovery and quarantine

Recovery discovers the existing B3 coordinator record and never rereads the original request.
Participant recovery uses the persisted transaction ID, operation ID, identity, capability
metadata, and opaque participant handle/evidence needed by the adapter.

- A `prepared` record takes the pre-decision abort path. Participants are rolled back in reverse
  order, with bounded retries only for declared transient failures.
- A `committed` record takes the roll-forward path. Participants are committed and verified in
  deterministic order; rollback is forbidden.
- A missing, malformed, inconsistent, unsupported, or capability-mismatched record fails closed
  for the affected scope under B3's existing recovery semantics.
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
| Ordering | Shuffled registration produces identical prepare/commit order; reverse rollback order; deterministic ties; concurrent registration/admission behavior |
| Lifecycle | Prepare success; veto; permanent failure; declared transient retry; exhausted retry; commit and proof; pre-decision rollback; post-decision no-rollback |
| Recovery | Crash/fault at each lifecycle boundary; prepared abort; committed roll-forward; idempotent replay; missing/ambiguous evidence; capability mismatch; participant quarantine and scope isolation |
| Retry/cancellation | Attempt and elapsed bounds; cancellation during callback and backoff; no retry for veto/permanent/ambiguous failures; no hangs |
| Observers | Observer failure warns and continues; observer order remains deterministic; observer failure cannot veto or roll back; observer/participant dual registration does not duplicate participant lifecycle |
| Compatibility | Existing B1/B2/B3 single- and multi-memory tests; existing event-sink and operation-hook behavior; unchanged MCP/CLI mappings; NMP/2, sidecar, archive, cache, facts, and vector regressions |
| Security/privacy | Sanitized IDs/reasons/diagnostics; control-character and path redaction; no payload/secret leakage; scope/path containment; malicious metadata and callback failures |
| Serialization | Any durable B4 metadata uses source-generated JSON; exact B3 record compatibility; trimming/AOT coverage; unknown metadata/version behavior fails closed |
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

The document was reviewed for placeholders, contradictions, and scope drift. `Prepare`, `Commit`,
and `Rollback` are consistently defined around B3's durable decision: rollback is pre-decision only,
while post-decision recovery is commit/verification. Observer failures consistently warn and
continue, whereas participant failures are typed, bounded, and fail closed. The specification
reuses B3 rather than introducing a second coordinator, explicitly preserves current hooks, and
repeats the exclusions in the boundary, compatibility, testing, and acceptance sections to prevent
remote, format, archive, vector, or public-surface expansion.
