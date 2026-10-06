# Review Remediation — Design

**Date:** 2026-10-06

**Status:** Proposed; awaiting written-spec review

**Scope:** The ten findings from the October 6 full design/code review.

## Intent and decisions

Restore predictable persistence and retrieval behavior: a successful append preserves existing
content, a successful write survives an unrelated failed import, rejected input changes no user
data, and filtered search selects its candidates from the eligible memories.

The user requested remediation of all ten findings and explicitly approved adding a query-aware
candidate-selection overload with a default implementation to `IMemoryStore`. Existing callers and
implementers remain supported. This is a narrow exception to the October 5 release-hardening
design's interface freeze; the broader `IMemoryStore` split remains deferred.

The remaining policies below are proposed for written-spec approval. In particular, the legacy
bundle-journal refusal policy, durable commit decision before journal cleanup, and explicit
resource limits are design decisions, not previously approved behavior.

## Approach and boundaries

Use focused changes to shared boundaries. Retain the existing store, file-entity coordinators,
source-generated serialization, durable-file primitives, and recovery validation utilities.

Ten independent patches would leave the same rules scattered among callers. A general transaction
framework or store rewrite would expand the compatibility and verification burden unnecessarily.
This design introduces only the internal components needed to give admission, bundle ownership,
raw-content mutation, and bounded selection explicit owners.

Do not add HTTP/REST, remote CLI, shared-memory CRUD, a merge project, new embedding models, or a
general plugin protocol. Do not rewrite historical plans. The October 5 packaging/analyzer work
remains separate. Do not publish packages or modify unrelated uncommitted work.

## Finding coverage

| Review finding | Owning correction |
|---|---|
| 1. Skill/agent append persists truncated responses | Raw file-entity update beneath rendering |
| 2. Bundle rollback loses acknowledged writes | Shared workspace admission at real transaction entry points |
| 3. Permission failures yield unlocked success | Fail-closed `FileLock` acquisition |
| 4. Recovery deletes before full validation | Bounded, validated, operation-owned bundle journal |
| 5. Empty/failed export deletes an existing backup | Temporary-output ownership and atomic replacement |
| 6. File-entity import ignores overwrite policy | Pair-level collision policy and actual committed counts |
| 7. Vector instances overwrite unrelated vectors | Locked fresh-state read/modify/write |
| 8. Candidate limiting precedes filters | Approved query-aware selection overload |
| 9. Reserved JSON store fails after committing | Common persisted-identity response contract |
| 10. Archive JSON is read without bounds | Shared archive member and aggregate budgets |

## 1. Workspace admission and lock ownership

Introduce an internal Core `WorkspaceAdmission` owner, keyed by the canonical workspace root and
using the existing `.scrinia/.bundle-restore.lock` file for cross-process exclusion. It provides
shared admission for ordinary persistent operations and exclusive admission for bundle restore
and startup bundle recovery.

Admission belongs at actual operation entry points. Cover store, append (including create-via-
append), delete, metadata updates, standalone import, multi-memory operations, maintenance that
mutates authoritative data, and file-entity publication/migration. MCP helpers that instantiate a
file-entity coordinator must receive the same protection as callers through `FileMemoryStore`.
Inventory every persistent mutation path before implementation; presence of an existing lock
helper is not evidence that all paths already use it.

The authoritative-data lock order is:

1. Workspace admission.
2. Scope or file-entity directory admission and exclusive file lock.
3. In-process index gate where required by the existing implementation.

An operation acquires workspace admission before materializing or reading the state it intends to
mutate, and holds it through verification, rollback, and required cleanup. Multiple scope/entity
locks use deterministic canonical ordering. Do not upgrade a workspace lease while holding a
scope/entity lock. Permission errors propagate; an existing lock file never permits returning an
unlocked success object.

Nested private operations receive an explicit live workspace lease and use private helpers that
require that lease. A lease is bound to its owner/workspace and cannot be used after disposal.
Public entry points acquire their own admission. Remove the inherited `AsyncLocal<int>` depth as
authority to skip locking: a child task must not inherit permission to bypass an unrelated public
operation's admission. A bundle coordinator does not parallelize mutation under one shared token.

