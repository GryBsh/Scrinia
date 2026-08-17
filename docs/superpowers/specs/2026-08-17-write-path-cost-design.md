# Write-path cost — design

**Branch 5 of 7** from the 2026-08-16 design review. Addresses finding **H5**, plus the
vector-write-amplification question deferred out of branch 4.

**Goal:** one logical write should cost one round of I/O, not N.

---

## The root shape

H5 names four call sites, but they are symptoms of one rule:

> Every write eagerly rebuilds process-wide derived state from the whole corpus, inside the
> write lock.

Three structures are invalidated per write — `_indexCache[scope]`, the cross-scope `_factStore`,
and `_cachedTopics` — and the first is immediately repopulated by re-reading and re-deserializing
**every sidecar in the scope**. Writing N memories costs O(N²) file reads.

The eager repopulation is the defect. Invalidation is fine; rebuilding from disk inside the write
is not.

### Verified state at `3be003e`

The review's line numbers have shifted. Current sites:

| Site | What it does |
|---|---|
| `FileMemoryStore.cs:1049-1051` (`Upsert`) | `TryRemove` then `LoadEntriesFromSidecars(...).Select(NormalizeAttributesKey)` then install |
| `FileMemoryStore.cs:1118-1120` (`Remove`) | same |
| `FileMemoryStore.cs:1178-1180` (`TryUpdateEntry`, read) | full scan to find one entry |
| `FileMemoryStore.cs:1194-1196` (`TryUpdateEntry`, write) | same rebuild again — this method pays it twice |
| `MaintenanceEventSink.cs:~148` | `store.ListScoped(null)` — every scope, every write event |

`LoadEntriesFromSidecars` (`:747`) enumerates the directory and does a `File.ReadAllText` plus a
JSON deserialize per sidecar.

These compose: one `remember` carrying three refs costs a full cross-scope listing plus three
rounds of read-all-then-rebuild.

### Honest scale statement

This workspace holds ~16 memories. The cost is invisible today. This is a scaling cliff, not a
present fire — but it sits on the write path of a product whose entire purpose is accumulation,
and it is the repeated-small-I/O pattern that is undesirable on SSD-backed and network-synced
stores (this repo lives on SynologyDrive). Part D measures it rather than asserting it.

---

## Constraint that governs part A: the aliasing invariant

`CachedIndex.Entries` (`FileMemoryStore.cs:73-83`) is **handed out by reference** from
`LoadIndex`. Callers get a live view, not a copy.

`docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md` establishes that this is
safe *only* because every cache write replaces the whole `CachedIndex` rather than mutating the
instance. That document names this branch's optimisation as the thing that would break it:

> **Any cache write that mutates an existing `CachedIndex.Entries` in place** rather than
> installing a new `CachedIndex`. … A future "incremental cache update" optimisation — patching
> `Entries` on `Upsert` instead of rebuilding from sidecars … breaks it directly.

**The review's prescribed fix — "update the cached list in place" — is therefore rejected as
written.** The implementation MUST build a new list and install a new `CachedIndex`.

This costs an O(N) copy of N references per write, against N file reads plus N JSON parses. The
copy is not the cost being removed.

### That analysis document is partly stale — re-verify, do not inherit

It treats `CachedIndex.NameToPosition` (`:66`), `Rebuild()` (`:77`), `Stats` (`:69`) and
`GetOrComputeCorpusStats()` (`:84`) as the load-bearing hazard. **None of those members exist at
`3be003e`** — `CachedIndex` now carries only `Entries` and a constructor. The hazard it guards
against is therefore already absent, which makes copy-on-write replacement simpler than the
document implies, not harder.

Implementation MUST re-verify the invariant against current code rather than citing the document.
Specifically, before changing any cache write, enumerate every `_indexCache` site and confirm each
is a whole-object replace. At `3be003e` they are: `:425` (`Clear`), `:742` (cold load), `:839`
(`TryRemove`), `:1024`, `:1049-1051`, `:1118-1120`, `:1194-1196`.

