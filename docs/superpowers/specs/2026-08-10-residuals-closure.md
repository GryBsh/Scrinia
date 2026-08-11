# Residuals Closure — the backlog stops rotating here

**Status:** closure in progress — Groups A–D and F closed; E decided; three designs outstanding.
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
`AppContext.BaseDirectory` — under VSTest these resolve to the same directory, so the pair adds
no discriminating power beyond either alone, but the test still catches the real regression),
plus the bundle-import
agent-metadata test (B5) widened into a real export/import round trip and the lock-file
precondition test (G5) now asserts the lock file existed before deleting it. B1 (transient 503 →
breaker), G1 (`MaxValuesPerKey` cap), G2 (`ListScoped` non-2xx throw), and G3 (error-body text in
the exception message) all got new tests. The `StorePurityInvariantTests` now assert the
invariant its own doc states, via a cold-load second store. Four golden fixtures were added
(`update-warning`, `recall-truncated`, `compact-multichunk`, `link-partial-failure`), taking the
snapshot count from 14 to 18. The `MemoryTools.Core.cs` `AppendLine`/`\r` formatting defect is
fixed — all 20 `AppendLine` calls in `BuildSummary`/`BuildFullList` now use
`Append(...).Append('\n')` — the only production change in this branch. Its regression guard
(`ListLineEndingTests`) binds only on a host where `Environment.NewLine` is `\r\n`; CI runs
`ubuntu-latest`, so CI does not enforce it, and the goldens structurally cannot either since
`GoldenNormalizer` strips `\r` on the write path — the guard is real but Windows-only, and that
is recorded, not silently assumed. `Scrinia.TestDoubles` now
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

