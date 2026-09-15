---
status: in-progress
phase: 1
updated: 2026-09-15
---

# B5 Retention, Pruning, and Recovery Maintenance Implementation Plan

> **For agentic workers:** Use the repository's implementation workflow and keep each task atomic.
> This plan is implementation-only; do not stage or commit this plan or the design specification.

**Goal:** Add one bounded Core maintenance path for eligible archive pairs and verified terminal
recovery evidence, shared by the local CLI and bounded background host adapter.

**Authoritative design:** `docs/superpowers/specs/2026-09-15-b5-retention-maintenance-design.md`

## Global constraints

- Keep the flat workspace dictionary and exactly the five approved B5 policy keys. Add no policy
  key, metadata file, archive index, database, manifest, protection file, or new wire format.
- Retain unresolved, malformed, ambiguous, unsupported, inaccessible, path-unsafe, incomplete, and
  unverifiable evidence indefinitely. Delete only typed, verified terminal recovery evidence.
- Use the existing scope-local exclusive lock and in-process write lock exactly once per scope;
  revalidate every deletion under that boundary. Never create a workspace-wide lock.
- Preserve NMP/2, ordinary sidecars, archive layout, B2/B3/B4 wire contracts, plugin lifecycle,
  observer semantics, and existing CLI/MCP behavior except for the approved local command.
- Bound scans, metadata reads, returned work, lookahead, memory, time, cancellation, scheduler work,
  and retries. Never create one task per candidate or an unbounded queue.
- Route every FileMemoryStore recovery byte/hash decision through the one internal bounded async
  verifier: 16 MiB total bytes, 2-second monotonic deadline, 2 opens, 8 stats, 257 reads (including
  the final zero-byte read), and a 64 KiB buffer; requests may lower but never raise these limits.
- Physical paths, raw JSON, exception text, secrets, and content never enter cursors, results,
  logs, reports, or CLI output.
- New serialized roots use source-generated JSON metadata. Continuation cursors are binary and are
  never JSON-serialized.

## Authoritative cursor and run ownership

The sole cursor is `MaintenanceCursor`, with the exact wire contract from spec §5.1: ASCII prefix
`b5c2.`, followed by unpadded base64url encoding of 160 encoded bytes total: 128-byte payload +
32-byte HMAC-SHA256. The encoded token is at most 219 ASCII characters.
`MaintenanceCursorCodec` is an internal static type owned by
`src/Scrinia.Core/MaintenanceContracts.cs`; its sole APIs are:

```csharp
Encode(MaintenanceCursor, ReadOnlySpan<byte>)
TryDecode(string, ReadOnlySpan<byte>, out MaintenanceCursor, out string?)
ComputeCandidateKey(MaintenanceWorkspaceKey, MaintenanceCandidateIdentity, ReadOnlySpan<byte>)
ComputeWorkspaceKey(ReadOnlySpan<byte>, ReadOnlySpan<byte>)
```

`MaintenanceRunRegistry` is owned by `src/Scrinia.Core/FileMemoryMaintenanceService.cs`. Its
exact lifecycle API is `Create(...)`, `TryGet(...)`, `Remove(...)`, and `EvictExpired(...)`. It
holds a process-local random 32-byte key plus immutable bounded run/snapshot state. Creation,
replacement, eviction, and removal are atomic; the snapshot is immutable after publication. Keys
expire with the cursor and are zeroed/removed on expiry, completion, invalidation, disposal, or
process exit. The key is not configuration, persistence, serialization, output, or a CLI argument.
There is no external-secret fallback and no new policy key. Missing run/key/snapshot fails closed.

All safe candidates use one path-free total order everywhere:
`(scope ordinal, kind, candidate key, subject identity digest, immutable entry ordinal)`. Scalar
fields use ordinal comparison; keys and digests use unsigned-byte lexicographic comparison. Subject,
timestamp, and physical path are classification metadata only.

