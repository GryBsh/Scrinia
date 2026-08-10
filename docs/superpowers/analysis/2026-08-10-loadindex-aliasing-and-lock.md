# `LoadIndex` aliasing safety and lock necessity — analysis

**Commit analysed:** `f60ddbc09d538a76c58af80ac68fcb86d8bef398` (branch `store-read-path`; the only
delta from `ef3221d` is a docs-only plan file, so every code fact below is also true at `ef3221d`)

**Verdict A (aliasing):** SAFE WITH CONDITIONS
**Verdict B (cache-hit lock):** SAFE TO SKIP

This is a written finding only. No source file was changed to produce it.

---

## Scope of the two questions

`FileMemoryStore.LoadIndexUnsafe` returns `cached.Entries.ToList()` on a cache hit
(`src/Scrinia.Core/FileMemoryStore.cs:592-593`), and `LoadIndex` wraps every call — hit or miss —
in `FileLock.AcquireShared(GetLockPath(scope))` (`:576`). Two separate questions:

- **A.** May `LoadIndexUnsafe` hand back `cached.Entries` itself instead of a copy?
- **B.** What does the shared file lock protect *on the cache-hit path*, and may it be skipped there?

They are independent. B does not depend on A.

---

## Call-site classification

Step 1 result: `rtk grep -rn "LoadIndex(" src/` returns **23 lines across 9 files**, matching F11
exactly. Two are declarations (`IMemoryStore.cs:20`, `FileMemoryStore.cs:574`), leaving the 21 sites
below.

A site is MUTATING if it calls `Add`/`AddRange`/`Remove`/`RemoveAt`/`RemoveAll`/`Insert`/`Clear`/
`Sort`/`Reverse` or an indexer *set* on the returned instance, or passes that instance somewhere
that does. Every site was read in context, and where the result was bound to a local, that local was
grepped across its whole file to confirm no later use.

| # | File:line | Verdict | Deciding expression | Reason |
|---|---|---|---|---|
| 1 | `src/Scrinia/Services/EntityTypingEventHandler.cs:38` | READ-ONLY | `store.LoadIndex(scope).FirstOrDefault(e => …)` (`:38-39`) | Never bound to a local; LINQ only |
| 2 | `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:574` | READ-ONLY | `entries.FirstOrDefault(e => e.Name == subject)` (`:575`) | `entries` occurs at `:574` and `:575` only |
| 3 | `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:710` | READ-ONLY | `foreach (var entry in agentEntries)` (`:711`) | `agentEntries` occurs at `:710` and `:711` only |
| 4 | `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:722` | READ-ONLY | `foreach (var entry in patternsEntries)` (`:723`) | `patternsEntries` occurs at `:722` and `:723` only |
| 5 | `src/Scrinia.Mcp/MemoryTools.Core.cs:319` | READ-ONLY | `existingEntries.FirstOrDefault(e => e.Name == subject)` (`:320`) | `existingEntries` occurs at `:319` and `:320` only; the method then works off the single `existingEntry` |
| 6 | `src/Scrinia.Mcp/MemoryTools.Core.cs:466` | READ-ONLY | `entries.FirstOrDefault(e => e.Name.Equals(subject, …))` (`:467`) | This method's `entries` occurs at `:466`/`:467` only (the `entries` at `:640+` is a different method's `List<ScopedArtifact>`) |
| 7 | `src/Scrinia.Mcp/MemoryTools.Core.cs:1123` | READ-ONLY | `existingEntries.FirstOrDefault(e => e.Name == subject)` (`:1124`) | `existingEntries` occurs at `:1123` and `:1124` only |
| 8 | `src/Scrinia.Server/Services/MemoryOrchestrator.cs:106` | READ-ONLY | `existingEntries.FirstOrDefault(e => e.Name == subject)` (`:107`) | `existingEntries` occurs at `:106` and `:107` only |
| 9 | `src/Scrinia.Server/Services/MemoryOrchestrator.cs:259` | READ-ONLY | `existingEntries.FirstOrDefault(e => e.Name == subject)` (`:260`) | `existingEntries` occurs at `:259` and `:260` only |
| 10 | `src/Scrinia.Core/Bundles/BundleFormatService.cs:56` | READ-ONLY | `entries.Count` (`:58`) and `new BundleIndex(entries)` (`:65`) | See note below — `BundleIndex` captures the reference but is constructed inline as a `JsonSerializer.Serialize` argument and never stored |
| 11 | `src/Scrinia.Core/FileMemoryStore.cs:1110` (`ListScoped(string?)`) | READ-ONLY | `foreach (var entry in LoadIndex(scope))` (`:1110`) | Enumeration into a separate `result` list built at `:1095` |
| 12 | `src/Scrinia.Core/FileMemoryStore.cs:1261` (`ListScoped(string?, string?)`) | READ-ONLY | `foreach (var entry in LoadIndex(scope))` (`:1261`) | Enumeration into a separate `result` list built at `:1247` |
| 13 | `src/Scrinia.Core/FileMemoryStore.cs:1415` | READ-ONLY | `LoadIndex(srcScope).FirstOrDefault(e => e.Name == srcSubject)` | Never bound to a local |
| 14 | `src/Scrinia.Core/FileMemoryStore.cs:1443` | READ-ONLY | `LoadIndex(srcScope).FirstOrDefault(e => e.Name == srcSubject)` | Never bound to a local |
| 15 | `src/Scrinia.Core/FileMemoryStore.cs:1559` (`GatherTopicInfos`) | READ-ONLY | `entries.Count` (`:1560`, `:1564-1565`), `entries.Select(e => e.Name).ToArray()` (`:1567`) | Read-only members and projections |
| 16 | `src/Scrinia.Core/FileMemoryStore.cs:1577` (`ListTopicArtifacts`) | READ-ONLY | `foreach (var entry in entries)` (`:1579`) | Results accumulate into a separate `result` list built at `:1576` |
| 17 | **`src/Scrinia.Core/FileMemoryStore.cs:1594`** (`ImportTopicEntries`) | **MUTATING** | **`existingEntries[idx] = updatedEntry` (`:1616`, indexer set) and `existingEntries.Add(updatedEntry)` (`:1618`)** | Then hands the same instance to `SaveIndex` at `:1621`. `FindIndex` at `:1614` and `Any` at `:1598` are read-only; `:1616`/`:1618` are what decide it |
| 18 | `src/Scrinia.Core/MaintenanceEventSink.cs:56` | READ-ONLY | `targetEntries.FirstOrDefault(e => …)` (`:57-58`) | `targetEntries` occurs at `:56` and `:57` only |
| 19 | `src/Scrinia.Core/MaintenanceEventSink.cs:83` | READ-ONLY | `sourceEntries.FirstOrDefault(e => …)` (`:84-85`) | `sourceEntries` occurs at `:83` and `:84` only. `:99` does `sourceEntry.Keywords?.ToList()` — that mutates a fresh copy of one entry's `string[]`, not the entry list |
| 20 | `src/Scrinia.Core/IMemoryStore.cs:35` (default `TryUpdateEntry`) | READ-ONLY | `LoadIndex(scope).FirstOrDefault(e => SanitizeName(e.Name) == SanitizeName(subject))` | Never bound to a local. `FileMemoryStore` overrides this member anyway (`FileMemoryStore.cs:981`) and does not call `LoadIndex` |
| 21 | `src/Scrinia/HttpMemoryStore.cs:253` | N/A | `=> [];` | An implementation returning a fresh empty list, not a caller |

