# CLI Architecture

The Scrinia CLI (`scri`) is a .NET 10 executable that serves as both a command-line tool and an MCP server. It's published as a trimmed single-file binary.

## Project Layout

```
src/Scrinia/
  Scrinia.csproj            net10.0 exe, AssemblyName: scri
  Program.cs                Entry point (ConsoleAppFramework host)
  Commands/
    ScriniaCommands.cs      11 public methods (CLI commands)
    WorkspaceSetup.cs       Workspace init + plugin discovery
    WorkspaceConfig.cs      .scrinia/config.json management
    ConfigJsonContext.cs     Source-gen JSON for config
  Mcp/
    ScriniaArtifactStore.cs CLI-specific static store implementation
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
app.Run(args);
```

`app.Add<T>()` (no arguments) registers all public methods on `ScriniaCommands` as root subcommands. Method names become command names (e.g., `Store` becomes `scri store`).

### Pitfalls

- `[Argument]` attribute requires `using ConsoleAppFramework;`
- XML doc comment aliases must not duplicate auto-generated option names
- Source generation happens at compile time; adding new commands requires rebuilding

## Workspace Discovery

`WorkspaceSetup` handles workspace initialization:

1. Walk up the directory tree from `cwd` looking for `.scrinia/`
2. If found, use that directory as the workspace root
3. If not found, use `cwd` as the workspace root
4. Create `.scrinia/store/` and `.scrinia/topics/` if they don't exist
5. Discover and launch plugins

This mirrors `git`'s `.git/` discovery pattern.

## ScriniaArtifactStore (CLI Store)

The CLI uses `ScriniaArtifactStore`, a static class that wraps a `FileMemoryStore` instance. It's set up during workspace initialization and accessed statically from both CLI commands and MCP tool handlers.

```
ScriniaCommands
      |
WorkspaceSetup.InitAsync()
      |
      +-- Creates FileMemoryStore(workspaceRoot)
      +-- Sets MemoryStoreContext.Current
      +-- Sets ScriniaArtifactStore static fields
      +-- Calls LoadPluginsAsync()
```

`ScriniaArtifactStore` provides compatibility shims and formatting utilities used by the Spectre.Console-based CLI output. It delegates all storage operations to the underlying `FileMemoryStore`.

## MCP Server Mode

When `scri serve` is invoked, the CLI starts an MCP server over stdio transport:

```csharp
builder.Services
    .AddMcpServer(mcp => mcp.ServerInfo = new() { Name = "scrinia", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithTools<ScriniaMcpTools>();
```

`ScriniaMcpTools` is a sealed class (non-static, no constructor, no DI) containing all 17 MCP tool methods. It accesses the store via `MemoryStoreContext.Current`, which is set during workspace initialization.

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

## AsyncLocal Considerations

`AsyncLocal<T>` does NOT propagate from the CLI's startup code through the generic host to MCP tool handler threads. Both `MemoryEventSinkContext` and `SearchContributorContext` have a `Default` static fallback for this reason:

```csharp
// CLI sets Default (global static)
SearchContributorContext.Default = pluginHost;
MemoryEventSinkContext.Default = pluginHost;

// MCP tool handlers read Default when Current is null
var contributor = SearchContributorContext.Current ?? SearchContributorContext.Default;
```

## Output Rendering

The CLI uses Spectre.Console for rich terminal output:
- Tables with colors for `list` and `search` results
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
- All JSON serialization uses source-gen contexts (`StoreJsonContext`, `BundleJsonContext`, `FileStoreJsonContext`, `ConfigJsonContext`, `PluginClientJsonContext`)
- ConsoleAppFramework v5 is source-generated (no reflection)
- No dynamic assembly loading (plugins are child processes)
- `InternalsVisibleTo` for test access only

## Test Coverage

449 tests in `Scrinia.Tests` covering:
- All 18 MCP tools
- Store operations and edge cases
- Search ranking and scoring
- Encoding and chunking
- Bundle export/import
- Workspace discovery
- Configuration resolution
- Embeddings (VectorStore, VectorIndex, HnswIndex, HybridReranker, BertTokenizer, SafeTensorsReader, Model2Vec, API providers)

Test isolation uses `TestHelpers.StoreScope` which redirects workspace, store directory, ephemeral store, and `SessionBudget` via `AsyncLocal` overrides.
