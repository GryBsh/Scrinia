# Remote Copy and Fence-Aware Extraction

**Status:** design approved 2026-08-14, ready for an implementation plan.

Closes every residual produced by the `derivation-merge-residuals` branch (merged to `main` at
`bc7fdb3`, 2026-08-13). Three groups: a wire change so a copy can cross the client/server store
boundary, fence-aware reference extraction, and a set of correctness and hygiene items.

## Why this document exists

The `residuals-closure` backlog carries a standing rule: a branch fixes what it finds, or raises it
as a stop-and-ask at the moment it finds it. It does not write notes for a later branch to inherit.
`derivation-merge-residuals` broke that rule — it collected roughly ten findings and disclosed them
at merge time, attached to a "merge with fixes" verdict. This branch closes them, and restates the
rule as a first-class constraint on itself.

Every claim below was verified against `main` at `bc7fdb3` before it was written. **Three of the
recorded residuals did not survive that check** and are corrected here rather than implemented:

- **`link` is not a silent refusal.** `MemoryTools.Core.cs:1014-1018` already returns a structured
  error with a suggested alternative, checked before either side is written. The ledger line
  describing it as silent predated that fix.
- **Term-frequency drift is a ruling, not a defect.** `SidecarTermFrequencyMerge.cs:23-26` argues
  Max explicitly: a count is a term's frequency in one memory's content, and summing inflates on
  every merge. The monotonic drift is the documented cost of the smallest answer that loses
  nothing. Closed as ruled; no code change.
- **The punctuation trim is already balance-aware.** See 3.3 — `thing:Foo(bar)` survives intact
  today; the residual was written before the fix that made it so.

That is three of roughly ten. A residual list written at branch close, rather than a decision taken
when each item was found, decays quietly — items get fixed by later work and the list never learns.
It is a second reason the standing rule below exists, independent of the first.

## Global constraints

- `src/Scrinia.Merge/Scrinia.Merge.csproj` must not gain a `ProjectReference`. It is a standalone
  AOT executable. Forced duplicates are pinned by parity tests.
- `TreatWarningsAsErrors=true` for every project not ending in `.Tests` or `.Benchmarks`.
- No mocking frameworks. Hand-written doubles only (`tests/Scrinia.TestDoubles`).
- Nothing is pushed. Local commits only.
- **Anything found mid-branch is fixed in-branch or raised as a stop-and-ask at the moment it is
  found.** Not collected for the close. This branch exists because that rule was broken.

---

## Part 1 — Remote copy across the store boundary

### The defect

In remote mode, ephemeral memories live **client-side**: `HttpMemoryStore` holds them in an
in-process map, derived by a local `FileMemoryStore` helper rooted at the client's workspace.
Persistent memories live server-side. `HttpMemoryStore.CopyMemory`
(`src/Scrinia/HttpMemoryStore.cs:786-827`) handles only one of those facts: it special-cases
ephemeral→ephemeral, then routes *everything else* to the server's copy endpoint, stripping the
`~` at `:813` and sending a bare name. The server (`MemoryEndpoints.cs:532`) resolves that name
against its own store.

Three symptoms follow:

| Direction | Behaviour today |
|---|---|
| `~a` → `~b` | Correct. Handled client-side at `:792-810`. |
| `a` → `b` | Correct. Server-side copy of two names the server owns. |
| `~a` → `b` (promotion) | Server has never seen `~a`. Returns **400** when no persistent `a` exists — and **silently copies the wrong memory** when an unrelated persistent `a` does. |
| `a` → `~b` (demotion) | Server copies into **its own** ephemeral map. Reports success; the result is invisible to the client that asked. |

### The rule this must honour

`FileMemoryStore.cs:1898` states the local semantic in the code:

> A copy carries; it does not re-derive. The artifact bytes are identical by definition, so the
> derived metadata is identical — carrying it is correct by construction, and re-deriving would
> re-hash files against the current workspace and could produce an entry that differs from its
> byte-identical source.

