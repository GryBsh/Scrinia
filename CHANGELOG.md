# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Scrinia uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The single source of truth for the version number is `Directory.Build.props` at the
repository root — bumping it there propagates to the CLI banner, the MCP `ServerInfo`,
and all assembly attributes.

## [Unreleased]

### Changed (breaking) — path-only memory names
- **The `topic:subject` colon naming syntax was removed; names are paths.**
  `api:auth` is now `/api/auth`. `ParseQualifiedName` rejects a colon name with
  a message suggesting the path form. All display (`list`, `search`, store
  confirmations) already rendered paths via `FileMemoryStore.FormatQualifiedName`,
  but `ScriniaArtifactStore.FormatQualifiedName` (used by the CLI) still emitted
  colon — it now emits paths too.
- **The internal `memory/` topic namespace was dropped.** A topic now resolves
  the same whether reached by a `/topic/subject` path or a bare topic filter —
  `local-topic:{topic}` (`.scrinia/topics/{topic}/`), not
  `local-topic:memory/{topic}`. This fixes `--scopes {topic}` / `Export([{topic}])`
  not matching path-stored memories. Data under the old `.scrinia/topics/memory/`
  layout is still read via fallback. `skill` (entity) and `agent` scopes are
  unchanged.
- Agent-config resolution moved from `agent:{name}` to the `/agent/{name}` path,
  which also repairs `restore`'s `/agent/**`, `/patterns/**`, `/checkpoint/latest`,
  and `/sessions/{today}` follow-ups (they were reading legacy colon-scoped dirs
  that path writes never populate).
- **In-content cross-references are now `[[/path]]` wiki-links.** The auto-linker
  (`MaintenanceEventSink` / `ReferenceExtractor`) matches `[[/api/auth]]` /
  `[[note]]` instead of bare `topic:subject`.
- The internal topic-scope prefix was shortened from `local-topic:` to `local:`
  (e.g. `local:api`), and `list --summary` now labels topic groups `/api` instead
  of `topic:api`. Scope strings feed vector-store directory names, so semantic
  search over existing topic data needs a one-time `scri reindex` (the same
  reindex the namespace change above already requires; BM25 search is unaffected).

### Changed (breaking) — flat CLI surface
- **The `memory` command group is gone; its subcommands are now top-level.**
  `scri memory list/search/show/store/forget/append/compact/link` become
  `scri list/search/show/store/forget/append/compact/link`. There is no
  `memory` alias — update any scripts that used the prefix.
- Trimmed agent-directed meta-commentary from `--help`: `guide` no longer says
  "Call once per session", and `hint`'s description drops the hook-envelope
  internals (and a stray XML tag that was leaking into the rendered help).

### Removed (breaking) — LLM-at-rest enrichment
- **LLM-at-rest enrichment pipeline deleted.** `LlmConsolidator` and the entire
  Tier 2 sweep over the local store is gone. Scrinia no longer consults an LLM
  at write time, at read time, or in any background sink. The deletion was
  driven by an empirical finding: MemBench R@5 / R@10 / NDCG@10 are
  **identical** with and without the sentence-MaxSim reranker (0.0 / 0.0 / 0.0
  deltas on the published runs), proving that the candidate-pool composition —
  not the rescoring stage — is the bottleneck. Agent-supplied retrieval anchors
  at write time are the right lever; LLM-at-rest enrichment was polishing the
  wrong end of the system. The LLM clients themselves were moved to the bench
  (see below) where they belong as a test harness simulating the production
  agent.
- `ImportanceScoringSink`, the `Importance` field on `ArtifactEntry`, the
  `Importance` weight in `RankerOptions`, and the importance branch in
  `ComputeTotalScore`. The ranker now composes relevance + recency only;
  setting `α_recency = 0` falls back to pure relevance.
- `McpLlmPluginHost` and the bundled local-LLM plugin projects:
  `Scrinia.Plugin.Llm`, `Scrinia.Plugin.Llm.Cli`, and `Scrinia.Plugin.Llama`
  (plus their test projects). `publish.ps1 -WithLlm` and the
  `scri-plugin-llm` subprocess no longer exist.
- `scri consolidate --with-llm` flag and the entire Tier-2 progress-file
  machinery (`.scrinia/.tier2-progress.json`). Existing files in user
  workspaces are now orphaned and can be deleted by hand.
- `scri reindex --importance` flag and its backfill path.
- The `SessionEnd` consolidate hook installer in `scri setup --hooks`. Only
  `SessionStart` and `UserPromptSubmit` hooks are installed now. The Codex
  no-SessionEnd workaround in the installer is also gone (it existed solely
  to skip the consolidate hook on Codex).
- The entire `Scrinia:Llm:*` configuration block (~14 keys, including
  `Provider`, `BaseUrl`, `Model`, `ApiKey`, `Temperature`, `RequestTimeoutSeconds`,
  `LocalModelFile`, `LocalModelUrl`, `LocalContextSize`, `AnthropicApiKey`,
  `AnthropicBaseUrl`, `GeminiApiKey`, `GeminiBaseUrl`). `scri setup` no longer
  prompts for any LLM provisioning — only embedding-provider config.
