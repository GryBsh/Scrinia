# B5 Retention, Pruning, and Recovery Maintenance — Design Specification

**Status:** Approved design
**Date:** 2026-09-15
**Predecessors:**

- `docs/superpowers/specs/2026-09-10-phase-b-roadmap-design.md`
- `docs/superpowers/specs/2026-09-11-journal-convergence-design.md`
- `docs/superpowers/specs/2026-09-12-multi-memory-transaction-design.md`
- `docs/superpowers/specs/2026-09-13-b4-plugin-event-boundary-design.md`

**Scope:** Per-workspace archive retention, bounded journal cleanup, and local operator maintenance

## 0. Execution amendment — current-source recovery-verifier migration (2026-09-16)

This amendment is normative and supersedes §§2.1–2.2 and the recovery-verifier portions of
Task 4 in the companion plan wherever they differ. It does not expand B5's approved product
scope: no public API, new format, index, deletion rule, policy key, or recovery-state transition
is added. It makes the already-approved verifier migration executable against the source currently
in this repository.

### 0.1 One limits contract and exact ceilings

`src/Scrinia.Core/MaintenanceContracts.cs`, `MaintenanceLimits`, is the single owner of every
maintenance and recovery-verification limit. Do not create a second literal-bearing limits class.
Move the verifier fields into this contract and expose one authoritative hard value:

| Field | Normative hard value | Current source to remove |
|---|---:|---|
| maximum bytes, source plus destination total | `16 MiB` | `8 * 1024 * 1024` in `MaintenanceLimits` and `128 * 1024 * 1024` in `RecoveryDiscovery.ReadBounded` |
| absolute monotonic duration | `2 seconds` | `250` milliseconds in `MaintenanceLimits` |
| open attempts | `2` | any per-member/unbounded open behavior |
| stat attempts | `8` | any uncapped `Exists`/attributes/length metadata path |
| read attempts | `257` | any uncapped loop; includes the final zero-byte read |
| read buffer | `64 KiB` | `81_920` in `RecoveryDiscovery.ReadBounded` and `FileMemoryStore.ReadBoundedJournalStream` |

The exact contract is `MaintenanceLimits` (16 MiB, 2 s, 2, 8, 257, 64 KiB), with
`CreateLowerOverride(...)` returning either a lower validated value or typed `InvalidLimits`.
No request, parser, adapter, or helper may carry duplicate byte/duration fields. A missing, zero,
negative, overflowing, non-finite, or above-hard value fails before containment, stat, open, read,
parse, or state work; it is never clamped, defaulted, retried, or partially applied. A source guard
must reject the old literals and every second limits declaration in B5 maintenance/recovery code,
and tests must assert the six exact hard values and that all consumers reference this owner.

### 0.2 Single evidence reader/parser owner

Create `src/Scrinia.Core/RecoveryVerification.cs`. `FileMemoryStore` constructs and owns one
internal `IRecoveryEvidenceVerifier`; the verifier owns the bounded read, incremental SHA-256,
pre/post metadata, and lifetime of `RecoveryVerificationContent`. It returns typed parsed content
and accounting metadata in one result. Content is caller-owned after return, disposed by the caller
in `finally`, borrowed (never copied, retained, or disposed) by parsers, and never reread by a
parser or adapter. The verifier retains at most 16 MiB total content plus the one 64 KiB buffer.

`ParseVerifiedEnvelope` is the sole operation that parses bytes and returns
`RecoveryVerifiedEnvelope`. It accepts `in RecoveryVerificationContent` and verified metadata,
performs the one bounded JSON parse and all envelope validation, and returns the common metadata
(`operationId`, `transactionId`, scope, state, and operation) plus the typed body. No other
operation parses bytes, JSON, or an envelope. Typed journal, restore, coordinator, and file-entity
consumers receive the already parsed `RecoveryVerifiedEnvelope`; operation-id lookup receives its
typed metadata from that same object. No consumer accepts a path, `ReadAllBytes` result, or
long-lived stream, and no consumer performs I/O, JSON parsing, or validation.

The implementation owner is one bounded evidence reader/parser pipeline, not a reader plus
independent parser services. Its typed handoff is equivalent to:

```csharp
internal sealed record RecoveryVerifiedEvidence(
    RecoveryEnvelopeDocument Envelope,
    RecoveryVerificationContent Source,
    RecoveryVerificationContent? Destination,
    RecoveryVerificationAccounting Accounting) : IDisposable;

internal readonly record struct RecoveryVerificationAccounting(
    long BytesRead, int OpenOperations, int StatOperations, int ReadOperations,
    bool FinalZeroByteRead, bool DeadlineObserved, bool CancellationObserved);
```

`IRecoveryEvidenceVerifier.VerifyAsync` returns this typed evidence on `Verified` and the same
accounting fields on every protected outcome. The pipeline may use the common-envelope parser
internally, but it returns the typed envelope/body and metadata once; adapters must not call a
second path-based parser. `RecoveryVerifiedEvidence.Dispose()` disposes both owned content values
and is required in the caller's `finally` block. A metadata-only probe uses the same pipeline and
does not materialize a second buffer.

### 0.3 Exhaustive current-source inventory and replacement map

The following inventory is exhaustive for direct recovery reads, parsers, and on-disk hashes in the
current source. Line numbers are review anchors only; method names and call purpose are authoritative.
Every row is a migration test target and must be named in the source-scan report.

| Current location and operation | Recovery mode/owner | Required replacement |
|---|---|---|
| `FileMemoryStore.CaptureWholeMemberAsync` (`File.ReadAllBytesAsync`, current line 2437) | whole-memory journal capture; `FileMemoryStore` | verifier source-only capture; construct `RawMemberSnapshot` from returned content |
| `FileMemoryStore.VerifyWholeMember` (2441–2448) | whole-memory replay/publication comparison; store | verifier source-only expected-hash check; no whole-file buffer |
| `FileMemoryStore.CaptureRawMemberAsync` (2856–2868) | append capture before journal admission; store | verifier source-only read; preserve cancellation and typed missing result |
| `FileMemoryStore.WriteArchiveMember` (2942–2947) | append archive destination reconciliation | verifier destination-only check against the member hash before treating it as already present |
| `FileMemoryStore.RecoverAppendUnsafeAsync` (3083; journal load 3095) | append replay; store | verifier source read, then one verified envelope parse |
| `FileMemoryStore.ReplayArchiveMembers` (3193) and `PersistAppendJournal` (3235; existing-journal parse 3244) | append replay/journal persistence | verified content parser; never reread existing journal |
| `FileMemoryStore.VerifyAppendMemberAsync` (3271–3307) | append publication verification | verifier with typed expected hash and shared deadline/counters |
| `FileMemoryStore.HasCommittedHash` (3313–3321) | append committed-state check; store | one verifier request for source/destination members; combine returned metadata/hashes |
| `FileMemoryStore.MatchesAppendMember`, `TryRestoreAppendTransaction`, `ReconcileArchiveMember`, `MatchesArchiveBytes` (3324–3419) | append rollback and archive reconciliation | verifier-backed comparisons; recovery failure remains typed and evidence is retained |
| `FileMemoryStore.TryReadAppendTransactionForRollback` (3351–3364) | append rollback journal parse | one verifier read and verified envelope parse; no catch-all collapse |
| `FileMemoryStore.GetOperationId` (renamed from `TryReadOperationId`, 3367–3378) | typed operation-id consumer | accept `in RecoveryVerifiedEnvelope`; return its already parsed operation ID; no I/O, JSON parsing, or validation |
| `FileMemoryStore.HashWorkspaceFile` (3574–3586) and `ReadArtifactAsync` (3618–3638, 3765–3784) | ordinary workspace reads/baselining | **exclusion**: not recovery evidence; do not route through verifier |
| `FileMemoryStore.ReadJournalBytesUnsafe` / `ReadBoundedJournalStream` (4486–4514) | restore discovery/replay stream materialization | replace with verifier-owned content; retain a compatibility test only if the public-internal test seam remains, and make it delegate to the same bounded reader |
| `FileMemoryStore.ApplyPreparedJournalUnsafe` (4457–4483), `VerifyJournalDestinationsUnsafe` and `VerifyJournalDestination` (4516–4535) | restore replay destination integrity | verifier destination checks; replay passes the typed result and does not reopen/read destinations |
| `RecoveryDiscoveryCoordinator.InspectCoordinator` (108–141), through `MultiMemoryTransactionRecordCodec.Read` (related helper, current parser/read at 177 and 449) | B3 coordinator discovery | add the cancellation-aware bounded common-record reader/parser; classify typed missing, invalid-limit, cancellation, and I/O outcomes separately |
| `RecoveryDiscoveryCoordinator.InspectCandidate` / `ReadBounded` (181–209, 239–255) | append/restore/file-entity discovery | verifier source read plus typed envelope parser; remove 128 MiB/81,920 reader |
| `RecoveryDiscoveryCoordinator.GetOperationId` (renamed from `TryJsonOperationId`, 282–295) | discovery typed metadata consumer | accept `in RecoveryVerifiedEnvelope`; return its already parsed operation ID; no I/O, JSON parsing, or validation |
| `B2RecoveryMaintenanceAdapter.VerifyTerminal`, `VerifyTerminalBounded`, `VerifyAppend`, `VerifyRestore`, `VerifyEntity`, `VerifyMember`, `VerifySnapshot`, `VerifyBytes`, `DecodeBounded` (385–489) | completed-evidence cleanup verification | make adapter consume the store-owned verifier result; in-memory base64 decoding remains bounded and supplies expected bytes/hashes only |
| `ParseVerifiedEnvelope` (sole operation; replaces byte-parsing `RecoveryEnvelopeCodec.Parse`/`ParseTypedBody`) | verifier-owned parser | accept verified content and metadata; perform the one bounded JSON parse and all envelope validation; return `RecoveryVerifiedEnvelope` |
| `RecoveryEnvelopeCodec.NormalizeLegacy` (580–586) | typed legacy consumer | accept `in RecoveryVerifiedEnvelope`; normalize its already parsed typed body; no I/O, JSON parsing, or validation |
| `AppendJournalCodec.ConsumeParsed` (renamed from `ParseAndValidate`) | typed journal consumer | accept `in RecoveryVerifiedEnvelope`; consume its typed body; no I/O, JSON parsing, or validation |
| `RestoreJournalCodec.ConsumeParsed` (renamed from `ParseAndValidate`) | typed restore consumer | accept `in RecoveryVerifiedEnvelope`; consume its typed body; no I/O, JSON parsing, or validation |
| `FileEntityRecoveryAdapter.ConsumeParsed` (renamed from `ParseAndNormalize`) | typed file-entity consumer | accept `in RecoveryVerifiedEnvelope`; consume/normalize its typed body; no I/O, JSON parsing, or validation |
| `RecoveryEnvelopeCodec` payload hash checks (257, 946) | decoded in-memory envelope payload integrity | **exclusion**: hashes bytes already decoded from the envelope; retain local hashing, but never let it read a path |
| `AppendRecoveryAdapter.RecoverCommittedCleanup` (619–648) | append cleanup/replay | verifier-backed member checks before cleanup; only `Verified` permits cleanup |
| `RestoreRecoveryAdapter.VerifyFile` (817–822) | restore destination check | verifier destination result; no `File.ReadAllBytes` or second open |
| `FileEntityRecoveryAdapter.VerifyCommittedSnapshot` (1054–1063) | file-entity committed snapshot check | verifier destination result; decoded snapshot remains expected in-memory data |
| `FileEntityTransactionCoordinator.Capture` (146–152) | file-entity capture | inject the store-owned verifier/factory; preserve raw snapshot ownership |
| `FileEntityTransactionCoordinator.Verify` (176–191) | file-entity publication verification | verifier with expected hash; hash of `expected` is in-memory and may remain local |
| `FileEntityTransactionCoordinator.RecoverPendingEvidence` (292–346) | file-entity discovery and replay | one verifier read per evidence file, one typed parse, then pass the same cancellation token to replay |

Legitimate non-recovery `ReadAllBytes`/hash exclusions are: `AppendTransaction` and `RestoreJournal`
in-memory payload hashing; `RecoveryEnvelope`/`RestoreJournal` decoded-payload integrity hashing;
ordinary bundle, configuration, migration, embedding/vector, and workspace-file baselining reads;
and any hash over bytes already owned in a transaction snapshot. The guard must prove that an
excluded helper is not called from a recovery path. `MultiMemoryTransactionRecordCodec.Read` and
other related recovery helpers are not exclusions merely because they are outside the four primary
files.

