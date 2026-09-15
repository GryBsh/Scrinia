# B5 Retention, Pruning, and Recovery Maintenance — Design Specification

**Status:** Approved design
**Date:** 2026-09-15
**Predecessors:**

- `docs/superpowers/specs/2026-09-10-phase-b-roadmap-design.md`
- `docs/superpowers/specs/2026-09-11-journal-convergence-design.md`
- `docs/superpowers/specs/2026-09-12-multi-memory-transaction-design.md`
- `docs/superpowers/specs/2026-09-13-b4-plugin-event-boundary-design.md`

**Scope:** Per-workspace archive retention, bounded journal cleanup, and local operator maintenance

## 1. Goal and governing decisions

B5 provides one Core-owned maintenance service for pruning eligible historical archive material and
cleaning up completed recovery evidence. The service is callable by an explicit CLI maintenance
command and by a bounded background adapter. Both callers use the same lock-aware Core path and
receive the same typed result; neither caller implements deletion rules.

The following decisions are normative:

- Retention policy is configured per workspace and has conservative defaults.
- Unresolved, malformed, ambiguous, unsupported, or otherwise recovery-required transaction
  evidence is retained indefinitely until an explicit operator resolution exists. B5 never deletes
  evidence merely because it is old.
- Automatic background cleanup is bounded by a fixed batch and time budget. It is best effort and
  never becomes unbounded work, a daemon, or a prerequisite for ordinary reads and writes.
- An explicit operator maintenance command can request another bounded batch and can repeat it until
  the reported work is complete.
- Maintenance uses the existing scope-local exclusive lock and the existing in-process index lock;
  it does not introduce a workspace-wide lock or a second lock hierarchy.
- Current, active, referenced, and explicitly protected entries are skipped. Eligibility is
  revalidated while the scope lock is held immediately before deletion.
- Invalid policy fails closed: no archive or journal deletion is attempted, and the result identifies
  the invalid policy through the existing configuration error vocabulary.
- Lock contention is retryable and typed. It is not reported as successful maintenance and does not
  cause deletion outside the lock.
- A deletion failure retains the candidate and produces a warning. It does not turn a successful
  deletion of another candidate into a rollback, and it does not hide the failure.
- B5 does not add a remote API, archive index, NMP/2 or ordinary sidecar format, transaction format,
  plugin lifecycle, or separate CLI deletion logic. Work is always bounded.

## 2. Existing architecture and ownership

`FileMemoryStore` is the filesystem-backed store. Persistent memory data is organized by resolved
scope, with an artifact and sidecar for each entry and a `versions/` directory for historical
archive pairs. `FileMemoryStore.WithExclusiveLock` acquires the scope's `.lock`, enters the
in-process `ReaderWriterLockSlim`, performs recovery, and then runs the operation. The B5 service
must use this boundary or a narrow internal equivalent; it must not acquire a raw lock and then call
another store operation that acquires the same lock.

The B2 recovery coordinator discovers append, restore, and file-entity evidence under the same
scope-local boundary. B3 adds the scope-root `.multi-memory-transaction.json` decision record, and
B4 adds participant state to that record while retaining observer-only event semantics. These
records are private control data, not archive candidates. `DurableFile` remains the primitive for
durable replacement; maintenance deletes only already-identified files and never rewrites a journal
or archive to make it eligible.

The proposed ownership is:

| Concern | Owner |
|---|---|
| Policy parsing, validation, defaults, and typed errors | `Scrinia.Core.Configuration` / Core maintenance contracts |
| Candidate discovery and protection classification | Core maintenance service |
| Scope lock, recovery barrier, cache/index invalidation | `FileMemoryStore` existing boundary |
| File deletion and bounded retry | Core maintenance service using existing filesystem/resilience primitives |
| CLI rendering and exit mapping | existing CLI facade; it passes the request and renders the Core result |
| Background scheduling and shutdown | existing local host / `BackgroundWork` adapter |
| Plugin and event observation | unchanged B4 observer path; never a cleanup prerequisite |

There is one maintenance decision path. The CLI and background adapter are transport/scheduling
callers, not alternative policy engines.

## 3. Workspace policy schema and defaults

The workspace configuration remains the existing flat `.scrinia/config.json` dictionary. B5 adds no
nested configuration document and no new metadata file. The following keys are the complete B5
policy surface; absent keys use the stated defaults.

