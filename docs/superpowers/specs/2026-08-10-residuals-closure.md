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

## Group A — merge driver (SPECCED, 8 items)

Design: `2026-08-08-merge-driver-residuals-design.md`. Untouched by the three merged branches;
its facts are from `c3773bf` and need re-verification, and it flags F9 as carried-from-memory.

Sharpest item: a `ConflictDir` of `"."` or `""` resolves to the artifact directory and the driver
writes a `.gitignore` containing `*` into it — silently untracking every memory at and below it.
The previous fix wave made this **worse** (before the `.gitignore` write it merely left a stray
directory pair). Needs a hand-edited `merge.config` to reach: low likelihood, high consequence.

Also carries the highest-value follow-up its own reviewer named: a `merge.config` round-trip test
through the **real writer** and the **real reader**. The contract is declared twice in two
assemblies with two independent camelCase policies and nothing pinning them — that exact
divergence *was* the camelCase bug.

## Group B — the sweep (SPECCED, 11 items + 5 inherited)

Design: `2026-08-08-store-read-path-and-sweep-design.md`, **Part B**. Split out when Part A ran,
for reasons that were good then and have expired now: Part A is merged, so nothing gates this.

Contains B1–B8 (test-coverage gaps), the `MemoryList` `AppendLine`/`\r` formatting defect masked
by `GoldenNormalizer`'s blanket `\r` strip, and two docs corrections.

**Two items in it are the ones worth ranking first** — `Migration_DoesNotSweepTheAgentDirectory`
discards `Migrate()`'s result, and the remote file-refs mutation test only discriminates a
CWD-shaped root. Both are tests that **cannot fail** under the regression they exist to catch,
which is worse than no test because the suite reports coverage that is not there.

**Resolved while Part A ran, fold in:** the `link.yaml` golden covers the success envelope only,
not partial failure. Part B's B6 lists that as an open question; it is answered.

**New coverage gaps from this sequence to fold into the same branch:**
- The local `MaxValuesPerKey` cap in `KnownAttributes.Merge` has **no test**, so the "local and
  remote accept the same input by construction" property is unpinned.
- `ListScoped`'s new throw-on-non-2xx is unguarded.
- The error-body text in `WriteArtifactAsync`'s exception is unguarded.
- No test pins the fast-path `EnterReadLock` in `LoadIndex` (all four tests are single-threaded
  and would pass with it deleted).
- `LoadIndexCacheHitTests`' `File.Delete` is not self-guarding.

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

## Group G — genuinely unverified, must be checked before ruling (3 items)

Carried as "unverified" since the original triage and never resolved. **Verify first; a ruling on
an unverified claim is a guess.**

- `reconcile()` leaving `originalBytes`/`chunkCount`/`contentPreview`/`keywords` stale after
  resolving, with no reindex triggered.
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

**Ready to execute (specs exist or none needed):**

1. **Group A — merge driver.** Spec exists (`2026-08-08-merge-driver-residuals-design.md`); its
   facts are from `c3773bf` and must be re-verified first. Contains the `.gitignore`-clobbering
   `ConflictDir` bug and the `merge.config` round-trip test.
2. **Group B — the sweep.** Spec exists (Part B of the store-read-path design), plus the five
   coverage gaps this sequence added. Ranks the two cannot-fail tests first.
3. **Group C** — folds into whichever remote branch runs first, or stands alone if none does soon.

**Needs a spec before a plan:**

4. **Remote wire parity (E4 + E5)** — the PATCH endpoint for entry metadata and chunk structure on
   the wire. One spec, for the reason given above.
5. **Addressable artifacts (E2)** — a resolvable scheme for `remote://`.
6. **Remote scoring surface (E3)** — start from the trust question.

**Group G** gets verified inside whichever branch touches its area. Each result either closes the
item or produces exactly one new specced item — not a `/findings/` chunk.

## The standing rule for every branch on this list

**They do not get to write new deferred-followups.**

Anything found mid-branch is fixed in that branch, or it is raised as an explicit decision at the
time — not written to a `/findings/` memory for a fourth generation to inherit. If a branch
genuinely cannot close something it found, that is a stop-and-ask, not a note.

The three merged branches in this sequence wrote 21 deferred chunks between them while draining a
35-item backlog. That is the behaviour this document exists to end.
