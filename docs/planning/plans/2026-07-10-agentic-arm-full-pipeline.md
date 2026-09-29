# Plan: agentic-ingest arm retrieves through the full production pipeline (embeddings + hybrid rerank)

## Goal / intent

The agentic-ingest arm was always meant to have the agent's writes flow through the **real** Scrinia
ingestion+retrieval pipeline — the agent's own keywords **plus** the usual mechanical keyword extraction,
**plus embeddings and the hybrid semantic rerank** — not a stripped bare `FileMemoryStore` queried with
BM25-only `SearchAll`. Keyword extraction already flows through (the harness calls the real
`ScriniaMcpTools.MemoryRemember/Append` → `Store()` → `TextAnalysis`); the gap is embeddings + the
semantic contributor. Close it with the **literal production components** (user decision).

## Confirmed decisions

- **Literal production path** — real `VectorStore` + `HybridReranker` + `PathSegmentScoreContributor`
  (the prod composite), fed by the real `IEmbeddingProvider` the bench already builds.
- **Reindex after episode** — because the production embed sink is fire-and-forget
  (`MemoryTools.Core.cs:1474` `FireEventSinkAsync` = `_ = Task.Run(...)`, discarded), the bench cannot
  race it. After each episode's writes land, populate the `VectorStore` synchronously via the real
  `EmbeddingReindexer.ForceReindexAsync` (the `scri reindex` helper). Deterministic, same vectors.
- **Design→SDD** with per-task tests + reviews.

## Key findings that shape the implementation (evidence)

1. Embed sink is fire-and-forget → `EmbeddingReindexer.ForceReindexAsync(store, provider, embeddingsDir,
   logger, onProgress, ct, options)` (`EmbeddingReindexer.cs:144`) is the deterministic populate step.
2. The semantic blend is applied in `MemoryTools.MemorySearch` (`MemoryTools.Core.cs:732`), NOT in
   `SearchAll`: it computes `supplemental = contributor.ComputeScoresAsync(query, ListScoped(...), store)`
   then calls the fusion overload `SearchAll(query, scopes, limit, supplemental, entityScores)`
   (`FileMemoryStore.cs:1154`). The bench's `RetrieveAsync` must replicate that flow, not just set a context.
3. The bench embedder (`EmbeddingProviderLoader.LoadAsync`) returns the same
   `Scrinia.Core.Embeddings.IEmbeddingProvider` the prod components consume — plugs in directly.
4. Prod wiring reference: `WorkspaceSetup.cs:242-247` — `VectorStore(embeddingsDir, signature)`,
   `HybridReranker(provider, vectorStore, options.SemanticWeight)`,
   `CompositeSearchScoreContributor(reranker, new PathSegmentScoreContributor())`,
   `signature = ChunkedSignature.Compose(provider.Signature, options.ChunkSize, options.ChunkOverlap)`.
5. `EmbeddingOptions` defaults: SemanticWeight 50.0, ChunkSize/Overlap = TextChunker defaults,
   MaxChunksPerMemory 100.

## Design

`AgenticScriniaRetriever` gains an optional `IEmbeddingProvider` (+ `EmbeddingOptions`). When present:

- **Per reset** (`CreateStore()` / `ResetAsync`): build a per-question `VectorStore` under the reset dir
  (`{rootDir}/q{resetCount}/embeddings`, signature via `ChunkedSignature.Compose`), a
  `HybridReranker(provider, vectorStore, options.SemanticWeight)`, and a
  `CompositeSearchScoreContributor(reranker, new PathSegmentScoreContributor())`. Dispose the VectorStore
  on reset/dispose.