**Totals: 19 READ-ONLY, 1 MUTATING, 1 not-a-caller.**

Both sites the brief said to expect were verified rather than assumed: `:1594` is MUTATING (F9
holds — the deciding expressions are the indexer set at `:1616` and `.Add` at `:1618`, not the
variable name `existingEntries`), and `:1110`/`:1261` are `foreach`, READ-ONLY.

Note on site 10 (`BundleFormatService.cs:65`): `public sealed record BundleIndex(List<ArtifactEntry> Entries);`
(`BundleFormatService.cs:10`) holds the list **by reference**. This is the one site in the table that
does hand the instance somewhere else. It is still READ-ONLY because the record is constructed as an
argument to `JsonSerializer.Serialize` on the same line, serialized, and dropped — nothing retains or
mutates it. It is worth flagging as the site nearest to the line: if `BundleIndex` ever gains a
lifetime beyond that expression, re-classify it.

---

## Verdict A — may `LoadIndex` return a live alias?

The question is not "does any caller mutate today"; one does. It is: **if `LoadIndexUnsafe` returned
`cached.Entries` and `ImportTopicEntries` were given an explicit `.ToList()`, what would break?**

### A1. Does anything read a derived structure that would desync? — **No** (F5 re-derived, and it holds)

This is the fact doing the most work, so it was re-derived from scratch rather than relied on.

`rtk grep -rn "NameToPosition" .` (whole repo, `*.cs` and `*.md`) returns, in source, exactly three
lines, all inside `CachedIndex` itself:

- `src/Scrinia.Core/FileMemoryStore.cs:66` — the declaration, `{ get; private set; }`
- `src/Scrinia.Core/FileMemoryStore.cs:74` — the constructor assignment
- `src/Scrinia.Core/FileMemoryStore.cs:79` — the assignment inside `Rebuild()`

There is **no read** of the property anywhere. The remaining hits are prose: this brief, the plan
document, and `docs/architecture/core.md:440,445`.

`rtk grep -rn "Rebuild(" . --include=*.cs` returns exactly one line — `FileMemoryStore.cs:77`, the
declaration. Zero call sites. (Widening to bare `Rebuild` adds only unrelated identifiers:
`RebuildEmbeddingPipeline`, comments, and two test method names.) So `NameToPosition` is written by a
method that is itself never called, from a constructor, and then never read.

`rtk grep -rn "GetOrComputeCorpusStats" . --include=*.cs` returns exactly one line —
`FileMemoryStore.cs:84`, the declaration. Zero call sites. `CachedIndex.Stats` (`:69`) is likewise
written only at `:80` and `:92`, both inside the two dead members, and read only at `:86` inside
`GetOrComputeCorpusStats` itself. The live BM25 path calls `Bm25Scorer.ComputeCorpusStats` directly
from `WeightedAttributeScorer.cs:80` and `:141` — it never touches `CachedIndex`.

Reflection was checked as an escape hatch: `CachedIndex` is `internal sealed`, and the only
`GetProperty`/`GetField`/`GetMethod` hits in the repo are in `archive/Nurl.Strategies/` and are
`JsonElement.GetProperty` string lookups, unrelated to this type. `Scrinia.Core` grants
`InternalsVisibleTo` to seven assemblies (`src/Scrinia.Core/Properties/AssemblyInfo.cs:3-10`), so
`CachedIndex` *is* visible to tests — but no test names it: `rtk grep -rn "CachedIndex"` over `*.cs`
returns only the eight `FileMemoryStore.cs` lines (`:29, :63, :71, :630, :903, :930, :958, :1001`),
plus one test *comment* mentioning `_indexCache`
(`tests/Scrinia.Tests/AttributesKeyMigrationTests.cs:174`).

**F5 is confirmed.** Only `Entries` is consumed, at `:593`. There is no derived structure that a
caller mutating an alias could desync — the map that would go stale is written but never read, and
the method that would repair it is never called. Had this been wrong, Verdict A would be UNSAFE,
because a mutation through the alias has no path back to `Rebuild()`.

### A2. Does anything mutate `Entries` in place after caching? — **No** (F6 re-derived, and it holds)

`rtk grep -rn "_indexCache" . --include=*.cs` returns **12 lines**, all in `FileMemoryStore.cs`
(plus the one test comment noted above):