This rules out the cheap fix. Promoting by `POST /memories` and letting the server derive would
recompute `FileHashes` against the **server's** filesystem — only `FileMemoryStore` passes a
`fileHasher` (`:1171`, `:1321`), and the server's store is one. A memory referencing `src/Foo.cs`
on the client would be promoted with an empty or wrong drift baseline, silently. `PATCH` cannot
fill the gap either: it rejects keyword changes by design, because keywords derive from content the
metadata endpoint does not have.

So the wire must be able to carry a fully-derived entry.

### The wire change

A **new route**, alongside the existing one. `CopyRequest`
(`src/Scrinia.Server/Models/ApiDtos.cs:144-146`) and `POST /memories/{name}/copy` are untouched:

```
POST /api/v1/stores/{store}/memories/{name}/copy-from-client
```

```csharp
/// <summary>The metadata a client-side source carries to a destination the server owns: exactly
/// the fields the server can neither derive from the artifact nor compute from the destination
/// name. Name, QualifiedName and Scope are the destination's; ChunkCount is Content.Length;
/// CreatedAt is stamped on arrival. None of them appear here, so none of them can disagree with
/// the request that carries them.</summary>
public sealed record CarriedEntry(
    long OriginalBytes,
    DateTimeOffset? UpdatedAt,
    string Description,
    Dictionary<string, string[]>? Attributes,
    string[]? Keywords,
    Dictionary<string, int>? TermFrequencies,
    ChunkEntryDto[]? ChunkEntries,
    Dictionary<string, string>? FileHashes,
    string[]? AgentKeywords = null);

/// <summary>A copy whose source the server cannot resolve, because it lives only on the client:
/// the caller supplies the artifact and the metadata it already derived. "Store this exact entry
/// from this exact artifact; do not derive." Used for ephemeral→persistent promotion.</summary>
public sealed record CopyFromClientRequest(
    string[] Content,
    CarriedEntry Entry,
    bool Overwrite = false);
```

**Why a dedicated shape rather than reusing `MemoryEntry`.** `MemoryEntry` carries `ChunkCount`,
which would sit on the request beside the `Content` it must agree with — and `Name`,
`QualifiedName`, `Scope` and `CreatedAt`, which the server ignores or recomputes. `ApiDtos.cs:109`
states the rule this would break: *"ChunkCount is deleted, not retained: it is `Content.Length`, and
a derivable field that can disagree with its payload is the defect this shape fixes"*, and `:23-26`
says the same for `StoreResponse`. Omitting them makes the disagreement **unrepresentable** rather
than validated — the pattern `AppendRequest` already uses (`:32-36`).

**Why a route and not an optional field on `CopyRequest`.** An older server receiving an unknown
optional field ignores it, falls through to resolving `{name}` in its own store, and silently copies
the wrong memory while reporting success — reintroducing the exact defect this part removes, through
version skew. An unknown *route* 404s: loud, and the client can report it. The failure mode being
fixed must not be reachable by a deployment mismatch.

**`{name}` is the DESTINATION here, unlike `/copy`, where it is the source.** There is no server-side
source to name — that is the whole reason this route exists. The asymmetry is deliberate but is a
genuine misreading hazard, so it is called out in the route's own doc comment, in the handler, and in
the client method that calls it.

The client mirror lives in `src/Scrinia/HttpMemoryStore.cs` beside `CopyApiRequest` (`:184`), reusing
its own mirrors of both records. Both need entries in `HttpStoreJsonContext` (`:122`) for AOT
serialization. The client projects its `EphemeralEntry` into the carried shape; it does **not** reuse
`internal sealed record MemoryEntry` (`:153-165`), for the reason above.

### Server validation

The governing principle is the local one: **carry, do not re-derive.** The server writes what it is
given, changing only what identifies the destination. This mirrors `FileMemoryStore.cs:1905-1919`,
which carries `OriginalBytes` and `ChunkCount` verbatim from the source entry.

- `Name`, `QualifiedName`, `Scope` and `Uri` come from the **destination**. They are not on the
  request at all, so there is nothing to trust or reject.
- `ChunkCount` is `Content.Length`. Not on the request either.
- `CreatedAt` is **stamped server-side** — a copy is created now, matching `FileMemoryStore.cs:1903`,
  which deliberately does not carry it. `UpdatedAt` **is** carried.
