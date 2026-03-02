# Scrinia Plugins

Scrinia supports plugins in both the CLI and the HTTP API server.

## Embeddings plugin

The built-in embeddings plugin adds semantic vector search alongside BM25 for hybrid scoring. It uses ONNX Runtime with the `all-MiniLM-L6-v2` model (384 dimensions, ~87 MB).

### How it works

- On `store`/`append`, text is embedded and vectors stored in `.scrinia/embeddings/`
- On `search`, the query is embedded and cosine similarity scores combine with BM25
- The ONNX model is cached globally (`%LOCALAPPDATA%/scrinia/plugins/models/`)
- Vector data is workspace-local (per-project isolation)

### Publishing with embeddings

```bash
.\publish.ps1 -OutputDir ./dist -Platform win-x64 -WithEmbeddings
```

This produces `scri.exe` plus `plugins/scri-plugin-embeddings.exe`.

### Configuration

Set via `scri config`, environment variables, or both:

```bash
scri config Scrinia:Embeddings:Provider onnx      # onnx | ollama | openai | none
scri config Scrinia:Embeddings:Hardware auto       # auto | directml | cuda | cpu
scri config Scrinia:Embeddings:SemanticWeight 50.0 # weight in hybrid scoring
```

**Ollama provider:**
```bash
scri config Scrinia:Embeddings:Provider ollama
scri config Scrinia:Embeddings:OllamaBaseUrl http://localhost:11434
scri config Scrinia:Embeddings:OllamaModel nomic-embed-text
```

**OpenAI provider:**
```bash
scri config Scrinia:Embeddings:Provider openai
scri config Scrinia:Embeddings:OpenAiApiKey sk-...
scri config Scrinia:Embeddings:OpenAiModel text-embedding-3-small
```

**Custom plugin executable:**
```bash
scri config plugins:embeddings my-custom-embeddings
```

The CLI looks for `{exeDir}/plugins/{name}[.exe]`.

### Hardware acceleration

The ONNX provider auto-detects available hardware:

| Hardware | Flag | Notes |
|---|---|---|
| DirectML | `directml` | Windows GPU (AMD, Intel, NVIDIA) |
| CUDA | `cuda` | NVIDIA GPU (requires CUDA toolkit) |
| CPU | `cpu` | Fallback, always available |
| Auto | `auto` | Tries DirectML, then CUDA, then CPU |

## Plugin architecture

### CLI plugins (child-process)

CLI plugins run as separate executables communicating via MCP over stdio. Each plugin **is** an MCP server.

```
scri (MCP client) ←─stdio─→ scri-plugin-embeddings (MCP server)
```

**How it works:**
1. `WorkspaceSetup.LoadPluginsAsync` discovers `{exeDir}/plugins/scri-plugin-*` executables
2. Launches each as a child process via `StdioClientTransport`
3. Calls `ListToolsAsync` to discover capabilities (search, upsert, remove, status)
4. Wires up `ISearchScoreContributor` and `IMemoryEventSink` based on detected tools
5. Auto-reconnects up to 3 times on crash, then degrades to BM25-only

**Plugin MCP tools** (exposed by the embeddings plugin):

| Tool | Description |
|---|---|
| `status` | Provider info, availability, vector count |
| `search` | Similarity scores keyed by `{scope}\|{name}` |
| `upsert` | Embed text and store vector |
| `remove` | Remove all vectors for a memory |

### Server plugins (in-process)

Server plugins are .NET class libraries loaded via isolated `AssemblyLoadContext`. Drop DLLs into `{dataDir}/plugins/`.

**Plugin interface** (`Scrinia.Plugin.Abstractions`):

```csharp
public interface IScriniaPlugin
{
    string Name { get; }
    string Version { get; }
    int Order { get; }  // lower runs first

    void ConfigureServices(IServiceCollection services, IConfiguration config);
    void ConfigureMiddleware(IApplicationBuilder app);
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
```

**Memory operation hooks:**

```csharp
public interface IMemoryOperationHook
{
    Task OnBeforeStoreAsync(BeforeStoreContext ctx);
    Task OnAfterStoreAsync(AfterStoreContext ctx);
    Task OnBeforeAppendAsync(BeforeAppendContext ctx);
    Task OnAfterAppendAsync(AfterAppendContext ctx);
    Task OnBeforeForgetAsync(BeforeForgetContext ctx);
    Task OnAfterForgetAsync(AfterForgetContext ctx);
}
```

Before-hooks can set `Cancel = true` to abort the operation (returns 409 Conflict to the client).

Use `ScriniaPluginBase` as a convenience base class with virtual empty defaults.

### Writing a CLI plugin

1. Create a .NET console app
2. Add MCP server support (`ModelContextProtocol` package)
3. Register tools (at minimum: `status` for health checks)
4. Accept `--data-dir` and `--models-dir` arguments for data isolation
5. Accept `--config Key=Value` arguments for configuration passthrough
6. Publish as self-contained (NOT trimmed) to `{exeDir}/plugins/scri-plugin-{name}`

See `src/Scrinia.Plugin.Embeddings.Cli/` for a complete reference implementation.

### Writing a server plugin

1. Create a .NET class library referencing `Scrinia.Plugin.Abstractions`
2. Implement `IScriniaPlugin` (or extend `ScriniaPluginBase`)
3. Optionally implement `IMemoryOperationHook` for store/append/forget hooks
4. Optionally implement `ISearchScoreContributor` for custom search scoring
5. Build the DLL and place it in `{dataDir}/plugins/`

### Two code paths

The CLI and server use different hook mechanisms to avoid double-firing:

| Path | Hook mechanism | Search scoring |
|---|---|---|
| CLI (MCP stdio) | `IMemoryEventSink` via `MemoryEventSinkContext` | `ISearchScoreContributor` via `SearchContributorContext` |
| Server (REST API) | `IMemoryOperationHook` via `PluginPipeline` | `ISearchScoreContributor` via `SearchContributorContext` |

A plugin that implements both interfaces will only have one fire per request.
