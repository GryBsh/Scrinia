# Retrieval failure classification

## Scope

- Worktree: `C:\SynologyDrive\Source\repos\workspace-scrinia\Scrinia\.claude\worktrees\unified-memory-addressing-task-1`
- HEAD: `38f1c05`
- Green baseline: `90827cd`
- The worktree already had an untracked `src/Scrinia.Server/`; it was not inspected or changed.

The recorded full retrieval run contains 11 failures. Re-running exactly those 11 tests at HEAD
before the fix produced 9 failures and 2 passes; after the boundary fix all 11 pass. The two
agentic hybrid tests remain timing/order-sensitive across broader runs, while the eight identity
failures are resolved deterministically.

## Grouped failures

### A. Logical result identity was replaced by `ArtifactKey` (8 failures)

Failures:

- `ScriniaRetrieverTests.Ingest_and_retrieve_returns_top_match`
- `ScriniaRetrieverTests.Doc_id_is_preserved_through_search_round_trip`
- `ScriniaRetrieverTests.Attribute_value_is_retrievable_even_when_absent_from_content`
- `ScriniaRetrieverTests.EntityGraph_LiftsAssociatedMemory_SharingEntity_ButNotQueryTerms`
- `AblationFlagTests.Mmr_with_low_lambda_diversifies_away_from_dominant_topic`
- `AblationFlagTests.Mmr_lambda_1_returns_relevance_order`
- `AblationFlagTests.Recency_alpha_pushes_recent_doc_above_stale_equal_match`
- `AblationFlagTests.Recency_alpha_zero_ties_resolve_by_creation_date_tiebreaker`

Evidence: every failure has the expected ranking/content behavior but an ID such as
`artifacts/d2.nmp2`, `artifacts/new.nmp2`, or `artifacts/convomem_user_5_msg_12.nmp2` instead of
the logical benchmark ID. `FileMemoryStore.SearchAll` returns `EntryResult`/`ChunkEntryResult`
carrying `ArtifactEntry`; `WeightedAttributeScorer`, entity PPR, recency composition, and MMR all
operate on that result without changing the ranking signal. `ScriniaRetriever` then calls
`ExtractDocId(er.Item.Entry.Uri)` at lines 361/366. The compatibility `Uri` view now returns
`ArtifactKey`, so the adapter emits the portable key as `RetrievalHit.Id`.

The baseline `ArtifactEntry` had independent `Name` and `Uri`; the benchmark stored `bench://<id>`
in `Uri`, which `ExtractDocId` converted back to `<id>`. The addressing commits replaced that
physical/URI carrier with `Address` plus opaque `ArtifactKey`, and `8538bc3` retained a compatibility
`Uri` view that aliases the key. That alias is the direct introducing change for these failures.

Classification: **unintended regression at the benchmark result-mapping boundary**, not a search,
entity, recency, or MMR regression. Resolution: `ScriniaRetriever` now preserves the benchmark's
logical doc ID in an explicit adapter-side map for both entry and chunk results. Retrieval resolves
through that map before constructing `RetrievalHit`, without changing ranking or ArtifactKey
validation. Do not make `ArtifactKey` carry logical IDs or reintroduce a public URI property.

### B. Agentic retrieval failures (3 failures in the recorded run)

Failures:

- `AgenticIngestEndToEndTests.RunGroupedAsync_AgenticIngest_HybridPath_SurfacesLexicallyMismatchedGoldDoc`
  (recorded `RecallAt10 == 0`; passed in the exact rerun)
- `AgenticScriniaRetrieverTests.Hybrid_SurfacesSemanticMatch_ThatBm25Misses`
  (recorded empty hits; passed in the exact rerun)
- `AgenticScriniaRetrieverTests.ToolExecutionThrows_InvalidPath_SurfacesInStats_DoesNotAbortRun`
  (`ToolCallErrors == 0`; reproduced)