- Everything the request does carry — `OriginalBytes`, `Description`, `Keywords`, `AgentKeywords`,
  `TermFrequencies`, `Attributes`, `ChunkEntries`, `FileHashes` — is written verbatim. Recomputing
  even the content-derived ones would reintroduce the divergence risk this design exists to avoid,
  and `FileHashes` cannot be recomputed at all: they describe the client's workspace, which is where
  the content was written about. The promotion test asserts they arrive intact.

Because a client-supplied entry crosses a trust boundary the local path does not, the endpoint adds
checks the local copy has no need for. Each failure is a 400 naming the specific problem:

- `Content` must be non-empty, and the destination `{name}` must satisfy the same `MaxNameLength`
  bound `RememberMemory` enforces.
- `MaxContentBytes` (5 MB per element, `MemoryEndpoints.cs:41`) applies to `Content`, matching
  `RememberMemory:67`.
- The existing `copy` permission check and `IsWritable` store check are unchanged and run first.

**`OriginalBytes` is derived, not carried — corrected after Task 3's review.** An earlier draft of
this section had it carried, on the stated grounds that "the relationship between a memory's
original byte count and its decoded chunks is not an invariant this design verified". It is one, and
the encoder computes it: `Nmp2ChunkedEncoder.cs:201-205` sums the chunks' UTF-8 byte lengths and
writes the total into the artifact header at `:220`. The claim was unverified, not unverifiable.

Carrying it therefore broke this section's own rule twice over — it let the request state a byte
count the server *knows* to be wrong, and it exempted a derivable field from the very principle
applied to `ChunkCount` one line above.

So `OriginalBytes` leaves `CarriedEntry` entirely, and the endpoint computes it from the content it
is already encoding. `CarriedMetadata` keeps the member: the store takes an already-encoded artifact
and cannot recompute it without parsing the header, and the in-process promotion path has the
authoritative value in hand. The wire is where the untrusted number was, and the wire is where it
stops.

### Client dispatch

`HttpMemoryStore.CopyMemory` replaces its two-branch shape with the full four-way split:

- **`~a` → `~b`** — unchanged, client-local.
- **`a` → `b`** — unchanged, `POST /memories/{a}/copy` with `{ Destination = b }`.
- **`~a` → `b`** — read the local `EphemeralEntry`; if absent, fail client-side **without sending a
  request**. Otherwise `POST /memories/{b}/copy-from-client` carrying the artifact chunks and the
  entry projected to the carried shape. This route performs **no name lookup on the server at all**,
  which is what removes the collision: there is no resolution step left to land on the wrong memory.
  A 404 means the server predates this route — surfaced to the caller as an explicit
  "this server does not support promoting an ephemeral", never as a copy that appeared to work.
- **`a` → `~b`** — no wire change needed. `GET /memories/{a}/entry` and `GET /memories/{a}` already
  return the entry and the content. Construct an `EphemeralEntry` and write it through the existing
  local path. Honour `overwrite` against the client's own ephemeral map.

### Store surface

The server needs to write an artifact plus a caller-supplied entry without deriving. `FileMemoryStore`
already does exactly this inline at `:1895-1919` (write artifact, construct `ArtifactEntry`,
`Upsert`). That block is lifted into a named member on `ILocalMemoryStore` so the endpoint and
`CopyMemory` share one implementation rather than growing a second. The plan names it and its exact
signature; the requirement here is that **one** implementation exists.

### What this does not do

It does not introduce a resolvable `remote://` address. The root cause of the wrong-memory symptom
is that a name on the wire carries no scope, which is exactly the E2 addressing design's problem
statement. This branch fixes the copy path narrowly and correctly; E2 generalises the addressing
later, and a scheme that can name a client-side ephemeral unambiguously should retire the
`copy-from-client` route rather than sit beside it.

---

## Part 2 — Fence-aware reference extraction

### The defect