| Key | Value | Default | Meaning |
|---|---|---:|---|
| `Scrinia:Retention:MaxArchivedVersions` | positive integer | `10` | Maximum retained archive pairs per subject after eligible pruning; current and protected pairs are additional and are never displaced by this limit. |
| `Scrinia:Retention:MinimumAgeDays` | non-negative integer | `30` | An archive pair must be at least this old before age-based deletion. |
| `Scrinia:Maintenance:JournalCleanupBatchSize` | positive integer | `50` | Maximum recovery-evidence candidates inspected/deleted by one maintenance invocation. |
| `Scrinia:Maintenance:TimeBudgetSeconds` | positive integer | `5` | Maximum maintenance work time after admission; the service returns a continuation indication when the budget expires. |
| `Scrinia:Maintenance:BackgroundEnabled` | `true` or `false` | `true` | Enables the local background adapter. It does not affect explicit operator maintenance. |

The defaults are conservative: existing archive behavior already keeps ten versions, cleanup does
not consider a version younger than thirty days, each invocation examines at most fifty journal
candidates, and background work is small and time-bounded. Defaults preserve data rather than
optimizing disk use. A policy value is valid only when its complete lexical and range rules pass:
integers are decimal, non-negative where stated, and within implementation-safe bounds; booleans are
exactly lowercase `true` or `false`; and unknown B5 keys are ignored as they are by the existing flat
configuration model. A present empty value, malformed number, overflow, or invalid boolean makes the
policy invalid.

The service reads the effective policy once before candidate planning. It does not mix values from
multiple configuration snapshots within one batch. Configuration changes affect the next batch, not
one already-running deletion decision.

## 4. Candidate and protection model

Maintenance operates on typed candidates, not arbitrary filesystem paths supplied by a caller. A
candidate contains:

- resolved persistent scope and canonical subject;
- candidate kind (`archivePair` or `completedRecoveryEvidence`);
- canonical body path and optional paired sidecar path;
- creation or completion timestamp used only for eligibility;
- operation/transaction identity when the candidate is recovery evidence; and
- a protection classification and an eligibility reason.

Candidate discovery is scope-local and deterministic. Archive candidates are discovered from the
existing `versions/` layout and paired by the existing naming convention. Recovery candidates are
the existing append, restore, file-entity, and B3 coordinator evidence locations. The coordinator's
participant state and B4 recovery/quarantine state are read through existing typed recovery
contracts; B5 does not parse raw JSON or exception messages.

The protection predicate is evaluated before any deletion:

```text
protected(candidate) =
    current(candidate)
    OR active(candidate)
    OR referenced(candidate)
    OR explicitlyProtected(candidate)
    OR unresolvedRecovery(candidate)
    OR unsafeOrUnverifiable(candidate)
```

Definitions:

- **Current** means the live artifact or live artifact/sidecar pair for the subject. A current pair is
  never an archive candidate, and a missing or mismatched pair is protected rather than repaired by
  maintenance.
- **Active** means evidence whose lifecycle is prepared, applying, recovery required, ambiguous,
  quarantined, or otherwise not proved complete by the owning B2/B3/B4 contract. A
  `cleanup-pending` record is protected until the owning contract has separately proved its terminal
  state and absence of references; only then can it be a completed-evidence cleanup candidate.
- **Referenced** means a path or identity required by retained transaction evidence, a participant
  completion proof, a live archive/sidecar pairing, or another existing recovery/rollback contract.
  References are resolved through typed records and contained-path validation.
- **Explicitly protected** means an operator or future approved Core caller has marked the candidate
  protected through the maintenance request. B5 does not invent a user-facing protection file or
  sidecar field.
- **Unresolved recovery** includes every retained transaction/recovery record for which the system
  cannot prove a terminal, verified state. Age and disk pressure never override this protection.
- **Unsafe or unverifiable** includes malformed, newer-than-supported, ambiguous, inconsistent,
  missing, inaccessible, or path-containment-failing evidence. It is retained and reported.

Completed recovery evidence is eligible only when the owning adapter has verified the committed
state, the relevant live bytes, index consequences, and any required sidecar pairing, and has marked
the evidence as cleanup-pending/terminal in a typed result. B5 may then remove the completed evidence
according to its cleanup rule; it may not manufacture a terminal state.

## 5. Eligibility and deterministic bounded batches

The service processes one resolved persistent scope at a time. A request may target one scope or the
resolved workspace set, but workspace-wide processing is a sequence of independent scope batches,
not a workspace transaction. Scopes are ordered ordinally. Within a scope, candidates are ordered
by kind, canonical subject, timestamp, and canonical path using ordinal comparison. Ties are broken
by the complete canonical relative path; equal identities are treated as inconsistent and retained.

Archive eligibility requires all of the following:

