# Embeddings & Search Consolidation — Design

**Date:** 2026-08-16
**Branch:** `fix/embeddings-search-consolidation`
**Findings:** H3, H6, M6, L1b (from `docs/reviews/2026-08-16-design-review.html`)
**Branch 4 of 7** in the design-review remediation sequence.

## Goal

One embed-and-index policy, reachable from one endpoint, with one scoring rubric — and the
dead code that accumulated around all three removed.

## What was verified against HEAD before designing

Every claim below was read directly at `2478f0a`. Three of them change the design relative to
the review text, and one is a new defect the review did not record.

### H3 confirmed, and deeper than stated

The review says "the load-bearing divergence is the CHUNKING UNIT, not the key format." Both
are load-bearing, and they compound.

`ChunkEntry.ChunkIndex` is **1-based over nmp2 content elements** (`TextAnalysis.cs:248`
emits `ChunkIndex: i + 1`). That is the index the scorer's chunk pass carries into its
supplemental lookup (`WeightedAttributeScorer.cs:208` → `:211` → `GetSupplemental` at `:714`,
which probes `$"{scope}|{name}|{chunkIndex}"`).

The built-in path writes vectors keyed by `TextChunker.SliceWindows` indices — **0-based over
char windows** (`TextChunker.cs:55-62`). Different unit *and* different base. The chunked
supplemental key it writes can therefore never be the key the scorer asks for. Not "rarely":
never, including for single-window memories, where the window is index `0` and the content
element is index `1`.

The plugin path (`EmbeddingsPlugin.cs:273-280`) writes `i + 1` over content elements — the
correct unit and base, matching `ChunkEntry` exactly — but never windows at all, so a long
single-element memory becomes one mean-pooled vector.

Each path implements one half of the contract. Renumbering windows to 1-based, which is the
review's literal recommendation, fixes the base without fixing the unit and leaves the keys
still unmatched.

### H6 confirmed on both copies

`MemoryStoreContext.Current` is set only at `Program.cs:309`, inside the MCP session flow.
`RequestContext.Store` is set only at `Program.cs:502`, inside the
`if (RouteValues.TryGetValue("store", …))` branch. The embeddings group is mapped at
`/api/v1/plugins/embeddings` (`Program.cs:535,544`), which carries no `{store}` route value.

So both resolutions are null on every request to this route:

- the built-in copy (`Program.cs:562-580`) has a `reqCtx.Store` fallback at `:572-573` that
  is null for exactly the same reason its `MemoryStoreContext.Current` probe is;
- the plugin copy (`EmbeddingsPlugin.cs:142-154`) has no fallback at all.

Both return `400 "No store context available."` unconditionally. The group also carries only
`.RequireAuthorization()` — no permission gate — and is declared inline in `Program.cs` rather
than under `Endpoints/`.

### NEW: the Settings page Save button cannot work

Not in the review. `web/src/pages/SettingsPage.tsx:22` wires a Save control to
`updateEmbeddingsSettings`, which issues `PUT /api/v1/plugins/embeddings/settings`
(`client.ts:282-287`). **No server route maps PUT there** — neither `Program.cs:554` nor
`EmbeddingsPlugin.cs:156` maps anything but `MapGet("/settings")`. The request falls through
to `app.MapFallbackToFile("index.html")` (`Program.cs:592`) and returns HTML, which
`apiFetch` fails to parse.

Same root cause as H6 — routes declared inline, never reviewed against the endpoint
conventions. It is folded into this branch because it lives in the same route group H6 moves.

### L1b: the review's RRF claim is false

The review lists "the ~90 lines of RRF fusion that `RankerOptions.FromConfig` cannot reach"
among code that is *genuinely dead*. RRF is not dead. It is reached by:

- the benchmark harness, via `--fusion rrf` (`RetrieverFactory.cs:145`, passed at `:155`,
  parsed at `:390-393`);
- direct tests — `RankerCompositionTests.cs:181,209,211,226,235,248,258` and
  `EntityGraphSourceSearchTests.cs:55,62`.