### 0.4 Accounting, outcomes, and cooperative cancellation

The verifier checks the single absolute monotonic deadline and cancellation before every stat, open,
and read, and between read chunks. Bytes count all source and destination bytes. Opens count every
handle-open attempt; stats count every metadata attempt (`Exists`, attributes, length, and final
stability stat); reads count every `ReadAsync` attempt, including the final zero-byte read. Counters
never reset for a member, retry, or parser. A final zero-byte read is required to establish EOF and
is charged; exceeding any budget returns a typed outcome rather than a generic exception.

Use typed outcomes: `InvalidLimits` is the fail-fast configuration/programmer error and maps to
`RecoveryProtectionStatus.InvalidVerificationLimits`, `RecoveryBlockReason.InvalidVerificationLimits`,
and top-level `RecoveryRequired`, with zero I/O counters. Runtime protected evidence outcomes are
`Missing`, `Oversized`, `Timeout`, `Cancellation`, `Blocked`, `Unsafe`, `HashMismatch`, and `IoFailure`.
They map to the existing typed recovery/read-barrier vocabulary, retain evidence, and perform no
destination, journal, rollback, index, cache, or topic/facts mutation. There is no `catch (Exception)`
that collapses these classes; only explicitly classified filesystem/parser exceptions may be mapped,
and unexpected exceptions remain diagnostic failures without authorizing progress.

Thread cancellation through the complete recovery call graph. Exact affected signatures are:
`FileMemoryStore.DiscoverRecoveryForScope(string scope, string storeDirectory, CancellationToken cancellationToken)`;
`RecoveryDiscoveryCoordinator.Discover(string scope, string storeDirectory, CancellationToken cancellationToken, bool includeCoordinator = true)`;
its `DiscoverCore`, `InspectCoordinator`, `InspectFixedCandidate`, `InspectCandidate`, and entity enumeration helpers;
`FileMemoryStore.RecoverScopeUnsafe(string scope, string storeDir, bool recoverCoordinator, CancellationToken cancellationToken)`;
`ReadJournalBytesUnsafe(..., CancellationToken)` and `ReadBoundedJournalStream(..., CancellationToken)`;
`FileEntityTransactionCoordinator.RecoverPendingEvidence(..., CancellationToken cancellationToken)`;
and `MultiMemoryTransactionRecordCodec.Read(..., CancellationToken cancellationToken)`.
Update every caller, including synchronous/asynchronous recovery barriers, append participant recovery,
whole-memory transaction recovery, coordinator recovery, and scheduler maintenance. Remove
`CancellationToken.None` from these affected paths, including the current call in
`RecoverPendingEvidence` at line 337 and the `VerifyTerminal` convenience path at line 386;
use the active token or an explicitly-created linked deadline token. `CancellationToken.None` may
remain only in tests or non-recovery, in-memory helpers proven outside this inventory.

### 0.5 Migration guard and acceptance gate

Add a source-scan test covering the complete recovery regions of `FileMemoryStore.cs`,
`RecoveryDiscovery.cs`, `RecoveryEnvelope.cs`, and `FileEntityTransactionCoordinator.cs`, plus
`MultiMemoryTransactionRecord.cs`, `AppendJournal.cs`, `RestoreJournal.cs`, and related recovery
helpers. It fails on direct path-based whole-file reads, whole-file hashes, unbounded JSON parsers,
the old limits, `CancellationToken.None`, the four superseded consumer names, any parser other than
`ParseVerifiedEnvelope`, or any typed consumer that parses/validates or lacks the already parsed
`RecoveryVerifiedEnvelope` in an inventory row. It allowlists only the explicit in-memory/non-recovery exclusions above and asserts those
helpers are unreachable from recovery call sites. Each inventory row gets a focused test for
successful bounded migration, budget/cancellation behavior, typed failure mapping, and no second
read. Acceptance also asserts cursor/protection/unresolved retention, no index, no deletion on
non-verified outcomes, unchanged public surface, and the exact paging equations already specified.

## 0.6 Review-resolution amendment — authoritative limits, complete inventory, and ownership

This amendment is normative and supersedes any earlier wording that names `RecoveryVerificationLimits`
as an owner or permits an independent operation probe. It changes no B5 product scope, safety rule,
wire format, public API, or non-goal.

### One limits owner and migration

`MaintenanceLimits` in `src/Scrinia.Core/MaintenanceContracts.cs` is the sole owner of all
maintenance and recovery-verification limits. It owns exactly one immutable hard tuple:
`MaximumBytes = 16 MiB`, `MaximumDuration = 2 seconds`, `MaximumOpenOperations = 2`,
`MaximumStatOperations = 8`, `MaximumReadOperations = 257`, and `ReadBufferBytes = 64 KiB`,
plus the sole lower-only override validator. The existing byte/time fields are migrated in place;
all terminal-verification consumers use this expanded contract.

`RecoveryVerificationLimits` and `RecoveryVerificationLimits.Hard` are removed, not retained as a
second value object or compatibility alias. A request carries one effective `MaintenanceLimits`
value. No other type may declare defaults, hard values, limit literals, or validation. The source
guard rejects a second declaration, second `Hard`, second validator, and old literals; tests prove
source/reflection ownership, exact hard values, lower-only overrides, and zero I/O for invalid input.

### Complete one-row-per-operation recovery-read inventory

Every row is an independent migration target and test target. “Metadata” includes `Exists`,
attributes/reparse kind, length, and final stability. Each row explicitly names the owner, mode,
token and one absolute monotonic deadline source, disposal rule, and focused test; no row may use a
path parser or reread content. The final two rows are retained in the inventory to prove the named
§0.3 exclusions remain outside recovery.

| Direct operation | Owner | Mode | Token / deadline source | Disposal rule | Focused test |
|---|---|---|---|---|---|
| `MultiMemoryTransactionRecord.ReadBounded` | `IRecoveryEvidenceVerifier` | coordinator source; pre/post metadata; no destination | discovery token; verifier deadline from effective `MaintenanceLimits` | caller disposes source in `finally` | coordinator bounded read, accounting, and parse-once |
| `RecoveryDiscoveryCoordinator.InspectCoordinator` | discovery coordinator | coordinator admission; no independent read | discovery token; same verifier deadline | disposes returned source in `finally` | coordinator admission maps each typed outcome |
| `MultiMemoryTransactionRecordCodec.ConsumeParsed` (typed consumer replacing `Read`) | coordinator typed consumer | accepts `in RecoveryVerifiedEnvelope`; consumes typed coordinator metadata; no I/O, JSON parsing, or validation | discovery token; no new operation | codec never disposes or retains; caller `finally` | cancellation-aware verified record consumption |
| `RecoveryDiscovery.ReadBounded` | `IRecoveryEvidenceVerifier` | candidate journal source; pre/post metadata; no destination | discovery token; verifier deadline | caller disposes source in `finally` | candidate bounded read and final zero-byte read |
| `RecoveryDiscoveryCoordinator.InspectCandidate` | discovery coordinator | candidate source admission; typed envelope parse | discovery token; one deadline across read and parse | caller `finally` disposes source | candidate missing/invalid/IO mapping |
| `RecoveryDiscoveryCoordinator.GetOperationId` (renamed from `TryJsonOperationId`) | parsed-envelope result owner | metadata-only operation lookup; no read/stat | discovery token from enclosing verification; no new deadline | no content ownership; enclosing caller disposes | proves no independent probe/read |
| `CaptureWholeMemberAsync` | `FileMemoryStore` through verifier | source member capture; pre/post metadata; no destination | active store token; verifier deadline | raw snapshot/content disposed by store `finally` | whole-member capture uses one bounded read |
| `VerifyWholeMember` | `FileMemoryStore` through verifier | source expected-hash comparison; pre/post metadata | active store token; shared deadline | any content disposed by store `finally` | publication comparison and mismatch accounting |
| `CaptureRawMemberAsync` | `FileMemoryStore` through verifier | append source snapshot; pre/post metadata; no destination | append token; shared deadline | snapshot disposed on success and failure in `finally` | raw capture preserves cancellation/missing result |
| `WriteArchiveMember` | `FileMemoryStore` through verifier | archive destination hash check; pre/post metadata; no source reread | append token; shared deadline | destination content disposed in `finally` | destination check never rereads source |
| `RecoverAppendUnsafeAsync` | `FileMemoryStore` through verifier | append journal source plus one typed parse | recovery token; one deadline for read/parse | evidence disposed in `finally` | append recovery parse-once |
| `ReplayArchiveMembers` | `FileMemoryStore` through verifier | each member source/destination check; metadata per member | recovery token; one deadline/counter set for the replay | every acquired member disposed in reverse-order `finally` | multi-member accounting and partial acquisition |
| `PersistAppendJournal` | `FileMemoryStore` through verifier | existing journal source plus one typed parse | persistence token; shared deadline | source disposed in `finally` | existing journal is not reread |
| `VerifyAppendMemberAsync` | `FileMemoryStore` through verifier | source and optional destination; pre/post metadata | append token; one shared deadline | all returned content disposed in outer `finally` | paired hash verification and counters |
| `HasCommittedHash` | `FileMemoryStore` through verifier | source/destination committed-state check; pre/post metadata | recovery token; one shared deadline | both slots disposed in outer `finally` | combines one verifier result without second buffer |
| `MatchesAppendMember` | `FileMemoryStore` through verifier | recorded source/destination comparison | rollback token; shared deadline | caller disposes both in `finally` | rollback member match is bounded |
| `TryRestoreAppendTransaction` | `FileMemoryStore` through verifier | recorded transaction member comparison | rollback token; shared deadline | partial results disposed in `finally` | restore rollback mismatch retains evidence |
| `ReconcileArchiveMember` | `FileMemoryStore` through verifier | archive source/destination reconciliation | reconciliation token; shared deadline | every member disposed in `finally` | archive reconciliation has no direct hash read |
| `MatchesArchiveBytes` | `FileMemoryStore` through verifier | archive comparison against expected hash | reconciliation token; shared deadline | verifier content disposed by caller `finally` | expected hash and mismatch mapping |
| `TryReadAppendTransactionForRollback` | `FileMemoryStore` plus envelope codec | rollback journal source; one verified envelope parse | rollback token; shared deadline | source disposed in `finally` | malformed rollback journal is typed, not collapsed |
| `GetOperationId` (renamed from `TryReadOperationId`) | typed metadata consumer | `in RecoveryVerifiedEnvelope`; operation ID from already-parsed metadata; no I/O, JSON parsing, or validation | enclosing recovery token/deadline; no new operation | no independent ownership | operation ID comes from the sole parse-once result |
| `ReadJournalBytesUnsafe` | `FileMemoryStore` through verifier | restore journal source; pre/post metadata; no long-lived stream | restore token; shared deadline | returned content caller-owned and disposed in `finally` | restore read ownership and cancellation |
| `ReadBoundedJournalStream` | verifier-owned content adapter | bounded restore source content; no stream escape | restore token; shared deadline | caller disposes content; stream cannot outlive it | compatibility seam delegates to bounded reader |
| `ApplyPreparedJournalUnsafe` | `FileMemoryStore` | prepared verified content application; no destination reread | restore token; enclosing absolute deadline | prepared content disposed by outer recovery `finally` | apply path performs no second read |
| `VerifyJournalDestinationsUnsafe` | `FileMemoryStore` through verifier | restore destination pair checks; no source reread | restore token; shared deadline | all destination content disposed in `finally` | destination pair integrity |
| `VerifyJournalDestination` | `FileMemoryStore` through verifier | one restore destination; pre/post metadata | restore token; shared deadline | destination disposed in `finally` | destination missing/replaced mapping |
| `B2RecoveryMaintenanceAdapter.VerifyTerminal` | B2 recovery maintenance adapter | terminal evidence dispatch; typed verifier result only | active recovery token; verifier deadline | adapter disposes any returned content in `finally` | terminal dispatch has no independent probe |
| `B2RecoveryMaintenanceAdapter.VerifyTerminalBounded` | B2 recovery maintenance adapter | bounded terminal verification; source/destination as requested | active recovery token; one absolute deadline | outer `finally` disposes every acquired buffer | exact limits and all protected outcomes |
| `B2RecoveryMaintenanceAdapter.VerifyAppend` | B2 recovery maintenance adapter | append evidence source/destination verification | append recovery token; shared deadline | source/destination disposed in `finally` | append cleanup authorization only on `Verified` |
| `B2RecoveryMaintenanceAdapter.VerifyRestore` | B2 recovery maintenance adapter | restore evidence/destination verification | restore recovery token; shared deadline | all content disposed in `finally` | restore protected-result mapping |
| `B2RecoveryMaintenanceAdapter.VerifyEntity` | B2 recovery maintenance adapter | file-entity evidence verification | entity recovery token; shared deadline | content disposed in `finally`; expected bytes remain caller-owned | entity verification and ownership |
| `B2RecoveryMaintenanceAdapter.VerifyMember` | B2 recovery maintenance adapter | one member source/destination check | active recovery token; shared deadline | acquired content disposed in `finally` | member hash/accounting |
| `B2RecoveryMaintenanceAdapter.VerifySnapshot` | B2 recovery maintenance adapter | committed snapshot destination check | entity token; shared deadline | destination disposed in `finally` | snapshot mismatch and retention |
| `B2RecoveryMaintenanceAdapter.VerifyBytes` | B2 recovery maintenance adapter | expected in-memory bytes versus verified file | active recovery token; shared deadline | file content disposed in `finally`; expected bytes not disposed here | in-memory expected hash remains local |
| `B2RecoveryMaintenanceAdapter.DecodeBounded` | B2 recovery maintenance adapter | bounded in-memory base64 decode; no filesystem I/O | enclosing recovery token; same absolute deadline/accounting context | decoded expected bytes owned and disposed by adapter `finally` | bounded decode limit and no path read |
| `ParseVerifiedEnvelope` | verifier-owned parser | sole operation that parses bytes/JSON and validates the envelope; returns `RecoveryVerifiedEnvelope` | enclosing recovery token; verifier deadline, no reset | caller disposes source/destination content in outer `finally` | exactly one bounded parse and one returned envelope |
| `RecoveryEnvelopeCodec.NormalizeLegacy` | typed legacy consumer | normalization of already parsed in-memory envelope; no I/O, JSON parsing, or validation | enclosing recovery token; no new deadline | borrowed parsed object; outer caller `finally` | legacy normalization has no path access |
| `AppendJournalCodec.ConsumeParsed` | typed journal consumer | consumes already parsed typed body; no I/O, JSON parsing, or validation | enclosing recovery token; no new operation | borrowed envelope; outer caller `finally` | journal consumer cannot parse bytes |
| `RestoreJournalCodec.ConsumeParsed` | typed restore consumer | consumes already parsed typed body; no I/O, JSON parsing, or validation | enclosing recovery token; no new operation | borrowed envelope; outer caller `finally` | restore consumer cannot parse bytes |
| `FileEntityRecoveryAdapter.ConsumeParsed` | typed file-entity consumer | consumes/normalizes already parsed typed body; no I/O, JSON parsing, or validation | enclosing recovery token; no new operation | borrowed envelope; outer caller `finally` | file-entity consumer cannot parse bytes |
| `RecoveryEnvelopeCodec` payload hash checks | `RecoveryEnvelopeCodec` | decoded in-memory payload integrity; no filesystem metadata | existing recovery token; no new filesystem deadline | existing payload owner controls disposal | local payload hash remains an approved exclusion |
| `AppendRecoveryAdapter.RecoverCommittedCleanup` | append recovery adapter through store verifier | committed member checks before cleanup | recovery token; shared deadline | all verifier content disposed in `finally` | only `Verified` yields cleanup decision |
| `RestoreRecoveryAdapter.VerifyFile` | restore recovery adapter through store verifier | destination check; no source reread | restore token; shared deadline | destination disposed in `finally` | destination verification has no second open |
| `FileEntityRecoveryAdapter.VerifyCommittedSnapshot` | file-entity recovery adapter through verifier | destination body/metadata; expected snapshot in memory | entity token; shared deadline | destination disposed in `finally`; expected snapshot remains caller-owned | snapshot result and retention |
| `FileEntityTransactionCoordinator.Capture` | coordinator through injected store verifier | entity source body/metadata capture | capture token; shared deadline | raw snapshot disposed in `finally` | capture ownership and local payload hash exclusion |
| `FileEntityTransactionCoordinator.Verify` | coordinator through injected store verifier | entity destination versus in-memory expected bytes | verify token; shared deadline | destination disposed in `finally` | expected bytes are not reread |
| `FileEntityTransactionCoordinator.RecoverPendingEvidence` | coordinator through store verifier | evidence source plus one envelope parse, then replay | recovery token propagated to replay; one absolute deadline | evidence disposed in `finally` before return | cancellation reaches replay and no second read |
| `HashWorkspaceFile` | `FileMemoryStore` ordinary path | non-recovery workspace baseline; outside verifier | ordinary caller token/deadline | ordinary caller ownership | source guard proves unreachable from recovery |
| `ReadArtifactAsync` | `FileMemoryStore` ordinary path | non-recovery artifact read; outside verifier | ordinary caller token/deadline | ordinary caller owns returned bytes | source guard proves unreachable from recovery |

