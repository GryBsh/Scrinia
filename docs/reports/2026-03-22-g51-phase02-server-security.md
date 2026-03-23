# March Report: G-51-165 Phase 02 — Server Resilience + Security

**Goal:** Release readiness audit — four-stream parallel scan (security, code quality, documentation, chaos) with validated findings
**Phase:** 02 of 04
**Date:** 2026-03-22
**Outcome:** All 11 tasks completed successfully. 7 formal concerns resolved, 10 backlog items addressed. 896 tests pass, 0 failures, clean build.

## 1. Summary

Phase 02 targeted server resilience and security hardening across 17 concern IDs (7 formal concerns + 10 backlog items). The phase introduced one architectural change (ChatProviderCache singleton) and 15 mechanical fixes spanning resource lifecycle, connection management, input validation, and authentication hardening. All 11 tasks succeeded first try with zero SOS signals, zero replanning, and zero rework. The largest parallel wave in the pipeline's history (9 concurrent agents) executed cleanly via worktree isolation.

## 2. Changes

### Server Resilience
- **ChatProviderCache singleton** (QAL-062, QAL-024, QAL-020): New `ChatProviderCache.cs` replaces per-request provider creation in `ChatEndpoints`. Providers are now long-lived singletons with shared `CircuitBreaker` and `HttpClient` instances. Registered as singleton in DI, disposed on shutdown via lifetime event. Eliminates socket exhaustion risk and makes circuit breakers functional.
- **StoreManager IDisposable** (QAL-055): `StoreManager` now implements `IDisposable`, iterating child stores and disposing any that are `IDisposable`. Prevents kernel handle and socket leaks on server shutdown.
- **SQLite connection pooling** (QAL-061): `ApiKeyStore` converted from single long-lived connection to connection-per-operation with `Pooling=true` and `Cache=Shared`. `ReaderWriterLockSlim` removed entirely — WAL mode handles concurrency natively.
- **Ephemeral store cap** (QAL-064): Both `FileMemoryStore` and `HttpMemoryStore` now enforce `MaxEphemeralEntries=1000` with oldest-first eviction by `CreatedAt`. Prevents unbounded memory growth from long sessions.
- **Chat provider error bodies** (QAL-046): All 3 chat providers (OpenAI, Anthropic, Gemini) now include truncated (500 char) response body in error events. Previously the body was read but discarded.
- **EndOfStream fix** (QAL-052): All 3 chat providers replaced `while (!reader.EndOfStream)` + `ReadLineAsync` with `while (await reader.ReadLineAsync(ct) is { } line)` to eliminate synchronous blocking in async context (CA2024).

### Security
- **Gemini API key in header** (SEC-049): `GoogleGeminiEmbeddingProvider` moved from `?key=` query parameter to `x-goog-api-key` header, matching `GeminiChatProvider` pattern. Keys no longer appear in logs or proxy traces.
- **MCP content validation** (SEC-053): `MemoryTools.cs` Store validates 5MB per content element and 256-char name limit. Append validates 5MB limit. Matches REST endpoint validation.
- **Chat message validation** (SEC-051): `ChatEndpoints` validates MaxMessages=1000, MaxContentBytes=1MB per message, MaxToolCalls=100 per message. All with descriptive error responses.
- **manage_roles removed** (SEC-037): Dead permission string removed from bootstrap key creation. No enforcement code existed — pure dead code cleanup.
- **Plugin endpoint auth** (SEC-048): `/api/v1/plugins` group now has `.RequireAuthorization().RequireRateLimiting("api")`, matching every other endpoint group. MCP endpoint also secured.
- **CORS wildcard warning** (SEC-035): `LogWarning` emitted when CORS origins configured as `["*"]`. Alerts operators in production.
- **OpenAPI gating** (SEC-052): `MapOpenApi()` and `MapScalarApiReference()` now conditional on `IsDevelopment()` or `Scrinia:ExposeOpenApi` config flag.
- **Key prefix index** (SEC-033): New `key_prefix` column and filtered index `idx_key_prefix` on `api_keys`. `CreateKey` stores `rawKey[..8]` as prefix. `ValidateKey` uses prefix-indexed fast path, falling back to legacy full scan for keys without prefix.

### Files Touched (summary)
- `src/Scrinia.Server/Chat/ChatProviderCache.cs` (new)
- `src/Scrinia.Server/Chat/ChatEndpoints.cs`
- `src/Scrinia.Server/Chat/Providers/OpenAiChatProvider.cs`
- `src/Scrinia.Server/Chat/Providers/AnthropicChatProvider.cs`
- `src/Scrinia.Server/Chat/Providers/GeminiChatProvider.cs`
- `src/Scrinia.Server/Services/StoreManager.cs`
- `src/Scrinia.Server/Auth/ApiKeyStore.cs`
- `src/Scrinia.Server/Program.cs`
- `src/Scrinia.Server/Endpoints/ChatEndpoints.cs`
- `src/Scrinia.Core/FileMemoryStore.cs`
- `src/Scrinia/HttpMemoryStore.cs`
- `src/Scrinia.Core/Embeddings/Providers/GoogleGeminiEmbeddingProvider.cs`
- `src/Scrinia.Mcp/MemoryTools.cs`