Page accounting is exact: `CandidatePageSize=128` is one shared invocation-wide candidate-admission
budget, not a safe-only allowance. Every admitted candidate consumes one unit, including safe
candidates, unsafe/reparse/malformed journal entries, non-journal entries, and lookahead. The
effective journal budget is `min(requestedJournalBatchSize, JournalCleanupBatchSize)`,
with both values in `1..50`; the configured default and ceiling are 50. This is one budget for the
whole invocation, not one budget per scope. Scopes are resolved and processed in ordinal order; each
scope receives only the remaining budget after earlier scopes, and no scope may reset or replenish it.
`JournalInspected` counts every admitted journal candidate and `NonJournalInspected` every admitted
non-journal candidate. Track `UnsafeJournalScanned`, `UnsafeNonJournalScanned`, and
`UnsafeScanned = UnsafeJournalScanned + UnsafeNonJournalScanned`; safe counters are class totals minus
their unsafe counters. The exact equations are the full class equations in spec §5. Every unsafe
candidate consumes exactly one shared 128-admission unit and increments its class counter. Classify an
unsafe entry as journal only when safe directory-entry location/name metadata establishes that class;
otherwise it is unsafe non-journal. An unsafe journal additionally consumes exactly one journal-budget
unit. No unsafe scan may bypass either cap. Assert
`Scanned = JournalInspected + NonJournalInspected = SafeJournalInspected + UnsafeJournalScanned +
SafeNonJournalInspected + UnsafeNonJournalScanned`,
`SafeInspected = Scanned - UnsafeScanned`,
`UnsafeScanned = UnsafeJournalScanned + UnsafeNonJournalScanned`,
`JournalInspected = SafeJournalInspected + UnsafeJournalScanned`, and
`NonJournalInspected = SafeNonJournalInspected + UnsafeNonJournalScanned`.
There is no separate safe pool or per-scope reset. `Projected` counts safe candidates converted to
projections; `MetadataReads` counts attempted metadata reads; `Returned` counts emitted projections;
and `Lookahead` is zero or one safe projection inspected after the returned page. Lookahead counts
toward Scanned, Projected, MetadataReads, and memory, but not Returned, and a journal lookahead is
forbidden after the global journal budget is reached. `JournalCapReached` and saturated
`JournalDeferred` (`0` or `1`) report the cap boundary without inspecting or skip-listing deferred
journals. The deferred bit is set only from a non-serialized, private-constructor
`JournalBoundaryProof` minted by the pre-inspection enumerator factory. Its exact fields bind the
validated safe root, run, snapshot, enumerator identity, scope/page boundary, and canonical entry key;
they also carry the directory-entry kind and `BufferedDirectoryEntryMetadataOnly` provenance. The
factory cannot mint from a candidate, projection, content result, or post-inspection record. The
validator signature is `ValidateJournalBoundaryProof(in JournalBoundaryProof proof, in
JournalBoundaryValidationContext context, out JournalBoundaryRejection rejection)`. It rejects wrong
kind, unsafe or mismatched safe root, wrong run/snapshot/enumerator, scope/page/boundary mismatch,
canonical-key mismatch, non-journal syntax, malformed metadata, non-metadata-only provenance, stale
or advanced boundaries, false presence, nonzero remaining journal budget, and any opened/read/parsed/
admitted/inspected proof. Its actionable classification is syntactic directory-entry classification
only; it does not validate content, envelopes, supported versions, accessibility, terminal state, or
deletion eligibility. Producing the proof does not open the entry, read journal content, parse recovery
data, advance past the entry, admit a candidate, or perform a content metadata read. If the enumerator
cannot provide that exact classification, no proof is emitted. Malformed, unsupported, or inaccessible
content is discovered only after later counted admission and becomes typed protected evidence. Remaining
non-journal, unsafe, or unclassifiable entries never set `JournalDeferred` or journal-specific `HasMore`. `HasMore` is true for a cap only when
`JournalCapReached && JournalDeferred == 1`; it is otherwise true only for another actionable safe
candidate or another scope. The protected unsafe aggregate never sets it.
`UnsafeScanLimit=32` is invocation-wide across all scopes. The scan stops at the shared page, 32 unsafe
entries, or zero remaining journal budget for a journal candidate; it can never cause
`JournalInspected` or `JournalDeleted` to exceed 50. `JournalBudget` and
`JournalBudgetConsumed` are explicit invocation-level result fields; per-scope counters are additive
views of them.
`PageSequence` starts at zero and increments once only when a page returns a projection and advances
the cursor. A monotonic absolute deadline prevents new operations after expiry. Cancellation is
checked before each candidate; the current filesystem operation may finish, but no new candidate
starts and no advanced cursor is returned. Assert the exact equations above, including
the full class equations and `SafeInspected = Scanned - UnsafeScanned`, on every page, plus
`Scanned <= 128`, `JournalInspected <= JournalBudget <= 50`, `UnsafeScanned <= 32`,
`JournalDeleted <= JournalInspected`, `NonJournalInspected <= 128 - JournalInspected`,
`MetadataReads <= 128`, `Returned <= 64`, `Lookahead <= 1`, and
`Returned + Lookahead <= 65` at every boundary.

## Phase 1 — contracts, codec, registry, and policy

### Task 1: Define contracts and fixed binary cursor

