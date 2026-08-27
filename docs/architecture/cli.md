# CLI Architecture

The Scrinia CLI (`scri`) is a .NET 10 executable that serves as both a command-line tool and an MCP server. It's published as a trimmed single-file binary.

## Project Layout

```
src/Scrinia/
  Scrinia.csproj            net10.0 exe, AssemblyName: scri
  Program.cs                Entry point (ConsoleAppFramework host)
  Commands/
    ScriniaCommands.cs      Partial CLI command class (split across ScriniaCommands.*.cs)
    BundleCommands.cs        Public bundle command group; delegates to ScriniaCommands
    WorkspaceSetup.cs       Workspace init, local store configuration, plugin discovery
    WorkspaceConfig.cs      .scrinia/config.json management
    ConfigJsonContext.cs     Source-gen JSON for config
  Services/
    McpPluginHost.cs        MCP client for child-process plugins
    PluginClientJsonContext.cs  Source-gen JSON for plugin comms
```

## ConsoleAppFramework v5

The CLI uses ConsoleAppFramework v5, a source-generated CLI framework with zero reflection. This is critical for trimming compatibility.

```csharp
// Program.cs
var app = ConsoleApp.Create();
app.Add<ScriniaCommands>();
app.Add<BundleCommands>("bundle");
app.Run(args);
```

`app.Add<T>()` registers each public method on the type as a top-level command. `app.Add<T>("prefix")` nests commands under that prefix — `ScriniaCommands.Store` becomes `scri store`, while `BundleCommands.Export` becomes `scri bundle export`. The `ScriniaCommands` partials keep command implementations organized by area; helper methods that should not be commands are `internal` or `private`, so the source generator skips them. `BundleCommands` provides the public `bundle` command group and forwards its operations to the internal implementations on `ScriniaCommands`.

### Pitfalls

- `[Argument]` attribute requires `using ConsoleAppFramework;`
- XML doc comment aliases must not duplicate auto-generated option names
- Source generation happens at compile time; adding new commands requires rebuilding

## Workspace Discovery

`WorkspaceSetup` handles workspace initialization:

1. Walk up from `cwd` looking for the nearest enclosing git repository (a `.git` entry,
   which may be a directory or, in worktrees and submodules, a file)
2. If found, that repository's root is the workspace root
3. If no enclosing repository is found, `cwd` is the workspace root and no upward search
   happens
4. `.scrinia/memories/` and any topic subdirectories beneath it are created lazily by the first
   write, not during workspace-root resolution
5. Discover and launch plugins

This mirrors `git`'s own `.git/` discovery, bounded the same way `git` bounds it: at the
first repository found, not the first `.scrinia/` found.

## WorkspaceSetup (CLI Store Access)

The CLI uses `WorkspaceSetup`, a static class that configures the active store for the process. `WorkspaceSetup.Configure(workspaceRoot)` runs at the top of every CLI command; it resolves the workspace root, builds ranker weights from config, and sets `MemoryStoreContext.Current` to a new `FileMemoryStore`. Commands that also need embeddings/search plugins additionally call `WorkspaceSetup.LoadPluginsAsync()` right after.

```
ScriniaCommands.<Command>
      |
WorkspaceSetup.Configure(workspaceRoot)
      |
      +-- Resolves workspace root (WorkspaceRoot.Resolve)
      +-- Creates FileMemoryStore(root, rankerOptions)
      +-- Sets MemoryStoreContext.Current
      |
WorkspaceSetup.LoadPluginsAsync()  (commands that need search/embeddings)
      |
      +-- Wires embeddings provider, plugin host
```

Once configured, code reads the local filesystem store back through `WorkspaceSetup.Store`, backed by
`MemoryStoreContext.Current`. Both CLI commands and MCP tool handlers reach that store through the
context populated by `WorkspaceSetup`; there is no remote store or remote CLI mode.

## MCP Server Mode

When `scri serve` is invoked, the CLI starts an MCP server over stdio transport:

```csharp
builder.Services
    .AddMcpServer(options => options.ServerInstructions = ScriniaPrimer.McpInstructions)
    .WithStdioServerTransport()
    .WithTools<ScriniaMcpTools>(ScriniaMcpTools.CreateToolSerializerOptions())
    .WithPrompts<ScriniaMcpPrompts>()
    .WithResources<ScriniaMcpResources>();
```