**B2 was redefined, not implemented as specced.** The proposed test inputs (`C:\foo`, `/foo`)
could never pin `FileEntityName.IsValid`'s `!Path.IsPathRooted(name)` clause: both contain a
separator, so `IsValid` short-circuits on `IndexOfAny(['/','\\'])` before reaching
`!Path.IsPathRooted(name)` — verified by running `Path.IsPathRooted` against representative
inputs, not by inspection alone. The clause itself is reachable, though — `C:foo` (no separator)
passes the slash and `..` checks and reaches it, and it rejects `C:foo` because
`Path.IsPathRooted("C:foo")` is `true` on Windows. The clause is redundant, not unreachable: the
char whitelist that follows would also reject `C:foo` (it admits no `:`) had this clause not
fired first. B2 now adds the two non-pinning-but-still-required cases and records the redundancy
(not unreachability) as a ruling in `FileEntityName.cs` (`IsValid`'s doc comment).

**G4 was ruled closed in writing, not pinned by a test.** The fast-path `EnterReadLock` in
`FileMemoryStore.LoadIndex` got a real 300-round stress test; it never failed with or without the
lock, so the lock is unpinnable by a black-box test. The ruling is in
`src/Scrinia.Core/FileMemoryStore.cs` immediately above the lock (`// RULING (the-sweep, Task 1,
G4)`), explaining what the lock protects and why nothing here can pin it.

See commits `20a14dd`, `4bd2436`, `3ae44b6`, `7eab8d9`, `2ebd54a`, `32bc5fe`, `692cae0`, `767dc7a`,
`f0184da` on `the-sweep`, and the plan at `docs/superpowers/plans/2026-08-11-the-sweep.md`.

## Group C — CLOSED on `remote-mode-leftovers`, 2026-08-11.

All four specced items fixed. **This section's own table was wrong about the scale: it named 2
sites of the case-sensitivity defect, and there were 10 — fixing those uncovered 2 more.** The
user ruled at plan time that the branch fixes the whole class rather than only the named sites, so
one of its five tasks exists entirely because of that undercount. Recorded here rather than
quietly corrected.

The ten: the interface default (`src/Scrinia.Core/IMemoryStore.cs:50`), the remote override
(`src/Scrinia/HttpMemoryStore.cs:411`), `EntityTypingEventHandler`'s own upstream lookup
(`src/Scrinia/Services/EntityTypingEventHandler.cs:43-44`),
`MemoryOrchestrator.StoreAsync` (`src/Scrinia.Server/Services/MemoryOrchestrator.cs:124-127`) and
`AppendAsync` (`:228-231`), the two MCP write paths (`src/Scrinia.Mcp/MemoryTools.Core.cs:323-326`
and `:1096-1099`), `compact()` (`src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:583-586`), and
`FileMemoryStore.CopyMemory`'s two ordinal source lookups, now one lookup taken before the
artifact path is built (`src/Scrinia.Core/FileMemoryStore.cs:1614-1617`).

**Three of the ten lost data silently, and each deserves naming:**

- **`StoreAsync` lost a version with no archive.** A re-store under different casing was read as a
  brand-new memory, so it skipped `ArchiveVersion` and reset `CreatedAt` — while the write still
  landed on the same file on a case-insensitive filesystem. Prior version gone, nothing in
  `versions/`.
- **`AppendAsync` dropped everything the entry carried forward** — its prior `ChunkEntries`, its
  merged attributes, its curated preview — because a null `existingEntry` meant the append had
  nothing to carry.
- **`compact()` discarded the memory's attributes and keywords**, sending the write out with
  `meta: null`, which is exactly the metadata reset the comment above that call already existed to
  prevent.

`CopyMemory` was a fourth, of a different shape: it resolved the source *file* case-insensitively
(the filesystem did that) but its *entry* ordinally, so a case-mismatched copy silently took the
synthesized-defaults branch — destination created, bytes intact, attributes and keywords and
curated preview gone.

**Matching the name was only half the fix.** Every one of these paths turns the subject back into
a file path afterwards (artifact writes, sidecar upserts, version archives), so a resolved entry
now hands its own casing forward as well. Leaving the caller's casing in place would have left the
fix depending on the filesystem to fold case on its behalf: right on NTFS, and a second entry
alongside the first anywhere case-sensitive. That is not a stylistic preference — it is the
Critical finding from the branch's first review, where a fix that looked complete was still
Windows-only.

**The interface default is reachable only from the test doubles, and its fix should not be
overstated.** Both real stores override `TryUpdateEntry`
(`FileMemoryStore.cs:1078`, `HttpMemoryStore.cs:408`); `Scrinia.TestDoubles.FakeMemoryStore` is
the only type that inherits the default. Fixing it is a contract fix and a double-fidelity fix —
it keeps the doubles modelling the stores they stand in for, and it keeps the interface's stated
contract true — **not** a live production bug fix.

**The remote override's fix restores a distinction, not data.** `HttpMemoryStore.TryUpdateEntry`
still returns `false` by design, because there is no server endpoint to persist an entry-metadata
mutation through; E4's PATCH endpoint remains the real fix for remote metadata writes. What the
case-insensitive lookup buys is that "not found" and "found but cannot persist" stay told apart,
and since the return value is `false` either way, `mutateRan` is the only observable — which is
what the guards assert (`tests/Scrinia.Server.Tests/RemoteLoadIndexTests.cs:105-150,228-235`).

**`Keywords` on the wire disarms a trap rather than fixing a live loss.** `MemoryListItem` and
`ListApiItem` now carry `keywords`, threaded at both server construction sites and both client
ones. Remotely, `MaintenanceEventSink`'s orphan branch (`src/Scrinia.Core/MaintenanceEventSink.cs:102-108`)
reads `sourceEntry.Keywords`, gets `null`, defaults to an empty list and writes back
`Keywords = ["orphan"]` — clobbering every other keyword the memory had. It is inert today only
because the remote `TryUpdateEntry` refuses every write. **E4 would arm it.** Fixing the wire now
means the trap is disarmed before the endpoint that would spring it exists.

**`RemoteApiVersion.Current` was deliberately not bumped.** It stays at `"1"`
(`src/Scrinia.Core/Resilience/RemoteApiVersion.cs:22`). The reasoning: this is an additive,
optional *response* field, and it cannot make either direction silently wrong — an older client
ignores it, and a newer client against an older server reads `null` exactly as it did before the
change. The guard exists to catch a server silently ignoring a *request* parameter it does not
understand; nothing here is a request parameter.

**Two sites shipped rulings instead of tests, on a claim that was false — CORRECTED on
`case-sensitive-guards`, 2026-08-11.** `FileMemoryStore.TryUpdateEntry`'s
`File.Exists(SanitizeName(subject) + ".meta.json")` gate is removed — the authoritative lookup
below it is already case-insensitive and returns false for a genuine miss, so the gate could only
ever disagree with it by being wrong — and `Remove`'s exact-name probe now falls back to a
case-insensitive sidecar scan (ordinal-sorted, so the tie-break on a filesystem holding both
`Note.meta.json` and `note.meta.json` is deterministic rather than arbitrary). Both fixes are
right. The ruling attached to them was not. It said NTFS folds case, so a green test at either
site "would be green for the filesystem's reasons, not the code's", and that the only instrument
that could observe them was running the suite on a case-sensitive filesystem, which this host was
taken to be incapable of providing.

**NTFS is case-sensitive per directory on request.** `fsutil file setCaseSensitiveInfo <dir>
enable` — no admin rights, no WSL — and the flag is inherited by every subdirectory created
underneath afterwards, including the ones .NET creates. Both behaviours are now pinned by tests
that run on Windows against a directory carrying that flag:
`CaseSensitiveFileStoreTests.TryUpdateEntry_ResolvesCaseMismatchedSubject` and
`Remove_DeletesCaseMismatchedName` (`tests/Scrinia.Tests/CaseSensitiveFileStoreTests.cs:37,62`),
over the helper at `tests/Scrinia.TestDoubles/CaseSensitiveDirectory.cs`. Each was verified
load-bearing by reverting the production fix and watching it fail, and the reviewer reproduced
those reverts independently rather than taking the implementer's word. The comments at
`src/Scrinia.Core/FileMemoryStore.cs:1008` and `:1087` survive, but they now cite the tests
instead of asserting nothing can pin them.

**The false claim was in eight places, not two.** Five `RULING` comments in `src/`
(`FileMemoryStore.cs:1008`, `:1087`, `:1177`;
`src/Scrinia.Server/Services/MemoryOrchestrator.cs:191`;
`src/Scrinia.Mcp/MemoryTools.Core.cs:1063`); two untagged restatements of it in test doc comments
(`tests/Scrinia.Tests/RememberWriteMetadataTests.cs`,
`tests/Scrinia.Server.Tests/MemoryOrchestratorAttributesTests.cs`); and one further paragraph
claiming an ordering guard fails under a revert it does not fail under. The plan was written for
five; verifying the plan found the two untagged ones, and the branch's own Task 2 review found the
eighth.

**Why it survived scrutiny is the transferable part.** Two reviewers examined "unpinnable"
specifically for whether it was a convenient excuse, and both judged it genuine. They answered
their own question correctly: the excuse was *sincere*. It was also *false*. Nobody checked
whether NTFS could be *made* case-sensitive. Sincerity and truth are separate tests, and a review
that runs only the first one passes an honest mistake through untouched.

**Nothing about the production behaviour changed.** Every `src/` edit on `case-sensitive-guards`
is a comment; the fixes themselves were already correct when they landed on
`remote-mode-leftovers`. Only the claims about their observability were wrong. Where the
case-sensitivity flag cannot be obtained — a non-NTFS volume, a policy that blocks `fsutil`, a
case-insensitive macOS volume — `CaseSensitiveDirectory.TryCreate` returns null and every new test
**skips rather than passes**, because a skipped test read as a passing one is the same failure
wearing a new costume. The suite went 1867 → 1872 with the pre-existing skip count held at 4 at
every step, which is how that was checked rather than assumed.

See commits `428140f`, `ef059a4`, `4c26866`, `4b825a1` on `case-sensitive-guards`, and the plan at
`docs/superpowers/plans/2026-08-11-case-sensitive-guards.md`.

**One user-visible behaviour change, ruled intended.** Re-storing `MixedCase` as `mixedcase` now
keeps the name `MixedCase` instead of silently renaming the memory to the caller's casing, and the
response reports `MixedCase`. The consequence is that `remember()` no longer offers any route to
change a memory's name casing — a case-differing name is an update. Ruled intended: casing renames
are not a `remember()` capability, and the old behaviour was an accident of the defect, not a
feature. In CHANGELOG.md.

**Two spec/plan errors this branch found, both load-bearing enough to record:**

- **The plan claimed `FileMemoryStore.TryUpdateEntry` "already got this fix."** It did not. The
  claim came from reading the method's case-insensitive comparison and not the case-sensitive
  `File.Exists` gate three lines above it, which gated the whole method. This is what made the
  first task's fix Windows-only, and it is why an eighth site had to be added to a task specced for
  seven.
- **The plan told an implementer to pin the append defect via the wire's `chunkCount`, which
  cannot observe it.** `chunkCount` is derived from the appended artifact
  (`MemoryOrchestrator.cs:256`), never from `existingEntry`, so the prescribed `ChunkCount == 2`
  assertion passed *before* the fix. What a null `existingEntry` actually loses is `ChunkEntries`,
  which is not a wire field at all. The implementer caught this by measuring rather than working
  around it, and re-aimed the guard at `Attributes` and `CreatedAt`, which do fail pre-fix.

**One same-class site deliberately not fixed. It is an open decision awaiting an answer, not a
deferral** — this branch's standing rule forbids writing new deferred followups, and this is not
one:

1. **`CopyMemory`'s *destination* existence check is still filesystem-case-dependent**
   (`src/Scrinia.Core/FileMemoryStore.cs:1653`). There is no existing case-insensitive lookup to
   align it with, and the right answer is a product call, not an implementer's: is a
   case-differing destination "already exists", or a distinct memory? **Needs a decision.**

**A second open decision was recorded here and has since been closed by fixing it, because the
claim underneath it was false.** It read: "`Compact` and `AppendAsync` resolve the artifact on the
caller's casing before the entry lookup … Both fail *closed* on a case-sensitive filesystem —
not-found, or create-new — so there is no data loss." The whole-branch review disproved the append
half. "Create-new" does not create anything new: `append()` fell through to the store/`Remember`
path, whose lookup **this branch had just made case-insensitive**, which found the existing memory,
archived it, and wrote the appended chunk alone as the memory's entire content. `append()` behaved
as `replace()` — and that was a regression this branch introduced, since before it the fallback's
lookup was ordinal too and the miss produced a harmless duplicate instead. Both append
implementations now take the entry lookup **before** the artifact read and read under the entry's
own casing; see the "Fixed — a re-store under a different casing…" section of CHANGELOG.md.

`Compact` was checked at the same time and does genuinely fail closed: its
`ReadArtifactAsync` (`src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:520`) returns a `NotFound` error
response and the method changes nothing — no archive, no write. Left as it is, deliberately. The
residual is that on a case-sensitive filesystem `compact('mixedcase')` answers "not found" for a
memory that exists, and its own case-insensitive lookup at `:583-584` is then unreachable. That is
a wrong answer, not a lost one.

Suite went 1849 → 1867, zero failures throughout.

See commits `f79dde1`, `b96b7d1`, `b6e2ce6`, `e8c2e25`, `64911b5`, `66d31c3`, `7898ed7` on
`remote-mode-leftovers`, and the plan at
`docs/superpowers/plans/2026-08-11-remote-mode-leftovers.md`.

**The whole-branch review found three things the five per-task reviews structurally could not, and
they are fixed in `706b3e7`, `588e03a`, `37b5bea`, `860c624`.** Two of the twelve fixes were halves
whose other half nobody owned, and one of those was a regression this branch introduced:

- **`append()` under a mismatched casing became a destructive replace** — the open decision
  corrected above. Both implementations now take the entry lookup before the artifact read.
- **`forget()` was half-fixed.** `Remove` became case-insensitive; `DeleteArtifact` did not, and
  `forget()` calls both. On a case-sensitive filesystem it reported success while leaving the
  `.nmp2` on disk, orphaned. Fixed in the store primitive so the pair agrees for every caller.
- **One of the ten sites had no guard at all** — the MCP `append()` persistent branch. Deleting its
  `OrdinalIgnoreCase` and its carry-forward passed the entire suite, because the only append
  coverage drove `MemoryOrchestrator.AppendAsync`, a different implementation in a different
  project. Now covered.

**A third wrong prescribed fact, caught by measuring.** The review specified pinning the append
carry-forward with a single-chunk seed and a `ChunkEntries.Length == 2` assertion. `remember()`
populates `ChunkEntries` only for multi-chunk content (`MemoryTools.Core.cs:203-205`), so a
single-chunk seed leaves it `null` and the assertion reads 1 with or without the fix. The guard uses
a two-chunk seed and asserts 2 → 3 instead.

**Two of the three fixes were labelled unobservable on NTFS at the site, and both labels were
wrong — CORRECTED on `case-sensitive-guards`, 2026-08-11.** The append hoist
(`src/Scrinia.Server/Services/MemoryOrchestrator.cs:191`, `src/Scrinia.Mcp/MemoryTools.Core.cs:1063`)
and `DeleteArtifact`'s fallback (`src/Scrinia.Core/FileMemoryStore.cs:1177`) carried ruling comments
naming a case-sensitive filesystem — "CI runs `ubuntu-latest`" — as the instrument that would observe
them. That instrument is available here: NTFS takes the case-sensitivity flag per directory (see the
correction above). All three are now pinned by tests asserting the **outcome**, not the ordering:
`CaseSensitiveAppendTests.AppendUnderADifferentCasing_AppendsRatherThanReplacesTheContent`
(`tests/Scrinia.Server.Tests/CaseSensitiveAppendTests.cs:33`),
`CaseSensitiveMcpAppendTests.Append_UnderADifferentCasing_AppendsRatherThanReplacesTheContent`
(`tests/Scrinia.Tests/CaseSensitiveMcpAppendTests.cs:38`), and
`CaseSensitiveFileStoreTests.DeleteArtifact_DeletesCaseMismatchedName`
(`tests/Scrinia.Tests/CaseSensitiveFileStoreTests.cs:85`). With the hoist reverted, both append tests
fail on content — the appended body replaces the original instead of following it. That is the data
loss itself, not a proxy for it. All three skip rather than pass where the flag cannot be obtained.

**The claim about the ordering guards needed its own correction, and it cuts both ways.** The
paragraph above asserted that removing the hoist turns the ordering guard red at both sites. On the
server side that is **not** true of a pure block-move revert: `MemoryOrchestrator.AppendAsync`'s
rollback capture (`:339`) calls `ReadArtifactAsync(subject, scope)` with the by-then-corrected
subject regardless of hoist order, overwriting the value the guard recorded, so all three of its
assertions stay green — verified by applying exactly that revert, not reasoned out. It **is**
discriminating for the larger whole-file revert an earlier reviewer used to certify it load-bearing,
which also collapses the persistent branch's `ReadArtifactAsync(subject, scope)` back into the
unified `ResolveArtifactAsync(name)` call — the mutation that actually makes `ResolvedName` fire.
Both results are true; the original comment never said which mutation it meant. The MCP-side
ordering guard genuinely does discriminate, because that path has no rollback read to mask it:
reverted, `ReadSubject` is `"mixedcase"` and the assertion fails on a value mismatch, not on a null.
Both guards now say all of this at the site
(`tests/Scrinia.Server.Tests/MemoryOrchestratorAttributesTests.cs:258-276`,
`tests/Scrinia.Tests/RememberWriteMetadataTests.cs:211-220`). The content-based tests are the reliable
discriminator in both cases; the ordering guards are complementary rather than redundant — they run
on every host and prove only the order.

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
- **VERIFIED TRUE, `the-sweep`, 2026-08-11 (evidence corrected — see below).**
  `MemoryEventSinkContext.Default` (`src/Scrinia.Core/IMemoryEventSink.cs:27,33`) is a bare
  mutable static with no lock, mutated by many uncoordinated fixtures — `TestHelpers.cs:111-118`
  (`StoreScope`, used pervasively), `RemoteStoreToolTests.cs:106,118,173,180`,
  `ConsolidateReembedTests.cs:46,89`, and `MemoryEventSinkContextInstallDefaultTests.cs:19-41`.
  Three of the four sites are outside any `[Collection]`: `TestHelpers.cs`'s `StoreScope` helper,
  `RemoteStoreToolTests.cs`, and `MemoryEventSinkContextInstallDefaultTests.cs` are all
  uncollected. `ConsolidateReembedTests.cs` is the exception — it carries
  `[Collection(GlobalWorkspaceConfigCollection.Name)]` (`:26`) — but that collection's
  `[CollectionDefinition(..., DisableParallelization = true)]` (`:129`) exists to serialise
  `WorkspaceSetup`'s global static config, not `MemoryEventSinkContext.Default`; its own doc
  comment (`:120-127`) says `StoreScope` is already `AsyncLocal`-safe and out of scope for that
  collection. Membership in a `[Collection]` only serialises a test against other tests in the
  *same* collection (here: `BundleGitIgnoreTests`, `ConsolidateTests`, `MigrationTests`) — it does
  nothing against the uncollected `RemoteStoreToolTests` and
  `MemoryEventSinkContextInstallDefaultTests` mutators running in parallel. The race on
  `MemoryEventSinkContext.Default` survives regardless of that one collection membership. It
  surfaced once during Task 6 of `the-sweep` and did not reproduce across three full reruns. Not
  fixed here — both real fixes have costs outside that branch's scope: serialising every mutator
  means putting `StoreScope`, used by most of the suite, into a non-parallel collection, which
  would serialise a large fraction of the run; making `Default` `AsyncLocal` is a production
  change with its own semantics to settle. This becomes **one specced item** — a dedicated design
  choosing between serialising `StoreScope`'s mutators and making
  `MemoryEventSinkContext.Default` `AsyncLocal` — not a `/findings/` chunk.
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
them. **All of the closure work has now landed — the three designs are all that remain.**

**Done:**

- ~~Group F + Group D + E1~~ — closed on `closure-hygiene-rulings`, 2026-08-10. See the group
  sections above.
- ~~Group A~~ — closed on `merge-driver-residuals`, 2026-08-11. See the group section above.
- ~~Group B~~ — closed on `the-sweep`, 2026-08-11. See the group section above.
- ~~Group C~~ — closed on `remote-mode-leftovers`, 2026-08-11; its rulings corrected on
  `case-sensitive-guards`, 2026-08-11. Five behaviours had shipped as rulings claiming NTFS could
  not observe them and that only a `ubuntu-latest` CI leg could; that was false, and all five are
  now pinned by tests running on Windows against a per-directory case-sensitive volume. **Nothing
  in Group C is waiting on a CI leg to be verified.** See the group section above, which also
  carries one open decision it deliberately did not close (`CopyMemory`'s destination existence
  check — the second recorded decision was closed by fixing it).

**Needs a spec before a plan:**

1. **Remote wire parity (E4 + E5)** — the PATCH endpoint for entry metadata and chunk structure on
   the wire. One spec, for the reason given above.
2. **Addressable artifacts (E2)** — a resolvable scheme for `remote://`.
3. **Remote scoring surface (E3)** — start from the trust question.

**Group G** gets verified inside whichever branch touches its area. Each result either closes the
item or produces exactly one new specced item — not a `/findings/` chunk.

## The standing rule for every branch on this list

**They do not get to write new deferred-followups.**

Anything found mid-branch is fixed in that branch, or it is raised as an explicit decision at the
time — not written to a `/findings/` memory for a fourth generation to inherit. If a branch
genuinely cannot close something it found, that is a stop-and-ask, not a note.

The three merged branches in this sequence wrote 21 deferred chunks between them while draining a
35-item backlog. That is the behaviour this document exists to end.
