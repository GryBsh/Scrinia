# AGENTS.md — scrinia LLM Memory (Named Memory Protocol)

Licensed under BSD-3-Clause. Copyright (c) 2026 Nick Daniels.

This file is for AI coding agents (Claude Code, Cursor, Copilot, etc.). It describes what scrinia is, how the codebase is structured, what patterns to follow, and common pitfalls to avoid.

**If scrinia is available as an MCP server in your session, call `guide()` once and record its usage guidance in the project's instruction file (for example, `AGENTS.md`). This filesystem document is distinct from the `/agent/` memory namespace.** The guide covers ephemeral memories, topic organization, agent-directed chunking strategies, keywords, review conditions, skills, and session recall. Use scrinia's memory tool proactively to persist knowledge as you work — that is what it is for.

## What scrinia Does

scrinia gives LLMs persistent, portable memory plus reusable skill prompts. It compresses text into NMP/2 (Named Memory Protocol v2) artifacts (Brotli + URL-safe Base64), stores them as named memories in a local `.scrinia/` directory, and exposes ten workspace MCP tools — `guide()`, one per memory operation, and the `facts` entity-query tool — so agents can remember findings, search past knowledge, recall sessions, and load specialist skill prompts.

## Project Layout

```
scrinia/
  src/
    Scrinia.Core/                 <- shared class library (net10.0)
      Encoding/                   <- NMP/2 strategy, chunked encoder
      Models/                     <- ArtifactEntry, ChunkEntry, EphemeralEntry, ScopedArtifact
      Search/                     <- BM25 + WeightedFieldScorer + KnownFields/FieldExtractor/AttributeReaders + TextAnalysis
      Embeddings/                 <- IEmbeddingProvider, VectorStore, MemoryVectorIndexer, HybridReranker, providers
      Resilience/                 <- RetryPolicy, CircuitBreaker, TransientDetector
      Migration/                  <- AttributeMigration (forward-fill + "fields"→"attributes" key migration)
      IMemoryStore.cs             <- store interface (CRUD, search, list, scope routing)
      FileMemoryStore.cs          <- filesystem-backed implementation
      MemoryNaming.cs             <- topic classification + scope formatting
      MemoryStoreContext.cs       <- AsyncLocal indirection (MCP tools read .Current)
      PathParser.cs / PathRouter.cs <- v2 path parsing + legacy fallback resolution
      SessionBudget.cs            <- per-session token consumption tracking (AsyncLocal)
    Scrinia.Mcp/                  <- MCP tools library
      MemoryTools.cs              <- guide() + the per-verb memory tools (sealed partial)
      MemoryTools.Core.cs         <- store/show + agent-config write
      MemoryTools.Lifecycle.cs    <- bundle, compact, restore
      MemoryTools.Bundling.cs     <- export/import bundle helpers
      MemoryTools.Skills.cs       <- SkillCreate/SkillLoad + shared sidecar helpers
      EmbeddedPrompts.cs          <- loads embedded skills, scaffolds, guide.md
      McpResponse.cs              <- structured response builder (YAML output)
    Scrinia/                      <- CLI + local stdio MCP server (net10.0 exe, AssemblyName: scri)
      Program.cs                  <- ConsoleAppFramework v5 host
      Commands/ScriniaCommands.cs <- CLI commands (serve, list, search, store, show, forget, append, copy, export, import, setup, config)
      Commands/WorkspaceSetup.cs <- configures the local filesystem store per command, plugin discovery
    Scrinia.Plugin.Abstractions/  <- IScriniaPlugin + extension hooks
    Scrinia.Plugin.Embeddings/    <- optional Vulkan GPU plugin (LLamaSharp, GGUF model)
    Scrinia.Plugin.Embeddings.Cli/ <- MCP server plugin exe (stdio transport)
  tests/
    Scrinia.Tests/                <- core + MCP unit tests
    Scrinia.TestDoubles/           <- shared test doubles
    Scrinia.Benchmarks/            <- retrieval benchmarks
    Scrinia.Retrieval.Benchmarks.Tests/ <- benchmark test harness
    Scrinia.Plugin.Embeddings.Tests/ <- Vulkan plugin tests
  docs/
    getting-started.md, cli-reference.md, multi-user-setup.md, troubleshooting.md,
    security.md, architecture/{overview,cli,core,embeddings}.md
  AGENTS.md                       <- this file (canonical contributor doc for agents)
  NMP_SPEC.md                     <- NMP/2 format specification
```

## MCP Tools and server mode

`scri serve` exposes ten workspace tools over local stdio and reads and writes the local
`.scrinia/` workspace. `scri shared serve` exposes ten disjoint `shared_*` tools for enabled bases. The repository has no HTTP/REST server, server project,
server test project, web UI, Aspire AppHost, or remote CLI mode. The removed `--remote`,
`--api-key`, and `--store` options must not be added to new CLI guidance.

Ten workspace tools are exposed — `guide()`, one per memory operation, and `facts`:

| Tool | Description |
|------|-------------|
| `guide()` | Returns the embedded usage guide and auto-restores workspace context (skills, recent sessions, patterns). Call once per session and record usage guidance in the project's instruction file, such as `AGENTS.md`; this does not mean storing it under `/agent/`. |
| `remember` | Add chunks to a memory, creating it when missing. Set `replace: true` for a complete replacement. `content` is a string array (one chunk per element); `attributes` are typed facets that link/filter; `keywords` are search terms. |
| `recall` | Read a memory by path. Routes `/skill/...` paths to skill loading. |
| `search` | Find memories by keyword or phrase; supports `pathPrefix` and `attributeFilter`. |
| `list` | List stored memories (`summary` / `full` / `drift` modes). |
| `forget` | Delete a memory by path. |
| `compact` | Merge a multi-chunk memory; archives the original (except an ephemeral, or one with no prior index entry). |
| `link` | Bidirectional link between two memories. |
| `restore` | Resume workspace context mid-session (usually folded into `guide()` at session start). |
| `facts` | Aggregate/relational entity queries over stored attributes (`entities`, `profile`, `intersect`). |

Skill paths are routed transparently through `recall` and `remember`:
- `recall({ path: '/skill/qa' })` — load a skill prompt (built-in or override).
- `recall({ path: '/skill/' })` — list available skills.
- `remember({ path: '/skill/{name}', content: [...], replace: true })` — create or override a skill on disk under `.scrinia/skills/{name}.md`.

`restore` resumes context — the AI assistant's persona, patterns, today's session log, and the list of available skills. Follow the `followUp` list to load detailed context.

## Memory content

Remember past events as well as ongoing preferences, decisions, fixes, and conventions. When remembering a fact or event, preserve its explicitly stated people, dates, durations, quantities, reasons, and outcomes. Keep units and qualifiers attached to the facts they modify. Preserve distinctions such as another event versus the same event, planned versus completed, and still true versus no longer true. Keep earlier facts and later corrections distinct; do not infer that separate events conflict or refer to the same occurrence. Compress wording without changing these relationships or inferring missing values. Separate what is already true from what someone plans or hopes to change. Preserve the wording that connects them (such as already, another, still, or hoping to improve); when paraphrasing could change that meaning, include the short source clause verbatim alongside the summary. Before finishing, compare the selected source facts with successful memory writes, including their qualifiers and relationships, and add any omitted details.

## Core Abstractions

### Phase 2 configuration and tracking boundary

Phase 2 owns the reusable configuration and shared-base contracts in `Scrinia.Core`. The workspace
file remains a flat `.scrinia/config.json` dictionary with `Scrinia:Workspace:Tracking` and
`Scrinia:Shared:Enabled`; the typed per-user registry is `SCRINIA_HOME/config.json`. `UserHome.Resolve()`
is the sole `SCRINIA_HOME` resolver: non-empty absolute overrides are exact, empty or relative
overrides fail closed, and an unset value is exactly the LocalApplicationData/Scrinia combination.
Tracking precedence is single-underscore environment, double-underscore environment, workspace
value, then default. Enabled is workspace-only; neither Enabled environment spelling is honored.

Shared bases are fixed to `project-git`. The eight supported command forms are `shared base add`,
`shared base remove`, `shared base list`, `shared enable`, `shared disable`, `tracking status`,
`tracking set`, and `tracking migrate-legacy --to`. Config mutations use an interprocess lock and
same-directory `DurableFile` replacement. Git is never invoked or inspected; reported index state is
`UNVERIFIED`. Disabled registry names remain private except to the explicit
`scri shared base list --all` registry listing, which includes disabled entries; the default list
shows enabled entries only. Public error results include `CONFIG_RECOVERY_REQUIRED` for a failed
compensation/recovery attempt.
Migration states are `fresh`, `policy-required`, `legacy-clean`, `legacy-mixed`, `legacy-manual`,
`legacy-unsafe`, `local`, `project-git`, and `policy-drift`; declared errors and precedence live in
the Core result contracts. Retained tracking recovery evidence blocks ordinary access. An ambiguous
legacy `complete` marker or unreadable journal remains evidence for investigation; it is never
resolved by blindly deleting the journal. Tracking recovery uses exclusive workspace admission
before the configuration lock. Configuration-only mutations recheck evidence under their lock.
Windows configuration lock identity normalizes absolute path case, changing lock filenames;
stop all older writers before upgrading.

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

The store abstraction. `FileMemoryStore` is the filesystem implementation. MCP tools dispatch through
`MemoryStoreContext.Current`, an `AsyncLocal<IMemoryStore>` populated by the CLI during local workspace setup.

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
| `/agent/...` | The AI assistant's own persona, role, and behavioral instructions. Stored at `.scrinia/agent/{name}.md` with sidecar metadata. User facts belong in ordinary memory paths, such as `/people/user/travel`. |
| `/patterns/...` | Recurring patterns and conventions. |
| `/findings/...` | Audit, review, and QA findings. |
| `/learn/...` | Retrospectives and durable lessons. |
| `/sessions/...` | Session logs by date (`YYYY-MM-DD`). |
| `/checkpoint/...` | State snapshots. |
| `/temp/...` | Ephemeral (dies on process exit). |