`remember` and `append` describe extraction as skipping fenced code blocks for some routes.
`ReferenceExtractor` (`src/Scrinia.Core/Search/ReferenceExtractor.cs`) has no fence state at all:
`FilePathPattern` (`:35`), `MemoryNamePattern` (`:41`) and `EphemeralNamePattern` (`:45`) are bare
regexes run over the whole string by `ExtractFileRefs` (`:49`) and `ExtractMemoryRefs` (`:62`).

Paste a fenced build log and every path in it becomes a `file` attribute — and, since
`derivation-merge-residuals` extended the drift baseline to appends and ephemerals, gets hashed
into that baseline too.

### The machinery exists, but in two disagreeing copies

`AttributeExtractor` scans line by line **twice**, and the two loops do not apply the same rule:

| | `Extract` (`:53`) | `ExtractMentionSpans` (`:200`) |
|---|---|---|
| Fenced blocks | skipped (`:71`) | skipped (`:213`) |
| YAML frontmatter | **read** — no skip exists | skipped (`:222`) |
| Inline-code spans | **not stripped** | stripped (`:224`) |

`Extract` reading frontmatter is deliberate, not an oversight: the class doc (`:7`) names frontmatter
as the **first** source of attributes, which is where `key: value` metadata legitimately lives. It
tracks `inFrontmatter` only to know when a later `---` closes the block.

So there is no single existing rule to copy. There is a duplicated loop and a divergence.

### The design

A new unit in `Scrinia.Core.Search` owns the single question *"which parts of this content are
prose?"* and excludes **fenced blocks only**. Both of `AttributeExtractor`'s loops adopt it, which
removes the duplication *and* the divergence; all three of `ReferenceExtractor`'s patterns run over
prose only.

**Why fences only.** A fenced block is bulk machine output — a pasted build log, a diff, a config
dump — that nobody is citing. Everything else the author typed on purpose:

- **Frontmatter is prose.** It is deliberate structured metadata, and `Extract` has always read it.
- **Inline-code spans are prose.** A backtick-quoted `` `[[/api/auth]]` `` or `` `~scratch` ``
  extracts today and must keep doing so, and inline code is where mention spans find real entity
  names. Excluding it would drop deliberate references.

**A correction, because the first draft of this section got it wrong.** That draft justified the
choice by claiming `` `src/Foo.cs` `` is the commonest way a person cites a file and that excluding
inline code would drop it. **`ExtractFileRefs` has never extracted a backtick-quoted path and will
not after this change** — `FilePathPattern` (`ReferenceExtractor.cs:35`) carries the backtick inside
its lookbehind exclusion class, and every interior position of the token is preceded by a word
character, `/` or `.`, so no restart position matches either. That is deliberate: `:10-16` documents
that narrowing the lookbehind *shifts* the match rather than suppressing it, turning
`file:src/new.cs` into the corrupt `rc/new.cs`, and `ReferenceTests.cs:49-64` pins it. **Do not widen
the lookbehind to "fix" this.** A backtick-quoted path reaches attributes only when written
`` file:`src/a.cs` ``, which `StripReferenceWrappers` (`:428-431`) unwraps.

The decision stands on the two surviving reasons: frontmatter, and backtick-quoted *memory* refs
plus mention spans.

**This reconciles the two loops rather than preserving both.** Moving them onto the mention-span set
instead would stop `Extract` reading frontmatter, destroying a documented attribute source; moving
onto fences-only is additive. The cost is real and is measured rather than assumed: mention spans
will now also come from frontmatter and inline code, and those feed the entity graph. The
implementation measures that growth against the real corpus before adopting it, and stops if it is
large — `KnownAttributes.StateMarkerKeys` documents what a flood of low-value entity nodes does.

**`~~~` is not recognised**, inherited from the loops being replaced and left alone deliberately.
Widening it is a behaviour change with its own blast radius, not a tidy-up. The gap is recorded in
the unit's own doc comment so the next reader does not mistake it for an oversight.

### Three consequences the falsification pass surfaced

