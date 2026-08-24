# Journaled Restore Recovery Design

**Status:** Approved design

## Purpose

Bundle restore currently provides prefix consistency: each imported artifact body is moved into
place and its sidecar is then written, but a process loss during the loop can leave only a
successfully applied prefix. That is intentionally documented in the current implementation
(`src/Scrinia.Core/FileMemoryStore.cs:2830-2838`). A prefix is safer than a body/sidecar mismatch,
but it is not an all-or-recoverable restore transaction.

This design makes each imported topic scope recoverable after interruption. The outcome is
**journaled roll-forward**, never rollback: once a durable journal exists, the scope will converge
to its transaction's final incoming bytes before it is readable or writable.
This is per topic scope, not per bundle. A multi-topic bundle can therefore expose completed
topics while a different interrupted topic is recovered on its next access.

## Current integration points

The implementation must preserve the current split between bundle validation/building and the
store's scope-local persistence:

- `BundleFormatService.ImportTopicsFromZip` validates and builds every selected topic before its
  write pass (`src/Scrinia.Core/Bundles/BundleFormatService.cs:157-459`), then invokes
  `IMemoryStore.ImportTopicEntries` once per topic (`:471-479`). It currently retains incoming
  artifact text in `artifactContents`; the new transaction input must instead retain the exact
  incoming body bytes required by the journal.
- `FileMemoryStore.ImportTopicEntries` delegates to `ImportTopicEntriesCore`
  (`src/Scrinia.Core/FileMemoryStore.cs:2707-2727`). The core currently takes the per-scope
  exclusive lock, cold-loads the index, stages body files, and performs the prefix commit
  (`:2728-2854`). This is the replacement point.
- `WithExclusiveLock` is the existing per-scope cross-process `.lock` plus in-process write lock
  (`FileMemoryStore.cs:637-675`); `FileLock.AcquireExclusive` excludes readers and writers across
  processes (`src/Scrinia.Core/FileLock.cs:28-38`). The journal uses this same scope lock, not a
  new bundle-wide lock.
- Cold index reads acquire the shared scope lock in `LoadIndex`
  (`FileMemoryStore.cs:685-775`); cache hits currently bypass the file lock (`:689-756`).
  `ReadArtifactAsync` similarly has an artifact-cache fast path and direct file read
  (`FileMemoryStore.cs:1962-1978`). Both must be gated before returning cached data.
- `ListScoped` and `AnyScoped` read scopes through `LoadIndex`
  (`FileMemoryStore.cs:2099-2135`), `SearchAll` is the store search entry point
  (`FileMemoryStore.cs:2463-2503`), and `GetEntityFactStore` has a merged facts-cache fast path
  before it builds from `ListScoped` (`FileMemoryStore.cs:2141-2201`). These determine the read
  integration surface.
- `Upsert`, `Remove`, and `TryUpdateEntry` are scope writes (`FileMemoryStore.cs:1302-1602`), and
  `StoreAsync`/`AppendAsync` read then write their scope (`:1608-1835`). They must not bypass a
  pending restore journal. `WriteArtifactAsync` is a public file-I/O primitive
  (`FileMemoryStore.cs:1953-1960`) and must be treated as a scope write as well.

The existing `DurableFile.AtomicWriteAllBytes` writes through a flushed sibling temporary file
then replaces the destination (`src/Scrinia.Core/DurableFile.cs:38-70`). It is the required
primitive for the journal, journal state transition, and replayed destination bytes. Its stated
directory-flush limitation remains true (`:6-9`), which is why replay must be idempotent.

## Scope and transaction model

1. A restore transaction owns exactly one canonical persistent topic scope (for example,
   `local:notes`) and is created only after the existing bundle build pass has validated that
   topic. The selected scope is already validated by
   `MemoryNaming.BuildScopedTopicScope` in the bundle build pass
   (`BundleFormatService.cs:212-217, 233-244`).
2. The transaction includes only entries that the current import rules will apply: an existing
   entry is skipped when `overwrite` is false, and an entry without a body is skipped, matching
   today's behavior (`FileMemoryStore.cs:2754-2768`). It does not journal no-op entries.
3. For every included entry, transaction construction produces the final, normalized
   `ArtifactEntry`, the exact incoming `.nmp2` body bytes, and the exact serialized
   `.meta.json` sidecar bytes. The sidecar is serialized once for the transaction; replay never
   regenerates it from mutable code or disk state.
4. The transaction's staged payload lives in the journal. Recovery never opens, re-reads, or
   depends on the source ZIP. The source bundle may be removed immediately after the journal is
   durable.
5. Existing import behavior remains additive: it does not claim atomicity across all selected
   topics or file-entity categories. Skill and agent file entities remain prefix-consistent under
   their existing dedicated import path and are explicitly out of scope.

