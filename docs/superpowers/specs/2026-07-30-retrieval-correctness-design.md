# Retrieval correctness — design

**Date:** 2026-07-30
**Status:** Approved (design); implementation plan pending
**Stream:** 4 of 7 from the 2026-07-29 whole-codebase review remediation

## Context

Stream 4 addresses the four confirmed **retrieval-correctness** findings from the
2026-07-29 review. Each was re-verified against the current source; two had moved
or narrowed since the review was written, and one additional live defect was found
adjacent to a confirmed one:

- The **filter-before-top-K** and **MMR chunk-TF** findings are live exactly as
  described.
- The **signature-validation** infrastructure (SVF3 signed vector files, quarantine
  on mismatch, `VectorStore.ExpectedSignature`) fully exists and the *reindexer*
  uses it correctly — but the two **live-serving** call sites construct `VectorStore`
  without a signature, silently disabling the protection.
- The **stale-chunk-on-shrink** finding's cited location (the reindexer) is **already
  fixed** with a Remove-then-Upsert. The identical bug is **live** in the incremental
  embed path (`EmbedAndIndexAsync`). While verifying it, the append path was found to
  re-embed only the appended fragment as the full-document vector — the same
  incremental-path root cause — and is folded in per approval.

`Scrinia.Merge` is untouched by this stream. No new dependencies are introduced; the
solution-wide vulnerability audit stays clean (Stream 3).

## Group A — search ranking correctness

### A1. MMR chunk diversity compares parent TF instead of chunk TF

**File:** `src/Scrinia.Core/Search/MmrReranker.cs` (`GetTf`, ~L176-181)

**Now:** `GetTf(SearchResult)` returns, for a `ChunkEntryResult`,
`cr.ParentItem.Entry.TermFrequencies` — the *parent document's* term frequencies.
The MMR diversity penalty is TF-cosine over this dict, so two distinct chunks of the
same parent document have cosine 1.0 (identical) and MMR wrongly treats them as
redundant, suppressing legitimately diverse chunk hits.

**Fix:** return the chunk's own term frequencies with a parent fallback:
`ChunkEntryResult cr => cr.Chunk.TermFrequencies ?? cr.ParentItem.Entry.TermFrequencies`.
`ChunkEntry.TermFrequencies` already exists and is populated — `WeightedAttributeScorer`
scores chunk BM25 from it (`ScoreChunkEntryTerm` reads `chunk.TermFrequencies`). The
parent fallback preserves behavior for any legacy chunk that carries no TF of its own.

**Tests:** two `ChunkEntryResult`s from the same parent with *different* chunk TF must
be treated as diverse (TF-cosine < 1, both can survive MMR); two chunks with identical
chunk TF still collapse (cosine 1). A chunk with null TF falls back to the parent dict.

### A2. pathPrefix / attributeFilter applied after the top-K limit

**Files:** `src/Scrinia.Mcp/MemoryTools.Core.cs` (`ExecuteSearchAsync`, `ApplyPostFilters`,
`MatchesPathPrefix`, `MatchesAttributes`), `src/Scrinia.Core/IMemoryStore.cs`,
`src/Scrinia.Core/FileMemoryStore.cs`, `src/Scrinia.Core/Search/WeightedAttributeScorer.cs`

**Now:** `ExecuteSearchAsync` calls `store.SearchAll(effectiveQuery, scopes, limit, …)` —
which cuts to the top `limit` inside `WeightedAttributeScorer.SearchAll`'s size-K
min-heap — and *then* applies `ApplyPostFilters(store, matches, pathPrefix,
attributeFilter)`. A filtered search therefore returns fewer than `limit` results even
when more matching results exist below the top-K cut. `excludeTopics` is already applied
pre-limit (via `ListScoped(scopes, excludeTopics)`); `pathPrefix`/`attributeFilter` are not.

**Fix — push the filter into Core, applied before the top-K cut (symmetric with
`excludeTopics`):**

