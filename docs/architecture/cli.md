# CLI Architecture

The Scrinia CLI (`scri`) is a .NET 10 executable that serves as both a command-line tool and an MCP server. It's published as a trimmed single-file binary.

## Project Layout

```
src/Scrinia/
  Scrinia.csproj            net10.0 exe, AssemblyName: scri
  Program.cs                Entry point (ConsoleAppFramework host)
  Commands/
    ScriniaCommands.cs      11 public methods (CLI commands)
    WorkspaceSetup.cs       Workspace init, store configuration (Store/LocalStore), plugin discovery
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
app.Add<MemoryCommands>("memory");
app.Add<BundleCommands>("bundle");
app.Run(args);
```

`app.Add<T>()` registers each public method on the type as a command. `app.Add<T>("prefix")` nests them under that prefix — `MemoryCommands.List` becomes `scri memory list`, `BundleCommands.Export` becomes `scri bundle export`. Methods on `ScriniaCommands` that should NOT be top-level commands (the memory CRUD operations, the bundle file operations) are kept `internal` so the source generator skips them while the forwarder classes in `MemoryCommands` / `BundleCommands` call into them across the assembly.

### Bundle commands: topic filters differ deliberately between CLI and HTTP

`scri bundle import --topics a,b` comma-splits one string. The HTTP endpoint binds
`[FromQuery] string[] topics` and reads repeated keys — `?topics=a&topics=b`. Each follows its own
surface's convention: comma-separated lists are idiomatic for a CLI flag, repeated keys are
idiomatic for a query string and are what ASP.NET Core's model binder produces.

This is not an inconsistency to unify. Unifying requires first deciding which surface is canonical,
and neither has a claim over the other. Documented and pinned 2026-08-10.

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
4. `.scrinia/store/` and `.scrinia/topics/` are created lazily, only when something is
   written -- not during this resolution step
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

Once configured, code reads the store back through two static properties: `WorkspaceSetup.Store` (the `IMemoryStore` interface, backed by `MemoryStoreContext.Current`) and `WorkspaceSetup.LocalStore` (narrowed to `ILocalMemoryStore`, for commands that read or write workspace files directly — it throws if the process is in remote mode, i.e. `scri serve --remote`). Both CLI commands and MCP tool handlers reach the store through `MemoryStoreContext.Current`; `WorkspaceSetup` is what populates it.

## MCP Server Mode

When `scri serve` is invoked, the CLI starts an MCP server over stdio transport:

```csharp
builder.Services
    .AddMcpServer(mcp => mcp.ServerInfo = new() { Name = "scrinia", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithTools<ScriniaMcpTools>();
```

The MCP tools are exposed by `ScriniaMcpTools`, a sealed partial class registered via `WithTools<T>()`. They access the store via `MemoryStoreContext.Current`, which is set during workspace initialization.

Each operation is its own tool with its own schema: `guide`, `remember`, `recall`, `search`, `list`, `append`, `forget`, `compact`, `link`, `restore`, `reconcile`, and `facts`. Skill-path routing (`/skill/...`) handles skill create/load inside `remember`/`recall`.

### Remote Mode

When `--remote` is specified, the CLI creates a remote store proxy instead of a local `FileMemoryStore`. All MCP tool calls are forwarded to the Scrinium HTTP API.

## Embeddings Integration

### Two-Step Initialization

`WorkspaceSetup.LoadPluginsAsync()` performs two-step embeddings initialization:

**Step 1: Built-in embeddings (in-process, always available):**
- Create `EmbeddingOptions` from workspace config
- `EmbeddingProviderFactory.Create(options, modelsDir, logger)` → `IEmbeddingProvider`
- Create `VectorStore(embeddingsDir)` + `HybridReranker(provider, store, weight)`
- Create `CoreEmbeddingEventHandler(provider, store, logger)` (in-process event sink)
- Set `SearchContributorContext.Default` + `MemoryEventSinkContext.Default`

**Step 2: Optional Vulkan plugin (child-process, overrides built-in):**
- Discover `{exeDir}/plugins/scri-plugin-embeddings[.exe]`
- If found: launch via `McpPluginHost`, override context defaults
- If not found or fails: built-in remains active

This means semantic search works out of the box with zero plugins installed.

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

1. `WorkspaceSetup.LoadPluginsAsync()` scans `{exeDir}/plugins/scri-plugin-*`
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

| Context | Purpose | CLI | Server |
|---------|---------|-----|--------|
| `MemoryStoreContext` | Routes to active `IMemoryStore` | Set once at startup | Set per-request |
| `SearchContributorContext` | Semantic search scoring plugin | `.Default` (static) | `.Current` (per-request) |
| `MemoryEventSinkContext` | Store/append/forget event hooks | `.Default` (static) | `.Current` (per-request) |

**Why Default exists:** `AsyncLocal<T>` does NOT propagate from the CLI's startup code through the .NET generic host to MCP tool handler threads. The generic host creates new threads with empty `AsyncLocal` values. The `.Default` static fallback solves this:

```csharp
// CLI sets Default (global static) — survives thread boundary
SearchContributorContext.Default = pluginHost;
MemoryEventSinkContext.Default = pluginHost;

// MCP tool handlers read Default when Current is null
var contributor = SearchContributorContext.Current ?? SearchContributorContext.Default;
```

The server does NOT use `.Default` — it sets `.Current` per-request in middleware, which propagates correctly within a single request's async chain. See [Server Architecture](server.md#asynclocal-context-pattern) for the server-side pattern.

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
- All JSON serialization uses source-gen contexts (`StoreJsonContext`, `BundleJsonContext`, `FileStoreJsonContext`, `ConfigJsonContext`, `PluginClientJsonContext`, `CliJsonContext`, `ScriniaMcpJsonContext`)
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
