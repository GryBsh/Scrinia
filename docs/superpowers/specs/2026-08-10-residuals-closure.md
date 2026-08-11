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

## Group D — hygiene (OPEN, 5 items)

| Item | Verified at `d6fd830` |
|---|---|
| `cmds.txt` is untracked **and unignored** at the repo root — `git check-ignore` finds no rule. Every branch in this sequence has routed around it by staging explicit paths | confirmed |
| `src/Scrinia.Core/FileMemoryStore.cs:3` — unnecessary `using System.Linq;` (CS8019/CS8933), invisible to the build because `EnforceCodeStyleInBuild` is unset and there is no `.editorconfig` | confirmed |
| `FileLock.AcquireShared` does **four** filesystem ops per call: `Directory.Exists` at `:43`, then an **unconditional** `Directory.CreateDirectory` at `:51` redundant with it, then the open, then dispose. Removing the redundancy cuts the remaining cold-path cost | confirmed |
| `CachedIndex.BuildNameMap` runs on every cache write to build a dictionary nobody reads. `NameToPosition`, `Rebuild()`, `GetOrComputeCorpusStats()` are documented as dead in code and docs but not deleted | documented on `store-read-path` |
| **Live** docs still referencing the deleted `ScriniaArtifactStore`: `docs/architecture/cli.md` and `AGENTS.md`. **Do not touch** the 12 hits under `docs/planning/**` and `docs/superpowers/**` — those are dated records of past work, and rewriting history to match the present is how a project loses its audit trail | confirmed |

Also: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs:265` cites
`docs/superpowers/specs/2026-07-10-memory-model-keywords-attributes-design.md`, which does not
exist and was never tracked.

## Group E — DECIDED 2026-08-10. All five ruled; none documented away.

The user took the maximal option on every one. Nothing here closes with a written limitation;
all five become work. **E2–E5 are design efforts, not fixes** — they need specs before plans.

**E1 — RULED: "matched nothing" is NOT a success. Make it fail visibly on all three surfaces.**
The warning envelope prints yellow, not green (`ScriniaCommands.Bundle.cs:62`, `:101`);
`scri bundle import --topics doesnotexist` exits non-zero; `ImportBundle` stops returning 200 when
`topicCount == 0`. Breaking for anything gating on exit 0 or HTTP 200 — needs a CHANGELOG entry
saying so. This is the one Group E item that is a straightforward fix rather than a design.

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

## Group F — rule closed, do not carry (RULE, 6 items)

Each has been deliberately left with sound reasoning, more than once. Write the ruling into the
code or docs where a future reader will hit it, then **delete the backlog entry**.

1. `tests/Scrinia.Tests/Benchmarks/ScriniaMemorySystem.cs:36,63` spell `Status == "error"` rather
   than using `McpResponse.IsError`. Test-project code, deliberate, zero stray occurrences in
   `src/`.
2. `HttpMemoryStore.ListScoped`'s `scopes is not null` check drives *which URL is built*, not
   whether an empty filter is appended — genuinely not the same inconsistency as the sibling that
   was changed.
3. CLI/HTTP divergence on bundle topic filters: `[FromQuery] string[]` binds repeated keys, the
   CLI comma-splits. Both correct for their own surface; documented and pinned. Do not unify
   without deciding which surface is canonical.
4. `using Xunit;` in `tests/Scrinia.Server.Tests` files — that project has no `GlobalUsings.cs`,
   so it is required, and in `tests/Scrinia.Tests` it is the 72-file house preamble.
5. `Uri: ""` and `ChunkCount: 1` in the client's search-result `ArtifactEntry` — `Uri` is E2,
   `ChunkCount` is E5. Tracked there, not separately.
6. The `review` round-trip timestamp is tokenized into junk search terms (`00`, `0000000`, `2026`)
   on **both** paths. Ranking noise, affects local equally, no data loss.

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

**Ready to execute (specs exist or none needed):**

1. **Group F + Group D + E1** — one branch. Write the six standing rulings where a future reader
   hits them, delete the backlog entries, do the five hygiene fixes, and make the bundle warning
   fail visibly. Small, mechanical, one review. Shrinks the list fastest.
2. **Group A — merge driver.** Spec exists (`2026-08-08-merge-driver-residuals-design.md`); its
   facts are from `c3773bf` and must be re-verified first. Contains the `.gitignore`-clobbering
   `ConflictDir` bug and the `merge.config` round-trip test.
3. **Group B — the sweep.** Spec exists (Part B of the store-read-path design), plus the five
   coverage gaps this sequence added. Ranks the two cannot-fail tests first.
4. **Group C** — folds into whichever remote branch runs first, or stands alone if none does soon.

**Needs a spec before a plan:**

5. **Remote wire parity (E4 + E5)** — the PATCH endpoint for entry metadata and chunk structure on
   the wire. One spec, for the reason given above.
6. **Addressable artifacts (E2)** — a resolvable scheme for `remote://`.
7. **Remote scoring surface (E3)** — start from the trust question.

**Group G** gets verified inside whichever branch touches its area. Each result either closes the
item or produces exactly one new specced item — not a `/findings/` chunk.

## The standing rule for every branch on this list

**They do not get to write new deferred-followups.**

Anything found mid-branch is fixed in that branch, or it is raised as an explicit decision at the
time — not written to a `/findings/` memory for a fourth generation to inherit. If a branch
genuinely cannot close something it found, that is a stop-and-ask, not a note.

The three merged branches in this sequence wrote 21 deferred chunks between them while draining a
35-item backlog. That is the behaviour this document exists to end.