The true defect is narrower and real: `RankerOptions.FromConfig:74-78` constructs a
`RankerOptions` without passing `Fusion`, so the *production configuration path* silently
cannot select a mode the rest of the system supports. The positional-record constructor
(`RankerOptions.cs:57-62`) is what made the omission invisible.

The other three L1b items are confirmed dead as described:

- `HnswIndex` — 380 lines; referenced only by `HnswIndexTests.cs` and `HnswIndexBenchmarks.cs`.
  Its documented "vector count >= 1000" threshold (`HnswIndex.cs:7`) appears nowhere else in
  the codebase.
- `EntityGraph`'s instance side — `EntityGraph.Build(...).Score(...)` appears only in
  `EntityGraphEquivalenceTests.cs:33`, held in place by an equivalence test against
  `EntityFactStore.ScoringAdjacency` (`EntityFactStore.cs:85`), which is what production uses
  via `EntityGraphScoreContributor.cs:64`. The **static** parsers `ParseEntityKeywords` and
  `ParseEntityAttributes` are live production code (`EntityFactDerivation.cs:14-15`) and stay.
- `MergeConfig.Resolver` — written by `ScriniaCommands.Setup.cs:369`, never read by the merge
  driver. Its sibling `ResolverCommand` (`MergeConfig.cs:9`) has no writer either.

Also confirmed: `HybridReranker`'s header claims it "embeds only the BM25 top-K candidates"
(`HybridReranker.cs:7-16`) while `:62` passes `topK: candidateVectors.Count` and scores every
candidate vector in the scope.

## Architecture

### 1. `MemoryVectorIndexer` — one embed-and-index policy

New: `src/Scrinia.Core/Embeddings/MemoryVectorIndexer.cs`.

```csharp
public sealed class MemoryVectorIndexer(
    IEmbeddingProvider provider, VectorStore store, ILogger logger, EmbeddingOptions options)
{
    public Task IndexAsync(string scope, string subject, string[] content, CancellationToken ct);
    public Task RemoveAsync(string scope, string subject, CancellationToken ct);
}
```

It owns, in one place: availability check, window, cap, batch, count-guard,
remove-then-upsert ordering, and `VectorWriteRetry`.

**The chunk model.** A memory's `content` array is its addressable unit — `ChunkEntry`,
1-based, the same unit the REST contract and the scorer's chunk pass already use. Each element
is windowed by `TextChunker`. Each window is embedded. Window similarities are max-aggregated
up to the element and again up to the memory:

```
{scope}|{name}                  max over every window in the memory
{scope}|{name}|{elementIndex}   max over that element's windows      (1-based)
```

Both keys `GetSupplemental` probes now resolve. Windows remain an implementation detail and
never appear in a key, which is what makes the unit disagreement disappear rather than get
renumbered. Long memories stay windowed, so semantic recall on them is preserved — including
for a single-element memory, the case where the plugin currently produces one mean-pooled
vector over the whole text.

The cap (`MaxChunksPerMemory`, default 100) applies to the total window count across the
memory, matching today's behaviour on the built-in path.

**Read side.** New `VectorScoreProjection` owns max-aggregation and the key contract,
replacing four hand-copied projection loops: `HybridReranker.cs:63-81`,
`BuiltInEmbeddingsService.cs:62-74`, `EmbeddingsPlugin.cs:190-198`, and
`EmbeddingsTools.cs:44-51`.

**Callers routed through it — six in-process copies:**

| Call site | Today |
|---|---|
| `EmbeddingReindexer.ReindexAsync:84-115` | windowed, capped, remove-then-upsert, no retry |
| `CoreEmbeddingEventHandler.EmbedAndIndexAsync:69-95` | windowed, capped, retry |
| `BuiltInEmbeddingsService.EmbedAndIndexAsync:189-206` | windowed, capped, retry |
| `BuiltInEmbeddingsService.ReindexStoreAsync:149-159` | windowed, capped, no retry |
| `EmbeddingsPlugin.EmbedAndIndexAsync:273-310` | **not windowed**; doc vector + per-element |
| `EmbeddingsPlugin.ReindexStoreAsync:359-362` | **not windowed, no remove**; one doc vector |