**Create:** `src/Scrinia.Core/MaintenanceContracts.cs`,
`src/Scrinia.Core/MaintenanceJsonContext.cs`, and
`tests/Scrinia.Tests/MaintenanceContractTests.cs`.

Define the immutable request/result, candidate, projection, policy, cursor, identity, key, and page
types from spec §§3–5.1, including invocation-wide `JournalBudget` and `JournalBudgetConsumed`,
additive per-scope `JournalInspected`, `JournalDeleted`, `NonJournalInspected`, `UnsafeScanned`,
`UnsafeJournalScanned`, `UnsafeNonJournalScanned`, `JournalCapReached`, and `JournalDeferred`
counters/state. Define the exact path-free `JournalBoundaryProof` fields from spec §5: directory-entry
kind, metadata-only provenance, run/snapshot/enumerator identity, validated safe-root binding, and
canonical entry key, in addition to the fixed location/name and boundary fields. Implement it as an
internal non-serialized type with a private constructor and an enumerator-only pre-inspection factory;
post-inspection or content-derived values must be unable to construct a proof. Implement the exact
`ValidateJournalBoundaryProof(in JournalBoundaryProof, in JournalBoundaryValidationContext,
out JournalBoundaryRejection)` signature and every rejection condition in spec §5. **Citations:**
proof semantics and fields — `docs/superpowers/specs/2026-09-15-b5-retention-maintenance-design.md`,
§5, “The exact proof shape and validator”; run/snapshot identity and cursor lifecycle — the same
document, §5.1; continuation ownership and budget invariants — the same document, §5 and §12.
Register only the directly serialized
result/policy graph. Do not register
cursor, keys, identities, projections, or any legacy payload. Implement the codec and registry
ownership exactly as stated above, including fixed widths, network-order GUIDs, canonical base64url,
HMAC domains, expiry, atomic lookup/removal, and no path-bearing state.

### Task 2: Define policy loading

**Create:** `src/Scrinia.Core/Configuration/MaintenancePolicy.cs` and
`tests/Scrinia.Tests/MaintenancePolicyTests.cs`.

Implement the five approved keys, defaults, lexical parsing, exact ranges—including
`JournalCleanupBatchSize` as `1..50` with default/ceiling 50—and fail-closed typed
invalid-policy result. Load once per batch. No continuation key is loaded from configuration.

### Task 3: Remove B5 legacy symbols and add the scoped zero-symbol guard

The B5 maintenance inventory is exhaustive for the B5 scope and must be checked before
implementation and again in the final gate. This is not a prohibition on unrelated existing
embedding functionality:

| Area | Exact paths/roots |
|---|---|
| B5 maintenance implementation | `src/Scrinia.Core/MaintenanceContracts.cs`, `MaintenancePolicy.cs`, `MaintenancePlanner.cs`, `FileMemoryMaintenanceService.cs`, `MaintenanceJsonContext.cs`, plus B5 maintenance-only regions in recovery/store files |
| B5 tests | B5 contract, policy, recovery-candidate, planner, service, CLI, scheduler, serialization, compatibility, and zero-symbol guard tests |
| Maintenance CLI and guidance | `src/Scrinia/Commands/ScriniaCommands.Maintenance.cs`, maintenance-only serializer/help regions, and B5 maintenance command guidance |
| B5 artifacts | `docs/superpowers/specs/2026-09-15-b5-retention-maintenance-design.md` and `docs/superpowers/plans/2026-09-15-b5-retention-maintenance.md` |
| Forbidden literals and exact guard inputs | `b5ct1`, `MaintenanceContinuationPayload`, `MaintenanceDiscoveryCursor`, `AfterRelativePath`, `UnsafeCandidatesToSkip`, legacy `NextCursor`, `OpenAiApiKey`, `OPENAIAPIKEY`, `openaiapikey`, `OpenAIAPIKey`, `openAiApiKey`, `OPENaiapikey`; guidance inputs `use OpenAiApiKey for maintenance`, `USE OPENAI API KEY FOR MAINTENANCE`, `use openai-key access on maintenance path` |

Excluded existing embedding paths are `src/Scrinia.Core/Embeddings/**`,
`src/Scrinia.Plugin.Embeddings/**`, `src/Scrinia.Plugin.Embeddings.Cli/**`,
`tests/Scrinia.Tests/Embeddings/**`, `tests/Scrinia.Plugin.Embeddings.Tests/**`,
`src/Scrinia/Services/CoreEmbeddingEventHandler.cs`, and embedding-only regions of
`src/Scrinia/Commands/WorkspaceSetup.cs`. The guard must not edit or fail these paths. If a
mixed file receives B5 maintenance changes, only its B5 maintenance region is scanned.

