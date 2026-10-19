# Core Architecture

`Scrinia.Core` is the shared class library containing encoding, models, search, storage, embeddings, and extensibility interfaces. It has no ASP.NET or native dependencies (only `System.IO.Hashing`) and is referenced by all other projects. Built-in semantic search (Model2Vec provider, API embedding providers, VectorStore, HybridReranker) lives in `Scrinia.Core.Embeddings` -- see [Embeddings Architecture](embeddings.md).

## NMP/2 Encoding

### Format Overview

NMP/2 (Named Memory Protocol v2) compresses text using Brotli and encodes it as URL-safe Base64 with CRC32 integrity checks.

**Single-chunk format:**

```
NMP/2 {N}B CRC32:{hex} BR+B64
{base64url lines, 76 chars each}
##PAD:{n}
NMP/END
```

**Multi-chunk format:**

```
NMP/2 {N}B CRC32:{hex} BR+B64 C:{k}
##CHUNK:1
{base64url lines}
##PAD:{n}
##CHUNK:2
{base64url lines}
##PAD:{n}
NMP/END
```

Each chunk is independently Brotli-compressed and decodable. The CRC32 covers the full concatenated original bytes across all chunks.

### Encoding Pipeline

```
text → UTF-8 bytes → Brotli compress → Base64url encode → line-wrap (76 chars) → add header/footer
```

Compression density: ~0.68-0.76 characters per original byte.

### IEncodingStrategy

```csharp
public interface IEncodingStrategy
{
    string StrategyId { get; }                    // "nmp/2"
    EncodingResult Encode(ReadOnlySpan<byte> input, EncodingOptions options);
    byte[] Decode(string artifact);
    bool CanDecode(string artifact);
    ArtifactMetadata ParseHeader(string artifact);
}
```

`Nmp2Strategy` is the sole implementation.

Header metadata requires a nonnegative decimal byte count, exactly eight hexadecimal CRC32
characters, the `BR+B64` codec, and an optional positive `C` no greater than 100,000. Header parsing
enforces metadata grammar and the chunk-count range. Full decoding checks aggregate length/CRC32
within the unchanged 64 MiB decoded limit. A selected-chunk read validates body structure and that
chunk's decode bound without establishing whole-memory integrity. Valid empty content and CRLF
artifacts remain compatible; a format probe is not an integrity check.

### Nmp2ChunkedEncoder

Public utility for all encoding operations:

```csharp
public static class Nmp2ChunkedEncoder
{
    // Single text → single-chunk artifact
    public static EncodingResult Encode(string text);

    // Multiple texts → multi-chunk artifact (if >1 element)
    public static EncodingResult EncodeChunks(string[] chunks);

    // Append a chunk without re-encoding existing content
    public static string AppendChunk(string existingArtifact, string newChunkText);

    // Count independently decodable chunks
    public static int GetChunkCount(string artifact);

    // Decode one chunk (1-based index)
    public static string DecodeChunk(string artifact, int chunkIndex);
}
```

**Key design:** There is no auto-chunking. A single element always produces a single chunk. Multi-chunk artifacts require explicitly passing multiple elements or using `AppendChunk`.

**AppendChunk** performs surgical promotion:
- Single-chunk → two-chunk: rewrites header with `C:2`, wraps existing content as `##CHUNK:1`, appends new `##CHUNK:2`
- Multi-chunk → N+1 chunks: appends new chunk section, updates header count and CRC32

Append fully decodes and verifies the predecessor's length and CRC32 before combining checksums with GF(2) matrix math (the zlib algorithm). Verification reads all existing bytes; append preserves the exact existing encoded sections and compresses only the new chunk.

## Data Models

### ArtifactEntry

The primary index entry for a stored memory:

```csharp
public sealed record ArtifactEntry(
    MemoryAddress Address,                           // canonical logical memory address
    string ArtifactKey,                              // opaque store-relative artifact key
    long OriginalBytes,
    int ChunkCount,
    DateTimeOffset CreatedAt,
    string? ContentPreview = null,                   // first 500 chars, or a curated description
    string[]? Keywords = null,                       // extracted + user-provided
    Dictionary<string, int>? TermFrequencies = null, // for BM25
    DateTimeOffset? UpdatedAt = null,
    ChunkEntry[]? ChunkEntries = null,               // per-chunk metadata
    Dictionary<string, string>? FileHashes = null,   // JSON "codeRefs": dependency path -> content hash (drift baseline); keys also surface as the `file` attribute
    Dictionary<string, string[]>? Attributes = null);// JSON "attributes": unified typed facets
```

Sidecars written before the Fields→Attributes rename stored the dict under the legacy JSON key `"fields"`. `ArtifactEntry.LegacyFields` captures that key at deserialization time; `NormalizeAttributesKey()` folds it into `Attributes` (new key wins) and clears the legacy holder so re-serialization emits only `"attributes"`. Every load seam calls it before an entry escapes.

`Attributes` is multi-valued (`key -> string[]`), lowercase keys, values verbatim. It's `null` for pre-attributes entries and degrades gracefully — no backfill; see [Attributes](#attributes).

### ChunkEntry

Per-chunk metadata within a multi-chunk memory:

```csharp
public sealed record ChunkEntry(
    int ChunkIndex,
    string? ContentPreview = null,
    string[]? Keywords = null,
    Dictionary<string, int>? TermFrequencies = null);
```

