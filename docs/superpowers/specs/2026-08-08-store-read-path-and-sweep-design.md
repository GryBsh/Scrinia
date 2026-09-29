# Store Read Path and Sweep — Design

**Status:** design, awaiting review
**Sequence:** residuals spec 4 of 4. Inventory: `2026-08-08-residuals-triage.md`
(T1.5, T3.1, T3.2, T4.4, T5.1–T5.5, T5.9–T5.11, T6.1, T6.3).

## Goal

Two halves, deliberately kept in one document so the split decision is visible rather than
assumed:

- **Part A — the `FileMemoryStore` read path.** Three items about `LoadIndex` and
  `SearchQuery.Candidates`. Needs an analysis nobody has done, then a measurement.
- **Part B — the sweep.** Test-coverage gaps, one formatting defect, and two docs corrections
  inherited across four branches.

**These could reasonably be two branches.** Part A is design work with a measurement gate; Part
B is mechanical and could be done by anyone in an afternoon. They are together here because
Part B is too small to justify its own spec and too scattered to attach to any of the other
three. If Part A's analysis says "don't", Part B still stands alone.

---

# Part A — the read path

## Established facts

| # | Fact | Evidence |
|---|---|---|
| F1 | `LoadIndex` calls `FileLock.AcquireShared(GetLockPath(scope))` unconditionally, before any cache consultation | `src/Scrinia.Core/FileMemoryStore.cs:576` |
| F2 | The in-memory cache is checked inside `LoadIndexUnsafe`, at `:592-593`, and returns `cached.Entries.ToList()` — a defensive copy | `src/Scrinia.Core/FileMemoryStore.cs:589-593` |
| F3 | `LoadIndex` returns `List<ArtifactEntry>` | `src/Scrinia.Core/FileMemoryStore.cs:574` |
| F4 | `LoadIndex(` appears 23 times across 9 `src/` files and 141 times across `tests/` | repo-wide search at `c3773bf` |
| F5 | Exactly one production site mutates the returned list: `FileMemoryStore.ImportTopicEntries` (`existingEntries[idx] = …` / `.Add(…)` before `SaveIndex`) | `src/Scrinia.Core/FileMemoryStore.cs:1603-1630`, per the search-contract residual — **re-verify, lines may have moved** |
| F6 | `Upsert` and `Remove` do not call `LoadIndex` at all — they write sidecars directly and invalidate the cache | per the search-contract residual — **re-verify** |
| F7 | `FileMemoryStore` resolves candidates as `query.Candidates ?? ListScoped(query.Scopes, query.ExcludeTopics)` | `src/Scrinia.Core/FileMemoryStore.cs:1283-1284` |
| F8 | `SearchPipeline.ExecuteAsync` is the only production setter of `Candidates` | per the search-contract residual — **re-verify** |

F5, F6 and F8 come from the search-contract branch's own correction of an earlier wrong claim.
That branch originally recorded "roughly ten mutating callers", asserted from a variable name
rather than traced, and reversed the conclusion once traced. **Re-trace all three.** The
history of this particular fact is the reason this spec exists rather than the change simply
having been made.

## The blocking question

The search-contract residual is explicit that this item was deferred, **not rejected**, and
routed here by name: *"EVALUATE THIS PROPERLY IN THE RESIDUALS SPEC — do not skip it as
already-rejected."*

What blocks it is a single unanswered question:

> Is it safe to hand 19-odd read-only callers a live alias into `_indexCache`'s backing list,
> given that cache's invalidation strategy?

Nobody has answered it. **Task 1 of this branch is that analysis and nothing else.** Its
outcome gates §1 and §2 below. If the answer is "not safe", the correct result of this branch
is a written finding and no code change — and that is a success, not a failure.

## 1. Narrow `LoadIndex` to `IReadOnlyList<ArtifactEntry>`

Conditional on the analysis. On F5/F6's numbers the trade is favourable: one explicit
`.ToList()` at the single mutating site, against 19 callers who stop paying a defensive copy
per call.

Note what the narrowing does *not* buy on its own — see §2. The copy is not the expensive part.

## 2. Move the lock behind the cache check

Per F1/F2, every `LoadIndex` call opens an OS file handle even when the in-memory cache would
have satisfied it with no I/O at all. That is a fixed per-call cost on a hot path used
throughout the store, not only by search.

This is the item with the clearest standing justification: this project's stated preference is
against repeated small filesystem operations on hot paths, on wear grounds as much as latency
grounds. Latency is not the argument here — the argument is that the syscall is unnecessary.

**Measure it.** Do not reason about it from the code shape. The search-contract branch's own
numbers (~0.87–0.91ms vs ~1.09–1.14ms at 70 memories; 2.16 vs 2.45ms at 272) came from a
LoCoMo-seeded corpus topping out at 272 memories in one scope, and that branch explicitly
recorded that the scaling behaviour beyond that point is extrapolated, not measured. So:

- Re-measure at a corpus size representative of real use, not the bench corpus.
- Report the number, including if it is uninteresting. A finding of "this costs nothing
  measurable" is a legitimate outcome and should be recorded rather than quietly dropped.
- Justify the change by the syscall being unnecessary, not by a benchmark delta. The benchmark
  is a check, not the reason.

Correctness first: establish what the lock is protecting against before removing it from the
cache-hit path. A cache hit that races a concurrent sidecar write must still return a coherent
answer. If the lock is load-bearing for that, the fix is not "skip it" — it is a different fix.