Persistent read operations also take shared admission while gathering their authoritative
snapshot, so they cannot see a partially applied bundle. Release admission before slow derived
scoring or response rendering; an MCP connection does not hold a lease for its lifetime. Existing
scope recovery that needs exclusive admission releases its read locks first, then rechecks under
write admission. Ephemeral-only operations require no filesystem lease.

After acquiring shared admission, check for retained bundle recovery evidence. If recovery is
needed, release shared admission, recover under exclusive admission, then retry. A second
already-running process must not ignore a journal merely because its constructor predates it.
Malformed, unsupported, or unprovable bundle evidence blocks ordinary persistent operations with
`BUNDLE_RECOVERY_REQUIRED`; diagnostics that inspect evidence remain available. No automatic
quarantine or deletion of authoritative recovery evidence is permitted.

Derived vectors/facts and unrelated configuration are not part of a bundle rollback manifest.
Their independent writers need not join the bundle transaction merely because their files live
under `.scrinia`. Existing embedding indexing runs after authoritative commit and outside its
workspace lease. This avoids adding a workspace/vector lock cycle.

## 2. Raw skill and agent updates and successful responses

Separate complete file-entity content from client envelopes. Introduce an internal raw-read result
containing the full body, metadata, and source kind; it never invokes `ResponseBuilder` or applies
display truncation. Preserve the existing skill lookup order: disk override, legacy NMP/2 skill,
then embedded built-in. Agent reads retain their existing lookup behavior.

Append is a read/transform/publish transaction under workspace and entity-directory admission.
Resolve the current full content under that admission; concatenate the suffix; derive/preserve
metadata using the existing policies; and publish the pair through the coordinator. Extend the
internal file-entity preparation seam as needed so callers do not compute a replacement from an
unlocked prior read. Existing whole-body publication remains supported.

Appending to an existing skill does not call the new-skill scaffold constructor. Appending to an
embedded or legacy skill creates a disk override containing its complete resolved prompt and the
suffix, without an additional scaffold. Preserve the current missing-target behavior for each
reserved namespace. Preserve untouched body text, unknown metadata, curated fields, and archive
behavior; only the existing append separator and new content are added. Two concurrent appends
must both survive in a valid serial order.

Only read responses are truncated for display. No durable mutation consumes `McpResponse.Content`.

The successful-write projection requires a canonical persisted address and sets both the visible
`Path` and `PersistedPath` consistently for ordinary, skill, and agent writes. It preserves the
coordinator's success/warning/recovery outcome. CLI JSON output consumes that identity directly;
it must not discover that a required field is absent after committing. Add no new public CLI flags
or MCP operations, and retain existing JSON fields and stable error codes.

## 3. Bundle preparation and operation-owned recovery

Retain `IMemoryStore.RestoreBundleAsync(BundleRestorePlan, CancellationToken)` and its public
result types. Introduce an internal `BundleRestoreCoordinator` and `BundleRestoreJournalCodec`.
The coordinator uses a workspace-wide boundary because a bundle can span topics and file-entity
directories; the existing scope-local multi-memory coordinator remains scope-local.

The import flow is:

1. Read and fully validate the bounded archive, including all declared members, before publishing
   any recovery journal or modifying authoritative data. Topic selection changes the plan, not
   admission of malformed declared members.
2. Acquire exclusive workspace admission. Recover or reject existing bundle, scope, and file-entity
   evidence before proceeding. Read fresh affected state under the appropriate ordered locks.
3. Resolve overwrite policy and canonical physical identities. Produce a frozen mutation set
   containing only files that this import will create, replace, or remove. Respect existing
   forward-versioned sidecar protections.
4. Resolve mapped addresses, artifact keys, and generated timestamps once. Serialize final member
   bytes once, then capture and validate all prior bytes and expected new hashes. Persist the
   complete prepared journal durably before the first member mutation.
5. Apply those exact frozen bytes; verify expected existence and hashes for every member; durably
   record the committed decision; clean up evidence; invalidate affected caches; release admission;
   report actual counts. The decision and cleanup rules below define failure outcomes.
