# AGENTS.md — scrinia LLM Memory (Named Memory Protocol)

Licensed under BSD-3-Clause. Copyright (c) 2026 Nick Daniels.

This file is for AI coding agents (Claude Code, Cursor, Copilot, etc.). It describes what scrinia is, how the codebase is structured, what patterns to follow, and common pitfalls to avoid.

**If scrinia is available as an MCP server in your session, call `guide()` once and commit its content to your project's agent file.** The guide covers ephemeral memories, topic organization, agent-directed chunking strategies, keywords, review conditions, skills, and session recall. Use scrinia's memory tool proactively to persist knowledge as you work — that is what it is for.

## What scrinia Does

scrinia gives LLMs persistent, portable memory plus reusable skill prompts. It compresses text into NMP/2 (Named Memory Protocol v2) artifacts (Brotli + URL-safe Base64), stores them as named memories in a local `.scrinia/` directory, and exposes two MCP tools (`guide`, `memory`) so agents can remember findings, search past knowledge, recall sessions, and load specialist skill prompts.

## Project Layout

```
scrinia/
  src/
    Scrinia.Core/                 <- shared class library (net10.0)
      Encoding/                   <- NMP/2 strategy, chunked encoder
      Models/                     <- ArtifactEntry, ChunkEntry, EphemeralEntry, ScopedArtifact
      Search/                     <- BM25 + WeightedFieldScorer + TextAnalysis + ReferenceExtractor
      Embeddings/                 <- IEmbeddingProvider, VectorStore, HnswIndex, HybridReranker, providers
      Resilience/                 <- RetryPolicy, CircuitBreaker, TransientDetector
      IMemoryStore.cs             <- store interface (CRUD, search, list, scope routing)
      FileMemoryStore.cs          <- filesystem-backed implementation
      MemoryNaming.cs             <- topic classification + scope formatting
      MemoryStoreContext.cs       <- AsyncLocal indirection (MCP tools read .Current)
      PathParser.cs / PathRouter.cs <- v2 path parsing + legacy fallback resolution
      SessionBudget.cs            <- per-session token consumption tracking (AsyncLocal)
    Scrinia.Mcp/                  <- MCP tools library
      MemoryTools.cs              <- guide() + memory() entry points (sealed partial)
      MemoryTools.Core.cs         <- store/show + agent-config write
      MemoryTools.Lifecycle.cs    <- bundle, reconcile, compact, restore
      MemoryTools.Bundling.cs     <- export/import bundle helpers
      MemoryTools.Skills.cs       <- SkillCreate/SkillLoad + shared sidecar helpers
      EmbeddedPrompts.cs          <- loads embedded skills, scaffolds, guide.md
      McpResponse.cs              <- structured response builder (YAML output)
    Scrinia/                      <- CLI + MCP server (net10.0 exe, AssemblyName: scri)
      Program.cs                  <- ConsoleAppFramework v5 host
      Commands/ScriniaCommands.cs <- CLI commands (serve, list, search, store, show, forget, append, copy, export, import, setup, config)
      Mcp/ScriniaArtifactStore.cs <- CLI store wrapper (static, delegates to FileMemoryStore)
    Scrinia.Server/               <- HTTP API server (net10.0 web, refs Core + Mcp)
      Endpoints/MemoryEndpoints.cs <- REST: store, list, show, append, search, export, import
      Endpoints/HealthEndpoints.cs <- /health, /health/live, /health/ready, /health/details
      Endpoints/KeyEndpoints.cs    <- API key management
      Endpoints/ChatEndpoints.cs   <- general-purpose LLM chat (OpenAI/Anthropic/Gemini providers)
      Auth/                        <- ApiKeyStore (SQLite), bearer-token auth handler, RequestContext
      Services/StoreManager.cs     <- multi-store factory keyed by name
    Scrinia.Plugin.Abstractions/  <- IScriniaPlugin + extension hooks
    Scrinia.Plugin.Embeddings/    <- optional Vulkan GPU plugin (LLamaSharp, GGUF model)
    Scrinia.Plugin.Embeddings.Cli/ <- MCP server plugin exe (stdio transport)
    Scrinia.Merge/                <- merge driver for .scrinia .meta.json conflicts
    Scrinia.AppHost/              <- .NET Aspire orchestrator
  tests/
    Scrinia.Tests/                <- core + MCP unit tests
    Scrinia.Server.Tests/         <- WebApplicationFactory tests (memory, auth, health, chat)
    Scrinia.Plugin.Embeddings.Tests/ <- Vulkan plugin tests
    Scrinia.Merge.Tests/          <- merge handler tests
  web/                            <- React + Vite + Tailwind SPA
    src/pages/                    <- Login, Dashboard, MemoryBrowser, MemoryDetail, KeyManagement, AgentChat, Settings
    src/components/               <- Layout, MemoryList, MemoryContent, ChunkViewer, SearchBar
  docs/
    getting-started.md, cli-reference.md, server-admin.md, multi-user-setup.md,
    troubleshooting.md, web-ui-guide.md, architecture/{overview,cli,server,core,embeddings}.md
  AGENTS.md                       <- this file (canonical contributor doc for agents)
  NMP_SPEC.md                     <- NMP/2 format specification
```

## MCP Tools

Two tools are exposed:

| Tool | Description |
|------|-------------|
| `guide()` | Returns the embedded usage guide. Call once per session and commit its content to your project's agent file. |
| `memory(action, ...)` | Unified dispatcher: `remember`/`store`, `recall`/`show`, `forget`, `search`, `list`, `append`, `compact`, `link`, `restore`, `reconcile`. |

Skill paths are routed through `memory()`:
- `memory('recall', { path: '/skill/qa' })` — load a skill prompt (built-in or override).
- `memory('recall', { path: '/skill/' })` — list available skills.
- `memory('remember', { path: '/skill/{name}', content: [...] })` — create or override a skill on disk under `.scrinia/skills/{name}.md`.

`memory('restore')` resumes context — agent profile, patterns, today's session log, and the list of available skills. Follow the `followUp` list to load detailed context.

## Core Abstractions

### `IEncodingStrategy`

Only one implementation exists: `Nmp2Strategy`. Namespace: `Scrinia.Core.Encoding`.

```csharp
public interface IEncodingStrategy
{
    string StrategyId { get; }           // "nmp/2"
    EncodingResult Encode(ReadOnlySpan<byte> input, EncodingOptions options);
    byte[] Decode(string artifact);
    bool CanDecode(string artifact);
    ArtifactMetadata ParseHeader(string artifact);
}
```

### `IMemoryStore`

The store abstraction. `FileMemoryStore` is the filesystem implementation. MCP tools dispatch through `MemoryStoreContext.Current`, an `AsyncLocal<IMemoryStore>` that the CLI (set once at startup) and server (set per-request in middleware) both populate.

### `MemoryNaming`

Pure static utilities for topic classification:
- `EntityTopics` contains a single entry: `skill`. This routes legacy NMP/2 skill data under `local-topic:entity/skill/`.
- `AgentTopics` contains `agent`, routed to `local-topic:agent`.
- Everything else routes to `local-topic:memory/{topic}`.

## Reserved Paths

Memory paths are free-form. A small set has first-class behavior:

| Path | Purpose |
|------|---------|
| `/skill/...` | Reusable specialist prompts. Stored at `.scrinia/skills/{name}.md` with sidecar metadata. Built-in skills are embedded in the binary; user overrides on disk take precedence. |
| `/agent/...` | Agent profile and behavioral norms. Stored at `.scrinia/agent/{name}.md` with sidecar metadata. |
| `/patterns/...` | Recurring patterns and conventions. |
| `/sessions/...` | Session logs by date (`YYYY-MM-DD`). |
| `/checkpoint/...` | State snapshots. |
| `/temp/...` | Ephemeral (dies on process exit). |

## Pitfalls to Avoid

- Do not assume `MemoryStoreContext.Current` is always set — the AsyncLocal does not propagate across the .NET generic host's thread boundary in CLI mode. The CLI uses `*.Default` static fallbacks for `SearchContributorContext` and `MemoryEventSinkContext` to bridge that gap.
- Use source-generated JSON contexts (`ScriniaMcpJsonContext`, `ServerJsonContext`, `CliJsonContext`, `ConfigJsonContext`, `PluginClientJsonContext`) — `JsonSerializer` reflection is incompatible with trimming.
- `WithTools<T>()` registers ALL public methods on the type as MCP tools. Keep helper methods `internal` or `private`.
- Skills write to `.scrinia/skills/` first, fall back to NMP/2 `skill:{name}` legacy entries, then to embedded built-ins. Don't bypass this lookup chain.
- File I/O for skill / agent sidecars uses `ReadSidecarMeta<T>` / `WriteSidecarMeta<T>` on `ScriniaMcpTools`, not raw `JsonSerializer` calls — they use the source-gen context.

## Testing

```bash
dotnet test Scrinia.sln                                    # full suite
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj       # core tests only
```

Use `TestHelpers.StoreScope` for test isolation — it redirects workspace, store directory, ephemeral store, and `SessionBudget` via `AsyncLocal` overrides.

## Contributing

- All commits should keep the build green (`dotnet build Scrinia.sln`) and tests passing (`dotnet test Scrinia.sln`).
- New JSON-serialized types must be registered in the appropriate source-gen context.
- New CLI commands go in `ScriniaCommands.cs` and inherit `--workspace-root` / `--remote` / `--api-key` patterns.
- Plugin authors implement `IScriniaPlugin` (lifecycle), and any combination of `ISearchScoreContributor`, `IMemoryEventSink`, `IMemoryOperationHook`.

## Docs maintenance — known gaps

These are intentional follow-ups, not bugs in any one commit. Track them when touching the relevant area.

- **No CI check enforces skill/doc parity.** The skill table in `docs/getting-started.md` is manually edited; if a skill is added to or removed from `src/Scrinia.Mcp/skills/` without updating the doc, the drift is invisible until the next manual review. A small CI step that diffs `Directory.GetFiles("src/Scrinia.Mcp/skills", "*.md")` against the rows in the skills table would close this gap (and would also catch the dual breakage where a skill body still references a removed MCP surface).
- **No CI check enforces XML-doc coverage on the public API.** Public `IMemoryStore` / `MemoryTools` members are well-documented today; the next regression isn't caught until somebody notices. DocFX-with-coverage-threshold or a custom roslyn analyzer is the standard fix.
- **No tooling enforces version-string single-sourcing.** `Directory.Build.props` is the canonical `<Version>`; a grep over `src/**/*.cs` and `src/**/*.csproj` for the literal version is a one-line CI guard against drift returning.
