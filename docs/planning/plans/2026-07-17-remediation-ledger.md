# Code Review Remediation — Findings Ledger

Disposition of every finding from the full-codebase review, per the plan
`2026-07-17-code-review-remediation.md`. Branch `fix/code-review-remediation`.

Legend: **fixed** (commit) · **dismissed** (intentional / not a defect) · **deferred / discovered** (out of scope, logged for follow-up).

## Tier 1 — verified high-severity (all fixed)

| # | Finding | Disposition | Commit |
|---|---------|-------------|--------|
| 1 | Merge driver discarded the other branch's fields + NRE on null `ours` | fixed (true 3-way merge) | `695a177` |
| 2 | Zip-slip via manifest `category` key in bundle import | fixed (validate against base dir) | `e254b00` |
| 3 | `VectorStore.GetVectors` returned the live mutable list | fixed (snapshot copy) | `b900021` |
| 4 | Embeddings console logger corrupted the MCP stdio frame in `serve` | fixed (route to stderr) | `ea1144a` |
| 5 | Web login rejected valid non-admin keys (ManageKeys gate) | fixed (probe authenticated read route) | `a288ac7` |
| 6 | `EmbeddingsPlugin.EnsureInitialized` double-init race (VRAM leak) | fixed (double-checked lock) | `3d8567b` (+ test `b79badf`) |

## Tier 2 — systemic path-prefix (fixed)

| # | Finding | Disposition | Commit |
|---|---------|-------------|--------|
| 7 | Shared `PathContainment.IsWithin` (trailing-separator-safe) | added | `e751553` |
| 8 | Applied at FileMemoryStore / MemoryTools.Bundling / MemoryTools.Core + folded BundleFormatService's local helper | fixed | `b0d9e98` |

## Tier 3 — medium (all confirmed + fixed)

| # | Finding | Disposition | Commit |
|---|---------|-------------|--------|
| 9 | `TransientDetector` misclassified HttpClient timeout | fixed | `0221c32` |
| 10 | `ProcessRunner` bound reads to caller token → threw instead of `TimedOut` | fixed | `4417370` |
| 11 | `Nmp2ConflictHandler` `TrimEnd('}')` stripped all trailing braces | fixed | `cd6b92d` |
| 12 | Chat providers counted client-cancellation as breaker failures (3 files) | fixed | `9222cb8` |
| 13 | `ResilientEmbeddingProvider` leaked response + swallowed cancellation | fixed | `1f51ca8` |
| 14 | Kestrel 10 MB < 50 MB multipart → imports 413'd early | fixed (per-request body-size override) | `ad9cf5b` |
| 15 | `CircuitBreaker` no single-flight half-open trial | fixed (`TryEnterHalfOpen`) | `ee2ed2f` |
| 16 | BM25 min-max normalization dropped a unique keyword-only match | fixed (max-norm) | `e4bf453` |
| 17 | no-arg `reconcile()` wrote to disk during "scan and report" | confirmed + fixed (read-only scan) | `84d1fcc` |
| 18 | `excludeTopics` applied after limit in embeddings search path | confirmed + fixed (new overload) | `9628040` |
| 19 | `ChatProviderCache` / `StoreManager` `GetOrAdd` double-construct | confirmed + fixed (`Lazy<T>`) | `70875b3` (+ poison-evict `d00fd10`) |
| 22 | Web scope-filter tabs vanished after selecting a scope | confirmed + fixed (accumulated `knownScopes`) | `db947e8` |

## Tier 4 — low (confirmed + fixed, or dismissed)

| # | Finding | Disposition | Commit |
|---|---------|-------------|--------|
| 20 | `VectorStore` torn append dropped the whole scope | confirmed + fixed (tolerate torn tail) | `6ae410d` |
| 21 | `VectorStore.UpsertAsync` case-sensitive dedup vs load/remove | confirmed + fixed (`OrdinalIgnoreCase`) | `0ff6319` |
| 23a | Remote-mode `serve` wired a local embeddings pipeline | confirmed + fixed | `f4fe96d` |
| 23b | `/health/ready` unauthenticated + unrated (SQLite amplification) | confirmed + fixed (`RequireRateLimiting`) | `aa8eec4` |
| 23c | Memory names in request path logged at Info | **dismissed** — Operator with log access already has plaintext filesystem access to memory bodies (per `docs/security.md`); redaction removes diagnostic value for no security gain | — |
| 24a | `consolidate` left stale/orphaned vectors | confirmed + fixed (re-embed via event sink) | `5138679` |
| 24b | Web bearer token in `localStorage` | **dismissed** — latent, not active; no XSS/DOM-injection sink exists (content renders as text). Follow-up if threat model tightens: `sessionStorage` / short-lived token / HttpOnly cookie | — |

## Tier 5 — housekeeping

| # | Finding | Disposition | Commit |
|---|---------|-------------|--------|
| 25 | Ignore the downloaded benchmark model cache | fixed (`.gitignore`) | this commit |

## Residuals & minor notes (surfaced to the final whole-branch review)

- **Task 3:** `VectorStore.GetVectors` fast-path `ToArray()` is unsynchronized — could still throw under adversarial interleaving; the clean fix is copy-on-write in Upsert/Remove (a naive lock would stall reads behind upsert disk I/O).
- **Task 6:** `EmbeddingsPlugin._vectorStore` double-checked read is not `volatile` (brief-mandated pattern) — add `volatile` to fully harden weak memory models.
- **Task 16:** the new BM25 regression test covers the entry site only, not the (textually identical) chunk site. Max-norm shifts the Sum-mode BM25 contribution scale globally (RRF mode immune) — full search suite stayed green.
- **Task 20:** torn-tail catch is scoped to `EndOfStreamException`; a mid-append `IOException` still drops the whole scope (defensible).
- **Task 22:** one-frame stale-tab flash on store switch; `scopeFilter` not reset on store change (pre-existing).

## Discovered during remediation (new, out of scope — follow-up candidates)

- **`ResolveExecutable` (ProcessRunner):** the PATHEXT loop appends extensions onto already-qualified names, so `"cmd.exe"` / `"git.exe"` never resolve on Windows (Task 10).
- **`FileMemoryStore.FormatQualifiedName`/`ParseQualifiedName`:** round-trip loss for v1 `"memory/X"`-namespaced scopes (Task 24).
- **Test isolation:** several tests share static `WorkspaceSetup.Configure` state and can race under `--filter` (full-suite runs are green) (Task 24).
