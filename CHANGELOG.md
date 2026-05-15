# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Scrinia uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The single source of truth for the version number is `Directory.Build.props` at the
repository root — bumping it there propagates to the CLI banner, the MCP `ServerInfo`,
and all assembly attributes.

## [Unreleased]

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