6. On failure before attempting the commit decision, restore the complete prior state of the
   recorded members after a full recovery preflight. Prove restoration and durably record the
   rolled-back decision before cleanup and `BUNDLE_IMPORT_FAILED`. If proof, decision persistence,
   or rollback cleanup fails, preserve available evidence and return `BUNDLE_RECOVERY_REQUIRED`.

Do not call a per-topic import entry point that publishes an independently replayable redo journal
inside this transaction. Reuse its validation, mapping, and leaf publication helpers under the
bundle coordinator's explicit lease. Standalone topic import keeps its own existing recovery
protocol. No archive pruning, migration, or whole-scope sidecar rewrite may occur as an incidental
bundle leaf operation. Any auxiliary authoritative file the bundle genuinely must change belongs
in its frozen manifest; unchanged entries remain byte-for-byte untouched.

### Journal contract

Register a distinct `bundle-restore-journal` format, version 1, in `DurableFormats`. Register its
DTOs in a source-generated JSON context. Keep the existing journal location so old evidence is
detected. The format carries an operation ID, workspace binding, lifecycle state (`prepared`,
`committed`, or `rolled-back`), bounded member list, and a hash of its canonically serialized mutation
manifest. Workspace binding is the SHA-256 of the canonical workspace root.

Each member records a canonical relative path, member kind, prior existence, prior bytes and
SHA-256 when present, expected new existence, and expected new SHA-256 when present. Existence is
explicit: missing and an existing empty file are different states. Incoming bytes need not be
duplicated in the journal: prepared imports roll back, while terminal journals only verify their
recorded outcome and clean up evidence.

The manifest includes enough validated identity to restrict members to approved memory and
file-entity locations; a contained path alone does not authorize changing configuration or exports.

Before any replay mutation, validate the complete document: size, version, required fields,
operation/workspace binding, lifecycle, member count, path authorization and containment,
duplicate/case-colliding destinations, existence/payload consistency, Base64, lengths, all payload
hashes, and manifest integrity. Hashes detect inconsistent evidence; they do not authenticate a
hostile workspace editor. Refuse unsupported or invalid evidence with zero authoritative changes.

For a prepared journal, preflight every current member as well: it must match either its recorded
prior state or expected new state. Any third state retains evidence and refuses replay before
changing any member. A valid partially applied transaction or partially completed rollback is
therefore repeatable. A
missing previously existing file is admissible only when it is the recorded intended after-state;
otherwise recovery cannot prove ownership of the change and refuses.

Rollback visits only listed members. Restore prior bytes where a file existed; delete only a
listed file whose prior state was absent and whose current state matches this operation's expected
new state. Never enumerate the workspace to delete everything absent from a snapshot. Leave empty
directories rather than guessing ownership for recursive removal. Invalidate index, topic, and
fact caches affected by either commit or rollback, including failure paths after partial mutation.

### Commit decision and evidence cleanup

After verifying every intended member, atomically and durably replace the journal with state
`committed`. That successful durable write is the commit point. Cancellation or cleanup failure
afterward cannot initiate rollback. Keep the existing public `Committed` outcome and actual counts;
report a subsequent cleanup failure through existing diagnostics without adding a public result
variant. Retained evidence must be verified and cleaned before subsequent persistent operations.

A commit-decision write can fail after replacement but before directory synchronization. Treat
that uncertainty as `BUNDLE_RECOVERY_REQUIRED`; retain available evidence and do not guess whether
to compensate. Recovery reads the validated on-disk decision: `prepared` rolls back even if all
new members are present; `committed` verifies every expected after-state and performs cleanup only;
`rolled-back` verifies every before-state and performs cleanup only. A terminal-state mismatch
refuses recovery without modifying any authoritative member.

After a successful rollback, durably record `rolled-back` before deleting its journal. If that
decision write fails, retain available evidence and require recovery. Prepared recovery permits
mixed before/after states, so retrying an interrupted rollback remains safe.

Add an internal evidence-deletion primitive that removes the journal and synchronizes its parent
directory using the same platform durability policy as `DurableFile`. Existing atomic-write
helpers do not provide durable deletion. Test failures before and after unlink and directory
synchronization. A terminal journal that reappears after a crash can only verify and clean up; it
must never undo acknowledged content. This deliberately replaces the old rule that a cleanup
failure always rolls back, because unlink may already have removed the rollback evidence.