Remove all superseded cursor contracts, legacy continuation members, and secret-key maintenance
instructions from the B5 scope. Replace them only with the fixed `MaintenanceCursor`/`b5c2.`
contract. The existing embedding paths explicitly excluded by the spec remain outside this guard;
the guard must not edit or fail them. In both B5 artifacts, the only allowlisted ranges are the
literal cells in the exact forbidden-literals inventory row and the complete named historical
removal note. Every other B5 maintenance code, test, command, CLI guidance, serializer metadata,
generated-root assertion, report, plan, spec prose, fenced code, indented code, example, fixture,
or assertion is forbidden. The guard also fails on forbidden serializer registration or secret-key
access on any B5 maintenance path, while never scanning excluded embedding paths.

The guard test inputs are the exact literals and guidance inputs in the inventory's forbidden-symbol
row, including the case variants listed there. It scans the B5 scope case-insensitively and asserts
one failure for each literal at an in-scope executable location, while asserting zero failures for
the identical literal in either exact inventory-table row or either named historical-note range. A
control fixture under an excluded embedding path proves that existing embedding key usage is not
affected. The legacy-input test asserts rejection with zero enumeration and zero deletion; it does
not construct or decode a legacy payload.

> **Historical removal note — non-executable and explicitly allowlisted:** Earlier B5 drafts used
> `b5ct1`, `MaintenanceDiscoveryCursor`, `AfterRelativePath`, `UnsafeCandidatesToSkip`,
> `MaintenanceContinuationPayload`, legacy `NextCursor`, and OpenAI-key access wording. Those
> materials are removed, not compatibility aliases; only the fixed `b5c2.` continuation contract
> remains.

## Phase 2 — typed recovery projections and deterministic planning

### Task 4: Add B2/B3/B4 maintenance adapters

**Modify:** `src/Scrinia.Core/FileMemoryStore.cs`, `RecoveryContracts.cs`, `RecoveryDiscovery.cs`, `RecoveryEnvelope.cs`,
`AppendJournal.cs`, `RestoreJournal.cs`, `FileEntityTransactionCoordinator.cs`,
`MultiMemoryTransactionCoordinator.cs`, `MultiMemoryTransactionRecord.cs`, and
`TransactionalParticipantSerialization.cs`.

**Create:** `tests/Scrinia.Tests/MaintenanceRecoveryCandidateTests.cs`.

**Create:** `src/Scrinia.Core/RecoveryVerification.cs` and
`tests/Scrinia.Tests/RecoveryEvidenceVerifierTests.cs`.

**Production inventory (exhaustive):** Task 4 must migrate the named recovery sites in
`src/Scrinia.Core/FileMemoryStore.cs`: `CaptureRawMemberAsync` (`ReadAllBytesAsync`), publication
and whole-member verification, `HasCommittedHash`, `MatchesAppendMember`/rollback state matching,
`RecoverAppendUnsafeAsync` journal loading and `TryReadOperationId` fallback,
`PersistAppendJournal` existing-journal parsing, `VerifyJournalDestinationsUnsafe`/
`VerifyJournalDestination`, and `ReadBoundedJournalStream`. It must also migrate
`RecoveryDiscovery` journal parse/member hashes,
`RecoveryEnvelope.AppendRecoveryAdapter.RecoverCommittedCleanup`,
`RestoreRecoveryAdapter.VerifyFile`, `FileEntityRecoveryAdapter.VerifyCommittedSnapshot`, and
`FileEntityTransactionCoordinator` capture/hash and `RecoverPendingEvidence` envelope loading.
The exact current call-site map and replacement for each helper is in design §2.2. Approved
exclusions are in-memory transaction-payload and decoded-envelope-payload hashes, ordinary
bundle/configuration/migration reads, embedding/vector hashes, and workspace-file baselining. A
source guard scans the complete recovery regions of all four production files and related helpers,
fails on every unapproved whole-file read/hash/parser call, and proves excluded helpers are not
called by recovery paths.

**Dependencies:** Consume the existing B2 recovery envelopes/adapters, B3 coordinator and
participant contracts, B4 quarantine/admission state, `PathContainment`, incremental SHA-256, and
the existing filesystem/cancellation abstractions. Produce one internal verifier used by all
FileMemoryStore recovery paths and typed results consumed by the read barrier, recovery adapters,
and later Task 5/Task 6 maintenance planning/execution. Do not make Task 4 depend on the maintenance
cursor, an archive index, cache contents, plugin lifecycle, or a new public API.

