# Plugin Authoring Guide

Scrinia's HTTP server loads .NET plugins at startup to extend functionality without modifying the core. Plugins can register DI services, add HTTP middleware/endpoints, hook into memory operations, and contribute supplemental search scores (e.g. semantic similarity from a custom embedding model).

This guide walks through writing a minimal plugin end to end. It assumes you have .NET 10 SDK installed and a working Scrinia server.

## Concepts

A plugin is a single .NET DLL placed in the server's plugin directory. On startup, the server scans the directory, loads each DLL in an isolated `AssemblyLoadContext`, discovers public non-abstract types that implement `IScriniaPlugin`, and instantiates them via their parameterless constructor.

Plugins are loaded in ascending `Order` value. The order matters when multiple plugins register the same DI service or when middleware ordering is significant.

### The four extension points

| Interface | Project | When it fires |
|---|---|---|
| `IScriniaPlugin` | `Scrinia.Plugin.Abstractions` | Required entry point — gives you DI access plus middleware + endpoint mapping hooks. |
| `IMemoryOperationHook` | `Scrinia.Plugin.Abstractions` | Before/after `Store`, `Append`, `Forget` on the **REST** path. Can cancel operations. |
| `IMemoryEventSink` | `Scrinia.Core` | After `Store`, `Append`, `Forget` on the **MCP** path. Notification-only — no cancellation. |
| `ISearchScoreContributor` | `Scrinia.Core` | Asked to supply supplemental scores (keyed by `{scope}\|{name}` or `{scope}\|{name}\|{chunkIndex}`) during search. |

The REST and MCP paths are intentionally split — implement both interfaces (often by delegating to one shared method) if your plugin needs to observe every write regardless of caller.

## Minimal plugin

A "hello world" plugin that logs every store operation.

**1. Create the project**

```bash
mkdir Hello.Scrinia.Plugin
cd Hello.Scrinia.Plugin
dotnet new classlib -f net10.0
```

**2. Reference `Scrinia.Plugin.Abstractions`**

Until the abstractions are published to NuGet, reference the assembly directly. The simplest way is via `ProjectReference` if you build alongside Scrinia, or via `Reference` to the compiled DLL.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- Do not copy referenced framework assemblies into the plugin output. -->
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Scrinia.Plugin.Abstractions">
      <HintPath>..\path\to\Scrinia.Plugin.Abstractions.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Scrinia.Core">
      <HintPath>..\path\to\Scrinia.Core.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

`Private=false` ensures the referenced assemblies are **not** copied into the plugin output; the server provides them at load time.

**3. Implement `IScriniaPlugin`**

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scrinia.Plugin.Abstractions;

namespace Hello.Scrinia.Plugin;

public sealed class HelloPlugin : ScriniaPluginBase, IMemoryOperationHook
{
    public override string Name => "hello";
    public override string Version => "0.1.0";
    public override int Order => 100;  // run after built-in plugins

