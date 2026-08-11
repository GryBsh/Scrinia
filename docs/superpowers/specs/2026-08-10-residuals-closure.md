# Residuals Closure — the backlog stops rotating here

**Status:** inventory, awaiting scope decision.
**Supersedes as the working list:** `2026-08-08-residuals-triage.md` (the 35-item inventory that
started this sequence).

## Why this document exists

The four-spec sequence cut from the original triage was itself the deferred-items stage. Three of
its four branches have merged — `surface-defects`, `store-read-path` (Part A),
`remote-write-correctness` — and each closed real items **and wrote new ones**. Twenty-one new
deferred chunks across three `/findings/*-deferred-followups` memories, against a backlog it was
supposed to be draining.

That is a backlog rotating, not closing. This document is the consolidated list, and its purpose
is that **every item leaves it by being fixed, decided, or ruled closed in writing** — not by
being carried to a fourth generation.

**The "ruled closed" bucket is the important one.** Several items have been deliberately left
three times running, each time with sound reasoning. An item that keeps earning a good reason not
to fix it is not backlog; it is a decision nobody has written down. Writing the ruling and
deleting the entry is closure.

## How to read the state column

- **OPEN** — verified present at `d6fd830`, needs work.
- **SPECCED** — a design already exists; needs a plan and a branch.
- **DECIDE** — cannot be closed by an implementer; needs a call first.
- **RULE** — should be closed by writing down the standing reason, not by code.

---

## Group A — CLOSED on `merge-driver-residuals`, 2026-08-11.

All 8 items fixed. `ConflictDir` values that resolve to the artifact directory itself
(`"."`, `""`, `"./"`, `".//"`, or a bare trailing separator) are now **rejected** rather than
accepted, closing the hole where the driver wrote a `.gitignore` of `*` into the artifact
directory itself. Git does not retroactively untrack files it already tracks, so this did not
untrack existing memories — but it clobbered any `.gitignore` the user already had there, and
every memory written into the directory afterwards became invisible to `git status`/`git add`
and was silently lost on a fresh clone. The legacy four-argument
invocation branch moved inside the top-level try (closing an unguarded-exception gap) and no
longer describes an unregistered driver key with failure language. The `"conflicted": true`
sidecar flag that nothing ever read was deleted. A `merge.config` round-trip test now pins the
contract through the **real writer** and the **real reader** — the exact divergence that was the
camelCase bug — and also records that the reader's `ResolverCommand` property is untestable from
the writer side today, since the writer has no parameter for it.

See commits `4ff61d0`, `ddfdf0c`, `715ae37`, `d49521c`, `783f8a6`, `384166a`, `fcec93e`,
`7745c22`, `af84907`, `ccd247f` on `merge-driver-residuals`, and the plan at
`docs/superpowers/plans/2026-08-11-merge-driver-residuals.md`.

## Group B — CLOSED on `the-sweep`, 2026-08-11.

Design: `2026-08-08-store-read-path-and-sweep-design.md`, **Part B**.

