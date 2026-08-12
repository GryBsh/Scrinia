# Store-Owned Derivation, and Remote Wire Parity — Design

**Status:** approved 2026-08-11, ready for an implementation plan.

Closes **E4** and **E5** of `docs/superpowers/specs/2026-08-10-residuals-closure.md`, and the two
divergences that document files as permanent and not in any group — **BM25 term frequencies differ
by store mode** and **MCP and REST disagree on a bare date in a `review` attribute**. Those two are
closed by this design rather than documented, because the cause it removes is what produced them.

**The goal is that MCP and HTTP are effectively the same.** Not "the same where convenient" — an
accepted difference between the two paths is the defect this spec exists to make impossible.

---

## The problem

### One: derivation is implemented four times

Deriving a memory's metadata — auto keywords, term frequencies, chunk entries, content preview — is
not a shared routine. It is written out in two files, twice each:

| Path | MCP (`src/Scrinia.Mcp/MemoryTools.Core.cs`) | Server (`src/Scrinia.Server/Services/MemoryOrchestrator.cs`) |
|---|---|---|
| store | `:141` `AnalyzeText` → `:146` `MergeKeywordsWithSource` → `:204` `ComputeChunkEntries` | `:22` `AnalyzeText` → `:27` `MergeKeywordsWithSource` → `:68` `ComputeChunkEntries` |
| append | `:1322` `AnalyzeText` → `:1333` `MergeKeywordsWithSource` | `:269` `AnalyzeText` → `:270` `MergeKeywords` |
| compact | `:1147` `AnalyzeText` | `:283` `AnalyzeText` |

**They have already drifted.** On append the two differ in three ways:

| | MCP (`:1322-1341`) | Server (`:269-275`) |
|---|---|---|
| Agent keywords | curated + caller additions | **`null`** |
| TF boost | agent `+5`, auto `+2` | everything `+2` |
| Boost target | `IndexTokensForKeyword(kw)` | the raw `kw` |

The server's append does not even match the server's own store path, which uses
`MergeKeywordsWithSource` at `:27`. Of four implementations of one contract, it is the outlier.

**The BM25 divergence has two independent causes, and only one was recorded.**

The recorded one is real and correctly diagnosed, in a comment at
`src/Scrinia.Server/Services/MemoryOrchestrator.cs:57-66`: locally `SeedTermFrequencies` runs
*before* the carrier merge so carrier keys are never seeded, while over the wire those keys arrive
inside one flat map and get seeded here — and a flat map cannot distinguish an agent-supplied
attribute from a carrier one. That comment names one possible fix: *"sending the two maps
separately, which is a wire-shape change, not a reordering here."*

**This design takes the other fix, deliberately.** `MemoryWrite` carries a single flat `Attributes`
map, so MCP's split is not expressible under it — the divergence closes by making both paths adopt
the server's order (assemble the complete map, then seed once, last) rather than by preserving
local's. Ruled 2026-08-12 after the Task 1 inventory surfaced the contradiction; recorded as D-08 in
`docs/superpowers/analysis/2026-08-11-derivation-inventory.md`.

**This is a deliberate, user-visible ranking change.** Values under `tag`, `review` and `file`
currently seed `0` locally and `+3` remotely; after, `+3` on both. `anchor` seeds `+2` locally,
`+3` remotely; after, `+3` on both. Term frequencies are persisted rather than recomputed at query
time, so a memory's ranking shifts the first time it is rewritten, not on deploy. The semantic being
dropped is weakly held: MCP's own comment says a curated anchor's real boost lives in the
attribute-score branch at `+15`, not in term frequency. The rejected alternative — a second map on
`MemoryWrite` — would encode a legacy internal distinction on the wire that no REST client could
sensibly populate.

The second cause is undocumented and lives on a different path: the append drift in the table above.
The server is not failing to distinguish agent terms there — it passes `null` and does not try.

They are separate faults with separate mechanisms, and one design closes both, because both exist
only because the derivation exists more than once. (An earlier draft of this spec claimed the
recorded explanation was false. It was not — it was a correct account of the store path that says
nothing about append.)

### Two: the wire loses things the local path keeps

**Chunk structure.** `MemoryOrchestrator.ShowAsync` decodes the artifact to joined text and reports
the chunk count as a separate field (`:427`). The client then discards that count and re-encodes the
joined text as a **single chunk** (`src/Scrinia/HttpMemoryStore.cs:278`):