- Add an optional result filter to the `IMemoryStore.SearchAll` overload that
  `ExecuteSearchAsync` calls. The **interface default** post-filters after the base
  `SearchAll` (matching the existing `excludeTopics` default-vs-`FileMemoryStore`-override
  pattern, so non-scorer stores keep working). `FileMemoryStore` **overrides** to thread
  the filter into the scorer for true pre-limit filtering.
- The filter is applied in `WeightedAttributeScorer.SearchAll` at the point results are
  fed into the size-K min-heap (the `HeapInsert` calls, ~L231-236) and to topic results
  before they enter the heap — so filtered-out results never consume a top-K slot.
- Relocate the `MatchesPathPrefix` / `MatchesAttributes` predicate logic from
  `MemoryTools.Core.cs` into Core so the scorer can apply it. `FileMemoryStore` composes
  the `Func<SearchResult,bool>` predicate because it owns the store-specific
  `FormatQualifiedName` labeling that path-prefix matching needs; the scorer applies the
  opaque predicate and stays store-agnostic.
- Remove the `ApplyPostFilters` call from `ExecuteSearchAsync` — single source of truth.
  (The relocated matcher helpers replace it; the MCP-layer copies are deleted.)

**Filter semantics preserved verbatim:** path-prefix is a case-insensitive `StartsWith`
on the qualified label (ephemeral entries labeled `~{name}`, topics via
`FormatScopeLabel`); attribute filter is AND across keys, OR within each key's values,
case-insensitive, and excludes `TopicResult` (no attributes) when any filter is active.

**Correctness criterion (load-bearing):** for any active filter, the search returns up
to `limit` matching results whenever at least `limit` matches exist. Tests cover entry,
chunk, and topic results and the failing-today case: a selective filter over a candidate
set larger than `limit`, asserting the result count equals `limit` (not the smaller
post-filtered remnant of the old top-K).

## Group B — embeddings integrity

### B1. Live-serving VectorStore construction skips signature validation

**Files:** `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs` (`EnsureInitialized`, ~L94),
`src/Scrinia.Plugin.Embeddings.Cli/Program.cs` (~L47)

**Now:** both construct `new VectorStore(dataDir)` with no `expectedSignature`, so the
SVF3 quarantine-on-mismatch protection is disabled on the live-serving path. If the
embedding provider or model changes, the store silently serves semantically incompatible
vectors instead of quarantining them. The reindexer
(`EmbeddingReindexer.ForceReindexAsync` / `ReindexIfStaleAsync`) already composes and
passes the signature; only the two live entry points bypass it.

**Fix:** initialize the provider **before** constructing the `VectorStore`, then pass the
composed signature:
`new VectorStore(dir, ChunkedSignature.Compose(provider.Signature, options.ChunkSize, options.ChunkOverlap))`,
using the same `EmbeddingOptions` the plugin / CLI already build. Both call sites
currently create the `VectorStore` before the provider — reorder them.