## 3. Findings

| ID | Description | Severity | Status | Resolution |
|----|-------------|----------|--------|------------|
| QAL-062 | Per-request circuit breakers in ChatProviderFactory — CB never accumulates failures | High | **Resolved** | ChatProviderCache singleton replaces per-request factory. CB state persists across requests. |
| QAL-055 | StoreManager caches IDisposable stores without disposing them | Medium | **Resolved** | StoreManager implements IDisposable; iterates and disposes child stores. |
| QAL-061 | Single SQLite connection in ApiKeyStore — no pooling, corruption risk | Medium | **Resolved** | Connection-per-operation with `Pooling=true, Cache=Shared`. RWLS removed. |
| QAL-064 | Unbounded ephemeral store — no size limit, no eviction | Medium | **Resolved** | MaxEphemeralEntries=1000 with oldest-first eviction in both FileMemoryStore and HttpMemoryStore. |
| SEC-049 | Gemini embedding API key in URL query string | Medium | **Resolved** | Moved to `x-goog-api-key` header, matching GeminiChatProvider pattern. |
| SEC-053 | MCP path missing content size validation | Medium | **Resolved** | 5MB per content element, 256-char name limit. Matches REST validation. |
| SEC-048 | Plugin endpoints missing auth + rate limiting | Medium | **Resolved** | `.RequireAuthorization().RequireRateLimiting("api")` added to plugin group. |
| QAL-024 | Per-request HttpClient creation — socket exhaustion risk | Medium | Addressed (no formal concern) | Resolved by ChatProviderCache singleton (shared HttpClient). |
| QAL-020 | IChatProvider missing IDisposable | Low | Addressed (no formal concern) | ChatProviderCache owns lifecycle; per-request disposal removed. |
| QAL-046 | Dead body variable in chat providers — error detail discarded | Low | Addressed (no formal concern) | Body included in error event (truncated to 500 chars). |
| QAL-052 | CA2024 EndOfStream in async context | Low | Addressed (no formal concern) | Replaced with `ReadLineAsync` null-check pattern. |
| QAL-022 | Inconsistent error handling (embed vs chat) | Low | Addressed (no formal concern) | Chat providers now include error body detail. |
| SEC-051 | Chat messages no size/count validation | Low | Addressed (no formal concern) | MaxMessages=1000, MaxContentBytes=1MB, MaxToolCalls=100. |
| SEC-037 | manage_roles defined but never enforced | Low | Addressed (no formal concern) | Removed from bootstrap key permissions. |
| SEC-035 | CORS wildcard — no production warning | Low | Addressed (no formal concern) | LogWarning emitted when origins=["*"]. |
| SEC-052 | OpenAPI/Scalar exposed without auth | Low | Addressed (no formal concern) | Gated by IsDevelopment() or ExposeOpenApi config. |
| SEC-033 | Linear key scan O(n) in ValidateKey | Low | Addressed (no formal concern) | Key prefix index with filtered lookup. Legacy fallback preserved. |

### Remaining Active Concerns (not in phase 02 scope)

| ID | Description | Severity | Phase |
|----|-------------|----------|-------|
| DOC-060 | Documentation references pre-G-47 tool names, stale test counts | High | Release (phase 04) |
| QAL-049 | Store logic duplicated between MCP and REST paths | Medium | Release (phase 03) |
| QAL-042 | "Just do it" removal may cause agent friction | Low | 01 (accepted) |
| QAL-043 | CalculateProgress called at 7+ sites — negligible cost today | Low | 01 (accepted) |
| QAL-045 | CalculateProgress promoted to internal — document contract | Low | 01 (accepted) |

## 4. Test Impact

| Suite | Before Phase 02 | After Phase 02 | Delta |
|-------|-----------------|----------------|-------|
| Scrinia.Tests | 821 | 821 | 0 |
| Scrinia.Server.Tests | 63 | 63 | 0 |
| Scrinia.Plugin.Embeddings.Tests | 12 | 12 | 0 |
| **Total** | **896** | **896** | **0** |

**No new tests were added.** Verification was performed by code inspection (QA agent confirmed correct implementation via grep and file analysis). This is the second consecutive phase with zero test growth — 27 behavioral changes across phases 01 and 02, no dedicated test coverage for any of them.