**Auto-linking and orphan marking change.** `MaintenanceEventSink.cs:100` calls
`ExtractMemoryRefs` directly — not through `ExtractRefAttributes` — on **every** store (`:62`) and
**every** append (`:74`). So a `[[wiki-link]]` inside a fenced block will no longer link its target
back, and a memory whose only inbound reference lived inside a fence can now be marked
`orphan:true`. That is correct under the new rule and it is a user-visible behaviour change on a
write path; it goes in the CHANGELOG.

**`MemoryNamePattern` can match across a newline.** `\s*` matches `\n`, so
`[[\n/api/auth\n]]` extracts today. Wiki-link matching therefore runs over the prose lines
**rejoined** (`string.Join("\n", ProseLines(content))`), not line by line — preserving cross-line
matches while still excluding fenced blocks. The other two patterns cannot span a newline and run
per line.

**Dropping `StripInlineCode` splits mention spans, it does not only add them.** The strip replaces
backticks and their contents with spaces, and whitespace does not break a run while other
non-word characters do. So `` Alpha `x` Beta `` is the single span `alpha beta` today and becomes
two spans. That changes entity-graph node **identity**, not merely node count, which is why the
implementation measures the symmetric difference of the mention sets rather than two totals.

### Convergence is asymmetric, and stated plainly

- **`remember()` converges for free.** A re-store re-derives from full content, so the memory picks
  up the new rule on its next write.
- **`append()` does not, and must not be made to.** Three ruled invariants block it, and the spec
  keeps all three:
  - `MemoryDerivation.cs:238` extracts from the appended chunk only (D-22).
  - `MemoryDerivation.cs:243` merges through `KnownAttributes.Merge`, where `file` and `ref` are
    multi-valued, so attributes only ever grow.
  - `MemoryDerivation.cs:273` carries the drift baseline forward and adds to it (D-21); `skip`
    exists specifically to prevent re-hashing.

  The blocker is the second. To drop a `file` value minted from a fence, the code would have to
  know that value came from extraction rather than from a caller's explicit `attributes` argument —
  and it cannot. Provenance is tracked for keywords (`AgentKeywords`) but **not for attribute
  values**; both land in the same array under the same key. Converging on append would either
  delete curated attributes alongside stale ones, or require an attribute-value provenance model,
  which is a larger design than the residual it fixes.

  **Consequence, documented rather than fixed:** a memory that is only ever appended to keeps its
  fence-minted attributes and their drift baselines until it is re-stored.

### Tool descriptions

`MemoryTools.cs:112`, `:124`, `:267` and `:277` currently enumerate which routes skip fences and
state that file paths are extracted "over the WHOLE content — fenced blocks and frontmatter
included." Once extraction is uniform, that collapses to a single rule covering every route. The
descriptions are user-facing surface for LLM callers, so they state the new rule and the
append-convergence caveat in plain terms.

---

## Part 3 — Correctness and hygiene

### 3.1 `link` and `UpdateMeta` on ephemerals

`UpdateMeta` (`MemoryTools.Core.cs:305`) refuses ephemerals at `:315-319` because it writes through
`TryUpdateEntry`, which operates on the index and `ArtifactEntry`; ephemerals live in a separate
map. `Link` (`:1000`) therefore refuses when either side is ephemeral (`:1014-1018`).

The data model is no longer the obstacle — `EphemeralEntry` carries `Attributes`, and since the last
branch, `FileHashes`. What is missing is a metadata write path.

`UpdateMeta` gains an ephemeral branch: load the `EphemeralEntry`, apply the same
added-keywords/added-attributes rule, and write back through the existing ephemeral write path.

**No derivation rule changes, and none is duplicated.** `UpdateMeta`'s mutation is already a lambda
over `ArtifactEntry` handed to `store.TryUpdateEntry` (`:338`), and
`MemoryDerivation.ForMetadataUpdate` (`:418`) already takes and returns `ArtifactEntry`. So the
ephemeral path projects `EphemeralEntry → ArtifactEntry`, runs **the same** mutation and **the same**
`ForMetadataUpdate`, and projects back — reusing both unchanged.

The lambda body moves into a named private method so the two paths call one implementation rather
than a copy. That is the whole structural change.