Each caller keeps only its own resolve/decode preamble. `Scrinia.Plugin.Embeddings` already
`ProjectReference`s `Scrinia.Core` (`Scrinia.Plugin.Embeddings.csproj:15`), so the plugin
calls the shared indexer directly — no copy and no wire hop.

`EmbeddingReindexer.ReindexViaSinkAsync` is not an indexer and is not in this list: it is a
driver that replays memories through a sink. It stays.

### 2. Signature bump — retiring vectors written under the old layout

`EmbeddingsPlugin.cs:118` passes `new EmbeddingOptions()` — a default-constructed object — to
`ChunkedSignature.ForProvider`, so the file is stamped `|c1200o200` regardless of configured
chunk settings, and configured settings never reach the signature at all. Passing the live
options is the bug fix.

That alone does not retire vectors written under the old layout when the config is at its
defaults. `ChunkedSignature.Compose` (`ChunkedSignature.cs:17-18`) gains a layout marker, so
every existing vector file mismatches on next load, self-quarantines through
`VectorStore.LoadFromDisk`, and rebuilds via reindex. This is the same flow that already
handles a model switch or a chunk-config change.

**This is why H6 lands first.** The migration path is `reindex`, and `reindex` does not
currently work over REST.

### 3. `Endpoints/EmbeddingsEndpoints.cs` — one reachable, gated endpoint file

New file under `Endpoints/`, mapped into the existing `/api/v1/stores/{store}` group
(`MemoryEndpoints.cs:18`), so the middleware at `Program.cs:480-503` resolves the store and
applies the store-access check before the handler runs:

```
GET  /api/v1/stores/{store}/embeddings/status
GET  /api/v1/stores/{store}/embeddings/settings
POST /api/v1/stores/{store}/embeddings/reindex    [permission: reindex]
```

Built-in and plugin both register one DI abstraction; the endpoint file resolves that and does
not know which implementation answered. `EmbeddingsPlugin.MapEndpoints` stops mapping
`/embeddings`. `MapEndpoints` survives as a general plugin extension point — it simply is no
longer how embeddings routes get published.

The `reindex` permission is gated in the same hand-written `ctx.HasPermission(...)` style as
its ~30 neighbours in `MemoryEndpoints.cs`. Permissions are free-form strings on the API key
(`ApiKeyStore.cs:128`), so no catalogue needs extending. **Consolidating those gates is M5,
branch 6**; doing it here would collide with that branch.

**Web client.** `client.ts` — 3 URLs updated to the store-scoped paths.
`updateEmbeddingsSettings` (`:282-287`) is **deleted** along with the Save control and
`validateEmbeddingsSettings`; the settings panel becomes read-only with a line naming where
the values come from. The server has no config-write mechanism — `Scrinia:Embeddings` is bound
from configuration and read-only at runtime — so honouring the button would mean building one,
which is not an embeddings change. A button that returns HTML is worse than no button.

### 4. Out-of-process host — `index` tool, negotiated

`McpPluginHost.OnStoredAsync:156-183` is the seventh copy: it sends `upsert{text: joined}`
for the document vector and `upsert{text: content[i], chunkIndex: i+1}` per element, and
windows nothing. The policy is re-expressed over the wire instead of shared.

`EmbeddingsTools` (`Scrinia.Plugin.Embeddings.Cli`) gains an `index` tool taking the whole
`content` array, which runs `MemoryVectorIndexer` inside the plugin process. It already has
`VectorStore`, `IEmbeddingProvider` and `EmbeddingOptions` injected
(`EmbeddingsTools.cs:9-10`) and references `Scrinia.Core.Embeddings`, so this is a
constructor-level change.

