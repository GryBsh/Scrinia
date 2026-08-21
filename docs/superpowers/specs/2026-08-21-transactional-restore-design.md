# Transactional restore — design

**Stream A of 6** from the 2026-08-21 design review. Addresses finding **C1** (bundle restore
silently deletes concurrently-written memories and reverts metadata on memories the bundle never
mentions), plus the crash-safety hole recorded as an aside on that finding.

**Goal:** importing a bundle must never destroy a memory it was not asked to touch, and must never
leave an entry whose content and sidecar describe different versions.

## The root shape

`ImportTopicEntries` is an unlocked read-modify-write whose commit step deletes files it did not
decide were stale. It snapshots the index under a shared lock that releases immediately, writes
every bundled `.nmp2` with no lock held at all, then calls `SaveIndex` — which takes an exclusive
lock and deletes every `.meta.json` absent from a snapshot that is now seconds old.

The fix is not to widen the lock around that sweep. It is to **remove the sweep from the import
path**, because import is purely additive: it adds entries and overwrites named ones, and never
removes. The sweep is not a capability import needs; it is the mechanism that destroyed the data.

This distinction is the whole point. Guarding the caller leaves the mechanism armed for the next
caller. The 2026-08-21 review found that this exact mechanism was already understood — a test in
`DualModeParseTests` documents that `SaveIndexUnsafe`'s sweep will delete another scope's sidecars
when handed a stale list, calls it "the data-loss path behind the aliasing", and was deliberately
written against the store rather than the parser because the hazard outlives the parser fix. The
trigger was closed. The mechanism was not, and `ImportTopicEntries` reached it by another route.

### Verified state at `9c0144a`

- `ImportTopicEntries` is `FileMemoryStore.cs:2375-2419`. Lock acquisitions are `LoadIndex`
  (shared, scoped to that method) and `SaveIndex` (exclusive, scoped to that method). Nothing spans
  them.
- `SaveIndexUnsafe` (`:1196-1237`) does six things: normalize attribute keys in place, delete
  orphaned sidecars, `WriteSidecars`, refresh `_indexCache`, clear `_cachedTopics` for topic
  scopes, invalidate `_factStore`. Import needs all of these **except** the sweep.
- `SaveIndexUnsafe`'s own comment states: *"ImportTopicEntries — the only production caller of
  `SaveIndex`."* Confirmed by enumeration — `SaveIndexUnsafe` has one entry point (`SaveIndex`),
  which has one production caller. `Upsert` and `Remove` never reach it.
- `ImportTopicEntries` has exactly one production caller, `BundleFormatService.cs:446`, in a loop
  over topics inside a documented WRITE PASS whose invariant is that the preceding build pass has
  no side effects.
- `WriteSidecar(entry, storeDir)` already exists per-entry (`:969`).
- `WithExclusiveLock` uses a non-recursive `ReaderWriterLockSlim` (`:611`), so the inner calls must
  become the `*Unsafe` variants — re-entering throws `LockRecursionException`.
- `FileLock.AcquireExclusive` takes no timeout from `WithExclusiveLock` and inherits a 5-second
  default (`FileLock.cs:36`).

### Reproduced, not inferred

A harness against the built `Scrinia.Core` — 300-entry bundle, one concurrent `StoreAsync` 400 ms
in, verified against a **cold** reload rather than the warm cache — produces both failure shapes:

```
StoreAsync returned SUCCESS for: urgent-note
  immediately after write: .nmp2=True .meta.json=True inIndex=True
        ↓ restore completes
urgent-note .meta.json on disk  : False
urgent-note in COLD LoadIndex   : False
cold index count                : 301   (expected 302)
  ORPHAN: urgent-note.nmp2 (107 bytes)

seed sidecar OriginalBytes      : 25                          ← version ONE
seed .nmp2 length on disk       : 110                         ← version TWO
seed preview in sidecar         : "seed content, version ONE"
```

The window is not a narrow race. It is the entire `.nmp2` write loop — one `Flush(flushToDisk:
true)` per bundled memory — so it widens with bundle size.

## The constraint that governs the design: an entry is two files

`name.nmp2` and `name.meta.json` cannot be updated atomically together on any filesystem we
target. Content-then-sidecar leaves new content under an old sidecar; sidecar-then-content leaves
the reverse. "Crash-safe" therefore has to mean *recoverable* or *window narrowed to a single
rename*. This design takes the second, and Part C states precisely what it does not buy.

## Part A — one critical section, no sweep

`ImportTopicEntries` becomes a single exclusive-locked region. Inside it:

1. `SweepStaleStaging(storeDir)` — remove staging litter left by any prior crash. Safe because we
   hold the exclusive lock, so no concurrent import is staging into this scope.
2. `LoadIndexUnsafe(topicScope).ToList()` — the snapshot is now taken under the lock that will
   commit it. The `.ToList()` stays: `LoadIndexUnsafe` returns the cache's live backing list, and
   the never-mutate-a-published-list rule still applies.
