# March Report: Resilience & Meta-Skills — 2026-03-21

**Goals completed:** G-20 through G-1 (5 goals)
**Duration:** Single session (continuation)
**Final state:** 803 tests pass (730 CLI + 61 server + 12 embeddings), release-ready

---

## 1. Summary

This session continued from the v0.5 release baseline (745 tests, zero accepted tech debt) and built three arcs of work:

1. **Resilience infrastructure (G-20)** — full retry, circuit breaker, and transient detection stack across all external HTTP providers, internal I/O, and health observability. Three phases: core primitives (44 tests), provider integration (8 providers), and local I/O + registry + health endpoints.
2. **Process improvement (G-21, G-23)** — guide nudges for planner-driven workflows, background agent execution in the planner skill, and two new meta-skills (evolutionary and cartographer) that improve the knowledge base itself.
3. **Audit, remediation, and backlog (G-22, G-1)** — 4-stream release readiness audit producing 71 new findings with parallel remediation, followed by backlog infrastructure and a product-evolutionary skill that produced 5 improvement proposals from observed friction.

---

## 2. Changes

### Resilience Infrastructure (G-20)

| File | Purpose |
|------|---------|
| Resilience/TransientDetector.cs | Static classifier: HTTP 429/500/502/503/504, timeouts, SocketException, TaskCanceledException |
| Resilience/RetryPolicy.cs | Exponential backoff + jitter, Retry-After header support, sync and async overloads |
| Resilience/CircuitBreaker.cs | State machine (Closed/Open/HalfOpen) with Interlocked thread safety, configurable threshold + cooldown |
| Resilience/CircuitBreakerRegistry.cs | Static ConcurrentDictionary for centralized CB observability across providers |
| Chat/Providers/*.cs (3 providers) | OpenAI, Anthropic, Gemini — EnsureClosed + RetryPolicy.ExecuteAsync + RecordSuccess/Failure |
| Embeddings/Providers/*.cs (5 providers) | OpenAI, Azure, Google, Ollama, VoyageAI — same resilience pattern |
| HttpMemoryStore.cs | Sync + async HTTP calls wrapped with retry; HttpContent buffered to byte[] for sync retry safety |
| ApiKeyStore.cs | SQLite BUSY retry via RetryPolicy + WAL mode enabled |
| Endpoints/HealthEndpoints.cs | /health/details exposes CircuitBreakerRegistry state; CB entries excluded from readiness pass/fail |

### Process & Tooling Improvements (G-21, G-23)

| Change | Purpose |
|--------|---------|
| guide() nudges: planner mandatory, tool hints at decision points | Enforce planner-driven workflow at research_complete, plan_tasks, task_next, plan_resume |
| Planner skill: `run_in_background: true` for execution agents | Primary agent stays responsive during parallel task execution |
| Built-in evolutionary skill (5-step methodology) | Proactive knowledge base improvement: scan for staleness, verify accuracy, enrich keywords, prune dead entries |
| Built-in cartographer skill (4-step methodology) | Cross-domain connection indexing: map bridges between topics, enrich memories with cross-references |

### Release Audit & Remediation (G-22)

| Fix | Finding | Severity |
|-----|---------|----------|
| HttpContent reuse in SendSync retries — buffer to byte[] | SEC-028 | MEDIUM |
| HttpContent reuse in WriteArtifactAsync — fresh inside lambda | SEC-029 | MEDIUM |
| /health/ready CB entries excluded from readiness logic | SEC-030 | LOW |
| Rate limiter wrong claim name | SEC-036 | LOW |
| MCP import tool path traversal — workspace sandbox enforced | SEC-041 | MEDIUM |
| RetryOnBusy deduplicated to use RetryPolicy | QAL-014 | MEDIUM |
| HttpResponseMessage leaked on retry | QAL-018 | LOW |
| RetryPolicy static Random replaced with lock-free pattern | QAL-023 | LOW |
| RetryOptions validation (MaxRetries upper bound, delay overflow) | SEC-025/026 | MEDIUM/LOW |
| ValidateKey TOCTOU — 3 lock acquisitions consolidated to 1 write lock | SEC-032 | MEDIUM |
| Bootstrap key file auto-cleanup warning | SEC-034 | HIGH |
| GeminiChatProvider unused _apiKey field removed | SEC-038 | LOW |
| Content size check uses byte length (not char count) | SEC-039 | LOW |
| 22 documentation fixes (test counts, filenames, signatures, project layout, config) | DOC-018 through DOC-037 | HIGH-LOW |

### Backlog Infrastructure (G-1)

| Change | Purpose |
|--------|---------|
| backlog:* reserved topic with guide docs + tool hints | Structured deferred-work tracking across goals |
| Built-in evolutionary skill updated with backlog scan step | Agents discover and triage deferred work during knowledge improvement |
| product-evolutionary project-specific skill created | Self-referential improvement: observe meta-skill friction, propose tool changes |
| 4 backlog entries seeded (resilience, auth, docs, scrinia) | Initial deferred items from G-22 accepted findings |

---

## 3. Findings

**Total findings: 110** (47 SEC + 24 QAL + 39 DOC)
**Fixed: 77** | **Dismissed: 16** | **Accepted: 17**

All 16 dismissals (G-9 through G-17) debugger-verified. Accepted items are low-severity or architectural decisions tracked for future consideration.

New findings from this session:

| Range | Category | Count | Key items |
|-------|----------|-------|-----------|
| SEC-023 through SEC-047 | Security | 25 | CB race conditions (accepted), HttpContent reuse (fixed), import path traversal (fixed), bootstrap key (fixed) |
| QAL-011 through QAL-024 | Quality | 14 | Provider boilerplate duplication (accepted), RetryOnBusy dedup (fixed), HttpContent reuse (fixed) |
| DOC-018 through DOC-039 | Documentation | 22 | Test counts (fixed), stale filenames (fixed), missing resilience docs (fixed) |

**Next available IDs:** SEC-048, QAL-025, DOC-040

---

## 4. Test Impact

| Suite | Start of Session | End of Session | Delta |
|-------|-----------------|----------------|-------|
| Scrinia.Tests (CLI) | 673 | 730 | +57 |
| Scrinia.Server.Tests | 60 | 61 | +1 |
| Scrinia.Plugin.Embeddings.Tests | 12 | 12 | -- |
| **Total** | **745** | **803** | **+58** |

The bulk of new tests are resilience infrastructure: 44 unit tests for core primitives (TransientDetector, RetryPolicy, CircuitBreaker), 7 provider integration tests, 7 local I/O tests (HttpMemoryStore, ApiKeyStore), and server-side health endpoint CB state tests.

---

## 5. Meta-Skills

Three meta-skills ran during this session, each producing measurable knowledge base improvements:

### Evolutionary (built-in skill)
First run of the proactive knowledge improvement agent. Scanned the full memory base for staleness and accuracy.
- **14 memories updated** — stale test counts, renamed class references, outdated tool counts, missing resilience layer in architecture descriptions
- **3 patterns surfaced** — HttpContent reuse, resilience integration, file-conflict analysis
- **1 pruned** — dead memory with no remaining references
- **2 norms updated** — agent:profile behavioral norms refined
- **1 skill updated** — release-auditor exclusion list extended

### Cartographer (built-in skill)
First run of the cross-domain connection indexing agent. Mapped relationships between isolated topic areas.
- **12 connections mapped** — resilience-to-auth, resilience-to-server, testing-to-resilience, etc.
- **3 bridges created** — new memories linking previously unconnected domains
- **22 memories enriched** — keywords and cross-reference pointers added for search recall
- **8/8 validations passed** — all connections verified bidirectional

### Product-Evolutionary (project-specific skill)
First run of the self-referential improvement agent. Observed friction from the evolutionary and cartographer runs and proposed tool improvements.
- **5 product improvement ideas** (PEA-001 through PEA-005) — metadata-only updates, code drift detection, memory compaction, reverse-reference index, file-conflict analysis in plan_tasks
- Ideas stored in backlog:scrinia and detailed in the companion product ideas report

---

## 6. Configuration Changes

### New: Resilience Properties in ChatOptions / EmbeddingOptions

| Setting | Default | Purpose |
|---------|---------|---------|
| MaxRetries | 3 | Maximum retry attempts for transient failures |
| RetryBaseDelayMs | 200 | Base delay for exponential backoff (with jitter) |
| CircuitBreakerThreshold | 5 | Consecutive failures before circuit opens |
| CircuitBreakerCooldownSeconds | 30 | Duration circuit stays open before half-open probe |

All configurable via appsettings.json under `Scrinia:Chat` and `Scrinia:Embeddings` sections. Providers inherit these defaults; per-provider override is supported.

---

## 7. Deferred

Deferred items are tracked in backlog:* topics for future goals:

| Topic | Items | Key entries |
|-------|-------|-------------|
| backlog:resilience | 4 | Provider boilerplate extraction (QAL-011), IChatProvider IDisposable (QAL-020), error handling consistency (QAL-022), per-request HttpClient (QAL-024/SEC-045) |
| backlog:auth | 4 | CORS wildcard warning (SEC-035), manage_roles enforcement (SEC-037), Argon2/bcrypt consideration (SEC-046), key lookup scaling (SEC-033) |
| backlog:docs | 2 | AGENTS.md docs/ section gaps (DOC-038), stale filename references (DOC-039) |
| backlog:scrinia | 7 | Native links, auto-detect test counts, skill override precedence, recurring pattern auto-detection, agent MCP tool, backlog promotion, stale auto-flagging |

Additionally, 5 product improvement proposals (PEA-001 through PEA-005) from the product-evolutionary skill are documented in the companion report.