## Durable journal contract

Each persistent topic directory may contain one reserved file named
`.restore-journal.json`, beside its `.lock`, artifact bodies, and artifact sidecars. It is a
store-private control file: discovery and sidecar enumeration must ignore it. A second restore
for that scope cannot start while one journal exists; it first recovers that journal under the
scope lock.

The journal is UTF-8 JSON with no BOM, written through `DurableFile.AtomicWriteAllBytes`. Version
1 has this logical shape (JSON property order is not semantic):

```json
{
  "format": "scrinia.restore-journal",
  "version": 1,
  "state": "prepared",
  "transactionId": "d85b1407b7934e0fa0e0c6a895aa0f33",
  "scope": "local:notes",
  "entries": [
    {
      "name": "daily-log",
      "bodyFile": "daily-log.nmp2",
      "bodyBase64": "aGVsbG8=",
      "bodySha256": "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
      "sidecarFile": "daily-log.meta.json",
      "sidecarBase64": "aGVsbG8=",
      "sidecarSha256": "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"
    }
  ]
}
```

`bodyFile` and `sidecarFile` are file names, not paths. They must exactly equal the canonical
sanitized names derived from `name` with `.nmp2` and `.meta.json` appended; separators, rooted
paths, `.`/`..`, duplicates after case-insensitive comparison, and noncanonical names are
malformed. Entries are non-empty and have unique names/files under ordinal-ignore-case comparison.
The decoded byte length is bounded by the normal import limits plus the serialized sidecars; it
must be validated before allocation beyond the encoded journal content. Each SHA-256 is calculated
`hello` payload demonstrates the encoding and digest grammar only; a real journal carries the
actual NMP/2 body and serialized sidecar bytes for its entry.

The only version-1 states are `prepared` and `completed`:

- `prepared` means the complete desired final state is durable and must be replayed.
- `completed` means all destination hashes were verified; cleanup was interrupted and may be
  retried without rewriting destinations.

No `applying` cursor is stored. A cursor adds a second recovery state that can itself tear while
providing no correctness benefit: replaying every transaction-owned final byte sequence is
idempotent. The journal is retained until completion cleanup succeeds.

Add a `DurableFormats.RestoreJournal` contract and include it in `DurableFormats.All`
(`src/Scrinia.Core/Formats/DurableFormats.cs:15-91`). Version 1 is current and absent is invalid.
Older and newer journal versions both **refuse access**: unlike an artifact sidecar, this file is
an executable recovery instruction, so an older build must not guess how to replay a newer one.
The existing registry pattern and central evaluator are `FormatContract`
(`src/Scrinia.Core/Formats/FormatContract.cs:3-23`) and `FormatVersion.Evaluate`
(`src/Scrinia.Core/Formats/FormatVersion.cs:5-35`).

## State machine and idempotent recovery

### Normal restore

1. Under the scope exclusive lock, recover any pre-existing journal first.
2. Cold-load the current index, construct the additive import result, and preflight every final
   entry for the existing read-only sidecar policy. The current whole-list preflight precedent is
   `FileMemoryStore.cs:2809-2828`.
3. Serialize and hash all transaction-owned body and sidecar bytes in memory. Validate the journal
   object before it is written.
4. Durably write the `prepared` journal. This is the transaction commit point: no live destination
   may be changed before this succeeds.
5. Replay the journal: for each entry, verify its embedded decoded hashes, then atomically replace
   the body and sidecar from the journal bytes. Replay order is body then sidecar for each entry,
   retaining the current prefix-consistency property even during a live process.
6. Re-read every replayed destination and verify its SHA-256 equals the journal's expected hash.
7. Atomically rewrite the journal with `state: completed`.
8. Only now invalidate the scope's index and artifact caches, invalidate the facts view, and publish
   the recovered index/cache state. Delete the completed journal last. If deletion fails, access
   still succeeds; the next access verifies `completed` and retries cleanup.

### Automatic recovery

Every persistent scope access starts with `EnsureScopeRecovered(scope)`:

1. Acquire the existing shared scope lock without materializing an absent directory. If no journal
   exists, release it and continue. This preserves read-only behavior for a never-written scope
   (`FileLock.AcquireShared`, `FileLock.cs:40-59`).
2. If a journal exists, release the shared lock, acquire the existing exclusive lock, and re-check
   for it. The re-check resolves concurrent recovery or cleanup.
3. Parse and validate the entire journal, including format/version, scope identity, canonical names,
   base64 payloads, and hashes, before changing any destination.
4. For `prepared`, replay every entry, verify all destinations, mark it `completed`, invalidate
   unpublished stale state, and delete it. For `completed`, verify every destination hash before
   deleting it; a mismatch is a blocking recovery failure, not permission to overwrite a state
   whose completion evidence is contradicted.
