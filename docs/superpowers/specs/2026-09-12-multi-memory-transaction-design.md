# B3 Multi-Memory Transactions — Design Specification

**Status:** Approved design  
**Date:** 2026-09-12  
**Scope:** One persistent topic scope; multiple memory participants within that scope

## Goal

B3 adds an atomic coordination protocol for a single logical operation that changes multiple
memories in one persistent topic scope. Readers must observe either the pre-transaction state or
the committed state, never a participant-by-participant mixture. Recovery must make a durable
decision and finish the decision after a process crash, without reopening the source request.

B3 coordinates existing participant transactions. It does **not** replace or duplicate participant
payload journals. Each participant continues to own its payload, validation, replay, rollback, and
cleanup evidence through the B2 adapters.

## Boundary: one topic scope

A transaction has exactly one resolved persistent scope, such as `local-topic:notes`. Every
participant address must resolve to that same scope before admission. The coordinator rejects an
empty participant set, duplicate canonical subjects, ephemeral addresses, file-entity paths, and
any participant whose resolved scope differs from the transaction scope.

The scope boundary is also the isolation boundary: the scope-local lock, recovery barrier, cache,
facts index, and coordinator decision record apply only to that scope. A failure or retained record
in one topic must not block another topic or ephemeral memory operations.

## Durable coordinator decision record

The coordinator writes one private record at the root of the affected persistent scope:

```text
<scope>/.multi-memory-transaction.json
```

This record is control data and is excluded from memory enumeration, bundle export, sidecar
enumeration, facts results, and ordinary search. It is written with the existing durable atomic
replacement primitive and retained whenever recovery cannot establish or complete the decision.

The minimal supported record version is **version 1**. The record is source-generated JSON with
these exact members:

```json
{
  "format": "scrinia.multi-memory-transaction",
  "version": 1,
  "transactionId": "01J...",
  "scope": "local-topic:notes",
  "state": "prepared",
  "participants": [
    { "subject": "note-a", "operationId": "01J...", "kind": "memory" }
  ],
  "createdAt": "2026-09-12T00:00:00Z",
  "updatedAt": "2026-09-12T00:00:00Z"
}
```

`state` is one of `prepared`, `committed`, or `recoveryRequired`. The record contains participant
identity and operation IDs, but not participant payloads, artifact bodies, sidecars, hashes, or
rollback snapshots. `prepared` means no durable commit decision has been published. `committed`
means the coordinator has durably decided that all participants must be committed. A malformed,
inconsistent, unsafe, or unrecoverable record is retained and represented as `recoveryRequired` by
the typed recovery boundary; the malformed bytes are never rewritten merely because they were read.

Migration/version policy is intentionally minimal: B3 ships only record version 1, has no migration
writer, and does not rewrite older or unknown records. A record declaring a version newer than 1
blocks only its scope with a typed unsupported-version outcome. A future version must introduce an
explicit adapter and acceptance tests before it is accepted; silent field defaulting is forbidden.

## Identity and operation IDs

`transactionId` is a non-empty, caller-independent durable identity generated at admission. Each
participant has a canonical subject derived from its validated `MemoryAddress` and a non-empty
`operationId` supplied by the participant operation or its existing B2 evidence. IDs are opaque
strings: they are compared ordinally, not parsed as timestamps or filesystem names.

The tuple `(scope, transactionId)` identifies the coordinator record. Within a transaction,
`(subject, operationId)` identifies a participant attempt. Replaying the same decision is
idempotent; a participant must not receive a second payload mutation for an already verified commit.

## Deterministic ordering and locking

Admission canonicalizes and validates all participants before acquiring mutation locks. Participants
are sorted by ordinal canonical subject, then ordinal operation ID. The coordinator acquires the
scope lock once and invokes participants in that order. No participant may acquire another scope
lock, and no lock is acquired in caller-provided order. This prevents deadlocks and makes journal,
cache, facts, and outcome ordering deterministic.

The existing scope recovery barrier runs before admission and remains held across prepare, decision
publication, commit, verification, and coordinator cleanup. Reads entering the scope wait for the
barrier and never observe an in-progress participant set.

## Admission and B2 adapter reuse

Admission first runs the existing B2 discovery/admission path for the target scope. B2’s operation
adapters remain the sole authorities for participant payload validation and evidence transitions.
The B3 coordinator receives typed participant handles/results, never raw JSON, exception text, or
filesystem guesses.

The coordinator rejects a participant if its operation is not supported by a B2 adapter, its subject
or scope is inconsistent, its operation ID is blank or conflicts with another participant, or its
existing recovery evidence is blocked. B3 may create or update the coordinator decision record, but
it must not create a second journal for a participant payload.