    public Task OnAfterStoreAsync(AfterStoreContext context, CancellationToken ct = default)
    {
        // Loggers, options, etc. resolved at construction time would be wired via
        // ConfigureServices + a separate component class. For a notification-only hook
        // this inline approach is fine.
        Console.WriteLine($"[hello] stored {context.QualifiedName} ({context.ChunkCount} chunks)");
        return Task.CompletedTask;
    }
}
```

Extending `ScriniaPluginBase` gives you empty defaults for the three `IScriniaPlugin` methods (`ConfigureServices`, `ConfigureMiddleware`, `MapEndpoints`) so you only override what you actually use. Implementing `IMemoryOperationHook` directly on the plugin class auto-registers it with the server's hook pipeline — no extra DI plumbing required.

**4. Build and deploy**

```bash
dotnet build -c Release
cp bin/Release/net10.0/Hello.Scrinia.Plugin.dll <server-data-dir>/plugins/
```

The default plugin directory is configurable via `Scrinia:PluginsDir` in the server config; the server creates it at startup if missing. Restart `scrinia-server` — on boot you should see:

```
info: Loaded plugin: hello v0.1.0 (order=100) from Hello.Scrinia.Plugin.dll
```

Storing a memory now logs `[hello] stored ...` to standard output.

## Worked example: a custom search-score contributor

This plugin boosts memories tagged `priority` by a configurable factor. It implements both `IScriniaPlugin` (entry point) and `ISearchScoreContributor` (the actual scoring logic), with config bound from `Scrinia:HelloPlugin`.

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Plugin.Abstractions;

namespace Hello.Scrinia.Plugin;

public sealed class PriorityBoostOptions
{
    public double Factor { get; set; } = 1.5;
    public string Tag { get; set; } = "priority";
}

public sealed class PriorityBoostPlugin : ScriniaPluginBase
{
    public override string Name => "priority-boost";
    public override string Version => "0.1.0";

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PriorityBoostOptions>()
            .Bind(configuration.GetSection("Scrinia:PriorityBoost"))
            .ValidateOnStart();
        services.AddSingleton<ISearchScoreContributor, PriorityBoostScorer>();
    }
}

internal sealed class PriorityBoostScorer(IOptions<PriorityBoostOptions> options) : ISearchScoreContributor
{
    public Task<IReadOnlyDictionary<string, double>?> ComputeScoresAsync(
        string query,
        IReadOnlyList<ScopedArtifact> candidates,
        IMemoryStore store,
        CancellationToken ct)
    {
        var opts = options.Value;
        var scores = new Dictionary<string, double>();
        foreach (var candidate in candidates)
        {
            if (candidate.Entry.Tags is null) continue;
            if (Array.Exists(candidate.Entry.Tags, t => string.Equals(t, opts.Tag, StringComparison.OrdinalIgnoreCase)))
            {
                string key = $"{candidate.Scope}|{candidate.Entry.Name}";
                scores[key] = opts.Factor;
            }
        }
        return Task.FromResult<IReadOnlyDictionary<string, double>?>(scores);
    }
}
```

Two patterns worth noting:

- **Options are bound the same way as core server options.** `Scrinia:` configuration sections are merged from `appsettings.json`, environment variables, and command-line — your plugin participates in the standard pipeline.
- **Scoring keys are `{scope}|{name}` for entries and `{scope}|{name}|{chunkIndex}` for chunks.** Returning `null` is equivalent to returning an empty dictionary.

  `chunkIndex` is a **1-based index over the memory's content elements** — the same value
  `ChunkEntry.ChunkIndex` carries. It is not a character-window ordinal and not 0-based. This is
  worth getting right the first time, because getting it wrong is silent: a mis-keyed chunk score
  never matches, the entry-level key absorbs the lookup, and search keeps working while quietly
  losing the chunk-level signal. If you slice a long element into overlapping windows, every
  window rolls up into that one element's key with the maximum score winning —
  `VectorScoreProjection.Project` in `Scrinia.Core.Embeddings` does exactly this and is the
  supported way to build the dictionary.

## Lifecycle hooks reference

`IMemoryOperationHook` (REST path) gives you before/after pairs for each mutating operation:

| Hook | Cancellable | Context |
|---|---|---|
| `OnBeforeStoreAsync` / `OnAfterStoreAsync` | Yes (before only) | `BeforeStoreContext` / `AfterStoreContext` |
| `OnBeforeAppendAsync` / `OnAfterAppendAsync` | Yes (before only) | `BeforeAppendContext` / `AfterAppendContext` |
| `OnBeforeForgetAsync` / `OnAfterForgetAsync` | Yes (before only) | `BeforeForgetContext` / `AfterForgetContext` |

Set `context.Cancel = true` and provide `context.CancelReason` to abort the operation; the server returns the reason as a structured error to the caller.

`IMemoryEventSink` (MCP path) is notification-only — `OnStoredAsync`, `OnAppendedAsync`, `OnForgottenAsync`. There's no equivalent before-hook on the MCP path because MCP tools enforce permissions upstream.

Each method hands the sink an `IPluginStore`, not the full `IMemoryStore` — four members:
`ParseQualifiedName`, `IsEphemeral`, `ResolveArtifactAsync`, `ReadArtifactAsync`. That is what the
sink path itself uses, and it is what most sinks need too: enough to key and read the memory that
just changed. A sink that needs more — writing back, listing a scope, resolving entities — can cast
to `IMemoryStore`, which every concrete store still implements.

## Endpoints and middleware

Override `MapEndpoints` to add HTTP routes scoped under your plugin's own group:

```csharp
public override void MapEndpoints(IEndpointRouteBuilder endpoints)
{
    endpoints.MapGet("/hello", () => "world").RequireAuthorization();
}
```

