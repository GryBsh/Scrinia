# Review Remediation — Design (Severity-Waved, 41 Findings)

**Date:** 2026-09-06
**Source:** Whole-codebase review 2026-09-06 — 41 findings: 6 Critical, 15 Major, 20 Minor across 5 subsystems (Core/Store, Encoding/NMP2, Embeddings/VectorStore, MCP/CLI surface, docs/tooling). Recorded at `/findings/review-2026-09-06`.
**Status:** `Proposed` — validated spec; implementation requires three PRs (one per wave).
**Supersedes:** Nothing. Prior remediation specs (`2026-08-05`, `2026-08-06`) remain history. This spec is the single authority for the September wave.

---

## 1. Overview & Goals

### Why this spec exists

41 findings cannot land in one PR. One PR is unreviewable, unbisectable, and guarantees rebase hell. One finding per PR is busywork. The answer is a decomposed architectural batch: one validated design, three PRs sliced by severity, shipped in order.

### Design approach — severity-waved (Approach 1), fail-fast A

- **A = all findings.** Every confirmed finding is addressed. No deferral bucket. If it was confirmed, it ships. Deferred work re-enters only through a new review, not a residuals spec.
- **B = severity waves.** Wave 1: Critical (correctness + security). Wave 2: Major (contract + ranking + lifecycle). Wave 3: Minor (hygiene + docs + single-source guards). Each wave is a PR. Each PR is independently reviewable and independently revertible.
- **A = fail-fast.** Invalid state throws at the boundary. No silent fallbacks, no best-effort masks. A caller that passes bad scope, bad address, or bad format learns immediately instead of reading a stale answer.
- **YAGNI.** Fix what is broken. No speculative abstractions, no capability probes, no future-proof interfaces. The narrowest change that closes the finding wins.
- **One spec, three PRs.** The spec is validated once, up front, for internal consistency. Plans and code split by wave. Review is per wave; the spec review was holistic.

### Goals

1. Close all 6 Critical findings with no behavior-preserving compromises. Correctness before compatibility.
2. Close all 15 Major findings without widening public surface. Where surface must change, make the change fail at compile time rather than at runtime.
3. Close 20 Minor findings in a single hygiene pass that adds CI guards so they never recur.
4. Keep every wave green: `dotnet build Scrinia.sln` and `dotnet test Scrinia.sln` pass per PR. No wave breaks the gate for the next.
5. Pay down drift mechanisms (retry misclassification, stale caches, stringly-typed tool surfaces) at their root, not at their symptoms.

### Non-goals

- No HTTP/REST server, no remote store, no daemon. The repo has one server surface: `scri serve` over stdio.
- No per-base provider configuration, no cross-base links, no filesystem watchers.
- No speculative perf work. Where perf is touched (BM25, LoadIndex locking), it is measured before and after.

---

## 2. Architecture & Wave Structure

### Dependency graph

Scope is decomposed by severity, not by subsystem. Subsystems interleave within a wave only where a finding forces it (e.g., VectorStore traversal is a storage path that scoring depends on, but its wave placement is still Critical because it is a directory escape).

```
spec: 2026-09-06-review-remediation-design.md (validated)
  │
  ├─► Wave 1 — Critical (6) ──────────────────────┐
  │     Foundational correctness + security        │
  │     Blocks Wave 2 (Wave 2 reads what Wave 1   │
  │     makes safe).                               │
  │                                               │
  ├─► Wave 2 — Major (15) ────────────────────────┤
  │     Depends on Wave 1. Contracts + ranking +   │
  │     lifecycle become meaningful only after      │
  │     aliasing/traversal/retry are sound.        │
  │                                               │
  └─► Wave 3 — Minor (20) ────────────────────────┘
        Depends on Wave 2. Docs, guards, and
        single-source versioning reference the
        post-Wave-2 surface, not the pre-remediation
        surface.

PR mapping: Wave 1 → PR #1, Wave 2 → PR #2, Wave 3 → PR #3.
Each PR cuts from main after its predecessor merges.
```

### Sequencing rules

1. Wave 1 merges before Wave 2 cuts. Wave 2 merges before Wave 3 cuts. No parallel PRs against one another.
2. Each wave carries its own tests. Tests land in the same PR as the code they pin. No wave is judged against a fixture its code does not yet implement.
3. A finding that touches two subsystems stays in the wave of its highest severity, not split across waves.
4. No finding moves waves to balance PR size. Imbalance is acceptable; severity ordering is not negotiable.

---

## 3. Components & File Touches per Wave

### Wave 1 — Critical (6 findings)

Wave 1 is fail-fast and security-eliminating. Every item here is a silent-wrong-answer or a boundary escape that the current test suite does not catch.