The MCP server uses stdio transport and exposes tools, prompts, and resources. The tools are
provided by `ScriniaMcpTools`, a sealed partial class registered with source-generated serializer
options for trimmed publishing. They access the store via `MemoryStoreContext.Current`, which is
set during workspace initialization. Server instructions are supplied through
`ScriniaPrimer.McpInstructions`; the registration does not assert a separate server version.

Each operation is its own tool with its own schema: `guide`, `remember`, `recall`, `search`, `list`, `append`, `forget`, `compact`, `link`, `restore`, `reconcile`, and `facts`. Skill-path routing (`/skill/...`) handles skill create/load inside `remember`/`recall`.

## Embeddings Integration

### Provider Selection

`WorkspaceSetup.LoadPluginsAsync()` selects the embeddings path in this order:

An explicitly configured HTTP provider (`ollama`, `openai`, `voyageai`, `azure`, or `google`)
takes precedence over both the Vulkan plugin and built-in Model2Vec. In that case, the plugin and
Model2Vec initialization paths are skipped.

**Built-in embeddings (in-process fallback):**
- Create `EmbeddingOptions` from workspace config
- `EmbeddingProviderFactory.Create(options, modelsDir, logger)` → `IEmbeddingProvider`
- Create `VectorStore(embeddingsDir)` + `HybridReranker(provider, store, weight)`
- Create `CoreEmbeddingEventHandler(provider, store, logger)` (in-process event sink)
- Set `SearchContributorContext.Default` + `MemoryEventSinkContext.Default`

**Optional Vulkan plugin (child process, preferred when installed):**
- Resolve the configured plugin name from `plugins:embeddings` (default:
  `scri-plugin-embeddings`)
- Check `{exeDir}/plugins/{name}/{name}[.exe]` first, then
  `{exeDir}/plugins/{name}[.exe]`
- If found: launch via `McpPluginHost`; the plugin takes ownership of the embedding path
  and built-in Model2Vec is skipped
- If no plugin is found: initialize the in-process provider, normally Model2Vec
- If the installed plugin fails to start or becomes unavailable: do not initialize Model2Vec;
  search remains available through BM25 and path scoring, but semantic plugin scoring is absent

This means semantic search works out of the box with zero plugins installed when the built-in
Model2Vec model files are available at `{exeDir}/models/m2v-MiniLM-L6-v2/`. An installed plugin is
an explicit preference; if plugin startup fails, Scrinia does not silently fall back to Model2Vec.

### CoreEmbeddingEventHandler

In-process `IMemoryEventSink` that handles embed-and-index:

- **On store:** Embeds full content + per-chunk vectors, upserts to VectorStore
- **On append:** Embeds the new chunk, upserts to VectorStore
- **On forget:** Removes all vectors for the memory from VectorStore

## Plugin Host (Optional Vulkan)

### Architecture

The optional Vulkan GPU plugin runs as a separate executable communicating via MCP over stdio:

```
scri (MCP client) <--stdio--> scri-plugin-embeddings (MCP server)
```

This architecture is necessary because the trimmed single-file CLI host is incompatible with dynamic assembly loading (CoreLib type forwarder conflicts).

### McpPluginHost

`McpPluginHost` manages the plugin lifecycle:

```csharp
internal sealed class McpPluginHost : ISearchScoreContributor, IMemoryEventSink, IAsyncDisposable
```

It implements both extension interfaces so the CLI can use a single object for all plugin interactions.

### Startup Sequence

1. `WorkspaceSetup.LoadPluginsAsync()` resolves the configured plugin name from
   `plugins:embeddings` (default: `scri-plugin-embeddings`) and checks
   `{exeDir}/plugins/{name}/{name}[.exe]` first, then `{exeDir}/plugins/{name}[.exe]`
2. For each discovered plugin executable:
   - Launch as child process via `StdioClientTransport`
   - Pass `--data-dir`, `--models-dir`, and `--config KEY=VALUE` arguments
   - Call `ListToolsAsync()` to detect capabilities
3. Wire up plugin based on detected tools:
   - `search` tool present: register as `ISearchScoreContributor`
   - `upsert`/`remove` tools present: register as `IMemoryEventSink`
   - `status` tool present: health check capability