```csharp
// Re-encode the decoded content back to NMP/2 for callers that need it
return Nmp2ChunkedEncoder.Encode(show.Content);
```

`Encode`, not `EncodeChunks`. A five-chunk memory arrives as one chunk while the response says
`chunkCount: 5`. `recall({ chunk: 3 })` (`src/Scrinia.Mcp/MemoryTools.Core.cs:90`) then fails.

Two corrections to E5's original ruling, both from reading the code: this is **not** a missing
capability — `GET /memories/{name}/chunks/{i:int}` exists and works
(`src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:27`, handler `:247`) and the client declares a DTO
for it and never calls it. And chunk **content** and chunk **metadata** were conflated; they are
separate concerns.

**Entry completeness.** `ArtifactEntry` has twelve fields
(`src/Scrinia.Core/Models/ArtifactEntry.cs:3-31`); `MemoryListItem`
(`src/Scrinia.Server/Models/ApiDtos.cs:34`) carries eight. Missing: `TermFrequencies`,
`ChunkEntries`, `FileHashes`. So a remote `ArtifactEntry` is a partial lie, and E4's PATCH would
make a partial read feed a full write — nulling BM25 weights, chunk scoring data and the `file`
attribute's drift baseline.

### The seam already has a workaround in it

`FileMemoryStore.WriteArtifactAsync` takes an `ArtifactWriteMetadata? meta` it deliberately ignores
(`src/Scrinia.Core/FileMemoryStore.cs:1141-1147`): *"The parameter exists for HttpMemoryStore, whose
Upsert is a no-op because the server owns persistence — for it, this call is the only channel the
metadata has."* That parameter exists because callers derive and then have to smuggle the result
past a store that cannot accept it. This design removes the reason for it.

---

## The principle

**A store takes raw inputs and returns a derived entry. Callers never derive.**

Who computes then has exactly one answer in every configuration, including the two that made the
earlier framing wrong:

| Configuration | Who derives |
|---|---|
| MCP + `FileMemoryStore` (no server exists) | the local store |
| MCP + `HttpMemoryStore` | the server, via one POST; the client derives nothing |
| MCP hosted on the server (`/mcp`) | that server's local store |
| REST client → server | the server's local store |

This follows the precedent `ILocalMemoryStore` already set in this codebase: the split exists *"so
that 'this operation needs local files' is a compile-time fact."* Here, "this component does not
derive" becomes a structural fact rather than a convention someone must remember.

---

## Decisions

**D1 — The store owns derivation.** `MemoryTools` and `MemoryOrchestrator` stop calling
`TextAnalysis`. One pipeline, in `Scrinia.Core`, invoked by the store.

**D2 — Inventory before collapse.** The four implementations are diffed and every difference
recorded as a deliberate keep-or-drop *before* any of them is deleted. The append drift above was
found by reading two of the four; the plan must not assume the remaining pairs are equivalent. This
is the first task of the plan and it produces a written table, not a verbal assurance.

**D3 — `Upsert` stays.** `TryUpdateEntry` and the event sinks legitimately persist an
already-complete entry. `Upsert` keeps serving that; `StoreAsync` becomes the only path that
*derives*. Keeping both is not a hedge — they are different operations, and conflating them is what
produced the `meta` workaround.

**D4 — Break the wire. No compatibility shims.** Nothing is deployed over HTTP.

**D5 — Bump `RemoteApiVersion.Current` from `"1"` to `"2"`**
(`src/Scrinia.Core/Resilience/RemoteApiVersion.cs:22`). Breaking response shapes with a guard
already in place and unbumped means a future client mis-parses instead of failing closed.

**D6 — One entry shape, always complete.** No `?full=true`, no light variant, no field whose null
means "not requested" in one response and "empty" in another. An earlier draft traded completeness
against `list()` payload size; that trade is exactly the accepted-difference this spec rejects.
Locally `LoadIndex` returns complete entries for free, so remotely it must too.

**D7 — Chunk content crosses as an array.** `ShowResponse.Content` becomes `string[]`, one element
per chunk. `ChunkCount` is deleted, not retained — it is `Content.Length`, and a derivable field
that can disagree with its payload is the defect being fixed.

**D8 — Optimistic concurrency on `UpdatedAt`.** Locally `TryUpdateEntry` holds the per-scope write
lock across load-mutate-persist. A remote read-then-write cannot. The client sends the `UpdatedAt`
it read; the server rejects a mismatch with `409`, which maps onto `TryUpdateEntry`'s existing
`bool`.

---