`upsert` is **not** broken. `McpPluginHost` already discovers capabilities via
`ListToolsAsync` (`:121-125`), so `_hasIndex` joins `_hasSearch`/`_hasUpsert`/`_hasRemove` as
one line. The host prefers `index`; where a plugin offers only `upsert`, it falls back to
today's two-call path — roughly fifteen lines, exactly the code that exists now — and logs a
warning naming the divergence. Third-party plugins implementing only `upsert` keep working on
today's behaviour rather than silently ceasing to be indexed.

### 5. `ScoringWeights` — one home for the rubric

New record with named fields, constructed once and passed into `WeightedAttributeScorer`.
It replaces:

- three literal ladders — `ScoreEntryTerm:326-390`, `ScoreTopicTerm:414-453`,
  `ScoreChunkEntryTerm:480-547`;
- the `(matchedTerms - 1) * 15.0` intersection bonus, verbatim at `:321`, `:409`, `:475`;
- `Bm25Weight = 5.0` at `:51`;
- the ASCII score table in the class header (`:14-45`), which is **deleted**, not regenerated.
  A table that must be hand-synced is the defect; the record is now the readable statement.

No new configuration surface. The argument for consolidating here is editability and honesty
about where a number lives — not a projected retrieval score. Every value keeps its current
magnitude; this refactor must not move a single result.

The literal `50.0` appears as a default in five places
(`EmbeddingOptions.cs:10`, `HybridReranker.cs:24`, `EntityGraphScoreContributor.cs:27`,
`EmbeddingsPlugin.cs:34`, `Scrinia.Plugin.Embeddings.Cli/Program.cs:46`) plus a sixth,
`RankerOptions.RecencyScale` at `:61`, which is not a weight at all. These are *different
knobs that happen to share a value* — a semantic-similarity weight, an entity-graph PPR
weight, and a recency scale are not the same number. They are left alone; collapsing them
would change behaviour under configuration, which this branch does not do. The review's
"three independent definitions of the semantic weight" undercounts and over-groups.

### 6. Dead code

**Delete:**

- `HnswIndex.cs` (380 lines), `tests/Scrinia.Tests/Embeddings/HnswIndexTests.cs`,
  `tests/Scrinia.Benchmarks/HnswIndexBenchmarks.cs`. The prose reference at
  `EntityTypeClassifier.cs:87` is a comment and is reworded.
- `EntityGraph.Build` and `EntityGraph.Score` (the instance side) with
  `EntityGraphEquivalenceTests.cs`. The static `ParseEntityKeywords` / `ParseEntityAttributes`
  are production code and stay untouched.
- `MergeConfig.Resolver` and `MergeConfig.ResolverCommand`, and the write at
  `ScriniaCommands.Setup.cs:369`. `MergeConfigTests.cs:43-44` and
  `MergeConfigRoundTripTests.cs` are updated to match.

**Wire up:**

- `Scrinia:Search:Fusion` in `RankerOptions.FromConfig`, parsing `sum` / `rrf` with the same
  case-insensitive shape as the bench harness (`RetrieverFactory.cs:390-393`) and falling back
  to `FusionMode.Sum` on anything unrecognized, matching the other keys' behaviour.

**Fix:**

- `HybridReranker`'s header, which claims BM25 top-K while `:62` scores every candidate vector.
  The behaviour is correct for a filtered candidate set; the comment is what is wrong.

## Testing

The load-bearing proof is a conformance test that **fails on HEAD**: for a memory with several
content elements, at least one long enough to window, assert that *both* supplemental keys
resolve —

```
{scope}|{name}                  present
{scope}|{name}|{elementIndex}   present for each element
```

— and that the element key's value equals the max over that element's windows. Neither path can
satisfy this today: the built-in writes window-indexed keys the scorer never asks for, and the
plugin writes matching keys over unwindowed vectors.

Alongside it:

- **Indexer parity.** One input through `MemoryVectorIndexer` from the built-in service and
  from the plugin, asserting identical key sets and identical chunk counts. This is the test
  that would have caught H3.
- **Long single-element memory.** A memory well past `ChunkSize` in one content element
  produces more than one vector — the specific regression the Vulkan plugin causes today.
