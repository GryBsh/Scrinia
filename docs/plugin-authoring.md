# Plugin Authoring Guide

A Scrinia plugin is a **standalone executable** that the CLI launches as a child process and drives
over MCP stdio. `scri-plugin-embeddings` is the reference implementation.

This guide is shorter than it was, and the reason matters if you are holding an older copy of it.
Scrinia used to have a second plugin shape: a .NET DLL dropped into an HTTP server's plugin
directory, loaded into the server process through an `AssemblyLoadContext`, wired into its DI
container and given the chance to map its own HTTP endpoints. **That server was deleted, and the
loader went with it.** Nothing in the repository calls `Assembly.LoadFrom`, `AssemblyLoadContext`
or `Activator.CreateInstance` any more. A DLL built against the old instructions would compile and
then never be loaded by anything.

## The one shape that works

The host (`McpPluginHost`) launches your executable, hands it `--data-dir`, `--models-dir` and a
`--config key=value` pair per forwarded configuration key, and then calls its tools.

Choose an executable — as you now must — and you get what the shape was always good for anyway:
heavyweight or awkward native dependencies (GPU runtimes, large models) stay outside the host
process, and a plugin crash does not take the host with it.

Which config keys are forwarded, which of them are secrets, and how a secret maps to an environment
variable are all defined in one place: `PluginConfigProtocol` in `Scrinia.Core.Embeddings`.
`PluginConfigProtocol.BuildOptions` is the plugin-side half — call it rather than parsing keys
yourself, so the two ends cannot drift.

**Secrets never travel on argv.** A process command line is world-readable on the local host (`ps`,
Task Manager, `/proc/<pid>/cmdline`). Any key matching `*ApiKey` is passed through the child's
environment instead; `PluginConfigProtocol.ApplySecretEnvironment` reads them back.

### Discovery

The host looks for `{exeDir}/plugins/{name}/{name}[.exe]` first, then `{exeDir}/plugins/{name}[.exe]`.
The name comes from the `plugins:embeddings` config key (default `scri-plugin-embeddings`).

### Tools an embeddings-shaped plugin exposes

| Tool | Purpose |
|---|---|
| `index` | **Preferred.** Takes a memory's whole content array and replaces its vectors. The plugin runs the shared `MemoryVectorIndexer` internally, so the chunking policy lives in one place instead of being restated by every caller that speaks the protocol. |
| `search` | Query text plus scopes; returns a JSON map of supplemental scores keyed by `{scope}\|{name}` or `{scope}\|{name}\|{chunkIndex}`. |
| `upsert` | Legacy single-vector write. Still supported, but its `chunkIndex` cannot express the element/window layout — see below. |
| `remove` | Drops every vector for a memory. |
| `status` | Provider name, availability, dimensions, vector count. |

The host negotiates: it calls `index` when the plugin advertises it, and falls back to the previous
`upsert` call sequence when it does not, warning on stderr about what that costs. **Offer `index`.**
`upsert`'s `chunkIndex` is a flat integer, and any value small enough to pass through it decodes to
content element 0 — which does not exist, since elements are 1-based — so those vectors contribute
to a memory's score but never to a chunk's.

Retry belongs at exactly one layer. `McpPluginHost.CallToolWithRetryAsync` owns it for this process
boundary, so a tool implementation should fail fast and return an error rather than retrying
internally — otherwise a bulk reindex retries twice over and stalls.

## The in-process contracts

These interfaces still exist and still compile. Be clear-eyed about their status: **nothing in the
repository invokes them across a plugin boundary**, because the thing that did was the server's
plugin loader. They are kept because they are published, and because a component *inside* the
process — the built-in embeddings service, the maintenance sink — implements the Core ones for
real.

| Interface | Project | Status |
|---|---|---|
| `ISearchScoreContributor` | `Scrinia.Core` | **Live.** Supplies supplemental search scores. The built-in embeddings path installs one at startup. |
| `IMemoryEventSink` | `Scrinia.Core` | **Live.** Fires after `Store`, `Append`, `Forget`. Notification-only, no cancellation. Takes `IPluginStore`, the four-member read surface the sink path actually uses. |
| `IEmbeddingsAdmin` | `Scrinia.Core` | **Live contract, no caller.** Reindex and status. Its callers were the server's embeddings endpoints. |
| `IMemoryOperationHook` | `Scrinia.Plugin.Abstractions` | **No caller.** Before/after hooks with cancellation. Its invoker was `PluginPipeline`, which wrapped the deleted REST routes. |
| `IScriniaPlugin` | `Scrinia.Plugin.Abstractions` | **Identity only** — `Name`, `Version`, `Order`. |

`IScriniaPlugin` used to declare three more members — `ConfigureServices(IServiceCollection,
IConfiguration)`, `ConfigureMiddleware(IApplicationBuilder)` and `MapEndpoints(IEndpointRouteBuilder)`.
They were removed along with the host that called them. They were also the sole reason
`Scrinia.Plugin.Abstractions` carried a `FrameworkReference` on `Microsoft.AspNetCore.App`, which a
program with no HTTP surface has no business requiring of its plugin authors: dropping it took
**27MB** out of the published `scri-plugin-embeddings` binary (129MB to 102MB), because a
self-contained publish was bundling the entire ASP.NET Core runtime to satisfy it.

If your plugin needs configuration, bind it yourself. `EmbeddingsPlugin.Configure(IConfiguration)`
is the worked example — it is what `ConfigureServices` became once the DI registrations it also
performed had nothing left to register into.

**The rule that outlived the split** is still worth honouring if a second invoker ever appears: a
component registered as both an `IMemoryOperationHook` and an `IMemoryEventSink` must fire exactly
once per operation.

## Testing

Instantiate the plugin directly and call its methods — there is no special harness required. See
`tests/Scrinia.Plugin.Embeddings.Tests/` for a worked example, which constructs the plugin directly
rather than through any host.

## Common pitfalls

- **Building a DLL.** The host launches an executable. A class library is not loaded by anything.
- **Retrying inside a tool.** The host owns retry for this boundary; doing it again turns one
  transient failure into a stalled reindex.
- **Parsing `--config` by hand.** Use `PluginConfigProtocol.BuildOptions`, or the two ends drift.
- **Offering only `upsert`.** Its chunk indices cannot address content elements, so the vectors it
  writes never contribute to chunk-level scores.
