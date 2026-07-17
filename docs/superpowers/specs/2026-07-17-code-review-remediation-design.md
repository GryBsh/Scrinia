# Code Review Remediation — Design

**Date:** 2026-07-17
**Status:** Approved (design), pending implementation plan
**Branch:** `fix/code-review-remediation` (off `main`)

## Goal

Address the findings from the full-codebase review (src/ + web/). The high-severity
findings and the systemic path-prefix pattern were verified directly during the review.
The medium and low tiers are agent-reported at stated confidence but not yet
independently verified — each must be confirmed against the code before any fix is
written. False positives and intentional-design items are documented and dismissed, not
"fixed."

## Decisions (locked)

1. **Verify-first.** For every not-yet-verified finding, confirm the failure scenario
   against the code before touching it. Fix only confirmed issues; record dismissals.
2. **TDD per fix.** Write a failing regression test that reproduces the bug, then fix
   until green. Items where a true unit test is infeasible (native GPU init, MCP stdio
   framing, some web behaviors) get a pragmatic substitute (integration-style check or a
   documented rationale) rather than being skipped silently.
3. **One branch, grouped commits, one PR.** Branch `fix/code-review-remediation`; one
   atomic commit per confirmed fix, grouped tier-by-tier; a single PR at the end.
4. **Sequencing = severity-tiered waves (approach A).** Verified high-severity + systemic
   first, then verify-and-fix medium, then low. Same-file fixes within a tier are grouped
   to avoid conflicts.

## Finding Inventory & Disposition

Confidence values are from the review agents. Tier 1 and Tier 2 were verified by direct
code reads during the review; Tiers 3–4 require a verify step before fixing.

### Tier 1 — Verified high-severity (fix directly, TDD)

| # | Finding | Location |
|---|---------|----------|
| 1 | Merge driver discards the other branch's non-keyword fields (`Description`, `Name`, `Uri`, `OriginalBytes`, `ChunkCount`) and NREs on `ours!` when `ours` is null; `ancestorPath` never read (no true 3-way merge). | `src/Scrinia.Merge/MetaJsonMerger.cs:24-57` |
| 2 | Zip-slip: manifest `category` key flows unchecked into `Path.Combine`/`Directory.CreateDirectory`; per-file guard checks only `fileName`, and the `StartsWith(fullCategory)` check baselines against the already-escaped dir. Arbitrary file write outside `.scrinia`. | `src/Scrinia.Core/Bundles/BundleFormatService.cs:344-376` |
| 3 | `GetVectors` returns the live mutable `List<VectorEntry>` (typed `IReadOnlyList`) with no copy; concurrent `UpsertAsync`/`RemoveAsync` mutate it while readers enumerate → `InvalidOperationException` / torn read. | `src/Scrinia.Core/Embeddings/VectorStore.cs:75-97` |
| 4 | `LoadPluginsAsync` builds `LoggerFactory.Create(b => b.AddConsole()...)` for the embeddings pipeline; runtime `LogWarning` calls write to stdout, which is the JSON-RPC channel in `scri serve` → frame corruption. | `src/Scrinia/Commands/WorkspaceSetup.cs:58` → `src/Scrinia/Services/CoreEmbeddingEventHandler.cs:74,92` |
| 5 | Web login validates the key against `GET /api/v1/keys`, gated by the `ManageKeys` policy; valid non-admin keys get 403 → "Invalid API key" → cannot log in. Probe an endpoint the key is actually authorized for. | `web/src/pages/LoginPage.tsx:18-25` vs `src/Scrinia.Server/Endpoints/KeyEndpoints.cs:12-13` |
| 6 | `EmbeddingsPlugin.EnsureInitialized` is check-then-act with no lock; concurrent cold-start requests load the GGUF onto the GPU twice, orphaning one `LLamaWeights`/`LLamaEmbedder` (VRAM leak) and tearing `_provider`/`_vectorStore`. | `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs:70` |

### Tier 2 — Systemic (verified pattern, single shared fix)

Path-sandbox checks use `StartsWith(root)` without a trailing separator, so a sibling
directory sharing the prefix (e.g. `.scrinia-backup` vs `.scrinia`, `Scrinia-backup` vs
`Scrinia`) clears the boundary. `TryResolveScriniaRelativePath` already does this
correctly (appends `Path.DirectorySeparatorChar`) and is the template. Introduce/reuse a
single helper and apply at:

- `src/Scrinia.Core/FileMemoryStore.cs:1251`
- `src/Scrinia.Mcp/MemoryTools.Bundling.cs:90` (import)
- `src/Scrinia.Mcp/MemoryTools.Core.cs:1593`
- the `categoryDir` baseline in `BundleFormatService` (shares root cause with #2)

### Tier 3 — Medium (verify → fix)

- BM25-only min-max normalization can zero out a unique match, dropped by the
  `relevance <= 0` gate; codebase elsewhere uses max-norm. `WeightedFieldScorer.cs:197`
- `reconcile()` with no args is documented "scan and report" but writes resolved JSON
  during the scan. `MemoryTools.Bundling.cs:196,410`
- `excludeTopics` applied after the limit when embeddings are active → excluded hits
  consume result slots and silently truncate. `MemoryTools.Core.cs:824`
- Chat providers count client-disconnect cancellation as breaker failures
  (`catch (Exception) { RecordFailure(); }`). `OpenAiChatProvider.cs:69`,
  `AnthropicChatProvider.cs:70`, `GeminiChatProvider.cs:77`
- `GetOrAdd` with side-effecting factory double-constructs on concurrent first access,
  leaking `HttpClient`/file locks. `ChatProviderCache.cs:32`, `StoreManager.cs:32`
- `TransientDetector` misclassifies HttpClient timeouts (`TaskCanceledException` wrapping
  `TimeoutException`) → no retry, breaker never trips. `TransientDetector.cs:23`
- `VectorStore` single-record append is non-atomic with no per-record framing; a torn
  tail makes `LoadFromDisk`'s blanket catch drop all vectors for the scope.
  `VectorStore.cs:274`
- `ProcessRunner` binds stdout/stderr reads to the caller's token (so `WaitAsync(None)`
  can't suppress cancellation), and drains stdin fully before starting readers (filter
  deadlock). `ProcessRunner.cs:34,41`
- Nmp2 conflict marking uses `TrimEnd('}')`, stripping all trailing braces and corrupting
  sidecars ending in a nested object. `Nmp2ConflictHandler.cs:84`
- Kestrel `MaxRequestBodySize` (10 MB) < multipart limit (50 MB) → bundle imports 10–50 MB
  are 413'd before the form limit applies. `Program.cs:25,29`
- Web scope-filter tabs vanish once a scope is selected (`scopes` derived from
  already-filtered results). `MemoryBrowserPage.tsx:76`

### Tier 4 — Low (verify → fix, batched)

- `CircuitBreaker` has no single-flight gate on the half-open trial (thundering herd on
  recovery). `CircuitBreaker.cs:33`
- `ResilientEmbeddingProvider` swallows caller cancellation as `null` and leaks the
  success `HttpResponseMessage`. `ResilientEmbeddingProvider.cs:82,96`
- `VectorStore.UpsertAsync` dedups case-sensitively while load/remove are
  case-insensitive → reload divergence. `VectorStore.cs:109`
- Remote-mode `serve` still wires a local embeddings pipeline against the remote store.
  `ScriniaCommands.Serve.cs:37-59`
- Unauthenticated `/health/ready` has no rate limit (SQLite-open amplification).
  `HealthEndpoints.cs:17-18`
- Request path (containing memory names) logged at Info. `RequestTimingMiddleware.cs:12`
- Web `localStorage` bearer token (latent XSS exfil risk). `web/src/api/client.ts:26`
- `consolidate` leaves stale/orphaned vectors un-reindexed. `ScriniaCommands.Migration.cs:474-497`

### Tier 5 — Non-code

- Add `benchmarks/Scrinia.Retrieval.Benchmarks/tools/kompress/model-cache/` (downloaded
  ML model cache: `.safetensors`, `.onnx`, `.pt`, tokenizer blobs — untracked but not
  ignored) to `.gitignore`.

## Per-Item Workflow

For each finding, in tier order:

1. **Verify** — read the code, confirm the concrete failure scenario. If false positive or
   intentional design, record the disposition in the findings ledger and skip.
2. **Test-first** — write a failing xUnit test in the matching test project
   (`Scrinia.Tests`, `Scrinia.Server.Tests`, `Scrinia.Merge.Tests`,
   `Scrinia.Plugin.Embeddings.Tests`). Web items get a test if a harness exists, otherwise
   a documented manual-verification note.
3. **Fix.**
4. **Verify green + build** (see constraints).
5. **Atomic commit** citing the finding.

## Build / Verification Constraints

- Keep `dotnet build Scrinia.sln` green and `dotnet test Scrinia.sln` passing.
- Known snag: `Scrinia.sln` restore fails on a pre-existing `Scrinia.AppHost` NuGetAudit
  (MessagePack) error — bypass with `-p:NuGetAudit=false`, or build/test in-scope projects
  individually.
- New JSON-serialized types must be registered in the appropriate source-gen context
  (`ScriniaMcpJsonContext`, `ServerJsonContext`, `CliJsonContext`, `ConfigJsonContext`,
  `PluginClientJsonContext`).
- `WithTools<T>()` / public-method conventions: keep any new helpers `internal`/`private`.

## Delivery

- Branch `fix/code-review-remediation` off `main`.
- One atomic commit per confirmed fix, grouped tier-by-tier.
- A findings ledger (checklist in the implementation plan / PR body) tracks each item's
  disposition: fixed / false-positive / deferred, with a one-line reason for dismissals.
- One PR at the end summarizing what was fixed, what was dismissed and why, and any
  deferred items.

## Non-Goals

- No unrelated refactoring beyond what a fix directly requires.
- Not addressing the three AGENTS.md "known doc-maintenance gaps" (CI skill/doc parity,
  XML-doc coverage, version-string single-sourcing) — those are tracked follow-ups, not
  review findings.
- Items that prove intentional on verification are documented, not changed.
- No changes to the working-tree files already modified before this effort (the benchmark
  harness / opencode plugin edits) beyond the `.gitignore` addition.
