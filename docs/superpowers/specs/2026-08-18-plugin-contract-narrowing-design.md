# Plugin Contract Narrowing — Design

**Date:** 2026-08-18
**Finding:** M3 from the 2026-08-16 design review — "the published plugin contract cannot be versioned independently of internal churn"
**Base:** `refactor/plugin-contract-narrowing`, cut from `fix/published-surface-coherence` at `089f892`
**Supersedes:** Part C of `docs/superpowers/specs/2026-08-18-published-surface-coherence-design.md`, which is withdrawn

## Why this is not the change M3 asked for

M3's remedy — move the DTOs and `IMemoryEventSink` into `Scrinia.Plugin.Abstractions`, reverse the `ProjectReference`, pin `AssemblyVersion` — exists to let **third parties** compile against a contract that survives a routine product release. That premise was tested and does not hold: **there is no external plugin audience, and none is planned.** The only real plugin, `Scrinia.Plugin.Embeddings`, ships in this repository and is rebuilt with it.

With the audience gone, every expensive part of the original design buys nothing:

- **`AssemblyVersion` pinning** stops third-party rebinding. There is nothing to rebind.
- **The compile-without-Core boundary test** asserts a property no consumer needs.
- **The assembly move** costs `<Using>` items in ~11 csprojs, because 106 files outside `Scrinia.Core` reference `ArtifactEntry`/`ChunkEntry`/`ScopedArtifact` through `using Scrinia.Core.Models;` — a namespace that *survives* the move, so there is no compiler hint, just `CS0246` on the type name.
- **The `ProjectReference` reversal** drags ASP.NET Core into a `--self-contained -p:PublishTrimmed=true` CLI publish, where `TreatWarningsAsErrors` turns trim-analysis warnings into publish failures. `Scrinia.Plugin.Abstractions.csproj:12` carries `<FrameworkReference Include="Microsoft.AspNetCore.App" />` and needs it, because `IScriniaPlugin.ConfigureMiddleware` takes `IApplicationBuilder` and `MapEndpoints` takes `IEndpointRouteBuilder`. `FrameworkReference` is transitive across `ProjectReference`, verified empirically.

What survives the audience's absence is a smaller, real defect: **`IMemoryEventSink` hands every implementer the full `IMemoryStore` — 30 instance members — when the sink path demonstrably uses four.** That is a legibility problem, not a versioning one, and it is fixable without moving a single type between assemblies.