---

## Part A — stop rebuilding the index cache from disk on write

### A1. Move normalization to the load seam

The rebuild's stated justification is coalescing the legacy `fields` key, so a write-first session
never caches an entry with `Attributes == null` while its data sits in `LegacyFields` — which
would hide that entry's facets from search, filter and display for the process lifetime.

`ArtifactEntry.cs:67` states the real invariant:

> EVERY load seam calls `NormalizeAttributesKey()` before an entry [escapes]

`LoadEntriesFromSidecars` is that seam, and it does **not** call it. Four call sites paste
`.Select(e => e.NormalizeAttributesKey())` around it instead — the same rule expressed four times,
which is why removing any one of them would be a silent data-visibility bug.

**Change:** apply `NormalizeAttributesKey()` inside `LoadEntriesFromSidecars`, at the seam. The
four `.Select(...)` wrappers then become redundant and are deleted.

This makes cheap invalidation safe *by construction* rather than by remembering to normalize, and
it is a prerequisite for A2 — not an optional tidy-up.

Note `AttributeMigration.MigratesTo` (`AttributeMigration.cs:68`) already calls
`NormalizeAttributesKey()` as its first step, and the cold path (`LoadIndexUnsafe:731-739`) runs
`MigratesTo` over every entry — but only *installs* the result when it returns `true`. The seam
change closes that gap rather than depending on it.

### A2. Copy-on-write cache update