### Parse-once API, disposal, cancellation, and metadata accounting

The sole parser boundary is exactly
`ParseVerifiedEnvelope(in RecoveryVerificationContent, in RecoveryVerifiedFileMetadata)`, which
parses bytes once, validates the envelope once, and returns one `RecoveryVerifiedEnvelope` with
operation ID, transaction ID, scope, state, operation, and typed body. The chosen migration option
is **rename, not delete**, for the four legacy consumers: `AppendJournalCodec.ConsumeParsed`,
`RestoreJournalCodec.ConsumeParsed`, `FileEntityRecoveryAdapter.ConsumeParsed`, and
`FileMemoryStore.GetOperationId` (with the discovery equivalent
`RecoveryDiscoveryCoordinator.GetOperationId`). Each accepts the already parsed envelope or its
typed metadata and performs no I/O, JSON parsing, or validation. The old
`AppendJournalCodec.ParseAndValidate`, `RestoreJournalCodec.ParseAndValidate`,
`FileEntityRecoveryAdapter.ParseAndNormalize`, and `TryReadOperationId` names must not remain.
Parsers/consumers borrow, never copy, retain, dispose, or reopen content.

The exact typed-handoff signatures are:

```csharp
AppendJournalCodec.ConsumeParsed(in RecoveryVerifiedEnvelope envelope)
RestoreJournalCodec.ConsumeParsed(in RecoveryVerifiedEnvelope envelope)
MultiMemoryTransactionRecordCodec.ConsumeParsed(in RecoveryVerifiedEnvelope envelope)
FileEntityRecoveryAdapter.ConsumeParsed(in RecoveryVerifiedEnvelope envelope)
FileMemoryStore.GetOperationId(in RecoveryVerifiedEnvelope envelope)
RecoveryDiscoveryCoordinator.GetOperationId(in RecoveryVerifiedEnvelope envelope)
```

The first three retain their existing typed return values; the operation-id consumers return the
already parsed `OperationId`. These signatures accept no bytes, path, stream, expected scope, or
validation options. The only byte-parsing signature is
`ParseVerifiedEnvelope(in RecoveryVerificationContent content,
in RecoveryVerifiedFileMetadata metadata) -> RecoveryVerifiedEnvelope`.

| Call-site path | Ownership and required `finally` |
|---|---|
| source-only or destination-only | caller disposes returned content in `finally` |
| source plus destination | one evidence owner disposes both in an outer `finally`, including destination-open failure |
| multi-member replay | caller disposes every acquired member in reverse order in `finally` |
| parser success or exception | parser never takes ownership; caller's outer `finally` always disposes |
| partial acquisition/cancellation/timeout | null slots are skipped; every returned buffer is disposed; disk evidence remains |

The complete current `CancellationToken.None` inventory is: `BackgroundWork.Register` (126),
`BackgroundWork` delay (205), and `BackgroundWork.Register` (304) — allowed infrastructure;
`FileEntityTransactionCoordinator.RecoverPendingEvidence` (337), `FileMemoryStore.PruneCommittedArchives`
(2874), `RecoverAppendUnsafeAsync` (3151), `RecoverScopeUnsafe` (4384), recovery admission (4394),
`MultiMemoryTransactionCoordinator` participant update (606), and `RecoveryDiscovery.VerifyTerminal`
(386) — required propagation; `FileLock.Acquire` (73), `ProcessRunner` stdout/stderr (41/42) and
waits (56/59) — allowed non-recovery helpers; `FileMemoryStore` rollback write (2284) and
`MultiMemoryTransactionCoordinator` rollback (776/782) — explicit compensation exceptions.
The source guard is a table-driven exact `(relative file, containing member, call shape, class)`
allowlist, not a line-number search. It rejects `None` in recovery regions and new occurrences in
allowed members unless recorded. Compensation cannot authorize forward progress.

The verifier accounts for every `Exists`, attributes/reparse-kind, length, and final stability stat;
each attempted operation consumes one `StatOperations` unit, including failure. A completed
source-only or destination-only check has exactly two stats (pre/post); a paired check exactly four,
unless an earlier typed failure stops it. Each open attempt and every read, including final zero-byte
EOF, is charged once; counters and the single deadline never reset. Any future metadata-only path
must use a separately named bounded metadata abstraction with the same counters, deadline, token,
and tests; ad hoc metadata calls are forbidden.

### Task 4 dependency boundary

Task 4 depends at the Core type/project level on recovery contracts, containment, filesystem, and
hashing only. It has neither a compile-time dependency on `MaintenanceCursor`, codec, registry,
planner, or policy nor a runtime dependency on cursor decoding, registry state, candidate ordering,
or cursor lifetime. Task 5 consumes the verifier result after Task 4; Task 4 never creates or
validates a maintenance cursor.

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

### 2.1 Shared bounded recovery verification contract

Task 4 replaces every recovery-path whole-file read and whole-file hash with one shared,
fail-closed verifier. The contract is internal to Core and is owned by `FileMemoryStore`: the store
constructs the verifier with the active scope binding, passes it only already-canonicalized paths
and typed expected hashes, and maps its result into the existing recovery barrier and typed
maintenance protection result. The verifier is not a general file reader, is not used by ordinary
artifact reads, workspace file-attribute baselining, archive discovery, or unrelated configuration
loading, and has no delete, write, journal-rewrite, index, cache, plugin, or CLI authority.

The exact shared contract is:

```csharp
internal interface IRecoveryEvidenceVerifier
{
    ValueTask<RecoveryVerificationResult> VerifyAsync(
        RecoveryVerificationRequest request,
        CancellationToken cancellationToken);
}

internal readonly record struct RecoveryVerificationRequest(
    RecoveryScopeBinding Scope,
    RecoveryEvidenceKind Kind,
    string SourceRelativeName,
    string? DestinationRelativeName,
    RecoveryExpectedHash SourceHash,
    RecoveryExpectedHash? DestinationHash,
    MaintenanceLimits Limits);

internal readonly record struct MaintenanceLimits(
    long MaximumBytes,
    TimeSpan MaximumDuration,
    int MaximumOpenOperations,
    int MaximumStatOperations,
    int MaximumReadOperations,
    int ReadBufferBytes);

`MaintenanceLimits` is the one authoritative limits source. Its immutable hard value is
`(16 MiB, 2 seconds, 2 opens, 8 stats, 257 reads, 64 KiB)`; `CreateLowerOverride(...)` accepts only
positive/in-range values no greater than the hard value and returns either the effective lower limit
or the typed `InvalidLimits` validation failure. The request contains no
parallel byte or duration fields. Precedence is exact: hard ceiling, then the validated per-request
lowering, then the verifier's single monotonic deadline derived from that effective duration. A
missing, zero, negative, overflowing, non-finite, or otherwise malformed override is invalid; it is
not clamped, defaulted, retried, or partially applied. Invalid limits return before containment,
stat, open, read, parser, journal, destination, index, cache, or state work begins.

For parser consumers, a verified read returns bounded owned content rather than requiring a second
file read:

```csharp
internal sealed class RecoveryVerificationContent : IDisposable
{
    internal ReadOnlyMemory<byte> Bytes { get; }
    internal RecoveryVerifiedFileMetadata Metadata { get; }
    public void Dispose();
}

internal readonly record struct RecoveryVerifiedFileMetadata(
    long Length,
    string Sha256,
    bool RegularFile,
    bool Contained,
    bool StableAfterRead);
