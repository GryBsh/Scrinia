# Plugin Authoring Guide

Scrinia plugins are standalone executables launched by `scri` and driven over MCP stdio. The
embeddings plugin is the reference implementation. A plugin is not a class library loaded into the
CLI; build and publish an executable.

## Process contract

`McpPluginHost` launches the executable with `--data-dir`, `--models-dir`, and forwarded
`--config key=value` arguments. Configuration keys and secret handling are defined by
`PluginConfigProtocol` in `Scrinia.Core.Embeddings`; use `BuildOptions` instead of parsing keys
independently. API keys are placed in the child environment, never on the command line.

The executable should read MCP requests from stdin and write MCP responses to stdout. Diagnostics
belong on stderr. The host discovers `{exeDir}/plugins/{name}/{name}[.exe]` first, then
`{exeDir}/plugins/{name}[.exe]`; the name comes from `plugins:embeddings`.

## Embeddings plugin tools

| Tool | Purpose |
|---|---|
| `index` | Preferred whole-memory operation. Receives the complete content array and atomically replaces that memory's vectors using `MemoryVectorIndexer`. |
| `search` | Embeds a query and returns supplemental scores keyed by scope, name, and optionally chunk. |
| `upsert` | Compatibility single-vector operation. It cannot represent the complete element/window layout and should not be used by new hosts. |
| `remove` | Removes every vector for a memory. |
| `status` | Reports provider availability, dimensions, and vector count. |

Offer `index`. The host falls back to `upsert` only for older plugins. Retry is owned by
`McpPluginHost.CallToolWithRetryAsync`; plugin tools should fail fast rather than retrying across the
same process boundary a second time.

## Current project boundaries

- **Core** owns `IEmbeddingProvider`, provider resilience, chunking, `MemoryVectorIndexer`,
  `VectorStore`, reindexing, and in-process search scoring.
- **CLI** owns workspace configuration and the MCP client that supervises an optional plugin child
  process.
- **MCP** owns the public memory tool surface and event dispatch.
- **Plugin.Abstractions** contains the published plugin contracts that are still useful to callers.
- **Plugin.Embeddings** contains the Vulkan provider and plugin implementation; its CLI project is
  the executable MCP host for that implementation.

The executable may configure itself with `EmbeddingsPlugin.Configure(IConfiguration)`. There is no
HTTP endpoint-registration or web-host contract for plugins.

## Testing

Instantiate the plugin directly and call its methods. Inject a deterministic `IEmbeddingProvider`
and `VectorStore` with `InitializeForTests` when testing indexing behavior. The worked examples are
in `tests/Scrinia.Plugin.Embeddings.Tests/`.

## Common pitfalls

- Build an executable, not a DLL.
- Use `PluginConfigProtocol.BuildOptions` for forwarded configuration.
- Keep secrets out of argv and stdout.
- Prefer `index` so chunking and dimension validation remain shared with Core.
- Do not add a second retry loop around MCP calls.