### Legacy and cross-version policy

The old unversioned whole-workspace journal cannot prove operation ownership. Preserve it intact
and return `BUNDLE_RECOVERY_REQUIRED` without replay. Unknown versions and malformed journals have
the same refusal policy. This repair does not create a destructive compatibility fallback or an
automatic legacy migration. Diagnostics identify the retained evidence and the unsupported format.
Keep the workspace backup and use an explicit recovery procedure before retrying.

The new guarantee requires all concurrent writers to use the repaired admission protocol. An
older running process bypasses that protocol and must be stopped before reopening the workspace
with the repaired build. Document this upgrade requirement; retaining a filename cannot make old
code participate in locking.

## 4. Bundle overwrite, export, and result contracts

With `overwrite=false`, an existing file-entity body or metadata sidecar reserves the entire
logical pair. Skip that entity without changing either file. With `overwrite=true`, publish the
bundle's exact body/metadata pair; if the bundle has no sidecar, remove a prior destination sidecar
within the same journaled mutation rather than retaining metadata for different content. Apply
the same canonical case and physical-path collision rules used by normal reserved operations.

Preserve the current selection policy: file entities are included only when the selected topic
set is the complete archive topic set. Validate declared file entities even when not selected.
Duplicate logical entities or conflicting physical archive members are invalid.

Keep `BundleRestoreResult.FileEntityCount` through internal import helpers and CLI response
mapping. Existing public tuple-returning helpers remain compatible wrappers if an internal richer
result is introduced. A file-only import that changes files reports those changes as success.
An all-skipped or empty import reports an explicit no-change result. Counts describe committed
changes, not declarations or skipped collisions.

Export builds and validates its output before replacing the destination. Failure and empty-result
paths delete only temporary files owned by this attempt. A previously existing destination is
byte-for-byte preserved until successful atomic replacement. Automatic filename collisions obey
the same rule as explicit filenames. The successful export format remains unchanged.

## 5. Archive and recovery resource limits

Use one internal bundle-limit definition and one accounting owner shared by JSON, artifact, and
file-entity readers. MiB means 1,048,576 bytes. The initial policy is:

| Resource | Limit |
|---|---:|
| Compressed input file before opening a CLI archive | 256 MiB |
| Physical ZIP entries, including unused entries | 4,096 |
| Logical memories plus file entities declared across the archive | 1,000 |
| Individual manifest, topic index, or metadata JSON | 16 MiB |
| Individual artifact or markdown body after ZIP expansion | 96 MiB |
| Aggregate expanded bytes across distinct physical archive entries | 128 MiB |
| Aggregate prior bytes retained by one bundle recovery journal | 128 MiB |
| Journal member count | 2,000 |

These values reuse existing restore payload limits where applicable, while closing previously
unbounded JSON and whole-workspace snapshot paths. An archive or pre-import state exceeding a
limit is refused before journal publication and before authoritative mutation. Existing NMP/2
decoded-content and chunk-count limits remain additional checks; ZIP byte admission cannot disable
them. Do not add user-configurable limit knobs in this repair.

Check entry declarations before allocation, then enforce the same limits on actual streamed reads.
Use checked arithmetic. Reject negative/overflowing declarations and ambiguous duplicate archive
names. Do not reserve a maximum-sized buffer for every tiny entry. Parse JSON only after its
bounded bytes are admitted. Cache validated member material as needed so repeated index/count
checks do not decompress and account for the same member repeatedly. No `ReadToEnd` path bypasses
the shared budget. Externally supplied `ZipArchive` instances get all importer-side checks; their
construction remains the supplying caller's responsibility.

The new codec derives its maximum JSON length from the 128 MiB prior-payload bound, padded Base64
expansion, at most 2,000 members, and bounded identity fields. Use a maximum of 4,096 UTF-8 bytes per
relative member path, 64 per operation ID, and fixed-size lowercase SHA-256 text for hashes and
workspace binding. Cap encoded JSON bytes at
`4 * ceil(128 MiB / 3) + 2,000 * (6 * 4,096 + 1,024) + 4,096`: path allowance covers worst-case JSON
escaping, per-member overhead includes independent Base64 padding and fixed fields, and the final
allowance covers the envelope. Serialize payloads as Base64 without additional JSON escaping of
their ASCII alphabet, and verify that the serializer fits these allowances. Validate decoded
aggregate bytes and every field independently; the document cap does not replace payload
validation. Tests exercise smaller injected budgets rather than allocating hundreds of MiB per case.

