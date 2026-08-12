# Remote Wire Parity (E4 + E5) — Design

**Status:** approved 2026-08-11, ready for an implementation plan.

Closes E4 and E5 of `docs/superpowers/specs/2026-08-10-residuals-closure.md`. They are one spec
because they are one problem: **the remote wire cannot carry something the local path has**, and
both halves touch the same DTOs. Splitting them would widen the same types twice and answer the
same compatibility question twice.

---

## The problem, stated precisely

**The wire contradicts itself about chunks.** `MemoryOrchestrator.ShowAsync` decodes the stored
artifact to joined plain text and reports the chunk count as a *separate field*
(`src/Scrinia.Server/Services/MemoryOrchestrator.cs:427`). The client then throws that count away
and re-encodes the joined text as a **single chunk**
(`src/Scrinia/HttpMemoryStore.cs:278`):

```csharp
// Re-encode the decoded content back to NMP/2 for callers that need it
return Nmp2ChunkedEncoder.Encode(show.Content);
```

`Encode`, not `EncodeChunks`. So a five-chunk memory arrives as a one-chunk artifact while the
response says `chunkCount: 5`. `recall({ chunk: 3 })` calls
`Nmp2ChunkedEncoder.DecodeChunk(artifact, 3)` (`src/Scrinia.Mcp/MemoryTools.Core.cs:90`) against a
one-chunk artifact and fails. The tool description promising chunk retrieval is false remotely.

Two corrections to the original E5 ruling, both found by reading the code rather than the ruling:

- This is **not** a missing capability. `GET /memories/{name}/chunks/{i:int}` exists and works
  (`src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:27`, handler at `:247`), and the client already
  declares a `ChunkApiResponse` DTO for it — and never calls it. The defect is a lossy re-encode,
  not an absent endpoint.
- Chunk **content** and chunk **metadata** (`ChunkEntry`) are separate concerns and were conflated.
  `ChunkEntry` (`src/Scrinia.Core/Models/ChunkEntry.cs`) carries per-chunk previews, keywords and
  term frequencies, and feeds `WeightedAttributeScorer`'s chunk-level scoring. Remote *search* runs
  server-side against the server's own complete entries, so that scoring is unaffected today. The
  metadata matters for client-side consumers — bundle export in particular.

**The client's entry is a partial lie.** `ArtifactEntry` has twelve fields
(`src/Scrinia.Core/Models/ArtifactEntry.cs:3-31`). `MemoryListItem`
(`src/Scrinia.Server/Models/ApiDtos.cs:34`) carries eight. Missing: `TermFrequencies`,
`ChunkEntries`, `FileHashes`.

That is why E4 cannot be built naively. `IMemoryStore.TryUpdateEntry` is
`Func<ArtifactEntry, ArtifactEntry?>` — load, mutate, persist. The moment a PATCH endpoint makes
that write for real, a client reads a **partial** entry, mutates one attribute, and sends it back:
BM25 term weights, chunk scoring data, and the `file` attribute's drift baseline are nulled
server-side. That is the same partial-read-feeding-a-full-write defect class the preceding branches
spent their length closing, and it is the reason the completeness question below is load-bearing
rather than cosmetic.

---

## Decisions

**D1 — Break the wire. No compatibility shims.** Nothing is deployed over HTTP. Response shapes
change outright rather than accreting optional fields.

**D2 — Bump `RemoteApiVersion.Current` from `"1"` to `"2"`**
(`src/Scrinia.Core/Resilience/RemoteApiVersion.cs:22`). Nothing is deployed, so nothing needs the
protection today — but this is a genuinely breaking response-shape change and the header exists for
exactly this. Without a bump, a future client meeting a v1 server mis-parses instead of failing
closed.

**D3 — Complete entry on the wire, full-entry write.** Rather than a field-scoped patch, the client
gets a complete entry and sends a complete entry back. One shape to reason about, and the client's
`ArtifactEntry` stops being partial — which removes the defect class rather than routing around it.

**D4 — Completeness lives on a single-entry read, plus an opt-in on list.** The heavy fields
(`TermFrequencies`, per-chunk term-frequency maps inside `ChunkEntries`, `FileHashes`) would make
every `list()` page carry megabytes for a call whose job is names and previews. So: a new
single-entry GET always complete, and `?full=true` on the list for bulk consumers such as bundle
export. Default list shape stays light.