### EphemeralEntry

In-memory-only entries (prefix `~`):

```csharp
public sealed record EphemeralEntry(
    string Name,
    string Artifact,          // stored inline (no file)
    long OriginalBytes,
    int ChunkCount,
    DateTimeOffset CreatedAt,
    string? ContentPreview = null,
    string[]? Keywords = null,
    Dictionary<string, int>? TermFrequencies = null,
    DateTimeOffset? UpdatedAt = null,
    ChunkEntry[]? ChunkEntries = null,
    Dictionary<string, string>? FileHashes = null,     // JSON "codeRefs": same drift baseline as ArtifactEntry
    Dictionary<string, string[]>? Attributes = null);  // JSON "attributes"
```

### Index File (v3)

```csharp
public sealed class IndexFile
{
    public int Version { get; set; } = 3;
    public List<ArtifactEntry> Entries { get; set; } = [];
}
```

Stored as `index.json` in each scope directory. Backward-compatible with v2 (entries without TF data get BM25 score 0).

### Search Result Types

```csharp
public abstract record SearchResult(double Score);

public sealed record EntryResult(ScopedArtifact Item, double Score) : SearchResult;
public sealed record ChunkEntryResult(ScopedArtifact ParentItem, ChunkEntry Chunk,
    int TotalChunks, double Score) : SearchResult;
public sealed record TopicResult(string Scope, string TopicName, string Description,
    int EntryCount, string[]? Tags, double Score) : SearchResult;
```

## Search System

Search combines three scoring mechanisms into a single ranked result set.

### Scoring Formula

```
finalScore = weightedAttributeScore + (normalizedBm25 * 5.0) + supplementalScore
```

Where:
- **weightedAttributeScore**: Attribute-specific matching (0-100+ points)
- **normalizedBm25**: BM25 relevance normalized to 0-100 via min-max
- **supplementalScore**: Plugin-provided scores (e.g., cosine similarity from embeddings)

### Weighted Attribute Scoring

> **The table below is a restatement, not the source of truth.** The rubric lives in
> `ScoringWeights` (`Scrinia.Core.Search`) — one record, read directly by
> `WeightedAttributeScorer`. If this table and that record ever disagree, the record is right.

Each query term is matched against entry attributes with different weights:

| Match Type | Score |
|------------|-------|
| Exact name match | 100 |
| Attribute pair match (`key:value` query term matches an `Attributes[key]` value) | 50 |
| Keyword exact match | 40 |
| Attribute value exact match (bare query term equals an attribute value, any key) | 40 |
| Name starts with | 30 |
| Name contains | 20 |
| Keyword contains | 12 |
| Attribute value contains (query term is a substring of an attribute value) | 12 |
| Content preview contains | 5 |

(There is no separate tag or description scoring: `tag` is an ordinary attribute key scored via the attribute rows, and a curated description lives in `ContentPreview`.)

Attribute scores are multiplied by a per-key weight (`KnownAttributes.AttributeWeight(key)`, seeded at 1.0 so it changes no score today; the curation knob that replaced the old EntityKeys-only graph gate). **Legacy entries:** the scorer reads only `entry.Attributes`; `key:value`-shaped legacy `Keywords` (e.g. `person:maria` written before unification) are promoted into `Attributes` by the load-time forward-fill (`AttributeMigration.Forward`), so pre-migration entries score identically without a re-index — the on-the-fly key:value re-parse that once lived in the scorer was retired in P6.5. See `WeightedAttributeScorer.ScoreEntryTerm` in `Scrinia.Core.Search`.

**Chunk scoring** uses parent metadata at half weight plus chunk-specific attributes.

**Topic scoring** follows a similar pattern (exact topic name: 100, tag match: 50, etc.).

**Multi-term intersection bonus:** `(matchedTerms - 1) * 15` -- rewards entries matching multiple query terms.

### BM25

Standard BM25 with parameters `k1 = 1.5`, `b = 0.75`:

- **IDF:** `ln((N - df + 0.5) / (df + 0.5) + 1.0)`
- **TF normalization:** `(tf * (K1 + 1.0)) / (tf + K1 * (1.0 - B + B * (docLen / avgDocLen)))`
- Scores are min-max normalized to 0-100 across the result set

Corpus statistics (average doc length, document frequencies) are computed directly by
`Bm25Scorer.ComputeCorpusStats` on every `WeightedAttributeScorer.Search`/`SearchAll` call
(`WeightedAttributeScorer.cs:80-82`, `:141-143`) — not cached. A per-scope cache for them was
carried on `CachedIndex` and never called; it was deleted on 2026-08-10. See **CachedIndex** below.

### Keyword TF Boosting

Agent-provided keywords get a +5 boost to their term frequency. Auto-extracted keywords get +2. This ensures user-specified keywords rank higher in BM25 scoring.

### Top-K Selection

Candidate K and the final result limit are separate. Scope, excluded-topic, path/address, and
attribute eligibility are applied before lexical ranking and truncation to K. The pipeline ranks
with the context-augmented query and materializes one bounded eligible snapshot for supplemental
contributors, entity scoring, final search, and surfaced facts. Topic-support entries preserve
topic-summary parity with direct search.

Final results use a min-heap (`PriorityQueue`). Ties are deterministic; final score ties use
creation date descending.