1. the candidate is a valid existing archive pair in the existing layout;
2. it is older than `MinimumAgeDays`;
3. the subject has more than `MaxArchivedVersions` eligible retained pairs;
4. it is not current, active, referenced, explicitly protected, or unverifiable; and
5. its body and sidecar paths remain contained by the expected scope `versions/` directory.

The service never deletes an archive body without its sidecar when a sidecar exists, and never
deletes a sidecar belonging to a non-deleted body. If the pair is incomplete or the pair state
cannot be proved, both members are protected and a warning is returned.

Journal cleanup eligibility requires a verified terminal result and no unresolved participant,
rollback, quarantine, or referenced evidence. Prepared, applying, recovery-required, rolled-back
with retained proof, cleanup-pending without terminal proof, malformed, unsupported, and ambiguous
records remain until the owning recovery path explicitly proves they are no longer needed. B5 does
not delete `.lock` or staging files, and does not delete coordinator records or journal members
through filename age alone; a verified terminal coordinator or journal record may be removed only
through the typed cleanup decision.

The service stops when it reaches the batch size, time budget, cancellation, or a scope boundary.
It returns counts and a continuation flag. A later invocation may continue from the deterministic
start order; deletion is idempotent because an already absent eligible member is treated as complete
only if the paired-state recheck proves the absence is expected.

## 6. Data flow

1. The CLI or background adapter constructs a typed maintenance request containing workspace/scope
   selection, optional batch override within the configured safety ceiling, and cancellation.
2. Core resolves the workspace and reads the flat policy. Invalid policy returns before lock
   acquisition or candidate deletion.
3. Core resolves the target scope list and acquires the existing scope-local exclusive lock and
   in-process write lock. Lock acquisition uses the existing retry/timeout behavior.
4. The existing recovery barrier runs first. Any unresolved or blocking recovery result is retained,
   reported, and excluded from deletion; maintenance does not bypass or reinterpret B2/B3/B4
   recovery.
5. Under the lock, Core discovers archive pairs and completed evidence, canonicalizes paths, loads
   only bounded metadata needed for protection, and builds the deterministic candidate list.
6. Core revalidates each candidate immediately before deleting it: existence, pair membership,
   current state, references, protection state, age, containment, and the policy threshold are all
   checked against the locked filesystem view.
7. Core deletes eligible members using the existing filesystem/resilience conventions. A failure
   retains the candidate, records a sanitized warning, and continues within the batch where safe.
8. Core invalidates affected index/cache/topic/facts projections only after a deletion that can
   affect an existing derived view. Invalidation is scope-local and idempotent. If no live memory
   state changed, no derived state is rebuilt merely for maintenance.
9. Core releases the scope lock and returns a typed aggregate result. The CLI renders it through the
   existing output wrapper; the background adapter logs a bounded warning and schedules no recursive
   retry from inside the service.

Background scheduling uses the existing detached-work ownership and drain behavior. It may enqueue
one maintenance batch per scheduling interval, but it must not create one work item per candidate or
hold work past host shutdown. A short-lived CLI invocation explicitly drains its already-admitted
background work as it does today; maintenance itself remains local and bounded.

## 7. Races, revalidation, and recovery safety

The lock is the serialization boundary with normal store writes, reads that escalate for recovery,
and another maintenance invocation in another process. Candidate discovery outside the lock is not
permitted to make a deletion decision; if a future optimization performs a hint scan before locking,
the hint is discarded and the complete candidate/protection check is repeated under the lock.

The critical race cases are handled as follows:

- A writer creates or replaces a live entry after discovery: the locked revalidation sees the
  candidate as current or referenced and skips it.
- A writer begins a transaction after discovery: the existing lock/recovery boundary serializes
  admission; the revalidation sees active evidence and skips it.
- A concurrent process deletes or changes an archive: missing or changed pair state is retained as
  a no-op or warning, never replaced by a guessed deletion.
- A cleanup failure occurs after one member is deleted: the remaining member is retained and the
  result warns about an incomplete pair; the next run verifies the pair before acting.
- A process stops during deletion: the next maintenance or normal recovery path treats incomplete,
  mismatched, or unverifiable evidence as protected. No cleanup operation is allowed to turn an
  ambiguous state into success.
- A scope lock times out: no candidate is deleted; the result is retryable and the other scopes in a
  multi-scope request may be reported independently if they were processed before the contention.
- Cancellation occurs: the current deletion is allowed to return its ordinary filesystem outcome,
  then the service stops starting new candidates and retains all unprocessed candidates.