## Prepare and commit lifecycle

1. Resolve one scope and canonicalize, deduplicate, and deterministically order participants.
2. Run scope recovery and B2 admission; refuse before mutation if any participant is unsafe,
   ambiguous, malformed, newer than supported, or otherwise blocked.
3. Ask every participant adapter to prepare its existing payload journal. Each participant retains
   its own B2 location and format.
4. Atomically publish the coordinator record as `prepared`, including the complete ordered
   participant manifest. A missing or unverifiable record means there is no B3 commit decision.
5. Atomically replace the coordinator record as `committed`. This is the linearization point:
   after it succeeds, every participant must be rolled forward to its committed state.
6. Commit participants in deterministic order, verify their final bytes and indexes, and invalidate
   scope-local derived state only after each verified participant mutation.
7. Remove the coordinator record only after every participant is verified committed and cleanup is
   complete. Cleanup failure is a typed post-commit warning; it does not undo the decision.

Before the `committed` decision, a participant failure causes best-effort rollback through the B2
adapter and leaves the coordinator record only if rollback or cleanup remains unresolved. After the
decision, rollback is forbidden: recovery retries commit/verification until it can report success
or a retained recovery block.

## Read visibility

Reads in the affected scope pass through the existing recovery barrier. They cannot read between
participant commits. A `prepared` coordinator record is resolved before a read proceeds: the
transaction is aborted/rolled back when safe, or the scope is blocked if safety cannot be proven.
A `committed` record is resolved by participant roll-forward and verification before cached or
on-disk state is returned. A `recoveryRequired` or unsupported record fails closed for that scope.

No cross-scope snapshot or workspace-wide read barrier is introduced.

## Restart and crash recovery

On process restart, the first read or write entering the scope discovers the coordinator record
before loading cache or accepting mutation. Recovery validates format, version, scope, participant
manifest, identity, and exact participant evidence locations. It never rereads the original request.

If the decision is `prepared`, recovery performs the pre-decision abort path and verifies that no
participant remains committed by the B3 operation. If the decision is `committed`, recovery invokes
the B2 adapters to finish or verify every participant in sorted order. Only a fully verified result
permits coordinator-record cleanup.

A crash during record replacement is handled by the existing durable-file guarantees: the prior
complete bytes or the new complete bytes are read. An incomplete, malformed, or conflicting record
is retained and blocks only the scope. Cleanup is retryable and must not replay business mutation
after verified commit.

## Failure classes and ambiguous participants

Failures before the durable commit decision are non-committed outcomes: admission failure,
prepare failure, coordinator-record publication failure, or rollback failure. Failures after the
decision are committed-but-incomplete outcomes: participant commit failure, verification failure,
or cleanup failure.

An ambiguous participant is any participant for which the adapter cannot prove whether its payload
is prepared, committed, rolled back, or belongs to this transaction—for example, missing evidence,
conflicting operation IDs, mismatched scope/subject, unsafe paths, or an I/O error during proof.
Ambiguity is never guessed in favor of success. Before decision it blocks rollback completion; after
decision it blocks roll-forward completion. The retained evidence and exact safe paths identify the
required recovery action.

## Aggregate typed outcomes

Core returns an immutable aggregate outcome containing transaction ID, scope, lifecycle result,
ordered participant outcomes, and one overall classification. Participant outcomes retain their
operation kind, subject, operation ID, evidence path, committed/rolled-back status, and typed reason.
The aggregate classifications are:

- `committed`: all participants committed and verified;
- `committedCleanupPending`: the decision and participant state are verified, but cleanup remains;
- `rolledBack`: no participant remains committed and the pre-decision transaction is closed;
- `recoveryRequired`: safe completion cannot be established; evidence is retained;
- `rejected`: admission failed before a transaction was published.

Facades map these typed results to existing MCP/CLI success, recovery-required, and blocked
responses. They must not infer transaction state from journal-file existence or exception messages.

## Cache and facts invalidation

The coordinator invalidates the affected scope’s cached index and facts/entity projections as one
post-commit scope action after all participant final bytes are verified. The invalidation is
idempotent and may be retried after a crash. A pre-decision rollback invalidates only if a
participant’s observable state changed, using the existing store rules.

No new cache or facts persistence format is introduced. Other scopes are not invalidated.

## MCP and CLI compatibility

Existing MCP tool names, CLI command names, successful payload shapes, output wrappers, correlation
semantics, and exit classes remain unchanged. Existing callers that perform one memory operation
continue to use the same participant path and behavior. B3 adds only internal Core contracts and,
where an already supported multi-memory entry point exists, renders its typed aggregate through the
existing response fields without exposing a new remote or HTTP surface.