At each of the three write sites (`Upsert`, `Remove`, `TryUpdateEntry`'s write):

- **Scope is cached:** build a NEW `List<ArtifactEntry>` from the existing cached entries with the
  written entry's slot replaced (or appended for a new entry, or omitted for a remove), and
  install a NEW `CachedIndex`. Zero sidecar reads.
- **Scope is not cached:** invalidate only (`TryRemove`) and let the next read cold-load. Do NOT
  populate a partial cache — a cache holding only the just-written entry would make `LoadIndex`
  return one entry for the whole scope.

Entry identity for slot replacement uses the same rule the current code uses for lookup:
`SanitizeName(e.Name).Equals(SanitizeName(target), OrdinalIgnoreCase)`.

**Ordering must be decided explicitly, not left to fall out.** Today every cache population comes
from `LoadEntriesFromSidecars`, i.e. `Directory.EnumerateFiles` order. A copy-on-write update that
appends a new entry at the end produces a *different* order than a cold load of the same scope
would — so a warm cache and a cold cache could disagree, and a test that passes warm could fail
cold (or vice versa). Two acceptable resolutions:

- replace-in-slot for existing entries (preserving position) and append for new ones, and
  **verify no caller depends on `LoadIndex` ordering** — enumerate the callers to establish it; or
- sort both paths by the same key so warm and cold are identical by construction.

Implementation must pick one, state which, and cover it with a test that compares a warm-updated
cache against a cold load of the same scope. Do not leave this implicit.

### A3. `TryUpdateEntry` stops paying twice

Its read (`:1178`) does a full `LoadEntriesFromSidecars` scan to find one entry. Serve that read
from the cache when the scope is cached, falling back to the existing scan when it is not. The
comment at `:1172-1177` accepts the scan cost on the grounds that a miss and a hit pay the same
thing; that reasoning is unchanged, and this only removes the cost when the answer is already in
memory.

### A4. Precise `_cachedTopics` invalidation

`_cachedTopics = null` fires on every write to a `local:` scope (`:1055`, `:1127`, `:1199`),
forcing `DiscoverTopics` (`:2163-2176`) to re-enumerate directories. The topic list changes only
when a scope directory appears or empties — not when writing into an existing one.

**Change:** invalidate when the scope directory is created, and on removal of a scope's last
entry. Not on every write.

### What part A does NOT change

- The locking model. `WithExclusiveLock` stays exactly as-is.
- `LoadIndex`'s aliasing behaviour or return type.
- The cold-load path, including its forward-fill write-back.
- `_factStore` invalidation semantics (see part B).

---

## Part B — orphan detection

`MaintenanceEventSink` answers "does any entry reference me?" with a forward scan of the entire
store, once per write event: `var allEntries = store.ListScoped(null);` then `.Any(...)`.

### Rejected: route it through `EntityFactStore`

`EntityFactStore` (`Search/EntityFactStore.cs`) is already a reverse index — it keys every
attribute `(key, value)` pair as an entity, so `("ref", target)` → `MemoriesFor(...)` is exactly
the question being asked.

**It is nevertheless the wrong answer, and the spec records why so the next reader does not retry
it.** `_factStore` is invalidated by every `Upsert`/`Remove`/`TryUpdateEntry` — including the write
that immediately precedes the sink. A cold `GetEntityFactStore()` calls `ListScoped(null)` *and*
reads or rebuilds a `facts.json` sidecar per scope. Routing the orphan check through it would do
strictly more work than the scan it replaced.

Making `_factStore` incrementally maintained is a larger change (cross-scope merge, per-scope
sidecar validity tokens) with its own risk, and is out of scope here.

### Accepted: make the existing scan cheap

After part A, `LoadIndex(scope)` serves from warm memory with zero file reads, so
`ListScoped(null)` is already close to free. Two things remain:

- **A4 removes the per-write directory enumeration.**
- **Short-circuit the scan.** The sink materializes a `List<ScopedArtifact>` of every entry in the
  store and then asks `.Any(...)` of it. Enumerate lazily and stop at the first inbound ref: no
  full materialization, no allocation proportional to corpus size, and early exit on the common
  case where a referenced memory is found quickly.

Semantics are unchanged — same predicate, same answer, same immediacy. Orphan marking stays
synchronous; no deferral to a periodic sweep.

---

## Part C — one file operation per memory, not per vector

`MemoryVectorIndexer.IndexAsync` (`MemoryVectorIndexer.cs:133-138`) currently does:

```csharp
await _store.RemoveAsync(scope, subject, ct);
for (int i = 0; i < plan.Count; i++)
    await _store.UpsertAsync(scope, subject, plan[i].ChunkIndex, vectors[i], ct);
```

`VectorStore.UpsertAsync` (`VectorStore.cs:~115-185`) is per-vector: it takes the scope lock,
rebuilds the whole in-memory vector list copy-on-write, calls `File.Exists` + `ProbeAppendable`,
then appends a delete op and an add op. `RemoveAsync` (`:~190-240`) does its own lock, probe and
appends.

For a memory with K vectors, where the memory previously held P vectors: **1 + K lock
acquisitions, 1 + K file probes, and up to P + 2K append operations** (P deletes from
`RemoveAsync`, then per `UpsertAsync` a delete op when it supersedes an entry plus an add op).
Branch 4's per-element windowing multiplied K by roughly 4–5×.

### The API

```csharp
public async Task ReplaceMemoryVectorsAsync(
    string scope, string name,
    IReadOnlyList<(int? ChunkIndex, float[] Vector)> vectors,
    CancellationToken ct = default)
```

One lock; one copy-on-write rebuild that drops every existing entry for `name` and adds all new
ones in a single pass; one `ProbeAppendable`; one append carrying every op (deletes for the
superseded set, adds for the new); one compaction check. When not appendable, one
`SaveAsSvf3Async`.

`IndexAsync`'s remove-then-loop becomes a single call.

**This is a correctness improvement, not only a cost one.** The current code's own comment admits
the loop is not atomic — "If the upsert loop is interrupted the next index pass rebuilds this
memory cleanly." A single batched write makes the per-memory replace genuinely atomic, which is
what that comment wanted.

`UpsertAsync` and `RemoveAsync` stay as public single-item APIs: they have callers in
`Scrinia.Plugin.Embeddings.Cli/EmbeddingsTools.cs:101` and across the test suite. This adds an
API; it does not replace one.

Name/chunk-index matching uses `OrdinalIgnoreCase`, matching `UpsertAsync`, `RemoveAsync` and the
op-log replay in `ReadEntryOps`.

---

## Part D — measurement

Per the recorded preference: measure and report, do not theorize away. Benchmarks are evidence,
never the design target — nothing here is tuned to move a number.

Harness follows `tests/Scrinia.Benchmarks/LoadIndexBenchmarks.cs` (BenchmarkDotNet, `-c Release`)
and the before/after table format of the 2026-08-10 analysis, including its environment block.

1. **Store write path.** N sequential `Upsert` calls at N = 100 / 1000 / 5000, before and after.
   Demonstrates the O(N²) → O(N) shape rather than asserting it. Report mean, CI and allocation.
2. **Vector write path.** Indexing one memory of K vectors, before and after part C, counting file
   operations as well as time.
3. **Vector multiplication (reporting only).** Measured vectors-per-memory across a representative
   corpus, to quantify branch 4's per-element windowing. This is the input to the deferred
   decision on reducing vector *count* — **reported, not acted on in this branch.**

Shipped with a PowerShell script so the numbers are reproducible rather than a one-off paste.
Results are recorded in the spec's companion analysis file, not only in a commit message.

---

## Testing

The correctness risk is concentrated in the aliasing invariant and the normalization seam.

- **Aliasing.** A reader holding a `LoadIndex` result across a concurrent write still sees a
  consistent snapshot — never a torn or mutated list. This is the test that would fail if someone
  later "optimises" A2 into an in-place patch.
- **Normalization seam.** An entry whose data sits in `LegacyFields` is visible through
  `Attributes` after a write-first session with no prior cold load — the exact bug the four
  deleted `.Select(...)` wrappers existed to prevent. Must fail if the seam change is reverted.
- **Cold-cache write.** A write against an uncached scope leaves the cache absent, and the next
  `LoadIndex` returns the whole scope, not just the written entry.
- **`_cachedTopics`.** A new topic scope appears in `DiscoverTopics` after its first write; writing
  again into an existing topic does not re-enumerate.
- **Orphan detection.** Unchanged behaviour: same marking and clearing outcomes as today,
  including the dual-marked migration case the sink documents. Plus: the scan short-circuits.
- **Vector batch equivalence.** `ReplaceMemoryVectorsAsync` produces byte-identical on-disk state
  and identical in-memory vectors to the remove-then-loop it replaces, including the shrink case
  (a memory losing vectors must not keep stale higher chunk indices).
- **Vector batch atomicity.** A memory's vectors are never observable half-replaced.

Every existing test must keep passing; the suite is the regression gate for behaviour this branch
explicitly does not intend to change.

---

## Scope boundaries

**In:** the three cost defects above, their measurement, and the normalization-seam consolidation
that makes part A safe.

**Out, deliberately:**

- **Reducing vectors per memory** (branch 4's per-element windowing). Measured in part D, decided
  separately — it trades retrieval precision and needs its own recall measurement.
- **Incremental `_factStore` maintenance.** Larger, riskier, and not required by any part here.
- **The locking model.** Untouched.
- **`EmbeddingsPlugin.ConfigureServices`'s missing overlap guardrail** and the **missing
  `ReindexStoreAsync` ephemeral test**, both carried forward from branch 4 — they belong to branch
  6's surface work, not here.

---

## Risks

- **The aliasing invariant is subtle and its guiding document is stale.** Mitigation: re-verify
  against current code, enumerate every `_indexCache` site before changing any, and pin the
  invariant with a test rather than a comment.
- **The normalization seam change touches every read.** Mitigation: it is additive — `LegacyFields`
  is never cleared — and the four call sites it replaces already did exactly this to their own
  results.
- **`local:` scope-directory lifecycle for A4.** Getting "directory appears or empties" wrong
  silently stales the topic list. Mitigation: test both transitions explicitly.