## The store write surface

```csharp
// New on IMemoryStore
Task<ArtifactEntry> StoreAsync(MemoryWrite write, CancellationToken ct = default);
Task<ArtifactEntry> AppendAsync(MemoryWrite write, CancellationToken ct = default);

public sealed record MemoryWrite(
    string Name,
    string Scope,
    string[] Content,
    string? Description = null,
    Dictionary<string, string[]>? Attributes = null,
    string[]? Keywords = null);
```

- **`FileMemoryStore`** runs the Core derivation pipeline, writes artifact and sidecar, returns the
  derived entry.
- **`HttpMemoryStore`** POSTs the raw `MemoryWrite` and returns the entry from the response. It
  derives nothing. This is what "the client defers to the server" means concretely.

`ArtifactWriteMetadata` and the `meta` parameter on `WriteArtifactAsync` are deleted — the smuggling
channel has nothing left to smuggle.

**What the callers become.** `MemoryTools.Remember` validates input, builds a `MemoryWrite`, calls
the store, formats the response. `MemoryOrchestrator.StoreAsync` calls the local store's
`StoreAsync`. Neither references `TextAnalysis`.

**Compact needs no third method.** It rewrites a memory's content wholesale, which is exactly
`StoreAsync` over the compacted text — the metadata it must preserve (attributes, keywords,
description) travels in the `MemoryWrite`. Adding a `CompactAsync` to the store surface would be a
third derivation entry point and reintroduce the problem in miniature. The D2 inventory must
confirm compact's derivation is genuinely a subset of store's; if it is not, that difference is a
recorded keep-or-drop like any other, not a reason to add a method.

---

## The wire contract

```csharp
public sealed record ShowResponse(string Name, string[] Content, long OriginalBytes);

public sealed record MemoryEntry(
    string Name, string QualifiedName, string Scope,
    int ChunkCount, long OriginalBytes,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt,
    string Description,
    Dictionary<string, string[]>? Attributes,
    string[]? Keywords,
    Dictionary<string, int>? TermFrequencies,
    ChunkEntryDto[]? ChunkEntries,
    Dictionary<string, string>? FileHashes);

public sealed record ChunkEntryDto(
    int ChunkIndex, string? ContentPreview, string[]? Keywords,
    Dictionary<string, int>? TermFrequencies);
```

`MemoryEntry` replaces `MemoryListItem` and is **always complete** — a null field means the value is
genuinely absent. `ChunkEntryDto` mirrors `Scrinia.Core.Models.ChunkEntry` as a separate wire type:
`ApiDtos.cs` references no Core model today (verified — no `using Scrinia.Core`), and keeping that
boundary lets the storage model change without dragging the wire along.

`ListResponse` (`src/Scrinia.Server/Models/ApiDtos.cs:32`) becomes
`ListResponse(MemoryEntry[] Memories, int Total)`.

**Both JSON source-generation contexts must register the new types**, or serialization fails at
runtime rather than at build: `src/Scrinia.Server/Models/ServerJsonContext.cs` (which registers
`ListResponse` at `:12`) and `HttpStoreJsonContext` in `src/Scrinia/HttpMemoryStore.cs`. The
`MemoryListItem` / `ListApiItem` registrations go.

### Endpoints

| Verb | Route | Shape |
|---|---|---|
| `GET` | `/memories` | `MemoryEntry[]`, complete |
| `GET` | `/memories/{name}/entry` | **New.** One complete `MemoryEntry`. Efficiency for the write path, which should not page a scope to mutate one entry. |
| `PATCH` | `/memories/{name}` | **New.** Body is a complete `MemoryEntry`. |
| `POST` | `/memories` | Body becomes the raw `MemoryWrite`; the server derives. **Returns the complete `MemoryEntry`**, not today's `StoreResponse` — `StoreAsync` is declared as returning `ArtifactEntry`, so the entry the server derived has to come back. `StoreResponse`'s `Message` field goes; message formatting belongs to the caller, not the wire. |
| `POST` | `/memories/{name}/append` | Same: raw inputs in, complete `MemoryEntry` out. |
| `GET` | `/memories/{name}` | `ShowResponse`, reshaped per D7. |

All under `/api/v1/stores/{store}`.

---

## The PATCH endpoint

**Permission:** `store`. No new permission — the existing set covers it.