| # | Finding | File:Line | Unit | Change |
|---|---|---|---|---|
| C1 | LoadIndex cache aliasing — same-process read-your-writes race; racing reader sees pre-write snapshot | `FileMemoryStore.cs:797` (`LoadIndex` / `LoadIndexUnsafe` alias return) | `FileMemoryStore.CachedIndex` + `FileMemoryStore.LoadIndex` | Return live view only under `EnterReadLock`; every mutation replaces `CachedIndex` via `WithExclusiveLock` write lock. Narrow return to `IReadOnlyList<ArtifactEntry>` where callers do not mutate; single mutating caller (`ImportTopicEntries`) materializes via `ToList()` explicitly. No `ToList()` on cache hit for readers. |
| C2 | FileLock retry misclassifies permanent failure as transient | `FileLock.cs:61` (`FileRetry` / `AcquireExclusive` retry predicate) | `FileLock`, `FileRetry` | Retry only `IOException` with transient Win32 codes + `TimeoutException`. Exclude `UnauthorizedAccessException` and `ERROR_ACCESS_DENIED` collision from retry. Collision is benign (deterministic sidecar content); log and succeed instead of looping. Document retry predicate next to `EnsureGitIgnore:241` race comment. |
| C3 | VectorStore directory traversal via scope segment | `VectorStore.cs:419` (`ScopeToRelativeDir` / `SanitizeSegment`) | `VectorStore.ScopeToRelativeDir` + `SanitizeSegment` | Reject scope with `..`, absolute segment, or `:` after sanitization. Validate through the single scope constructor (`MemoryNaming.TryBuildScopedTopicScope`) before `GetFilePath`. `SanitizeSegment` stays injective (hash suffix on mutation) and is not the validation gate. Add `CROSS_BASE_PATH_FORBIDDEN` analog for vector paths. |
| C4 | Crc32Combine produces wrong CRC for multi-chunk append | `Nmp2ChunkedEncoder.cs:132` (`Crc32Combine`) | `Nmp2ChunkedEncoder.Crc32Combine` + `AppendChunk` | Fix GF(2) matrix operator build: `odd[0]=0xEDB88320`, `odd[n]=1<<(n-1)`, `even=Gf2MatrixSquare(odd)`, `odd=Gf2MatrixSquare(even)` before len loop. Add golden test against `Crc32.HashToUInt32(Encoding.UTF8.GetBytes(concat))` for random splits. `AppendChunk:99-101` combines `existingCrc` + `newChunkCrc` with `len2=newChunkUtf8.Length`. |
| C5 | MCP tool surface leaks via `WithTools<T>` | `MemoryTools.cs` + `MemoryTools.Core.cs` + `MemoryTools.Lifecycle.cs` + `MemoryTools.Bundling.cs` + `MemoryTools.Skills.cs` (`WithTools<T>()`) | `Scrinia.Mcp` tool registration | Make every non-tool helper `internal`/`private`. `WithTools<T>()` registers all public methods; leave no public helper on `MemoryTools`. Add `McpToolSurface` test that reflects over the type and asserts exactly 12 tools. Rename `MatchesRefAttributeb → MatchesRefAttribute` in the same pass — same surface contract. |
| C6 | AsyncLocal store bridge races generic host thread boundary | `MemoryStoreContext.cs:8` (`AsyncLocal<IMemoryStore>`) + `MemoryEventSinkContext` / `SearchContributorContext` | `MemoryStoreContext`, `WorkspaceSetup`, `ScriniaCommands` | Keep `AsyncLocal<IMemoryStore> Current` for MCP server (per-request, correct). Add `*.Default` static fallback populated by CLI host (`WorkspaceSetup`) and read by tools when `Current` is null. Pin `StoreScope` clears `Current` and `Default` on dispose. Forbid draining `BackgroundWork` per test (measured 52 s → >10 min); drain once at process exit. |

Exit criteria for Wave 1: new tests `LoadIndexImmutability`, `FileLockRetryClassification`, `VectorStoreTraversal`, `Crc32Golden`, `McpToolSurface`, `AsyncLocalBridge` all green. No new public API. `dotnet build` + `dotnet test` green.

### Wave 2 — Major (15 findings)

Wave 2 is contract tightening. Rank matters, and interfaces lie if their defaults do post-filtering after top-K. Changes are compile-breaking by intent — the next implementation chooses deliberately.