Expose non-serialized typed projections and `RecoveryCleanupDecision` through the owning recovery
adapters. B5 never parses raw JSON, envelopes, filenames, or exception messages. Cover every B2
location, B3 record, and B4 admitted/rejected/quarantined state. Keep all physical locations and
wire bytes unchanged.

The adapters and `FileMemoryStore` must share exactly one internal verifier contract:

```csharp
internal interface IRecoveryEvidenceVerifier
{
    ValueTask<RecoveryVerificationResult> VerifyAsync(
        RecoveryVerificationRequest request,
        CancellationToken cancellationToken);
}
```

`FileMemoryStore` owns the verifier instance and is the only caller allowed to map its result into
recovery progress, read-barrier exceptions, or maintenance protection. The verifier is limited to
already-bound recovery evidence and its optional destination; it is not a general file reader and
does not own writes, deletes, journal rewrites, index/cache invalidation, CLI/MCP output, plugin
callbacks, or ordinary workspace-file hashing. The exact request/result/limit types and outcome
enum are the contract in spec §2.1.

`RecoveryVerificationRequest` contains one `RecoveryVerificationLimits Limits` value only; duplicate
`MaximumBytes`/`MaximumDuration` fields are forbidden. `RecoveryVerificationLimits.Hard` is the
authoritative 16 MiB/2-second/2-open/8-stat/257-read/64 KiB ceiling. A validated override may only
lower a field, with precedence hard ceiling → override → one monotonic deadline. Zero, negative,
overflow, non-finite, above-hard, or mixed invalid values return `ProtectedInvalidLimits` before any
I/O and never clamp, default, retry, or partially apply. Adapters map it exactly to typed
`InvalidVerificationLimits` and `RecoveryRequired`; no generic exception or success mapping is
allowed.

The result carries caller-owned `RecoveryVerificationContent` for source and optional destination:
bounded read-only memory plus verified length, hash, regular-file, containment, and post-read-
stability metadata. Ownership transfers on return; the caller disposes each content value in
`finally` after parsing, while parsers borrow and never retain or dispose it. `RecoveryEnvelopeCodec`,
`AppendJournalCodec`, `RestoreJournalCodec`, `FileEntityRecoveryAdapter`, and `TryReadOperationId`
consume verified content/metadata rather than a path, fresh `byte[]`, or long-lived stream. They do
not reread the file. Retained content remains within 16 MiB total, in addition to the one fixed
64 KiB read buffer, and
`ReadBoundedJournalStream` produces this same contract. Cite design §§2.1–2.2 and the bounded-read
precedent in `docs/superpowers/specs/2026-08-24-journaled-restore-recovery-design.md`.

Implement hard ceilings of 16 MiB total bytes, a 2-second monotonic absolute deadline, 2 open
operations, 8 stat operations, 257 `ReadAsync` attempts (including the final zero-byte read), and a 64 KiB fixed read buffer. Requests
may lower but never raise those limits. Count source and destination bytes together; count every
read attempt including the final zero-byte read; count every open/stat attempt; and never reset a
counter for a retry or a second member. Validate scope containment and regular-file identity before
open, incrementally hash source and destination, and re-stat after hashing to detect replacement.
Return typed `ProtectedOversized`, `RecoveryRequiredTimeout`, `RecoveryRequiredCancellation`,
`ProtectedBlocked`, `ProtectedMissing`, `ProtectedHashMismatch`, `ProtectedUnsafe`, or
`ProtectedIoFailure`, or `ProtectedInvalidLimits` for every non-verified condition. Only `Verified` may authorize recovery
progress or completed-evidence cleanup; every other result retains evidence and performs no
destination, journal, index, cache, or topic/facts mutation.

Replace every unbounded FileMemoryStore recovery `ReadAllBytes`, `ReadAllText`, whole-file
`SHA256.HashData`, and byte-buffer JSON parse path, including every inventory site above, capture, publication verification,
committed-hash verification, member matching, archive reconciliation, rollback matching, journal
parsing, and operation-id fallback. Route each through the shared verifier or a bounded typed parser
 that first obtains `RecoveryVerificationContent` and verified metadata. Do not replace ordinary non-recovery hashing
such as workspace-file attribute baselining. Cancellation and deadline checks occur before each
open, stat, and read and between read chunks; an in-flight OS filesystem call may finish, so document
and test the cooperative-I/O limitation rather than claiming hard preemption. The verifier is
internally injectable for tests only and adds no public surface or configuration key.