### Deduplication

An inline `bestPerMemory` dictionary (keyed by `scope|name`) ensures only the highest-scoring variant of each memory appears in results.

### Text Analysis

```csharp
public static class TextAnalysis
{
    public static IReadOnlyList<string> Tokenize(string text);
    public static Dictionary<string, int> ComputeTermFrequencies(string text);
    public static string[] ExtractKeywords(string text, int topN = 25);
    public static string[] MergeKeywords(string[]? agentKeywords, string[] autoKeywords, int maxTotal = 30);
    public static (Dictionary<string, int> TF, string[] Keywords) AnalyzeText(string text, int topN = 25);
}
```

Tokenization: split on non-alphanumeric characters, lowercase, filter stop words (~200 common English words), require minimum 2 characters.

### Attributes

`Attributes` (`ArtifactEntry.Attributes`, `Dictionary<string, string[]>?`, JSON `"attributes"`) is the unified set of typed facets that replaced the older scheme of encoding structured facts as prefixed keywords (`person:maria`, `file:src/Foo.cs`). An attribute key is either an **entity key** (recommended entity vocabulary) or a **reference key** (feeds cross-reference tracking); everything else is scalar state. The write path (MCP `Store`/`Append`) routes agent keywords, mechanical content extraction, and reference extraction into `Attributes` instead of emitting prefixed keywords. Prefixed-keyword *writes* are retired; prefixed-keyword *reads* are preserved everywhere via dual-read (see below), so entries written before this change keep working with no re-index or backfill.

#### KnownAttributes

`KnownAttributes` in `Scrinia.Core.Search` is the single schema point — the only place that knows which attribute keys are multi-valued and what merge semantics apply:

```csharp
public static class KnownAttributes
{
    public const int MaxAttributes = 32;
    public static readonly IReadOnlySet<string> EntityKeys;     // person, place, org, thing, topic
    public static readonly IReadOnlySet<string> ReferenceKeys;  // file, ref
    public const string ReviewKey = "review";
    public const string TagKey = "tag";                          // legacy free-form tags migrate here
    public const string AnchorKey = "anchor";                    // legacy retrieval anchors migrate here
    public static readonly IReadOnlySet<string> RecommendedKeys; // person, place, org, thing, topic, file, ref, review
    public static double AttributeWeight(string key);            // per-key scoring multiplier, seeded at parity (1.0)
    public static bool IsMultiValued(string key);                // true for EntityKeys ∪ ReferenceKeys
    public static Dictionary<string, string[]>? Merge(existing, incoming);
}
```

- **Entity keys** (`person`, `place`, `org`, `thing`, `topic`) union-merge and are the **recommended** entity vocabulary — but every attribute key feeds the entity graph uniformly, not just these five (see Dual-read below).
- **Reference keys** (`file`, `ref`) union-merge and feed References/Link/orphan tracking plus the **`FileHashes`** builder.
- Per-key influence is a soft weight (`KnownAttributes.AttributeWeight`, seeded at parity — every key `1.0`) that **replaced the old "only EntityKeys build the graph" gate**. `RecommendedKeys` is guidance plus the weight-table seed, not a gate.
- All other keys are scalar: `Merge` treats incoming as newer-wins (first-wins would freeze stale statuses), except multi-valued keys which union-dedupe with existing values keeping their original order.
- `MaxAttributes = 32` caps total attribute keys per entry; existing keys count first against the cap.

#### AttributeExtractor

`AttributeExtractor` in `Scrinia.Core.Search` is the single write-time entry point with three sources feeding one attribute bag:

- **`RouteKeywords(keywords)`** -- agent-keyword routing. Splits agent-supplied keywords into attribute-shaped (`key:value` -> `Attributes`) and plain (stay in `Keywords`, still participate in BM25/keyword scoring).
- **`Extract(content)`** -- mechanical content extraction. Line-by-line scan for `key: value` line-starts, `**Bold:** value` labels, and inline prose `key:value` pairs (`MatchInlineProsePairs`, routed only for the eight recommended keys — see CHANGELOG); frontmatter lines pass through untouched, not specially gated. Skips properly closed code-fenced blocks only — an unterminated fence (an opening ``` with no matching closer) is not a fence at all, and everything after it extracts as ordinary prose. Both the fence rule and the frontmatter pass-through come from the shared `ProseScanner`.
- **`ExtractRefAttributes(content)`** -- reference extraction. Delegates to `ReferenceExtractor.ExtractFileRefs`/`ExtractMemoryRefs`, both fence-aware via the same `ProseScanner` rule as `Extract`, and packages results as `{file: [...], ref: [...]}`, replacing the retired `file:`/`ref:` prefixed-keyword emission.
- **`SeedTermFrequencies(attributes, tf)`** -- not an extraction source; seeds attribute keys/values into the term-frequency dictionary (+3/token) so BM25 also matches attribute vocabulary.

`ReferenceExtractor` itself (source-generated regexes for file-path and memory-reference patterns) has exactly two production call sites, and `ExtractRefAttributes` above is one of them, not the only consumer: `AttributeExtractor.ExtractRefAttributes` (`AttributeExtractor.cs:277-278`) and, separately, `MaintenanceEventSink`'s private `AutoLinkAndDetectOrphans` helper, which calls `ReferenceExtractor.ExtractMemoryRefs` directly (`MaintenanceEventSink.cs:100`) to find `[[wiki-link]]` targets for auto-linking and orphan detection. Neither site emits a `ref:`/`file:`-prefixed keyword any more — that emission is retired — but `ReferenceExtractor` is not merely a private implementation detail sitting only underneath `ExtractRefAttributes`; the CHANGELOG's "three write-path consumers" (`ExtractRefAttributes`, `MaintenanceEventSink`, `Extract`/`ExtractMentionSpans`) names the same two direct callers plus `Extract`/`ExtractMentionSpans`, which do not call `ReferenceExtractor` at all.

#### Dual-read

Every consumer of typed/prefixed data reads **both** shapes so old entries (which only ever had prefixed `Keywords`, since `Attributes` didn't exist yet) and new entries (which have `Attributes`) retrieve identically:

- **Scorer** (`WeightedAttributeScorer.ScoreEntryTerm`): scores `entry.Attributes` at 50/40/12 (pair/exact/contains — see the scoring table above), each × the per-key `AttributeWeight`. It does not re-parse `key:value` `Keywords` — that on-the-fly parse was retired in P6.5; the load-time forward-fill promotes such keywords into `Attributes` first.
- **Entity graph** (`EntityFactDerivation.Derive`, which `EntityGraphScoreContributor` runs via `EntityFactStore.ScoringAdjacency`; `EntityGraph.Build` was deleted): unions `ParseEntityKeywords(keywords)` (legacy `type:value` keywords) with `ParseEntityAttributes(attributes)`, which yields a graph node for **every** attribute key uniformly — not just `KnownAttributes.EntityKeys`. Any attribute key you name consistently links memories that share its value; the entity/recommended keys are guidance, not a graph gate.
- **Orphan/reference tracking** (`MaintenanceEventSink`): checks both `Keywords` for `ref:...` and `Attributes["ref"]` for inbound references; new links are written forward-only into the `ref` attribute (no more `ref:` keyword emission) via `KnownAttributes.Merge`.
- **FileHashes**: the derivation pipeline (`MemoryDerivation`) unions the explicit `codeRefs` parameter with `Attributes["file"]` (deduplicated case-insensitively) before hashing into `FileHashes` — on `ArtifactEntry` for a persistent write and identically on `EphemeralEntry` for an ephemeral one. An append hashes only newly-referenced paths and keeps an already-baselined path's original hash (see CHANGELOG); a plain `remember()`/`Store` hashes every referenced path. Because `Attributes["file"]` is itself fence-aware (`ExtractRefAttributes`, above), a path mentioned only inside a properly closed fenced block no longer reaches `Attributes["file"]` and therefore no longer reaches `FileHashes` either — a drift baseline can only be built from a file reference that reads as a real reference, not a pasted example.

This is a **forward-only migration**: `Attributes` is `null` for entries written before this change and stays `null` — there is no backfill script. Dual-read at the scorer/graph/orphan layers is what keeps old and new entries behaving the same, permanently, without a re-index.

## Storage

### IMemoryStore

The whole storage contract, filesystem layout included. Abstract members a conforming store must
implement, plus default-bodied helpers a store may override for efficiency.

`IMemoryStore` is the single local filesystem store contract. `FileMemoryStore` is its sole
implementation, so paths, the workspace root, and bundle operations are part of the same contract
instead of being split behind a local-versus-remote distinction.

The store contract preserves the rule that a store must not answer a failure with an empty result:
an unavailable or corrupt scope is an error, not an empty search result. Anything reading through
`LoadIndex` cannot distinguish an unavailable or corrupt scope from one with no entries, so a store
that cannot answer must throw. `FileMemoryStore` is the current filesystem implementation.

**Naming:** `ParseQualifiedName`, `FormatQualifiedName`, `IsEphemeral`, `SanitizeName`

**Write surface:** `StoreAsync`, `AppendAsync` — raw caller input in, the entry the store itself
derived out (see `MemoryDerivation`, above). Neither has a default body: a store added later must
say how it derives rather than silently inheriting a derivation that is wrong for it.

**CRUD:** `ReadAsync(MemoryAddress)`, `WriteAsync(MemoryAddress, MemoryWrite)`,
`DeleteAsync(MemoryAddress)`, plus index maintenance through `LoadIndex`, `Upsert`, and `Remove`

**Listing:** `ListScoped(scopes)`

**Search:** `SearchAll(SearchQuery query)` — one method, no overloads, no default body. Every
overload it replaced (with/without supplemental scores, with/without `excludeTopics`) carried a
default body that post-filtered after the scorer's top-K cut, so a store that didn't override it
silently returned fewer than `Limit` results for a selective filter — the top-K cut had already
thinned the candidate set before the filter ever ran. Collapsing every filter dimension
(`PathPrefix`, `ExcludeTopics`, `AttributeFilter`, `Scopes`, `Limit`, `Candidates`,
`SupplementalScores`, `EntityGraphScores`) onto one `SearchQuery` record and removing the default
body means a store that cannot pre-filter now fails to compile instead of silently under-filling —
the contract moved from "correct if you remembered to override it" to "correct or it doesn't
build."

Search applies scope, excluded-topic, path, and attribute eligibility before retaining the lexical
candidate window. Topic-support candidates preserve topic-summary semantics. Contributors and entity
facts consume that same bounded snapshot; the final result limit is separate.

**Ephemeral:** `RememberEphemeral`, `ForgetEphemeral`, `GetEphemeral`

**File I/O:** `WriteArtifactAsync`, `ReadArtifactAsync(MemoryAddress)`, `DeleteArtifact`

**Naming / addressing:** `MemoryAddress` is the typed logical address. `ArtifactEntry.Address` carries
the logical address and `ArtifactEntry.ArtifactKey` is an opaque, store-relative persistence key.

**Archive:** `ArchiveVersion`

**Filesystem layout:** `ArtifactPath`, `FindArtifactPath`, `GetStoreDirForScope`, `WorkspaceRoot`,
`ListTopicArtifacts`, `ImportTopicEntries` — the six merged back in from `ILocalMemoryStore`.
`WorkspaceRoot` is the authority for the workspace's on-disk layout: hand-authored file entities,
bundle export/import, conflict scanning and drift detection all resolve from it.

**Topics:** `DiscoverTopics`, `GatherTopicInfos`

**Scope resolution:** `ResolveReadScopes(scopes)`

**Default-bodied members (a store may override for efficiency):** `TryUpdateEntry`
(non-atomic load→mutate→Upsert unless overridden), `TryUpdateEphemeral` (the ephemeral counterpart
— an unsynchronized load→mutate→store unless overridden; `FileMemoryStore` overrides it as a
compare-and-swap retry loop instead, so a concurrent writer's change is re-applied to the newer
entry rather than silently lost), `GetEntityFactStore` (rebuilds fresh from `ListScoped`
unless overridden), `AnyScoped(scopes, predicate)` — "does any entry match?" without the caller
materializing a listing to test; the default is `ListScoped(scopes).Any(predicate)`, which is
correct but still builds the full listing first, and `FileMemoryStore` overrides it to enumerate
scope by scope and return on the first match — `ListScoped(scopes, excludeTopics)`, and
`ResolveReadScopes(scopes, excludeTopics)` — the last two post-filter
`ListScoped`/`ResolveReadScopes` by scope, which is
cheap and correct even unoverridden because scope exclusion is a set-membership check, not a
top-K-sensitive filter the way `pathPrefix`/`attributeFilter` were. `FileMemoryStore` overrides
both for efficient scope-level filtering. There is no `SearchAll(..., excludeTopics)` default
overload anymore: `excludeTopics` for search travels as `SearchQuery.ExcludeTopics`, one field
among several on the single `SearchAll` method above, not a separate overload. The MCP `list` and
`search` actions accept `excludeTopics` so agents can scope queries to specific topic subsets.

Both lexical selectors also have default bodies:
`SelectLexicalCandidates(scopes, excludeTopics, query, limit)` remains compatible with existing
callers. The additive `SelectLexicalCandidates(SearchQuery query, int limit)` applies complete
query eligibility before lexical ranking and K, using the complete listing or the supplied
candidate set constrained to the resolved scopes. `FileMemoryStore` overrides this selector
under workspace admission. This overload does not change the abstract `SearchAll` contract.

`GenerateContentPreview` is not a store member at all — it moved to the static
`Scrinia.Core.ContentPreview.Generate(string content, int maxLength = 500)`. The two store
implementations that existed then had byte-identical bodies that touched no store state, so it was
never really a storage operation.

`SaveIndex` is not on the interface either: it has no production caller outside the store itself,
so it is a public member of `FileMemoryStore` directly.

### FileMemoryStore

Instance-based implementation of `IMemoryStore`, and the only one. One instance per workspace.

**Scope mapping:**
- `"local"` → `.scrinia/store/`
- `"local-topic:{name}"` → `.scrinia/topics/{name}/`
- `"ephemeral"` → in-memory `ConcurrentDictionary`

**Name sanitization:** Strips `..`, replaces `/` and `\` with `_`, removes invalid filename characters, extracts final path component.

**Artifact resolution:** callers provide a parsed `MemoryAddress` to `ReadAsync` or
`ReadArtifactAsync`. Inline NMP/2 decoding is an MCP recall concern, not a store address form.
The store owns mapping the opaque `ArtifactKey` to its local filesystem and never accepts caller
supplied file URIs.

### Journaled Bundle Restore Recovery

Workspace bundle imports coordinate ordinary memories and skill/agent body-metadata pairs with
one operation-owned manifest at `.scrinia/.bundle-restore-journal.json`. Under exclusive workspace
admission, preparation captures bounded prior bytes and proposed hashes before publication.
Recovery validates the complete manifest, approved relative paths, member kinds, physical
collisions, and every current member before replaying any member. It never rereads the source
bundle or rolls unrelated workspace files back.

A `prepared` journal restores only its owned members to their prior state. A `committed` or
`rolled-back` journal verifies the corresponding terminal state and cleans evidence. A proven
durable commit remains successful when evidence cleanup fails; returned counts describe committed
changes. An uncertain decision, unknown/legacy evidence, or a member matching neither expected
state is refused with `BUNDLE_RECOVERY_REQUIRED`. Retained bundle evidence blocks subsequent
persistent operations until supported verification and cleanup succeed. Explicit ephemeral-only
operations remain available.

The existing standalone per-scope `.restore-journal.json` is a separate roll-forward protocol.
Its `prepared` state replays final bytes; `completed` verifies and cleans. Its limits remain
1,000 entries, 96 MiB per body, 16 MiB per sidecar, 128 MiB decoded total, and a
181,009,068-byte journal document. Malformed or unreplayable scope evidence produces
`RECOVERY_BLOCKED`. Verified terminal scope/coordinator evidence can permit a bounded read
snapshot without rewriting proof-covered bytes. New scope mutations, standalone import,
multi-memory execution, and bundle publication refuse retained scope/coordinator evidence.
A legacy index projection that needs a sidecar rewrite also refuses until evidence is retired.

The workspace bundle journal instead permits 2,000 owned members, 128 MiB aggregate prior bytes,
and 230,161,068 encoded JSON bytes. See [bundle limits](../security.md#bundle-and-journal-limits)
and [evidence-preserving recovery](../troubleshooting.md#bundle-recovery-required).
Derived vectors, facts, and configuration are outside this authoritative rollback manifest.
Committed file-entity cleanup verifies both expected existence and content. An absent member
requires a positively inspected missing path under validated directory parents; an unexpected
file, directory, link, or inspection failure retains evidence. Explicit recovery and maintenance
cleanup remain available under their existing locks.

### CachedIndex

Each persistent scope caches index entries in `CachedIndex`. `LoadIndex` returns its
`IReadOnlyList<ArtifactEntry>` directly; writes publish a replacement list rather than mutating
a previously handed-out list.

Before reuse, the store checks a sorted fingerprint of sidecar filenames and SHA-256 content
hashes under workspace admission. Changed names or bytes reload the authoritative index,
including changes made by another process or by source-control operations. Topic discovery
enumerates current directories rather than relying on an indefinite process cache. A cache hit
therefore includes authoritative filesystem I/O and hashing; it is not O(1).

Read recovery preparation runs before a cached snapshot can be returned. Mutations read their
authoritative predecessor under the appropriate ownership boundary. Copy-on-write retains list
ownership safety without making an old cached list an authoritative mutation baseline. Corpus
statistics are computed by `WeightedAttributeScorer` for each search's candidate set.

### Artifact LRU Cache

50 MB bounded cache for decoded artifact content:
- LinkedList-based LRU eviction
- Thread-safe via `lock`
- Keys include modification timestamp for staleness detection
- Cache miss reads from filesystem and populates cache

### Cross-Process File Locking

Persistent operations first enter workspace admission through
`.scrinia/.bundle-restore.lock`, before scope or file-entity locks. Ordinary persistent reads,
writes, and export snapshots share admission; bundle publication and recovery require exclusive
admission. Internal nested work receives an explicit validated `WorkspaceLease`; a cache hit does
not bypass admission. A read that discovers recovery work releases shared admission before
requesting exclusive recovery, avoiding a lock upgrade while readers remain admitted.
Ephemeral-only operations bypass filesystem admission.

`FileLock` supplies OS-enforced shared/exclusive scope locks with bounded contention retry.
Permission and unrelated I/O errors fail closed. A leftover filename is not proof of ownership.
Cold index loads also use scope locks; cache reuse still performs admission and freshness checks.
All cooperating writers must use this protocol: stop every older writer before upgrading.
Uncoordinated filesystem editors can still race between a path check and its subsequent open.

Tracking recovery has separate version-1 evidence for exactly `config.json`, `.gitignore`,
`.gitattributes`, and `merge.config`. Prepared recovery accepts proven member states; ambiguous
legacy `complete` and unreadable evidence remain blocked for investigation. Preserve the whole
workspace and journal, never blindly delete evidence. The limits are 16 MiB per member state,
128 MiB decoded total, 192 MiB serialized journal, and 64 UTF-8 bytes per operation ID.
Tracking obtains exclusive workspace admission before the configuration lock; configuration-only
mutations recheck evidence under their lock. Windows lock identities normalize absolute path case,
changing filenames from older builds, so mixed-version configuration writers must be stopped.

### Concurrency

- Per-scope `ReaderWriterLockSlim` protects in-process index CRUD.
- Persistent index snapshots use copy-on-write publication and authoritative freshness checks.
- Topic discovery observes the current directory set.
- `ConcurrentDictionary` holds ephemeral state and lock/cache registries.
- Workspace admission precedes cross-process scope/entity ownership; bundle recovery and
  publication hold the exclusive workspace lease across all owned members.

### Keyword-Only Index Scans

Index entries carry keywords, so filtering need not decode artifacts. `LoadIndex(scope)` returns
the cached list without copying after admission, recovery preparation, and sidecar fingerprint
validation. Keyword filtering can reduce subsequent payload work; cache validation still reads
authoritative sidecar bytes.

### Version Archiving

When a memory is overwritten, the old version is copied to
`.scrinia/memories/{scope}/versions/{name}_{yyyyMMdd-HHmmss-fff}.nmp2`, pruned to the 10 most
recent. This provides a safety net for accidental overwrites.

The stamp is millisecond-granular and an archive is **never** overwritten — a `-2`, `-3` … suffix
is appended if that millisecond is already taken. It used to be second-granular and copied with
`overwrite: true`, so two archives of one subject inside the same second silently replaced each
other; two overwrites in a row is the ordinary shape of an agent correcting itself mid-turn, not a
contrived race.

The memory's `.meta.json` sidecar is archived alongside the body under the same base name, so a
version restored by hand arrives with the keywords, attributes and drift baseline it had at the
time. Recovery is still manual and unassisted: no CLI verb, MCP tool, or code path reads
`versions/` back. See [Version retention](../troubleshooting.md#version-retention).

## MemoryStoreContext

AsyncLocal-based dispatch for routing operations to the correct store:

```csharp
public static class MemoryStoreContext
{
    public static IMemoryStore? Current { get; set; }  // AsyncLocal<IMemoryStore?>
}
```

Set at startup by the CLI. MCP tools read `Current` to find the active store.

## SessionBudget

Tracks token consumption within a session:

```csharp
internal static class SessionBudget
{
    public static void RecordAccess(string memoryName, long charsLoaded);
    public static long TotalCharsLoaded { get; }
    public static int EstimatedTokensLoaded { get; }  // chars / 4
    public static IReadOnlyDictionary<string, (long Chars, int EstTokens)> Breakdown { get; }
}
```

Records are made when `show()` or `get_chunk()` decode memory content.

## Extensibility Interfaces

### ISearchScoreContributor

Plugin hook for providing supplemental search scores (e.g., semantic similarity):

```csharp
public interface ISearchScoreContributor
{
    Task<IReadOnlyDictionary<string, double>?> ComputeScoresAsync(
        string query, IReadOnlyList<ScopedArtifact> candidates,
        IMemoryStore store, CancellationToken ct);
}
```

Each contributor receives the same bounded eligible lexical candidate snapshot, selected with
the context-augmented query after scope/path/attribute filtering. Entity derivation and surfaced
facts use that snapshot too. Contributors cannot broaden retrieval beyond it.

Scores are keyed by `"{scope}|{name}"` for entries or `"{scope}|{name}|{chunkIndex}"` for chunks.

**`chunkIndex` is a 1-based index over the memory's nmp2 content elements** — the same value
`ChunkEntry.ChunkIndex` carries, which is what the scorer probes with. It is *not* a character-window
ordinal and it is *not* 0-based. Getting this wrong is invisible: a mis-keyed chunk score simply
never matches, and the entry-level key silently absorbs the lookup, so search keeps working and
quietly loses its chunk-level signal. Use `VectorScoreProjection.Project`, which owns the mapping
from stored vectors to these keys, rather than composing them by hand.

Dispatch via `SearchContributorContext`:
- `.Current` (AsyncLocal): what a host serving concurrent callers would set per call
- `.Default` (static): set at startup by the CLI, and what actually carries today

### IMemoryEventSink

Plugin hook for reacting to store/append/forget events. Takes `IPluginStore`, not the full
`IMemoryStore`. The typed plugin-store contract has two members: `ResolveAddress(MemoryAddress)`
returns the logical address's `(Scope, Subject)` storage coordinates, and
`ReadArtifactAsync(MemoryAddress)` reads the artifact addressed by that typed logical value. A
sink needing broader store operations can explicitly cast to `IMemoryStore`; Core's own handlers
use `CoreSinkStore.Require` for that case:

```csharp
public interface IMemoryEventSink
{
    Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct);
    Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct);
    Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct);
}
```

`IPluginStore` does not expose URI, physical-path, or name-only resolver methods. `MemoryAddress`
is the validated logical address passed through callbacks, while `ArtifactEntry.ArtifactKey` is an
opaque, store-relative persistence key. Plugins pass the address back to `ReadArtifactAsync`; they
must not interpret or concatenate an artifact key as a filesystem path.

Dispatch via `MemoryEventSinkContext`:
- `.Current` (AsyncLocal): per async flow
- `.Default` (static): CLI fallback, and what actually carries today

This is the MCP path hook mechanism. `IMemoryOperationHook` is the other one, and the rule that
kept them apart still stands: a component registered as both must fire exactly once.

## JSON Serialization

All serialized types use source-generated `JsonSerializerContext` for trimming safety:

| Context | Location | Purpose |
|---------|----------|---------|
| `FileStoreJsonContext` | Core | Index file persistence |
| `BundleJsonContext` | Core | Export/import bundles |
| `StoreJsonContext` | CLI | CLI store operations |
| `ScriniaMcpJsonContext` | Mcp | Skill and agent file sidecar metadata (`SkillFileMeta`, `AgentFileMeta`) |
| `ConfigJsonContext` | CLI | Workspace config file |
| `PluginClientJsonContext` | CLI | Plugin MCP communication |
| `CliJsonContext` | CLI | `--json` CLI output |

Without source-gen contexts, the trimmed CLI binary silently fails to serialize/deserialize.

## Resilience

`Scrinia.Core.Resilience` provides transient failure handling for HTTP-based providers (embedding and chat). Zero external dependencies — pure .NET.

### TransientDetector

Classifies failures as transient (retryable) or permanent:
- **HTTP status codes**: 429 (Too Many Requests), 500, 502, 503, 504
- **Exceptions**: `TimeoutException`, `IOException`, `SocketException`, `HttpRequestException` wrapping socket/IO errors

### RetryPolicy

Static class with async and sync retry methods using exponential backoff with jitter:
- **Backoff formula**: `baseDelayMs * 2^attempt + random(0, baseDelayMs)`
- **Retry-After header**: Honored when the response is `HttpResponseMessage` (uses the longer of computed delay or header value)
- **Exception retry**: Catches transient exceptions (via `TransientDetector`) and retries up to `MaxRetries`
- **Result retry**: Caller provides `isTransient` predicate for result-based retry decisions

### CircuitBreaker

Per-provider circuit breaker with three states:
- **Closed**: Normal operation. Consecutive failures tracked via `Interlocked` operations.
- **Open**: Failures reached threshold. All requests rejected with `CircuitBreakerOpenException`. Transitions to HalfOpen after cooldown expires.
- **HalfOpen**: Cooldown expired. Next request is a probe — success closes the circuit, failure re-opens it.

Thread-safe via `Interlocked` and `Volatile` operations (no locks).

### CircuitBreakerRegistry

Static `ConcurrentDictionary<string, CircuitBreaker>` registry. Providers register their circuit breaker instances by name so the `/health/details` endpoint can report state without tight coupling.

### Configuration Records

```csharp
public sealed record RetryOptions(int MaxRetries = 3, int BaseDelayMs = 200);
public sealed record CircuitBreakerOptions(int Threshold = 5, int CooldownSeconds = 30);
```

`EmbeddingOptions` (Core) exposes `MaxRetries`, `RetryBaseDelayMs`, `CircuitBreakerThreshold`, and `CircuitBreakerCooldownSeconds` properties that map to these records. They cover the remote embedding providers (Ollama, OpenAI, Voyage AI, Azure, Google); nothing else in the repository makes an outbound HTTP call.

## Shared Base Abstractions

The shared-memory surface (`scri shared serve`) is fully owned by Core. `Scrinia.Mcp` references Core
and implements the shared handlers and envelope serialization; the executable owns only host adapters.
Every shared abstraction below lives in `Scrinia.Core.Configuration` or `Scrinia.Core`:

- `IHomeConfigStore` / `HomeConfigStore` — the per-user shared-base registry store (`HomeConfig`,
  `SharedBaseEntry`, `SharedBasesMap`).
- `IWorkspaceConfig` / `WorkspaceConfig` — the flat `.scrinia/config.json` workspace settings,
  including the `Scrinia:Shared:Enabled` scalar and enabled-base selection.
- `ISharedBaseRegistry` / `SharedBaseRegistry` — canonical-name validation, root evidence
  (directory `.git` or exact `gitdir:` worktree marker), containment/collision detection, and enabled
  filtering. Disabled registry names and roots are never surfaced.
- `ISharedBaseRuntimeFactory` / `SharedBaseRuntimeFactory` — opens a base into a keyed runtime.
- `ISharedBaseRuntime` / `SharedBaseRuntime` — per-base snapshot, generation/fingerprint, read/mutation
  refresh leases, `RevalidateBeforeMutation` (F0), `CommitMutation` (F1), and `Refresh`.
- `SharedClassifier` — the single authority for the durable classification matrix and the derived-path
  union excluded from fingerprints.
- `SharedManifest` — the deterministic authoritative mutation manifest (§7.2).
- `SharedServer` — the MCP selector/orchestration surface (enabled-only selectors, base provenance,
  persistent-only address parsing, and operation-based Git guidance).

**Selectors and provenance.** `SharedServer.SelectBase` validates canonical-name syntax and
enabled-membership before any registry lookup; no scalar base name is echoed before that validation.
`SelectEnabledBases` preserves base provenance for every enabled base.

**Persistent-only addresses.** Every Ephemeral spelling is refused with `SHARED_EPHEMERAL_NOT_SUPPORTED`
before storage, cache access, fingerprinting, or I/O; shared inventory contains persistent entries
only.

**Enabled-only privacy.** Disabled names and roots never appear in any shared result, warning, error,
diagnostic, instruction, follow-up, or log.

**No-base startup.** An absent `Scrinia:Shared:Enabled` key makes `scri shared serve` fail before
transport with `NO_SHARED_BASES_ENABLED`.

**Refresh and freshness.** Each runtime owns a `ReaderWriterLockSlim` generation gate; `SharedSnapshot`
carries a generation/fingerprint computed over authoritative files only. `RevalidateBeforeMutation`
validates F0 while holding the mutation lease; the durable write validates and publishes F1 atomically.
Any uncertain or newly invalid result is `unknown` and blocks writes until a successful `shared_refresh`
or restart. The documented F0-to-F1 race limitation is not claimed.

**Authoritative mutation manifest.** Every successful shared mutation returns a deterministic
authoritative mutation manifest; each entry is made repository-root-relative (`.`scrinia/`-prefixed`),
`/`-separated, sorted by UTF-8 ordinal bytes, and deduplicated, retaining only exact authoritative files
(NMP bodies, memory sidecars, file-entity bodies/sidecars, skill/agent Markdown bodies/sidecars). No
control file, derived file, directory, wildcard, or directory pathspec may enter a manifest.

**Git prohibition and operation-based guidance.** Scrinia never launches `git` for any reason and never
inspects or modifies the index. Guidance is print-only, operation-based, and shell-quoted: exactly one
`git -C "<registered-root>" add -A -- '<manifest-path>' ...` command with every manifest path as an
individual shell-quoted argument, followed by the fixed commit and push. It never claims Git state.

**Phase 4 consumption rule.** Phase 4 consumes these final contracts and cannot alter the shared response
wording (§13).
