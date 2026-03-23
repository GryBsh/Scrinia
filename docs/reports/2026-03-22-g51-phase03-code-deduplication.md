# March Report: G-51-165 Phase 03 — Code Deduplication

**Goal:** Release readiness audit — four-stream parallel scan (security, code quality, documentation, chaos) with validated findings
**Phase:** 03 of 04
**Date:** 2026-03-22
**Outcome:** All 3 tasks completed successfully. 1 formal concern resolved (QAL-049), 2 non-concern quality items addressed (QAL-011, QAL-063). Broader store flow deduplication assessed and correctly deferred. 896 tests pass, 0 failures, clean build.

## 1. Summary

Phase 03 was the smallest user-task phase in G-51, targeting code duplication and missing infrastructure across three independent workstreams. The `ComputeChunkEntries` method was extracted from two duplicate sites into `TextAnalysis` (QAL-049/050). A new `ResilientEmbeddingProvider` abstract base class eliminated ~175 lines of resilience boilerplate across 5 embedding providers (QAL-011). Cross-process file locking was added to `VectorStore` using the established `FileLock` pattern (QAL-063). The broader store flow duplication between MCP and REST paths was investigated but correctly deferred — the two paths have diverged with MCP-only features (codeRefs, event sink, ref keyword extraction, CountPattern auto-reviewWhen) that make safe unification infeasible without a significant abstraction layer. All 3 tasks executed in a single parallel wave with zero SOS signals, zero replanning, and zero rework.

## 2. Changes

### ComputeChunkEntries Extraction (QAL-049/050)
- **Before:** `ComputeChunkEntries` existed as a private static method in both `MemoryTools.cs` and `MemoryOrchestrator.cs` with functionally identical bodies (only cosmetic variable naming differed).
- **After:** Single `public static` implementation in `TextAnalysis.cs` (line 173). Both `MemoryTools.cs` (line 384) and `MemoryOrchestrator.cs` (line 30) now call `TextAnalysis.ComputeChunkEntries`. No duplicate logic remains.
- **Deferred:** The broader store flow between `MemoryTools.Store` (MCP path) and `MemoryOrchestrator.StoreAsync` (REST path) follows a similar pipeline but has accumulated divergent features. The agent assessed this and correctly deferred unification — forced extraction would create a leaky abstraction worse than the duplication.

### ResilientEmbeddingProvider Base Class (QAL-011)
- **Before:** All 5 HTTP embedding providers duplicated identical resilience boilerplate: `_circuitBreaker` and `_retryOptions` fields, constructor defaults, and the `EnsureClosed` / `RetryPolicy.ExecuteAsync` / `RecordSuccess` / `RecordFailure` wrapper pattern. ~35 lines of boilerplate per provider.
- **After:** New abstract base class `ResilientEmbeddingProvider` in `src/Scrinia.Core/Embeddings/Providers/` owns the shared fields, constructor defaults, and resilience wrapper. Each provider overrides only `ExecuteEmbedAsync` for HTTP request construction and response parsing. ~175 lines of boilerplate removed across 5 providers.
- **Refactored providers:** OpenAiEmbeddingProvider, AzureAiEmbeddingProvider, GoogleGeminiEmbeddingProvider, OllamaEmbeddingProvider, VoyageAiEmbeddingProvider.

### VectorStore Cross-Process Locking (QAL-063)
- **Before:** `VectorStore` used in-process `SemaphoreSlim` per scope but had no cross-process file locking, unlike `FileMemoryStore` and `ScriniaArtifactStore` which both used `FileLock`.
- **After:** All 4 disk I/O methods now use `FileLock`:
  - `LoadFromDisk`: `FileLock.AcquireShared` (read lock)
  - `SaveAsSvf2Async`: `FileLock.AcquireExclusive` (write lock)
  - `AppendAddOpAsync`: `FileLock.AcquireExclusive` (write lock)
  - `AppendDeleteOpAsync`: `FileLock.AcquireExclusive` (write lock)
- New `GetVectorLockPath` helper generates lock file path as `vectorPath + ".lock"`.
- In-process `SemaphoreSlim` retained for in-process concurrency serialization.

### Files Touched (summary)
- `src/Scrinia.Core/Search/TextAnalysis.cs` (ComputeChunkEntries added)
- `src/Scrinia.Core/Embeddings/Providers/ResilientEmbeddingProvider.cs` (new)
- `src/Scrinia.Core/Embeddings/Providers/OpenAiEmbeddingProvider.cs`
- `src/Scrinia.Core/Embeddings/Providers/AzureAiEmbeddingProvider.cs`
- `src/Scrinia.Core/Embeddings/Providers/GoogleGeminiEmbeddingProvider.cs`
- `src/Scrinia.Core/Embeddings/Providers/OllamaEmbeddingProvider.cs`
- `src/Scrinia.Core/Embeddings/Providers/VoyageAiEmbeddingProvider.cs`
- `src/Scrinia.Core/Embeddings/VectorStore.cs`
- `src/Scrinia.Mcp/MemoryTools.cs` (ComputeChunkEntries call site)
- `src/Scrinia.Server/Services/MemoryOrchestrator.cs` (ComputeChunkEntries call site)

## 3. Findings