5. Release the lock only after recovery or cleanup completes. A subsequent normal operation then
   runs from disk/current state; it never uses an index, artifact, or facts cache that predates the
   recovered scope.

Crashes at any point before `completed` leave `prepared`, which replays the full desired state.
A crash after `completed` but before deletion leaves verified completion evidence and only repeats
verification/cleanup. Replaying an already-written body or sidecar is safe because the target byte
sequence and hash are immutable transaction data.

Malformed JSON, unsupported version, wrong scope, invalid name, duplicate target, undecodable
payload, invalid digest, embedded-payload hash mismatch, or `completed` destination-hash mismatch
does **not** delete, rename, overwrite, or quarantine the journal. It remains in place and blocks
the scope. This deliberately favors a visible, inspectable recovery artifact over a guessed repair.

## Locking and read/write integration

Recovery is not an import-only preamble. It is an access barrier.

- Wrap the existing exclusive-lock write path so it calls the unsafe recovery helper after acquiring
  the lock and before its body. Avoid recursive lock acquisition by keeping a separate
  `RecoverScopeUnsafe` helper whose caller already owns both locks.
- Add the shared-check/exclusive-escalation gate before `LoadIndex` can return its cache fast path,
  before `ReadArtifactAsync` can return its artifact-cache fast path, and before direct artifact
  resolution paths can read files (`ResolveArtifactAsync` currently reads a qualified artifact
  directly at `FileMemoryStore.cs:2039-2077`).
- `ListScoped`, `AnyScoped`, and `SearchAll` inherit the barrier through their per-scope index
  loads; verify every direct scope/file read rather than assuming this inheritance.
- Before `GetEntityFactStore` returns its merged in-memory cache, it must gate all persistent
  readable scopes. It may return a cached fact store only after those gates complete. Recovery
  invalidates `_factStore`, increments its generation through the existing invalidation mechanism
  (`FileMemoryStore.cs:2230-2245`), and drops any queued facts derived from the pre-recovery
  generation. A rebuilt facts sidecar is derived after recovery; it is never journal payload.
- On recovery completion, invalidate `_indexCache` for the recovered scope and the scope's artifact
  LRU prefix before any cache publication. Do not publish a recovered index before the journal is
  `completed` and destination hashes verify. This corrects the current cache-first behavior
  documented at `FileMemoryStore.cs:685-770` and the facts fast path at `:2147-2154`.

The lock is scoped only to the recovered topic. Other topic scopes and ephemeral memories remain
available. A scope lock timeout remains a normal lock-contention failure; it is not a recovery
success and must not bypass the journal.

## Errors and user-visible semantics

Introduce a distinct recovery-blocked exception/error code rather than mapping journal failures to
`INVALID_BUNDLE` or a generic durable-write failure. The bundle has already been converted into a
durable local recovery obligation; the source ZIP is no longer relevant.

For MCP and CLI surfaces, the error must identify the canonical scope and journal path and use
actionable copy. Required forms are:

- **Unsupported journal:**
  `Recovery is blocked for topic scope 'local:notes': restore journal version 2 is newer than this build supports (1). The journal was left unchanged at '/workspace/.scrinia/memories/notes/.restore-journal.json'. Upgrade scri, then retry the operation.`
- **Malformed or integrity-failed journal:**
  `Recovery is blocked for topic scope 'local:notes': restore journal '/workspace/.scrinia/memories/notes/.restore-journal.json' is malformed or failed integrity verification. No files were changed and the journal was retained. Inspect or restore the journal with a compatible scri build, then retry.`
- **Replay I/O failure:**
  `Recovery could not complete for topic scope 'local:notes'; the journal remains at '/workspace/.scrinia/memories/notes/.restore-journal.json' and access is blocked until retry succeeds. Check permissions and free disk space, then retry.`

The import response remains successful only after every selected topic's journal has completed and
been cleaned up. An automatic recovery performed by a later read or write is normally transparent;
if it fails, that operation fails with the recovery-blocked error rather than returning stale or
partial results. Existing bundle parsing/version errors retain their `INVALID_BUNDLE` behavior in
`MemoryTools.Bundling.Import` (`src/Scrinia.Mcp/MemoryTools.Bundling.cs:104-170`).

## Test and failure-injection matrix

Add focused tests beside `tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs`, which already
tests the current scope lock, cold reads, staging invisibility, and prefix consistency
(`:49-70, 161-370`). Tests must use a fresh `FileMemoryStore` for post-crash assertions so caches
cannot hide disk state.