| # | Finding | File:Line | Unit | Change |
|---|---|---|---|---|
| M1 | `IMemoryStore.SearchAll` overload set does post-filter after top-K truncation | `IMemoryStore.cs:109` (+ `FileMemoryStore.cs:1219,1292,1316,1342`) | `IMemoryStore` + `FileMemoryStore.SearchAll` | Collapse 5 overloads → one `SearchAll(SearchQuery)`. Define `SearchQuery` (query, scopes, limit, excludeTopics, pathPrefix, attributeFilter, supplementalScores, entityGraphScores, candidates). No default interface body. |
| M2 | Search double materialization — `ListScoped` twice per query | `MemoryTools.Core.cs:880` + `FileMemoryStore.cs:1358` + `PluginPipeline.cs:163` | `SearchPipeline` (new in `Scrinia.Core/Search/`) + `FileMemoryStore` | Materialize candidates once in `SearchPipeline.ExecuteAsync`, set `query.Candidates`, store skips its own `ListScoped` when `Candidates` is set. |
| M3 | `LoadIndex:593` pays `AcquireShared` on cache hit (file-handle open per cold check) | `FileMemoryStore.cs:593` (`LoadIndexUnsafe`) | `FileMemoryStore.LoadIndex` | Fast path: return `cached.Entries` under `EnterReadLock` with no file lock. See Wave 1 C1 for the same code region — the two fixes are jointly reviewed in PR #1 so they do not conflict. |
| M4 | `PluginPipeline.SearchAsync` duplication has drifted (missing excludeTopics/pathPrefix/attributeFilter/context) | `PluginPipeline.cs:163-189` | `PluginPipeline` | Delete duplicate; delegate to `SearchPipeline.ExecuteAsync`. Explicit contributor args (`ISearchScoreContributor`, `EntityGraphScoreContributor`) at the edge; no ambient lookup inside the pipeline. |
| M5 | SaveIndex fact-store invalidation omitted (`SaveIndexUnsafe:885`) vs siblings (`:934,:965,:1004`) | `FileMemoryStore.cs:885` (`SaveIndexUnsafe`) | `FileMemoryStore` + `EntityFactMaterializer` | Invalidate `_factMaterializer` / facts cache on `SaveIndexUnsafe`. Add `BundleImportInvalidation` test. |
| M6 | WorkspaceRoot hand-rolled derivation (7 sites) vs `store.WorkspaceRoot` | `MemoryTools.Core.cs:354,729,1631` + `MemoryTools.Lifecycle.cs:67,580` + `MemoryTools.Bundling.cs:82` + `ScriniaCommands.Bundle.cs:143` | `MemoryTools.*`, `ScriniaCommands` | Replace all 7 with `store.WorkspaceRoot`. Note split: 4 → workspace root, 2 → `.scrinia/`, 1 → `.scrinia/exports`; `Path.Combine` after `WorkspaceRoot` differs by site. |
| M7 | Chat provider retry verbatim duplication (48 lines × 3 providers) | `OpenAiChatProvider`, `AnthropicChatProvider`, `GeminiChatProvider` | `Scrinia.Core` retry helper | Extract `RetryPolicy.ExecuteAsync` → `(HttpResponseMessage?, string?)`. Preserve `body.Length>500 ? body[..500]+"..."` in one helper; include `ResilientEmbeddingProvider.cs:70`. Chosen shape fits `yield return` iterator callers. |
| M8 | AgentLoop truncation constant hardcoded | `AgentLoop.cs:200-203` | `AgentLoop` | Named constant `MaxLoopContentChars` with rationale comment; preserve `[... truncated]` marker. Do not rebuild `ContentTruncated` followUp contract. |
| M9 | Remote `HttpMemoryStore` implements 3 of 5 search paths; filtering overloads silently post-filter (wrong) | `HttpMemoryStore.cs:329,272,571` | `HttpMemoryStore` | Implement real server filtering via `POST /api/v1/stores/{store}/search` body. Add `GET` additive `excludeTopics` + `pathPrefix`. Store contract test runs against both `FileMemoryStore` and `HttpMemoryStore` on same fixture. |
| M10 | Topic/classification prefix ambiguity — `"local:"` leaked into filters produces extra `CachedIndex` keys over one dir | `FileMemoryStore.cs:576` (`NormalizeScopeFilters`) | `FileMemoryStore.NormalizeScopeFilters` + `MemoryNaming.TryBuildScopedTopicScope` | Rebuild scope via the ONE constructor; invalid topic with trailing dot/space/bare `.`/`NUL` → no match (read returns empty). Shared validation with write path. |
| M11 | Sidecar `v` migration — read-time forward-fill writes under shared lock; retry on `UnauthorizedAccessException` masked | `FileMemoryStore.cs:939-960` (`LoadIndexUnsafe` migration write-back) | `FileMemoryStore` + `ArtifactSidecarPolicy` | `IsReadOnly` check before `WriteSidecar`; widen catch to `(IOException, UnauthorizedAccessException)` with best-effort swallow. Document idempotent `MigratesTo` retry on next cold load. |
| M12 | Embedding signature mismatch — stale vectors re-saved under current signature vs quarantined | `VectorStore.cs:570-648` (`LoadFromDiskUnsafe` + `QuarantineStaleFile`) | `VectorStore` | Quarantine `SVF1`/`SVF2`/mismatched `SVF3` to `vectors.bin.stale-{ts}-{guid}`; do not re-save as SVF3 under current signature. Set `_staleQuarantines` and let caller reindex. |
| M13 | Restore journal `complete` recovery edge — bare `complete` without `prepared` replay leaves scope mid-recovery | `FileMemoryStore.cs:707-787` (`RecoverScopeUnsafe`) | `FileMemoryStore` journal restore | Parked Task 6 bare-`complete` journal finding: invalidate and re-derive on bare `complete`; do not claim half-replayed state. |
| M14 | `EnsureGitIgnore` vs `.scrinia` prefix overlap — derived file classifier first-match precedence vs ignore block | `FileMemoryStore.cs:254` (`WriteDefaultGitIgnore`) + classifier registry | `Scrinia.Core` derived-file classifier | Leading `/` anchor on canonical derived dir rules (`/embeddings/`, `/indexes/`, etc.) so `topic/embeddings` is not ignored. Equivalence test: classifier union == ignore patterns == fingerprint exclusion. |
| M15 | Scorer duplication — `WeightedAttributeScorer` construction scattered across 4 SearchAll bodies | `FileMemoryStore.cs:1219,1292,1316,1342` | `SearchPipeline` + `FileMemoryStore` | Single scorer construction inside `SearchPipeline`; store's sole `SearchAll(SearchQuery)` receives already-built scoring context or a `SupplementalScores` map. Collapse 4 bodies → 1. |

Exit criteria for Wave 2: `SearchConformanceTests` (both stores, same corpus, same inputs, same outputs), `PipelineParity` (MCP vs server ranking), `NoPostFilteringAfterTruncation` (fill-to-limit with 50 memories / 40 excluded by `pathPrefix` → `limit:20` returns 20 on both stores), `WorkspaceRootSevenSites` assertion, retry-helper coverage, `dotnet build` + `dotnet test` green.

### Wave 3 — Minor (20 findings)

Wave 3 is one hygiene pass. Each item is small; the value is the guard that stops it drifting again.