(Counted at `089f892`: 32 declarations in `IMemoryStore.cs`, of which `ShouldExcludeScope` and `BuildExcludedScopeSet` are static helpers. M3's blockers finding records this as "twenty-two"; that figure is stale and the correction widens the gap the narrowing closes, so the spec states the measured number and the method for re-deriving it.)

This spec fixes that. It does not invert the build graph.

## Measured basis

Every sink implementation's store usage at `089f892`, measured by reading each body — including members reached through a helper's own parameter, which is what a `store.` grep misses and what produced two wrong measurements in M3's first pass.

| Sink | Project | Sink-path members | Fits four? |
|---|---|---|---|
| `EmbeddingsPlugin` | `Scrinia.Plugin.Embeddings` | `ParseQualifiedName` + `ReadForAppendAsync`'s four | ✅ |
| `BuiltInEmbeddingsService` | `Scrinia.Server` | `ParseQualifiedName` + `ReadForAppendAsync`'s four | ✅ |
| `CoreEmbeddingEventHandler` | `src/Scrinia` | `ParseQualifiedName` + `ReadForAppendAsync`'s four | ✅ |
| `McpPluginHost` | `src/Scrinia` | `ParseQualifiedName`, `ReadArtifactAsync` | ✅ |
| `CompositeEventSink` | `Scrinia.Core` | none — pure fan-out | ✅ |
| `EntityTypingEventHandler` | `Scrinia.Core` | `GetEntityFactStore`, `LoadIndex`, `ParseQualifiedName`, `SanitizeName`, `TryUpdateEntry` | ❌ five |
| `MaintenanceEventSink` | `Scrinia.Core` | `LoadIndex`, `SanitizeName`, `ParseQualifiedName`, `TryUpdateEntry`, `FormatQualifiedName`, `AnyScoped` | ❌ six |

Two corrections to the original spec's table, both verified:

1. **`ListScoped` is not on the sink path at all.** It appears only in `ReindexStoreAsync` (`EmbeddingsPlugin.cs:386`, `BuiltInEmbeddingsService.cs:110`), which is `IEmbeddingsAdmin` and receives its own `IMemoryStore` parameter. The original "exactly `ParseQualifiedName` + `ListScoped`" named a member the contract does not need and missed three it does.
2. **`CoreEmbeddingEventHandler` is a sink and was missed entirely** by the original enumeration.

Five of seven fit a four-member contract. The two that do not are Core's own internal handlers — so the split falls exactly along the line between "what a plugin needs" and "what Core's maintenance code needs", which is the conflation M3 named.

One constraint from the original design is void here: `GetEntityFactStore` returning a Core-internal type was a problem only because types were crossing an assembly boundary. Nothing crosses one in this design.

## The design

### 1. `IPluginStore`

A new interface in `Scrinia.Core` — same assembly, same namespace as `IMemoryStore`:

```csharp
public interface IPluginStore
{
    (string Scope, string Subject) ParseQualifiedName(string name);
    bool IsEphemeral(string name);
    Task<string> ResolveArtifactAsync(string nameOrArtifact, CancellationToken ct = default);
    Task<string> ReadArtifactAsync(string subject, string scope, CancellationToken ct = default);
}
```

`IMemoryStore : IPluginStore`. The four declarations move out of `IMemoryStore` and are inherited back, so every existing `IMemoryStore` consumer is unaffected and no implementation changes.

**The four declarations to move, named exactly** — `IMemoryStore.cs` at `:13` (`ParseQualifiedName`), `:15` (`IsEphemeral`), `:66` (`ResolveArtifactAsync`), `:318` (`ReadArtifactAsync`). This precision is not pedantry: `IMemoryStore` declares `ListScoped` twice (abstract one-arg at `:154`, default-bodied two-arg at `:197`), and M3's blockers finding records that a vague "delete the members now inherited" instruction risks taking the topic-exclusion overload and its `ShouldExcludeScope` filter with it.

### 2. The sink signature

`IMemoryEventSink`'s three methods take `IPluginStore`:

```csharp
Task OnStoredAsync(string qualifiedName, string[] content, IPluginStore store, CancellationToken ct);
Task OnAppendedAsync(string qualifiedName, string content, IPluginStore store, CancellationToken ct);
Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IPluginStore store, CancellationToken ct);
```

`MemoryArtifactSource.ReadForAppendAsync` changes with them, from `IMemoryStore` to `IPluginStore` (`MemoryArtifactSource.cs:93`). This is required, not incidental: three of the four plugin-shaped sinks reach their store members *only* through that helper. Its three call sites are `CoreEmbeddingEventHandler.cs:51`, `EmbeddingsPlugin.cs:351` and `BuiltInEmbeddingsService.cs:181`, all inside `OnAppendedAsync`.

### 3. The two casts

`EntityTypingEventHandler` and `MaintenanceEventSink` need five and six members respectively. They cast:

```csharp
if (store is not IMemoryStore full)
    throw new InvalidOperationException(
        $"{nameof(MaintenanceEventSink)} maintains cross-memory references and requires the " +
        $"full {nameof(IMemoryStore)}, not the narrowed {nameof(IPluginStore)} the sink " +
        "contract declares. It is a Core-internal handler, not a plugin.");
```

**Throw, not silently skip.** Skipping entity typing or reference maintenance is invisible data loss; a throw is a visible failure. `CompositeEventSink` already isolates per-sink exceptions, so the blast radius is one logged sink failure rather than a broken pipeline — which is the correct severity for "a Core handler was handed a store it cannot work with".

This is the design's acknowledged weakness. A cast is enforcement by convention: nothing stops a future sink widening its reach the same way. What the narrowing buys is that the contract now *states* what a plugin needs, and any widening is a visible cast rather than an invisible use of an already-available member. That was the trade accepted in choosing this over a two-interface split.

### 4. Call sites that hand a store to a sink

These pass an `IMemoryStore` where an `IPluginStore` is now expected — an implicit upcast, so they should compile unchanged. The plan must **verify** this rather than assume it:

- `MemoryTools.Core.cs` — `:224`, `:953`, `:982`, `:1015`, `:1083`, and `FireEventSinkAsync` at `:1346`
- `ScriniaCommands.Migration.cs:314`
- `CompositeEventSink`, `EventSinkOperationHook`
- `EmbeddingReindexer.ReindexViaSinkAsync` — its own `store` parameter stays `IMemoryStore`, because it calls `ListScoped` for its own iteration; only what it passes to the sink narrows.

### 5. Implementations to update

**Sixteen**, all mechanical parameter-type changes.

Production (7): `CoreEmbeddingEventHandler`, `McpPluginHost`, `CompositeEventSink`, `EntityTypingEventHandler`, `MaintenanceEventSink`, `EmbeddingsPlugin`, `BuiltInEmbeddingsService`.

Test doubles (9): `SilentEventSink` (`Scrinia.TestDoubles`), `RecordingSink` and `ThrowingSink` (`CompositeEventSinkTests`), `CapturingSink` (`EmbeddingReindexerNamespacedScopeTests`), `CaptureSink` (`EmbeddingReindexerTests`), `RecordingSink` and `NoOpSink` (`MemoryEventSinkContextInstallDefaultTests`), `CapturingSink` (`ScriniaMcpToolsTests`), `DoubleFireSpy` (`EventPipelineParityTests`).

The original M3 plan counted the production sinks and not the doubles. Under this design the doubles are cheap — a parameter type — but they must be counted so the change is not reported as smaller than it is.

## Testing

**The load-bearing test:** a test double implementing **only** `IPluginStore` — with no `IMemoryStore` implementation anywhere in its type — compiles and can be driven through `CompositeEventSink` to a plugin-shaped sink. This is the assertion the whole change exists to make true, and it is pinned by the compiler rather than by inspection. If this test cannot be written, the narrowing did not happen.

**The two casts:** for each of `EntityTypingEventHandler` and `MaintenanceEventSink`, one test proving the handler works when handed a real `IMemoryStore`, and one proving it throws — with the handler named in the message — when handed an `IPluginStore` that is not one.

**Per-sink isolation survives:** a throwing Core handler does not stop the other sinks in a `CompositeEventSink` fan-out.

**Every new pin is falsified by mutation before it is trusted** — break the fix, watch the test fail, restore, watch it pass. Two of the previous branch's own tests passed while the thing they claimed to pin was broken; that is this codebase's most expensive recurring lesson and it applies here.

## Non-goals

Stated explicitly, because the superseded design included all of them:

- **No assembly moves.** `ArtifactEntry`, `ChunkEntry`, `ScopedArtifact`, `IMemoryEventSink` and the hook contexts all stay where they are.
- **The `ProjectReference` is not reversed.** `Scrinia.Plugin.Abstractions` continues to reference `Scrinia.Core`.
- **No `AssemblyVersion` pin.**
- **No `ICoreEventSink`, no adapter, no second sink interface.**
- **Hook contexts are not retyped.** `ctx.Store` stays `IMemoryStore`: the falsification pass found nine sites passing it into `IMemoryStore`-typed parameters, so narrowing it breaks them for no in-repo gain.
- **No change to `IMemoryOperationHook` or the REST path.**

## What this leaves on the table, deliberately

If an external plugin audience ever becomes real, the assembly split is still the right answer and this change is a strict step toward it — `IPluginStore` is the type that would move first. The findings that make that migration cheap are already recorded and verified, and should not be re-derived: the build-graph flip is cycle-free (`Scrinia.Core` has no `ProjectReference`), the three DTOs are self-contained, the move is serialization-inert (no `[JsonSerializable]`, no `[JsonDerivedType]`, no `Type.GetType` naming them), and the real blocker is the ASP.NET `FrameworkReference`, which would force splitting Abstractions into an ASP.NET-free contract assembly and a hosting assembly. See `/findings/plugin-contract-inversion-blockers`.

## Risks

**The cast is the weak point.** Mitigated by the throw and its test, but not eliminated. Anyone may widen a sink's reach by casting, and only review will catch it.

**Sixteen mechanical edits across five projects** is low risk per edit and easy to get wrong in aggregate. The LSP in this workspace emits phantom `CS0535`/`CS1503` errors during large renames while `dotnet build` is green, so verification must be by build and by real file bytes, never by the diagnostic stream.

**`ReadForAppendAsync` is the trap.** It is the reason the original measurement was wrong twice: its store members are invisible to a grep scoped to the sink's own file. Any re-measurement of "what does a sink use" must follow helpers that take a store parameter.