The hybrid path is: agent episode -> typed `FileMemoryStore` writes -> `EmbeddingReindexer` into the
shared `VectorStore` -> `HybridReranker`/`CompositeSearchScoreContributor` -> `SearchAll` ->
provenance mapping. The two hybrid symptoms are not stable at HEAD and are not explained by
`ArtifactKey`: agentic hits map through `_provenance` and buffered doc IDs, not `ArtifactEntry.Uri`.
Treat them as agent/test isolation or episode-order flakes until a serialized run proves otherwise.

The invalid-path failure is reproducible at `38f1c05`, with the assertion stack trace ending at
`AgenticScriniaRetrieverTests.cs:293`: `ToolCallErrors` is `0` instead of `>= 1`. `RetrieveAsync`
does not throw and the final hit list is empty, so this is not a retrieval or environment failure.

The test's exception premise is stale. The call reaches `ScriniaMcpTools.MemoryRemember`, which
rejects `/findings/trip:lisbon` at the MCP boundary: `TryParseLogicalAddress` calls `LooksLikeUri`,
sees the colon, and returns a structured `UnsupportedInputForm` error (`MemoryTools.cs:49-56,
73-75`) before the store or `PathParser.Parse` can run. `DispatchRememberAsync` returns that YAML
response as `(result, false)` (`AgenticIngestHarness.cs:224-225`), because the harness only marks
JSON parse failures and thrown tool exceptions as errors. `RunEpisodeAsync` therefore leaves
`malformedCount` at zero, and `RunSessionEpisodeAsync` propagates zero at
`AgenticScriniaRetriever.cs:416-430` into `Stats.ToolCallErrors`.

This boundary behavior was introduced by logical-address enforcement in `3a822e8` (typed address
work completed by `2582ae1`/`8538bc3`); the identity-only change in `38f1c05` is unrelated. The green
baseline's old facade allowed the call to reach the lower store path and throw, so the old rationale
no longer describes HEAD. Classification: **agentic harness contract mismatch exposed by an
intentional production input-boundary change**, not a production retrieval regression and not an
environment issue. The desired invariant (an errored tool call is counted and the run continues)
remains reasonable, but the error signal changed from an exception to a structured MCP error.

Recommended minimal fix: have the harness adapter classify a structured MCP error response from
`MemoryRemember` and the other dispatchers as `IsError: true` before returning it (ideally centrally
in `ExecuteCallAsync`). This preserves the test and metric semantics while retaining the new address
boundary. If the metric is intentionally narrowed to exceptions, update this test's comment/assertion
instead; that would make `ToolCallErrors` inconsistent with its documented malformed/errored-call
contract.

Resolution: `ExecuteCallAsync` now classifies a dispatcher result whose first structured YAML line is
`status: error` as an error, while preserving dispatcher error flags and exception handling. A theory
regression covers both `UNSUPPORTED_INPUT_FORM` and `MALFORMED_ADDRESS`; the original invalid-path
retriever test now counts the boundary response without aborting the episode.

## Contract ruling

Replacing the public `ArtifactEntry.Uri` with `ArtifactEntry.ArtifactKey` is an **approved contract
change**. The design spec explicitly requires a portable, store-relative, opaque key, with only
`FileMemoryStore` mapping it to a physical path; it also explicitly excludes retrieval ranking and
benchmark identity redesign. Therefore the approved change does **not** authorize using
`ArtifactKey` as a logical retrieval result ID. The eight deterministic failures are an unintended
regression caused by conflating those identities.

## Verification

The exact 11-test filter before the fix at `38f1c05`: **9 failed, 2 passed**. After the fix:
**11 passed, 0 failed, 0 skipped**. The full retrieval test project after the fix: **272 passed,
0 failed, 0 skipped**. Added whole-document and chunked boundary regressions prove sanitized/
portable storage keys do not escape as public IDs; the new harness theory covers both categorized
structured address-error responses.
