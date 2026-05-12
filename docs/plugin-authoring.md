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

## Lifecycle hooks reference

`IMemoryOperationHook` (REST path) gives you before/after pairs for each mutating operation:

| Hook | Cancellable | Context |
|---|---|---|
| `OnBeforeStoreAsync` / `OnAfterStoreAsync` | Yes (before only) | `BeforeStoreContext` / `AfterStoreContext` |
| `OnBeforeAppendAsync` / `OnAfterAppendAsync` | Yes (before only) | `BeforeAppendContext` / `AfterAppendContext` |
| `OnBeforeForgetAsync` / `OnAfterForgetAsync` | Yes (before only) | `BeforeForgetContext` / `AfterForgetContext` |

Set `context.Cancel = true` and provide `context.CancelReason` to abort the operation; the server returns the reason as a structured error to the caller.

`IMemoryEventSink` (MCP path) is notification-only — `OnStoredAsync`, `OnAppendedAsync`, `OnForgottenAsync`. There's no equivalent before-hook on the MCP path because MCP tools enforce permissions upstream.

## Endpoints and middleware

Override `MapEndpoints` to add HTTP routes scoped under your plugin's own group:

```csharp
public override void MapEndpoints(IEndpointRouteBuilder endpoints)
{
    endpoints.MapGet("/hello", () => "world").RequireAuthorization();
}
```

The server mounts plugin endpoints under `/api/v1/plugins` and applies `RequireAuthorization` + the `api` rate-limit policy to the group, so your endpoints inherit those. Override `ConfigureMiddleware` if you need to add cross-cutting middleware — it runs after authentication, before endpoint routing.

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