Archive admission failures return `INVALID_BUNDLE`; unsupported or invalid retained recovery
evidence returns `BUNDLE_RECOVERY_REQUIRED`. An otherwise valid plan whose prior state exceeds
journal capacity is refused before mutation with `BUNDLE_IMPORT_FAILED` and an explicit capacity
reason. Current CLI exit conventions remain unchanged.

## 6. Filter-aware bounded candidate selection

Add the approved overload while retaining the existing four-argument member:

```csharp
IReadOnlyList<ScopedArtifact> SelectLexicalCandidates(SearchQuery query, int limit);
```

Provide a default implementation so existing `IMemoryStore` implementations do not acquire a new
abstract member. Its complete scoped listing is the correctness fallback. `FileMemoryStore`
overrides it through `ListingService` to filter while enumerating and retain a bounded selection.
Do not implement the new overload by filtering the old overload's already-truncated result.

Both store-produced and caller-supplied candidates follow the same sequence: resolve scope and
excluded-topic membership; apply address/path and attribute predicates; rank lexically with the
effective context-augmented query; then retain at most K. Reuse `SearchResultFilter` and the existing
scope-resolution rules. Preserve deterministic tie-breaking and case semantics. The result limit
is separate from K.

All downstream scorers, entity facts, and response facts receive the same bounded eligible
snapshot. Retain defensive filtering in `SearchService`; it cannot repair prematurely discarded
matches. Preserve topic-result semantics: attribute-filtered queries omit topic summaries, and
path eligibility must not accidentally discard entries needed for an eligible topic summary.
Cover entry and topic results in parity tests against the existing direct-search behavior.

The configuration key, default K=200, and supported range 1–1,000 remain unchanged. No ranking
weight, embedding model, protocol DTO, or entity-token rule changes. A compatibility test uses a
store implementation with only the pre-existing members and exercises the new default overload.

## 7. Vector read/modify/write ownership

Keep `IVectorStore` and `VectorStore` public signatures unchanged. Persistent replacement, upsert,
removal, migration publication, and compaction share this sequence:

1. Acquire the instance's scope semaphore.
2. Acquire the scope's exclusive vector file lock.
3. Read fresh authoritative state from disk under that lock, including existing format/signature
   admission; never use a warm instance cache as the write baseline.
4. Apply the requested mutation to a new snapshot.
5. Publish a complete replacement atomically with the existing SVF3 writer.
6. Publish the cache only after durable success, then release the locks.

Extract private lock-held readers/writers so migration, validation, and publication do not acquire
the same file lock recursively. Use complete replacement for these persistent mutation paths;
do not retain a two-record delete/add append path that can expose a partial mutation on retry.
Compaction operates on the same fresh locked state. Ephemeral scopes keep in-memory behavior.

On unavailable or invalid state, preserve the existing refusal/quarantine policy rather than
treating it as an empty authoritative file. Failed publication does not expose the attempted cache
snapshot. A successful writer publishes a cache containing unrelated entries observed under the
lock. This repair guarantees durable writer coherence; it does not add filesystem watchers or
instant invalidation of every other process's read cache. Existing explicit reindex/fallback
behavior remains available for previously damaged derived indexes.

The cost is a fresh scope read and complete scope write for each persistent mutation. Replacement
already writes a complete file today. Measure the existing multi-memory indexing lane after the
change; a future batched-generation design is separate work, not grounds to retain unsafe writes.

## Acceptance and verification

