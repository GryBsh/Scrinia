# Search System Reference

> Hybrid BM25 + weighted field + semantic scoring in `Scrinia.Core.Search`.

---

## Scoring Formula

```
finalScore = weightedFieldScore + normalizedBm25 × 5.0 + supplementalScore
```

| Component | Source | Range |
|-----------|--------|-------|
| `weightedFieldScore` | `WeightedFieldScorer` — name, tag, keyword, description, preview matches | 0–unbounded (sum of per-term max) |
| `normalizedBm25` | BM25 over term frequencies, min-max normalized to 0–100 | 0–100 |
| `supplementalScore` | `ISearchScoreContributor` plugins (e.g., semantic cosine similarity) | Plugin-defined (default weight: 50.0) |

When no search score contributor is registered, `supplementalScore = 0` (legacy BM25+field behavior).

### BM25 Normalization

Raw BM25 scores are min-max normalized to a 0–100 range across all candidates in a single search:

```
normalizedBm25 = (rawBm25 - minBm25) / (maxBm25 - minBm25) × 100
```

This ensures BM25 scores are comparable across corpora of different sizes and TF distributions.

### Multi-Term Intersection Bonus

When a query has multiple terms, entries matching more than one term receive a bonus:

```
intersectionBonus = (matchedTerms - 1) × 15
```

This rewards entries that match multiple query terms rather than just one very strongly.

---

## Weighted Field Scoring

Per query term, each field is checked and the maximum score wins. Multi-term queries: per-term max scores are summed, then the intersection bonus is added.

### Entry Scoring

| Match Type | Score |
|-----------|-------|
| Exact name match | 100 |
| Tag exact match | 50 |
| Keyword exact match | 40 |
| Name starts with | 30 |
| Name contains | 20 |
| Tag contains | 15 |
| Keyword contains | 12 |
| Description contains | 10 |
| Content preview contains | 5 |

### Chunk Scoring

Chunk-level search uses parent entry metadata at half weight plus chunk-specific fields:

| Match Type | Score |
|-----------|-------|
| Parent name exact match | 50 (half of 100) |
| Chunk keyword exact match | 40 |
| Chunk keyword contains | 12 |
| Chunk content preview contains | 5 |

Each chunk also gets its own BM25 score from chunk-level `TermFrequencies`.

### Topic Scoring

| Match Type | Score |
|-----------|-------|
| Topic name exact match | 80 |
| Topic tag exact match | 40 |
| Topic name contains | 15 |
| Topic description contains | 10 |
| Entry name within topic | 30 |

---

## BM25

Standard BM25 with parameters:
- **k1** = 1.5 (term saturation)
- **b** = 0.75 (document length normalization)
- **IDF**: `ln((N - df + 0.5) / (df + 0.5) + 1)`

Corpus stats computed per search invocation. `CachedIndex` in `FileMemoryStore` caches `CorpusStats` alongside the index for O(1) reuse when the index hasn't changed.

Entries without `TermFrequencies` (v2 index) gracefully get `bm25Score = 0`.

---

## Top-K Selection (Min-Heap)

Results are collected using a `PriorityQueue<SearchResult, double>` as a min-heap, bounded at `k` items. The `HeapInsert` method:

1. If heap has fewer than `k` items, enqueue directly
2. If the new score exceeds the minimum (peek), dequeue the minimum and enqueue the new item
3. Otherwise, discard

This gives O(n log k) selection versus the previous O(n log n) sort.

---

## Deduplication

Inline deduplication via `bestPerMemory` dictionary (keyed by `"{scope}|{name}"`). For each candidate, only the highest-scoring result per memory is kept. `TopicResult` passes through unaffected.

---

## Supplemental Scores

Plugins implement `ISearchScoreContributor` to provide supplemental scores (e.g., semantic cosine similarity from the embeddings plugin).

**Score key format** (matches deduplication key pattern):
- Entries: `"{scope}|{name}"`
- Chunks: `"{scope}|{name}|{chunkIndex}"` — chunk key checked first, falls back to entry key

`IMemoryStore.SearchAll()` has two overloads: the 3-parameter version delegates to the 4-parameter version with `supplementalScores: null`.

---

## Keyword TF Boosting

When storing or appending content, keywords are boosted in the term frequency map:

| Keyword Source | TF Boost |
|---------------|----------|
| Agent-provided keywords | +5 |
| Auto-extracted keywords | +2 |

This ensures agent-specified keywords rank higher in BM25 scoring without requiring exact field matches. Applied in both `ScriniaMcpTools.Store` (MCP path) and `MemoryOrchestrator.StoreAsync` (REST path).

---

## TextAnalysis

Public static class in `Scrinia.Core.Search`:

| Method | Description |
|--------|-------------|
| `Tokenize(text)` | Character-by-character scan, splits on non-alphanumeric, lowercases, filters ~190 stop words and tokens < 2 chars |
| `AnalyzeText(text)` | Single-pass tokenization with TF computation — returns `(tokens, termFrequencies)` |
| `ComputeTermFrequencies(text)` | Tokenize + count occurrences |
| `ExtractKeywords(text, topN=25)` | Top N terms by frequency |
| `MergeKeywords(agent?, auto, max=30)` | Dedup, agent-first ordering, cap at 30 |
| `MergeKeywordsWithSource(agent?, auto, max=30)` | Same as MergeKeywords but returns `(keyword, isAgent)` tuples for differential TF boosting |