Maintenance never rolls back a verified B1/B2/B3/B4 decision and never invokes a plugin lifecycle
callback. B4 observers may observe ordinary maintenance-related store events only if an existing
event contract already requires it; observer failure remains a warning and cannot veto deletion.

## 8. Outcomes and errors

Core returns immutable typed results with per-scope counts, candidate summaries without content,
continuation state, warnings, and a top-level classification. The result distinguishes:

- `completed`: the bounded request inspected all work in its requested range;
- `moreWork`: the safety bound stopped the request and another bounded invocation can continue;
- `policyInvalid`: no deletion was attempted;
- `lockContended`: the scope could not be acquired within the retry budget;
- `recoveryRetained`: recovery evidence was found and retained because it is unresolved or
  unverifiable; and
- `deletionWarning`: one or more eligible deletions failed, while all successful deletions remain
  successful and failed candidates remain for retry.

These classifications are orthogonal where necessary: a result can be `moreWork` with warnings, or
`completed` with `recoveryRetained`. A deletion warning is never converted into a successful count
for the failed path. The result contains safe scope labels, canonical relative evidence/archive
names, counts, and typed reasons; it does not contain memory content, NMP/2 bytes, sidecar contents,
secrets, arbitrary exception text, or foreign absolute paths.

The CLI adds one explicit operator maintenance command under the existing local CLI surface. Its
command handler only resolves arguments, calls the Core service, and renders the shared result. It
does not enumerate, classify, or delete files itself. Invalid policy and invalid arguments use the
existing configuration/argument failure classes; lock contention is retryable and non-success;
warnings are visible in both human and structured output. No MCP maintenance tool or remote surface
is added by this design.

## 9. Security and privacy

Maintenance is destructive, so path safety is fail-closed. Every discovered path is derived from a
validated logical scope and canonical subject, canonicalized, and checked for containment in the
expected store or `versions/` directory before it is opened or deleted. Reparse-point/symlink
escapes, traversal, absolute foreign paths, scope mismatches, filename/identity mismatches, and
unexpected file kinds are protected and reported, not normalized into a deletion path.

The service runs with the workspace's existing filesystem permissions and the existing scope lock.
It does not elevate privileges, invoke Git, contact a network service, or grant plugins deletion
authority. Operator output is sanitized to safe relative names and stable typed reasons. Background
logs contain counts and safe scope/evidence identifiers only. Unresolved evidence is retained even
when it contains sensitive transaction metadata; B5 never copies that metadata into a new report or
archive index.

## 10. Compatibility and migration

B5 preserves the existing physical and logical contracts:

- NMP/2 artifacts, chunk semantics, and encoding remain unchanged.
- Ordinary sidecar fields, skill/agent sidecars, attribute migration, and metadata serialization
  remain unchanged.
- Existing archive directories, archive naming, artifact/sidecar pairing, and current ten-version
  behavior remain the compatibility baseline; B5 centralizes future pruning behind the policy gate.
- B2 journal locations and the common recovery envelope remain unchanged: restore
  `.restore-journal.json`, append `versions/.append-journal.json`, and file-entity
  `.file-entity-{id}.recovery`.
- The B3 coordinator record remains scope-root `.multi-memory-transaction.json`, including B4
  participant state and quarantine evidence. B5 never rewrites it as a different format.
- Ephemeral memories remain in-process and outside persistent archive/journal maintenance.
- Existing MCP names, CLI names, successful payloads, correlation semantics, and normal store
  behavior remain unchanged except for the explicitly added local operator maintenance command.
- Configuration continues to use the flat workspace dictionary and existing durable atomic writes;
  no configuration migration is needed. Absent B5 keys use defaults.
- There is no archive index migration. Candidate discovery is bounded filesystem enumeration using
  the existing layout.

## 11. Testing matrix

The implementation plan must provide focused tests for each row below and retain the existing B1–B4
regression suites unchanged unless a test is extended solely to prove compatibility.