```

`RecoveryVerificationResult` carries one `SourceContent` and, when requested, one
`DestinationContent`; either is null for a non-verified result. The verifier owns both buffers until
the result is returned, transfers ownership to the caller, and never retains them after return. The
caller must dispose each content object in a `finally` block after parsing/consuming it; parsers borrow
the memory for the duration of the call and never dispose, retain, or copy it. Parser methods take
`in RecoveryVerificationContent` (or the pair) plus verified metadata, not `byte[]`, a new
path-based read, or a stream that outlives the result. The verifier retains at most the effective
byte ceiling of verified content in total across both members, in addition to its one fixed 64 KiB
read buffer; bounded parsing therefore proves that each file is read and hashed once without an
unbounded allocation.

internal readonly record struct RecoveryVerificationResult(
    RecoveryVerificationOutcome Outcome,
    long BytesRead,
    int OpenOperations,
    int StatOperations,
    int ReadOperations,
    bool FinalZeroByteRead,
    bool DeadlineObserved,
    bool CancellationObserved,
    RecoveryVerificationContent? SourceContent,
    RecoveryVerificationContent? DestinationContent);

internal enum RecoveryVerificationOutcome : byte
{
    Verified = 1,
    ProtectedOversized = 2,
    RecoveryRequiredTimeout = 3,
    RecoveryRequiredCancellation = 4,
    ProtectedBlocked = 5,
    ProtectedMissing = 6,
    ProtectedHashMismatch = 7,
    ProtectedUnsafe = 8,
    ProtectedIoFailure = 9,
    ProtectedInvalidLimits = 10
}
```

For example, a successful source-only result still reports the accounting observations explicitly:

```text
RecoveryVerificationResult {
  Outcome = Verified, BytesRead = 4096, OpenOperations = 1, StatOperations = 2,
  ReadOperations = 2, FinalZeroByteRead = true, DeadlineObserved = false,
  CancellationObserved = false, SourceContent = owned, DestinationContent = null
}
```
The same three boolean fields are present on an early `ProtectedMissing`, an
`ProtectedInvalidLimits` zero-I/O result, and every other outcome; their values reflect what was
actually observed before that outcome was returned.

The hard verifier limits are `MaximumBytes = 16 MiB`, `MaximumDuration = 2 seconds`,
`MaximumOpenOperations = 2`, `MaximumStatOperations = 8`, `MaximumReadOperations = 257`, and a
`ReadBufferBytes = 64 KiB`. A request may lower a limit but may not raise it; invalid limits are a
typed protected result and never start I/O. The byte count includes every source and destination
byte read, and the read count includes every `ReadAsync` attempt, including a final zero-byte read.
Every outcome, including `Verified`, invalid limits, and every protected/runtime failure, carries
`FinalZeroByteRead`, `DeadlineObserved`, and `CancellationObserved`. These fields describe observed
execution rather than inferred cause; `FinalZeroByteRead` is false when the result returns before
EOF, and the two observation fields may both be true. Callers preserve all three fields when mapping
outcomes and never synthesize or default them.
The open count includes source and destination handles; stat count includes every file/directory
metadata operation; no retry may reset any counter. Source and optional destination must be regular
files, remain within the already-bound scope/evidence destination, and match their typed expected
hashes. A missing member is `ProtectedMissing`; a different hash is `ProtectedHashMismatch`; an
oversized member or byte-limit breach is `ProtectedOversized`; a limit reached before completion is
`RecoveryRequiredTimeout`; cancellation observed before a new operation or between read chunks is
`RecoveryRequiredCancellation`; sharing/locking denial is `ProtectedBlocked`; containment,
unexpected kind, malformed identity, or other safety failure is `ProtectedUnsafe`; and other
I/O failure is `ProtectedIoFailure`; invalid limits are `ProtectedInvalidLimits`. Only `Verified` can authorize recovery progress or expose
evidence as a completed-maintenance candidate. Every other outcome retains the evidence and maps to
the existing recovery-required/read-barrier failure vocabulary without embedding paths, content, or
exception text in the result. The exact invalid-limit mapping is
`ProtectedInvalidLimits` → `RecoveryProtectionStatus.InvalidVerificationLimits` and
`RecoveryBlockReason.InvalidVerificationLimits`, with top-level `RecoveryRequired`. No invalid-limit
result may fall through to a generic exception or a successful/progress result; all other
non-verified outcomes map to their corresponding typed protected reason and `RecoveryRequired`.

The verifier validates containment and expected destination identity before opening anything, stats
each member before opening it, opens at most the two validated members, hashes incrementally into a
fixed buffer, checks the byte/deadline/cancellation limits before each operation and between every
read, and re-stats each member after hashing to detect replacement/length changes. A destination is
never created, repaired, or compared by rewriting it. A successful verification does not mutate the
recovery journal, archive, live artifact, sidecar, index, topic/facts projection, or cache; the
caller owns any later typed state transition and invalidates derived state only after a separate
successful deletion or write operation.

The duration is a monotonic absolute deadline shared by the complete verification, not a fresh
timeout per member or retry. Filesystem APIs may block cooperatively: cancellation and deadline
checks cannot preempt an already-running synchronous OS `stat`, `open`, or `ReadAsync` operation.
The contract therefore promises no new operation after the deadline/cancellation is observed and
returns the typed timeout/cancellation outcome when the in-flight operation returns; it does not
claim a hard wall-clock upper bound for an uncooperative filesystem. No detached task, `Task.Run`,
unbounded retry, `ReadAllBytes`, `ReadAllText`, `JsonDocument.Parse(File.ReadAllBytes(...))`, or
whole-file `SHA256.HashData(File.ReadAllBytes(...))` recovery path is permitted. All existing
FileMemoryStore recovery capture, publication verification, rollback matching, archive-member
reconciliation, committed-hash verification, journal parsing/operation-id fallback, and restore
verification helpers must route through this contract or through a bounded typed parser that first
uses it; ordinary non-recovery file hashing is outside this replacement set.

The verifier is dependency-injected internally for deterministic tests, but no public abstraction,
MCP tool, CLI option, configuration key, or plugin hook is added. Its only dependencies are the
existing scope/path-containment, cancellation/deadline, incremental SHA-256, and filesystem
abstractions; it must not depend on maintenance cursors, an archive index, cache contents, or
recovery state that it is responsible for proving.

### 2.2 Task 4 production inventory and parser boundary

`src/Scrinia.Core/FileMemoryStore.cs` is explicitly part of Task 4's production inventory. Every
current recovery read/hash site below must migrate; changing only the verifier helper is not
sufficient:

| Current site | Required migration |
|---|---|
| `FileMemoryStore.CaptureRawMemberAsync` (`ReadAllBytesAsync`, current line 2866) | Shared verifier source-only read/hash; build the raw snapshot from verified content without rereading. |
| `FileMemoryStore` publication/whole-member checks (current lines 2437–2446) | Shared verifier with the typed expected hash, including post-read stability. |
| `FileMemoryStore.HasCommittedHash` (current lines 3313–3321) | One source/destination verification request; combine verified hashes/metadata without a second whole-file buffer. |
| `FileMemoryStore.MatchesAppendMember` and rollback state helpers (current lines 3324–3339) | Shared verifier for each recorded member; no direct whole-file hash helper. |
| `FileMemoryStore.RecoverAppendUnsafeAsync` journal load and `GetOperationId` fallback (renamed from `TryReadOperationId`, current lines 3092–3111 and 3357–3371) | `ParseVerifiedEnvelope` parses once; pass its `RecoveryVerifiedEnvelope` to typed journal consumption and `GetOperationId`. |
| `FileMemoryStore.PersistAppendJournal` existing-journal parse (current lines 3242–3245) | Verify once and pass borrowed content to the journal/envelope parser; do not reread the existing journal. |
| `FileMemoryStore.VerifyJournalDestinationsUnsafe` / `VerifyJournalDestination` (current lines 4516–4535) | Shared verifier-backed destination checks; no whole-file read. |
| `RecoveryDiscovery.cs` journal parsing and archive-member hashes (current lines 287 and 473–474) | Verify journal content before parsing and use verifier-backed member checks. |
| `RecoveryEnvelope.AppendRecoveryAdapter.RecoverCommittedCleanup` (current lines 619–648) | Verify committed members before deletion and pass the typed result into cleanup. |
| `RecoveryEnvelope.RestoreRecoveryAdapter.VerifyFile` (current lines 817–822) | Consume verified destination content/metadata; it must not open or read the destination. |
| `RecoveryEnvelope.FileEntityRecoveryAdapter.VerifyCommittedSnapshot` (current lines 1054–1063) | Same verifier-backed destination check; decoded payload supplies expected metadata only. |
| `FileEntityTransactionCoordinator` capture/hash paths (current lines 151, 187–188) | Route on-disk capture and comparison through the store-owned verifier; retain only in-memory payload hashing locally. |
| `FileEntityTransactionCoordinator.RecoverPendingEvidence` envelope load (current lines 294–306) | Verify once, call `ParseVerifiedEnvelope`, and pass the returned envelope to `FileEntityRecoveryAdapter.ConsumeParsed`; remove the whole-file read. |

Approved non-recovery exclusions are in-memory transaction-payload hashes (`AppendTransaction`,
`RestoreJournal`, and decoded envelope payload validation), ordinary bundle/configuration/migration
reads, embedding/vector hashes, and workspace-file attribute baselining (`MemoryDerivation`). These
are excluded only when they do not prove a recovery file or destination; an excluded helper may not
be called by a recovery path. The source guard scans the complete recovery regions of
`FileMemoryStore.cs`, `RecoveryEnvelope.cs`, `RecoveryDiscovery.cs`, and
`FileEntityTransactionCoordinator.cs`, plus related recovery helpers, and fails on every unapproved
whole-file read/hash or parser call.

Parser consumption is implementable through `RecoveryVerificationContent`: a verifier result owns
bounded `ReadOnlyMemory<byte>` plus `RecoveryVerifiedFileMetadata` for each requested member. The
caller owns the content after return and disposes it in `finally` after the sole parser and typed
consumers complete. `ParseVerifiedEnvelope(in RecoveryVerificationContent,
in RecoveryVerifiedFileMetadata)` is the only operation that parses bytes, parses JSON, or validates
the envelope and returns `RecoveryVerifiedEnvelope`. `AppendJournalCodec.ConsumeParsed`,
`RestoreJournalCodec.ConsumeParsed`, `FileEntityRecoveryAdapter.ConsumeParsed`,
`FileMemoryStore.GetOperationId`, and `RecoveryDiscoveryCoordinator.GetOperationId` accept only
that parsed envelope or its typed metadata (not a path, fresh `byte[]`, or long-lived stream) and
perform no I/O, JSON parsing, or validation. The verifier's total
16 MiB allocation covers source and destination content together, so parser consumption performs no
second whole-file read. The bounded restore stream primitive must produce this same owned contract,
not an untyped whole-file buffer.

## 3. Workspace policy schema and defaults

The workspace configuration remains the existing flat `.scrinia/config.json` dictionary. B5 adds no
nested configuration document and no new metadata file. The following keys are the complete B5
policy surface; absent keys use the stated defaults.

| Key | Value | Default | Meaning |
|---|---|---:|---|
| `Scrinia:Retention:MaxArchivedVersions` | positive integer | `10` | Maximum retained archive pairs per subject after eligible pruning; current and protected pairs are additional and are never displaced by this limit. |
| `Scrinia:Retention:MinimumAgeDays` | non-negative integer | `30` | An archive pair must be at least this old before age-based deletion. |
| `Scrinia:Maintenance:JournalCleanupBatchSize` | integer `1..50` | `50` | Single invocation-wide budget for all journal candidates admitted to inspection across all requested scopes. |
| `Scrinia:Maintenance:TimeBudgetSeconds` | positive integer | `5` | Maximum maintenance work time after admission; the service returns a continuation indication when the budget expires. |
| `Scrinia:Maintenance:BackgroundEnabled` | `true` or `false` | `true` | Enables the local background adapter. It does not affect explicit operator maintenance. |

The defaults are conservative: existing archive behavior already keeps ten versions, cleanup does
not consider a version younger than thirty days, each invocation admits at most fifty journal
candidates to inspection across all scopes, and background work is small and time-bounded. Defaults preserve data rather than
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
not a workspace transaction. Candidates use one authoritative, path-free total order:
`(scope ordinal, kind, candidate key, subject identity digest, immutable entry ordinal)`, with
ordinal comparison for scalar fields and unsigned-byte lexicographic comparison for digests and
keys. Canonical subject, timestamps, and physical paths are classification metadata only; they
never participate in continuation ordering. Equal identity tuples are inconsistent and retained.

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