The server mounts plugin endpoints under `/api/v1/plugins` and applies `RequireAuthorization` + the `api` rate-limit policy to the group, so your endpoints inherit those. Override `ConfigureMiddleware` if you need to add cross-cutting middleware — it runs after authentication, before endpoint routing.

## Out-of-process plugins: the MCP tool protocol

The four interfaces above are for **in-process** plugins — a DLL the server loads. There is a
second kind: a standalone executable the CLI launches as a child process and drives over MCP
stdio. `scri-plugin-embeddings` is the reference implementation. Choose this shape when your
plugin has heavyweight or awkward native dependencies (GPU runtimes, large models) that you do
not want inside the host process, or when you want the plugin to crash without taking the host
with it.

The host (`McpPluginHost`) launches the child, hands it `--data-dir`, `--models-dir` and a
`--config key=value` pair per forwarded configuration key, and then calls its tools. Which keys
are forwarded, which of them are secrets, and how a secret maps to an environment variable are
all defined in one place: `PluginConfigProtocol` in `Scrinia.Core.Embeddings`.
`PluginConfigProtocol.BuildOptions` is the plugin-side half — call it rather than parsing keys
yourself, so the two ends cannot drift.

**Secrets never travel on argv.** A process command line is world-readable on the local host
(`ps`, Task Manager, `/proc/<pid>/cmdline`). Any key matching `*ApiKey` is passed through the
child's environment instead; `PluginConfigProtocol.ApplySecretEnvironment` reads them back.

### Tools an embeddings-shaped plugin exposes

| Tool | Purpose |
|---|---|
| `index` | **Preferred.** Takes a memory's whole content array and replaces its vectors. The plugin runs the shared `MemoryVectorIndexer` internally, so the chunking policy lives in one place instead of being restated by every caller that speaks the protocol. |
| `search` | Query text plus scopes; returns a JSON map of supplemental scores in the same key shape described above. |
| `upsert` | Legacy single-vector write. Still supported, but its `chunkIndex` cannot express the element/window layout — see below. |
| `remove` | Drops every vector for a memory. |
| `status` | Provider name, availability, dimensions, vector count. |

The host negotiates: it calls `index` when the plugin advertises it, and falls back to the
previous `upsert` call sequence when it does not, warning on stderr about what that costs.
**Offer `index`.** `upsert`'s `chunkIndex` is a flat integer, and any value small enough to pass
through it decodes to content element 0 — which does not exist, since elements are 1-based — so
those vectors contribute to a memory's score but never to a chunk's.

Retry belongs at exactly one layer. `McpPluginHost.CallToolWithRetryAsync` owns it for this
process boundary, so a tool implementation should fail fast and return an error rather than
retrying internally — otherwise a bulk reindex retries twice over and stalls.

## Loading and isolation

- Plugins load from `<DataDir>/plugins/*.dll` (configurable via `Scrinia:PluginsDir`).
- Each plugin DLL gets a dedicated `AssemblyLoadContext` that **falls back to the default context** for shared assemblies (Scrinia.Core, Scrinia.Plugin.Abstractions, ASP.NET). Don't ship those — reference them with `Private=false`.
- Native dependencies (e.g. LLamaSharp backends for the Vulkan embeddings plugin) live alongside the plugin DLL and are resolved via the plugin's load context.
- Plugins are loaded in `Order` ascending. Built-in plugins use order 0–10; user plugins should use ≥ 100.

## Testing

For unit tests, instantiate the plugin directly and call its methods — there's no special harness required. For integration tests, use `WebApplicationFactory<Program>` and inject the plugin via `IReadOnlyList<IScriniaPlugin>`. See `tests/Scrinia.Server.Tests/` for examples of the server test setup.

## Common pitfalls

- **Forgetting `Private=false`**: causes assembly version conflicts when the server tries to load `Scrinia.Core` from the plugin directory.
- **Parameterless constructor required**: the loader calls `Activator.CreateInstance(type)` with no args. If you need dependencies, resolve them inside `ConfigureServices` and register a service that the runtime portion of your plugin uses.
- **Plugin throws on load**: caught by the loader, logged as a warning, and skipped — check the boot log if your plugin isn't appearing.
- **Order ties**: two plugins with the same `Order` load in directory enumeration order, which is filesystem-dependent. Set explicit distinct values when ordering matters.
- **JSON serialization**: if your plugin exposes endpoints, register a source-generated `JsonSerializerContext` for your types — the server trims reflection-based JSON to keep the publish AOT-safe.