### Capability Detection

The plugin host discovers capabilities dynamically:

```csharp
var tools = await _client.ListToolsAsync(ct);
_hasSearch = tools.Any(t => t.Name == "search");
_hasUpsert = tools.Any(t => t.Name == "upsert");
_hasRemove = tools.Any(t => t.Name == "remove");
_hasStatus = tools.Any(t => t.Name == "status");
```

### Fault Tolerance

- Auto-reconnect up to 3 times on plugin crash
- After 3 failures, set `_degraded = true` and fall back to BM25-only search
- All plugin calls wrapped in try/catch returning null/no-op on failure
- Plugin failure never breaks core memory operations

### Config Passthrough

Workspace configuration is forwarded to plugins as CLI arguments:

```
scri-plugin-embeddings --data-dir /path/.scrinia --models-dir /path/plugins/scri-plugin-embeddings --config Scrinia:Embeddings:Provider=vulkan
```

## Configuration Resolution

`WorkspaceConfig` manages `.scrinia/config.json`:

```csharp
public static string? Get(string key, string workspaceRoot)
{
    // 1. Environment variable (KEY with : → _, uppercased)
    // 2. Config file value
    // 3. null (caller uses default)
}
```

The config file is a flat `Dictionary<string, string>` with case-insensitive keys.

## AsyncLocal Context Pattern

Three `AsyncLocal`-based context types provide indirection between MCP tool handlers and their backing services:

| Context | Purpose | How the CLI sets it |
|---------|---------|---------------------|
| `MemoryStoreContext` | Routes to active `IMemoryStore` | Set once at startup |
| `SearchContributorContext` | Semantic search scoring plugin | `.Default` (static) |
| `MemoryEventSinkContext` | Store/append/forget event hooks | `.Default` (static) |

**Why Default exists:** `AsyncLocal<T>` does NOT propagate from the CLI's startup code through the .NET generic host to MCP tool handler threads. The generic host creates new threads with empty `AsyncLocal` values. The `.Default` static fallback solves this:

```csharp
// CLI sets Default (global static) — survives thread boundary
SearchContributorContext.Default = pluginHost;
MemoryEventSinkContext.Default = pluginHost;

// MCP tool handlers read Default when Current is null
var contributor = SearchContributorContext.Current ?? SearchContributorContext.Default;
```

The `.Current` half is available to embedders and tests that need one async flow isolated from
another; `AsyncLocal` propagates correctly within that flow.

## Output Rendering

The CLI uses Spectre.Console for rich terminal output:
- Tables with colors for `list` and `search` results (bypassed when `--json` is used)
- Progress bars for `setup` (model download)
- Formatted memory content for `show`
- Markup for status messages

## Build and Publish

```powershell
# Development build
dotnet build src/Scrinia/Scrinia.csproj

# Trimmed single-file release
dotnet publish src/Scrinia/Scrinia.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true
```

### Trimming Safety

The CLI is safe for trimming because:
- All JSON serialization uses source-gen contexts (`BundleJsonContext`, `FileStoreJsonContext`, `ConfigJsonContext`, `PluginClientJsonContext`, `CliJsonContext`, `ScriniaMcpJsonContext`)
- ConsoleAppFramework v5 is source-generated (no reflection)
- No dynamic assembly loading (plugins are child processes)
- `InternalsVisibleTo` for test access only

## JSON CLI Output

All CLI commands support a `--json` flag for machine-readable JSON output. A source-generated `CliJsonContext` ensures trimming safety. When `--json` is passed, Spectre.Console rendering is bypassed and structured JSON is written to stdout instead.

## Test Coverage

`Scrinia.Tests` covers:
- The MCP tools (`guide`, the per-verb memory tools, and `facts`)
- Store operations and edge cases
- Search ranking and scoring
- Encoding and chunking
- Bundle export/import
- Workspace discovery
- Configuration resolution
- Embeddings (VectorStore, VectorIndex, MemoryVectorIndexer, HybridReranker, BertTokenizer, SafeTensorsReader, Model2Vec, API providers)

Test isolation uses `TestHelpers.StoreScope` which redirects workspace, store directory, ephemeral store, and `SessionBudget` via `AsyncLocal` overrides.