## 3. `SearchQuery.Candidates` becomes a hint, not a contract

Per F7, a caller supplying a differently-scoped candidate list silently gets different results.
`Candidates` can currently change the *answer*, not just the *cost* — which makes it a
correctness contract wearing a performance-hint's name.

Keep applying the scope and `ExcludeTopics` filter to the supplied list: a `HashSet.Contains`
per candidate, orders of magnitude cheaper than the re-listing it replaces, and it makes
`Candidates` unable to change the answer at all.

Worth doing now, per F8, because there is exactly one production setter. The benchmark
retrievers already materialize candidates and are the obvious next setters — after which this
becomes a change to established behaviour rather than a hardening.

---

# Part B — the sweep

## Test coverage

| # | Gap | Evidence | Fix |
|---|---|---|---|
| B1 | `ChatProviderHttpTests` never exercises the transient → `RecordFailure` branch; its only non-success case is HTTP 400, which `TransientDetector` does not classify as transient | only `OK` at `:27`, `BadRequest` at `:44` | Add a 503 case with `MaxRetries: 0` so the test doesn't sit through backoff, and assert the breaker recorded the failure |
| B2 | `TryToFileEntityPath_RejectsUnsafeNames` has no rooted-path case despite `FileEntityName.IsValid` checking `Path.IsPathRooted` | InlineData at `PathRouterTests.cs:199-203` | Add `C:\foo` and a POSIX-rooted case |
| B3 | `StorePurityInvariantTests` globs only `*.md` while its own doc states the invariant as "no .md **and** no foreign .meta.json" | `:28` | Needs a known-names filter — a real judgement call, not an oversight |
| B4 | `Migration_DoesNotSweepTheAgentDirectory` discards `Migrate()`'s result, so it passes vacuously if migration ever short-circuits | `AgentFileEntityTests.cs:63` | Assert `Ran` |
| B5 | `BundleImportOfAnAgentTopic_…` calls `store.SaveIndex` directly rather than performing a bundle round trip — tighter than its name promises | `AgentFileEntityTests.cs:89` | Either widen the test or rename it to what it tests |
| B6 | No golden covers a `warning` status or a `followUp`/truncation section; the `compact` golden never exercises the multi-chunk merge branch | 16 fixtures under `tests/Scrinia.Tests/Golden/__snapshots__/` | Add fixtures |
| B7 | The remote `remember()` file-refs mutation test only discriminates a CWD-shaped fabricated workspace root — a regression falling back to a hardcoded path or `AppContext.BaseDirectory` would leave the test spuriously green | as recorded on store-boundaries | Widen the mutation, or record the limit in the test's own doc |
| B8 | `Scrinia.Server.Tests` carries a duplicate set of test doubles because `Scrinia.Tests`' `FakeMemoryStore` is internal to another assembly | as recorded | A shared test-doubles assembly, or `InternalsVisibleTo` |

B4 and B7 are the two worth ranking above the rest: both are tests that **cannot fail** under
the regression they exist to catch, which is worse than no test, because the suite reports
coverage that is not there.

B6 also has a live question attached: whether the existing `link.yaml` golden covers `link`'s
*partial-failure* envelope or only its success case. The triage could not settle it. Check
before adding a fixture.

## One formatting defect

`MemoryList`'s builders use `AppendLine` (`\r\n` on Windows) while `ToYaml` splits on `\n`
only, leaving a stray `\r` in rendered output. Twenty `AppendLine` calls in
`MemoryTools.Core.cs`.

This is currently invisible because `GoldenNormalizer` strips every `\r` unconditionally — a
deliberate, already-commented trade-off that is precisely why no golden detects the
malformation. Fixing the formatter moves every affected golden, which is why it was held back;
it is a mechanical but wide diff and should be its own commit.

## Two docs corrections

- **CHANGELOG.** The `[Unreleased]` block carries three sections on where agent configs live,
  written at three points in that directory's history, and `:368-369` — "`skill` (entity) and
  `agent` scopes are unchanged" — flatly contradicts `:213-230` and `:281-301`. This one has
  survived four branches, partly because the sentence wraps across two lines and single-line
  greps miss it. Sweep the whole block, do not fix the one line.
- **Stale comment.** `BundleZipSlipTests.cs:37` cites the deleted `GetScriniaBaseDir` and
  describes a mechanism that no longer gates the test — `../outside` is now rejected by the
  `CategoryToKind` allowlist before any path resolution. The test passes on a *stronger* gate
  than its comment describes, which is the kind of comment that misleads a future reader into
  weakening the real gate.

## Out of scope

- The three process items (plan code-block comments as production comments; the SDD ledger's
  missing branch-close section; `rtk err` not being a build gate). They are workflow changes,
  not code, and belong wherever this project keeps its process notes.
- Everything in the triage's "deliberate no-action" list. Each is recorded there with the
  reason it must not be "fixed" back.

## Risks

**Part A §2 touches locking.** The only item in any of these four specs where a wrong fix
produces a data race rather than a visible failure. It gets the analysis-first ordering and the
correctness-before-measurement discipline above for that reason.

**Part B's formatting fix moves every golden.** Large diff, low risk, but it must not be
bundled with anything else — a golden-wide diff hides whatever else is in the commit.