| Area | Required proof |
|---|---|
| Raw append | Skill and agent bodies beyond the display cap retain their complete original text and suffix; no truncation marker or repeated scaffold is persisted; metadata and archived prior body survive. Test disk, legacy skill, and built-in skill sources. |
| Concurrent append | Two public reserved appends preserve both suffixes in a serial order. |
| Write responses | Ordinary, skill, and agent `store --json` return success with canonical persisted identity after commit; committed warnings remain warnings. |
| Workspace admission | Paused bundle restore blocks public store, append/create-via-append, delete, and MCP skill/agent writes; they complete afterward and survive rollback. Run separate-instance and separate-process cases. |
| Lock failure | Read-only/denied lock files refuse exclusive admission; no two successful exclusive leases exist. Cancellation and nested helpers release ownership without deadlock. |
| Owned rollback | Inject failure before/after each member publication and verification, before the commit decision; exact prior bytes return while unrelated memories, configuration, exports, vectors, and files remain untouched. |
| Commit and cleanup | Interrupt decision persistence, unlink, and directory synchronization. Prepared evidence rolls back; committed/rolled-back evidence only verifies and cleans up. Ambiguous decisions require recovery; cleanup failure never undoes acknowledged content. |
| Recovery validation | Invalid final payload, hash, identity, duplicate path, traversal, unknown version, oversized journal, and legacy journal cause zero replay mutations and retain evidence. A valid interrupted rollback resumes safely. |
| Recovery visibility | A process opened before another process leaves a journal detects it at its next persistent operation. Reads do not expose partial imports. |
| Export preservation | Empty selection, invalid source, archive failure, and failed replacement preserve a pre-existing destination byte-for-byte; success replaces it with a valid bundle. |
| File-entity import | Default overwrite skips existing complete or partial pairs; enabled overwrite installs the exact bundle pair; file-only and all-skipped results report truthful counts/outcomes. |
| Limits | Boundary and just-over-boundary cases for every member type and cumulative budget; deceptive declarations and many small entries refuse without durable changes. |
| Search | More than K higher-scoring ineligible entries cannot suppress eligible path/attribute matches. Cover supplied candidates, topic summaries, default-interface compatibility, and identical contributor/fact snapshots. |
| Vectors | Interleaved instances/processes preserve unrelated additions, replacements, removals, and compaction; failed publication preserves durable state and does not publish an attempted cache. |

Promote the review's temporary reproductions into focused repository regressions. Use existing
fault seams and isolated test stores; do not use production memories or a remote embedding service
for correctness tests. Every data-loss regression fails against the reviewed implementation before
the corresponding fix, then passes against the repaired public path.

The current workspace cannot build the main test project because untracked legacy tests reference
the retired `Scrinia.Merge` namespace. Preserve those user-owned files. Establish an isolated
verification checkout containing the intended source changes and only the active test topology;
document excluded legacy artifacts. Do not restore a removed project or claim that a selectively
filtered test run proves the unchanged dirty checkout builds.

After focused tests, run the solution build and default test suite against the integrated intended
tree, plus trimming/source-generation checks for changed recovery DTOs and the existing relevant
performance lane. Repeat only when changes or failures warrant it. Update living CLI, recovery,
security, and architecture guidance for corrected behavior and limits; leave historical specs
unchanged. No package publication is part of acceptance.

## Delivery dependencies

1. Establish the intended verification tree and pin the regressions.
2. Correct fail-closed file locking and introduce shared workspace admission.
3. Repair raw reserved-content updates and successful-write identity mapping using that admission.
4. Implement bounded archive preparation, owned bundle recovery, overwrite/count rules, and safe
   export publication. Admission and pair semantics are prerequisites.
5. Implement query-aware candidate selection and coherent vector mutation. These can proceed
   independently once their interfaces and lock contracts are fixed.
6. Integrate, run the full acceptance gate, and update current documentation.

This is the remediation design, not an implementation plan. Written-spec approval precedes the
task-by-task plan and selection of its execution method.

## Spec self-review

- All ten findings map to a named correction and behavioral acceptance case.
- The only approved public interface addition is the default-implemented query-aware overload;
  existing signatures, bundle transport format, NMP/2 semantics, and CLI/MCP command names remain.
- Lock ordering, explicit nested ownership, legacy evidence policy, rollback decision, resource
  limits, and commit/outcome semantics are explicit.
- Existing user changes and historical documentation are preserved. Unrelated release/analyzer
  work and broader store restructuring remain outside scope.
