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

Every claim below was verified against `main` at `bc7fdb3` before it was written. Two recorded
residuals did not survive that check and are corrected here rather than implemented:

- **`link` is not a silent refusal.** `MemoryTools.Core.cs:1014-1018` already returns a structured
  error with a suggested alternative, checked before either side is written. The ledger line
  describing it as silent predated that fix.
- **Term-frequency drift is a ruling, not a defect.** `SidecarTermFrequencyMerge.cs:23-26` argues
  Max explicitly: a count is a term's frequency in one memory's content, and summing inflates on
  every merge. The monotonic drift is the documented cost of the smallest answer that loses
  nothing. Closed as ruled; no code change.

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

`OriginalBytes` is deliberately **not** cross-checked against `Content`. The relationship between a
memory's original byte count and its decoded chunks is not an invariant this design verified, and
inventing one would reject valid promotions.

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

### The mechanism already exists

`AttributeExtractor` scans line by line with `inFence`, `inFrontmatter` and `firstLine` state, and
does it **twice**: at `:59-76` and again at `:208-222`. Its class doc (`:12`) and the mention-span
doc (`:196-197`) describe the exclusion set as fenced blocks, inline-code spans, and YAML
frontmatter.

So this is not new machinery. It is one scanner, currently duplicated, that one extractor never got.

### The design

A new unit in `Scrinia.Core.Search` owns the single question *"which parts of this content are
prose?"* and yields the prose regions of a document, excluding:

- fenced blocks delimited by ``` at line start
- YAML frontmatter (a `---` line as the first line, through its closing `---`)
- inline-code spans

Both of `AttributeExtractor`'s loops adopt it, removing the duplication. All three of
`ReferenceExtractor`'s patterns run over prose only.

**Exclusion parity is exact and deliberate.** `~~~` fences are *not* recognised, because
`AttributeExtractor` does not recognise them. Widening the set is a separate change; matching it
keeps one rule across every extractor and lets the tool descriptions state that rule once. The gap
is recorded in the unit's own doc comment so the next reader does not mistake it for an oversight.

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

`MemoryDerivation.ForMetadataUpdate` (`MemoryDerivation.cs:418`) is typed
`(ArtifactEntry current, ArtifactEntry updated)`, but its body touches only six field values —
`current.Attributes`, `updated.Attributes`, `current.Keywords`, `updated.Keywords`,
`updated.TermFrequencies` and `updated.AgentKeywords` — and mutates exactly two, returning
`updated with { TermFrequencies, AgentKeywords }` (`:450`). `EphemeralEntry` carries all four of
those field kinds, so the rule already fits it.

That body is lifted into a core computing the two mutated values from the six read ones. The
existing `ArtifactEntry` signature becomes a thin wrapper over it; the `EphemeralEntry` path calls
the same core. **One rule, two callers** — the shape `Scrinia.Core.Merge` uses for every shared
field rule. A second copy of the boost logic for ephemerals is precisely how the four derivation
implementations drifted.

**The no-op contract must survive the refactor.** `:416-417` documents that an update with nothing
seed-worthy returns `updated` unchanged *including the same `TermFrequencies` reference*, so a
caller can distinguish a no-op by reference identity. A core that always allocates a fresh
dictionary would break the PATCH round-trip contract that depends on it. A test pins the reference
identity, for both entry types.

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
- **Present but not an array** → escalate, matching the timestamp and term-frequency rules
  ("refuse rather than guess").

**Implementation note, from two parity gaps on the last branch:** use `TryGetPropertyValue`, not the
`node?[key]` indexer. The indexer returns a null reference for *both* "key absent" and "key present
with value JSON `null`", because System.Text.Json represents a JSON null as a null node. Only
`TryGetPropertyValue` distinguishes them.

Both mergers behave the same today, so this is a consistency gap rather than a divergence — but the
`Scrinia.Merge` copy must move with it, pinned by the existing parity suite.

### 3.3 Balance-blind punctuation trim

`AttributeExtractor`'s inline-prose route trims trailing punctuation without tracking brackets, so
`thing:Foo(bar)` yields `Foo(bar`. The trim becomes balance-aware: a closing bracket is kept when
the value contains its matching opener. File paths are unaffected — they are matched by a pattern
with its own terminator — so the change is scoped to prose-routed attribute values.

### 3.4 Two comments assert a tool that does not exist

`UpdateMeta` is `internal`, is called by no CLI command, and is reachable only through `Link`'s two
hardcoded `ref:` calls. There is no public `update()` MCP tool. Two comments say otherwise:

- `tests/Scrinia.Tests/StoreBoundaryTests.cs:26` — "the MCP tools that need a metadata-only write —
  `update()` and `link()`".
- `tests/Scrinia.Tests/RemoteStoreToolTests.cs:730` — "reached by `update()` and by both halves of
  `link()`".

Both are corrected to describe the real caller graph. The surrounding claims about `TryUpdateEntry`
and `PATCH` are accurate and stay.

### 3.5 `compact`'s archive prose — two sites

"Archives the original version" appears in **both** `MemoryTools.Lifecycle.cs:777` and
`ScriniaCommands.Memory.cs:535`. It is imprecise for the no-index-entry case. Both are corrected
together; correcting one is how the two descriptions drift.

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

## Testing

**Remote copy.** All four directions, in both local and remote mode. The promotion test asserts the
**client's** `FileHashes` arrive intact — that is the whole point of the carry rule, and a test that
only checks the copy exists would pass while the baseline was silently re-derived. One test per
server validation rejection. One test that `POST /copy` — the existing route — still behaves exactly
as it does today, since persistent→persistent must not shift. One test that a 404 from
`copy-from-client` surfaces as an explicit refusal rather than a reported success.

**Fence-awareness.** Extraction from ordinary prose must be **identical to pre-branch output**, not
merely "correct" — comparing against expectations rather than against prior behaviour is how the
reference-extraction regex silently dropped drift baselines three times on the last branch. Then:
paths inside fences, frontmatter and inline code are not extracted; the shared scanner is pinned
once and both extractors are shown to use it.

**Hygiene.** `link` across all four ephemeral/persistent combinations. `ReadStrings` for absent,
JSON `null`, malformed non-array, and well-formed. Parity suites stay green for every shared rule.

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