- `:29` — the declaration, `ConcurrentDictionary<string, CachedIndex>`
- **Read (1):** `:592` — `_indexCache.TryGetValue(scope, out var cached)`
- **Writes (5), every one a whole-object replace:** `:630`, `:903`, `:930`, `:958`, `:1001` — each
  is literally `_indexCache[scope] = new CachedIndex(entries);`
- **Invalidations (6):** `:403` (`Clear()` in the migration latch), `:727`, `:928`, `:956`, `:999`
  (`TryRemove`)

`CachedIndex.Entries` is `{ get; }` with no setter (`:65`), assigned once in the constructor
(`:73` — by reference, F4 confirmed). No code path calls a mutating `List<T>` member on a
`CachedIndex.Entries` instance. The one place a cached list's contents are written is
`LoadIndexUnsafe`'s forward-fill loop, `entries[i] = upgraded` at `:623` — and that runs at `:619-627`,
**before** the cache write at `:630`, on a list no other thread can reach yet.

Critically, the write paths do **not** patch the cached list; they rebuild it. `Upsert` (`:928-930`),
`Remove` (`:956-958`) and `TryUpdateEntry` (`:999-1001`) each `TryRemove`, then
`LoadEntriesFromSidecars(...).Select(...).ToList()` into a brand-new list, then install a brand-new
`CachedIndex`. This is the structural property the whole verdict rests on: **a reader holding a
previously-returned list can never observe a later write, because writes never touch the list the
reader holds.** A copy and an alias are therefore equivalent from the reader's point of view.

**F6 is confirmed.**

### A3. The write direction (F7) — walked concretely

`SaveIndexUnsafe` caches the **caller's own list** without copying: `_indexCache[scope] = new CachedIndex(entries)`
at `:903`, where `entries` is the `List<ArtifactEntry>` parameter. So the write direction already
aliases today.

`SaveIndex` has exactly two occurrences in `src/` that are code (F8 re-checked and confirmed: the
declaration at `:880` and one call at `:1621`; the other five `SaveIndex` grep hits are comments and
`<see cref>` prose in `PathRouter.cs` and `StoreLayoutMigration.cs`). `SaveIndex` is not on
`IMemoryStore` or `ILocalMemoryStore` — it is a concrete-class-only public method.

The sole call sequence, today:

1. `ImportTopicEntries` calls `LoadIndex(topicScope)` at `:1594` → gets **copy C** (via `.ToList()` at `:593`).
2. It mutates **C** at `:1616` / `:1618`.
3. It calls `SaveIndex(C, topicScope)` at `:1621` → `WithExclusiveLock` → `SaveIndexUnsafe` → `_indexCache[topicScope] = new CachedIndex(C)` at `:903`. **The cache now holds C by reference.**
4. `ImportTopicEntries` returns immediately after `:1621`. Nothing in the method touches `C` again.
   Its only caller, `BundleFormatService.cs:258`, passes in a locally built `entries` list
   (`:164`, `:237`) and does not hold `C` at all — `C` is `existingEntries`, a local of
   `ImportTopicEntries` that goes out of scope.

So the currently-cached list for an imported topic scope is a list the caller *did* mutate, but only
strictly before it was cached. That is safe today and is not made less safe by anything here.

The sequence **with a read alias and no `.ToList()` at `:1594`** is the failure mode, and it is real:
`existingEntries` would *be* `cached.Entries`. The mutations at `:1616`/`:1618` would then modify the
live cache **outside every lock** — `LoadIndex` releases both the file lock and the in-process read
lock before returning (`:576-586`), and `ImportTopicEntries` takes no lock of its own until
`SaveIndex` at `:1621`. Any concurrent reader enumerating that same instance would throw
`InvalidOperationException`. That is exactly why the `.ToList()` at the call site is not optional —
it is the whole of condition C1 below.

With the `.ToList()` present, step 3 caches a private copy, step 4 is unchanged, and the sequence is
byte-for-byte what happens today.

### A4. Concurrency — can two threads hold the same instance while one mutates?

With A1–A3 answered: **no, provided C1 holds.**

- After C1, **no production caller mutates the returned list.** 19 of 21 sites are read-only, 1 is
  not a caller, and the 1 mutator gets its own copy.
- Concurrent read-only enumeration of a `List<T>` by multiple threads is safe. `LoadIndex` takes
  `ReaderWriterLockSlim.EnterReadLock` (`:578`), so N readers do run `LoadIndexUnsafe` concurrently
  and, on a warm cache, would all receive the same instance — but all only read it.
- Writers cannot interfere: every write goes through `WithExclusiveLock` (`:555-569`), which takes
  the write lock, so it cannot overlap a reader; and even if it could, it *replaces* the
  `CachedIndex` (A2) rather than mutating the list a reader holds. `ConcurrentDictionary` makes the
  swap itself atomic.
- Cross-process and cross-instance: `_indexCache` is an instance field (`:29`); two processes or two
  `FileMemoryStore` instances never share a `List<T>`.

One honest caveat that a later implementation task must not assume away: **on the cold path the
returned list is not the cached one.** This is unconditional, not a race:

- **In the code as it stands**, `LoadIndexUnsafe` caches `entries` at `:630` and then returns
  `entries.ToList()` at `:632` — a different instance every time, single-threaded runs included.
- **After a change that aliases the cold path too** (returning `entries` at `:632`), identity still
  fails in two ways. The cache write at `:630` is guarded by `if (entries.Count > 0)` (`:629`), so an
  **empty** cold load caches nothing and returns a list that is in no cache at all. And for non-empty
  loads, two threads may hold the read lock simultaneously, each build its own `entries` list at
  `:596`, each write `_indexCache[scope]` at `:630` (last-writer-wins), and each return its own list —
  so at most one of them holds the cached instance.

"The returned list is the cached list" is therefore a warm-path property only, under both the current
and the post-change shape. It creates no bug (nobody mutates), but any future code that relies on
reference identity between a `LoadIndex` result and the cache is relying on something that is not
true — hence condition C4.