| Area | Required coverage |
|---|---|
| Policy | Every default; valid boundary values; malformed, empty, negative, overflow, and invalid boolean values; one consistent snapshot per batch; fail-closed with zero deletion. |
| Candidate discovery | Existing archive pairs; incomplete pairs; all B2 locations; B3 coordinator records; B4 admitted/rejected/quarantined state; deterministic ordering; bounded metadata reads; control files excluded from archive enumeration. |
| Protection | Current, active, referenced, explicitly protected, unresolved, malformed, newer, ambiguous, unsafe, and unverifiable candidates are all skipped and reported. |
| Retention | Ten-version compatibility default; age threshold; over-limit pruning; younger versions preserved; pair deletion; no body-without-sidecar deletion; no current-entry deletion. |
| Journals | Verified terminal evidence may be cleaned; unresolved evidence survives indefinitely; cleanup retry; cleanup failure warning; no filename-age-only deletion; B2/B3/B4 physical locations remain byte-compatible. |
| Locking and races | Existing scope lock is used once; concurrent writer/reader/maintenance serialization; lock timeout is typed and retryable; one blocked scope does not block an independent scope; revalidation defeats stale candidates. |
| Bounds | Batch cap, time budget, cancellation, continuation, deterministic repeated invocation, and no per-candidate unbounded task creation. |
| Failures | Permission/IO failure retains and warns; partial pair failure; process-stop/restart safety; recovery barrier failure; no rollback of verified transactions. |
| Derived state | Scope-local cache/index/topic/facts invalidation after relevant deletion only; other scopes unchanged; no unnecessary rebuild. |
| Facades | Shared Core result mapping through human and structured CLI output; stable safe diagnostics; invalid argument/configuration and retryable lock outcomes; no separate CLI deletion path. |
| Host | Background adapter scheduling, shutdown/drain, disabled background policy, detached-work fault isolation, and no maintenance dependency on plugins or event sinks. |
| Compatibility | Existing append/restore/file-entity recovery, B3 coordinator, B4 participant, NMP/2, sidecar, archive, CLI, source-generation, trimming, and privacy tests. |

Acceptance also requires focused build/test evidence, the ordinary full solution test command with
exit status and no-hang evidence, relevant trimmed/source-generated serialization checks, a clean
scoped diff, and independent review against the exact B5 implementation diff.

## 12. Acceptance criteria

B5 is accepted only when all of the following are true:

1. A workspace can use the documented flat policy keys, receives the conservative defaults, and
   rejects invalid policy without deleting anything.
2. One Core maintenance service is shared by explicit operator maintenance and background cleanup.
3. Every maintenance decision is made under the existing scope-local lock/barrier and revalidated
   immediately before deletion.
4. Current, active, referenced, explicitly protected, unresolved, malformed, unsupported,
   ambiguous, unsafe, and unverifiable entries are retained.
5. Archive pruning preserves artifact/sidecar pairing and never removes current state.
6. Verified terminal journal evidence can be cleaned in bounded batches, while unresolved evidence
   is retained indefinitely until explicitly resolved.
7. Batch size, time budget, cancellation, and continuation prevent unbounded automatic or operator
   work.
8. Lock contention is retryable and typed; deletion failures retain candidates and produce warnings.
9. Recovery, cache, index, facts, B3, and B4 semantics are not weakened, duplicated, or bypassed.
10. Output contains safe operational diagnostics only, with no memory content or secret leakage.
11. Existing local-only CLI/MCP, NMP/2, sidecar, archive layout, source-generation, trimming, and
    plugin observer contracts remain compatible.

## 13. Explicit non-goals

B5 does not include:

- a remote API, HTTP/REST service, remote CLI, web UI, daemon, or fleet-wide maintenance/reporting;
- an archive index, new archive manifest, or new sidecar/protection format;
- changes to NMP/2, ordinary sidecars, bundle formats, migration formats, or recovery journal wire
  locations;
- a new transaction coordinator, transaction state machine, journal writer, or recovery parser;
- deletion or auto-resolution of unresolved, ambiguous, malformed, unsupported, or quarantined
  evidence;
- multi-scope atomic maintenance or a workspace-wide lock/barrier;
- separate CLI deletion/classification logic;
- plugin transactional participation, plugin lifecycle changes, observer promotion, or plugin-required
  cleanup;
- unbounded scans, unbounded queues, one task per file, or automatic retries that outlive the host;
- automatic repair of incomplete artifact/sidecar pairs or guessing the meaning of unknown files;
- changing the semantics of existing `forget`, `compact`, append, restore, import, migration, or
  bundle operations.

## 14. Self-review

The specification contains no placeholders. Policy keys, defaults, ranges, protection rules,
ownership, lock boundary, race handling, result classes, privacy behavior, compatibility constraints,
testing requirements, and acceptance criteria are explicit. The design consistently keeps unresolved
evidence indefinitely while allowing only verified terminal evidence to be cleaned. It also keeps
background and operator work bounded and routes both through one Core service, so the CLI cannot
diverge from background deletion behavior.

The scope is one implementation slice: policy/configuration, Core candidate maintenance, one local
CLI adapter, and one bounded background adapter. It does not smuggle in B6 format evolution, a new
transaction system, plugin lifecycle work, remote deployment, or a workspace-wide coordination
mechanism.