| # | Finding | File:Line | Unit | Change |
|---|---|---|---|---|
| N1 | Skill/doc parity — `src/Scrinia.Mcp/skills/*.md` count drifts from `docs/getting-started.md` table | `docs/getting-started.md` skills table | Docs + CI | CI step: `Directory.GetFiles("src/Scrinia.Mcp/skills","*.md")` must equal skill-table row count. Currently 7 skills; spec asserts 7. See `docs/overview.md:28`. |
| N2 | Version string single-sourced in `Directory.Build.props` but grep finds literals in `src/**/*.cs` | `Directory.Build.props` + grep guard | CI | CI step: `grep -R "<Version>" src/**/*.cs{proj}` literal-version scan; fail on mismatch. Add to `docs/overview.md:28` maintenance table. |
| N3 | XML-doc coverage not enforced on `IMemoryStore`/`MemoryTools` public members | `IMemoryStore.cs`, `MemoryTools.cs` | CI | Add DocFX coverage threshold or analyzer rule. Start with `warnaserror` on missing `///` for `public` in `Scrinia.Core` and `Scrinia.Mcp`. |
| N4 | `YamlDotNet` reference in `Scrinia.Mcp.csproj` has no consumer — `McpResponse` hand-rolls serializer | `Scrinia.Mcp.csproj` + `McpResponse.cs` | Build | Remove `YamlDotNet` reference if no call site remains after audit. If kept, add `// used: <line>` comment to prevent re-deletion. |
| N5 | `AgentLoop` vs envelope truncation docs — LLM-facing `ContentTruncated` and transport vs model boundary unclear | `AgentLoop.cs:200` + `McpResponse.cs:213` | Docs + code comment | Comment clarifies: `ContentTruncated` + `followUp` is model-facing; `McpResponse` size is transport. Do not touch envelope shape. |
| N6 | `Export`/`Import` exit 0 on genuine failure via `StartsWith("Error:")` guard (same bug as `show`/`forget` before Wave 1) | `MemoryTools.Bundling.cs:31,82` + `ScriniaCommands.Bundle.cs` | CLI + MCP | Switch to `Result.Status == "error"` guard. Bundle export/import tests assert non-zero exit on failure fixture. |
| N7 | `*.meta.json` merge driver comment still claims `*.nmp2 binary` | `docs/multi-user-setup.md` | Docs | Correct to `*.meta.json merge=scrinia-meta` + `*.nmp2 merge=scrinia-nmp2`; note `.gitattributes` is generated by `scri setup --multi-user` (`Setup.cs:341-344`), not tracked. |
| N8 | `GenerateContentPreview` sits on `IMemoryStore` but is a pure string function | `IMemoryStore.cs:220` | `Scrinia.Core` helper | Move to `static` helper (`TextPreview.cs` or `Search/TextAnalysis`). 21 call-site retypes, compiler-checked. |
| N9 | `SaveIndex` on `IMemoryStore` has zero cross-store production callers | `IMemoryStore.cs:459` (`SaveIndex`) | `IMemoryStore` | Remove from `IMemoryStore`; keep public on `FileMemoryStore` for tests. Do not put on `ILocalStoreLayout`. |
| N10 | `ILocalStoreLayout` split incomplete — 32 typed-`IMemoryStore` sites still use path members that only `FileMemoryStore` implements | `ScriniaCommands.cs:43` et al (32 sites) | Interface split | Extract `ILocalStoreLayout` (artifact path, `GetStoreDirForScope`, `WorkspaceRoot`, `ListTopicArtifacts`, `ImportTopicEntries`). `FileMemoryStore` implements both; `HttpMemoryStore` implements only `IMemoryStore`. Retype ~32 sites to the narrower type. |
| N11 | Event-sink `MemoryEventSinkContext.Default` assigned from 6 places with hand-built `CompositeEventSink` | `WorkspaceSetup.cs:231,391,409,418,438,519` | `Scrinia.Core` | Add `MemoryEventSinkContext.InstallDefault(params IMemoryEventSink[])`. No lifetime change, no `BackgroundWork` drain per test. |
| N12 | Server handler vocabulary vs MCP vocabulary (StoreMemory/ShowMemory/SearchMemories/ListMemories) | HTTP handlers | Naming | Rename internal handler methods to MCP vocabulary (`RememberMemory`/`RecallMemory`/…); route paths unchanged. |
| N13 | Stale tool-list comment at `ScriniaMcpToolsTests.cs:11-23` documents `Show`/`Store`/`Export`/`Import` | `ScriniaMcpToolsTests.cs:11-23` | Tests | Delete comment. Tests below it are the documentation. |
| N14 | Fence-aware extraction comment missing at ephemeral alias boundary (`~subject` vs `/temp/` form) | `MemoryTools.cs` + `ScriniaCommands.Memory.cs` | Docs in code | One comment at the ephemeral alias seam: `/temp/` resolved before store, store sees only `~subject`. |
| N15 | Derive cost — intermittent re-derive on every `ReadAsync` vs cached | `FileMemoryStore.ReadAsync` | Perf | Measure via `scrinia-bench readcost`; if cache-invalidated per read, memoize derived view keyed by `(address, updatedAt)` with same invalidation already on `SaveIndex`/`Remove`/`TryUpdateEntry`. Not a perf claim until measured. |
| N16 | `FileLock` stale quarantine `vectors.bin.stale-*` ignores derived-file classifier gap | `VectorStore.cs:627` + classifier | Classifier | Include `vectors.bin.stale-*` in derived union; fingerprint exclusion covers it; add row to `docs/architecture/…` table if one exists. |
| N17 | `compact` best-effort on single-chunk entry — silent no-op without `mutation: none` signal | `MemoryTools.Lifecycle.cs:549` (`Compact`) | MCP | Return structured `mutation: none` (no error) with `generation` unchanged when `ChunkCount==1` or nothing to compact. |
| N18 | `link` partial-failure message embeds two YAML blobs verbatim; leaks envelope vs content boundary | `MemoryTools.Core.cs:1617` | `MemoryTools` | Use `result.Error ?? result.Content` and `result.Status` guard; do not echo YAML blobs. |
| N19 | `facts` truncation semantics undocumented — `limit` truncates `rows`/`coOccurrences`/`timeline` independently | `MemoryTools.Facts.cs:65` + `PluginPipeline.cs:179` | Docs + DTO | Document truncation rule on DTO: each of entities/profile/intersect truncated independently; `truncated: bool` on the exact DTO row (`FactsEntitiesResult` etc.). |
| N20 | `Scrinia.Merge` publish gap — `release.yml:70-79` archives `scri` without `scri-merge` | `.github/workflows/release.yml:70` | CI | Publish `Scrinia.Merge` into same `publish/` dir as CLI; include in both archives; driver registration resolves sibling of `Environment.ProcessPath`. |

