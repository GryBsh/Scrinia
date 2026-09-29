# Append Hardening — Phase A Design Specification

**Status:** Approved for implementation planning
**Date:** 2026-09-08
**Scope:** Phase A only

## 1. Purpose

`append` must preserve memory integrity when any part of the read, derivation,
archive, artifact write, sidecar write, pruning, event, cancellation, or
recovery path fails. The current write sequence can publish a new artifact
before derived metadata is known to be writable, and failures can escape as a
generic invocation error. This design gives append one transactional boundary,
one typed result contract, and a recoverable journal without changing the
on-disk memory format.

Phase A is deliberately an append-hardening slice. It does not redesign all
memory writes or introduce a general transaction framework. Phase B is listed
explicitly in Section 18 and is not part of this work.

## 2. Goals and non-goals

### Goals

- Make every append outcome explicit to MCP and CLI callers.
- Ensure an interrupted or failed append leaves either the complete old state,
  the complete new state, or a journal that deterministically restores one of
  those states.
- Roll back using captured raw bytes, not by decoding and re-encoding data.
- Archive the exact pre-append bytes and metadata selected by an immutable plan.
- Keep artifact and sidecar publication ordered and observable.
- Make create-via-append follow the existing `StoreAsync` creation behavior.
- Make recovery automatic at the store boundary and safe to retry.
- Provide deterministic fault seams for every durability boundary.
- Keep event-hook failures from corrupting a committed append.

### Non-goals for Phase A

- No change to NMP/2, sidecar schema, index schema, or archive naming rules
  except the append journal described here.
- No conversion of `remember`, `compact`, import, migration, or bundle writes
  to the new transaction mechanism.
- No general-purpose transaction abstraction for all store operations.
- No change to plugin event semantics beyond the append result and warning
  contract.
- No distributed locking or cross-process transaction coordinator.
- No attempt to make arbitrary external event sinks transactional.

## 3. Terminology and invariants

An append has two durable memory members:

1. the NMP/2 artifact bytes; and
2. the JSON sidecar bytes.

The **old pair** is the state visible before the operation. The **new pair**
is the state produced from the old content plus the appended content. A pair is
publishable only when both members are present and their journal-recorded
hashes match.

The following invariants apply to persistent memories:

- The old bytes are captured before either member is changed.
- The journal is durable before the old pair is archived or the new pair is
  published.
- The journal records immutable bytes/hashes and paths; recovery never
  reconstructs rollback bytes from decoded objects.
- A committed new pair is never replaced by an old pair merely because archive
  cleanup or an event failed.
- A result marked `success` means the new pair is durable and verified. A
  result marked `success_with_warning` means the new pair is durable and the
  warning is actionable but does not invalidate the append.
- If neither pair can be proven complete, the operation returns
  `RECOVERY_REQUIRED` and leaves the journal and evidence intact.

Ephemeral memories do not use the persistent archive or append journal. They
retain their existing in-process behavior, but use the same typed result and
event/cancellation rules where those rules apply.

## 4. Public store contract

The store-facing append operation returns a typed result rather than only an
`ArtifactEntry`:

```csharp
public sealed record AppendStoreResult(
    AppendStoreOutcome Outcome,
    ArtifactEntry? Entry,
    string OperationId,
    AppendStoreWarning? Warning = null,
    AppendRecoveryInfo? Recovery = null);

public enum AppendStoreOutcome
{
    Created,
    Appended,
    SuccessWithWarning,
    RecoveryRequired
}

public sealed record AppendStoreWarning(
    AppendWarningCode Code,
    string Message);

public enum AppendWarningCode
{
    PostCommitCleanupFailed,
    EventFailed
}
```

`Created` identifies create-via-append; `Appended` identifies a normal append;
`SuccessWithWarning` identifies a durable commit with a non-fatal warning; and
`RecoveryRequired` is returned when the store can still return a structured
status and recovery information. `OperationId` is stable for the lifetime of
one attempted append and is also the journal correlation key. A retry after
recovery gets a new operation ID.