**Task 4 test matrix:** Test each fixed B2/B3/B4 location and both source-only and
source/destination verification; exact-limit success and one-byte/one-operation over-limit failures;
missing source, missing destination, directory/reparse/containment rejection, sharing/permission
blocked, source hash mismatch, destination hash mismatch, post-read replacement/length mismatch,
 malformed typed identity, generic I/O failure, every invalid-limits form and precedence case, timeout before first operation, timeout between
chunks, cancellation before open, cancellation between chunks, and an in-flight read that completes
before the typed cancellation/timeout result. Assert exact bytes/open/stat/read counters, 64 KiB
buffer use, shared deadline, no counter reset, no detached task, and no unbounded allocation. Assert
that invalid limits perform zero opens/stats/reads and map exactly to `InvalidVerificationLimits`/
`RecoveryRequired`; assert content ownership, `finally` disposal, bounded subviews, parser borrowing,
and no second whole-file read. Assert
every non-verified outcome retains the journal/evidence and leaves destination, journal bytes, live
pair, index, cache, topic/facts projections, and transaction state unchanged. Add a source scan test
over the complete FileMemoryStore/RecoveryEnvelope/RecoveryDiscovery/FileEntityTransactionCoordinator
inventory and related recovery helpers, proving every recovery path routes through the shared
verifier and no unbounded read/hash/parser helper remains; separately prove approved exclusions and
ordinary workspace-file baselining remain out of scope. Add adapter tests proving only
`Verified` can produce `RecoveryCleanupDecision` or advance recovery state, and that all typed
failures map to the existing recovery-required/read-barrier vocabulary without path, content, or
exception text.

### Task 5: Implement bounded candidate planning

**Create:** `src/Scrinia.Core/MaintenancePlanner.cs` and
`tests/Scrinia.Tests/MaintenancePlannerTests.cs`; modify `FileMemoryStore.cs` only for narrow
internal read/invalidation seams.

Implement safe path containment, typed protection/reference classification, the authoritative total
order with non-journal candidates before `completedRecoveryEvidence`, bounded metadata reads, one
lookahead, and per-subject completeness before quota decisions. Enforce one shared invocation-wide
`CandidatePageSize=128` admission budget with the exact class equations above, plus one invocation-wide
journal budget with greedy ordinal scope allocation. Charge every unsafe journal and non-journal
candidate exactly once to the shared admission and increment its class counter; charge unsafe journals
once to the journal budget while enforcing the invocation-wide `UnsafeScanLimit=32`; no class or scope
may replenish either budget. Lookahead
is counted in the shared 128 admissions but not returned and cannot admit a journal after the global
 journal budget. Replace the old remainder concept with the exact typed `JournalBoundaryProof` fields,
 private enumerator-only factory, validation context, rejection enum, and validator in spec §5. Its
 actionable input is only a validated safe-root directory entry whose fixed
  location, accepted filename layout, and entry kind syntactically classify it as a journal; it makes
  no content-validity or accessibility claim. Malformed, unsupported, or inaccessible content is
  discovered only after later counted admission and becomes typed protected evidence. Add positive
  tests for each fixed journal location/name class and negative tests for non-journal,
  unsafe/non-classifiable, non-actionable, stale/advanced, content-inspected, and path-bearing proofs,
   plus tests proving deferred content failures cannot produce a proof. Add direct contract tests that
   attempt wrong kind, unsafe-root, wrong run/snapshot/enumerator, wrong canonical key, non-journal
   syntax, malformed metadata, non-metadata provenance, and content-inspection inputs, and assert the
   factory/validator boundary rejects them. Cite the proof contract in the test names/comments as
   `B5 design §5 — JournalBoundaryProof`; cite the run/snapshot binding and unchanged continuation
   behavior as `B5 design §5.1` and `B5 design §12`. Derive `JournalDeferred` and
  journal-specific `HasMore` only from that validator.
Missing/deleted candidates require locked revalidation;
changed, mismatched, or unsafe candidates remain protected. Use no path or unsafe skip list in the
cursor or registry. Test exact vectors, ordering, counters, deadlines, cancellation, and bounds.

## Phase 3 — lock-scoped execution

### Task 6: Implement the Core service

**Create:** `src/Scrinia.Core/FileMemoryMaintenanceService.cs` and
`tests/Scrinia.Tests/FileMemoryMaintenanceTests.cs`; modify `FileMemoryStore.cs` for the single
existing admission seam.