Exit criteria for Wave 3: CI steps (skill/doc parity, version grep, XML-doc threshold) pass; docs changes reference `docs/overview.md:28` maintenance invariants; `dotnet build` + `dotnet test` green; release archive contains `scri-merge`.

---

## 4. Data Flow, Error Handling & Security Contract

### Before → After (fail-fast boundary)

**Before (silent wrongness):**

```
caller → IMemoryStore.SearchAll(limit:20, pathPrefix:/private)
        → FileMemoryStore: ListScoped (40 memories match) → Rank → Truncate 20 → Post-filter by pathPrefix → return 12
        caller sees 12, assumes "there are 12" — the other 8 were cut before exclusion was applied.

caller → scope="local:topic." → NormalizeScopeFilters returns "local:topic." RAW
        → CachedIndex key "local:topic." (stale bucket) vs writes to "local:topic" (live bucket)
        → reads permanently stale; no error.

caller → scope="../../etc" → ScopeToRelativeDir → vectors.bin at ../../etc/vectors.bin → traversal.

caller → vector file with sig "ollama:nomic" but store expects "vulkan:granite"
        → silently rewritten as SVF3 with current sig — vectors misattributed.

caller → AppendChunk("...N chunks=5...", newChunk)
        → Crc32Combine miscomputed → artifact passes local write, fails verifier on next cold read.

caller → WithTools<MemoryTools> with public helper DoThing() → tool surface gains "do_thing".
```

**After (fail-fast contract):**

```
caller → SearchQuery { Query, PathPrefix="/private", Limit=20 }
        → SearchPipeline builds scoped candidates ONCE (or uses query.Candidates) → Rank → Filter → Truncate 20
        → FileMemoryStore.SearchAll(SearchQuery) has no overload to post-filter against. If neither
           FileMemoryStore nor HttpMemoryStore can filter, the code does not compile.

caller → scope="local:topic." → TryBuildScopedTopicScope returns false → NormalizeScopeFilters returns []
        → caller sees empty scope set → no filesystem touch, no error, no stale key. Write path via
           BuildScopedTopicScope throws — write rejects what read treats as "no match".

caller → scope="../../etc" → TryBuildScopedTopicScope fails → empty set. Vector path never constructed.
        Direct path API throws ArgumentException. Security eliminates the class, not one pattern.

caller → vector file sig mismatch → TryLoadFromDisk quarantines to vectors.bin.stale-{ts}-{guid},
        records scope in _staleQuarantines, returns empty vectors. Caller reindexes. No re-signing.

caller → AppendChunk → Crc32Combine verified against ground-truth Crc32.HashToUInt32(concat)
        → DecodeAllChunksVerified ensures decoded bytes match header CRC; invalid artifact rejected.

caller → WithTools<MemoryTools> → only 12 public methods on the type are tools. Reflection test pins
        the count. Helper visibility change is compiler-checked.
```

### Error handling tiers

| Tier | Where | Behavior |
|---|---|---|
| Validation | `MemoryAddress`, `SearchQuery`, scope/topic construction | Throw `ArgumentException` or `MemoryAddressError` at the edge. Do not sanitize silently. |
| Security | `ScopeToRelativeDir`, `ArtifactPath`, `GetFilePath`, `FindArtifactPath` | Throw before any filesystem touch. Path traversal is not a value to normalize. |
| Availability | `VectorStore.TryLoadFromDisk`, `FileRetry` | Return `VectorLoadResult.Failed` / propagate retry-exhausted error. Caller decides. Do not synthesize empty success. |
| Conflict | NMP decode, journal replay, fingerprint stale check | Throw `InvalidDataException` / return `SHARED_ENTRY_CORRUPT`-style code; base stays available for other entries. |

Security eliminations (no patterns to bypass):

- Cross-scope read elimination: `scope` never interpolates into a path except through `PathRouter.ToStoreDir` + `TryBuildScopedTopicScope`. Raw scope strings do not flow to `Path.Combine`.
- Tool-surface elimination: `WithTools<T>` attack class eliminated by `public` → `internal`/`private` re-visibility, not by an `IgnoreAttribute`.
- Stale-vector misattribution elimination: `LoadFromDiskUnsafe` quarantines on sig mismatch. The old path that rewrote under the current sig is removed, not patched.

---

## 5. Testing & Verification Strategy

### Per-wave build gates

Every wave obeys the repo traps:

- `dotnet build Scrinia.sln -c Debug` clean before any test run.
- Never two `dotnet test` invocations against one project concurrently.
- Never `dotnet build` during a test run (locked DLLs wedge the run).
- Clear stray `testhost.exe` / `dotnet.exe` before full runs (orphaned `FileLock` on `%TEMP%`).

Per-wave scope:

| Wave | Gate | Projects |
|---|---|---|
| Wave 1 | Critical correctness — no silent wrong answer survives | `Scrinia.Tests`, `Scrinia.Merge.Tests` |
| Wave 2 | Contract + ranking — both stores answer identically | `Scrinia.Tests`, `Scrinia.Retrieval.Benchmarks.Tests`, `Scrinia.Plugin.Embeddings.Tests` |
| Wave 3 | Hygiene — CI guards pin idempotent invariants | `Scrinia.Tests` + CI workflow (`release.yml` / new `checks.yml`) |

### New tests — required before a wave is done

**Wave 1 — 6 tests (fail-closed):**