### A5. Test doubles

`tests/` contains **141** occurrences of `LoadIndex(` (re-counted with `rtk proxy grep … | wc -l`;
the filtered `rtk grep` reported 125 because it truncates — the brief's 141 is correct).

Exactly two of those are implementations:

- `tests/Scrinia.Tests/FakeMemoryStore.cs:23` — `public virtual List<ArtifactEntry> LoadIndex(string scope = "local") => [];`
- `tests/Scrinia.Server.Tests/BundleEndpointTests.cs:352` — `public List<ArtifactEntry> LoadIndex(string scope = "local") => [];`

Both return a **fresh empty list per call**, not a field. `rtk grep -rn "override List<ArtifactEntry> LoadIndex"`
over `tests/` and `benchmarks/` finds no subclass override at all, despite `FakeMemoryStore`'s member
being `virtual` for exactly that purpose.

No test mutates a `LoadIndex` result either: grepping four lines of trailing context after every
`LoadIndex(` in `tests/` for `.Add(`/`.AddRange(`/`.Remove(`/`.RemoveAt(`/`.RemoveAll(`/`.Insert(`/
`.Clear()`/`.Sort(`/`.Reverse(` yields one hit,
`tests/Scrinia.Tests/FileMemoryStoreTests.cs:715` — `_store.Remove("removable-entry", "local:concern")`,
which is the *store's* `Remove`, not the list's. The seven `SaveIndex(` calls in `tests/` all pass
freshly built collection literals (`[entry2]`, `[]`, `[entryA, entryB]`, …), never a `LoadIndex`
result.

**Consequence for a later narrowing task: its test-side diff is zero.** No test double hands out a
backing list, and no test would change behaviour.

### Verdict A justification

Returning `cached.Entries` directly is safe *at this commit*, and only because of two properties that
nothing in the type system or the test suite enforces. The mechanics are unambiguous: the single
derived structure that an aliased mutation could desync (`NameToPosition`) is written-but-never-read
and its repair method (`Rebuild()`) has zero call sites (A1, re-derived); every cache write replaces
the whole `CachedIndex` rather than mutating an existing `Entries` list, so a reader holding a list —
copy or alias — can never observe a later write (A2, re-derived); the write direction already aliases
and the one mutating caller drops its list immediately after handing it to `SaveIndex` (A3); no test
double or test call site is affected (A5). But the safety is entirely *behavioural*: `LoadIndex`
returns `List<ArtifactEntry>`, a mutable type, on a **public interface member**
(`IMemoryStore.cs:20`), so any future caller can legally call `.Add` and would silently corrupt the
cache with no compile error and no failing test. That is a materially different risk profile from
today, where the same mistake is harmless. Hence SAFE WITH CONDITIONS, not SAFE — the conditions are
what convert a today-true fact into a maintained invariant, and C5 in particular is what moves the
guarantee from "every caller was checked" to "the type forbids it."

**Conditions (all five required before the change ships):**

- **C1.** `FileMemoryStore.cs:1594` gets an explicit `.ToList()`. Non-negotiable; without it the change
  is outright unsafe (A3).
- **C2.** `IMemoryStore.LoadIndex`'s XML doc states the returned list is a live view that callers must
  not mutate, and `LoadIndexUnsafe`'s cache-hit line carries a comment naming `NameToPosition` and
  `Rebuild()` as dead — so the next reader learns *why* it is safe, not just that it is.
- **C3.** The narrowing is pinned by a test, so it cannot be silently undone. See below — the earlier
  draft of this condition proposed a mutate-and-compare test that cannot work, and is corrected here.
- **C4.** Reference identity between a `LoadIndex` result and `_indexCache[scope]` is **not** asserted
  anywhere, because it does not hold on the cold path (A4).
- **C5.** The return type is narrowed to `IReadOnlyList<ArtifactEntry>` **in the same change** that
  introduces the alias. See below — aliasing without narrowing is the genuinely dangerous combination.

#### C5, and why it is a precondition rather than a companion

Verdict A's residual risk is that a future caller mutates the alias with no compile error. Narrowing
`LoadIndex`'s return type from `List<ArtifactEntry>` to `IReadOnlyList<ArtifactEntry>` removes that
risk at the type level: `Add`/`Insert`/`Clear`/indexer-set are simply not on the interface, so the
mistake becomes a build failure rather than silent cache corruption.

This also converts **C1 from a discipline into a compile error.** After narrowing,
`existingEntries[idx] = updatedEntry` (`:1616`) and `existingEntries.Add(updatedEntry)` (`:1618`) do
not compile, so `ImportTopicEntries` *cannot* be left without its `.ToList()`.

The consequence is an ordering constraint: shipping the alias while the return type is still
`List<ArtifactEntry>` is strictly worse than either the status quo or the fully narrowed change, and
it is the one intermediate state to avoid. Ship both together.

#### C3, restated so it is actually implementable

The earlier formulation of C3 — call `LoadIndex(scope)` twice, mutate the first result, assert the
second is unaffected — is wrong, and would fail as a test for two independent reasons. Under the
aliasing change it exists to guard, both calls return **the same live list**, so mutating the first
necessarily changes the second and the assertion fails by construction. And under C5's narrowing the
mutation does not compile at all. It described the behaviour of the code *before* the change, which
is precisely what the change removes. Recorded rather than quietly deleted, because "assert the copy
is defensive" is the obvious test to reach for here and the next person will reach for it too.

What is actually checkable against the post-change code:

- **C3a (the real guard).** A reflection test pinning the declared return type, in the idiom
  `tests/Scrinia.Tests/StoreBoundaryTests.cs` already uses for the interface surface — assert
  `typeof(IMemoryStore).GetMethod("LoadIndex")!.ReturnType` is `IReadOnlyList<ArtifactEntry>`. Under
  C5 the compiler already prevents mutation; this test prevents the *narrowing* from being widened
  back, which is the only way mutation could silently return. It fails loudly on the edit that
  matters and is immune to the cold-path identity problem in C4.
- **C3b (optional, and weaker than it looks).** A concurrency stress test — one thread enumerating
  `LoadIndex(scope)` in a loop while another runs `ImportTopicEntries` on the same scope — which
  throws `InvalidOperationException` if C1's `.ToList()` is ever removed. Worth stating explicitly
  that a *single-threaded* round-trip test does **not** catch C1's removal: without the `.ToList()`,
  `ImportTopicEntries` mutates the cached list in place and then re-caches that same list, so the
  final contents are still correct and the test passes. The hazard is purely concurrent, which is why
  C5's compile error is the dependable guard and this test is a belt-and-braces addition with the
  usual flakiness caveats.

---

## Verdict B — may the shared file lock be skipped on a cache hit?

### B1. What the lock buys at all

`FileLock.AcquireExclusive` opens with `FileAccess.ReadWrite, FileShare.None` (`FileLock.cs:32`);
`AcquireShared` with `FileAccess.Read, FileShare.Read` (`:46`). Both funnel into `Acquire`, which
opens the `.lock` file with `FileMode.OpenOrCreate` and retries on `IOException` with exponential
backoff up to a 5-second default, then throws `FileLockTimeoutException` (`:49-81`). So a shared
reader genuinely blocks while any writer — same process or another — holds the exclusive lock, and
can throw after 5s.

`AcquireShared` short-circuits to "a lock over nothing" when the lock's directory does not exist
(`:42-44`), which is what keeps reads from materialising a store (F3 confirmed;
`tests/Scrinia.Tests/LazyStoreCreationTests.cs:34-53` pins that behaviour, and it is a **miss**-path
behaviour, unaffected by anything here).

`GetLockPath` and `FileLock` have exactly two consumers in `FileMemoryStore`:
`WithExclusiveLock` (`:558`) on every write, and `LoadIndex` (`:576`) on every read.

### B2. Is there any cross-process invalidation of `_indexCache`? — **No. Confirmed.**

All six invalidation points are in-process method bodies (`:403`, `:727`, `:928`, `:956`, `:999` — A2).
There is no `FileSystemWatcher` anywhere in `src/`
(`rtk grep -rn "FileSystemWatcher" src/ --include=*.cs` → zero hits), and no mtime/generation check
guards `_indexCache`. The only `LastWriteTime` use in `src/` is `FileMemoryStore.cs:1030`, which keys
the **artifact LRU** cache (`{scope}|{subject}|{ticks}`) — a different cache, and notably one that
*does* have staleness safety, which throws the index cache's lack of it into relief.

**This is the argument that decides Verdict B.** A cache hit already returns data that may be
arbitrarily stale relative to another process's committed write, and it does so *whether or not the
lock was taken*. Blocking a cache-hit reader behind another process's exclusive lock does not make
the value it is about to return any fresher — it returns the same in-memory snapshot either way. The
lock cannot be protecting the freshness of a cache hit, because a cache hit is not fresh either way.
Today's behaviour on a contended cache hit is precisely "wait up to 5 seconds, possibly throw
`FileLockTimeoutException`, then return the same stale cached list you would have returned
immediately." That wait is pure cost.

The remaining candidate protection — torn reads of `*.meta.json` sidecars mid-write — applies only to
the **cold** path, where `LoadEntriesFromSidecars` (`:596`, `:635-659`) actually touches disk, and
where the forward-fill loop at `:619-627` writes sidecars back. That path keeps the lock. Nothing is
weakened.

### B3. Side-effect check — the migration latch

This is the one most likely to be missed, and it is what makes skipping *safe* rather than merely
cheap.

`GetLockPath` → `GetStoreDirForScope` (`:537-538`) → `_ = MemoriesRoot;` (`:416`, commented "gate").
The `MemoriesRoot` getter (`:383-412`) is the one-time v3 layout-migration latch: under
`_layoutGate`, if `StoreLayoutMigration.NeedsMigration` it migrates, **clears `_indexCache` at `:403`**,
nulls `_cachedTopics`, and sets `_layoutChecked = true`. So today, a cache-hit `LoadIndex` does
trigger that latch as a side effect of computing the lock path. If the cache-hit path stops calling
`GetLockPath`, that trigger stops firing on those calls.

**Can a cache entry exist without migration having already been triggered? No — verified for all five
population sites:**

| Cache write | `GetStoreDirForScope` call that precedes it, same method |
|---|---|
| `:630` (`LoadIndexUnsafe`) | `:595` — `string storeDir = GetStoreDirForScope(scope);` |
| `:903` (`SaveIndexUnsafe`) | `:887` — plus `WithExclusiveLock` already called it twice via `EnsureMaterialized` (`:547`) and `GetLockPath` (`:558`) |
| `:930` (`Upsert`) | `:916` — plus the same two from `WithExclusiveLock` |
| `:958` (`Remove`) | `:944` — plus the same two |
| `:1001` (`TryUpdateEntry`) | `:985` — plus the same two |

`LoadIndexUnsafe` itself has exactly one caller (`LoadIndex`, `:581` — grep confirms no other), so
there is no back door into `:630`.

Therefore **a cache hit proves the latch already fired**: the entry could only have been written by a
method that first evaluated `MemoriesRoot`, which sets `_layoutChecked = true` before returning. Both
are fields of the same instance, so the implication is instance-local and total. The ordering is also
airtight in the other direction — `_indexCache.Clear()` at `:403` runs while `_layoutChecked` is still
`false` and `_layoutGate` is held, and every concurrent first-toucher blocks on that gate
(`:389-408`), so no thread can populate the cache before the clear and then survive it.

Skipping the lock on a cache hit therefore cannot skip a migration trigger that would otherwise have
fired. On a **miss** the lock is still taken, `GetLockPath` still runs, and the latch still fires — so
the very first `LoadIndex` on any scope, and every `LoadIndex` after any invalidation, behaves exactly
as today.

Two smaller side effects also disappear on cache hits, both checked and both benign:

- **`.lock` file creation.** When the scope directory exists, `Acquire` opens `.lock` with
  `FileMode.OpenOrCreate` (`FileLock.cs:62`), so today a cache-hit read *creates* the file if absent.
  Nothing depends on that: there is no `".lock"` string literal in `src/` or `tests/` outside
  `GetLockPath`/`GetVectorLockPath`, `FileLockLazyTests` and `FileLockTests` exercise `FileLock`
  directly rather than through `LoadIndex`, and the generated `.gitignore` covers `**/*.lock`
  regardless (`LazyStoreCreationTests.cs:86`). This only ever *removes* a file creation, never adds
  one, so the "reads do not materialize" invariant is strengthened, not weakened.
- **`FileLockTimeoutException` on a contended cache hit.** No test asserts it: all four
  `Throw<FileLockTimeoutException>` assertions are in `FileLockTests.cs` (`:32, :54, :64, :112`)
  against `FileLock` directly. Removing this failure mode from warm reads is a behaviour change and
  should be stated as such, but it removes a spurious throw, not a real guarantee.

Note also that `GetStoreDirForScope` throws for scopes with no on-disk store — `PathRouter.ToStoreDir`
throws `ArgumentException` for anything that is not `local` or `local:*` (`PathRouter.cs:154-165`), so
`LoadIndex("ephemeral")` throws today. That behaviour is preserved: `"ephemeral"` can never be in
`_indexCache` (populating it would require the very call that throws), so it always takes the miss
path and always throws exactly as now.

### B4. What the change must look like

`LoadIndex` holds **two** locks today: the cross-process `FileLock.AcquireShared` (`:576`) and the
in-process `ReaderWriterLockSlim` read lock (`:578`). Verdict B is about the first one only. They are
separable, and the distinction decides the implementation shape, so all three candidates are
evaluated here rather than only the endpoints.

| Shape | Fast-path cache check sits… | File lock on hit | In-process read lock on hit | Verdict |
|---|---|---|---|---|
| S0 | inside `LoadIndexUnsafe`, under both locks | taken | taken | status quo |
| **S1** | **in `LoadIndex`, under `EnterReadLock` only** | **skipped** | **taken** | **recommended** |
| S2 | in `LoadIndex`, above both locks | skipped | skipped | rejected — see below |

**S1 is the right shape**, and it is what this analysis recommends. `LoadIndex` acquires only the
in-process read lock, checks `_indexCache.TryGetValue`, and returns on a hit; on a miss it releases,
then takes `AcquireShared` + `EnterReadLock` + `LoadIndexUnsafe` exactly as today. `LoadIndexUnsafe`
must keep its own cache check as the second, under-lock check, so a miss that races a concurrent
population still short-circuits rather than re-reading disk.

S1 delivers everything Verdict B claims. The costs being removed on a warm read are a
`GetStoreDirForScope` call, a `Directory.CreateDirectory`, a file handle open and close, and the
possibility of blocking or throwing `FileLockTimeoutException` behind another process's writer. The
`EnterReadLock`/`ExitReadLock` pair that S1 keeps is uncontended in-process work of a few nanoseconds
— it is not the cost anyone is trying to remove.

**S2 is rejected, and the reason is not the one B2 covers.** Dropping the in-process read lock as
well would introduce a *new* failure mode that the current code closes. Every write runs under
`WithExclusiveLock` (`:555-569`), which takes `EnterWriteLock` at `:560` and performs both the sidecar
write and the cache replacement inside it. Because a reader takes `EnterReadLock` at `:578`, a
same-process reader on another thread **blocks until that write completes and therefore observes the
post-write cache**. Under S2 the reader's lock-free `TryGetValue` can run mid-write and return the
*pre*-write `CachedIndex`.

That is a genuine regression, and it must not be waved through by pointing at B2. B2's staleness is
**cross-process, pre-existing, and unavoidable** — no lock discipline inside this process can fix it.
S2's staleness would be **same-process, cross-thread, and newly introduced** by the change, in a
window the `ReaderWriterLockSlim` currently closes. The two are not the same thing, and equating them
(as an earlier draft of this section did) is the kind of reasoning that makes a verdict look
pre-decided. It also matters in practice: the MCP tools and `MemoryOrchestrator` do read-then-write
sequences, and the event sinks (`MaintenanceEventSink`, `EntityTypingEventHandler`) run off the back
of a write while other work proceeds — exactly the concurrent-reader shape that would observe the
stale snapshot.

Since S1 costs essentially nothing and preserves that guarantee, there is no reason to pay for S2.

**This does not weaken Verdict B.** The file lock buys nothing on a cache hit under either shape;
what S1-versus-S2 decides is only how much *else* gets dropped along with it. Verdict B stands as
SAFE TO SKIP, and S1 is how to skip it.

### Verdict B justification

On a cache hit the shared file lock protects nothing that survives scrutiny. The only two candidate
protections are freshness and torn sidecar reads. Freshness is not on offer: there is no
cross-process invalidation of `_indexCache` at all — no watcher, no mtime or generation token, only
five in-process invalidation points (B2) — so a warm read returns a snapshot that may be arbitrarily
stale with respect to another process's committed write, lock or no lock. All the lock adds is a
wait, and in the worst case a `FileLockTimeoutException`, before returning that same stale value.
Torn sidecar reads are real but strictly cold-path, where `LoadEntriesFromSidecars` and the
forward-fill write-back actually touch disk, and that path keeps the lock unchanged. The one
non-obvious risk — that `GetLockPath` doubles as the trigger for the v3 layout-migration latch via
`GetStoreDirForScope` → `MemoriesRoot` — is closed by direct verification that all five `_indexCache`
population sites are reached through a `GetStoreDirForScope` call, so the existence of a cache entry
is itself proof the latch already fired; a miss still takes the lock and still fires it. The residual
changes are the loss of a `.lock` file creation on warm reads (nothing depends on it, and it only ever
removes a write) and the loss of a spurious timeout throw on contended warm reads. The verdict applies
to the **file** lock alone: the in-process `ReaderWriterLockSlim` read lock must stay on the fast path
(shape S1 in B4), because it closes a same-process read-your-writes window that dropping it would
reopen — a separate question from the one Verdict B answers, and one whose answer is different.

---

## What would change the answer

**Verdict A flips to UNSAFE if any of these lands:**

- **Any reader of `CachedIndex.NameToPosition`** (`FileMemoryStore.cs:66`), or **any call to
  `CachedIndex.Rebuild()`** (`:77`). This is the single load-bearing fact. A live name→position map
  alongside an aliased list desyncs the moment anyone mutates through the alias, and with `Rebuild()`
  uncalled there is nothing to repair it. Reviving either member and aliasing the list are mutually
  exclusive; a change that does both must be rejected. The same applies to
  `GetOrComputeCorpusStats()` (`:84`) and `Stats` (`:69`), whose values are `Entries`-derived.
- **Any cache write that mutates an existing `CachedIndex.Entries` in place** rather than installing a
  new `CachedIndex`. The invariant is that all five writes (`:630, :903, :930, :958, :1001`) are
  whole-object replaces. A future "incremental cache update" optimisation — patching `Entries` on
  `Upsert` instead of rebuilding from sidecars, which is precisely what a reader of the stale
  `core.md:445` claim would be tempted to write — breaks it directly.
- **Any new MUTATING call site**, i.e. any caller that calls `Add`/`AddRange`/`Remove`/`RemoveAt`/
  `RemoveAll`/`Insert`/`Clear`/`Sort`/`Reverse` or an indexer set on a `LoadIndex` result, or passes
  it to something that outlives the expression. The table above is the baseline. Under C5 this is a
  compile error; C3a is what stops C5 being undone.
- **Widening `LoadIndex`'s return type back to `List<ArtifactEntry>`** after C5 narrows it. This is
  the single edit that would silently restore the whole risk class, which is why C3a pins it.
- **Removal of the `.ToList()` at `FileMemoryStore.cs:1594`** (condition C1) — the same failure,
  reintroduced at the one site that has always had it coming. Under C5 this does not compile; without
  C5 it is silent, which is why C5 is a precondition and not a companion.
- **`Scrinia.Core` becoming a published library.** Verdict A's exhaustiveness rests entirely on a
  repo-wide grep having found *every* caller — a premise that holds only while every consumer is in
  this repo. `src/Scrinia.Core/Scrinia.Core.csproj` carries `PackageLicenseExpression` (`:8`) but no
  `PackageId`, `IsPackable` or `GeneratePackageOnBuild`, and no `src/` project sets them, so nothing
  is packed today and the premise holds. Adding any of them would invalidate the call-site table
  wholesale: external callers compiled against `List<ArtifactEntry>` could mutate the alias, and no
  grep here would ever see them. C5's narrowing is what makes this survivable, since it moves the
  guarantee from "we checked every caller" to "the type forbids it" — but a packing change should
  still force a re-read of this section.
- **`BundleIndex` (`BundleFormatService.cs:10`) gaining a lifetime** beyond the inline
  `JsonSerializer.Serialize` argument at `:65`. It captures the list by reference; today that is
  harmless only because the instance is discarded within the expression.
- **A test double that returns a mutable field** from `LoadIndex` and then mutates it (A5). Not a
  production-correctness problem, but it would break the "zero test-side diff" property.

**Verdict B flips to UNSAFE TO SKIP if any of these lands:**

- **Any cross-process invalidation of `_indexCache`** — a `FileSystemWatcher`, an mtime or generation
  token on the store directory, a lock-file epoch, anything that lets one process's write invalidate
  another's cache. That would make a cache hit's freshness depend on serialising against writers, and
  the shared lock would start doing real work.
- **Any new population site for `_indexCache`** that is not reached through a `GetStoreDirForScope`
  call. The table in B3 is the exhaustive list at this commit, and the "cache entry ⇒ latch already
  fired" implication is only as strong as that table. A site that installs a `CachedIndex` from, say,
  a deserialized on-disk cache without touching `MemoriesRoot` would break it outright.
- **`MemoriesRoot` (`:383-412`) gaining a repeatable side effect**, or `_layoutChecked` becoming
  non-sticky (e.g. reset on some event). The argument depends on the latch being one-shot per
  instance.
- **Anything starting to depend on the `.lock` file existing after a read** — an external tool, an
  operational script, a lock-inventory feature.
- **Any caller relying on `LoadIndex` blocking behind another process's writer**, or on
  `FileLockTimeoutException` from a warm read.
- **`LoadIndexUnsafe` gaining a second caller** that does not hold the read lock. It has exactly one
  today (`:581`), which is what makes "the cache check is the first statement" a complete description
  of the hit path.
- **The fast-path cache check being hoisted above `EnterReadLock` (shape S2 in B4).** This does not
  flip Verdict B — the file lock still buys nothing — but it silently trades the verdict's benefit for
  a same-process read-your-writes regression that the current `ReaderWriterLockSlim` closes. If a
  future change removes the in-process read lock from the fast path, that is a separate decision
  needing its own justification, and it cannot borrow this document's.
- **Writes ceasing to replace the `CachedIndex` under `EnterWriteLock`.** S1's correctness on a hit
  depends on the reader either seeing the whole old `CachedIndex` or the whole new one. That holds
  because every write runs inside `WithExclusiveLock` (`:555-569`) and installs a new object (A2). An
  in-place cache update would break Verdict A and this at the same time.

---

## Findings recorded, not acted on

Out of this task's scope (it produces no code), recorded so they are not lost:

1. **`docs/architecture/core.md:445` is false on two counts.** It claims "`NameToPosition` enables O(1)
   upsert and remove without scanning." `NameToPosition` has no readers (A1), and `Upsert`/`Remove`
   do not consult the cache at all — they call `LoadEntriesFromSidecars` and rebuild
   (`FileMemoryStore.cs:928-930`, `:956-958`; F10). The doc block at `:437-443` also renders
   `NameToPosition` as `{ get; }`, whereas the source is `{ get; private set; }` (`:66`).
2. **`docs/architecture/core.md:220` is false.** "Corpus statistics ... are lazily computed and cached
   per scope in `CachedIndex`" — `GetOrComputeCorpusStats()` has zero call sites; the live BM25 path
   calls `Bm25Scorer.ComputeCorpusStats` directly from `WeightedAttributeScorer.cs:80` and `:141`, with
   no caching. This is the more misleading of the two, because it describes a performance property the
   code does not have.
3. **Three dead members in `CachedIndex`:** `NameToPosition` (`:66`), `Rebuild()` (`:77`),
   `GetOrComputeCorpusStats()` (`:84`), plus `Stats` (`:69`) which is only reachable through the
   latter. `BuildNameMap` (`:96`) exists solely to feed the dead map, and its cost is paid on **every**
   cache write — five sites, once per `Upsert`/`Remove`/`TryUpdateEntry`/`SaveIndex`/cold load — to
   build a dictionary nobody reads. Deleting them would make Verdict A's A1 structurally true rather
   than merely observed, which is a stronger guarantee than condition C2's comment. Note the ordering
   constraint: this deletion and any revival of the members are mutually exclusive, and the deletion
   should land *before or with* the aliasing change, not after.
4. **`ListScoped` can never alias the cache** regardless of the aliasing decision: both overloads build
   a fresh `result` list (`:1095`, `:1247`) and copy `ScopedArtifact` wrappers into it (`:1111`,
   `:1262`). So `SearchPipeline`'s `Candidates` (F12) can never hold `_indexCache`'s backing list. This
   was not required for either verdict but closes the one route by which a `LoadIndex` result could
   otherwise escape into a longer-lived object.
5. **`.ToList()` at `:593` is a shallow copy and always has been.** The `ArtifactEntry` instances are
   shared between the cache and every caller today. `ArtifactEntry.Keywords` is a `string[]` and
   `Attributes` a dictionary, both mutable in principle. Callers observed here are well-behaved —
   `MaintenanceEventSink.cs:99` does `sourceEntry.Keywords?.ToList()` before mutating, and every
   update path uses `with` expressions — but the current defensive copy protects only list *structure*,
   never element state. Aliasing does not widen this exposure by one byte; it is worth naming because
   "we copy the list" reads as a stronger guarantee than it is.
6. **`rtk grep` silently truncates.** It reported 125 `LoadIndex(` occurrences in `tests/` where
   `rtk proxy grep … | wc -l` reports 141 (the brief's figure, and the correct one). Any count derived
   from `rtk grep` should be re-derived with `rtk proxy` before it is used as evidence.

## Measurement

Harness: `tests/Scrinia.Benchmarks/LoadIndexBenchmarks.cs`, BenchmarkDotNet 0.15.4, `-c Release`.

```
BenchmarkDotNet v0.15.4, Windows 11 (10.0.26300.9032)
AMD Ryzen 5 9600X 3.90GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.400-preview.0.26322.102
  [Host]     : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v4
```

"Before" = `src/Scrinia.Core/FileMemoryStore.cs` checked out at `f8a316c` (pre-Task-2, lock
acquired before the cache check on every `LoadIndex` call). "After" = the same file at `cc7b3b4`
(current `HEAD`, cache checked first; the `.lock` file handle is only opened on a cache miss).

| Entries | Before (lock first) | After (cache first) | Delta | Alloc before | Alloc after |
|---|---|---|---|---|---|
| 100 | 164.90 us (±3.26 us 99.9% CI, N=28) | 76.99 ns (±1.542 ns 99.9% CI, N=28) | −164.82 us (~2142× faster) | 2.23 KB | 856 B |
| 1000 | 169.40 us (±3.36 us 99.9% CI, N=41) | 447.98 ns (±8.817 ns 99.9% CI, N=24) | −168.95 us (~378× faster) | 9.27 KB | 8056 B (7.87 KB) |
| 5000 | 170.93 us (±3.28 us 99.9% CI, N=19) | 1.817 us (±0.034 us 99.9% CI, N=15) | −169.11 us (~94× faster) | 40.52 KB | 40056 B (39.12 KB) |

**Reading:** This is not in the noise — it is several orders of magnitude beyond any margin of
error at all three sizes, in the expected direction (before slower, after faster). The "before"
mean sits in a tight 164.9–170.9 us band that barely moves with entry count, which is exactly the
shape you'd expect from a roughly constant per-call cost (opening and closing a `.lock` file
handle) dominating a cheap, size-dependent cache read. The "after" mean, with the lock removed
from the cache-hit path, scales with entry count instead (77 ns → 448 ns → 1.82 us across
100/1000/5000), consistent with the remaining cost being the defensive `ToList()` copy over the
cached list — the thing this benchmark deliberately still measures. Allocation drops at the low
end (856 B vs 2.23 KB at 100 entries) and converges toward parity at 5000 entries, where the
`ToList()` copy itself dominates the allocation and the lock's own allocations become a rounding
error by comparison.

One note on expectations: the brief's "Context for the sizes below" section cites prior figures
of ~0.87–1.14 ms (70 entries) and ~2.16–2.45 ms (272 entries) as the scale to expect. Those prior
numbers came from a different, heavier benchmark — a fuller read path including scoring, not an
isolated warm `LoadIndex` call — so the lock's cost was diluted among other work there. Here the
lock is the only thing separating "before" from "after," so its cost is not diluted and it
dominates the whole measurement; a >2000× difference at 100 entries is the expected consequence of
isolating it, not a contradiction of the earlier numbers. As stated up top, the change stands on
the syscall being unnecessary, not on this table — but the table backs that claim up unambiguously.