| ID | Description | Severity | Status | Resolution |
|----|-------------|----------|--------|------------|
| QAL-049 | ComputeChunkEntries duplicated identically in MemoryTools and MemoryOrchestrator | Medium | **Resolved** | Extracted to `TextAnalysis.ComputeChunkEntries` as public static method. Both call sites updated. |
| QAL-050 | Store flow duplicated between MCP and REST paths | Medium | **Deferred** | Assessed during task 03-1-01. MCP path has diverged with codeRefs, event sink, ref keyword extraction, and CountPattern auto-reviewWhen. REST path uses DTO mapping without these features. Forced unification would create a leaky abstraction. ComputeChunkEntries (the truly identical portion) was extracted; the diverged pipeline remains as-is. |
| QAL-011 | Provider resilience boilerplate duplicated across 5 embedding providers | Medium | **Resolved** | `ResilientEmbeddingProvider` abstract base class owns shared resilience logic. Providers override only `ExecuteEmbedAsync`. ~175 lines removed. |
| QAL-063 | VectorStore has no cross-process file locking | Low | **Resolved** | `FileLock.AcquireShared/AcquireExclusive` added to all 4 disk I/O methods, following the established FileMemoryStore/ScriniaArtifactStore pattern. |

### Remaining Active Concerns (not in phase 03 scope)

| ID | Description | Severity | Phase |
|----|-------------|----------|-------|
| DOC-060 | Documentation references pre-G-47 tool names, stale test counts | High | Release (phase 04) |
| QAL-042 | "Just do it" removal may cause agent friction | Low | 01 (accepted) |
| QAL-043 | CalculateProgress called at 7+ sites — negligible cost today | Low | 01 (accepted) |
| QAL-045 | CalculateProgress promoted to internal — document contract | Low | 01 (accepted) |

## 4. Test Impact

| Suite | Before Phase 03 | After Phase 03 | Delta |
|-------|-----------------|----------------|-------|
| Scrinia.Tests | 821 | 821 | 0 |
| Scrinia.Server.Tests | 63 | 63 | 0 |
| Scrinia.Plugin.Embeddings.Tests | 12 | 12 | 0 |
| **Total** | **896** | **896** | **0** |

**No new tests were added.** This is the third consecutive phase with zero test growth. Across all three G-51 phases, 30+ behavioral changes have been introduced with no dedicated test coverage. The phase 01 retrospective flagged this as a yellow flag, phase 02 escalated it to a confirmed anti-pattern, and phase 03 repeated the pattern.

Notable untested behaviors introduced in this phase:
- `TextAnalysis.ComputeChunkEntries` producing identical output to the former inline implementations in MemoryTools and MemoryOrchestrator
- `ResilientEmbeddingProvider` base class resilience wrapper behaving identically to the former per-provider implementations
- `VectorStore` cross-process contention (two processes reading/writing the same vector file simultaneously)

The root cause is structural: the planner does not consume retrospective findings or belief-derived mandates as planning constraints. Until the retro-to-planner feedback loop is closed, test budgeting will not happen organically.

## 5. Security Posture

### What Was Hardened
- **Cross-process data integrity:** VectorStore file operations are now protected by OS-enforced file locks, preventing corruption when CLI and MCP server access the same vector files simultaneously. This closes the last unprotected disk I/O path — FileMemoryStore and ScriniaArtifactStore were already locked.

### Accepted Risks
- **Store flow duplication remains:** The MCP and REST store pipelines follow a similar flow but are not unified. This is accepted because the paths have legitimately diverged (MCP has codeRefs, event sink, ref keywords; REST has DTO mapping). The risk is maintenance burden (fixes applied in two places), mitigated by the fact that the truly identical portion (ComputeChunkEntries) has been extracted.
- **No cross-process contention tests:** The VectorStore file locking follows the established FileLock pattern (validated by existing FileLock unit tests including concurrent access and stress tests), but there is no dedicated test for VectorStore-specific contention scenarios.

### Remaining Known Issues
- **DOC-060** (high): Documentation still references pre-G-47 tool names. Scheduled for phase 04.

## 6. Configuration Changes

### New Settings
- **None.** Phase 03 introduced no new configuration surface.

### Behavioral Changes (no new config surface)

| Change | Default Behavior | Notes |
|--------|------------------|-------|
| VectorStore lock files | `*.lock` files created alongside vector files | OS-enforced file locks via `FileLock`. Lock files are automatically managed. No operator action needed. |

### Breaking Changes
- **None.** All changes are internal refactoring. The public API surface, configuration, and client behavior are unchanged.
  - `ComputeChunkEntries` moved from private methods to a public static method in `TextAnalysis`, but this is internal infrastructure — no external callers are affected.
  - Embedding providers now inherit from `ResilientEmbeddingProvider`, but the `IEmbeddingProvider` interface contract is unchanged.

### Migration Notes
- No migration required. All changes are backward-compatible internal refactoring.
- VectorStore lock files (`*.lock`) will appear alongside vector files on first access. These are transient and managed automatically.

---

## Execution Summary

| Metric | Value |
|--------|-------|
| Tasks | 3 (all wave 1 parallel) |
| Concerns resolved (formal) | 1 (QAL-049) |
| Non-concern items addressed | 2 (QAL-011, QAL-063) |
| Items deferred with rationale | 1 (QAL-050 — store flow divergence) |
| SOS signals | 0 |
| Replanning events | 0 |
| Rework | 0 |
| Tests before | 896 |
| Tests after | 896 |
| Build warnings | 0 |
| Build errors | 0 |

**Phases remaining:** 1 (Phase 04: Documentation)

---
*Generated by march-reporter agent. Sources: qa:g51-phase03-verification, learn:retro-g51-165-03, research:g51-theme4-code-quality (chunks 1, 2, 11), concern:QAL-049, testing:infrastructure, research:g51-165-plan-summary, learn:beliefs-g51-phase-03.*