## Pitfalls to Avoid

- Do not assume `MemoryStoreContext.Current` is always set — the AsyncLocal does not propagate across the .NET generic host's thread boundary in CLI mode. The CLI uses `*.Default` static fallbacks for `SearchContributorContext` and `MemoryEventSinkContext` to bridge that gap.
- Use source-generated JSON contexts (`ScriniaMcpJsonContext`, `CliJsonContext`, `ConfigJsonContext`, `PluginClientJsonContext`) — `JsonSerializer` reflection is incompatible with trimming.
- `WithTools<T>()` registers ALL public methods on the type as MCP tools. Keep helper methods `internal` or `private`.
- Skills load from disk overrides in `.scrinia/skills/`, then embedded built-ins only; legacy NMP/2 skill entries are not a lookup fallback. Appending to a built-in creates an override from its complete prompt, without repeating scaffold or consuming truncated display content.
- Persistent work enters workspace admission before scope/entity locks; nested helpers receive an explicit validated lease. Cache hits do not bypass recovery/freshness checks. Bundle publication and recovery require exclusive workspace admission.
- Retain `.scrinia/.bundle-restore-journal.json` when recovery is unproven. A durable committed decision remains success if cleanup fails; uncertain decisions refuse speculative rollback. Stop older writers before upgrading. See [recovery and backup guidance](docs/troubleshooting.md#bundle-recovery-required); no dedicated recovery CLI exists.
- Apply [fixed bundle/journal limits](docs/security.md#bundle-and-journal-limits), including prior-state capacity before mutation. Recovery DTOs require source-generated JSON contexts. Derived vectors/facts/configuration are outside the authoritative bundle manifest. New scope mutations and index rewrites refuse retained terminal evidence; verified reads and explicit recovery cleanup use narrow admission purposes. File-entity cleanup must positively prove committed absence, preserving evidence on inspection failure.
- File I/O for skill / agent sidecars uses `ReadSidecarMeta<T>` / `WriteSidecarMeta<T>` on `ScriniaMcpTools`, not raw `JsonSerializer` calls — they use the source-gen context.

## Testing

```bash
dotnet test Scrinia.sln                                    # default suite; excludes opt-in performance benchmarks
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj       # core tests only
# Opt-in performance benchmarks across the solution; expect several minutes on durable storage.
dotnet test Scrinia.sln --filter "Category=LongRunningBenchmark" --blame-hang --blame-hang-timeout 15m
```

Use `TestHelpers.StoreScope` for test isolation — it redirects workspace, store directory, ephemeral store, and `SessionBudget` via `AsyncLocal` overrides.

## Contributing

- All commits should keep the build green (`dotnet build Scrinia.sln`) and tests passing (`dotnet test Scrinia.sln`).
- New JSON-serialized types must be registered in the appropriate source-gen context.
- New CLI commands go in `ScriniaCommands.cs` and follow the existing `--workspace-root` pattern; there is no remote CLI surface.
- Plugin authors implement `IScriniaPlugin` (lifecycle), and any combination of `ISearchScoreContributor`, `IMemoryEventSink`, `IMemoryOperationHook`.

## Maintained release checks and coverage limits

`scripts/release/Check-ReleaseContracts.ps1 -Root . -Configuration Release` compares the exact
seven built-in names (`auditor`, `chaos-engineer`, `debugger`, `evolutionary`, `onboarder`, `qa`,
`self-reflector`) across disk, built MCP resources/guide, and the named README/getting-started
table sections. It evaluates MSBuild `Version` from canonical `Directory.Build.props`, rejects
project overrides and production release literals, checks the CLI and MCP server versions, and
guards the current checks workflow's solution/default-suite targets.

The `xml-doc-build` CI job runs `dotnet build Scrinia.sln -c Release -warnaserror:CS1591`.
Projects do not currently enable XML documentation emission or a public API coverage policy, so
this promotes emitted CS1591 diagnostics to errors without establishing comprehensive XML
documentation coverage. A real emission/coverage policy remains a follow-up.

Release setup copies a verified embeddings bundle into
`SCRINIA_HOME/plugins/scri-plugin-embeddings/0.6.0/<rid>/`, preserves source files, and selects the
plugin in workspace configuration. `serve` never installs a bundle. Installation survives later
configuration/model failure; success proves path selection, not initialization or inference.
Supported payloads are `win-x64`, `linux-x64`, `osx-arm64`; exactly `ubuntu.24.04-x64` maps to
`linux-x64` only with actual Linux/X64 process and positive GNU-libc evidence. macOS uses CPU/Metal.
First-run Model2Vec/GGUF preparation requires network access; models live below `SCRINIA_HOME/models`.