**It fires no event sinks.** A correctness requirement: `IMemoryStore.TryUpdateEntry`'s contract
says so (`src/Scrinia.Core/IMemoryStore.cs:33`) and it is the seam the async entity-typing sink
writes back through. Route through the server's `FileMemoryStore.TryUpdateEntry` — which already
takes the per-scope write lock — and **not** through `PluginPipeline`, or a remote promotion
re-enters the sink that produced it.

**It archives no version.** Metadata-only, mirroring the local sidecar-only write.

**Bounds:** reuse the attribute key-count and value-array validator the search filter already
enforces.

| Code | When |
|---|---|
| `200` | Applied; body is the updated entry with the server's new `UpdatedAt`. |
| `400` | Bounds exceeded or malformed body. |
| `403` | Missing `store` permission, or store read-only for this key. |
| `404` | No such memory. |
| `409` | `UpdatedAt` mismatch — concurrent edit. |

---

## Client changes

- **`ResolveArtifactAsync` (`src/Scrinia/HttpMemoryStore.cs:278`)** — `EncodeChunks(show.Content)`.
  One line; it is what makes `recall({ chunk: N })` work.
- **`TryUpdateEntry` (`:408`)** becomes a real write: GET the complete entry, run `mutate`, PATCH,
  return `true` on `200` and `false` on `404`/`409`. The deliberately-dead block and its
  do-not-delete comment (`:413` onward) are **removed** — that comment preserves a distinction that
  only mattered while no persistence channel existed. Building the channel expires it; the removal
  is the point, not collateral.
- **`StoreAsync` / `AppendAsync`** POST raw inputs. No `TextAnalysis` call anywhere in
  `src/Scrinia/`.

---

## Testing

Over a real Kestrel socket, matching `RemoteLoadIndexTests` — `HttpMemoryStore` sends synchronously
and `TestServer`'s handler throws on `Send`.

**Parity, asserted directly.** The highest-value tests compare the two paths against each other
rather than against a hand-written expectation: store identical content through MCP-with-local-store
and through MCP-with-remote-store, then assert the resulting entries are **field-for-field equal**
(modulo `Uri`, which is E2). That is the spec's thesis expressed as an assertion, and it would have
caught the append drift.

**Derivation**
- The append TF contract holds identically on both paths: agent keywords survive, agent terms get
  `+5`, auto terms `+2`, and boosts land on index tokens.
- Every difference the D2 inventory recorded as "drop" has a test showing the surviving behaviour.

**Chunk fidelity**
- Five-chunk memory: `GetChunkCount == 5` remotely, and `recall({ chunk: 3 })` returns the third
  chunk — not the first, not the whole document.
- Negative control: a one-chunk memory still round-trips, so the fix is not "always split".

**Write fidelity**
- PATCH an attribute promotion onto an entry carrying `TermFrequencies`, `ChunkEntries` and
  `FileHashes`; re-read; **all three survive unchanged.**
- `TryUpdateEntry` returns `true` and the mutation is visible on a fresh read.

**Concurrency**
- Read, mutate the server's copy underneath, PATCH the stale entry: `409`, `false`, server value
  unchanged.

**Sink containment**
- A PATCH promoting `mention:` to a typed key does not re-enter the entity-typing sink. Assert on
  sink invocation, not on the absence of a symptom.

**Contract**
- `403` without `store`; `400` on an over-bounds attribute map; `404` for an absent name.

---

## Out of scope

- **E2** (a dereferenceable `remote://` address) and **E3** (remote scoring surface). `Uri` stays a
  placeholder remotely, and it is the one field the parity test excludes.
- **`GET /memories/{name}/chunks/{i:int}`** stays. The array on `ShowResponse` makes it redundant
  for this client, but removing a working endpoint is a separate cleanup.
- Ephemeral memories remain client-side in remote mode.

---

## Risks

**The collapse loses a behaviour only one implementation had.** This is the real risk, and D2 is the
mitigation: inventory first, record each difference as a deliberate keep-or-drop, and test the
survivor. Without that step the merge is four-way guesswork.

**Blast radius.** `IMemoryStore`'s write surface, three MCP write paths, three server write paths,
and every test that constructs an entry and calls `Upsert`. Larger than the wire fix alone, and the
justification is that the wire fix alone would design DTOs around a duplication that this deletes.

**`FileHashes` crosses the wire.** Documented as private behaviour state
(`ArtifactEntry.cs:15-21`): dependency path → content hash. The path keys are already public as the
searchable `file` attribute, so this exposes digests, not paths, and only to a client authenticated
against the store that wrote them. Accepted so a full-entry write cannot destroy drift detection.
