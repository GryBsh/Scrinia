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
belong on stderr. The host discovers the plugin executable only through the mandatory
`SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/` hierarchy — there is no flat plugin fallback — and
the name comes from `plugins:embeddings`.

## Plugin location and installation

A plugin is a **separately installed**, RID-specific executable — it is not part of the immutable
`scri` global tool package. After installation its executable, managed assemblies, and native
libraries live only at the mandatory hierarchy `SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/`, and
any mutable plugin state lives at `SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/state/`. The `scri`
host discovers the plugin executable only through this hierarchy — there is no flat
`plugins/<plugin>` fallback and nothing is written into the immutable package directory at runtime.
For the shipped embeddings plugin, extract the embeddings release archive. From the extracted
directory, run `./scri setup` on Linux/macOS or `.\scri.exe setup` in PowerShell on Windows, or run
`scri setup --embeddings-source <extracted-release-directory>` with the NuGet-installed host.
The source must contain `embeddings-bundle.json` and the exact
`plugins/scri-plugin-embeddings/0.6.0/<rid>/` tree. Setup verifies and copies the payload into
`SCRINIA_HOME/plugins/scri-plugin-embeddings/0.6.0/<rid>/`, preserving the extracted files.
A checksum detects changed contents, not publisher identity. Windows/Linux use Vulkan; macOS
uses CPU/Metal. See [installation outcomes and retries](installation.md#embeddings-plugin-installation).

The shipped bundle contract is version 1 with a 64 KiB manifest, at most 32 file records,
512 MiB per file, and 1 GiB total payload. It requires the fixed plugin name, exact host version,
current supported RID, and exact payload files. It is not a general plugin installer.
Setup success confirms installation/configuration/path selection, not initialization or inference;
`serve` does not implicitly install a bundle. Migration of legacy assets stored beside the
executable is a separate step.

## Embeddings plugin tools

The executable surface is the versioned embeddings protocol v1. The `status` handshake must report
`scrinia.embeddings.protocol = 1`, the exact capabilities `index`, `search`, `remove`, and `status`,
provider availability, dimensions, and the vector-store signature. Unsupported or malformed
handshakes are disabled for the session and produce `EMBEDDINGS_PROTOCOL_UNSUPPORTED` or
`EMBEDDINGS_PROTOCOL_INVALID`; the host falls back to lexical search.

| Tool | Purpose |
|---|---|
| `index` | Preferred whole-memory operation. Receives the complete content array and atomically replaces that memory's vectors using `MemoryVectorIndexer`. |
| `search` | Embeds a query and returns supplemental scores keyed by scope, name, and optionally chunk. |
| `upsert` | Deprecated protocol-v1 compatibility operation for installed legacy executables; new hosts never select it after a successful handshake. |
| `remove` | Removes every vector for a memory. |
| `status` | Reports provider availability, dimensions, and vector count. |

Offer `index`. `search` receives a bounded canonical candidate set and must return scores only for
those identities. Retry is owned by `McpPluginHost.CallToolWithRetryAsync`; plugin tools should
fail fast rather than retrying across the same process boundary a second time.

If vectors carry an incompatible provider signature, the host quarantines them instead of treating
them as current. Rebuild them through the documented `scri reindex` flow.

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

## Memory address contract

Plugin callbacks receive the validated `MemoryAddress` value type in their event payloads and
callback methods. Use `MemoryAddress.Parse(raw)` when converting trusted configuration or
`MemoryAddress.TryParse(raw, out address, out error)` when handling external input. Do not add raw
string address parameters, URI resolvers, or `file://` handling to a plugin.

`MemoryAddress` distinguishes persistent addresses from ephemeral addresses. `~name` is the
canonical ephemeral spelling; `/temp/name` is its accepted alias. The host's CLI is
persistent-only, while a long-lived MCP server process can dispatch events for ephemeral memories.

`ArtifactEntry.Address` identifies the logical memory. `ArtifactEntry.ArtifactKey` is an opaque,
store-relative portability key. Pass the typed address back to the supplied `IPluginStore` for
reads; do not interpret or concatenate the artifact key as a filesystem path or URI.

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