All items closed: the two cannot-fail tests were repaired
(`Migration_DoesNotSweepTheAgentDirectory` now captures and asserts `Migrate()`'s `Result.Ran`;
the remote file-refs mutation test now plants its probe at both `CWD` and
`AppContext.BaseDirectory` so a fabricated root can't hide from it), plus the bundle-import
agent-metadata test (B5) widened into a real export/import round trip and the lock-file
precondition test (G5) now asserts the lock file existed before deleting it. B1 (transient 503 →
breaker), G1 (`MaxValuesPerKey` cap), G2 (`ListScoped` non-2xx throw), and G3 (error-body text in
the exception message) all got new tests. The `StorePurityInvariantTests` now assert the
invariant its own doc states, via a cold-load second store. Four golden fixtures were added
(`update-warning`, `recall-truncated`, `compact-multichunk`, `link-partial-failure`), taking the
snapshot count from 14 to 18. The `MemoryTools.Core.cs` `AppendLine`/`\r` formatting defect is
fixed — all 20 `AppendLine` calls in `BuildSummary`/`BuildFullList` now use
`Append(...).Append('\n')` — the only production change in this branch. `Scrinia.TestDoubles` now
holds one shared set of memory-store doubles instead of a duplicated fake store.

**Two spec corrections this branch found, both load-bearing enough to record:**
- **B6's fixture count was 14, not 16.** The design spec's inventory overcounted the existing
  `tests/Scrinia.Tests/Golden/__snapshots__/` fixtures.
- **F1's blast radius was 2 goldens, not "every golden."** The design spec's Risks section
  claimed the `AppendLine`/`\r` fix would move every golden fixture. It moves none:
  `GoldenNormalizer` strips `\r` unconditionally before persisting, so committed fixtures
  structurally cannot show the defect — confirmed by regenerating `list-summary.yaml` and
  `list-full.yaml` (the only two fixtures downstream of the fixed builders) and getting a
  byte-identical diff.

**B2 was redefined, not implemented as specced.** The proposed test (rooted-path names rejected
by `FileEntityName.IsValid`) could never fail: `IsValid` short-circuits on
`IndexOfAny(['/','\\'])` before reaching `!Path.IsPathRooted(name)`, and no input can be rooted
while passing the sibling checks — verified by running `Path.IsPathRooted` against
representative inputs, not by inspection alone. B2 now adds the reachable cases and records the
unreachability as a ruling in `FileEntityName.cs` (`IsValid`'s doc comment), the same defect
class the two cannot-fail tests above exist to remove.

**G4 was ruled closed in writing, not pinned by a test.** The fast-path `EnterReadLock` in
`FileMemoryStore.LoadIndex` got a real 300-round stress test; it never failed with or without the
lock, so the lock is unpinnable by a black-box test. The ruling is in
`src/Scrinia.Core/FileMemoryStore.cs` immediately above the lock (`// RULING (the-sweep, Task 1,
G4)`), explaining what the lock protects and why nothing here can pin it.

See commits `20a14dd`, `4bd2436`, `3ae44b6`, `7eab8d9`, `2ebd54a`, `32bc5fe`, `692cae0`, `767dc7a`,
`f0184da` on `the-sweep`, and the plan at `docs/superpowers/plans/2026-08-11-the-sweep.md`.

## Group C — remote-mode leftovers (OPEN, 4 items)

Left by `remote-write-correctness`. Small, and all in one blast radius.

| Item | Evidence |
|---|---|
| `IMemoryStore`'s **default** `TryUpdateEntry` still matches names with an ordinal `==` — the identical bug just fixed in `FileMemoryStore` | `src/Scrinia.Core/IMemoryStore.cs:41`, verified at `d6fd830` |
| `EntityTypingEventHandler` has its **own** case-sensitive lookup upstream of its `TryUpdateEntry` call, which still gates mixed-case names out early | pre-dates the branch |
| The "Security headers" middleware sets headers by direct assignment and has the identical `Response.Clear()` wipe exposure Task 7 fixed for the version header — headers absent on a genuine 500 | `src/Scrinia.Server/Program.cs`; the proven one-liner (`Response.OnStarting`) is a few lines below it |
| `Keywords` is never populated by the `LoadIndex` proxy because `ListApiItem` has no such field, so `MaintenanceEventSink` always sees null remotely | same gap `ListScoped` already had |

## Group D — CLOSED on `closure-hygiene-rulings`, 2026-08-10.

All five items fixed, plus the stray test citation: the `cmds.txt` ignore rule was added, the
unnecessary `using System.Linq;` was removed, `FileLock.AcquireShared`'s redundant
`Directory.CreateDirectory` was cut, `CachedIndex`'s unread members (`BuildNameMap`,
`NameToPosition`, `Rebuild()`, `GetOrComputeCorpusStats()`) were deleted, and the live references
to the deleted `ScriniaArtifactStore` in `docs/architecture/cli.md` and `AGENTS.md` were corrected.
See commits `d703150`, `a9323fb`, `8b32bbe`, `230ac93` on `closure-hygiene-rulings`, and the plan
at `docs/superpowers/plans/2026-08-10-closure-hygiene-rulings.md`.

## Group E — DECIDED 2026-08-10. All five ruled; none documented away.

The user took the maximal option on every one. Nothing here closes with a written limitation;
all five become work. **E2–E5 are design efforts, not fixes** — they need specs before plans.

**E1 — CLOSED on `closure-hygiene-rulings`, 2026-08-10.** Fixed, not just ruled: the warning
envelope now prints yellow, not green; `scri bundle import --topics doesnotexist` exits non-zero;
`ImportBundle` returns `422` instead of `200` when `topicCount == 0`. Breaking change recorded in
CHANGELOG.md. See commits `133d9aa`, `aca1088`, `064902f` on `closure-hygiene-rulings`, and the
plan at `docs/superpowers/plans/2026-08-10-closure-hygiene-rulings.md`.

**E2 — RULED: design a resolvable addressing scheme.** `remote://{store}/{scope}/{name}` stops
being a synthesized string nothing can dereference. Needs its own spec.

**E3 — RULED: design the remote scoring capability surface.** `SearchRemote` forwarding
`SupplementalScores`/`EntityGraphScores` was a deliberate trust decision; the question of whether
remote scoring needs a surface gets answered rather than re-deferred. Needs its own spec, and the
spec should start from the trust question, not the plumbing.

**E4 — RULED: build the server-side PATCH endpoint** for entry metadata, so entity-typing
promotion actually persists remotely instead of failing closed. New endpoint, new DTO, auth and
bounds. Needs a spec.

**E5 — RULED: send the chunks.** Chunk structure crosses the wire so remote matches local,
`recall({chunk: N})` works remotely, and the tool description stops being false. Needs a wire
field and a server-side change. Arguably unfinished business from the metadata fix that just
landed — same class, same seam.

### Consequence for sequencing

E4 and E5 are the same shape — *the remote wire cannot carry something the local path has* — and
they touch the same seam the `remote-write-correctness` branch just widened. **Design them
together** as one spec rather than two; splitting them means widening the same DTOs twice and
reviewing the same compatibility question twice, which is the exact mistake spec 1 was written to
avoid.

E2 is adjacent (addressing is also a remote-parity gap) but its design question is genuinely
separate — how artifacts are *named and resolved*, not what the wire *carries*. Keep it apart.

E3 is a different axis entirely: trust and where computation happens. Its own spec, its own
brainstorm.

## Group F — CLOSED on `closure-hygiene-rulings`, 2026-08-10.

Six items closed. Two were reclassified from RULE to fix — cheaper to fix than to explain:

- **F1** (fixed, not ruled): `tests/Scrinia.Tests/Benchmarks/ScriniaMemorySystem.cs:36,63` now use
  `McpResponse.IsError` instead of hand-spelling `Status == "error"`.
- **F4** (fixed, not ruled): `tests/Scrinia.Server.Tests/GlobalUsings.cs` added, matching
  `tests/Scrinia.Tests`; the 30 individual `using Xunit;` lines it made redundant were removed.

The remaining four were ruled — written into the code and docs where a future reader will hit
them, not deleted from the record:

- **F2**: `HttpMemoryStore.cs` — the `ListScoped` `scopes is not null` ruling, above the URL
  construction.
- **F3**: `docs/architecture/cli.md` — "Bundle commands: topic filters differ deliberately between
  CLI and HTTP", with pointer comments at `ScriniaCommands.Bundle.cs` and `MemoryEndpoints.cs`.
- **F5**: `HttpMemoryStore.cs` — the `Uri`/`ChunkCount` placeholder ruling at `BuildArtifactEntry`,
  with pointer comments at the three other `ArtifactEntry` construction sites.
- **F6**: `AttributeExtractor.cs` — the timestamp-tokenization ruling on `SeedTermFrequencies`'s
  doc comment.

See commits `0f46797`, `f943f8e` on `closure-hygiene-rulings`, and the plan at
`docs/superpowers/plans/2026-08-10-closure-hygiene-rulings.md`.

## Group G — 2 verified and specced, 2 still genuinely unverified

Carried as "unverified" since the original triage. **Verify first; a ruling on an unverified
claim is a guess.** The first item below was verified on `merge-driver-residuals`, 2026-08-11;
the second was surfaced and verified on `the-sweep`, 2026-08-11; the remaining two still need to
be checked before ruling.

- **VERIFIED TRUE, `merge-driver-residuals`, 2026-08-11.** `reconcile()`'s resolution path
  writes via a raw `File.WriteAllText` (`src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:199-212`, meta
  auto-merge branch at `:125-138`), bypassing `FileMemoryStore`'s write pipeline entirely — so
  `originalBytes`, `chunkCount`, `contentPreview`, and `keywords` keep their pre-merge values and
  no reindex is triggered. Deliberately **out of scope** here: a behaviour change to the
  resolution path needs its own design, per the merge-driver spec's Out of Scope section. This
  becomes **one specced item** — a dedicated design for reindexing after `reconcile()`'s
  resolution writes — not a `/findings/` chunk.
- **VERIFIED TRUE, `the-sweep`, 2026-08-11.** `MemoryEventSinkContext.Default`
  (`src/Scrinia.Core/IMemoryEventSink.cs:27,33`) is a bare mutable static with no lock, mutated
  by many uncoordinated fixtures — `TestHelpers.cs:111-118` (`StoreScope`, used pervasively),
  `RemoteStoreToolTests.cs:106,118,173,180`, `ConsolidateReembedTests.cs:46,89`, and
  `MemoryEventSinkContextInstallDefaultTests.cs:19-41` — none of which sit in a `[Collection]`,
  though the suite uses `DisableParallelization` collections elsewhere. It surfaced once during
  Task 6 of `the-sweep` and did not reproduce across three full reruns. Not fixed here — both
  real fixes have costs outside that branch's scope: serialising every mutator means putting
  `StoreScope`, used by most of the suite, into a non-parallel collection, which would serialise
  a large fraction of the run; making `Default` `AsyncLocal` is a production change with its own
  semantics to settle. This becomes **one specced item** — a dedicated design choosing between
  serialising `StoreScope`'s mutators and making `MemoryEventSinkContext.Default` `AsyncLocal` —
  not a `/findings/` chunk.
- Whether `WorkspaceRoot` still bypasses the layout-migration latch (recorded as harmless at the
  time, with the reasoning noted as incomplete).
- The `WithInfo` prepend→append ordering concern. The original claim ("provably unreachable
  because Remember/Append never call `WithInfo`") is **stale** — it now has six production call
  sites, two of which append to an existing `Info`. The reachability argument no longer holds and
  the ordering needs re-deciding.

## Two known divergences, recorded and not in any group

Neither is closeable as a defect; both are consequences of decisions already made, and both are in
the CHANGELOG. Listed so nobody rediscovers them as bugs.

- **BM25 term frequencies differ by store mode**, so search ranking differs local vs remote. Not
  fixable by moving the merge — the wire carries one flat map and the server cannot distinguish
  agent attributes from carrier attributes.
- **MCP and REST disagree on a bare date in a `review` attribute.** `remember()` routes it through
  a lenient parse and stores a round-tripped date that goes stale; a direct `POST` stores it
  verbatim, so it reads as a *condition* and the memory reports `needsReview=true` forever.

## Closure order — revised after the Group E rulings

Group E's answers turned three closure branches into **three closure branches plus three new
designs**. The designs are the long pole; the closure work is ready now and should not wait on
them.

**Done:**

- ~~Group F + Group D + E1~~ — closed on `closure-hygiene-rulings`, 2026-08-10. See the group
  sections above.
- ~~Group A~~ — closed on `merge-driver-residuals`, 2026-08-11. See the group section above.
- ~~Group B~~ — closed on `the-sweep`, 2026-08-11. See the group section above.

**Ready to execute (specs exist or none needed):**

1. **Group C** — folds into whichever remote branch runs first, or stands alone if none does soon.

**Needs a spec before a plan:**

2. **Remote wire parity (E4 + E5)** — the PATCH endpoint for entry metadata and chunk structure on
   the wire. One spec, for the reason given above.
3. **Addressable artifacts (E2)** — a resolvable scheme for `remote://`.
4. **Remote scoring surface (E3)** — start from the trust question.

**Group G** gets verified inside whichever branch touches its area. Each result either closes the
item or produces exactly one new specced item — not a `/findings/` chunk.

## The standing rule for every branch on this list

**They do not get to write new deferred-followups.**

Anything found mid-branch is fixed in that branch, or it is raised as an explicit decision at the
time — not written to a `/findings/` memory for a fourth generation to inherit. If a branch
genuinely cannot close something it found, that is a stop-and-ask, not a note.

The three merged branches in this sequence wrote 21 deferred chunks between them while draining a
35-item backlog. That is the behaviour this document exists to end.