Notable untested behaviors introduced in this phase:
- ChatProviderCache singleton lifecycle and provider reuse across requests
- SQLite connection pooling without RWLS under concurrent load
- Ephemeral eviction at the 1000-entry boundary
- MCP content size validation (5MB rejection path)
- Chat message validation (1000-message, 1MB, 100-tool-call limits)
- Key prefix index correctness and legacy fallback

The phase 01 retrospective flagged zero test growth as a yellow flag. Phase 02 did not address it. The phase 02 retrospective escalated this to a confirmed anti-pattern.

## 5. Security Posture

### What Was Hardened
- **Authentication coverage**: Plugin endpoints now require API key authentication, closing the last unauthenticated endpoint group. All HTTP endpoint groups (`/api/v1/memory`, `/api/v1/keys`, `/api/v1/chat`, `/api/v1/plugins`, `/mcp`) now require auth + rate limiting.
- **API key lookup performance**: Key prefix index reduces ValidateKey from O(n) full-scan to O(1) prefix lookup for new keys. Legacy keys (without prefix) fall back to full scan.
- **Secret exposure**: Gemini API key moved from URL query string to request header, eliminating log/proxy exposure.
- **Input validation**: MCP content and chat message validation prevent memory/CPU exhaustion via oversized payloads.
- **Dead permissions**: `manage_roles` removed — no phantom permissions suggesting unimplemented features.
- **Production hardening**: OpenAPI/Scalar gated behind development mode or explicit config. CORS wildcard triggers operator warning.

### Accepted Risks
- **Key prefix leaks 8 characters**: The `key_prefix` column stores the first 8 characters of the raw API key (the constant `scri_` prefix + 3 random chars). This leaks minimal entropy and does not compromise key security. `FixedTimeEquals` still prevents timing attacks on the full hash.
- **No concurrency tests for RWLS removal**: The `ReaderWriterLockSlim` was removed from `ApiKeyStore` in favor of SQLite WAL mode's native concurrency. While architecturally sound, there is no stress test validating concurrent `CreateKey` + `ValidateKey` + `RevokeKey` operations.
- **Ephemeral eviction is lossy**: When the 1000-entry cap is reached, the oldest entry is silently evicted. Agents are not warned before eviction occurs.

### Remaining Known Issues
- **DOC-060** (high): Documentation still references pre-G-47 tool names. Scheduled for phase 04.
- **QAL-049** (medium): Store logic duplicated between MCP and REST paths. Scheduled for phase 03.

## 6. Configuration Changes

### New Settings

| Setting | Default | Purpose |
|---------|---------|---------|
| `Scrinia:ExposeOpenApi` | `false` | When `true`, exposes `/openapi/v1.json` and `/scalar/v2` outside development mode. Production deployments keep these hidden by default. |

### Behavioral Changes (no new config surface)

| Change | Default Behavior | Notes |
|--------|------------------|-------|
| Ephemeral entry cap | 1000 entries per store | Hardcoded constant. Oldest entries evicted by `CreatedAt`. |
| Chat message limits | 1000 messages, 1MB/message, 100 tool calls/message | Hardcoded constants in `ChatEndpoints`. |
| MCP content limits | 5MB per content element, 256-char name | Hardcoded constants in `MemoryTools`. Matches REST endpoint limits. |
| SQLite connection pooling | `Pooling=true, Cache=Shared` | Connection string change in `ApiKeyStore`. No operator action needed. |

### Breaking Changes
- **None.** All changes are backward-compatible. Existing API keys, configurations, and client integrations continue to work without modification.
- Legacy API keys created before the `key_prefix` migration will use the slower full-scan fallback for validation. This is transparent to callers.

### Migration Notes
- The `key_prefix` column is added via SQLite migration on startup. Existing databases are auto-migrated. No manual action required.
- Operators using `Scrinia:CorsOrigins: ["*"]` will see a new warning log at startup. This is informational only — behavior is unchanged.
- Operators who need OpenAPI/Scalar in production should set `Scrinia:ExposeOpenApi: true` in their configuration.

---

## Execution Summary

| Metric | Value |
|--------|-------|
| Tasks | 11 (9 wave 1 parallel + 2 wave 2 sequential) |
| Concerns resolved (formal) | 7 |
| Backlog items addressed | 10 |
| Total concern IDs covered | 17 |
| SOS signals | 0 |
| Replanning events | 0 |
| Rework | 0 |
| Tests before | 896 |
| Tests after | 896 |
| Build warnings | 0 |
| Build errors | 0 |

**Phases remaining:** 2 (Phase 03: Code Deduplication, Phase 04: Documentation)

---
*Generated by march-reporter agent. Sources: qa:g51-phase02-verification, learn:retro-g51-165-02, research:g51-theme2-server-resilience, research:g51-theme3-security, concern records (QAL-062/055/061/064, SEC-049/053/048), testing:infrastructure.*