Reusing `ForMetadataUpdate` untouched also preserves its no-op contract for free: `:416-417`
documents that an update with nothing seed-worthy returns `updated` unchanged *including the same
`TermFrequencies` reference*, which the PATCH round-trip depends on. Rewriting it to serve two entry
types would have put that contract at risk for no gain.

Applying `ForMetadataUpdate` on the ephemeral path is required, not optional: without it an
ephemeral's term frequencies would diverge from a persistent memory's given identical input — a
store-mode divergence, the class this codebase keeps removing.

`Link`'s pre-check at `:1014` then narrows from "either side is ephemeral" to "either side does not
exist". The both-sides-checked-before-either-is-written property at `:1006-1012` is load-bearing and
stays: a partial link leaves a dangling one-sided `ref`.

This works identically in local and remote mode with no wire change, because ephemerals are
client-side in both.

`update()` is not a public MCP tool (see 3.4), so this surfaces to callers through `link()` alone.
Making `UpdateMeta` ephemeral-capable is still the right shape: two write paths where one belongs is
how the four derivation implementations drifted in the first place.

### 3.2 `ReadStrings` escalates on malformed input

`ReadStrings` (`MemoryTools.Lifecycle.cs:677`) is documented at `:675-676` as returning "null when
the key is absent **or is not an array**." Every other sidecar field now refuses rather than guesses
when the JSON is malformed. This one degrades quietly, conflating two different facts.

The fix separates them:

- **Absent** → null. This is load-bearing: for `agentKeywords`, absent means UNKNOWN provenance, and
  `MemoryDerivation` depends on that distinction.
- **Present but not an array — including an explicit JSON `null`** → escalate, matching the
  timestamp and term-frequency rules ("refuse rather than guess").

**An earlier draft of this section said explicit `null` should read as absent, and justified it as
matching the sibling rules. That was backwards.** `TryReadTimestampString` (`:712-713`) and
`TryReadIntMap` (`:741-745`) both classify an explicit JSON `null` as MALFORMED, each saying so in
its own comment, and three green tests pin it —
`ReconcileMetaProvenanceTests.cs:350`, `:382`, and `SidecarTimestampMergeParityTests.cs:96-157`.
Reading `keywords: null` as absent would leave one file with two null policies, and would let a
foreign writer's explicit `null` be silently read as UNKNOWN provenance — the very distinction this
section calls load-bearing.

**Implementation note, from two parity gaps on the last branch:** use `TryGetPropertyValue`, not the
`node?[key]` indexer. The indexer returns a null reference for *both* "key absent" and "key present
with value JSON `null`", because System.Text.Json represents a JSON null as a null node. Only
`TryGetPropertyValue` distinguishes them.

Both mergers behave the same today, so this is a consistency gap rather than a divergence — but the
`Scrinia.Merge` copy must move with it, pinned by the existing parity suite.

### 3.3 Balance-blind punctuation trim — ALREADY FIXED, no work

The recorded residual says `thing:Foo(bar)` trims to `Foo(bar`. **It does not.**
`TrimTrailingUnbalancedPunctuation` (`AttributeExtractor.cs:448`) strips a closing bracket only when
the value holds more of that closer than its matching opener (`:460`), and a quote or backtick only
when its running count is odd (`:467`). `Foo(bar)` has one of each, so it survives untouched.

Verified by reading the method, not its comment — the comment at `:383-385` makes the same claim, and
a claim traceable to a comment is presumptively wrong until the code is read.

The fix landed during the previous branch's final review round; the residual line was written before
it and never updated. **Third stale residual**, after `link`'s refusal and term-frequency drift.
Closed with no code change.

### 3.4 Two comments assert a tool that does not exist

`UpdateMeta` is `internal`, is called by no CLI command, and is reachable only through `Link`'s two
hardcoded `ref:` calls. There is no public `update()` MCP tool. Two comments say otherwise:

- `tests/Scrinia.Tests/StoreBoundaryTests.cs:26` — "the MCP tools that need a metadata-only write —
  `update()` and `link()`".
- `tests/Scrinia.Tests/RemoteStoreToolTests.cs:730` — "reached by `update()` and by both halves of
  `link()`".