The service uses one invocation-wide journal bound and one shared candidate-admission bound.
`CandidatePageSize=128` is the fixed total number of candidate entries admitted to inspection across
all scopes and all candidate classes. It includes safe candidates, unsafe/reparse/malformed journal
entries, and lookahead; it is not a safe-only allowance. The effective journal budget is
`min(requestedJournalBatchSize,
JournalCleanupBatchSize)`; an omitted request uses the configured value, both values are in `1..50`,
and an override can never raise the configured ceiling. The effective budget is allocated greedily in
resolved scope ordinal order: the first scope consumes the remaining budget until its journal work is
exhausted or the budget is zero, then the next scope receives only the budget left by its predecessors.
A scope cannot reset or replenish the budget. `JournalBudget` and `JournalBudgetConsumed` are
invocation-level result fields; per-scope `JournalInspected` values sum to the latter.

`JournalInspected` counts every admitted journal candidate and `NonJournalInspected` counts every
admitted non-journal candidate. `UnsafeJournalScanned` and `UnsafeNonJournalScanned` count unsafe,
reparse-point, malformed, non-classifiable, or inaccessible candidates in their respective classes;
`UnsafeScanned = UnsafeJournalScanned + UnsafeNonJournalScanned`, and
`SafeJournalInspected = JournalInspected - UnsafeJournalScanned`. The exact equations are:
`Scanned = JournalInspected + NonJournalInspected = SafeJournalInspected + UnsafeJournalScanned +
SafeNonJournalInspected + UnsafeNonJournalScanned`;
`SafeInspected = SafeJournalInspected + SafeNonJournalInspected = Scanned - UnsafeScanned`;
`JournalInspected = SafeJournalInspected + UnsafeJournalScanned`;
`NonJournalInspected = SafeNonJournalInspected + UnsafeNonJournalScanned`;
`Scanned <= CandidatePageSize = 128`; `JournalInspected <= JournalBudget <= 50`; and
`UnsafeScanned <= UnsafeScanLimit = 32`. An unsafe entry counts as a journal candidate only when its
safe directory-entry location/name metadata establishes a journal classification; an unsafe or
non-classifiable entry without that classification counts as non-journal. Every unsafe candidate,
journal or non-journal, consumes
exactly one shared 128-admission unit and increments its class counter; an unsafe journal candidate
also consumes exactly one journal-budget unit and increments `JournalInspected`. No unsafe scan may
bypass either cap. Consequently, non-journal entries receive every admission not consumed by journals:
`NonJournalInspected <= 128 - JournalInspected`. There is no separate safe-candidate pool and no
per-class or per-scope reset.
Lookahead is one of these same admissions, is included in its class counter and `Scanned`, and is
never additional capacity. No journal candidate is inspected merely to establish that more work
exists after the journal limit is reached.

For continuation safety, the authoritative order places all non-journal candidates before
`completedRecoveryEvidence`, with the existing tuple order applied within each kind. This is a
partition of the one total order, not a second ordering. The planner admits candidates in that order
until the 128 total bound, while admitting no more than the remaining invocation-wide journal budget. Thus non-journal
candidates may use every page slot not used by journal candidates; the page is never reduced to 50
just because the journal cap is 50. When the global journal budget is reached, the planner does not inspect
another journal candidate. The cursor remains after the last inspected candidate, and the next
invocation resumes at the first uninspected journal candidate.

`JournalCapReached` is true when the remaining invocation-wide journal budget prevented inspection of
the next journal candidate. `JournalDeferred` is a saturated presence counter (`0` or `1`) per scope
and in the aggregate result. It is set only by an exact `JournalBoundaryProof` captured at the same
enumeration boundary: the directory enumerator has already returned a classified directory-entry
metadata record after the last admitted candidate, and that record's fixed journal-location/name and
entry-kind classification proves that a journal candidate remains. Producing the proof may consume
the enumerator's already-buffered directory-entry metadata, but it must not open the entry, read its
contents, parse a journal, resolve a recovery envelope, or admit the entry as a candidate. The proof
does not advance past or inspect the journal candidate; it is not a candidate, projection, candidate
metadata read, skip list, or count. An enumerator that cannot provide this exact buffered
classification produces no proof.

`JournalBoundaryProof` is a proof of a remaining *syntactically classified journal directory entry*,
not a parsed or validated journal. It is an `internal sealed` non-serialized type with a private
constructor. The only construction path is the enumerator's pre-inspection factory, which accepts
the enumerator's still-live metadata-only boundary token; callers cannot construct a proof from a
candidate, projection, content result, or post-inspection record. The factory consumes that token
when the enumerator advances, changes scope, opens the entry, reads content metadata, admits the
entry, or inspects the entry. Thus a proof captured after inspection, or derived from content, cannot
satisfy the construction boundary even if its values look correct.

For this validator, an input is actionable only when its safe root has already been validated and
bound to the active scope, the directory-entry location is one of the fixed journal locations, its
filename is one of the accepted journal layouts for that location, and its entry kind is classified
as the expected file/directory kind. The proof uses no content validity, envelope validity, supported
version, accessibility, terminal state, or deletion eligibility. Metadata-only proof therefore cannot
detect malformed, unsupported, or inaccessible content. Those failures are discovered only when the
entry is later admitted for counted inspection; the result is then typed protected evidence. A
non-journal, unsafe, or unclassifiable directory entry cannot produce a proof.

The exact proof shape and validator are:

```csharp
internal enum JournalBoundaryLocation : byte
{
    Restore = 1,
    AppendVersions = 2,
    FileEntity = 3
}

internal enum JournalBoundaryNameClass : byte
{
    RestoreJournal = 1,
    AppendJournal = 2,
    FileEntityRecovery = 3
}

internal enum JournalDirectoryEntryKind : byte
{
    File = 1,
    Directory = 2
}

internal enum JournalBoundaryProvenance : byte
{
    BufferedDirectoryEntryMetadataOnly = 1
}

internal enum JournalBoundaryRejection : byte
{
    None = 0,
    WrongEntryKind = 1,
    UnsafeOrMismatchedSafeRoot = 2,
    WrongRun = 3,
    WrongSnapshot = 4,
    WrongEnumerator = 5,
    BoundaryMismatch = 6,
    CanonicalEntryKeyMismatch = 7,
    NonJournalSyntax = 8,
    MalformedMetadata = 9,
    NonMetadataProvenance = 10,
    InvalidPresence = 11,
    StaleOrdinal = 12,
    JournalBudgetRemaining = 13,
    PostInspection = 14
}

internal readonly record struct MaintenanceEnumeratorId(Guid Value);

internal readonly record struct JournalBoundaryValidationContext(
    MaintenanceRunId ActiveRunId,
    MaintenanceSnapshotId ActiveSnapshotId,
    MaintenanceScopeBinding ActiveScopeBinding,
    MaintenanceEnumeratorId ActiveEnumeratorId,
    ushort ActiveScopeOrdinal,
    MaintenanceCandidateKey? LastAdmission,
    uint CurrentPageSequence,
    MaintenanceCandidateKey ExpectedCanonicalEntryKey,
    JournalDirectoryEntryKind ExpectedEntryKind,
    int JournalBudgetRemaining);

internal sealed class JournalBoundaryProof
{
    // Private construction is intentional: only the pre-inspection enumerator factory may mint one.
    private JournalBoundaryProof(
        MaintenanceRunId runId,
        MaintenanceSnapshotId snapshotId,
        MaintenanceScopeBinding safeRootBinding,
        MaintenanceEnumeratorId enumeratorId,
        ushort scopeOrdinal,
        MaintenanceCandidateKey? afterCandidate,
        uint pageSequence,
        ulong directoryEntryOrdinal,
        MaintenanceCandidateKey canonicalEntryKey,
        JournalBoundaryLocation location,
        JournalBoundaryNameClass nameClass,
        JournalDirectoryEntryKind entryKind,
        JournalBoundaryProvenance provenance) { }

    internal MaintenanceRunId RunId { get; }
    internal MaintenanceSnapshotId SnapshotId { get; }
    internal MaintenanceScopeBinding SafeRootBinding { get; }
    internal MaintenanceEnumeratorId EnumeratorId { get; }
    internal ushort ScopeOrdinal { get; }
    internal MaintenanceCandidateKey? AfterCandidate { get; }
    internal uint PageSequence { get; }
    internal ulong DirectoryEntryOrdinal { get; }
    internal JournalBoundaryLocation Location { get; }
    internal JournalBoundaryNameClass NameClass { get; }
    internal JournalDirectoryEntryKind EntryKind { get; }
    internal JournalBoundaryProvenance Provenance { get; }
    internal MaintenanceCandidateKey CanonicalEntryKey { get; }

    internal static bool TryCaptureFromBufferedMetadata(
        JournalEnumeratorBoundary boundary,
        out JournalBoundaryProof proof);
}

internal readonly record struct JournalEnumeratorBoundary(
    MaintenanceRunId RunId,
    MaintenanceSnapshotId SnapshotId,
    MaintenanceScopeBinding SafeRootBinding,
    MaintenanceEnumeratorId EnumeratorId,
    ushort ScopeOrdinal,
    MaintenanceCandidateKey? AfterCandidate,
    uint PageSequence,
    ulong DirectoryEntryOrdinal,
    MaintenanceCandidateKey CanonicalEntryKey,
    JournalBoundaryLocation Location,
    JournalBoundaryNameClass NameClass,
    JournalDirectoryEntryKind EntryKind,
    JournalBoundaryProvenance Provenance,
    bool ClassifiedJournalEntryRemains,
    bool SafeRootValidated,
    bool JournalSyntaxValid,
    bool MetadataWellFormed,
    bool EnumeratorHasNotAdvanced,
    bool EntryHasNotBeenOpened,
    bool EntryHasNotBeenInspected);
```

`TryCaptureFromBufferedMetadata(JournalEnumeratorBoundary boundary, out JournalBoundaryProof proof)`
is the only factory. It returns false unless the safe root is validated/bound, the metadata token is
enumerator-owned and still live, the entry has not been opened or inspected, the provenance is
`BufferedDirectoryEntryMetadataOnly`, the journal syntax and expected kind match, and the presence
flag is true.

`ValidateJournalBoundaryProof(in JournalBoundaryProof proof, in JournalBoundaryValidationContext context,
out JournalBoundaryRejection rejection)` accepts only matching run, snapshot, safe-root binding,
enumerator identity, scope/page boundary, canonical entry key, expected entry kind, fixed
location/name pair, metadata-only provenance, and zero remaining journal budget. It rejects wrong
kind, unsafe or mismatched root, wrong run/snapshot/enumerator, scope/page/boundary mismatch,
canonical-key mismatch, non-journal syntax, malformed metadata, non-metadata-only provenance, false
presence, stale or non-increasing ordinals, nonzero remaining journal budget, and any proof obtained
after opening, reading, parsing, admitting, or inspecting the entry. Its actionable classification is
only the validated-safe-root, fixed-location, accepted-layout filename, and expected entry-kind test;
it is not a content or metadata-validity test beyond that directory-entry classification.
The proof contains no path, raw filename, content, exception, identity digest, or skip list. A
non-journal, unsafe/non-classifiable, or non-actionable entry never satisfies the validator. The
enumerator emits the proof only from already-buffered directory-entry metadata and discards it when
the enumerator advances or the scope changes; invalid proofs are discarded without disclosure.
`JournalDeferred=0` unless this validator succeeds before the budget is exhausted. Once a syntactic
journal candidate is admitted, its proof is consumed before inspection; protected inspection results
cannot satisfy an uninspected-remainder proof. Remaining non-journal or non-actionable entries never
set `JournalDeferred` or journal-specific `HasMore`.
`HasMore` is true for a reached journal cap only when `JournalCapReached && JournalDeferred=1`; it is
otherwise true only when another actionable safe candidate remains after the cursor or another
requested scope remains. Protected or unsafe journal candidates still count when admitted to
inspection, but their presence alone never creates continuation work.

The service also stops on the time budget, cancellation, or a scope boundary. It returns the counters
and continuation flag. A later invocation may continue from the deterministic start order; deletion
is idempotent because an already absent eligible member is treated as complete only if the paired-state
recheck proves the absence is expected.

### 5.1 Authoritative cursor, identity, wire format, and paging contract

`MaintenanceCursor` is the only cursor type and name. All superseded cursor contracts are removed;
they are not compatibility aliases. Continuation is an opaque, fixed-size cursor over a stable
maintenance run. Physical paths are never
cursor fields, token fields, log fields, or CLI output. The exact internal value types are:

```csharp
internal readonly record struct MaintenanceWorkspaceKey(Guid Value);
internal readonly record struct MaintenanceRunId(Guid Value);
internal readonly record struct MaintenanceSnapshotId(Guid Value);
internal readonly record struct MaintenanceScopeBinding(Guid Value);
internal readonly record struct MaintenanceCandidateKey(Guid Value);
internal readonly record struct MaintenanceHash256(ReadOnlyMemory<byte> Bytes);

internal enum MaintenanceCursorPhase : byte
{
    Discover = 1,
    Complete = 2
}

internal readonly record struct MaintenanceCursor(
    byte Version,
    MaintenanceWorkspaceKey Workspace,
    MaintenanceRunId Run,
    MaintenanceSnapshotId Snapshot,
    MaintenanceScopeBinding Scopes,
    ushort ScopeOrdinal,
    MaintenanceCursorPhase Phase,
    MaintenanceCandidateKey AfterCandidate,
    uint PageSequence,
    MaintenanceHash256 PolicyHash,
    ulong ExpiresUnixSeconds);

internal readonly record struct MaintenanceCandidateIdentity(
    MaintenanceCandidateKind Kind,
    MaintenanceWorkspaceKey Workspace,
    ushort ScopeOrdinal,
    MaintenanceHash256 SubjectIdentity,
    ulong ImmutableEntryOrdinal);

internal readonly record struct MaintenanceDiscoveryPage(
    ImmutableArray<MaintenanceCandidateProjection> Projections,
    MaintenanceCursor? Continuation,
    int Scanned,
    int Projected,
    int MetadataReads,
    int Returned,
    int Lookahead,
    bool HasMore,
    int UnsafeScanned,
    int UnsafeJournalScanned,
    int UnsafeNonJournalScanned,
    int JournalBudget,
    int JournalBudgetConsumed,
     int JournalInspected,
    int JournalDeleted,
    int NonJournalInspected,
    bool JournalCapReached,
    int JournalDeferred);
```

The wire value is the ASCII prefix `b5c2.` followed by unpadded base64url encoding of exactly
160 encoded bytes total: a 128-byte payload plus a 32-byte HMAC-SHA256. The 128-byte
payload is, in order: version `u8` (value `2`), workspace/run/snapshot/scope-binding GUIDs
(`16` bytes each), scope ordinal `u16`, phase `u8`, after-candidate key (`16` bytes), page
sequence `u32`, policy hash (`32` bytes), and expiry (`u64` Unix seconds). Integers are big-endian;
GUIDs use RFC 4122/network byte order, not .NET mixed-endian `Guid.ToByteArray()`. The final 32
bytes are HMAC-SHA256 over ASCII `b5c2.` plus the 128-byte payload. The exact maximum token length
is 219 ASCII characters. There is no null sentinel on the wire: an absent continuation is the
typed `null` result value. `Phase=Complete` is the only sentinel and requires zero after-key and
zero page sequence.

`MaintenanceCursorCodec` is the exact implementation owner: an internal static type in
`src/Scrinia.Core/MaintenanceContracts.cs`. Its only Core token APIs are
`Encode(MaintenanceCursor, ReadOnlySpan<byte>)` and
`TryDecode(string, ReadOnlySpan<byte>, out MaintenanceCursor, out string?)`; both reject
non-canonical base64url, unknown versions, invalid enum values, nonzero complete sentinels, bad
expiry, and any length mismatch before MAC comparison. The same type owns
`ComputeCandidateKey(MaintenanceWorkspaceKey, MaintenanceCandidateIdentity, ReadOnlySpan<byte>)`
and `ComputeWorkspaceKey(ReadOnlySpan<byte>, ReadOnlySpan<byte>)`. No CLI or recovery type may
implement or wrap a second cursor codec.

`MaintenanceCandidateProjection` is the bounded, non-serialized projection used by the service and
contains a `MaintenanceCandidateKey`, safe relative names only, pair metadata, protection, and
eligibility. Safe identity is the canonical tuple `(workspace key, scope ordinal u16, kind u8,
canonical subject identity bytes, immutable entry ordinal u64)`; subject identity is the SHA-256 digest of the
canonical logical subject, and the typed adapter supplies the immutable logical ordinal. An adapter
that cannot prove this tuple returns an unsafe projection and never invents an identity from a path.
The cursor cannot carry a candidate list, a path list, or an unsafe-entry skip list.

Candidate and workspace keys are calculated by `MaintenanceCursorCodec` with domain-separated
HMAC-SHA256 and truncated to the fixed key size:

```csharp
internal static MaintenanceCandidateKey ComputeCandidateKey(
    MaintenanceWorkspaceKey workspace,
    MaintenanceCandidateIdentity identity,
    ReadOnlySpan<byte> signingKey);

internal static MaintenanceWorkspaceKey ComputeWorkspaceKey(
    ReadOnlySpan<byte> canonicalWorkspaceBinding,
    ReadOnlySpan<byte> signingKey);
```

The HMAC input is length-delimited and versioned. Candidate-key input is
`ASCII("scrinia/b5/candidate-key/v2") || workspace key 16 bytes || scope ordinal u16 || kind u8
|| subject digest 32 bytes || immutable entry ordinal u64`; the candidate key is the first 16
HMAC-SHA256 bytes. Workspace-key
input is `ASCII("scrinia/b5/workspace-key/v2") || u32+canonical workspace binding`. The key is a
process-local, cryptographically random 32-byte key owned by `MaintenanceRunRegistry`
in `src/Scrinia.Core/FileMemoryMaintenanceService.cs`; it is never read from configuration,
persisted, placed in a cursor, result, log, exception, or command line. There is no external-secret
fallback and no new policy key. `MaintenanceRunRegistry` exposes `Create(...)`, `TryGet(...)`,
`Remove(...)`, and `EvictExpired(...)`; it stores the key with the run/snapshot binding and bounded
immutable snapshot state. A key is created for each new run, expires with the cursor, and is
zeroed/removed on expiry, completion, explicit invalidation, disposal, or process exit. Creation,
replacement, eviction, and removal are atomic; snapshot state is immutable after publication. A
missing run, key, or snapshot fails closed with `InvalidContinuation`. Safe ordering is exactly
`(scope ordinal, kind, candidate key, subject digest, immutable ordinal)`.