**D5 — Chunk content crosses as an array.** `ShowResponse.Content` becomes `string[]`, one element
per chunk. The wire describes structure in its own terms and stays independent of NMP/2, so the
storage encoding can change without a wire change. Chunk count becomes derivable rather than a
field that can disagree with its payload.

**D6 — Optimistic concurrency on `UpdatedAt`.** Locally, `TryUpdateEntry` holds the per-scope write
lock across load-mutate-persist, and the entity-typing sink re-checks under that lock. A remote
read-then-write has an unavoidable gap. The client sends the `UpdatedAt` it read; the server rejects
a mismatch with `409`, which maps cleanly onto `TryUpdateEntry`'s existing `bool`.

---

## The wire contract

### `ShowResponse` becomes chunk-aware

```csharp
public sealed record ShowResponse(string Name, string[] Content, long OriginalBytes);
```

`ChunkCount` is **deleted**, not retained. It is `Content.Length`, and a derivable field that can
disagree with its payload is precisely the defect being fixed.

### One complete entry DTO

`MemoryListItem` is replaced by `MemoryEntry`, used by all three consumers:

```csharp
public sealed record MemoryEntry(
    string Name,
    string QualifiedName,
    string Scope,
    int ChunkCount,
    long OriginalBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    string Description,
    Dictionary<string, string[]>? Attributes,
    string[]? Keywords,
    // Heavy fields: always populated by the single-entry GET and by PATCH round trips;
    // populated on list only when ?full=true.
    Dictionary<string, int>? TermFrequencies = null,
    ChunkEntryDto[]? ChunkEntries = null,
    Dictionary<string, string>? FileHashes = null);

public sealed record ChunkEntryDto(
    int ChunkIndex,
    string? ContentPreview = null,
    string[]? Keywords = null,
    Dictionary<string, int>? TermFrequencies = null);
```

`ChunkEntryDto` mirrors `Scrinia.Core.Models.ChunkEntry`. It is a separate wire type on purpose:
`src/Scrinia.Server/Models/ApiDtos.cs` references no Core model today (verified — the file has no
`using Scrinia.Core`), and the client keeps its own DTO copies. Keeping that boundary means the
storage model can change without dragging the wire with it.

`ListResponse` (`src/Scrinia.Server/Models/ApiDtos.cs:32`) changes with it:
`ListResponse(MemoryEntry[] Memories, int Total)`.

**Both JSON source-generation contexts must be updated**, or serialization fails at runtime rather
than at build: `MemoryEntry` and `ChunkEntryDto` need `[JsonSerializable]` registrations in
`src/Scrinia.Server/Models/ServerJsonContext.cs` (which registers `ListResponse` at `:12`) and in
`HttpStoreJsonContext` in `src/Scrinia/HttpMemoryStore.cs`. The stale `MemoryListItem` /
`ListApiItem` registrations go.

**A null heavy field is ambiguous on the light list shape** — it means "not requested", not
"empty". Only the single-entry GET, the `?full=true` list, and PATCH bodies carry authoritative
values. This is the one sharp edge in the design and every consumer must respect it; see Testing.

### Endpoints

| Verb | Route | Shape |
|---|---|---|
| `GET` | `/api/v1/stores/{store}/memories` | `MemoryEntry[]`, light. `?full=true` populates heavy fields. |
| `GET` | `/api/v1/stores/{store}/memories/{name}/entry` | **New.** A single `MemoryEntry`, always complete. |
| `PATCH` | `/api/v1/stores/{store}/memories/{name}` | **New.** Body is a complete `MemoryEntry`. |
| `GET` | `/api/v1/stores/{store}/memories/{name}` | Unchanged route; `ShowResponse` reshaped per above. |

---

## The PATCH endpoint

**Permission:** `store` — it writes. No new permission; the existing set (`read`, `search`,
`store`, `forget`) covers it and adding one is unjustified.

**It fires no event sinks.** This is a correctness requirement, not a preference.
`IMemoryStore.TryUpdateEntry`'s own contract says so (`src/Scrinia.Core/IMemoryStore.cs:33`) and it
is the seam the async entity-typing sink writes promotions back through. The handler routes through
the server's `FileMemoryStore.TryUpdateEntry` — which already takes the per-scope write lock — and
**not** through `PluginPipeline`. Routing it through the pipeline would let a remote typing
promotion re-enter the typing sink that produced it.