- **Cap behaviour.** Window count above `MaxChunksPerMemory` truncates the tail and logs,
  matching current built-in behaviour.
- **Count-guard.** A provider returning a mismatched vector count leaves existing vectors
  intact — the `EmbeddingsPlugin.cs:284-295` guard, now shared.
- **Quarantine and rebuild.** A vector file written under the old signature is quarantined on
  load after the bump and rebuilt by reindex.
- **Route reachability.** Each moved route answers non-400 with a valid `{store}`; `reindex`
  returns 403 without the permission; a request to the retired
  `/api/v1/plugins/embeddings/reindex` path no longer resolves.
- **Host negotiation.** A plugin advertising `index` receives one call with the full content
  array; a plugin advertising only `upsert` receives today's two-call sequence.
- **Scorer pinning.** Characterization tests over `SearchAll` captured **before**
  `ScoringWeights` lands, asserting identical ordering and identical scores after. The
  refactor is behaviour-preserving or it is wrong.
- **Fusion config.** `FromConfig` with `Scrinia:Search:Fusion=rrf` yields `FusionMode.Rrf`;
  absent or unrecognized yields `Sum`.

## Task order

H6 before H3, because the migration path for the signature bump is `reindex`, and `reindex`
does not work over REST today.

1. **H6** — `Endpoints/EmbeddingsEndpoints.cs`, DI abstraction, `reindex` permission, plugin
   stops mapping its copy, web client updated, Save control removed.
2. **H3a** — `MemoryVectorIndexer` + `VectorScoreProjection` in Core, with tests.
3. **H3b** — route the six in-process copies through them.
4. **H3c** — `index` tool in `EmbeddingsTools`, negotiation in `McpPluginHost`.
5. **H3d** — signature bump, live options at `EmbeddingsPlugin.cs:118`, quarantine test.
6. **M6** — pinning tests, then `ScoringWeights`.
7. **L1b** — delete three, wire `Fusion`, fix the `HybridReranker` header.

## Out of scope, deliberately

- **Consolidating the ~30 permission gates** (M5) — branch 6. This branch adds one gate in the
  existing style.
- **Collapsing the four `50.0` weights** — they are distinct knobs sharing a default.
- **A server-side configuration-write path** — would be required to honour the Settings Save
  button; removed instead.
- **`EmbeddingReindexer.ReindexViaSinkAsync`** — a driver, not an indexing policy.
- **The `/facts` wire route** — carried in from branch 2. See below.

## Inherited from branch 2, and re-examined

`/findings/h1-store-defaults-falsified` chunk 7 records that a remote `search()` now lists the
whole corpus, because `MemoryTools.Core.cs:882` passes a hardcoded `surfaceEntityFacts: true`
and `SearchPipeline.cs:50` fires `ListScoped` on that disjunct alone. That memory names this
branch's neighbourhood as the place to re-examine — **not inherit** — the argument for a
`/facts` wire route.

Re-examined here: it does not belong in this branch. The cost is on the *search read path*
through `HttpMemoryStore`, and the fix named there is a one-line predicate change at
`MemoryTools.Core.cs:882` that alters remote search behaviour. This branch touches the vector
index and the scoring rubric, not scope resolution or the wire format. Folding it in would
mean a second behavioural change to remote search landing beside a REST-surface break and a
vector-layout migration. It stays recorded and unclaimed.

## Self-corrections recorded during design

1. **The review's RRF claim was false.** RRF is bench-reachable and directly tested; only the
   production config path cannot select it. Deleting it, as the review's L1b text implies,
   would have removed a working feature.
2. **The review's chunk-key recommendation was insufficient.** "Pick 1-based to match
   `ChunkEntry`" fixes the base but not the unit; the keys would still never match.
3. **A defect the review missed.** The Settings page's Save button has no server route at all.
4. **The review's "three independent definitions" of the semantic weight is wrong in both
   directions.** There are five `50.0` weight defaults, not three — and they are not one knob,
   so consolidating them is not the fix.