- "Tier 1 / Tier 2" framing throughout the user-facing documentation. There is
  no tier model anymore: writes happen at write time and `scri consolidate`
  is a mechanical-only manual sweep.
- `IBackgroundLlm` (and `BackgroundLlmContext.Default`) in `Scrinia.Core`. The
  bench has its own simpler `IBenchLlmClient` interface.

### Added
- **`RetrievalAnchors` field on `ArtifactEntry`** (string[]? — agent-supplied
  phrases, questions, or category-level paraphrases that bridge likely user
  queries to the entry's content). Use the vocabulary you expect users to query
  with, not the vocabulary already in the content (the indexer already covers
  exact-match). Indexed via the weighted field scorer (+15 on per-anchor
  contains-match — same weight the old `Facts` field carried) and TF-seeding
  at +2 so BM25 picks up anchor-derived tokens naturally.
- New `anchors` parameter on the MCP `memory_remember` tool. Optional, defaults
  to null. Empty / whitespace-only anchors are dropped at the boundary; an
  empty array is treated as null on the entry. The parameter is additive —
  existing callers that don't supply `anchors` see no behavior change.
- **Bench-side agent harness** under
  `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/` — an `IAgentHarness`
  abstraction with `GenerateAnchorsAsync` / `RewriteQueryAsync` /
  `GenerateHypotheticalAsync` ops, wired into the bench's ingest and query
  paths. New CLI flags: `--agent <off|anchors|rewrite|hyde|all>`,
  `--agent-provider <openai|anthropic|gemini|ollama|claude-cli|codex-cli|copilot-cli>`,
  `--agent-base-url`, `--agent-model`, `--agent-api-key`. Per-op response
  caching under `benchmarks/.../cache/agent-{provider}-{model}.ndjson` so
  reruns hit the cache and only first-pass calls hit the LLM. See
  [`docs/research/agent-harness.md`](research/agent-harness.md) for the full
  design including the anti-data-leakage prompt discipline.
- The LLM clients themselves moved to
  `benchmarks/Scrinia.Retrieval.Benchmarks/Llm/Providers/`:
  `IBenchLlmClient`, `OpenAiCompatibleBenchLlmClient` (covers
  Ollama / LM Studio / llama.cpp / vLLM / real OpenAI),
  `AnthropicBenchLlmClient`, `GeminiBenchLlmClient`,
  `AgentCliBenchLlmClient` (shells out to `claude` / `codex` / `copilot` CLIs
  reusing the user's CLI auth), and the shared `ResilientLlmProvider` base.

### Changed (breaking)
- **`ArtifactEntry.Facts` renamed to `ArtifactEntry.RetrievalAnchors`.** Hard
  rename, no read-side migration: persisted entries with the old `Facts` JSON
  field deserialize to `RetrievalAnchors = null` silently. Re-save any entries
  whose anchor data must be retained (or accept the loss — the field had no
  user-facing surface beyond ranking weight).
- **`scri consolidate` is mechanical-only.** `--with-llm` is removed (unknown
  flag → exit 1). The command does session compaction plus stale-entry reporting
  and nothing else.
- **`scri reindex` has no `--importance` flag.** Existing scripts passing it
  get an unknown-flag error.
- **`scri setup --hooks` no longer installs the SessionEnd consolidate hook.**
  Existing user installs are not actively cleaned up; re-running
  `--uninstall-hooks` removes scrinia-managed blocks including any prior
  SessionEnd entry. Run `scri consolidate` by hand when you want session
  housekeeping.
- **Production `Scrinia.Core` is LLM-free.** All LLM client code moved to
  `Scrinia.Retrieval.Benchmarks.Llm.Providers`. Downstream consumers that
  referenced `Scrinia.Core.Llm.IBackgroundLlm` or any concrete client need to
  either depend on the bench assembly or implement their own client.

### Changed
- **MCP surface split: `memory(action, …)` dispatcher replaced with ten per-verb
  tools** — `memory_remember`, `memory_recall`, `memory_search`, `memory_list`,
  `memory_append`, `memory_forget`, `memory_compact`, `memory_link`,
  `memory_restore`, `memory_reconcile`. The dispatcher's single JSONSchema had
  to declare every parameter across every operation, so it couldn't express
  "if `action='remember'` then `content[]` is required and `appendContent` is
  ignored" — the most salient parameter name won under autonomous reasoning
  and agents reliably produced malformed calls 40 turns into a session.
  Per-verb schemas give each operation exactly its own fields and upgrade the
  correctness signal from prose-in-description to validator-level. Skill-path
  routing is preserved inside `memory_recall` / `memory_remember` (no separate
  `skill_*` surface). `memory_append` parameter renamed from `appendContent`
  to `content` (the disambiguation suffix was an artifact of the dispatcher's
  ambiguity and is no longer needed). `INVALID_ACTION` error code is gone —
  unknown names now fail at MCP tool-registration, not in a runtime handler.

### Fixed
- `scri hint` no longer silently drops memories whose best score came from a
  chunk match. `WeightedFieldScorer.SearchAll` deduplicates per memory and
  emits one result per memory — either an `EntryResult` or a `ChunkEntryResult`
  depending on which scored higher — and the prior hint filter dropped the
  latter entirely, so multi-chunk session entries whose entry-level score was
  below the floor would never appear even when a chunk matched strongly.
- `FileMemoryStore.SearchAll(query, scopes, limit, excludeTopics)` now uses
  the workspace's configured `RankerOptions` instead of falling back to the
  defaults. Previously any caller exercising topic exclusion silently lost
  `Scrinia:Search:Alpha:*` config.
- `FileMemoryStore.Remove` invalidates the topic-discovery cache when removing
  from a `local-topic:*` scope. Without this, `DiscoverTopics` reported topics
  with no remaining entries until the cache flushed naturally.
- `ImportanceScoringSink.OnAppendedAsync` now rescores against the **full**
  memory after the append (existing + new), not the appended snippet alone.
  Previously a one-line "Yes" append could clobber an `Importance=9`
  architectural decision with a score derived from the snippet alone.
- `ImportanceScoringSink` is now registered in the server-mode event-sink
  pipeline as well as the CLI. Previously the CHANGELOG claimed coverage of
  "every workspace-bootstrap path" but server-mode writes were skipped.
- `MemoryTools.FireEventSinkAsync` now passes `CancellationToken.None` to the
  sink action rather than the request CT. The sink is fire-and-forget on a
  background `Task`, so propagating the request-scoped CT would abort the
  sink as soon as the user-facing response returned — leaving importance
  unscored or the vector index half-written. The synchronous
  `OnForgottenAsync` call sites continue to use the request CT, since their
  side effects must complete before the response returns.

### Added
- `scri hint` now applies **Maximal Marginal Relevance (MMR)** diversity rerank
  to its top-K output. Fixes a documented retrieval failure where one chatty
  session floods the hint results because its memories share vocabulary —
  with λ = 0.6 (default) the reranker breaks the flood by demoting candidates
  too-similar to already-selected ones, surfacing memories from other sources
  (`/findings/`, `/skill/`) that the BM25 top-3 would otherwise miss.
  Similarity is TF-cosine over already-loaded `TermFrequencies` dicts — zero
  additional I/O. Configurable via `Scrinia:Hint:DiversityLambda` (1.0 reverts
  to pre-MMR relevance-only ordering, 0.0 = pure diversity) and
  `Scrinia:Hint:InnerLimit` (the candidate pool size MMR diversifies down to
  top-3; default 10).
- New `IBackgroundLlm.ScoreImportanceAsync` Tier 2 capability — short prompt,
  parses a 1–10 integer reply, clamps out-of-range / null on failure. Wired
  through every concrete provider: OpenAI-compat (Ollama, llama.cpp, LM Studio,
  vLLM), native Anthropic Messages API, native Gemini generateContent, agent CLI
  (Claude Code / Codex / Copilot), and the bundled `scri-plugin-llm` subprocess.
- New `ImportanceScoringSink` event-sink fires asynchronously after every Upsert
  and Append, scoring the memory via Tier 2 and rewriting the sidecar's
  `Importance` field — never blocks the user-facing response, degrades silently
  when no Tier 2 LLM is configured. Registered alongside the embedding and
  maintenance sinks in every workspace-bootstrap path.
- `scri reindex --importance` backfills LLM-scored importance for any memory
  that doesn't already have one. Reports `scored / skipped / failed` counts;
  cancellable via Ctrl-C. Preserves prior scores so external/manual ratings
  aren't overwritten.
- New `scrinia-bench` CLI under `benchmarks/Scrinia.Retrieval.Benchmarks/` —
  retrieval-quality harness that runs Scrinia against academic benchmarks
  (ConvoMem, plus LongMemEval / LoCoMo / MemBench in upcoming commits) and
  reports R@5 / R@10 / R@50 / NDCG@10 with per-category breakdowns. JSON
  output schema mirrors MemPalace's per-question result files so per-question
  hits can be diffed across systems without a schema translator. First
  subcommand: `convomem` against the Salesforce evidence-question dataset.
  This is distinct from the existing perf microbenchmarks under
  `tests/Scrinia.Benchmarks/` (BenchmarkDotNet).
- `scrinia-bench longmemeval` — runs the headline LongMemEval benchmark
  (xiaowu0162/longmemeval-cleaned, 500 questions, per-question haystacks of
  ~53 sessions). Session/turn granularity, MemPalace's `lme_split_50_450.json`
  honoured via `--split full|dev|held_out`. Per-question-type breakdown
  (knowledge-update / multi-session / temporal-reasoning / single-session-{user,
  assistant, preference}). Apples-to-apples vs MemPalace's published 96.6% raw
  R@5 baseline.
- `scrinia-bench --mode bm25_embeddings` (default) — wires Scrinia's model2vec
  MiniLM-L6-v2 embedder into the harness alongside BM25. Embeds every haystack
  doc once at ingest time, caches the vector in memory, and at query time
  embeds the query and feeds cosine-sim supplemental scores into
  `WeightedFieldScorer.SearchAll` via the same `{scope}|{name}` contract the
  production `HybridReranker` uses. Falls back to BM25-only if the model
  isn't present.
- `scrinia-bench --mode bm25` — pure BM25 baseline for apples-to-apples vs
  MemPalace's `--mode raw` track without semantic search.
- `scrinia-bench membench` — runs MemBench (ACL 2025) across all 10 categories
  (8,500 items): simple, highlevel, knowledge_update, comparative, conditional,
  noisy, aggregative, highlevel_rec, lowlevel_rec, post_processing. Per-item
  haystack of 5–30 turns; ground truth matches against turn `sid`s. Apples-to-
  apples vs MemPalace's published 80.3% R@5 overall (43.4% on the designed-hard
  `noisy` category).
- `scrinia-bench locomo` — runs LoCoMo (10 conversations × ~200 QA pairs = 1986
  questions). Per-conversation haystack reuse via a new
  `BenchmarkRunner.RunGroupedAsync` path: each conversation's dialogs ingest
  once and serve every QA in that conversation before the retriever resets.
  Session or dialog granularity (`--granularity session|dialog`). Reports R@5,
  R@10, and R@50 side-by-side so the K=50 structural-guarantee question can be
  settled with data.
- New `BenchmarkQuestionGroup` core type for benchmarks where multiple
  questions share a haystack (LoCoMo's per-conversation shape). The runner has
  a matching `RunGroupedAsync` overload that ingests the group's shared
  haystack once and reuses it across every question in the group.
- `scrinia-bench` ablation knobs — `--alpha-recency`, `--tau-days`,
  `--alpha-importance`, `--diversity-lambda`, `--diversity-inner-limit`.
  Default to "off" so the headline run stays apples-to-apples; turning them on
  isolates the contribution of each Scrinia ranker feature (exp-decay recency,
  Generative-Agents-style importance, MMR diversity) against the benchmark
  ground truth. Output filenames pick up an ablation tag suffix
  (e.g. `__rec1.0_tau30_div0.6`) so sweep results don't collide.
- `benchmarks/Scrinia.Retrieval.Benchmarks/README.md` documents the harness
  methodology — metric definitions (R@K, NDCG@10), ranker composition formula,
  ground-truth matching modes (ID match for LongMemEval/LoCoMo/MemBench,
  substring match for ConvoMem), reproducibility caveats (model2vec
  determinism, MiniLM-architecture parity vs ChromaDB's sentence-transformer
  inference path), and per-question diff recipe vs MemPalace's published JSONL.
- Root README now points at the retrieval-quality harness alongside the
  existing token-cost benchmarks (which compare Scrinia vs flat-file vs
  auto-memory token budgets — distinct work, kept distinct).
- `scripts/prepare-datasets.{ps1,sh}` and `scripts/run-benchmarks.{ps1,sh}` —
  idempotent setup + run for the four retrieval benchmarks. Replaces the
  copy-pasteable curl/git/dotnet command blocks that were previously the
  canonical run path; the manual commands remain in the bench README as a
  fallback, but the scripts are the entry point now. Bash scripts are
  marked executable in the index so they work as-is on Linux/Mac.

### Changed
- Search ranker now uses an **additive composition** matching the Generative
  Agents (Park et al., 2023) score shape: `α_relevance·relevance +
  α_recency·exp(-Δt/τ)·scale + α_importance·(importance/10)·scale`. Replaces
  the previous multiplicative "relevance × (1 + linear recency boost)" formula
  whose maximum +10% nudge over 365 days was effectively noise on any real
  ranker decision. Default τ = 14 days produces a meaningful exponential decay
  (today=1.0, two-weeks=0.37, one-month=0.14) so fresh memories materially
  outrank stale ones on near-ties. Relevance still gates the composition —
  entries with no text match never surface regardless of recency / importance.
  Weights and time-constant are configurable via `Scrinia:Search:Alpha:Relevance`,
  `:Alpha:Recency`, `:Alpha:Importance`, `:TauDays`, `:NeutralImportance`,
  `:Scale:Recency`, `:Scale:Importance`. Setting α_recency = α_importance = 0
  reduces the ranker to pure relevance for users who want the prior behavior.
- `ArtifactEntry` gains an `Importance` field (nullable int, 1–10). Null means
  "not scored yet" — the ranker falls back to a neutral midpoint (5/10) so
  unscored memories rank as if "average importance" rather than getting
  penalised. The scoring path itself ships in a follow-up commit; this commit
  only adds the field, the sidecar round-trip, and the ranker read path.

### Fixed
- `scri hint` and `scri restore --hook` now emit the hook-output JSON envelope
  (`{"hookSpecificOutput":{"hookEventName":"...","additionalContext":"..."}}`)
  understood uniformly by Claude Code, Codex, and Copilot — replaces the bare
  stdout line that the agents were treating as ignorable status output.
  Payload is wrapped in semantic tags (`<scrinia-hint>` for the prompt-time
  hint, `<scrinia-restored-memory>` for the SessionStart dump) with imperative,
  second-person framing that reads as instruction rather than log line. On
  Claude Code the JSON shape arrives via the discreet `additionalContext`
  channel — context reaches the model without polluting the user's transcript
  on every prompt submit. SessionEnd output (`scri consolidate --auto`) is left
  un-wrapped: Claude Code routes it to debug-log only, Codex has no SessionEnd,
  Copilot ignores it. `scri hint --plain` retains the original log-line form
  for human inspection; `scri restore` defaults to YAML for direct invocation
  (only `--hook` switches to the envelope).
- Scrinia hooks now embed the **full absolute path** to the running `scri`
  executable (resolved via `Environment.ProcessPath`) rather than the bare
  `scri` name. Agent CLIs fire hooks in child shells whose `PATH` may not
  contain scrinia's install dir — especially when the CLI is launched from a
  desktop shortcut, a different shell profile, or a CI runner — and the hook
  would silently fail to find the executable. Install-time resolution writes
  the path once into each hook's command field; paths containing whitespace
  are shell-quoted. On Windows the path uses forward slashes — Claude Code
  and similar CLIs spawn hooks via git bash even when the interactive terminal
  is PowerShell, and bash interprets `\` as an escape. Forward slashes work
  uniformly in bash, PowerShell, and cmd. Re-run `scri setup --hooks` to
  update existing installs.
- `scri setup` now configures the Tier 2 LLM backend explicitly. Previously the
  CLI-based provider values (`claude-cli`, `codex-cli`, `copilot-cli`) and the
  native Anthropic/Gemini providers were reachable only via raw
  `scri config Scrinia:Llm:Provider <value>` — setup had no LLM step at all.
  New `--llm <value>` argument pins a backend non-interactively
  (`claude-cli`/`codex-cli`/`copilot-cli`/`openai`/`anthropic`/`gemini`/`plugin`/
  `none`/`auto`); when omitted, setup writes `auto` only if no provider is
  already configured. Secondary keys (API keys for anthropic/gemini, base URL +
  model for openai) are prompted only when missing from existing config.
- `scri setup` clears stale Ollama-derived config when the Ollama path falls
  through (probe failed, user declined, or `--no-ollama`). Previously, switching
  off Ollama left `Scrinia:Embeddings:Provider=ollama` + URL + model intact, so
  every subsequent startup tried Ollama, failed, and only then fell back.
  Custom OpenAI / Anthropic / Gemini configurations are preserved.
- `scri setup` now writes `Scrinia:Embeddings:Provider=model2vec` after the
  local model download step, so re-running setup after a previous configuration
  no longer leaves the value implicitly defaulted.

### Changed (breaking — CLI surface)
- Memory CRUD commands are now grouped under `scri memory <action>` — `scri list`,
  `scri search`, `scri store`, `scri show`, `scri forget`, `scri append`,
  `scri compact`, `scri link` become `scri memory list`, `scri memory search`,
  etc. The top-level `--help` is correspondingly shorter and easier to scan.
- Bundle file operations are grouped under `scri bundle <action>` — `scri export`
  and `scri import` become `scri bundle export` / `scri bundle import`; the old
  top-level `scri bundle` (pack-raw-files) is now `scri bundle pack`.
- `scri migrate` is hidden from top-level `--help` (still callable for one-shot
  v1→v2 store migration).
- `Scrinia:Llm:Provider` value renamed from `openai-compat` to `openai`; the old
  name is no longer accepted. Run `scri config Scrinia:Llm:Provider openai` if
  you have a workspace pinning the old value.

### Added — Agent integration (CLI backends, hooks, hint)
- **`scri hint <prompt>`** — pre-send relevance hint. Sub-millisecond BM25 lookup
  against the workspace store; emits a single-line marker
  (`[scrinia] N memories match: a, b, c…`) when results clear the score floor.
  Prompts shorter than `Scrinia:Hint:MinPromptChars` (default 8) and matches
  below `Scrinia:Hint:MinScore` (default 10.0) suppress the hint. Disable
  globally with `scri config Scrinia:Hint:Enabled false`. Reads prompt from
  positional arg or stdin; auto-detects JSON envelopes (`{prompt, …}`) used
  by some CLIs' hook protocols.
- **`scri setup --hooks`** — auto-install scrinia-managed SessionStart,
  SessionEnd, and UserPromptSubmit hooks across detected agent CLIs (Claude
  Code, OpenAI Codex, GitHub Copilot). SessionEnd fires once at session
  termination — NOT per assistant response — so consolidate runs once when the
  user wraps up, not on every turn. Codex CLI has no SessionEnd event, so the
  consolidate hook is skipped there with a notice (SessionStart and
  UserPromptSubmit still install). Prompts per CLI on PATH. User-global scope
  by default; `--project` writes workspace-local config that's committable for
  team sharing. `--uninstall-hooks` removes only scrinia-managed blocks,
  preserving user-authored hooks.
- **Per-CLI hook adapters** with format-appropriate writes:
  - Claude Code: `~/.claude/settings.json` merge with `_scriniaManaged: "v1"`
    sentinel for drift detection; user-authored hooks in the same event array
    survive every install/uninstall.
  - Codex 0.124+: `~/.codex/hooks.json` (loaded alongside `config.toml` —
    avoids round-tripping the user's TOML config).
  - GitHub Copilot CLI (GA Feb 2026): dedicated `scrinia.json` inside
    `~/.copilot/hooks/` (user) or `.github/hooks/` (project); event names
    canonical→camelCase (`SessionStart` → `sessionStart`, `SessionEnd` → `sessionEnd`).
  - Direct `scri` invocation in every hook — no intermediate shell scripts
    that can drift from their deployment locations (avoids the GSD #1834
    footgun).
- **Agent CLI as background LLM** — three new `Scrinia:Llm:Provider` values
  (`claude-cli`, `codex-cli`, `copilot-cli`) that shell out to the user's
  installed agent CLI in non-interactive print mode. Reuses the user's
  existing CLI authentication so Tier 2 doesn't need a separate API key or
  bundled model. Combined system+user prompt arrives on stdin to avoid
  Windows' 8K argument-length limit. Auto-mode (`Provider=auto`) tries HTTP
  → agent CLIs in preference order (claude → codex → copilot) → bundled
  plugin.
- **Native Anthropic + Gemini LLM providers** (`Provider=anthropic`,
  `Provider=gemini`) using each vendor's native API rather than the
  OpenAI-compat shim. Anthropic targets `/v1/messages` with the Messages API
  shape (system field, content blocks, x-api-key + anthropic-version
  headers) — better feature parity than the compat shim. Gemini targets
  `/v1beta/models/{model}:generateContent` with x-goog-api-key. Both inherit
  from a new `ResilientLlmProvider` base.
- **`IProcessRunner` abstraction** under `Scrinia.Core.Process` — testable
  one-shot process invocation with Windows PATHEXT resolution (`.cmd` shims
  for Node-based CLIs don't resolve through `Process.Start` with
  `UseShellExecute=false`).
- New config keys: `Scrinia:Llm:AnthropicApiKey`, `Scrinia:Llm:AnthropicBaseUrl`,
  `Scrinia:Llm:GeminiApiKey`, `Scrinia:Llm:GeminiBaseUrl`,
  `Scrinia:Hint:Enabled`, `Scrinia:Hint:MinScore`, `Scrinia:Hint:MinPromptChars`.

### Added — Tier 2 LLM consolidation
- `scri consolidate --with-llm` runs an LLM pass over the local store after the
  existing Tier 1 mechanical compaction: regenerates auto-fallback descriptions,
  one-paragraph summaries for compacted session entries, and extracts 3–7 atomic
  facts per memory (Mem0-style). Resumable via `.scrinia/.tier2-progress.json`
  keyed by qualified name + content hash — re-runs only touch memories whose
  content changed since the last pass.
- `ArtifactEntry.Facts: string[]?` field on the sidecar schema (v4). Each fact
  enters `TermFrequencies` at weight +2 so BM25 picks them up naturally;
  `WeightedFieldScorer.ScoreEntryTerm` also gives per-fact contains-match a
  small boost.
- `IBackgroundLlm` interface in `Scrinia.Core/Llm/` with two implementations:
  `OpenAiCompatibleBackgroundLlm` (HTTP via `HttpClient` against any
  OpenAI-compatible chat-completions endpoint — Ollama, llama.cpp server,
  LM Studio, Docker Model Runner) and `PluginBackgroundLlm` (MCP `complete`
  tool on the bundled `scri-plugin-llm`). Prompts live in `Scrinia.Core`
  (`LlmPrompts.cs`) so new Tier 2 tasks don't require a plugin rebuild.
- Bundled `Scrinia.Plugin.Llm` + `Scrinia.Plugin.Llm.Cli` ship LFM2.5-1.2B-Instruct
  on Vulkan via LLamaSharp 0.25. `publish.ps1 -WithLlm` packages it into
  `plugins/scri-plugin-llm/`. The plugin exposes a single `complete` MCP tool
  and a `status` tool; prompts stay on the host side.
- Provider preference for both LLM and embeddings auto-modes: existing HTTP
  backend (Ollama et al.) wins over the bundled Vulkan plugin. The principle is
  "if Ollama is already running, don't spin up another model in our own VRAM."
  Force a backend with `Scrinia:Llm:Provider = plugin` or `openai`.

### Added — Ollama auto-detection
- `scri setup` probes for a running Ollama instance via `GET /api/tags`. When
  reachable, prompts for an embedding model and a completion model (defaults:
  `nomic-embed-text`, `lfm2:1.2b` with `llama3.2:1b` fallback), pulls any
  missing models via streaming `POST /api/pull` with per-layer progress, and
  writes the resulting `Scrinia:Embeddings:*` + `Scrinia:Llm:*` config so
  subsequent `scri serve` / `scri consolidate --with-llm` "just work."
- Skip the Ollama prompt with `scri setup --no-ollama`.

### Added — Chunked embeddings + reindex
- `TextChunker.SliceWindows` slices every memory's decoded content into
  overlapping 1200/200-char windows. Each window becomes one vector keyed by
  `(scope, name, chunkIndex)`. Search-time dedup in
  `WeightedFieldScorer.SearchAll` collapses chunk matches back to one result
  per memory but lets the best-matching window drive the score. A needle
  buried mid-memory is now vector-reachable; the prior single-vector-per-memory
  layout silently lost it past whatever context window the provider supported.
- SVF3 signed vector file format: header carries the active embedding
  signature `{provider}|c{chunkSize}o{overlap}` (e.g.
  `ollama:nomic-embed-text|c1200o200`). On startup, vector files whose
  signature doesn't match the active config are quarantined as
  `vectors.bin.stale-{timestamp}` and `EmbeddingReindexer.ReindexIfStaleAsync`
  rebuilds them. Same flow triggers when the user changes
  `Scrinia:Embeddings:Provider`, `:OllamaModel`, `:ChunkSize`, or `:ChunkOverlap`.
- `scri reindex` command — forces a full vector rebuild by moving every
  `vectors.bin` to a `vectors.bin.pre-reindex-{timestamp}` backup, then letting
  the startup flow re-embed from sidecars.
- New config keys: `Scrinia:Embeddings:ChunkSize` (default 1200),
  `Scrinia:Embeddings:ChunkOverlap` (default 200),
  `Scrinia:Embeddings:MaxChunksPerMemory` (default 100, caps embed cost on
  pathologically long memories — BM25 still indexes the full text).
- `IEmbeddingProvider.EmbedBatchAsync(IReadOnlyList<string>)` default
  implementation (loops `EmbedAsync`) makes per-memory batch embedding work
  on every provider without HTTP-batch overrides; concrete providers can
  override for a perf win.

### Added — Search-path cache hardening
- `FileMemoryStore.DiscoverTopics` switched from a 2-second TTL to fully
  event-driven invalidation (already done in `Upsert` / `SaveIndex`). Repeated
  search calls between mutations now skip the `Directory.GetDirectories` rescan
  entirely.
- `FileMemoryStore.SearchAll` derives `TopicInfo[]` from the candidates already
  loaded by `ListScoped` instead of re-calling `LoadIndex` per topic scope inside
  `GatherTopicInfos`. The `_indexCache` was absorbing the disk reads but every
  duplicate call still acquired a shared file-lock and copied the entry list —
  measurable wear over long daemon sessions on sync-watched workspaces.

### Added — Resilience + observability
- `ResilientEmbeddingProvider` no longer trips its circuit breaker on HTTP 4xx
  responses. Bad payloads are per-input client errors, not provider-health
  signals — letting them cascade-fail every subsequent embed in a reindex was
  what produced the earlier 798/816 failure mode.
- `EmbeddingReindexer` classifies `FileNotFoundException` (sidecar without
  artifact) as Skipped rather than Failed and logs at Debug level.
- Robustness around Synology Drive interference in `LlmConsolidator` progress
  flushes: per-N-entry batched writes, atomic `.tmp` rename with [50,100,250]ms
  retry on `IOException`/`UnauthorizedAccessException`.

### Added
- `scri serve` auto-downloads the built-in embedding model on first run if it's
  missing — most users no longer need to run `scri setup` separately. Download
  progress goes to stderr to keep MCP stdio clean; if download fails, the server
  still starts with semantic search degraded to BM25-only. Opt out with
  `scri serve --no-auto-setup`.
- Six new CLI commands wrap the previously MCP-only handlers: `scri guide`,
  `scri append`, `scri compact`, `scri link`, `scri restore`, `scri reconcile`.
  Each wires directly to the same `ScriniaMcpTools` method the MCP server uses,
  so terminal users can evaluate every memory capability without an MCP client.
  `--json` returns the YAML response wrapped in a stable `CliMcpOutput` envelope;
  exit code is `1` on error responses.
- `Compact` MCP handler now returns a structured `NOT_FOUND` error instead of
  throwing `FileNotFoundException` when the target memory does not exist.
- Bounded NMP/2 decode: `Nmp2Strategy.MaxDecodedBytes` (default 64 MB) and `MaxChunkCount`
  (100k) reject memory-pressure DoS attempts during multi-chunk decompression.
- Structured error codes on every `memory()` failure path (`INVALID_PARAMETER`,
  `INVALID_ACTION`, `INVALID_PATH`, `NOT_FOUND`, `CONFLICT`, `INTERNAL`) plus concrete
  recovery hints in `actionNeeded[]`.
- Per-action parameter applicability table in the agent guide, plus elevated
  `memory('restore')` framing at the top of the tool description.
- `withBuiltin` parameter on skill recall (renamed from `reconcile` to disambiguate from
  the `reconcile` action).
- Truncation signal: responses whose content was capped at 8 KB now emit a `followUp`
  hint directing agents to fetch the next chunk.
- Soft-warning when writes target malformed reserved-prefix paths
  (`/Skills/`, `/pattern/`, `/agents/`, …) — the operation still succeeds but an
  `info[]` entry suggests the canonical spelling.
- `Directory.Build.props` centralises `<Version>` and `<AssemblyInformationalVersion>`
  across all projects; no more hardcoded version literals.
- `FileLock` contention observability via `ILogger` — `Warning` on first retry and
  `Error` on timeout with structured `{LockPath}` and `{ElapsedMs}` fields.
- `HnswIndex` upgraded to `ReaderWriterLockSlim` for concurrent search/insert workloads;
  exposes `IDisposable`.
- BM25 corpus-stats dictionary is pre-sized via a `docCountHint` parameter,
  removing rehash churn during search corpus computation.
- `IOptions<EmbeddingOptions>` and `IOptions<ChatOptions>` migration with
  `PostConfigure` for the historical Temperature/MaxTokens normalisation; server
  fails fast on missing required configuration.
- `ListMemories` REST endpoint accepts `offset`/`limit` query parameters
  (default 200, hard cap 1000) and returns the unpaginated total count.
- New documentation: `docs/plugin-authoring.md` (extension interfaces + worked
  example), `docs/security.md` (threat model + recommendations), embedding-provider
  selection section in `docs/architecture/embeddings.md`, `AgentChatPage` entry in
  `docs/web-ui-guide.md`, `scri migrate` reference in `docs/cli-reference.md`,
  chunked-embeddings + Tier 2 LLM coverage in `docs/cli-reference.md` and
  `docs/architecture/embeddings.md`.

### Changed
- Bundled-plugin default GGUF switched from `LFM2.5-1.2B-Thinking` to
  `LFM2.5-1.2B-Instruct`. The thinking variant burned the token budget on
  `<think>…</think>` reasoning blocks for Tier 2 tasks that want terse output;
  the instruct variant gives usable completions at the same parameter count.
  `VulkanLlmProvider` still detects and handles thinking models (heuristic via
  filename + GGUF metadata, `StripReasoningBlocks` regex, 8× max-tokens
  multiplier) for users who configure one explicitly.
- Bundled LLM plugin default context bumped from 4096 to 8192 tokens.
- Ollama probe uses `/api/tags` rather than `/` or `/v1/models` — works on every
  Ollama version including fresh installs with no models pulled, and surfaces a
  human-readable error string when unreachable.
- LLM HTTP probe timeout bumped 2s → 5s to match the Ollama-setup probe
  (Windows IPv6→IPv4 fallback latency on localhost was tripping the shorter
  budget intermittently).
- HybridReranker max-aggregates per-chunk vector scores under the
  `{scope}|{name}` key the whole-memory scorer pass looks up. The chunked
  `{scope}|{name}|{chunkIndex}` keys are still emitted alongside for any
  future per-chunk scoring path.

### Changed
- Trimmed CLI publish now declares `<IsTrimmable>true</IsTrimmable>` on `Scrinia` and
  `Scrinia.Core` and runs with `TreatWarningsAsErrors=true` + `SuppressTrimAnalysisWarnings=false`
  so reflection regressions cannot ship.
- Migrated 12 reflection-based `JsonSerializer` callsites to source-generated context
  overloads (`BundleFormatService`, `FileMemoryStore`, `ScriniaArtifactStore`,
  `ScriniaCommands`, `MemoryTools.Lifecycle`).
- `Nmp2Strategy.Encode` reuses an `ArrayPool<byte>` compression buffer and writes
  URL-safe Base64 directly via `System.Buffers.Text.Base64Url`, eliminating the
  three intermediate byte-array allocations and the `.Replace`/`TrimEnd` chain.
- Action aliases canonicalised: `remember` and `recall` are the canonical names;
  `store`/`show` remain accepted aliases for back-compat.

### Removed
- Ghost `sos-handler` skill reference from `docs/getting-started.md`; the skill set
  matches `src/Scrinia.Mcp/skills/` exactly.
- `docs/reviews/2026-04-29-llm-integration-architect-review.md` moved to
  `docs/reviews/archive/` with a resolution annotation.

## [0.5.0] — Initial public baseline

First release with the canonical two-tool MCP surface (`guide`, `memory`) after the
scope reduction that removed the goal/task/concern/plan vocabulary. The runtime is
considered stable from this point — future minor releases will not break the MCP
tool contract without a major-version bump.

[Unreleased]: https://github.com/nickd-scrinia/scrinia/compare/v0.5.0...HEAD
[0.5.0]: https://github.com/nickd-scrinia/scrinia/releases/tag/v0.5.0