Implement `FileMemoryMaintenanceService.ExecuteAsync(MaintenanceRequest, CancellationToken)` and
the store's `ExecuteMaintenanceScopeAsync` entry point. Load policy once, resolve scopes in ordinal
order, initialize one invocation-wide journal budget, validate the in-memory cursor binding, acquire
the existing scope boundary once, run the
recovery barrier, plan bounded work, revalidate immediately before deletion, delete only eligible
pairs or typed terminal evidence, and invalidate affected derived state only after successful
archive deletion. Continue independent scopes after contention. Retain failed or partial pairs.

### Task 7: Run execution failure and race coverage

Cover invalid policy/token with zero enumeration/deletion, lock timeout, writer races, concurrent
maintenance, partial pair failure, permissions, restart safety, cancellation, deadline-before-first-
yield unchanged cursor, exact sequence behavior, repeated convergence, idempotency, cache locality,
default/range/override journal budgets, multi-scope allocation without reset,
50-journal-with-128-shared-admission boundary, unsafe/reparse/malformed/non-classifiable charging for
both journal and non-journal candidates, exact class counter equations, non-journal capacity use, and
the 32-entry unsafe bound, cap-reached lookahead suppression, typed proof-backed deferred-journal
state, positive proof validation for each fixed journal location/name class, and negative proof
validation for non-journal, unsafe/non-classifiable, non-actionable, stale, advanced, content-inspected,
and path-bearing inputs. Verify malformed/unsupported/inaccessible content becomes typed protected
evidence only after admission, while every unsafe candidate consumes exactly one shared admission and
unsafe journals additionally consume one journal-budget unit. Cover deferred-journal counters, `Scanned <= 128`,
the exact admission equations, `HasMore` and continuation resumption,
indefinite unresolved retention, and no per-candidate task creation.

## Phase 4 — local CLI

### Task 8: Add the shared-result maintenance command

**Create:** `src/Scrinia/Commands/ScriniaCommands.Maintenance.cs` and
`tests/Scrinia.Tests/CliMaintenanceTests.cs`; modify `src/Scrinia/Commands/CliJsonContext.cs`.

The handler validates arguments, calls `WorkspaceSetup.Configure`, constructs `MaintenanceRequest`,
invokes the Core service, and renders the shared result. It never enumerates, classifies, deletes,
or retries. `--continuation-token` is first checked locally by the CLI-owned
`MaintenanceContinuationSyntax.TryValidate(string, out MaintenanceContinuationSyntaxError)` for
framing, ASCII, canonical unpadded base64url, and exact 160-byte decoded size only. A failed syntax
check constructs the typed `InvalidContinuation` result locally with zero Core service calls,
enumeration, or deletion. A syntactically valid token—including bad MAC, invalid scope/run binding,
expiry, semantic field errors, or missing registry state—is passed to the shared Core service, whose
`MaintenanceCursorCodec.TryDecode(string, ReadOnlySpan<byte>, out MaintenanceCursor, out string?)`
owns those semantic checks and returns the same sanitized typed result. Test both ownership paths,
including legacy/invalid framing versus semantically invalid tokens, human/structured output, safe
diagnostics, binding, expiry, cancellation, contention, and absence of a separate CLI path.

## Phase 5 — bounded background host adapter

### Task 9: Add scheduler and shutdown ownership

**Create:** `src/Scrinia/Services/MaintenanceScheduler.cs` and
`tests/Scrinia.Tests/MaintenanceSchedulerTests.cs`; modify `WorkspaceSetup.cs`,
`ScriniaCommands.Serve.cs`, and `Program.cs` only for scheduler setup and existing drain ownership.

Use only the five policy keys, fixed code scheduling defaults, one bounded invocation at a time, and
the unchanged void `BackgroundWork.Fire` boundary. Scheduler Stop/Dispose closes admission,
cancels and awaits its active invocation, and never drains unrelated global work. Program owns the
single global drain. Test disabled mode, cancellation, fault isolation, sanitized warnings, exact
inner-then-outer shutdown ordering, and plugin independence.

## Phase 6 — acceptance

### Task 10: Compatibility, serialization, privacy, and final review

**Create:** `tests/Scrinia.Tests/MaintenanceSerializationAndCompatibilityTests.cs`.