Both are corrected to describe the real caller graph. The surrounding claims about `TryUpdateEntry`
and `PATCH` are accurate and stay.

**There are 21 sites, not 2**, and four of them ship in production source —
`MemoryTools.Core.cs:377` and `:430`, `MemoryDerivation.cs:549`, `MemoryEndpoints.cs:252`. The one at
`:430` contradicts itself: `:428` says "reachable only through this one tool" three lines above.
Five more are in the still-editable `## [Unreleased]` CHANGELOG block, including a section heading.
All 21 are corrected together. Fixing two while committing "claims the code does not support" would
leave nineteen behind and read as closure.

The replacement wording must say **"no caller outside `Link` and the tests"**, not "reachable only
through `Link`": tests call `UpdateMeta` directly at fourteen sites, and an implementer who believes
the tests cannot see it will widen its behaviour unguarded.

### 3.5 `compact`'s archive prose — seven sites

**The behaviour is already precise; only the prose is not.** `MemoryTools.Lifecycle.cs:954` emits
`Original archived.` only when `hadIndexedEntry`, so the runtime message never over-claims. The flag
is false for **two** reasons: the memory is ephemeral — the common, user-reachable case, set
unconditionally at `:888` — and, rarely, a persistent artifact with no index entry.

**Seven sites carry the unconditional claim**, and four of them are user-facing:

| Site | |
|---|---|
| `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:777` | internal doc comment |
| `src/Scrinia/Commands/ScriniaCommands.Memory.cs:535` | **the `scri compact --help` text** |
| `src/Scrinia.Mcp/prompts/guide.md:94` | **what `guide()` returns to every agent at session start** |
| `README.md:201`, `AGENTS.md:83`, `docs/cli-reference.md:162` | user-facing tables and reference |
| `src/Scrinia.Mcp/MemoryTools.cs:342-344` | already conditional, but names only the ephemeral case |

All seven are corrected in one commit. An earlier draft of this section named two and diagnosed the
rare cause while omitting the common one — which would have left the ephemeral carve-out at one site
and the unconditional claim at five, ending **more** divergent than it started.

### 3.6 Two vacuous tests

- `tests/Scrinia.Tests/WriterCutoverTests.cs:215` asserts only `Attributes` while the code path it
  names dropped four fields.
- `tests/Scrinia.Tests/FileMemoryStoreTests.cs:189` asserts only that the destination exists.

Both are widened to assert what their names claim. Each must then fail under an isolated
single-step revert of the behaviour it pins.

### 3.7 `KeywordJaccard`'s unreachable branch

`Compute` (`src/Scrinia.Core/Merge/KeywordJaccard.cs:35`) ends with `union == 0 ? 1.0 : …`. By that
line both sides are non-empty — `:28-29` returned for every empty combination — so `setA.Count >= 1`
and `union >= 1`. The branch cannot be taken.

Removed from **both** copies identically, with `KeywordJaccardParityTests` staying green. The two
empty-side cases at `:28-29` keep their existing tests: they are the reachable behaviour the dead
branch was mistaken for.

---

## Part 4 — Scope segments are sanitized (added after Task 3's review)

`ParseQualifiedName` (`FileMemoryStore.cs:243-254`) sanitizes **only the leaf**. `:253` runs
`SanitizeName(segments[^1].Value)`; `:251` joins the remaining segments **raw** into the scope
string. `SanitizeNameCore` maps `* ? < > | "` and `:` to `_` — and never sees them.

Two consequences, both verified against the code and by probing on Windows/NTFS:

**Names that should be refused reach the filesystem.** `/a*b/x` produces scope `local:a*b`, which
`Directory.CreateDirectory` rejects with `IOException`; `/NUL/x` throws `DirectoryNotFoundException`.
Task 3 catches both at the endpoint, so they are 400s rather than 500s — but only on that route.

**Distinct scope strings alias to one directory.** `local:topic.`, `local:topic ` and `local:topic`
are three different strings — three different `_indexCache` keys and three different per-scope lock
entries — that resolve to the same directory, because Windows trims trailing dots and spaces from
path components. `local:.` resolves to the bare `local` store directory while indexing under scope
`"local:."`. **Two writers to one directory, holding different locks**, is a correctness defect that
no endpoint-level guard can reach.