- **After episodes run** (in `EnsureEpisodesRunAsync`, after the store is populated and BEFORE the
  resolve-once full-content cache / provenance): call `EmbeddingReindexer.ForceReindexAsync(_store,
  _embedder, embeddingsDir, logger, onProgress:null, ct, _embeddingOptions)`. Skip entirely (and keep
  today's BM25-only behavior) when no embedder is configured, so the no-embeddings arm is byte-for-byte
  unchanged.
- **Query path** (`RetrieveAsync` and `GetSurfacedContentsAsync`): when a contributor exists, mirror
  `MemorySearch` — `candidates = _store.ListScoped(scopes:null)`,
  `supplemental = await contributor.ComputeScoresAsync(query, candidates, _store, ct)`, then
  `_store.SearchAll(query, scopes:null, limit, supplemental, entityScores)`. When no contributor, keep the
  existing BM25-only `SearchAll(query, null, limit)`. Provenance/dedup/full-content resolution downstream
  is unchanged (operates on the returned results + `_fullContentByEntryName`).
- **Entity-graph**: include only when `--entity-graph` is set (matches the mechanical arm); wire an
  `EntityGraphScoreContributor` into `EntityGraphScoreContext` / pass its scores to the fusion overload,
  exactly as the mechanical arm does. Default off.

### CLI / factory (`RetrieverFactory.cs`)

- Remove the hard block that forces `Mode.Bm25` and the `--rerank` rejection for `--agentic-ingest`.
  Instead, when `--agentic-ingest` + an embeddings mode (`--mode bm25_embeddings` / `--embed-provider`)
  are set, load the embedder via the existing `EmbeddingProviderLoader` and pass it (plus a built
  `EmbeddingOptions`) into `AgenticScriniaRetriever`. Keep the existing `--rerank cross-encoder /
  sentence-maxsim` rerankers OUT of scope for now (the prod hybrid IS the semantic rerank); if `--rerank`
  is passed with agentic, either honor it as the mechanical arm does or reject with a clear message —
  decide during Task 3, document in-code.
- Log the enabled pipeline (provider, dims, semanticWeight, entityGraph on/off) like the mechanical arm.

## Files

- **Edit** `benchmarks/.../Core/AgenticScriniaRetriever.cs` — ctor gains `IEmbeddingProvider?` +
  `EmbeddingOptions?`; per-reset VectorStore/reranker/composite build + dispose; post-episode reindex;
  query-path contributor→fusion-SearchAll in `RetrieveAsync` + `GetSurfacedContentsAsync`.
- **Edit** `benchmarks/.../Cli/RetrieverFactory.cs` — drop the Bm25/​rerank block; load embedder + build
  options for the agentic arm; pass through; logging.
- **Tests** `tests/.../AgenticScriniaRetrieverTests.cs` (+ maybe a new focused file) — semantic-blend
  path, no-embedder path unchanged, reindex-populates-vectors, surfacing uses hybrid.
- Possibly a tiny seam so tests can inject a fake `IEmbeddingProvider` without a model download.

## Task breakdown (SDD)

1. **Retriever pipeline core** — ctor params, per-reset VectorStore/reranker/composite lifecycle
   (build + dispose), post-episode `ForceReindexAsync`. Tests: reindex populates vectors; no-embedder
   path unchanged; dispose releases the VectorStore.
2. **Query path hybrid** — `RetrieveAsync` + `GetSurfacedContentsAsync` compute supplemental via the
   contributor and call the fusion `SearchAll`; BM25-only when no contributor. Tests: with a stub
   embedder, a semantically-close-but-lexically-distinct query outranks/reaches a memory it wouldn't
   under BM25-only; no-contributor path identical to today.
3. **Factory wiring + flags** — remove the block, load embedder, build `EmbeddingOptions`, entity-graph
   gating, logging, `--rerank` decision. Tests: `--agentic-ingest --mode bm25_embeddings` builds an
   embedder-backed agentic retriever; plain `--agentic-ingest` stays BM25-only.
4. **End-to-end + docs** — extend the e2e smoke to assert the hybrid path runs with a stub embedder;
   update `--help` and any comments that claim the agentic arm is BM25-only.

## Verification

- `dotnet test` green; new tests prove the hybrid path changes ranking vs BM25-only and that the
  no-embedder arm is unchanged.
- A stub/fake `IEmbeddingProvider` (deterministic vectors, `IsAvailable=true`) drives tests with no
  model download.
- Manual: `locomo --granularity dialog --agentic-ingest --mode bm25_embeddings --embed-provider <p> …`
  logs the pipeline and reindex; spot-check that answerable@K / R@k move relative to the BM25-only agentic
  run.

## Risks / notes

- **Per-question isolation**: each reset needs its own VectorStore dir + fresh reranker; ensure disposal
  and no vector bleed across questions. Contexts (`SearchContributorContext` etc.) are AsyncLocal-with-
  Default — the bench sets the contributor per-retrieve or per-retriever; watch for cross-arm/parallel
  bleed (prefer passing the contributor explicitly into the query flow over relying on the static, since
  `RetrieveAsync` calls `SearchAll` directly and can hold its own reranker reference).
- **Cost/time**: reindex embeds every memory per question; Model2Vec is fast, HTTP/vulkan slower. Log
  progress; it's opt-in via the embeddings mode.
- **Don't touch production `src/`** — reuse the components as-is. All changes stay under `benchmarks/` +
  `tests/`.
- **No bench-shortcut** — this is the real pipeline, not an approximation (honors "the whole thing").