3. Stage, then commit (Part B).
4. `PublishIndexUnsafe(existing, topicScope)`.

### A1. Extract `PublishIndexUnsafe`

Split the non-destructive tail of `SaveIndexUnsafe` into a private helper, so import and save share
one definition rather than import restating five steps:

```csharp
// Normalize attribute keys in place, WriteSidecars, refresh _indexCache,
// clear _cachedTopics for topic scopes, invalidate _factStore.
private void PublishIndexUnsafe(List<ArtifactEntry> entries, string scope)
```

Then:

| Caller | Composition |
|---|---|
| `SaveIndexUnsafe` | `DeleteOrphanedSidecarsUnsafe(entries, scope)` + `PublishIndexUnsafe(entries, scope)` |
| `ImportTopicEntries` | `PublishIndexUnsafe(entries, scope)` only |

`SaveIndexUnsafe`'s behaviour is unchanged, so its public contract and its existing callers (none in
production; tests may drive it) are unaffected. This is consolidation onto one shared path with the
rule intrinsic to it, not a sixth copy of the publish tail.

**Do not** add a `bool sweepOrphans` parameter to `SaveIndexUnsafe` instead. A boolean whose `true`
value deletes files is precisely the kind of parameter this codebase's review history keeps
flagging; two named methods that compose are clearer at the call site and impossible to get
backwards.

### A2. The list is no longer stale by construction

Because the snapshot and the commit now happen inside one lock, `expected`-style reasoning about a
stale list cannot recur here even if a sweep is reintroduced later. The comment currently at
`:1217-1218` — *"Runs under the caller's write lock… so this is scope-local and safe"* — is true
and was always about the wrong interval; it should be amended to say what the sweep actually
requires: that the entry list was read under the same lock that commits it.

## Part B — stage, then commit per entry

### B1. Staging

Every bundled body is written to a staging path in the destination directory, never to the live
artifact path:

```
{storeDir}/{sanitizedName}.import-{guid:N}.staging
```

Same directory, therefore same volume, therefore `File.Move` to the final name is a rename rather
than a copy. Staging continues to go through `AtomicWriteAllText`, so each staged body is itself
written temp-and-rename; the durability idiom is unchanged.

**The name deliberately contains no `.nmp2` segment.** `DiscoverMemoriesRecursive` (`:2317-2318`)
decides whether a directory is a v2 scope with:

```csharp
bool hasArtifacts = Directory.EnumerateFiles(dir, "*.nmp2").Any()
                 || Directory.EnumerateFiles(dir, "*.meta.json").Any();
```

An earlier draft of this design used `{name}.nmp2.import-{guid}.staging`, which almost certainly
does not match `*.nmp2` — but "almost certainly" is the wrong standard when the failure mode is a
half-restored scope being discovered as a topic. Keeping `.nmp2` out of the staging name removes
the question rather than answering it.

Verified: the suffix is invisible to every enumerator in the store.

| Enumerator | Glob | Matches staging? |
|---|---|---|
| `LoadEntriesFromSidecars` | `*.meta.json` | no |
| `SaveIndexUnsafe` orphan sweep | `*.meta.json` | no |
| `DiscoverMemoriesRecursive` | `*.nmp2` and `*.meta.json` | no |

A consequence worth stating: a scope directory holding only staging litter after a crash-before-any-commit
has `hasArtifacts == false`, so it is not discovered as a topic. The litter is inert until the next
import sweeps it.

### B2. Commit

Per entry, in this order, then move to the next entry:

```csharp
File.Move(stagedPath, finalPath, overwrite: true);
WriteSidecar(entry, storeDir);
```

A crash therefore leaves a **prefix** of entries fully applied and a suffix untouched, with every
individual entry internally consistent. This is what closes the `seed` shape: a live `.nmp2` is
never new while its sidecar is old, because the live file does not change until immediately before
its sidecar does.

Note the residual per-entry window between the rename and the sidecar write is a single metadata
operation with no data fsync — the body was already durable at staging time. That is as narrow as
this can be made without Part C's rejected journal.

### B3. Rollback

Any throw during staging or commit deletes every staging file this call created, then rethrows.
Entries already committed in the loop stay committed — they are consistent, and rolling them back
would require the previous content, which staging deliberately did not retain.

### B4. Lock timeout

`FileLock.AcquireExclusive` already accepts `TimeSpan? timeout` (`FileLock.cs:31`, defaulting to
5 seconds at `:37`). `WithExclusiveLock` does not pass one through, so the plumbing is an optional
parameter forwarded to the existing API — no new machinery.

Import passes **5 minutes**, fixed. Not scaled to entry count: a scaled timeout is a magic constant
plus a formula to get wrong, and this value is not a correctness parameter — it is a ceiling that
exists only so a legitimate long restore does not fail spuriously. The default for every other
caller stays 5 seconds.