## Serialization and preserved B2 formats

All B3 coordinator records and typed result DTOs use source-generated `System.Text.Json` metadata.
Reflection-based serialization, `object` payloads, untyped reserialization, and exception-message
parsing are prohibited.

B2 physical locations and wire formats are preserved exactly:

- Restore evidence remains `<scope>/.restore-journal.json`.
- Append evidence remains `<memory>/versions/.append-journal.json` in the existing scope-local
  memory layout.
- File-entity evidence remains `<scope>/.file-entity-{id}.recovery`.

The common B2 recovery envelope remains `format: "scrinia.recovery"`, `version: 1`, with its
operation, body version, operation ID, scope, lifecycle, timestamps, optional address, and typed
operation body. Legacy B2 evidence is normalized in memory and is not rewritten merely by discovery.
B3 adds only the coordinator decision record described above; it does not alter NMP/2, ordinary
artifact metadata, ordinary sidecars, or participant payload formats.

## Security, path safety, and privacy

All addresses and evidence paths are parsed as logical addresses or opaque artifact keys, then
resolved by the store. Evidence paths are canonicalized and must remain contained by the expected
scope and participant layout before any read or write. Symlink/reparse escape, traversal, absolute
foreign paths, mismatched filenames, duplicate subjects, and scope confusion are typed unsafe or
inconsistent failures.

Coordinator and participant journals are private control data. Responses expose only safe,
canonicalized evidence paths and validated IDs; raw payloads, sidecar contents, secrets, and foreign
roots are not copied into aggregate errors or logs. Transaction records inherit existing workspace
permissions and atomic-write protections.

## Tests and acceptance gates

Acceptance requires focused tests for:

1. one-topic admission, duplicate identity, ephemeral/cross-scope rejection, and path containment;
2. deterministic ordering and lock acquisition under shuffled input and concurrent callers;
3. exact version-1 coordinator JSON and source-generated serialization/trimming coverage;
4. prepare, decision publication, commit, verification, cleanup retry, and idempotent replay;
5. every pre- and post-decision failure class, including coordinator-write interruption;
6. ambiguous participant isolation and fail-closed retained evidence;
7. cache/facts invalidation only after verified aggregate commit;
8. existing single-memory, MCP, CLI, B2 journal, NMP/2, sidecar, and B1 contamination regressions.

The acceptance gate rejects any source/test/doc change outside the B3 implementation plan, any
participant payload-journal duplication, any cross-scope lock or transaction, any reflection JSON,
any public compatibility break, or any cleanup path that rolls back a verified decision.

## Explicit exclusions

B3 does not include:

- multi-scope or workspace-wide transactions;
- plugin hooks, event publication, or event transactional semantics;
- archive policy, version retention policy, or garbage collection;
- NMP/2 changes or ordinary artifact/metadata/sidecar changes;
- replacement or duplication of participant payload journals;
- B1 contamination cleanup or reinterpretation of unrelated recovery evidence;
- HTTP/REST, remote CLI, daemon, or fleet-wide coordination.

## Task decomposition and split criteria

The implementation should proceed in dependency order:

1. **Contracts and format:** version-1 coordinator DTOs, typed states/outcomes, generated JSON
   metadata, and exact record validation.
2. **Admission and ordering:** one-scope participant resolution, identity rules, deterministic
   ordering, and scope lock/barrier integration.
3. **B2 orchestration:** prepare/decision/commit adapters that call existing participant journals
   without copying their payloads.
4. **Recovery and visibility:** restart handling, read barrier behavior, ambiguity classification,
   and idempotent cleanup.
5. **Derived state and facades:** cache/facts invalidation plus unchanged MCP/CLI mappings.
6. **Acceptance:** failure matrix, concurrency, serialization, compatibility, and B1 regression
   coverage.

Split into separate plans if the coordinator record contract cannot remain independent of the B2
adapter seam, if cache/facts invalidation requires a new durable format, or if facade compatibility
requires a public response-shape change. Split a task further when it mixes decision durability
with participant payload-format changes. Do not split by participant operation if that would create
parallel coordinators or duplicate lifecycle logic; adapters must remain behind one coordinator.

## Self-review

This specification contains no placeholders. Version policy, decision point, visibility rules,
ambiguous handling, preserved locations, and exclusions are explicit and consistent: B3 owns one
scope-local decision record, while B2 owns every participant payload journal. The design is limited
to one architectural slice and does not smuggle in migration, archive, plugin, event, NMP/2,
sidecar, or B1 cleanup work.