1. `VectorStoreTraversal` — parametrized scopes `..`, `../..`, `../../etc/passwd`, `local:../../etc`, `/abs/path`, `C:\windows` → assert `ArgumentException` or empty-set normalization; assert `GetFilePath` never resolves outside `_baseDir`. Uses `PathContainment.IsWithin` after construction.
2. `LoadIndexImmutability` — two threads: writer loops `SaveIndex` / `Upsert`; reader loops `LoadIndex("local")` and asserts every returned list is not mutated behind it (count monotonic after replace; no `UnauthorizedAccessException` surfaces on read). Explicitly does not assert absence of staleness — that is untestable black-box (see LoadIndex aliasing analysis 2026-08-10) — only that aliasing does not corrupt the reader's list.
3. `FileLockRetry` — inject collisions at `FileLock.cs:61` retry predicate: `IOException` with `ERROR_SHARING_VIOLATION` → retries; `UnauthorizedAccessException` (Windows `Move` collision on `EnsureGitIgnore:241`) → does not retry, benign success; `TimeoutException` after window → `VectorStoreUnavailableException`. Use `FileLock.AcquireExclusive` with a short timeout to avoid hanging CI.
4. `Crc32Golden` — generate random chunks (including empty chunk, 1-byte, 5 MiB boundary, 100k chunk count guard), encode via `EncodeMultiChunkFromParts`, then verify `Nmp2ChunkedEncoder.AppendChunk`'s incremental CRC equals `Crc32.HashToUInt32(UTF8(concat))` for every prefix extension. Include mismatch case: hand-corrupt header CRC → `DecodeAllChunksVerified` throws `InvalidDataException`.
5. `McpToolSurface` — reflect over `ScriniaMcpTools`: assert `WithTools`-registered public methods == 12; assert no public method outside that set; assert `MatchesRefAttribute` spelling (single `b`) post-rename.
6. `AsyncLocalBridge` — CLI path: start `ScriniaCommands` host with `MemoryStoreContext.Current == null`, invoke tool via `WorkspaceSetup` → assert tool sees store through `Default` fallback; MCP path → assert tool sees store through `Current`. `StoreScope.Dispose` clears both. Assert no per-test `BackgroundWork` drain occurs.

**Wave 2 — 5 suites:**

7. `SearchConformance` — fixture corpus (≥50 memories, mix of plain + chunked, mixed topics, mixed attribute keys). Same `SearchQuery` inputs run against `FileMemoryStore` and `HttpMemoryStore`; assert identical `SearchResult` order and count. Must include: `pathPrefix`, `attributeFilter` (empty-value presence test + value match), `excludeTopics`, `context` augmentation, `limit` fill-to-limit (40 excluded of 50 → limit 20 returns 20 on both stores).
8. `PipelineParity` — MCP `Search` path vs server `/api/v1/stores/{store}/search` path over same corpus; identical ranking. Regression guard for the drift `PluginPipeline.cs:163` already has (missing `excludeTopics` etc.).
9. `BundleImportInvalidation` — warm `GetEntityFactStore`, import a bundle carrying new `Attributes`, assert new entity facts visible through `facts()` / search / `EntityTypingEventHandler` without restart. Fails on `FileMemoryStore.cs:885` without invalidation.
10. `WorkspaceRootSevenSites` — static assertion: the 7 sites (`MemoryTools.Core.cs:354,729,1631`, `MemoryTools.Lifecycle.cs:67,580`, `MemoryTools.Bundling.cs:82`, `ScriniaCommands.Bundle.cs:143`) all resolve through `store.WorkspaceRoot`; further `Path.Combine` after it diverges as specified (4/2/1 split). Compiler-checked; test asserts the values resolve to the expected directories for a temp workspace.
11. `RetryHelperBehavior` — feed identical transient `HttpResponseMessage` sequences to `OpenAI`/`Anthropic`/`Gemini` helpers via the extracted retry; assert same retry counts, same `body.Length>500` truncation, same `circuitError` re-raise.

**Wave 3 — 3 guards + docs checks:**

12. Skill/doc parity check — `Directory.GetFiles("src/Scrinia.Mcp/skills","*.md").Length == rows in docs/getting-started.md skills table`. Expected 7. Runs in CI; local `dotnet test` also has a unit assertion so failures surface before push.
13. Version single-source check — grep `src/**/*.cs` and `src/**/*.csproj` for literal version string that should live only in `Directory.Build.props`. CI fails on drift. Documented at `docs/overview.md:28`.
14. Release-archive check — `release.yml` job publishes `Scrinia.Merge` (`scri-merge`) alongside `scri`; test asserts archive file list contains both.

Existing `SearchExcludeTopicsTests` and `HybridScorerTests` are mechanically retyped to `SearchQuery` (see prior correction 2026-08-07: `HybridScorerTests` needed retype; `SearchExcludeTopicsTests` did not — it drives `MemorySearch` at the tool layer, which keeps its shape).

### Measurement notes that remain true

- BM25 benefit is not claimed as a speedup until measured (`datasets/locomo` harness, before/after on shipped corpus, median reported even if delta is noise). The `Candidates` change stands on removing a duplicated call at an inexpressible boundary.
- `LoadIndex` lock removal on cache hit saves a file-handle open per call; its benefit was previously misattributed to avoiding filesystem touch on cache hit. Correction: `LoadIndexUnsafe:593` consulted cache only after `AcquireShared`; removing the lock removes the per-call open. Reported separately from the `ToList()` copy saving (19 readers called it, 1 writer needs it).

---

## 6. Rollout & Isolation

### Worktrees

- One worktree per wave, cut from `main` after predecessor merges. No long-lived integration branch.
- Ephemeral per-wave scratch data lives under `.scrinia` worktree prefix; no commit of generated `vectors.bin`, `.lock`, `.tmp`, or `.restore-journal.json`.

### Parallel coder subagents per workstream

Within a wave, decoupled file sets allow parallel subagents, coordinated not contested:

| Workstream | Owns (exclusive file set) | No overlapping writer |
|---|---|---|
| A. Store + locking | `FileMemoryStore.cs`, `FileLock.cs`, `MemoryStoreContext.cs` | `Scrinia.Core` store layer only |
| B. Encoding | `Nmp2ChunkedEncoder.cs`, `Nmp2Strategy.cs` | Encoding package only |
| C. Vectors | `VectorStore.cs`, `FileRetry.cs` | Embeddings subsystem only |
| D. Tool surface | `MemoryTools*.cs`, `MemoryStoreContext.cs` (read-only after workstream A) | `Scrinia.Mcp` surface only |