**It archives no version.** Metadata-only, mirroring the local sidecar-only write. `ArchiveVersion`
is for content changes.

**Bounds:** the same attribute key-count and value-array limits the search filter already enforces.
Reuse that validator rather than writing a second one.

**Responses:**

| Code | When |
|---|---|
| `200` | Applied. Body is the updated `MemoryEntry`, with the server's new `UpdatedAt`. |
| `400` | Bounds exceeded, or malformed body. |
| `403` | Missing `store` permission, or the store is read-only for this key. |
| `404` | No such memory. |
| `409` | The body's `UpdatedAt` does not match the stored value — concurrent edit. |

---

## Client changes

**`ResolveArtifactAsync` (`src/Scrinia/HttpMemoryStore.cs:278`)** — `EncodeChunks(show.Content)`
instead of `Encode(show.Content)`. One line, and it is what makes `recall({ chunk: N })` work.

**`TryUpdateEntry` (`src/Scrinia/HttpMemoryStore.cs:408`)** becomes a real write: GET the complete
entry from the new single-entry route, run `mutate`, PATCH the result, return `true` on `200` and
`false` on `404`/`409`.

The deliberately-dead block and its do-not-delete comment (`:413` onward) are **removed**. That
comment exists to preserve the distinction between "not found" and "found but cannot persist" while
no persistence channel existed. This spec builds the channel, so the reason expires with it — and
the removal is the point, not collateral. It must not be left in place out of caution.

**`LoadIndex` / `ListScoped`** stay on the light shape. **Bundle export** opts into `?full=true`,
because an exported bundle that silently drops chunk metadata is the same lossy-copy defect in a
different coat.

---

## Testing

Driven over a real Kestrel socket, matching `RemoteLoadIndexTests` — `HttpMemoryStore` sends
synchronously and `TestServer`'s handler throws on `Send`.

**Chunk fidelity**
- A five-chunk memory stored remotely, read back, `GetChunkCount == 5`, and `recall({ chunk: 3 })`
  returns the third chunk's content — not the first, and not the whole document.
- Negative control: a single-chunk memory still round-trips, so the fix is not "always split".

**Write fidelity — the load-bearing guard**
- PATCH an attribute promotion onto an entry that has `TermFrequencies`, `ChunkEntries` and
  `FileHashes`, then re-read: **all three survive unchanged.** This is the regression guard for the
  entire reason D3 was chosen over a field-scoped patch. If this test does not exist, the design's
  central risk is unguarded.
- `TryUpdateEntry` returns `true` and the mutation is visible on a fresh read — the assertion the
  current fail-closed override cannot satisfy.

**Concurrency**
- Read an entry, mutate the server's copy underneath, then PATCH the stale one: `409`, and
  `TryUpdateEntry` returns `false` with the server's value unchanged.

**Sink containment**
- A PATCH that promotes `mention:` to a typed key does **not** re-enter the entity-typing sink.
  Assert on sink invocation, not on the absence of a symptom.

**Contract**
- `403` without `store` permission; `400` on an over-bounds attribute map; `404` for an absent name.
- The light list shape omits heavy fields; `?full=true` populates them. A consumer must not read a
  null heavy field from a light response as "empty".

---

## Out of scope

- **E2** (addressable artifacts) and **E3** (remote scoring surface) — their own specs.
- **`GET /memories/{name}/chunks/{i:int}`** stays. The array on `ShowResponse` makes it redundant
  for this client, but removing a working endpoint is a separate cleanup with its own blast radius.
- **`Uri`** stays a placeholder remotely. Making it dereferenceable is exactly E2.

## Risks

**The light-list ambiguity (D4).** A null heavy field means "not requested" on a light response and
"genuinely empty" on a complete one. The design accepts this in exchange for a usable `list()`. The
mitigation is that only the two complete shapes are authoritative, and the one place it could cause
real damage — the write path — always reads the complete single-entry route.

**`FileHashes` crosses the wire.** It is documented as private behaviour state
(`ArtifactEntry.cs:15-21`): dependency path → content hash. The path keys are already public as the
searchable `file` attribute, so this exposes digests, not paths, and only to a client already
authenticated against the store that wrote them. Accepted so that a full-entry write cannot destroy
drift detection.