- **Load-bearing equivalence:** the live-path signature must be byte-identical to the
  reindexer's `ChunkedSignature.Compose(provider.Signature, options.ChunkSize,
  options.ChunkOverlap)` for the same provider + options. If they diverged, the live path
  would quarantine exactly the vectors the reindexer just wrote. A test asserts equality
  of the two composed signatures for a representative provider + options.
- **Non-destructive on provider failure:** when the active provider is unavailable (the
  `NullEmbeddingProvider` / failed-init case, `IsAvailable == false`), pass `null`
  (signature checking disabled) rather than the `"none"` signature — a transient provider
  load failure must never quarantine good vectors. Real vectors are quarantined only when
  a *different real* provider is active. A test asserts an unavailable provider leaves an
  existing signed file intact (no `.stale` quarantine).

### B2. Incremental embed leaves stale chunk vectors on shrink; append corrupts the document vector

**File:** `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs` (`EmbedAndIndexAsync`,
`OnAppendedAsync`, ~L235-269)

**Now (shrink):** `EmbedAndIndexAsync` builds a document (null-chunk) vector plus one
vector per content element and `UpsertAsync`es each, but never removes prior vectors for
the name first. Re-storing a memory with fewer chunks than before leaves the old
higher-index chunk vectors orphaned in the store — the same class of bug the reindexer
already fixed with a per-memory Remove-then-Upsert.

**Now (append):** `OnAppendedAsync(qualifiedName, content, …)` receives only the appended
fragment and calls `EmbedAndIndexAsync(qualifiedName, [content], …)`. Because that is a
single-element array, it embeds *only the fragment* as the document (null-chunk) vector,
overwriting the vector that should represent the full memory. After any append, the
document-level embedding no longer represents the document.

**Fix:**
- **Shrink:** in `EmbedAndIndexAsync`, before the upsert loop, call
  `await _vectorStore.RemoveAsync(scope, subject, ct)`. `RemoveAsync` is a no-op for a
  never-before-embedded name, so first-time stores are unaffected; re-stores clear stale
  vectors then re-add. This runs on the store path (multi-element content) and the
  reindex/migration sink-replay path (single-element) alike — correct for both, since each
  replaces the memory's vectors wholesale.
- **Append:** in `OnAppendedAsync`, read the full current content via
  `store.ReadArtifactAsync(subject, scope, ct)` and re-embed *that* (as `[fullText]`) —
  the same single-element representation the reindexer sink-replay
  (`sink.OnStoredAsync(qualifiedName, [decoded], …)`) and migration paths already use.
  Combined with the Remove-first above, append clears stale vectors and produces a correct
  full-document vector.

**Non-goal (documented):** unifying the incremental path's chunk representation with the
reindexer's `TextChunker.SliceWindows` windowing is a pre-existing divergence and out of
scope here. Chunk-level vectors after an append are rebuilt on the next reindex — exactly
as they already are for the reindex/migration single-element paths. This stream fixes
*correctness* (no stale vectors, correct document vector), not chunk-granularity parity.

**Tests:** re-store a memory with fewer chunks → the orphaned higher-index chunk vectors
are gone (query the store's vector count / chunk indices for the name). Append to a
memory → the document vector reflects the full post-append content, not the fragment, and
no stale vectors remain. First-time store → unaffected (Remove is a no-op).

## Ordering

A1 and A2 are independent of B1/B2 and of each other; any order works. Suggested:
A1 (smallest, isolated) → A2 (interface seam) → B1 (call-site reorder) → B2 (incremental
path). Each task ends green with its regression test.

## Testing / validation

Every fix ships a regression test (per-finding notes above). Full suites green
solution-wide (Stream 3 cleared the audit, so no `-p:NuGetAudit=false`):
- Core: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`
- Embeddings: `dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj`
- Server (search surface): `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj`

Two load-bearing invariants beyond per-fix tests:
- **A2:** a filtered search over a candidate set larger than `limit` returns exactly
  `limit` results.
- **B1:** the live-path composed signature byte-equals the reindexer's for the same
  provider + options.

## Non-goals (other streams)

Streams 5 (secrets & transport), 6 (web frontend), 7 (docs / minor) remain their own
specs. Streams 1 (containment & authz), 2 (data integrity), and 3 (supply chain / CI-CD)
are merged.

## Risks

- **A2** touches the `IMemoryStore.SearchAll` seam. The interface default post-filters
  (preserving non-scorer stores); only `FileMemoryStore` changes behavior. The
  entry/chunk/topic fill tests are the safety net that the pre-limit path returns the same
  *set* the old post-filter would, just correctly filled to `limit`.
- **B1** reordering must not change provider initialization semantics (lazy-init sentinel,
  disposal, logging). The equivalence and non-destructive tests bound the risk; the
  reorder is mechanical.
- **B2** the added `RemoveAsync` runs on every embed. It must stay a no-op for first-time
  names (verified) and must not race the subsequent upserts — both operate under the
  `VectorStore` per-scope lock, so the Remove fully completes before the upsert loop.