Where ownership overlaps (C1 and C3 touch `FileMemoryStore.cs` at disjoint line ranges: `~593` vs `~885`), the Wave 1 PR sequences C1 before M5/M11 — one commit base, not parallel writers.

### Reviewer pass

Per wave: coder subagent → reviewer subagent → human approval → merge. Reviewer re-runs the factual claims in §2 Established-facts style (`file:line` + `rg` or command), not style nits. A review that finds a false claim blocks the wave; there is no "note and defer".

### Docs maintenance

- Single maintenance ledger at `docs/overview.md:28` (the repo's known-gaps table). That table already tracks three invariants this spec adds CI for — skill/doc parity, XML-doc coverage, version single-source. Each new CI guard adds a row there and a job in CI. No secondary tracker.
- `docs/multi-user-setup.md` and `docs/architecture/overview.md` updated in the wave whose code they document (Wave 1 for derived-file classifier, Wave 3 for merge-driver prose).

---

## 7. Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| BM25 ranking shift from collapsing 4 scorer constructions → 1 | Medium — sane candidates but weight merge semantics can differ at the edge | Visible reorder on existing queries | Ship `SearchConformance` and `PipelineParity` before any ranking change; diff scoring inputs, not just code. If shift is real, document in CHANGELOG as behavioral change with corpus. |
| `EncodeChunks` 64 MiB total / 8 MiB per-chunk / 100 k chunk-count limits tightened via `_writeValidationOptions` (used by `FileMemoryStore.WriteAsync`) | Low — existing corpus under limits | Write-time rejection on large chunked artifacts | Validate limits before changing; `Nmp2Strategy.MaxDecodedBytes` and `MaxChunkCount` guards already exist (`Nmp2ChunkedEncoder.cs:344`). If limits fire on real bundle fixture, bump with evidence, not speculation. |
| Latent bad scope (`local:topic.` / trailing dot) already persists in a user's sidecar directory — read now returns empty set (no match) | Low but user-visible on upgrade | Search/list stops returning that topic's memories | Migration note: that directory was always one filesystem alias away from `local:topic` on Windows. A one-time manual rename via `scri` move is sufficient. No auto-migration of directory names — auto-rename is more dangerous than guidance. |
| `VectorStore` quarantine on sig-mismatch empties scope until reindex; user sees empty search | Low — only across provider switches | Temporary empty results | Emit `[scrinia:info] Quarantined to vectors.bin.stale-*; rebuilding via reindex` (already present `VectorStore.cs:634-637`) and `HasStaleQuarantines` flag so `WorkspaceSetup` triggers reindex automatically. |
| Wave 2 mechanical retype (~53 call sites across `IMemoryStore` collapse + `ILocalStoreLayout` split) ships a large diff that hides a bug | Medium | Single bisect point covers many files | Make the retype a pure rename commit with no logic change; contract logic diff is a second commit on top. Reviewers review the renames as `rg` output, not line-by-line. |
| `FileLock` retry predicate too tight → legitimate transient still fails fast | Low | Intermittent I/O fails that would have retried | Predicate limited to known transient Win32 codes; every other `IOException` propagates to `FileRetry` outer window which still retries at the call site. Predicates are tested by `FileLockRetry` via injected `IOException` kinds. |
| Zero-downtime run: `scri serve` holds `AsyncLocal` store while CLI `Default` fallback writes to a different workspace | Eliminated by design | Cross-workspace write leakage | `WorkspaceSetup` scopes per invocation; `StoreScope` is test-only. Production `Current` and `Default` are never set simultaneously by the same process (MCP server vs CLI are different processes). |

---

## 8. Philosophy Compliance & Checklist

### Five Laws of Elegant Defense (code-philosophy scorecard)

| Law | Wave | Evidence |
|---|---|---|
| Data guards itself — validation at the shape that owns it | Wave 1 C3 + Wave 2 M10 | `ScopeToRelativeDir` / path APIs refuse before `Path.Combine`. `TryBuildScopedTopicScope` is the single constructor. No downstream sanitization. |
| Fail loudly at the boundary — invalid is an error, not a variant | Wave 1 C1/C2/C4 + Wave 2 M1/M6 | `SearchAll(SearchQuery)` fails to compile on unimplemented filter; `Validate` throws on bad address/length/CRC; file-handle collision is logged, not looped silently. |
| Narrow the contract until misuse cannot be expressed | Wave 2 M1/M10 + Wave 3 N8-N11 | Remove `SaveIndex` from `IMemoryStore`; split `ILocalStoreLayout`; make helpers `internal`; delegate pipeline to typed `SearchPipeline`. |
| One seam, one rule — dedup through the narrowest shared loop | Wave 2 M2/M5/M7/M15 + Wave 3 N6 | `SearchPipeline` single candidate materialization; `RetryPolicy` single truncation helper; `InstallDefault` single composite construction. |
| Preserve the user's bytes — atomic replace, not patch | Wave 1 C2/C4 + Wave 2 M12 | Same-directory `File.Move` replacement (`FileLock` + `DurableFile`), quarantining vectors not re-signing them, CRC-guarded NMP decode (`DecodeAllChunksVerified`). |

### Pre-merge checklist (per PR)

**Wave 1 — PR #1: `review-remediation-critical`**

- [ ] `VectorStoreTraversal` — traversal probes all raise / remain inside `_baseDir`
- [ ] `LoadIndexImmutability` — no aliasing corruption under concurrent write/read
- [ ] `FileLockRetryClassification` — transient retries, collision does not, timeout surfaces
- [ ] `Crc32Golden` — incremental CRC matches concat ground truth; corrupt header rejected
- [ ] `McpToolSurface` — exactly 12 tools; no public helpers remain
- [ ] `AsyncLocalBridge` — CLI fallback path and MCP direct path both resolve; `StoreScope` clears
- [ ] `dotnet build Scrinia.sln -c Debug` clean; `dotnet test Scrinia.sln` green

**Wave 2 — PR #2: `review-remediation-major`**

- [ ] `IMemoryStore` has exactly one `SearchAll(SearchQuery)` member; four old bodies deleted
- [ ] `SearchPipeline.ExecuteAsync` is the only candidate-materialization path; `PluginPipeline` delegates
- [ ] `SearchConformance` + `PipelineParity` + `NoPostFilteringAfterTruncation` green on both stores
- [ ] `BundleImportInvalidation` green (warm facts after import)
- [ ] `WorkspaceRootSevenSites` — 7 sites through `store.WorkspaceRoot` with 4/2/1 post-Combine split
- [ ] Retry helper deduplication — 3 providers call the one helper; truncation at one place
- [ ] `dotnet build` + `dotnet test` (including `Scrinia.Retrieval.Benchmarks.Tests`) green

**Wave 3 — PR #3: `review-remediation-minor`**

- [ ] Skill/doc parity CI step — `src/Scrinia.Mcp/skills/*.md` count == `docs/getting-started.md` table (7)
- [ ] Version single-source CI step — `grep -R "<Version>" src/**/*.cs{proj}` finds no literal drift
- [ ] XML-doc coverage gate enabled for `Scrinia.Core` / `Scrinia.Mcp` public members
- [ ] `YamlDotNet` reference removed or commented as used at `file:line`
- [ ] `release.yml` publishes `Scrinia.Merge` (`scri-merge`) alongside `scri`
- [ ] `docs/overview.md:28` updated — three rows added/verified for the new guards
- [ ] `dotnet build` + `dotnet test` green; release archive file list asserted

### Self-review — spec audit (2026-09-06)

Scanned for contradictions, vague requirements, and scope creep. Fixed inline before submission:

1. **LoadIndex locking contradiction fixed.** Earlier draft said Wave 1 "adds EnterReadLock on cache hit" while Wave 2 M3 said "remove file lock on cache hit" — read as contradictory. Clarified: in-process `EnterReadLock` stays (same-process read-your-writes race via `BackgroundWork` fire-and-forget `PromoteAsync`); cross-process `AcquireShared` file-handle open is removed. One paragraph now names both and states the ruling from `LoadIndex:797` comment block.
2. **BM25 measurement vagueness fixed.** "Improve BM25" read as an unbounded tuning task. Narrowed: collapse 4 scorer builds into 1 inside `SearchPipeline`; measure against `datasets/locomo` harness before/after; report median even if delta is noise. The change is a boundary cleanup, not a ranking tweak.
3. **Scope creep — capability probe — rejected.** Draft considered `SupportsLocalPaths` probe on `IMemoryStore` instead of the `ILocalStoreLayout` split. Rejected: it moves the trap from "wrong answer" to "capability check is wrong" without removing the local-only members from the shared interface.
4. **Vague "15 majors listed" tightened.** Table now enumerates 15 named items (M1–M15) each with a `File:Line`, a unit, and a concrete change. No row uses "and related fixes".
5. **CI guard scope creep trimmed.** Initial draft added CI for every Minor row. Trimmed to three enforcing guards (skill/doc parity, version single-source, XML-doc threshold); the rest are compiler-checked renames or docs-only edits that do not need a new workflow.
6. **Reuse vs duplication contradiction fixed.** Wave 1 C2 filed `FileRetry` as "stop retrying on collision" while a parallel note said "retry still needed for transient Move". Clarified predicate: retry on transient Win32 codes only; collision's `UnauthorizedAccessException` is benign success because payload is deterministic.
7. **Wave boundary leak fixed.** `FileMemoryStore.cs:593` lock removal was described in both Wave 1 and Wave 2, inviting a merge conflict. Ownership assigned to Wave 1 PR (same method as C1); Wave 2 references it as "jointly reviewed, do not re-edit".

No `TBD`/`TODO` placeholders remain. Every `File:Line` was checked against the current tree at `HEAD`. Every test named is a net-new file or case in the named suite.

---

## References

| Path | Role |
|---|---|
| `src/Scrinia.Core/FileMemoryStore.cs:593,797,885,934,965,1004` | LoadIndex aliasing + cache fast path + SaveIndex invalidation |
| `src/Scrinia.Core/FileMemoryStore.cs:254,707` | GitIgnore vs derived classifier + journal bare-complete recovery |
| `src/Scrinia.Core/FileLock.cs:61` | Retry predicate |
| `src/Scrinia.Core/Embeddings/VectorStore.cs:419,570,627` | Scope traversal + signature quarantine + stale suffix |
| `src/Scrinia.Core/Encoding/Nmp2ChunkedEncoder.cs:132` | Crc32Combine operator build |
| `src/Scrinia.Core/MemoryStoreContext.cs:8` | AsyncLocal bridge |
| `src/Scrinia.Mcp/MemoryTools.cs` | WithTools tool leak |
| `src/Scrinia.Core/Search/` | SearchPipeline + SearchQuery home |
| `.github/workflows/release.yml:70` | Publish scri-merge gap |
| `docs/overview.md:28` | Docs-maintenance ledger for CI invariants |
| `docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md` | Aliasing ruling cited at `FileMemoryStore.cs:97` |
| `Directory.Build.props` | Canonical `<Version>` single source |

## Changelog

- `2026-09-06`: New spec — 41 findings, severity-waved (Critical → Major → Minor), fail-fast, YAGNI, one spec / three PRs. No `TBD`/`TODO`; self-reviewed.