### The fix, and the question it raises

Sanitize the scope segments the same way the leaf is sanitized, and reject the forms sanitization
cannot repair — trailing dots and spaces, bare `.`, and reserved device names. Every input in the
table above then becomes the `ArgumentException` at `SanitizeNameCore:291` that callers already
handle, and the aliasing disappears because the three strings collapse to one.

**This is pre-existing and shared.** `RememberMemory` reaches `ParseQualifiedName` through
`StoreRequest.Name` with the same exposure; so does every local `remember()`. It is not a residual
of `derivation-merge-residuals`, and it is not something Task 3 introduced — Task 3 is only what
made one axis of it reachable by an untrusted caller.

**It changes where an existing memory resolves.** A memory stored today under a scope containing one
of these characters would, after the change, parse to a different scope string — a different index
cache key and a different directory. The design must state what happens to it: whether such a
memory can exist in practice (the write paths that could create one, and whether any did), and
whether anything is needed beyond the sanitization itself. That question is the reason this is its
own task rather than a line in Task 3.

## Testing

**Remote copy.** All four directions, in both local and remote mode. The promotion test asserts the
**client's** `FileHashes` arrive intact — that is the whole point of the carry rule, and a test that
only checks the copy exists would pass while the baseline was silently re-derived. One test per
server validation rejection. One test that `POST /copy` — the existing route — still behaves exactly
as it does today, since persistent→persistent must not shift. One test that a 404 from
`copy-from-client` surfaces as an explicit refusal rather than a reported success.

Nothing tests `HttpMemoryStore.CopyMemory` today — the whole 42-line method is uncovered, including
the two branches this design leaves unchanged. Task coverage must therefore include
persistent→persistent, which is otherwise a modified branch with no net beneath it.

**Fence-awareness.** Extraction from ordinary prose must be **identical to pre-branch output**, not
merely "correct" — comparing against expectations rather than against prior behaviour is how the
reference-extraction regex silently dropped drift baselines three times on the last branch. The
pinned fixture must contain the cases that would otherwise slip past: a line-wrapped
`[[\n/api/auth\n]]`, a backtick-quoted path (pinned as **absent** — it never extracted), a
backtick-quoted `[[link]]` and `~ephemeral` (pinned as **present**), and an indented fence (pinned
as **not** a fence, since `StartsWith("```")` has no leading-whitespace trim).

Four existing reference tests are pure negatives (`ReferenceTests.cs:37`, `:103`, `:171`, and `:113`
which uses `Contain` rather than `Equal`). They pass if extraction returns nothing at all — no
protection against exactly the failure this change can introduce. Each gains a positive half.

**Hygiene.** `link` across all four ephemeral/persistent combinations, including one in **remote**
mode — ephemerals are client-side in both, so it is cheap and it is the only thing that proves the
"identically in local and remote mode" claim. `ReadStrings` for absent, JSON `null`, malformed
non-array, and well-formed, each with `.scrinia/merge.config` set to `{"jaccardThreshold":0.0}` —
without it the admission gate refuses every fixture before the read is reached. Both mergers'
JSON reads need a **matched pair** of end-to-end cases: there is no existing parity suite covering
them, because both helpers are private in projects that cannot reference each other.

**Vacuous-test discipline.** Every new test is proved by an **isolated single-step revert** of the
one step it names — not a revert of the whole fix, which can leave a test passing for the wrong
reason.

## Out of scope

- **E2 (addressable artifacts) and E3 (remote scoring surface).** Still unspecced design efforts.
- **Attribute-value provenance.** Required for append-time convergence; deliberately not started.
- **`~~~` fences.** Excluded to keep exact parity with `AttributeExtractor`.
- **Term-frequency drift.** Closed as ruled, not reopened.
- **Group G's two unverified items** — the `WorkspaceRoot` layout-migration latch and the `WithInfo`
  prepend/append ordering. Verified by whichever branch touches their area.