Assert the exact source-generated roots, fixed b5c2 vectors, no new durable format/index/policy key,
all unresolved evidence surviving a fresh process, existing B1–B4 and CLI behavior, privacy
boundaries, the exact `JournalBoundaryProof` fields/validator and every positive/negative case,
including directory-entry kind, metadata-only provenance, run/snapshot/enumerator identity,
validated safe-root binding, canonical entry key, and that actionable means only
validated-safe-root/fixed-location/accepted-layout/entry-kind directory classification and never
content validity or accessibility, the
post-inspection protected-evidence rule, the shared 128-admission invariant for both unsafe classes,
and the B5-scoped zero-symbol guard. Run focused B5 tests, regression tests, build,
trimmed publish, full solution tests, formatting/diff checks, and the final review against every
spec section. Record evidence separately; do not stage or commit this plan/spec or unrelated files.
Require the complete Task 4 production-inventory scan, invalid-limit zero-I/O tests, exact typed
`ProtectedInvalidLimits` → `InvalidVerificationLimits`/`RecoveryRequired` mapping, and
`RecoveryVerificationContent` ownership/disposal/no-second-read tests. Cite design §§2.1–2.2 and
the bounded-read precedent in `docs/superpowers/specs/2026-08-24-journaled-restore-recovery-design.md`.
Include the continuation ownership boundary: locally rejected framing/ASCII/canonical-base64url/size
input has zero Core calls, while syntactically valid-looking bad-MAC/scope/expiry/semantic input
reaches Core.

## Self-review checklist

- One path-free total order is used in spec, contracts, planner, service, CLI, and tests.
- `MaintenanceCursorCodec` and `MaintenanceRunRegistry` have one named owner and exact APIs.
- Registry key lifecycle, expiry, eviction, concurrency, and no-secret/path disclosure are explicit.
- Page counters, one shared invocation-wide 128-admission bound and 50 maximum journal bound, exact
  safe/unsafe journal and non-journal equations, greedy ordinal multi-scope allocation, the hard
  invariant that every unsafe candidate consumes exactly one shared admission while unsafe journals
  additionally consume one journal-budget unit, unsafe-scan/32-entry bound, lookahead inclusion,
   exact path-free `JournalBoundaryProof` fields and validator, including directory-entry kind,
   metadata-only provenance, run/snapshot/enumerator identity, safe-root binding, and canonical entry
   key. The private enumerator-only factory prevents post-inspection/content-derived proof creation;
   the validator rejects wrong kind, unsafe root, wrong run/snapshot/enumerator, non-journal syntax,
   malformed metadata, and key mismatch. Its actionable input is only validated-safe-root/fixed-
   location/accepted-layout/entry-kind directory metadata and never content validity or accessibility,
   proof-backed cap-reached
  continuation/`HasMore`, positive and negative proof tests, sequence rules, deadline, cancellation,
  and boundary assertions are explicit and consistent.
- The cursor accounting is unambiguous: 128-byte payload + 32-byte HMAC = 160 encoded bytes total,
  followed by the exact `b5c2.` prefix/base64url limit in the spec.
- Within the B5 scope, legacy material appears only in the historical removal note and exact
  forbidden-literals inventory-table row; excluded existing embedding paths are not scanned.
- Recovery verification has one authoritative `RecoveryVerificationLimits` source with no duplicate
  byte/duration fields, lower-only override validation and exact invalid-limit mapping; the never-
  raise rule is preserved.
- `RecoveryVerificationContent` is bounded to the shared 16 MiB total, caller-owned and disposed in
  `finally`; named journal/envelope/file-entity parsers borrow verified content and cannot trigger a
  second whole-file read.
- The complete FileMemoryStore/RecoveryEnvelope/RecoveryDiscovery/FileEntityTransactionCoordinator
  inventory and related helpers is enumerated in Task 4, with approved exclusions and a scan guard
  proving every recovery path uses the shared verifier.
  No executable B5 legacy token or secret-key instruction remains.
- Unresolved evidence remains indefinitely; work, memory, returned output, and scheduler activity
  remain bounded; no index, database, new policy key, format, or remote surface is introduced.
- Recovery verification has one FileMemoryStore-owned internal async path, exact shared limits
  (16 MiB, 2 seconds, 2 opens, 8 stats, 257 reads including the final zero-byte read, 64 KiB
  buffer), typed non-verified outcomes, lower-only request limits, post-read re-stat, and explicit
  cooperative-I/O behavior. Every listed recovery whole-file read/hash/byte-buffer parse is routed
  through it; ordinary workspace-file baselining is explicitly excluded.
- Non-verified recovery outcomes retain evidence and make no destination, journal, live-pair,
  transaction-state, index, cache, or topic/facts mutation; only `Verified` can advance recovery or
  create `RecoveryCleanupDecision`.
- Continuation ownership is explicit and tested: the CLI locally rejects only framing/ASCII/
  canonical-base64url/160-byte-size failures with a typed `InvalidContinuation` and zero Core calls;
  syntactically valid-looking tokens go through Core for MAC, scope/run binding, expiry, and other
  semantic validation. Both paths disclose neither paths nor secrets and share the valid-request
  Core service path.