The conformance vectors are normative. The field encoder vector is `u16(7) || u8(2) || u32(3)`
encoded as the hex bytes `00 07 02 00 00 00 03`; a zero-length field is `00 00 00 00` for `u32`
length-delimited bytes and is never omitted. For the HMAC primitive, key
`0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b` and data `4869205468657265` must produce
`b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7` (RFC 4231 vector). The
candidate-key and cursor-token tests additionally publish their complete encoded input and expected
MAC as checked-in fixtures generated from these exact rules; a fixture change is a wire-format
breaking change and requires a new cursor version. Candidate vector: key
`303132333435363738396162626364656630313233343536373839616263646566`, zero workspace key, scope `0003`, kind `02`,
subject digest `000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f`, and ordinal `42`
produce input
`736372696e69612f62352f63616e6469646174652d6b65792f763200000000000000000000000000000000000302000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f000000000000002a`,
HMAC `c6d0988044d8f2d78f44ec2a1625f0ace1e93a1cfaff73298585a6bf2f8dd5c3`, and candidate key
`c6d0988044d8f2d78f44ec2a1625f0ac`. Token vector: with maintenance test key
byte is `02`, all other bytes zero except the final eight bytes `0000000000000001`, produces MAC
`b23e2b7ba9ffb5015d1f7a276b6e56f503a23a49abda1604587cd62e555fd2cf` over ASCII `b5c2.` plus the
payload. (The token vector uses the maintenance test key `0123456789abcdef0123456789abcdef`, not
the candidate vector's truncated key.)

Canonical scope binding is the ordinal UTF-8 sequence of the requested persistent scope names,
each framed as `u32 big-endian byte length || UTF-8 bytes`, preceded by a `u16` count; names are
ordinally sorted and duplicate names are rejected. Canonical policy binding is the five approved
keys in the order listed in §3, each value encoded at its declared fixed integer/boolean width,
then SHA-256 hashed. No locale, JSON serializer, path separator, or dictionary iteration order
participates in either binding. Length-delimited fields use `u32` lengths and reject values over
`1,048,576` bytes before hashing.

Unsafe/unverifiable entries use safe exclusion, not a second identity scheme: they are not members
of the continuation order and never receive a cursor key. Every unsafe candidate, journal or
non-journal, is admitted exactly once against the shared `CandidatePageSize=128` page and increments
the appropriate class counter. An unsafe journal candidate additionally consumes exactly one unit of
the invocation-wide `JournalBudget` and increments `JournalInspected`; an unsafe non-journal candidate
consumes no journal-budget unit but increments `NonJournalInspected`. The invocation-wide
`UnsafeScanLimit=32` is a further total bound, so no unsafe scan can bypass the page or, for journals,
the journal cap. The scan stops when the shared page, unsafe bound, or applicable journal budget is
exhausted. Unsafe entries are retained/reported as a bounded aggregate and disclose no name.
`HasMore` excludes this protected aggregate because it is not actionable continuation work; a later
invocation repeats the bounded scan only if it has budget.

Each invocation has `CandidatePageSize=128`, `LookaheadSize=1`, `MetadataReadLimit=128`, and
`ReturnedProjectionLimit=64`: at most 128 candidate entries are scanned, 128 metadata reads are
performed, 64 safe projections are returned, and one additional safe projection is retained only
 as lookahead. Lookahead counts against `Scanned`, `Projected`, `MetadataReads`, and bounded
  in-memory work, but not `Returned`; `UnsafeScanned` is the total opaque safety-scan count, while
  `UnsafeJournalScanned` is included in `JournalInspected` and `UnsafeNonJournalScanned` is included
  in `NonJournalInspected`. `JournalBudget` and `JournalBudgetConsumed`
 are invocation-wide, including all scopes; per-scope values are additive views of the same counters.
 The
page fields mean exactly: `Scanned` is candidate entries admitted to inspection; `Projected` is
safe candidates converted to projections; `MetadataReads` is metadata reads actually attempted;
`Returned` is projections emitted; `Lookahead` is zero or one projection inspected after the page;
and `HasMore` also follows the journal-cap rule above: a reached global cap sets it only when the
typed `JournalBoundaryProof` was already captured without inspecting another journal. A journal lookahead
is not admitted after the global journal budget is reached. Unsafe aggregates do not set `HasMore`.

`PageSequence` starts at zero and increments exactly once for each continuation page that returns
at least one projection and advances `AfterCandidate`; it is unchanged for an empty page, a
deadline before the first yielded projection, cancellation, or a rejected token. The deadline is
a monotonic absolute deadline: once reached, no new candidate or metadata operation starts.
Cancellation is checked before each candidate; the current filesystem operation may finish, but no
new candidate starts and no advanced cursor is returned. Boundary tests assert the exact admission
equations `Scanned = JournalInspected + NonJournalInspected` and
`Scanned = SafeJournalInspected + UnsafeJournalScanned + SafeNonJournalInspected + UnsafeNonJournalScanned`, with
`SafeInspected = SafeJournalInspected + SafeNonJournalInspected = Scanned - UnsafeScanned`,
`UnsafeScanned = UnsafeJournalScanned + UnsafeNonJournalScanned`,
`JournalInspected = SafeJournalInspected + UnsafeJournalScanned`, and
`NonJournalInspected = SafeNonJournalInspected + UnsafeNonJournalScanned`, plus `Scanned <= 128`,
`JournalInspected <= JournalBudget <= 50`, `UnsafeScanned <= 32`, `JournalDeleted <= JournalInspected`,
`NonJournalInspected <= 128 - JournalInspected`, `MetadataReads <= 128`, `Returned <= 64`, `Lookahead <= 1`,
`Returned + Lookahead <= 65`, exact sequence increments, and unchanged-cursor behavior. The unsafe
counter tests also assert that each unsafe admission increments exactly one class counter and exactly
one `Scanned` unit, while an unsafe journal admission also consumes one journal-budget unit.

The run binding is in-memory and introduces no archive index or durable metadata. A new request
creates random run/snapshot IDs and a registry-owned random key, reads policy once, and computes `PolicyHash=SHA-256` over the
canonical policy encoding. Under the scope lock, snapshot creation hashes the length-delimited,
ordinal stream of safe candidate keys and logical ordinals discovered for the page plus the scope
root generation tuple `(root length u64, root LastWriteUtc ticks u64)`. The hash is retained only in
process-local run state; no manifest is written. A missing root-generation value, a pre/post-scan
generation mismatch, process restart, policy/scope/key change, expiry, or lost run state invalidates
the token and requires a fresh request with zero enumeration/deletion. Locked revalidation remains
mandatory, so a mutation not visible in filesystem generation metadata can cause repeat work but
cannot authorize unsafe deletion.

The bound covers candidate scans, metadata reads, projections, lookahead, returned work, memory, and
deadline/cancellation behavior. Mutable `Directory` enumeration cannot provide exact-once traversal
or detect every metadata-preserving mutation without a seekable index; B5 makes that limitation
explicit and retains/revalidates conservatively rather than adding an index.

## 6. Data flow

1. The CLI or background adapter constructs a typed maintenance request containing workspace/scope
selection, optional batch override within the configured safety ceiling, and cancellation. The
request's journal budget is one value for the entire invocation; it is not copied into each scope.
2. Core resolves the workspace and reads the flat policy. Invalid policy returns before lock
   acquisition or candidate deletion.
3. Core resolves the target scope list and acquires the existing scope-local exclusive lock and
   in-process write lock. Lock acquisition uses the existing retry/timeout behavior.
4. The existing recovery barrier runs first. Any unresolved or blocking recovery result is retained,
    reported, and excluded from deletion; maintenance does not bypass or reinterpret B2/B3/B4
    recovery. Every recovery byte/hash decision uses the shared bounded
    `IRecoveryEvidenceVerifier`; a non-`Verified` outcome remains typed protected/recovery-required
    evidence and cannot authorize a state transition.
5. Under the lock, Core discovers archive pairs and completed evidence, canonicalizes paths, loads
   only bounded metadata needed for protection, and builds the deterministic candidate page plus
   one lookahead projection. Cursor advancement occurs only after projection.
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
- Recovery verification is oversized, times out, is cancelled, blocked, missing, or hash-mismatched:
  the typed verifier outcome is retained as protected/recovery-required evidence. No journal,
  destination, index, topic/facts projection, or cache is rewritten or invalidated, and no retry
  resets the verifier's byte/time/open/stat/read counters.
- A scope lock times out: no candidate is deleted; the result is retryable and the other scopes in a
  multi-scope request may be reported independently if they were processed before the contention.
- Cancellation occurs: the current deletion is allowed to return its ordinary filesystem outcome,
  then the service stops starting new candidates and returns no advanced cursor. The caller retries
  the same cursor; unprocessed candidates are retained.

Maintenance never rolls back a verified B1/B2/B3/B4 decision and never invokes a plugin lifecycle
callback. B4 observers may observe ordinary maintenance-related store events only if an existing
event contract already requires it; observer failure remains a warning and cannot veto deletion.

## 8. Outcomes and errors

Core returns immutable typed results with per-scope counts, invocation-wide `JournalBudget` and
`JournalBudgetConsumed`, candidate summaries without content, continuation state, warnings, and a
top-level classification. `UnsafeScanned`, `UnsafeJournalScanned`, `UnsafeNonJournalScanned`,
`JournalDeferred`, and `JournalCapReached` are reported
both per scope and as aggregate fields; aggregate counters are sums/presence reductions over the
ordinally processed scopes, never independently allocated budgets. The result distinguishes:

- `completed`: the bounded request inspected all work in its requested range;
        - `moreWork`: the safety bound stopped actionable safe work and another bounded invocation can continue; protected unsafe aggregates do not set this flag;
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
  the existing layout; mutable-directory enumeration therefore bounds retained memory and returned
  work, not traversal or exact-once behavior under arbitrary mutation without a seekable index.
- Superseded cursor contracts and variable/JSON continuation material are deleted from contracts,
  serializers, reports, briefs, and tests. Unsupported continuation input is rejected as
  `InvalidContinuation` without enumeration or deletion. The CLI owns only a syntactic gate for a
  supplied token: `MaintenanceContinuationSyntax.TryValidate(string token, out
  MaintenanceContinuationSyntaxError error)` checks framing (`b5c2.`), ASCII input, canonical
  unpadded base64url, and the exact 160-byte decoded size. A failed syntax check constructs the
  typed `InvalidContinuation` result locally and makes zero Core service calls. The CLI does not
  compare a MAC or interpret cursor fields. A syntactically framed token, including one with a bad
  MAC, invalid scope, expired state, unknown semantic values, or missing in-memory run state, is
  passed unchanged to the shared Core service. Core owns
  `MaintenanceCursorCodec.TryDecode(string, ReadOnlySpan<byte>, out MaintenanceCursor, out
  string?)` and all semantic validation (including MAC, scope/run binding, and expiry), returning the
  same typed `InvalidContinuation` result without enumeration or deletion. The sole continuation name
  in Core, CLI, tests, and reports is `MaintenanceCursor`; the sole wire prefix is `b5c2.`. No
  continuation key is a policy/configuration key, and neither rejection path discloses paths or
  secret material.

## 11. Testing matrix

### B5 maintenance legacy removal inventory

The zero-symbol guard is scoped to B5 maintenance only. It must be run over the B5 maintenance
implementation paths, B5 tests, maintenance command/help guidance, and the two named B5 planning
artifacts below. It is not a repository-wide prohibition on unrelated existing embedding
functionality.

| Area | Files/roots to update and verify |
|---|---|
| Core maintenance paths | `src/Scrinia.Core/MaintenanceContracts.cs`, `MaintenancePolicy.cs`, `MaintenancePlanner.cs`, `FileMemoryMaintenanceService.cs`, `MaintenanceJsonContext.cs`, and B5 maintenance-only regions added to recovery/store files |
| B5 tests | B5 contract, policy, recovery-candidate, planner, service, CLI, scheduler, serialization, compatibility, and zero-symbol guard tests; the guard scans only B5 maintenance test paths and B5 maintenance regions |
| Maintenance CLI and guidance | `src/Scrinia/Commands/ScriniaCommands.Maintenance.cs`, its maintenance-only serializer/help regions, and documentation/help text that describes the B5 maintenance command |
| B5 artifacts | `docs/superpowers/specs/2026-09-15-b5-retention-maintenance-design.md` and `docs/superpowers/plans/2026-09-15-b5-retention-maintenance.md` |
| Forbidden literals and exact guard inputs | `b5ct1`, `MaintenanceContinuationPayload`, `MaintenanceDiscoveryCursor`, `AfterRelativePath`, `UnsafeCandidatesToSkip`, legacy `NextCursor`, `OpenAiApiKey`, `OPENAIAPIKEY`, `openaiapikey`, `OpenAIAPIKey`, `openAiApiKey`, `OPENaiapikey`; guidance inputs `use OpenAiApiKey for maintenance`, `USE OPENAI API KEY FOR MAINTENANCE`, `use openai-key access on maintenance path` |

The exact forbidden-literal row is the single shared source for the guard inputs in both B5
artifacts. Existing embedding functionality is explicitly excluded from this prohibition and scan:
`src/Scrinia.Core/Embeddings/**`, `src/Scrinia.Plugin.Embeddings/**`,
`src/Scrinia.Plugin.Embeddings.Cli/**`, `tests/Scrinia.Tests/Embeddings/**`,
`tests/Scrinia.Plugin.Embeddings.Tests/**`, `src/Scrinia/Services/CoreEmbeddingEventHandler.cs`,
and embedding-only regions of `src/Scrinia/Commands/WorkspaceSetup.cs`. The guard must not edit,
reject, or fail those paths merely because they use an existing embedding provider or key. If a
mixed file receives B5 maintenance changes, only its B5 maintenance region is in scope; unrelated
embedding regions remain excluded. The replacement set is only `MaintenanceCursor`,
`MaintenanceCursorCodec`, and the fixed `b5c2.` wire format.

The zero-symbol regression guard is an exact allowlist-aware scan. Within the B5 scope manifest
above, it scans case-insensitively for the forbidden literals and guidance inputs. The only
allowlisted ranges are (1) the literal cells in the exact forbidden-literals row of this inventory
table and the corresponding exact row in the B5 plan, and (2) the complete, explicitly named
historical removal note in each B5 artifact. No other B5 maintenance code, test, command, CLI
guidance, serializer metadata, generated-root assertion, report, plan, spec prose, fenced code,
indented code, example, fixture, or assertion is allowlisted. The guard also fails if a B5
maintenance serializer root registers or references a forbidden type, or if a B5 maintenance path
accesses a forbidden secret key. It must not scan excluded embedding paths.

The guard test inputs are the exact inventory literals and guidance inputs. For every input, the
assertion is one forbidden match at an in-scope executable location and zero matches for the
identical text in either exact inventory-table row or either named historical-note range. A
control fixture in an excluded embedding path proves that existing embedding key usage remains
outside the guard. The legacy continuation behavior test asserts rejection with zero enumeration
and zero deletion; it does not construct, decode, or enumerate a legacy payload.

> **Historical removal note — non-executable and explicitly allowlisted:** Earlier B5 drafts used
> `b5ct1`, `MaintenanceDiscoveryCursor`, `AfterRelativePath`, `UnsafeCandidatesToSkip`,
> `MaintenanceContinuationPayload`, legacy `NextCursor`, and OpenAI-key access wording. Those
> materials are removed, not compatibility aliases; only the fixed `b5c2.` continuation contract
> remains.

The implementation plan must provide focused tests for each row below and retain the existing B1–B4
regression suites unchanged unless a test is extended solely to prove compatibility.

| Area | Required coverage |
|---|---|
| Policy | Every default; valid boundary values; malformed, empty, negative, overflow, and invalid boolean values; one consistent snapshot per batch; fail-closed with zero deletion. |
| Candidate discovery | Existing archive pairs; incomplete pairs; all B2 locations; B3 coordinator records; B4 admitted/rejected/quarantined state; deterministic ordering; bounded metadata reads; control files excluded from archive enumeration. |
| Protection | Current, active, referenced, explicitly protected, unresolved, malformed, newer, ambiguous, unsafe, and unverifiable candidates are all skipped and reported. |
| Retention | Ten-version compatibility default; age threshold; over-limit pruning; younger versions preserved; pair deletion; no body-without-sidecar deletion; no current-entry deletion. |
| Journals | Verified terminal evidence may be cleaned; unresolved evidence survives indefinitely; cleanup retry; cleanup failure warning; no filename-age-only deletion; B2/B3/B4 physical locations remain byte-compatible; proof validation covers only validated-safe-root/fixed-location/accepted-layout/entry-kind directory classification, makes no content-validity or accessibility claim, and covers all negative classifications, no path disclosure, and post-inspection protected evidence. |
| Recovery verification | One shared internal `IRecoveryEvidenceVerifier` owned and invoked by `FileMemoryStore`; exact 16 MiB byte, 2-second monotonic deadline, 2-open, 8-stat, 257-read, and 64 KiB buffer ceilings from the sole `MaintenanceLimits` source; lower-only override validation/precedence and `ProtectedInvalidLimits` → typed `InvalidVerificationLimits`/`RecoveryRequired` mapping; bounded owned `RecoveryVerificationContent` plus verified metadata with caller disposal and no second read; incremental source/destination hashing and post-read re-stat; typed oversized, timeout, cancellation, blocked, missing, hash-mismatch, unsafe, and I/O outcomes; all non-verified outcomes retain evidence and make no destination, journal, index, topic/facts, or cache mutation. |
| Locking and races | Existing scope lock is used once; concurrent writer/reader/maintenance serialization; lock timeout is typed and retryable; one blocked scope does not block an independent scope; revalidation defeats stale candidates. |
| Bounds | `JournalCleanupBatchSize` default 50 and exact range `1..50`; one invocation-wide journal budget allocated greedily in scope order with no reset; one shared `CandidatePageSize=128` admission budget covers every safe/unsafe journal and non-journal candidate plus lookahead; exact §5 equations and counters; every unsafe candidate consumes exactly one shared admission and increments its class counter, while an unsafe journal additionally consumes one journal-budget unit, so no unsafe scan bypasses either cap; journal admission and deletion never exceed 50; exact typed `JournalBoundaryProof` fields/validator, whose actionable input is only validated-safe-root/fixed-location/accepted-layout/entry-kind directory metadata and never content validity or accessibility; no path disclosure; positive/negative tests for all classifications, deferred content failures, and post-inspection state; only that proof sets `JournalDeferred` or journal-specific `HasMore`; cap-reached lookahead and continuation behavior; time budget, fixed-size cursor, bounded page plus lookahead, cancellation, unchanged-cursor deadline behavior, deterministic repeated invocation, no unsafe skip list, and no per-candidate unbounded task creation. |
| Failures | Permission/IO failure retains and warns; partial pair failure; process-stop/restart safety; recovery barrier failure; no rollback of verified transactions; oversized, timeout, cancellation, blocked, missing, hash-mismatch, unsafe, and other I/O verifier outcomes retain typed evidence. |
| Verifier limits and replacement | Every recovery `ReadAllBytes`, `ReadAllText`, whole-file hash, and byte-buffer JSON parse is removed from the FileMemoryStore recovery paths; tests prove no unbounded recovery read remains, counters are invocation-wide, no retry resets them, cancellation/deadline checks are cooperative, and an in-flight filesystem call may finish before the typed result is returned. |
| Derived state | Scope-local cache/index/topic/facts invalidation after relevant deletion only; other scopes unchanged; no unnecessary rebuild. |
| Facades | Shared Core result mapping through human and structured CLI output; stable safe diagnostics; invalid argument/configuration and retryable lock outcomes; no separate CLI deletion path. |
| Host | Background adapter scheduling, shutdown/drain, disabled background policy, detached-work fault isolation, and no maintenance dependency on plugins or event sinks. |
| Compatibility | Existing append/restore/file-entity recovery, B3 coordinator, B4 participant, NMP/2, sidecar, archive, CLI, source-generation, trimming, and privacy tests. |

### 11.1 Recovery verification test matrix

The verifier tests are required in addition to the adapter and maintenance tests. Each row runs for
every fixed B2/B3/B4 evidence location; source-only and source/destination requests are both
covered where the owning adapter supplies a destination.

| Fixture or boundary | Required assertion |
|---|---|
| Valid bytes at zero, one, exact 16 MiB, and below the request-lowered ceiling | `Verified` only for valid contained regular files; exact aggregate bytes and reads are reported, including the final zero-byte read. |
| One byte over the effective byte limit; declared size already over the limit | `ProtectedOversized`; no hash completion, state advance, deletion, rewrite, or derived-state invalidation. |
| More than 2 opens, 8 stats, or 257 reads; retry attempted after a failed operation | Typed protected/timeout result; the counter is exact, invocation-wide, and never reset. |
| Timeout before open, between members, and between read chunks | `RecoveryRequiredTimeout`; no new operation after the monotonic deadline and retained evidence. |
| Cancellation before open and between chunks | `RecoveryRequiredCancellation`; no advanced recovery cursor/state and retained evidence. |
| Deliberately blocked/sharing-denied open or stat | `ProtectedBlocked`; no retry loop or mutation. |
| Missing source, missing destination, directory, reparse point, escaped path, wrong scope, malformed identity | `ProtectedMissing` for absence, otherwise `ProtectedUnsafe`; no open outside the bound. |
| Source hash mismatch, destination hash mismatch, and post-read replacement/length mismatch | `ProtectedHashMismatch` or typed protected unsafe result; no `Verified` decision. |
| Injected generic I/O failure and an in-flight read that completes after cancellation/deadline | `ProtectedIoFailure` for the former; the latter completes only the in-flight call and then returns the typed timeout/cancellation outcome without starting another operation. |
| Invalid limits: zero, negative, overflow, non-finite duration, above-hard, or mixed valid/invalid override | `ProtectedInvalidLimits`, exact `InvalidVerificationLimits`/`RecoveryRequired` mapping, zero opens/stats/reads and no parser or mutation; no clamp, fallback, retry, or partial application. |
| Every non-verified result | Journal/evidence bytes, destination, live pair, transaction state, index, cache, topic/facts projections, and physical layout remain unchanged; output contains no path/content/exception text. |
| Adapter mapping | Only `Verified` produces `RecoveryCleanupDecision` or advances recovery; `ProtectedInvalidLimits` maps exactly to typed `InvalidVerificationLimits`/`RecoveryRequired`; all other outcomes map to typed recovery-required/read-barrier protection. |
| Parser consumption | `ParseVerifiedEnvelope` is the sole byte/JSON parser and validator and returns `RecoveryVerifiedEnvelope`; the renamed journal, restore, file-entity, and operation-id consumers accept only that object or typed metadata, perform no I/O/parsing/validation, and borrow caller-owned content that is disposed in `finally`. |
| Accounting shape | Every `RecoveryVerificationResult` outcome carries `FinalZeroByteRead`, `DeadlineObserved`, and `CancellationObserved`; table-driven tests assert the fields are present and preserve observed values for `Verified`, zero-I/O invalid limits, early failures, cancellation, timeout, and all other protected outcomes. |
| Source audit | The complete Task 4 inventory covers `FileMemoryStore.cs`, `RecoveryEnvelope.cs`, `RecoveryDiscovery.cs`, `FileEntityTransactionCoordinator.cs`, and related recovery helpers; no unapproved recovery `ReadAllBytes`, `ReadAllText`, whole-file hash, or byte-buffer parser remains. Approved in-memory payload, configuration/bundle/migration, embedding/vector, and workspace-baselining exclusions are named and tested; every recovery parser routes through shared verified content. |

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
   is retained indefinitely until explicitly resolved. All FileMemoryStore recovery verification
   uses the one bounded async verifier contract: 16 MiB total bytes, 2-second monotonic deadline,
   2 opens, 8 stats, 257 reads, and 64 KiB read buffer hard ceilings, with lower-only request
   limits, incremental source/destination hashing, post-read re-stat, and typed protected or
    recovery-required outcomes for oversized, timeout, cancellation, blocked, missing, hash mismatch,
     unsafe, invalid limits, and other I/O failures. Every one of those outcomes, including `Verified`
     and zero-I/O `ProtectedInvalidLimits`, carries `FinalZeroByteRead`, `DeadlineObserved`, and
     `CancellationObserved` with observed (not defaulted or inferred) values. `ProtectedInvalidLimits` maps to typed
    `InvalidVerificationLimits`/`RecoveryRequired`; only `Verified` may advance recovery; non-verified outcomes make
   no destination, journal, index, cache, or topic/facts mutation. The contract explicitly documents
    cooperative-I/O behavior and does not promise preemption of an in-flight filesystem call. Parser
    consumers use caller-owned, disposable `RecoveryVerificationContent` and verified metadata; all
     `ParseVerifiedEnvelope` is the only byte/JSON parser and returns `RecoveryVerifiedEnvelope`;
     the named journal/restore/file-entity/operation-id consumers accept only that parsed object
     or its typed metadata and perform no I/O, JSON parsing, validation, or second whole-file read.
7. `JournalCleanupBatchSize` defaults to 50 and accepts only `1..50`; one invocation-wide journal
    budget is allocated in resolved scope order and cannot reset per scope. One shared
    `CandidatePageSize=128` admission budget covers every inspected candidate, including safe and
    unsafe journal/non-journal entries and lookahead. The exact class equations and counters in §5
    hold; every unsafe candidate consumes exactly one shared admission and increments its class
    counter, while an unsafe journal additionally consumes one journal-budget unit, so no unsafe scan
     bypasses either cap. Thus `Scanned` never exceeds 128. A private-constructor,
     enumerator-factory-only `JournalBoundaryProof` with the exact fields and validator in §5 is the
     only source of `JournalDeferred` or journal-specific `HasMore`; its directory-entry kind,
     metadata-only provenance, run/snapshot/enumerator identity, safe-root binding, and canonical entry
     key must match the active boundary. The validator rejects wrong kind, unsafe root, wrong
     run/snapshot/enumerator, non-journal syntax, malformed metadata, post-inspection provenance, and
     key mismatch. Its actionable input is only a validated-safe-root directory entry with fixed location, accepted
     journal filename layout, and expected entry kind. It proves no content validity or accessibility
     and is produced without journal-content inspection. Non-journal, unsafe/non-classifiable,
     non-actionable, or already-inspected entries cannot satisfy it; malformed, unsupported, or
     inaccessible content becomes typed protected evidence only after counted admission. Time budget,
   fixed-size cursor, bounded page plus lookahead, cancellation, and
   continuation prevent unbounded automatic or operator work; deadline-before-first-yield returns
   the unchanged cursor and cancellation returns no advanced cursor.
8. Lock contention is retryable and typed; deletion failures retain candidates and produce warnings.
9. Recovery, cache, index, facts, B3, and B4 semantics are not weakened, duplicated, or bypassed.
10. Output contains safe operational diagnostics only, with no memory content or secret leakage.
11. Existing local-only CLI/MCP, NMP/2, sidecar, archive layout, source-generation, trimming, and
    plugin observer contracts remain compatible.
12. Continuation ownership is exact: the CLI locally rejects only framing/ASCII/canonical
    base64url/160-byte-size failures by constructing typed `InvalidContinuation` with zero Core
    service calls, enumeration, or deletion; every syntactically valid-looking token goes through
    Core for MAC, scope/run binding, expiry, and other semantic validation. Both paths return
    sanitized results without path or secret disclosure, and legacy/invalid versus semantically
    invalid token tests prove the boundary.

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
- a seekable archive index or an exact-once/bounded-traversal guarantee under arbitrary mutation of
  a live `Directory` enumeration;
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

The cursor contract is also explicit and path-free: one fixed binary `MaintenanceCursor`, exact
128-byte payload plus 32-byte HMAC equals 160 encoded bytes total before the `b5c2.` prefix and
unpadded base64url limit, exact field widths and endian order, domain-separated length-delimited HMAC inputs, deterministic safe
identity/order, safe exclusion plus an invocation-wide `UnsafeScanLimit=32`, fixed 128-candidate
shared admission/page/lookahead/metadata bounds, one invocation-wide journal budget of at most 50
with greedy ordinal scope allocation, explicit safe-journal/unsafe-journal/non-journal equations and
counters, the invariant that every unsafe candidate consumes exactly one shared admission and unsafe
journals additionally consume the journal budget, cap-reached continuation and proof-backed deferred-
journal `HasMore`,
projection-gated advancement, in-memory run/snapshot binding, maintenance-specific key lifecycle,
unchanged-cursor deadline behavior, and no advanced cursor on cancellation are required. The exact
`JournalBoundaryProof` fields and validator accept only validated-safe-root/fixed-location/
accepted-layout/entry-kind directory classification, are metadata/name-only and path-free, and reject
non-journal, unsafe/non-classifiable, non-actionable, stale, advanced, and already-inspected entries;
they make no content-validity or accessibility claim, and malformed/unsupported/inaccessible content
is typed protected evidence only after counted admission. The design
does not claim exact-once traversal or detection of every metadata-preserving mutation in a mutable
directory; that requires the seekable index which remains out of scope.

The recovery-verification contract is also single-sourced: `FileMemoryStore` owns one internal
bounded async verifier invocation path for all recovery byte/hash decisions; its hard byte/time/
  open/stat/read/buffer limits (including 257 reads to permit the final zero-byte read at the 16 MiB ceiling), typed outcomes, lower-only request limits, post-read re-stat,
  cooperative-I/O limitation, and no-mutation rule are identical in the contract, Task 4, failure
  matrix, acceptance criteria, and tests. There are no duplicate request byte/duration fields;
  invalid limits have the explicit protected/recovery-required mapping and zero-I/O behavior.
  `RecoveryVerificationContent` has one caller-owned disposal boundary, and every named parser
  consumes that verified memory without a second whole-file read. The complete production inventory
  includes `FileMemoryStore.cs`, `RecoveryEnvelope.cs`, `RecoveryDiscovery.cs`, and
  `FileEntityTransactionCoordinator.cs`, with named approved exclusions and a scan guard. Ordinary
  non-recovery hashing is explicitly excluded, so the replacement rule does not contradict unrelated
  workspace-file baselining. Citations: current recovery sites are recorded in §2.2; the bounded
  restore/recovery precedent is `docs/superpowers/specs/2026-08-24-journaled-restore-recovery-design.md`.

The scope is one implementation slice: policy/configuration, Core candidate maintenance, one local
CLI adapter, and one bounded background adapter. It does not smuggle in B6 format evolution, a new
transaction system, plugin lifecycle work, remote deployment, or a workspace-wide coordination
mechanism.

The allowlist wording is consistent: the exact forbidden-literals inventory row and the named
historical removal note are the only allowed literal ranges inside the two B5 artifacts; every other
in-scope B5 maintenance location is forbidden, and the explicitly listed existing embedding paths
are excluded from the scan and prohibition.