Accepted consequence: a genuine deadlock on an import now takes 5 minutes to surface instead of 5
seconds. For a one-shot, user-initiated operation that is the right trade.

The failure message names the operation and the scope rather than surfacing a bare
`FileLockTimeoutException`, whose text points at a lock path the caller has no context for.

Consequence accepted deliberately: a concurrent writer to the same topic scope now blocks for the
duration of that scope's import, and fails loudly if it exceeds its own acquire timeout. A loud
failure a caller can retry is strictly better than a silent deletion.

## Part C — rejected alternatives

Recorded so they are not re-proposed, and so the residual risk is explicit.

### Rejected: widen the lock and keep the sweep

The deep dive that reproduced C1 proposed exactly this — wrap the method in `WithExclusiveLock` and
switch to the `*Unsafe` inner calls. It is correct and it fixes the reproduced failure. It is
rejected because it leaves a destructive sweep on a path that has no use for one, guarded only by
the discipline of whoever writes the next caller. Removing the sweep costs the same number of lines
and removes the hazard rather than fencing it.

### Rejected: whole-bundle atomicity across all scopes

Every topic and file entity committing together would need an ordered multi-scope lock protocol to
avoid deadlock against concurrent writers, and file entities live outside the scope store entirely.
Per-scope is the granularity the code and its return value (`importedTopics`, `importedEntries`)
already speak in, and a partial restore is reportable at topic granularity in a way it is not
within a topic.

### Rejected for now: a journal

An intent record written before applying and cleared after, with roll-forward or roll-back on next
open, is the only option that delivers genuine all-or-nothing per scope and closes the two-file
window. It is deferred rather than dismissed, for one reason: it introduces a new persistent
on-disk artifact, and **Stream B exists because nothing in this store has a schema version**.
Shipping the journal now would make it the sixth unversioned format. Revisit after Stream B, when a
journal can inherit versioning from birth.

### Rejected: shadow scope directory and swap

Windows cannot atomically swap a non-empty directory, and a swap would clobber concurrent unrelated
entries in that scope.

## Testing

The concurrency reproduction is real but timing-based, and this repo has been bitten by flaky
tripwires before. The load-bearing tests therefore carry no threads.

| Test | Pins | Threads |
|---|---|---|
| `Import_DoesNotDeleteAMemoryTheBundleNeverMentions` | The mechanism. A pre-existing memory absent from the bundle survives the import. **This is the test that would have caught C1.** | none |
| `Import_ThatFailsWhileStaging_LeavesEveryLiveArtifactUntouched` | B3 rollback. Fail one body mid-stage; assert no live `.nmp2` changed, no sidecar changed, no staging litter remains. | none |
| `Import_ThatFailsMidCommit_LeavesEveryAppliedEntryConsistent` | B2 ordering. Fail mid-commit; assert every applied entry's content and sidecar agree, and no entry is half-applied. | none |
| `Import_SweepsStagingLitterFromAPriorCrash` | A1 step 1. Pre-place a staging file; assert import removes it and does not import it. | none |
| `Import_ConcurrentWriteToTheSameScope_SurvivesOrFailsLoudly` | The reproduced defect, asserted as an invariant rather than a race: the concurrent write is present after a cold reload, **or** it threw. Never silently absent. | one |

The last test asserts a disjunction deliberately. Asserting "the write survives" would encode a
scheduling assumption; asserting "it survived or it threw" is the actual contract and cannot flake
into a false pass, because silent disappearance fails both branches.

`Scrinia.Merge.Tests` parity suites are unaffected — the merge driver does not go through this path.

## Scope boundaries

**In:** `ImportTopicEntries`, the `PublishIndexUnsafe` extraction, the staging and commit sequence,
the lock timeout plumbing, the five tests above, a CHANGELOG entry.

**Out:**

- `ImportFileEntities` (`BundleFormatService.cs:539`) runs after the topic loop and writes outside
  the scope store. It has the same additive shape and is worth the same treatment, but it does not
  reach `SaveIndexUnsafe` and is not implicated in C1. If it turns out to share the mechanism,
  raise it rather than widening this branch mid-flight.
- Whole-bundle atomicity, the journal, and crash recovery beyond staging-litter sweep — see Part C.
- Every other finding in the 2026-08-21 review. Streams B through F.

## Risks

- ~~`DiscoverMemoriesRecursive` may glob `*.nmp2`~~ — **resolved during spec review: it does.**
  The staging name was changed to exclude `.nmp2` entirely. No open question remains.
- **A long-held exclusive lock changes concurrent-writer behaviour** from "silently loses" to
  "blocks, then possibly throws". This is the intended trade and needs a CHANGELOG note, since a
  caller that previously appeared to succeed will now sometimes fail.
- **`PublishIndexUnsafe` extraction touches `SaveIndexUnsafe`**, which is on the write path for
  `SaveIndex`'s test callers. Behaviour must be byte-identical; the existing suite is the guard.