| Scenario | Injection / setup | Required assertion |
|---|---|---|
| Crash before journal move | `DurableFile.FailureInjector` at `BeforeMove` for the journal path | No live destination changes; no valid journal means a later access behaves normally. |
| Crash after prepared journal, before first body | Inject after journal write / before replay | Later read auto-recovers every entry from journal without the ZIP. |
| Crash between body and sidecar | Fail the sidecar replace for entry 1 | Later access roll-forwards body and sidecar to matching journal hashes. |
| Crash between entries | Fail body replace for entry 2 | Later access applies all entries; entry 1 is safely replayed. |
| Crash after all destination writes, before completed | Fail completed-journal rewrite | Later access verifies/replays and cleans up. |
| Crash after completed, before delete | Leave a valid completed journal | Later access verifies destinations, deletes journal, and makes no destination write. |
| Prepared replay I/O failure | Deny/lock a destination or inject `BeforeMove` | Journal remains; scope stays blocked; another scope remains readable. |
| Unsupported version | Handcraft a valid version-current+1 journal | Every read and write route for that scope fails with upgrade guidance; journal bytes remain unchanged. |
| Malformed JSON / wrong scope / noncanonical target / duplicate target | Handcraft each journal | No destination is changed; journal remains; access is blocked. |
| Base64 or embedded-hash mismatch | Change one payload character or digest | No destination is changed; journal remains; access is blocked. |
| Completed destination mismatch | Mark completed then alter one live destination | Access blocks and retains journal; it must not silently overwrite the contradictory destination. |
| Index cache barrier | Warm `LoadIndex`, create prepared journal externally, then call `LoadIndex` | It recovers before returning; returned entries reflect journal bytes. |
| Artifact cache barrier | Warm `ReadArtifactAsync`, create prepared journal, then read again | It recovers before returning; no stale artifact-cache value is returned. |
| Facts barrier | Warm `GetEntityFactStore`, create a prepared journal changing an entity, then query facts | Recovery runs before facts return; result reflects recovered attributes and no stale facts cache is published. |
| Concurrent access | Hold/recover a scope while another store reads or writes it | The other operation blocks or times out loudly; it never observes a half-recovered scope. |
| Multi-topic import | Interrupt one topic after a prior topic completes | No whole-bundle atomicity claim; completed topic remains available, interrupted topic recovers independently. |
| Existing import semantics | `overwrite: false`, missing body, newer sidecar preflight | Skipped entries are absent from the journal; existing refusal/no-op behavior remains intact. |

Extend the failure seam only as needed to distinguish journal write, replay body, replay sidecar,
completed-state write, and cleanup. The existing async-local seam and phases are in
`src/Scrinia.Core/DurableFile.cs:13-20, 41-97`; production code must not expose a test injector.

## Non-goals

- No atomicity claim across the entire ZIP/bundle, multiple topic scopes, or file-entity categories.
- No rollback to pre-import bytes. The journal owns only the incoming final state and recovery is
  roll-forward.
- No recovery from a malformed, hash-mismatched, or unsupported journal by deleting it,
  synthesizing metadata, or rereading the source ZIP.
- No changes to skill or agent file-entity import semantics. Their existing prefix-consistent
  behavior remains out of scope.
- No journal for ephemeral memories, ordinary `StoreAsync`/`AppendAsync` transactions, migration,
  compaction, or unrelated write paths. Those paths only participate in the access barrier when
  they target a scope that already contains a restore journal.
- No background sweeper. Recovery is synchronous and demand-driven so callers never receive data
  from a scope before its recovery outcome is known.

## Migration and compatibility

There is no migration for existing scopes: a journal is absent in all current workspaces, and
absence means no pending restore. Current `.staging` files remain governed by their existing import
cleanup rule (`FileMemoryStore.cs:2866-2880`); they are not interpreted as journals and do not
become recoverable transactions.

New builds write only version-1 journals. A build that encounters a newer or otherwise unsupported
journal must preserve it and block that scope until a compatible newer build performs recovery.
Older builds have no journal reader; operational compatibility therefore requires upgrading all
processes that can access a workspace before relying on interruption recovery. The journal is a
short-lived runtime artifact and remains excluded from source control alongside `.lock` files;
it must never be exported as a memory artifact or bundle file.

## Self-review

Reviewed for placeholders, contradictions, and ambiguity:

- No TBD/TODO or deferred implementation decisions remain.
- The design consistently specifies roll-forward, per-topic-scope recovery and explicitly rejects
  whole-bundle atomicity and rollback.
- The journal owns final body and sidecar bytes and hashes; recovery never depends on the ZIP.
- `prepared` and `completed` are the only states, and replay/cleanup behavior is defined for every
  crash boundary.
- Unsupported, malformed, and integrity-failed journals remain in place and block access; only a
  verified completed journal may be cleaned up.
- Cache and facts barriers are specified before their current fast paths, preventing publication
  before recovery completes.