The exact entry returned is the entry for the committed new state. A failed
operation does not return a partially derived entry.

### 4.1 Sealed append exception

The store exposes one sealed exception for append failures that cannot be
represented by a normal result:

```csharp
public sealed class AppendStoreException : Exception
{
    public AppendStoreException(
        AppendStoreErrorCode code,
        string operationId,
        string message,
        Exception? innerException = null);

    public AppendStoreErrorCode Code { get; }
    public string OperationId { get; }
}

public enum AppendStoreErrorCode
{
    RecoveryRequired,
    Internal
}
```

Only `RecoveryRequired` and `Internal` are permitted. Validation, read-only
scope, durable-write, cancellation-before-commit, and event failures must be
mapped to a typed result or to the existing public error contract at the
facade; they must not become new append exception subclasses or arbitrary
append exception codes. When possible, an ambiguous or incomplete operation
is returned as `AppendStoreResult(RecoveryRequired, ...)`. The
`AppendStoreException(RecoveryRequired, ...)` form is used when the store
cannot construct that normal result because recovery itself cannot be safely
finalized or its response envelope cannot be produced. `Internal` is reserved
for an invariant violation or an unexpected failure after the store has
established that no recovery journal is required.

## 5. Operation lifetime and entry point

`AppendAsync` owns the complete append transaction. The MCP and CLI facades
must call it and translate its typed result; they must not independently
derive content, archive the old version, write the artifact, or write the
sidecar. This closes the current facade/store split and ensures all callers
receive the same outcome.

At the start of `AppendAsync`:

1. Validate the address, content, and scope using existing validation rules.
2. Resolve the canonical existing address before reading, preserving the
   case-mismatch behavior of the store.
3. Create a fresh operation ID.
4. Run pending append recovery for the target scope before starting a new
   mutation. A scope with unresolved append recovery is write-blocked.
5. Read and capture the old state as raw bytes, including whether each member
   was missing.

Recovery is also available through an internal `RecoverAppendAsync` entry
point used by startup/store initialization and by the explicit retry path.
Recovery is idempotent and keyed by the journal operation ID.

## 6. Create-via-append behavior

If the target memory does not exist, append delegates to the existing
`StoreAsync` creation semantics after validation. It does not fabricate an
archive, old version, or rollback bytes for a nonexistent memory. The result
is `Created` and the returned entry represents the normal create path.

If creation has started and a durable write fails before the store can prove a
complete new pair, the journal records missing old members. Recovery deletes
only the operation's newly created members and restores the missing-file
condition. It must not delete an unrelated pre-existing file. A successful
create has no archive and no pruning step.

The create path remains subject to the same cancellation and event boundary:
cancel before commit returns `APPEND_CANCELLED`; cancellation after commit is
not allowed to turn a durable create into a failure.

## 7. Immutable transaction data

Before mutation, the store builds immutable values for the operation:

```csharp
public sealed record AppendTransaction(
    string OperationId,
    MemoryAddress Address,
    string Scope,
    bool IsCreate,
    RawMemberSnapshot OldArtifact,
    RawMemberSnapshot OldSidecar,
    RawMemberSnapshot NewArtifact,
    RawMemberSnapshot NewSidecar,
    ArchivePlan Archive,
    DateTimeOffset StartedAt);

public sealed record RawMemberSnapshot(
    string Path,
    bool Existed,
    byte[] Bytes,
    string Sha256);
```

The byte arrays and all path/hash values are immutable for the lifetime of the
transaction. Hashes are computed over the exact bytes that will be written.
No recovery code may call a decoder, serializer, derivation routine, or
formatter to recreate the rollback state.

## 8. Archive plan and sidecar archive statuses

Archive selection is represented by an immutable `ArchivePlan` created before
the first destructive operation:

```csharp
public sealed record ArchivePlan(
    bool Required,
    string? ArtifactArchivePath,
    string? SidecarArchivePath,
    ArchiveMemberStatus ArtifactStatus,
    ArchiveMemberStatus SidecarStatus,
    IReadOnlyList<string> PruneCandidates);

public enum ArchiveMemberStatus
{
    NotRequired,
    Planned,
    Created,
    AlreadyPresentVerified,
    Failed
}
```

`ArchivePlan` and its body are immutable. The plan identifies the exact old
artifact and sidecar destinations, and the journal stores the selected paths
and old-byte hashes. The archive is a pair operation, but artifact and sidecar
statuses are tracked independently so recovery can distinguish:

- neither archive member created;
- artifact only;
- sidecar only; and
- both members created and verified.

If an archive destination already exists, it is accepted only when its raw
bytes match the old member hash. A mismatch is a recovery condition, not an
overwrite permission. Archive creation must complete and be verified before
the live pair is changed. If archive creation fails, the live pair remains
untouched and the operation fails without publishing a new state.

Archive cleanup after commit is best effort and must not roll back a committed
append. Its failure produces `SuccessWithWarning` with
`POST_COMMIT_CLEANUP_FAILED` and retains sufficient journal/evidence for the
next maintenance or recovery pass.

## 9. Append journal

Persistent append operations use a separate version-1 journal at:

```text
<memory-versions-directory>/.append-journal.json
```

This is intentionally separate from the existing restore journal; restore
recovery is not append recovery and the two formats must not be conflated.

The journal is one transaction record whose transaction body is immutable;
state and per-step status are advanced by durable replacement of the complete
record. Its schema contains:

- `version` (currently `1`);
- `operationId`;
- canonical address and scope;
- state;
- old and new artifact paths;
- old and new sidecar paths;
- old/new existence flags;
- old/new raw-byte payloads (encoded for the JSON envelope) and their
  SHA-256 hashes;
- archive plan paths and per-member statuses;
- prune candidates and the pruning boundary;
- timestamps and the transaction's committed hash;
- whether the operation is create-via-append; and
- the last recovery/error detail, without replacing the original evidence.

Journal writes use durable replacement and flush semantics. The journal is
written and durably flushed before archive creation or live publication. A
zero-length, malformed, unsupported-version, or hash-inconsistent journal is
`RECOVERY_REQUIRED`; it is never silently deleted or treated as no journal.

### 9.1 State machine

The allowed states are:

```text
Prepared
ArchiveInProgress
Archived
NewPairInProgress
Committed
CleanupPending
RecoveryRequired
RolledBack
```

State transitions are monotonic in the durable journal. `Committed` means both
new members were written, flushed, and verified against their recorded hashes.
`CleanupPending` is a committed operation whose archive/pruning cleanup still
needs work. `RecoveryRequired` is a blocking state; normal writes in the scope
are refused until recovery succeeds or an operator explicitly resolves the
evidence.

The journal is removed only after the final state is proven and all required
evidence has been retained according to the archive policy. Failure to remove
it leaves `CleanupPending`, not a false success.

## 10. Durable write ordering

For an existing memory, the normal order is:

1. Read old artifact and sidecar raw bytes and validate the pair.
2. Derive the complete new content and sidecar in memory.
3. Build the immutable transaction and archive plan.
4. Write and flush `Prepared` journal state.
5. Create and verify archive members; persist archive statuses.
6. Persist `Archived` state.
7. Write the new artifact through the durable-file abstraction; flush it.
8. Persist `NewPairInProgress` with the artifact status/hash.
9. Write the new sidecar through the durable-file abstraction; flush it.
10. Verify both live members by raw read and hash.
11. Persist `Committed` with the verified new-pair hash.
12. Publish the typed success result and run post-commit cleanup/events.

The artifact-first order is retained for compatibility, but the journal and
raw rollback capture close the former derivation/sidecar window. If either
live member fails before commit, recovery restores both old raw members (or
their missing state), verifies the restoration, and records `RolledBack`.

### 10.1 Crash outcomes

Recovery must deterministically handle a process crash at every boundary:

| Crash point | Recovery action |
|---|---|
| Before durable `Prepared` | No append transaction is authoritative; leave live files unchanged. |
| After `Prepared`, before archive | Verify live old pair; retry archive or roll back without publishing new files. |
| During archive | Verify each archive member against old hashes; complete missing members or enter `RECOVERY_REQUIRED` on mismatch. |
| After `Archived`, before new artifact | Publish new pair or restore old pair; never assume a partial temp write is valid. |
| After new artifact, before new sidecar | Compare raw hashes; restore old pair or complete the new pair. |
| After both writes, before `Committed` | Verify new hashes; commit if both match, otherwise restore old raw bytes. |
| After `Committed`, before cleanup | Keep new pair; retry cleanup and report warning if still pending. |
| During cleanup/pruning | Keep new pair and journal evidence; do not roll back. |

If a committed journal's new hash does not match the live bytes, recovery must
not silently retry by overwriting the state. It must first attempt raw-byte
rollback only when the old pair can be verified, and otherwise preserve the
journal and return `RECOVERY_REQUIRED`. A retry after a successful rollback
starts a new operation ID.

## 11. Raw-byte rollback

Rollback writes the exact captured old bytes with durable replacement. For a
member whose old `Existed` flag is false, rollback deletes only the file
created by this operation and verifies that it is absent. For an existing
member, rollback verifies the destination is either the operation's new hash or
the old hash before replacing it; an unrelated hash is an ambiguity and
requires recovery.

Rollback order is artifact and sidecar independent, and each result is
recorded before proceeding. A rollback is successful only after both members
match their old existence/hash state. If rollback cannot prove that state, the
journal remains `RecoveryRequired` and the store throws
`AppendStoreException(RecoveryRequired, ...)`.

## 12. Pruning boundary

Pruning is not part of the commit boundary. The immutable archive plan records
the exact candidates selected before mutation, but pruning begins only after
`Committed` is durable and verified. A candidate is deleted only if it is
still one of the plan's candidates and is not the current live artifact,
current live sidecar, active append journal, or any archive member required for
recovery.

Pruning never deletes the current or newly created version to satisfy a count
limit. It is safe to stop after any candidate. A failed or cancelled prune
returns `SuccessWithWarning` and leaves the new pair committed.

Create-via-append has no pruning boundary because it creates no archive.

## 13. Cancellation

Cancellation is observed at validation, read, derivation, journal, archive,
write, verification, cleanup, and event boundaries, but its meaning depends on
the commit point:

- Cancellation before `Committed` initiates rollback. If rollback succeeds,
  return the public `APPEND_CANCELLED` result and no new memory state.
- Cancellation during recovery is not allowed to abandon a partially applied
  recovery. Recovery either completes, remains durably journaled, or returns
  `RECOVERY_REQUIRED`.
- Cancellation after `Committed` cannot undo the append. Cleanup and events
  become best effort; return success or `SuccessWithWarning`.

The cancellation token must not be used to skip verification, journal flush,
or rollback. A cancellation exception is never exposed as an untyped MCP
invocation failure.

## 14. Event hooks and event failure

`OnBeforeAppendAsync` runs before the durable transaction begins. Failure or
cancellation from a before-event prevents the append and is reported through
the existing event/error surface; no journal or live mutation is published.

`OnAfterAppendAsync` runs only after the new pair is committed and verified. A
failure is not a storage rollback. The operation returns
`SuccessWithWarning` with `EVENT_FAILED`, including the operation ID and the
event failure detail. Cancellation of an after-event has the same rule.

Event delivery must receive the committed entry and operation ID. A failing
event sink must not cause a second append on retry; callers retry event
delivery, not the storage transaction.

## 15. Facade and response mapping

MCP and CLI append surfaces consume `AppendStoreResult` and preserve its
structured fields: outcome, operation ID, entry, warning, and recovery
information. They map the storage contract to stable public codes:

| Store condition | Public result |
|---|---|
| `Created` | success with `created` action |
| `Appended` | success with `appended` action |
| `SuccessWithWarning` | success-with-warning, warning code preserved |
| cancellation before commit | `APPEND_CANCELLED` |
| read-only/validation/input failure | existing typed validation/read-only code |
| durable write failure with successful rollback | existing durable-write failure code |
| `AppendStoreException(RecoveryRequired, ...)` | `RECOVERY_REQUIRED` |
| `AppendStoreException(Internal, ...)` | `INTERNAL` |

No append path may fall through to the MCP framework's generic
`An error occurred invoking 'append'` response. Internal details remain in
diagnostics/logging; public responses include operation ID and recovery action
without exposing raw memory content.

## 16. Deterministic fault seams

Phase A must expose internal, test-only-invocable seams without making test
behavior depend on timing, process termination races, or real disk faults.
The seams are operation-ID scoped and can fail once or persistently at:

- raw artifact read;
- raw sidecar read;
- journal write, flush, parse, and delete;
- archive body write/flush;
- archive sidecar write/flush;
- durable live artifact write/flush;
- durable live sidecar write/flush;
- raw verification read;
- rollback write/delete;
- archive cleanup;
- pruning delete; and
- before-event and after-event delivery.

Each seam identifies the path and operation ID. Tests can inject a crash-like
failure after each durable state transition, a committed hash mismatch, a
missing member, an unexpected member hash, cancellation, and event failure.
The implementation must not use sleeps or wall-clock ordering to make these
tests deterministic.

The committed-hash-mismatch seam is especially important: recovery must
attempt verification, avoid blind overwrite, and either restore a verified old
pair or preserve the journal as `RECOVERY_REQUIRED`.

## 17. Acceptance criteria

Phase A is complete when all of the following are true:

1. The store exposes the typed `AppendStoreResult` contract and the sealed
   `AppendStoreException` contract exactly as defined here; no extra append
   exception codes or subclasses exist.
2. MCP and CLI append callers use the store transaction and never duplicate
   append derivation, archive, or write sequencing.
3. Existing-memory append captures raw old bytes before mutation and can restore
   both members byte-for-byte after every injected pre-commit failure.
4. Create-via-append preserves `StoreAsync` semantics, creates no archive, and
   restores missing-file state if creation fails.
5. The version-1 append journal is durably prepared before archive/live writes,
   has immutable operation data, and blocks normal writes while unresolved.
6. Recovery handles every state and crash boundary in Section 10.1, including
   malformed journals, archive-created cleanup, and committed hash mismatch.
7. Archive plans and bodies are immutable; artifact and sidecar archive status
   are tracked independently; an existing archive is never overwritten on a
   hash mismatch.
8. Pruning begins only after verified commit, honors the plan boundary, and
   cannot delete the current state or recovery evidence.
9. Cancellation before commit produces `APPEND_CANCELLED`; cancellation after
   commit cannot roll back the new pair.
10. After-event failure or cancellation produces `SuccessWithWarning` and never
    corrupts or rolls back a committed append. Cleanup failure is reported as a
    distinct warning.
11. No append failure escapes as a generic MCP invocation error.
12. Deterministic seams cover every journal, raw-read, durable-write, archive,
    rollback, verification, cleanup, pruning, and event boundary.
13. Tests prove byte-for-byte rollback, missing-file restoration, recovery
    idempotence, scope blocking, retry with a new operation ID, and all public
    result mappings.
14. No source or test changes outside the Phase A implementation and its tests
    are required by this specification.

## 18. Phase B deferral

The following remain explicitly deferred:

- applying the append transaction protocol to `remember`, `compact`, import,
  migration, bundle writes, or arbitrary sidecar mutations;
- unifying append and restore journals into a shared transaction format;
- cross-memory or multi-scope transactions;
- transactional guarantees for external plugin/event systems;
- a general archive/pruning service;
- background journal garbage collection and fleet-wide recovery reporting; and
- changes to NMP/2 or sidecar formats required to support richer transaction
  metadata.

Phase B must not be pulled into Phase A through convenience refactoring. Any
new requirement in those areas needs a separate approved design.
