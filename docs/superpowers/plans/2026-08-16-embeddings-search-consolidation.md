# Embeddings & Search Consolidation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One embed-and-index policy shared by every path, reachable through one store-scoped endpoint file, with one scoring rubric — and the dead code around all three removed.

**Architecture:** A new `MemoryVectorIndexer` in `Scrinia.Core/Embeddings/` owns window, cap, batch, count-guard, remove-then-upsert and retry; a new `VectorScoreProjection` owns the read-side key contract. Six in-process call sites route through them, and the out-of-process plugin host gets a new `index` MCP tool so the policy runs inside the plugin process rather than being re-expressed over the wire. The embeddings REST routes move from an inline `/api/v1/plugins/embeddings` group — where they could never resolve a store — into `Endpoints/EmbeddingsEndpoints.cs` under the existing `/api/v1/stores/{store}` group.

**Tech Stack:** C# / .NET 10.0 preview, ASP.NET Core minimal APIs, xUnit 2.9.3 + FluentAssertions, React + TanStack Query (web client), Model Context Protocol C# SDK.

**Spec:** `docs/superpowers/specs/2026-08-16-embeddings-search-consolidation-design.md` (committed at `ea9b776`).

## Global Constraints

- Build gate is `dotnet build Scrinia.sln`. Do **not** pass `-p:NuGetAudit=false`; that workaround was retired 2026-07-30.
- Test gate is `dotnet test Scrinia.sln`. A task is not done until it is green. Capture both `Failed!` and `[FAIL]` lines when filtering output — a filter that only greps `Failed!` discards the failing test's name.
- `tests/Scrinia.Tests` and `tests/Scrinia.Server.Tests` both have `global using Xunit;`. Adding a per-file `using Xunit;` to `Scrinia.Server.Tests` produces **CS8933**. `Scrinia.Merge.Tests`, `Scrinia.Plugin.Embeddings.Tests` and `Scrinia.Retrieval.Benchmarks.Tests` carry their own conventions — check the neighbouring files in whichever project you are adding to.
- The chunk index stored in a vector is **two-level**: `elementIndex * MemoryVectorIndexer.ChunkIndexStride + windowOrdinal`, where `elementIndex` is 1-based over nmp2 content elements and `windowOrdinal` is 0-based within that element. `ChunkIndexStride = 1000`. Never write a raw window ordinal as a chunk index.
- The supplemental score keys the scorer probes are exactly `$"{scope}|{name}"` and `$"{scope}|{name}|{chunkIndex}"` where `chunkIndex` is the **element index**, 1-based (`WeightedAttributeScorer.cs:714`). Never emit a window ordinal into a score key.
- `MaxChunksPerMemory` (default 100) caps the **total window count across the whole memory**, matching today's built-in behaviour.
- This repo suffers phantom LSP diagnostics during renames and moves (CS0101, CS0104, CS1061 against deleted or cached files). Trust `dotnet build`, not the diagnostic stream. Also check `git status` for untracked ghost `.cs` files that SynologyDrive resurrects after a `git rm`.
- CHANGELOG sections use `### <Type> — <descriptive title>`, never a bare `### Fixed`. Match the existing 40+ sections.
- Do **not** consolidate the ~30 hand-written `ctx.HasPermission(...)` gates in `MemoryEndpoints.cs`. That is finding M5, branch 6. This branch adds one gate in the existing style.
- Do **not** collapse the five `50.0` defaults into one constant. They are distinct knobs that share a value.

---

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `src/Scrinia.Core/Embeddings/MemoryVectorIndexer.cs` | The single embed-and-index policy: window, cap, batch, count-guard, remove-then-upsert, retry |
| `src/Scrinia.Core/Embeddings/VectorScoreProjection.cs` | The single read-side projection: vector hits → supplemental score keys, max-aggregated |
| `src/Scrinia.Core/Embeddings/IEmbeddingsAdmin.cs` | What the embeddings REST endpoints need, independent of which implementation provides it |
| `src/Scrinia.Server/Endpoints/EmbeddingsEndpoints.cs` | The three embeddings routes, store-scoped and permission-gated |
| `src/Scrinia.Core/Search/ScoringWeights.cs` | The scoring rubric's constants, in one place |
| `tests/Scrinia.Tests/Embeddings/MemoryVectorIndexerTests.cs` | Indexer behaviour: nesting, cap, count-guard, key shape |
| `tests/Scrinia.Tests/Embeddings/VectorScoreProjectionTests.cs` | Projection behaviour: both keys, max-aggregation |
| `tests/Scrinia.Tests/Embeddings/IndexerScorerContractTests.cs` | The conformance proof: both keys the scorer probes resolve |
| `tests/Scrinia.Server.Tests/EmbeddingsEndpointsTests.cs` | Route reachability, permission gate, retired path |
| `tests/Scrinia.Tests/Search/ScoringWeightsPinningTests.cs` | Characterization of `SearchAll` before and after the M6 refactor |

**Modified:** `EmbeddingReindexer.cs`, `CoreEmbeddingEventHandler.cs`, `BuiltInEmbeddingsService.cs`, `EmbeddingsPlugin.cs`, `EmbeddingsTools.cs`, `McpPluginHost.cs`, `HybridReranker.cs`, `ChunkedSignature.cs`, `Program.cs`, `WeightedAttributeScorer.cs`, `RankerOptions.cs`, `EntityGraph.cs`, `MergeConfig.cs`, `ScriniaCommands.Setup.cs`, `EntityTypeClassifier.cs`, `web/src/api/client.ts`, `web/src/api/types.ts`, `web/src/pages/SettingsPage.tsx`.

**Deleted:** `HnswIndex.cs`, `HnswIndexTests.cs`, `HnswIndexBenchmarks.cs`, `EntityGraphEquivalenceTests.cs`.

---

## Task 1: Store-scoped embeddings endpoints

Finding H6. The routes move first because the migration path for Task 7's signature bump is `reindex`, and `reindex` cannot resolve a store today.

**Files:**
- Create: `src/Scrinia.Core/Embeddings/IEmbeddingsAdmin.cs`
- Create: `src/Scrinia.Server/Endpoints/EmbeddingsEndpoints.cs`
- Modify: `src/Scrinia.Server/Program.cs:529-581` (delete the inline block, add the map call)
- Modify: `src/Scrinia.Server/Services/BuiltInEmbeddingsService.cs:13-14` (implement the interface)
- Test: `tests/Scrinia.Server.Tests/EmbeddingsEndpointsTests.cs`

**Interfaces:**
- Produces: `IEmbeddingsAdmin` with `ProviderName`, `IsAvailable`, `Dimensions`, `SemanticWeight`, `TotalVectorCount`, `Hardware`, `ReindexStoreAsync(IMemoryStore, CancellationToken) → Task<int>`. Tasks 2 and 5 depend on this exact shape.
- Produces: `EmbeddingsEndpoints.MapEmbeddingsEndpoints(this WebApplication app)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Server.Tests/EmbeddingsEndpointsTests.cs`. No `using Xunit;` — the project has a global using.

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Scrinia.Server.Tests;

/// <summary>
/// Finding H6. The embeddings routes lived at /api/v1/plugins/embeddings, a group with no
/// {store} route value — so neither MemoryStoreContext.Current (set only in the MCP session
/// flow) nor RequestContext.Store (set only inside the {store} branch) could ever be non-null,
/// and reindex returned 400 unconditionally. These tests pin the store-scoped replacement.
/// </summary>
public sealed class EmbeddingsEndpointsTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;

    public EmbeddingsEndpointsTests(ScriniaServerFactory factory) => _factory = factory;

    [Fact]
    public async Task Status_UnderStoreScopedPath_Answers200()
    {
        var client = _factory.CreateAuthenticatedClient(permissions: ["read"]);

        var resp = await client.GetAsync("/api/v1/stores/default/embeddings/status");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reindex_WithPermission_ResolvesTheStore()
    {
        var client = _factory.CreateAuthenticatedClient(permissions: ["reindex"]);

        var resp = await client.PostAsync("/api/v1/stores/default/embeddings/reindex", null);

        // The precise success shape depends on whether a provider is available in the test
        // environment; what must never happen again is the unconditional "no store context".
        string body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain("No store context available",
            because: "the {store} route value is what resolves the store — finding H6");
    }

    [Fact]
    public async Task Reindex_WithoutPermission_Is403()
    {
        var client = _factory.CreateAuthenticatedClient(permissions: ["read"]);

        var resp = await client.PostAsync("/api/v1/stores/default/embeddings/reindex", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RetiredPluginPath_NoLongerResolves()
    {
        var client = _factory.CreateAuthenticatedClient(permissions: ["reindex"]);

        var resp = await client.PostAsync("/api/v1/plugins/embeddings/reindex", null);

        resp.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }
}
```

Read `tests/Scrinia.Server.Tests/ScriniaServerFactory.cs` before writing these — use whatever authenticated-client helper it already exposes and match its parameter names. If it has no per-permission helper, add one there rather than hand-building headers in this file.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Server.Tests --filter "FullyQualifiedName~EmbeddingsEndpointsTests"`

Expected: FAIL — 404 on the store-scoped paths, because they do not exist yet.

- [ ] **Step 3: Create the DI abstraction**

Create `src/Scrinia.Core/Embeddings/IEmbeddingsAdmin.cs`:

```csharp
namespace Scrinia.Core.Embeddings;

/// <summary>
/// What the embeddings REST endpoints need, independent of which implementation provides it.
/// Implemented by the server's built-in Model2Vec service and by the Vulkan plugin, so one
/// endpoint file serves both and the plugin no longer maps a second copy of the same routes.
///
/// <para>Finding H6: the previous arrangement had each implementation map its own routes into
/// an inline /api/v1/plugins group that carried no {store} route value, so no request could
/// ever resolve a store and every reindex returned 400.</para>
/// </summary>
public interface IEmbeddingsAdmin
{
    /// <summary>Display name of the active provider, e.g. "Model2VecEmbeddingProvider".</summary>
    string ProviderName { get; }

    /// <summary>Whether the provider can currently embed.</summary>
    bool IsAvailable { get; }

    /// <summary>Vector dimensionality, or 0 when unavailable.</summary>
    int Dimensions { get; }

    /// <summary>Weight applied to cosine similarity in hybrid scoring.</summary>
    double SemanticWeight { get; }

    /// <summary>Total vectors held across all scopes.</summary>
    int TotalVectorCount { get; }

    /// <summary>Free-text hardware label surfaced in the web UI, e.g. "CPU" or "Vulkan GPU".</summary>
    string Hardware { get; }

    /// <summary>Re-embeds every persistent memory in the store. Returns the number reindexed.</summary>
    Task<int> ReindexStoreAsync(IMemoryStore store, CancellationToken ct);
}
```

- [ ] **Step 4: Implement it on the built-in service**

In `src/Scrinia.Server/Services/BuiltInEmbeddingsService.cs`, extend the declaration at `:13-14`:

```csharp
public sealed class BuiltInEmbeddingsService :
    ISearchScoreContributor, IMemoryEventSink, IMemoryOperationHook, IEmbeddingsAdmin
```

Every member already exists except `Hardware`. Add it beside the other properties at `:36-40`:

```csharp
public string Hardware => "CPU";
```

- [ ] **Step 5: Create the endpoint file**

Create `src/Scrinia.Server/Endpoints/EmbeddingsEndpoints.cs`:

```csharp
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Server.Auth;
using Scrinia.Server.Models;

namespace Scrinia.Server.Endpoints;

/// <summary>
/// Embeddings status, settings and reindex — under the {store}-scoped group so the auth
/// middleware resolves RequestContext.Store before the handler runs (finding H6), and under
/// Endpoints/ so the endpoint conventions apply to it like every other group.
/// </summary>
public static class EmbeddingsEndpoints
{
    public static void MapEmbeddingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/stores/{store}/embeddings")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        group.MapGet("/status", GetStatus);
        group.MapGet("/settings", GetSettings);
        group.MapPost("/reindex", Reindex);
    }

    private static IResult GetStatus(RequestContext ctx, IEmbeddingsAdmin? admin)
    {
        if (!ctx.HasPermission("read"))
            return Results.Json(new ErrorResponse("Permission 'read' required."), statusCode: 403);

        if (admin is null)
            return Results.Ok(new EmbeddingsStatusResponse("none", "", false, 0, 0.0, 0));

        return Results.Ok(new EmbeddingsStatusResponse(
            admin.ProviderName, admin.Hardware, admin.IsAvailable,
            admin.Dimensions, admin.SemanticWeight, admin.TotalVectorCount));
    }

    private static IResult GetSettings(RequestContext ctx, IEmbeddingsAdmin? admin)
    {
        if (!ctx.HasPermission("read"))
            return Results.Json(new ErrorResponse("Permission 'read' required."), statusCode: 403);

        if (admin is null)
            return Results.Ok(new EmbeddingsSettingsResponse("none", "", 0.0));

        return Results.Ok(new EmbeddingsSettingsResponse(
            admin.ProviderName, admin.Hardware, admin.SemanticWeight));
    }

    private static async Task<IResult> Reindex(
        string store, RequestContext ctx, IEmbeddingsAdmin? admin, CancellationToken ct)
    {
        // A full re-embed of every memory in the store is unbounded work, so it gets its own
        // permission rather than riding on 'store'. The hand-written gate matches the ~30 in
        // MemoryEndpoints.cs; consolidating them all is finding M5, branch 6.
        if (!ctx.HasPermission("reindex"))
            return Results.Json(new ErrorResponse("Permission 'reindex' required."), statusCode: 403);

        if (!MemoryEndpoints.IsWritable(ctx, store))
            return Results.Json(new ErrorResponse($"Store '{store}' is read-only for this key."), statusCode: 403);

        if (admin is null || !admin.IsAvailable)
            return Results.BadRequest(new ErrorResponse("Embedding provider is not available."));

        // ctx.Store is resolved by the auth middleware from the {store} route value. That is
        // the whole point of this move — the previous inline group had no {store} segment.
        if (ctx.Store is null)
            return Results.BadRequest(new ErrorResponse("No store context available."));

        int count = await admin.ReindexStoreAsync(ctx.Store, ct);
        return Results.Ok(new ReindexResponse($"Reindexed {count} memories."));
    }
}
```

Add the four response records to `src/Scrinia.Server/Models/` in whichever file the neighbouring endpoint responses live in (find it by grepping for `record ErrorResponse`), and register them in `ServerJsonContext` alongside their neighbours:

```csharp
public sealed record EmbeddingsStatusResponse(
    string Provider, string Hardware, bool Available, int Dimensions,
    double SemanticWeight, int VectorCount);

public sealed record EmbeddingsSettingsResponse(
    string Provider, string Hardware, double SemanticWeight);

public sealed record ReindexResponse(string Message);
```

- [ ] **Step 6: Delete the inline block and map the new group**

In `src/Scrinia.Server/Program.cs`, delete lines `541-581` entirely — the whole `if (!pluginProvidesEmbeddings) { var embGroup = … }` block. Add the map call beside its siblings at `:529-532`:

```csharp
app.MapMemoryEndpoints();
app.MapKeyEndpoints();
app.MapHealthEndpoints();
app.MapEmbeddingsEndpoints();
app.MapChatEndpoints(app.Services.GetRequiredService<Scrinia.Server.Chat.ChatOptions>());
```

Then register the abstraction inside the built-in embeddings block, beside the registrations at `:160-167`:

```csharp
builder.Services.AddSingleton<IEmbeddingsAdmin>(sp =>
    sp.GetRequiredService<BuiltInEmbeddingsService>());
```

Leave `pluginProvidesEmbeddings` (`:119-120`) exactly as it is — Task 5 is what makes the plugin register `IEmbeddingsAdmin`.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Server.Tests --filter "FullyQualifiedName~EmbeddingsEndpointsTests"`

Expected: PASS, 4 of 4.

- [ ] **Step 8: Run the full server suite for regressions**

Run: `dotnet test tests/Scrinia.Server.Tests`

Expected: PASS. Any test asserting the old `/api/v1/plugins/embeddings` paths must be updated to the new ones, not deleted.

- [ ] **Step 9: Commit**

```bash
git add src/Scrinia.Core/Embeddings/IEmbeddingsAdmin.cs \
        src/Scrinia.Server/Endpoints/EmbeddingsEndpoints.cs \
        src/Scrinia.Server/Program.cs \
        src/Scrinia.Server/Services/BuiltInEmbeddingsService.cs \
        src/Scrinia.Server/Models/ \
        tests/Scrinia.Server.Tests/EmbeddingsEndpointsTests.cs
git commit -m "fix(server): give the embeddings routes a store to resolve"
```

---

## Task 2: Web client follows the routes, and loses a button that never worked

Finding H6's client half, plus the defect found during design: `SettingsPage`'s Save control PUTs a route no server maps.

**Files:**
- Modify: `web/src/api/client.ts:274-293`
- Modify: `web/src/api/types.ts:155-180`
- Modify: `web/src/pages/SettingsPage.tsx`

**Interfaces:**
- Consumes: the routes from Task 1 — `/api/v1/stores/{store}/embeddings/{status,settings,reindex}`, and the response shapes `EmbeddingsStatusResponse` / `EmbeddingsSettingsResponse` / `ReindexResponse` defined there.

- [ ] **Step 1: Update the client functions**

In `web/src/api/client.ts`, replace lines `274-293` with:

```ts
export function getEmbeddingsStatus(store: string) {
  return apiFetch<EmbeddingsStatus>(`${API_BASE}/stores/${store}/embeddings/status`);
}

export function getEmbeddingsSettings(store: string) {
  return apiFetch<EmbeddingsSettings>(`${API_BASE}/stores/${store}/embeddings/settings`);
}

export function reindexEmbeddings(store: string) {
  return apiFetch<{ message: string }>(`${API_BASE}/stores/${store}/embeddings/reindex`, {
    method: 'POST',
  });
}
```

`updateEmbeddingsSettings` is deleted. So is `validateEmbeddingsSettings` and its `SettingsValidation` type (`client.ts:255-272` and the interface above it) — it exists only to validate the payload of the deleted call.

- [ ] **Step 2: Update the types**

In `web/src/api/types.ts`, replace lines `157-180`:

```ts
export interface EmbeddingsStatus {
  provider: string;
  hardware: string;
  available: boolean;
  dimensions: number;
  semanticWeight: number;
  vectorCount: number;
}

export interface EmbeddingsSettings {
  provider: string;
  hardware: string;
  semanticWeight: number;
}
```

`EmbeddingsSettingsUpdate` is deleted. The dropped `maxBatchSize`, `ollamaBaseUrl`, `ollamaModel`, `openAiModel` and `openAiBaseUrl` fields were never populated by either server handler — both `MapGet("/settings")` implementations returned only provider, semanticWeight and (built-in only) a hardcoded `maxBatchSize = 1`.

- [ ] **Step 3: Update the settings page**

In `web/src/pages/SettingsPage.tsx`:

Resolve the active store the same way `AgentChatPage.tsx:24-37` does, since the queries are now store-scoped:

```tsx
const { data: health } = useQuery({
  queryKey: ['health'],
  queryFn: getHealth,
  refetchInterval: 30_000,
})

const stores = health?.checks
  ?.filter((c) => c.name.startsWith('store:'))
  .map((c) => c.name.replace('store:', '')) ?? []

const store = stores.length === 0 || stores.includes('default') ? 'default' : stores[0]!
```

Thread `store` through the two queries and the mutation, and key the caches on it:

```tsx
const { data: status, isLoading: statusLoading, error: statusError } = useQuery({
  queryKey: ['embeddings-status', store],
  queryFn: () => getEmbeddingsStatus(store),
  refetchInterval: 30_000,
})

const { data: settings, isLoading: settingsLoading, error: settingsError } = useQuery({
  queryKey: ['embeddings-settings', store],
  queryFn: () => getEmbeddingsSettings(store),
})

const reindexMutation = useMutation({
  mutationFn: () => reindexEmbeddings(store),
  onSuccess: () => {
    queryClient.invalidateQueries({ queryKey: ['embeddings-status', store] })
  },
})
```

Delete `saveMutation` (`:21-27`), the `Save` import from `lucide-react` (`:5`), the two editable inputs and their `useState` backing, and every reference to `validateEmbeddingsSettings`. Render `semanticWeight` as read-only text beside the other status fields, and add one line stating where the value comes from:

```tsx
<p className="text-gray-500 text-xs mt-2">
  Embedding settings are read from configuration. Change them with{' '}
  <code>scri config Scrinia:Embeddings:*</code> and restart the server.
</p>
```

The Save button is removed rather than fixed because the server has no configuration-write mechanism — `Scrinia:Embeddings` is bound from configuration and read-only at runtime, so honouring the button would mean building one. Today the PUT falls through to `app.MapFallbackToFile("index.html")` and returns HTML.

- [ ] **Step 4: Typecheck and build the web client**

Run: `cd web && npm run build`

Expected: PASS with no TypeScript errors. A `TS6133`/`TS2304` here means a deleted symbol is still referenced — find it and remove the reference rather than re-adding the symbol.

- [ ] **Step 5: Commit**

```bash
git add web/src/api/client.ts web/src/api/types.ts web/src/pages/SettingsPage.tsx
git commit -m "fix(web): follow the embeddings routes, and drop a Save button with no server route"
```

---

## Task 3: `MemoryVectorIndexer` and `VectorScoreProjection`

Finding H3's core. This task creates the shared policy and proves the contract; Tasks 4 and 5 route callers through it.

**Files:**
- Create: `src/Scrinia.Core/Embeddings/MemoryVectorIndexer.cs`
- Create: `src/Scrinia.Core/Embeddings/VectorScoreProjection.cs`
- Test: `tests/Scrinia.Tests/Embeddings/MemoryVectorIndexerTests.cs`
- Test: `tests/Scrinia.Tests/Embeddings/VectorScoreProjectionTests.cs`
- Test: `tests/Scrinia.Tests/Embeddings/IndexerScorerContractTests.cs`

**Interfaces:**
- Consumes: `TextChunker.SliceWindows(string, int, int) → IReadOnlyList<(int Index, string Text)>`; `VectorStore.UpsertAsync(string scope, string name, int? chunkIndex, float[] vector, CancellationToken)`; `VectorStore.RemoveAsync(string scope, string name, CancellationToken)`; `VectorWriteRetry.RunAsync(Func<Task>, ILogger?, string, CancellationToken) → Task<bool>`; `IEmbeddingProvider.EmbedBatchAsync(IReadOnlyList<string>, CancellationToken) → Task<float[][]?>`; `VectorEntry(string Name, int? ChunkIndex, float[] Vector)`.
- Produces: `MemoryVectorIndexer` with `ChunkIndexStride`, `ElementIndexOf(int)`, `IndexAsync`, `RemoveAsync`. Tasks 4, 5, 6 and 7 all depend on these exact names.
- Produces: `VectorScoreProjection.Project(string scope, IEnumerable<(VectorEntry Entry, double Similarity)> hits, double weight, IDictionary<string, double> into)`.
- Produces: `Nmp2ChunkedEncoder.DecodeChunks(string artifact) → string[]`, 1-based order preserved. Every reindex path in Tasks 4 and 5 depends on it.

- [ ] **Step 1: Write the failing indexer tests**

Create `tests/Scrinia.Tests/Embeddings/MemoryVectorIndexerTests.cs`. Match the neighbouring files in `tests/Scrinia.Tests/Embeddings/` for `using` conventions.

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core.Embeddings;

namespace Scrinia.Tests.Embeddings;

/// <summary>
/// Finding H3. Six in-process copies of embed-and-index disagreed on the chunking unit: Core
/// and Server windowed by character and stamped 0-based window ordinals; the plugin never
/// windowed and stamped 1-based content-element indices. This class pins the one policy that
/// replaces them — elements are the addressable unit, windows nest inside them.
/// </summary>
public sealed class MemoryVectorIndexerTests
{
    /// <summary>Returns a distinct unit vector per input so callers can tell vectors apart.</summary>
    private sealed class CountingProvider : IEmbeddingProvider
    {
        public int BatchCalls { get; private set; }
        public List<string> LastTexts { get; private set; } = [];
        public bool IsAvailable => true;
        public int Dimensions => 2;
        public string Signature => "test:counting";

        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>([1f, 0f]);

        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            BatchCalls++;
            LastTexts = [.. texts];
            return Task.FromResult<float[][]?>([.. texts.Select((_, i) => new[] { 1f, (float)i })]);
        }
    }

    /// <summary>Returns one fewer vector than asked for — the count-guard case.</summary>
    private sealed class ShortProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 2;
        public string Signature => "test:short";
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>([1f, 0f]);
        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult<float[][]?>([.. texts.Skip(1).Select(_ => new[] { 1f, 0f })]);
    }

    private static (MemoryVectorIndexer Indexer, VectorStore Store, string Dir) NewIndexer(
        IEmbeddingProvider provider, EmbeddingOptions? options = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-mvi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new VectorStore(dir);
        var indexer = new MemoryVectorIndexer(
            provider, store, NullLogger.Instance, options ?? new EmbeddingOptions());
        return (indexer, store, dir);
    }

    [Fact]
    public async Task ShortElements_ProduceOneVectorEach_KeyedByOneBasedElementIndex()
    {
        var (indexer, store, _) = NewIndexer(new CountingProvider());

        await indexer.IndexAsync("local", "note", ["alpha", "beta", "gamma"], default);

        var vectors = store.GetVectors("local");
        vectors.Should().HaveCount(3);
        vectors.Select(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value))
            .Should().BeEquivalentTo([1, 2, 3],
                because: "content elements are the addressable unit, 1-based, matching ChunkEntry");
    }

    [Fact]
    public async Task LongSingleElement_IsWindowed_IntoSeveralVectorsUnderOneElementIndex()
    {
        var options = new EmbeddingOptions { ChunkSize = 100, ChunkOverlap = 20 };
        var (indexer, store, _) = NewIndexer(new CountingProvider(), options);
        string longText = new string('a', 500);

        await indexer.IndexAsync("local", "note", [longText], default);

        var vectors = store.GetVectors("local");
        vectors.Should().HaveCountGreaterThan(1,
            because: "a 500-char element at ChunkSize=100 must not collapse to one mean-pooled vector "
                   + "— that is the Vulkan-plugin regression H3 describes");
        vectors.Select(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value))
            .Distinct().Should().Equal(1);
    }

    [Fact]
    public async Task WindowsWithinOneElement_DoNotOverwriteEachOther()
    {
        var options = new EmbeddingOptions { ChunkSize = 100, ChunkOverlap = 20 };
        var (indexer, store, _) = NewIndexer(new CountingProvider(), options);

        await indexer.IndexAsync("local", "note", [new string('a', 500)], default);

        var vectors = store.GetVectors("local");
        vectors.Select(v => v.ChunkIndex).Should().OnlyHaveUniqueItems(
            because: "VectorStore.UpsertAsync dedupes on (name, chunkIndex), so two windows "
                   + "sharing an index would silently collapse to the last one written");
    }

    [Fact]
    public async Task ChunkCountAboveCap_TruncatesTheTail()
    {
        var options = new EmbeddingOptions { ChunkSize = 100, ChunkOverlap = 20, MaxChunksPerMemory = 3 };
        var (indexer, store, _) = NewIndexer(new CountingProvider(), options);

        await indexer.IndexAsync("local", "note", [new string('a', 5000)], default);

        store.GetVectors("local").Should().HaveCount(3);
    }

    [Fact]
    public async Task MismatchedVectorCount_LeavesExistingVectorsIntact()
    {
        var (good, store, dir) = NewIndexer(new CountingProvider());
        await good.IndexAsync("local", "note", ["alpha", "beta"], default);
        int before = store.GetVectors("local").Count;

        var bad = new MemoryVectorIndexer(
            new ShortProvider(), store, NullLogger.Instance, new EmbeddingOptions());
        await bad.IndexAsync("local", "note", ["alpha", "beta"], default);

        store.GetVectors("local").Should().HaveCount(before,
            because: "the guard must return before RemoveAsync, or a miscounting provider "
                   + "wipes the memory's vectors and writes nothing back");
    }

    [Fact]
    public async Task Reindexing_ReplacesRatherThanAccumulates()
    {
        var (indexer, store, _) = NewIndexer(new CountingProvider());

        await indexer.IndexAsync("local", "note", ["alpha", "beta", "gamma"], default);
        await indexer.IndexAsync("local", "note", ["alpha"], default);

        store.GetVectors("local").Should().HaveCount(1,
            because: "remove-then-upsert is what stops a shrunk memory keeping stale higher chunks");
    }

    [Fact]
    public async Task BlankElements_AreSkippedWithoutShiftingLaterIndices()
    {
        var (indexer, store, _) = NewIndexer(new CountingProvider());

        await indexer.IndexAsync("local", "note", ["alpha", "   ", "gamma"], default);

        var vectors = store.GetVectors("local");
        vectors.Select(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value))
            .Should().BeEquivalentTo([1, 3],
                because: "element index is the memory's own addressing — a blank element still "
                       + "occupies its slot, and ChunkEntry numbering does not renumber around it");
    }

    [Fact]
    public void Construction_RejectsACapThatCouldOverflowTheStride()
    {
        var options = new EmbeddingOptions { MaxChunksPerMemory = MemoryVectorIndexer.ChunkIndexStride };
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-mvi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        Action act = () => new MemoryVectorIndexer(
            new CountingProvider(), new VectorStore(dir), NullLogger.Instance, options);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
```

- [ ] **Step 2: Write the failing projection tests**

Create `tests/Scrinia.Tests/Embeddings/VectorScoreProjectionTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Embeddings.Models;

namespace Scrinia.Tests.Embeddings;

/// <summary>
/// Finding H3, read side. Four hand-copied projection loops each decided the key contract for
/// themselves; one of them (EmbeddingsTools.Search) did not max-aggregate at all, so a chunk
/// hit overwrote the memory-level score instead of raising it.
/// </summary>
public sealed class VectorScoreProjectionTests
{
    [Fact]
    public void EmitsBothTheMemoryKeyAndTheElementKey()
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        // element 2, window 0
        int chunk = 2 * MemoryVectorIndexer.ChunkIndexStride;

        VectorScoreProjection.Project(
            "local", [(new VectorEntry("note", chunk, [1f]), 0.5)], weight: 100.0, into: scores);

        scores.Should().ContainKey("local|note");
        scores.Should().ContainKey("local|note|2",
            because: "the scorer's chunk pass probes the ELEMENT index, not the window ordinal");
    }

    [Fact]
    public void ElementKey_TakesTheMaxOverThatElementsWindows()
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        int w0 = 1 * MemoryVectorIndexer.ChunkIndexStride + 0;
        int w1 = 1 * MemoryVectorIndexer.ChunkIndexStride + 1;

        VectorScoreProjection.Project("local", [
            (new VectorEntry("note", w0, [1f]), 0.2),
            (new VectorEntry("note", w1, [1f]), 0.9),
        ], weight: 100.0, into: scores);

        scores["local|note|1"].Should().BeApproximately(90.0, 1e-9);
    }

    [Fact]
    public void MemoryKey_TakesTheMaxAcrossEveryElement()
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        int e1 = 1 * MemoryVectorIndexer.ChunkIndexStride;
        int e2 = 2 * MemoryVectorIndexer.ChunkIndexStride;

        VectorScoreProjection.Project("local", [
            (new VectorEntry("note", e1, [1f]), 0.3),
            (new VectorEntry("note", e2, [0f]), 0.8),
        ], weight: 100.0, into: scores);

        scores["local|note"].Should().BeApproximately(80.0, 1e-9);
    }

    [Fact]
    public void NullChunkIndex_ContributesToTheMemoryKeyOnly()
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        VectorScoreProjection.Project(
            "local", [(new VectorEntry("note", null, [1f]), 0.4)], weight: 100.0, into: scores);

        scores.Should().ContainKey("local|note");
        scores.Keys.Should().NotContain(k => k.Count(c => c == '|') == 2,
            because: "vectors written before this branch carry a null chunk index and have no element");
    }
}
```

- [ ] **Step 3: Write the failing contract test**

This is the load-bearing proof. Create `tests/Scrinia.Tests/Embeddings/IndexerScorerContractTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Search;

namespace Scrinia.Tests.Embeddings;

/// <summary>
/// The finding-H3 conformance proof, and the one test that fails on every pre-branch path.
///
/// <para>WeightedAttributeScorer's chunk pass probes "{scope}|{name}|{chunkIndex}" where
/// chunkIndex comes from ChunkEntry — 1-based over nmp2 content elements (TextAnalysis
/// emits `ChunkIndex: i + 1`). Before this branch, Core and Server wrote 0-based CHAR-WINDOW
/// ordinals into that key position, so the probe could never hit; the plugin wrote matching
/// element indices but never windowed, so a long element became one mean-pooled vector.
/// This asserts both halves at once.</para>
/// </summary>
public sealed class IndexerScorerContractTests
{
    private sealed class OrdinalProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 2;
        public string Signature => "test:ordinal";
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>([1f, 0f]);
        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult<float[][]?>([.. texts.Select((_, i) => new[] { 1f, (float)i })]);
    }

    [Fact]
    public async Task EveryKeyTheScorerProbes_IsAKeyTheIndexerProduces()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new VectorStore(dir);
        var options = new EmbeddingOptions { ChunkSize = 100, ChunkOverlap = 20 };
        var indexer = new MemoryVectorIndexer(new OrdinalProvider(), store, NullLogger.Instance, options);

        // Three elements; the second is long enough to window several times.
        string[] content = ["short one", new string('b', 400), "short three"];
        await indexer.IndexAsync("local", "note", content, default);

        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var hits = store.GetVectors("local").Select(v => (Entry: v, Similarity: 0.5)).ToList();
        VectorScoreProjection.Project("local", hits, weight: 100.0, into: scores);

        using var scope = new FluentAssertions.Execution.AssertionScope();

        scores.Should().ContainKey("local|note");
        for (int elementIndex = 1; elementIndex <= content.Length; elementIndex++)
        {
            scores.Should().ContainKey($"local|note|{elementIndex}",
                because: "TextAnalysis stamps ChunkEntry.ChunkIndex as i+1, and "
                       + "WeightedAttributeScorer.GetSupplemental probes exactly that value");
        }

        // And the long element really was windowed rather than mean-pooled over 400 chars.
        store.GetVectors("local")
            .Count(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value) == 2)
            .Should().BeGreaterThan(1);
    }
}
```

- [ ] **Step 4: Run all three test files to verify they fail**

Run: `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~MemoryVectorIndexerTests|FullyQualifiedName~VectorScoreProjectionTests|FullyQualifiedName~IndexerScorerContractTests"`

Expected: FAIL to compile — `MemoryVectorIndexer` and `VectorScoreProjection` do not exist.

- [ ] **Step 5: Implement the indexer**

Create `src/Scrinia.Core/Embeddings/MemoryVectorIndexer.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// The single embed-and-index policy. Owns availability check, windowing, the per-memory chunk
/// cap, batching, the vector-count guard, remove-then-upsert ordering, and write retry.
///
/// <para><b>The chunk model.</b> A memory's content array is its addressable unit: element
/// index, 1-based, the same unit <c>ChunkEntry.ChunkIndex</c> and the REST contract use. Each
/// element is sliced into overlapping windows by <see cref="TextChunker"/>; every window is
/// embedded and stored. Windows are an implementation detail — they never appear in a
/// supplemental score key, which is what <see cref="VectorScoreProjection"/> guarantees.</para>
///
/// <para><b>Why the stored chunk index is two-level.</b>
/// <see cref="VectorStore.UpsertAsync"/> deduplicates on <c>(name, chunkIndex)</c>, so two
/// windows of the same element sharing an index would silently collapse to whichever was
/// written last. The stored index is therefore
/// <c>elementIndex * <see cref="ChunkIndexStride"/> + windowOrdinal</c>, and
/// <see cref="ElementIndexOf"/> recovers the element by integer division.
/// <see cref="EmbeddingOptions.MaxChunksPerMemory"/> caps total windows per memory well below
/// the stride, which the constructor asserts. The alternative — a second field on
/// <see cref="Models.VectorEntry"/> — would change the SVF3 binary format, its op-log replay
/// and its compaction, which is a larger change than this branch should carry.</para>
///
/// <para>Replaces six divergent copies (finding H3): EmbeddingReindexer.ReindexAsync,
/// CoreEmbeddingEventHandler, BuiltInEmbeddingsService's write and reindex paths, and
/// EmbeddingsPlugin's write and reindex paths — the last two of which never windowed at all.</para>
/// </summary>
public sealed class MemoryVectorIndexer
{
    /// <summary>Multiplier separating the element index from the window ordinal in a stored chunk index.</summary>
    public const int ChunkIndexStride = 1000;

    private readonly IEmbeddingProvider _provider;
    private readonly VectorStore _store;
    private readonly ILogger _logger;
    private readonly EmbeddingOptions _options;

    public MemoryVectorIndexer(
        IEmbeddingProvider provider, VectorStore store, ILogger logger, EmbeddingOptions? options = null)
    {
        _provider = provider;
        _store = store;
        _logger = logger;
        _options = options ?? new EmbeddingOptions();

        if (_options.MaxChunksPerMemory >= ChunkIndexStride)
            throw new ArgumentOutOfRangeException(
                nameof(options), _options.MaxChunksPerMemory,
                $"MaxChunksPerMemory must be below ChunkIndexStride ({ChunkIndexStride}); "
                + "otherwise one element's windows could overflow into the next element's index space.");
    }

    /// <summary>Recovers the 1-based content-element index from a stored chunk index.</summary>
    public static int ElementIndexOf(int storedChunkIndex) => storedChunkIndex / ChunkIndexStride;

    /// <summary>
    /// Embeds <paramref name="content"/> and replaces every vector held for
    /// <paramref name="subject"/> in <paramref name="scope"/>. Returns false when nothing was
    /// written — provider unavailable, no embeddable text, or the count guard tripped.
    /// </summary>
    public async Task<bool> IndexAsync(
        string scope, string subject, string[] content, CancellationToken ct)
    {
        if (!_provider.IsAvailable) return false;
        if (content.Length == 0) return false;

        // (storedChunkIndex, text), in element order.
        var plan = new List<(int ChunkIndex, string Text)>();
        for (int i = 0; i < content.Length; i++)
        {
            string element = content[i];
            if (string.IsNullOrWhiteSpace(element)) continue;

            // Element index is 1-based and never renumbers around a blank element: it is the
            // memory's own addressing, and ChunkEntry numbers the same way.
            int elementIndex = i + 1;
            var windows = TextChunker.SliceWindows(element, _options.ChunkSize, _options.ChunkOverlap);
            foreach (var (ordinal, text) in windows)
                plan.Add((elementIndex * ChunkIndexStride + ordinal, text));
        }

        if (plan.Count == 0) return false;

        if (plan.Count > _options.MaxChunksPerMemory)
        {
            _logger.LogWarning(
                "Indexing {Scope}/{Name}: {Count} chunks exceeds cap {Cap}; tail dropped from embed "
                + "(BM25 still covers it).",
                scope, subject, plan.Count, _options.MaxChunksPerMemory);
            plan = [.. plan.Take(_options.MaxChunksPerMemory)];
        }

        var vectors = await _provider.EmbedBatchAsync([.. plan.Select(p => p.Text)], ct);

        // The guard must return BEFORE RemoveAsync. A provider returning the wrong count would
        // otherwise wipe this memory's vectors and write nothing back, leaving it unsearchable
        // with only a log line.
        if (vectors is null || vectors.Length != plan.Count)
        {
            if (vectors is not null)
                _logger.LogWarning(
                    "Embed returned {Got} vectors for {Want} inputs for '{Scope}/{Name}'; "
                    + "skipping to preserve existing vectors.",
                    vectors.Length, plan.Count, scope, subject);
            return false;
        }

        // Atomic per-memory replace: clearing first is what stops a shrunk memory keeping stale
        // higher chunk indices. If the upsert loop is interrupted the next index pass rebuilds
        // this memory cleanly.
        return await VectorWriteRetry.RunAsync(
            async () =>
            {
                await _store.RemoveAsync(scope, subject, ct);
                for (int i = 0; i < plan.Count; i++)
                    await _store.UpsertAsync(scope, subject, plan[i].ChunkIndex, vectors[i], ct);
            },
            _logger, $"{scope}/{subject}", ct);
    }

    /// <summary>Drops every vector held for <paramref name="subject"/> in <paramref name="scope"/>.</summary>
    public async Task RemoveAsync(string scope, string subject, CancellationToken ct)
    {
        try
        {
            await _store.RemoveAsync(scope, subject, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove vectors for '{Scope}/{Name}'", scope, subject);
        }
    }
}
```

- [ ] **Step 6: Implement the projection**

Create `src/Scrinia.Core/Embeddings/VectorScoreProjection.cs`:

```csharp
using Scrinia.Core.Embeddings.Models;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// The single read-side projection from vector hits to supplemental score keys.
///
/// <para><see cref="Search.WeightedAttributeScorer"/> probes exactly two key shapes
/// (<c>GetSupplemental</c>): <c>"{scope}|{name}"</c> for its whole-memory pass and
/// <c>"{scope}|{name}|{chunkIndex}"</c> for its chunk pass, where <c>chunkIndex</c> is
/// <c>ChunkEntry.ChunkIndex</c> — 1-based over content elements. Window ordinals never appear
/// in a key; <see cref="MemoryVectorIndexer.ElementIndexOf"/> maps a stored chunk index back to
/// its element, and both keys carry the maximum over the vectors that roll up into them
/// ("the best-matching window's score wins").</para>
///
/// <para>Replaces four hand-copied loops (finding H3), one of which — the plugin's MCP search
/// tool — did not aggregate at all, so a low-scoring chunk hit could overwrite a high-scoring
/// memory-level score rather than lose to it.</para>
/// </summary>
public static class VectorScoreProjection
{
    public static void Project(
        string scope,
        IEnumerable<(VectorEntry Entry, double Similarity)> hits,
        double weight,
        IDictionary<string, double> into)
    {
        foreach (var (entry, similarity) in hits)
        {
            double score = similarity * weight;

            string memoryKey = $"{scope}|{entry.Name}";
            if (!into.TryGetValue(memoryKey, out double bestForMemory) || score > bestForMemory)
                into[memoryKey] = score;

            if (entry.ChunkIndex is not { } stored) continue;

            // Vectors written before this branch carry a raw ordinal (or null). Anything below
            // one stride has no element, so it contributes to the memory key only.
            int elementIndex = MemoryVectorIndexer.ElementIndexOf(stored);
            if (elementIndex == 0) continue;

            string elementKey = $"{scope}|{entry.Name}|{elementIndex}";
            if (!into.TryGetValue(elementKey, out double bestForElement) || score > bestForElement)
                into[elementKey] = score;
        }
    }
}
```

- [ ] **Step 7: Add `Nmp2ChunkedEncoder.DecodeChunks`**

Every reindex path (Tasks 4, 5 and `ReindexViaSinkAsync`) reads a stored artifact rather than an
in-flight content array. If those paths pass one decoded blob, every reindexed memory lands under
element index 1 and the element contract holds only for freshly-written memories — which would
make the whole branch's guarantee conditional on write order. The elements are recoverable:
`GetChunkCount` plus `DecodeChunk` already do it 1-based for `GET /memories/{name}/chunks/{i}`.

Add to `src/Scrinia.Core/Encoding/Nmp2ChunkedEncoder.cs`, beside `DecodeChunk` at `:290`:

```csharp
/// <summary>
/// Decodes every chunk of <paramref name="artifact"/> in order, reconstructing the content
/// array the memory was written with. Single-chunk artifacts yield a one-element array.
///
/// <para>Used by the reindex paths, which read a stored artifact rather than an in-flight
/// content array: without this they would collapse a multi-element memory to element 1 and
/// the chunk-level supplemental keys would exist only for memories written since.</para>
/// </summary>
public static string[] DecodeChunks(string artifact)
{
    int count = GetChunkCount(artifact);
    if (count <= 0) return [];

    var chunks = new string[count];
    for (int i = 1; i <= count; i++)
        chunks[i - 1] = DecodeChunk(artifact, i);
    return chunks;
}
```

Add a test to whichever file already covers `Nmp2ChunkedEncoder` (grep the test tree for
`DecodeChunk` before creating a new file):

```csharp
[Fact]
public void DecodeChunks_RoundTripsTheContentArray()
{
    string[] original = ["first element", "second element", "third element"];

    Nmp2ChunkedEncoder.DecodeChunks(Nmp2ChunkedEncoder.EncodeChunks(original))
        .Should().Equal(original);
}

[Fact]
public void DecodeChunks_SingleChunkArtifact_YieldsOneElement()
{
    Nmp2ChunkedEncoder.DecodeChunks(Nmp2ChunkedEncoder.Encode("just one"))
        .Should().Equal(["just one"]);
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~MemoryVectorIndexerTests|FullyQualifiedName~VectorScoreProjectionTests|FullyQualifiedName~IndexerScorerContractTests|FullyQualifiedName~Nmp2Chunked"`

Expected: PASS, 15 of 15.

- [ ] **Step 9: Commit**

```bash
git add src/Scrinia.Core/Embeddings/MemoryVectorIndexer.cs \
        src/Scrinia.Core/Embeddings/VectorScoreProjection.cs \
        src/Scrinia.Core/Encoding/Nmp2ChunkedEncoder.cs \
        tests/Scrinia.Tests/
git commit -m "feat(embeddings): one indexer and one projection, with elements as the chunk unit"
```

---

## Task 4: Route the Core, CLI and Server copies through the shared policy

**Files:**
- Modify: `src/Scrinia.Core/Embeddings/EmbeddingReindexer.cs:84-115`
- Modify: `src/Scrinia.Core/Embeddings/HybridReranker.cs:42-84`
- Modify: `src/Scrinia/Services/CoreEmbeddingEventHandler.cs:57-101`
- Modify: `src/Scrinia.Server/Services/BuiltInEmbeddingsService.cs:44-78, 126-212`

**Interfaces:**
- Consumes: `MemoryVectorIndexer` and `VectorScoreProjection` from Task 3, with exactly the signatures declared there.

- [ ] **Step 1: Rewrite `EmbeddingReindexer.ReindexAsync`'s per-item body**

Construct one indexer before the loop:

```csharp
var indexer = new MemoryVectorIndexer(provider, vectorStore, logger, options);
```

Then replace the window/cap/embed/guard/remove/upsert block at `:84-115` with a single call, reconstructing the memory's content array from the artifact rather than passing one decoded blob:

```csharp
var elements = Nmp2ChunkedEncoder.DecodeChunks(artifact);
if (elements.Length == 0)
{
    skipped++;
    continue;
}

if (await indexer.IndexAsync(item.Scope, item.Entry.Name, elements, ct))
    embedded++;
else
    skipped++;
```

The existing whitespace guard at `:78-82` still applies — keep it, testing the joined text, so a memory whose every element is blank is skipped rather than indexed to nothing.

Reindex **must** produce the same layout as a write. Passing `[decoded]` would put every reindexed memory under element index 1, so the chunk-level supplemental keys would exist only for memories written after this branch — and since Task 7's signature bump rebuilds every existing vector through this exact path, that would mean the element contract held for almost nothing.

Leave the surrounding `try`/`catch` ladder (`:119-138`) exactly as it is — `VectorStoreUnavailableException` still counts as failed, `FileNotFoundException` still counts as skipped.

Delete the now-unused `windowSize`, `overlap` and `maxChunks` locals at `:45-47`, and the `decoder`/`decoded` locals if nothing else reads them.

- [ ] **Step 1b: Give `ReindexViaSinkAsync` the same treatment**

`EmbeddingReindexer.ReindexViaSinkAsync:225` hands the sink `[decoded]` — a one-element array — with a comment saying "the sink does its own chunking (matches how a regular write flows through)". After this branch that comment is false: a regular write carries the real content array, so replaying with one element produces a different layout than the write it claims to match.

```csharp
var elements = Nmp2ChunkedEncoder.DecodeChunks(artifact);
if (elements.Length == 0)
{
    skipped++;
    continue;
}

string qualifiedName = store.FormatQualifiedName(item.Scope, item.Entry.Name);
await sink.OnStoredAsync(qualifiedName, elements, store, ct);
embedded++;
```

Update the doc comment at `:179-181` to say the sink receives the memory's content array, not a single-element one.

- [ ] **Step 2: Rewrite `HybridReranker.ComputeScoresAsync`'s projection loop**

Replace `:62-81` with:

```csharp
var topK = VectorIndex.Search(queryVec, candidateVectors, candidateVectors.Count);
VectorScoreProjection.Project(group.Key, topK.Select(t => (t.Item1, t.Item2)), _weight, scores);
```

Check `VectorIndex.Search`'s actual return element type before writing this — if it already yields a named tuple of `(VectorEntry, double)`, pass `topK` straight through with no `Select`.

While here, fix the header comment at `:7-16`, which claims the class "embeds only the BM25 top-K candidates" while `:62` passes `candidateVectors.Count`. The behaviour is right — the candidate set is already filtered to BM25 survivors at `:56-58` — so correct the comment, not the code:

```csharp
/// <summary>
/// Hybrid re-ranking search contributor: scores only the vectors belonging to the BM25 +
/// attribute candidate set, rather than the whole corpus. The candidate filter at
/// <c>candidateVectors</c> is what bounds the work; every surviving vector is then scored,
/// which is why <c>VectorIndex.Search</c> is called with the full candidate count rather than
/// a smaller K.
/// …
```

- [ ] **Step 3: Rewrite `CoreEmbeddingEventHandler`**

Replace the whole `EmbedAndIndexAsync` body (`:57-101`) with a delegation, and hold the indexer as a field built in the constructor:

```csharp
private readonly MemoryVectorIndexer _indexer;

public CoreEmbeddingEventHandler(
    IEmbeddingProvider provider, VectorStore store, ILogger logger, EmbeddingOptions? options = null)
{
    _store = store;
    _logger = logger;
    _indexer = new MemoryVectorIndexer(provider, store, logger, options);
}

private async Task EmbedAndIndexAsync(
    string qualifiedName, string[] content, IMemoryStore memoryStore, CancellationToken ct)
{
    try
    {
        var (scope, subject) = memoryStore.ParseQualifiedName(qualifiedName);
        await _indexer.IndexAsync(scope, subject, content, ct);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to embed memory '{Name}'", qualifiedName);
    }
}
```

`OnForgottenAsync` (`:42-55`) routes to `_indexer.RemoveAsync` instead of `_store.RemoveAsync`. The `_provider` and `_options` fields become unused — delete them.

Update the class doc comment at `:11-15`: it currently says content is joined and sliced, which is no longer how the unit works.

- [ ] **Step 4: Rewrite `BuiltInEmbeddingsService`**

Same three edits: hold a `MemoryVectorIndexer` field; `EmbedAndIndexAsync` (`:178-212`) delegates to it; `ComputeScoresAsync`'s loop (`:62-74`) delegates to `VectorScoreProjection.Project`.

`ReindexStoreAsync` (`:126-174`) keeps its resolve preamble — including the ephemeral `"~name"` branch at `:143-145`, which is real and must not be dropped — and replaces `:146-159` with:

```csharp
var elements = Nmp2ChunkedEncoder.DecodeChunks(artifact);
if (elements.Length == 0) continue;

if (await _indexer.IndexAsync(item.Scope, item.Entry.Name, elements, ct))
    count++;
```

Same reasoning as Task 4 Step 1: reindex must reproduce the write's layout, or the element keys exist only for memories written after this branch.

`OnForgottenAsync` (`:88-100`) and `OnAfterForgetAsync` (`:110-122`) both route to `_indexer.RemoveAsync`.

- [ ] **Step 5: Add the reindex-layout guard**

Reindex is how Task 7's signature bump rebuilds every existing vector, so a reindex that produced
a different layout than a write would leave almost the whole corpus without element keys. Pin it.
Add to `tests/Scrinia.Tests/Embeddings/IndexerScorerContractTests.cs`:

```csharp
[Fact]
public async Task ReindexProducesTheSameLayoutAsTheWriteItRebuilds()
{
    string[] content = ["short one", new string('b', 400), "short three"];
    var options = new EmbeddingOptions { ChunkSize = 100, ChunkOverlap = 20 };

    // The write path: the content array arrives in flight.
    var written = NewStore();
    await new MemoryVectorIndexer(new OrdinalProvider(), written, NullLogger.Instance, options)
        .IndexAsync("local", "note", content, default);

    // The reindex path: the array is reconstructed from the stored artifact.
    var rebuilt = NewStore();
    string artifact = Nmp2ChunkedEncoder.EncodeChunks(content);
    await new MemoryVectorIndexer(new OrdinalProvider(), rebuilt, NullLogger.Instance, options)
        .IndexAsync("local", "note", Nmp2ChunkedEncoder.DecodeChunks(artifact), default);

    rebuilt.GetVectors("local").Select(v => v.ChunkIndex)
        .Should().BeEquivalentTo(written.GetVectors("local").Select(v => v.ChunkIndex),
            because: "Task 7's signature bump rebuilds every existing vector through reindex — if "
                   + "reindex collapsed a memory to element 1, the element contract would hold "
                   + "only for memories written after this branch");
}
```

Add a `NewStore()` helper to that file matching the one in `MemoryVectorIndexerTests`.

- [ ] **Step 6: Build**

Run: `dotnet build Scrinia.sln`

Expected: 0 errors. Unused-field warnings here mean a leftover `_provider`/`_options` — delete rather than suppress.

- [ ] **Step 7: Run the affected suites**

Run: `dotnet test tests/Scrinia.Tests` then `dotnet test tests/Scrinia.Server.Tests`

Expected: PASS. Tests asserting old 0-based window keys must be **updated to the element contract**, not deleted — if one cannot be expressed under the new contract, stop and report it rather than removing coverage.

- [ ] **Step 8: Commit**

```bash
git add src/Scrinia.Core/Embeddings/EmbeddingReindexer.cs \
        src/Scrinia.Core/Embeddings/HybridReranker.cs \
        src/Scrinia/Services/CoreEmbeddingEventHandler.cs \
        src/Scrinia.Server/Services/BuiltInEmbeddingsService.cs \
        tests/
git commit -m "refactor(embeddings): four in-process copies become calls to the shared indexer"
```

---

## Task 5: Route the plugin's two copies through the shared policy

The plugin is where H3's user-visible harm lives: it never windowed, so a long memory became one mean-pooled vector, and its reindex never called `RemoveAsync`.

**Files:**
- Modify: `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs:26-28, 53-69, 125-165, 169-201, 259-334, 336-378`
- Test: `tests/Scrinia.Plugin.Embeddings.Tests/` (add a parity test; match the project's existing `using` conventions)

**Interfaces:**
- Consumes: `MemoryVectorIndexer`, `VectorScoreProjection` (Task 3); `IEmbeddingsAdmin` (Task 1).

- [ ] **Step 1: Write the failing parity test**

Add `tests/Scrinia.Plugin.Embeddings.Tests/IndexerParityTests.cs`. This is the test that would have caught H3:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core.Embeddings;

namespace Scrinia.Plugin.Embeddings.Tests;

/// <summary>
/// Finding H3. Installing the Vulkan plugin — the component marketed as better embeddings —
/// made semantic recall on long memories worse or absent, because the plugin's private copy of
/// embed-and-index never windowed. One input through both paths must now produce one layout.
/// </summary>
public sealed class IndexerParityTests
{
    private sealed class StubProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 2;
        public string Signature => "test:stub";
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>([1f, 0f]);
        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult<float[][]?>([.. texts.Select((_, i) => new[] { 1f, (float)i })]);
    }

    [Fact]
    public async Task PluginAndBuiltIn_ProduceTheSameChunkIndicesForTheSameInput()
    {
        var options = new EmbeddingOptions { ChunkSize = 100, ChunkOverlap = 20 };
        string[] content = ["short", new string('x', 400)];

        var a = NewStore();
        var b = NewStore();
        await new MemoryVectorIndexer(new StubProvider(), a, NullLogger.Instance, options)
            .IndexAsync("local", "note", content, default);
        await new MemoryVectorIndexer(new StubProvider(), b, NullLogger.Instance, options)
            .IndexAsync("local", "note", content, default);

        a.GetVectors("local").Select(v => v.ChunkIndex)
            .Should().BeEquivalentTo(b.GetVectors("local").Select(v => v.ChunkIndex));
        a.GetVectors("local").Should().HaveCountGreaterThan(content.Length,
            because: "the long element must window rather than mean-pool");
    }

    private static VectorStore NewStore()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new VectorStore(dir);
    }
}
```

- [ ] **Step 2: Run it to verify it passes trivially, then make the plugin use the same path**

Run: `dotnet test tests/Scrinia.Plugin.Embeddings.Tests --filter "FullyQualifiedName~IndexerParityTests"`

Expected: PASS — it exercises the shared indexer twice, so it is a guard for later drift rather than a red-to-green step. The red-to-green proof for this task is the existing `EmbeddingsPluginIncrementalEmbedTests` suite continuing to pass once the plugin's private copy is gone.

- [ ] **Step 3: Hold an indexer in the plugin**

In `EmbeddingsPlugin.cs`, `EnsureInitialized` (`:75-123`) already builds the provider and vector store under `_initLock`. Build the indexer there too, as a `volatile MemoryVectorIndexer? _indexer` assigned immediately before `_vectorStore` (which stays the last-assigned initialized sentinel), and hold the live `EmbeddingOptions` as a field rather than constructing a fresh one:

```csharp
private EmbeddingOptions _options = new();
private volatile MemoryVectorIndexer? _indexer;
```

`ConfigureServices` (`:53-69`) already reads `Scrinia:Embeddings`. Bind the whole section into `_options` there rather than reading only `SemanticWeight`, so `ChunkSize`, `ChunkOverlap` and `MaxChunksPerMemory` reach the indexer:

```csharp
_options = configuration.GetSection("Scrinia:Embeddings").Get<EmbeddingOptions>() ?? new EmbeddingOptions();
_semanticWeight = _options.SemanticWeight;
```

Keep the existing explicit `SemanticWeight` string parse as a fallback only if the bound value is absent — check the section's behaviour and preserve today's precedence.

- [ ] **Step 4: Replace the plugin's write path**

Replace `EmbedAndIndexAsync` (`:259-316`) with:

```csharp
private async Task EmbedAndIndexAsync(
    string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct)
{
    EnsureInitialized();
    if (_indexer is null) return;

    try
    {
        var (scope, subject) = store.ParseQualifiedName(qualifiedName);
        await _indexer.IndexAsync(scope, subject, content, ct);
    }
    catch (Exception ex)
    {
        _logger?.LogWarning(ex, "Failed to embed memory '{Name}'", qualifiedName);
    }
}
```

`RemoveVectorsAsync` (`:318-334`) routes to `_indexer.RemoveAsync`. `OnAppendedAsync` (`:227-250`) keeps its read-the-full-artifact preamble — that behaviour is correct and deliberate — and passes the decoded text through as a one-element array.

- [ ] **Step 5: Replace the plugin's reindex path**

`ReindexStoreAsync` (`:336-378`) keeps its resolve preamble including the ephemeral branch, and replaces `:356-364` with:

```csharp
var elements = Nmp2ChunkedEncoder.DecodeChunks(artifact);
if (elements.Length == 0) continue;

if (await _indexer!.IndexAsync(item.Scope, item.Entry.Name, elements, ct))
    count++;
```

This is where the missing `RemoveAsync` gets fixed — the review notes reindex "never calls RemoveAsync, so it refreshes the document vector and orphans stale chunk vectors behind it". The shared indexer removes first.

Change the method from `internal` to `public` so it satisfies `IEmbeddingsAdmin`.

- [ ] **Step 6: Replace the plugin's projection**

Replace `ComputeScoresAsync`'s loop (`:190-198`) with `VectorScoreProjection.Project(group.Key, topK, _semanticWeight, scores)`, matching whatever tuple shape `VectorIndex.Search` returns.

- [ ] **Step 7: Implement `IEmbeddingsAdmin` and stop mapping the routes**

Extend the declaration at `:26-27`:

```csharp
public sealed class EmbeddingsPlugin : ScriniaPluginBase,
    IMemoryOperationHook, ISearchScoreContributor, IMemoryEventSink, IEmbeddingsAdmin, IDisposable
```

Add the members the interface needs, calling `EnsureInitialized()` first in each so a cold plugin still answers:

```csharp
public string ProviderName { get { EnsureInitialized(); return _provider.GetType().Name; } }
public bool IsAvailable { get { EnsureInitialized(); return _provider.IsAvailable; } }
public int Dimensions { get { EnsureInitialized(); return _provider.Dimensions; } }
public double SemanticWeight => _semanticWeight;
public int TotalVectorCount { get { EnsureInitialized(); return _vectorStore?.Count() ?? 0; } }
public string Hardware => "Vulkan GPU";
```

Register it in `ConfigureServices` beside the existing three registrations at `:65-68`:

```csharp
services.AddSingleton<IEmbeddingsAdmin>(this);
```

Delete `MapEndpoints` (`:125-165`) entirely. Its three routes are now served by `EmbeddingsEndpoints` for both implementations. `ScriniaPluginBase.MapEndpoints` remains available to plugins generally — confirm the base declares it virtual with a no-op default before deleting the override, and if it is abstract, leave an empty override with a comment naming this change.

- [ ] **Step 8: Build and test**

Run: `dotnet build Scrinia.sln` then `dotnet test tests/Scrinia.Plugin.Embeddings.Tests` then `dotnet test tests/Scrinia.Server.Tests`

Expected: PASS. `EmbeddingsPluginIncrementalEmbedTests.cs:147` pins the plugin's writer/reader key coherence — it must still pass, now under the element contract. If it asserts a specific chunk index, update it to the two-level value and say so in the commit message.

- [ ] **Step 9: Commit**

```bash
git add src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs tests/Scrinia.Plugin.Embeddings.Tests/
git commit -m "fix(plugin): window like everything else, remove before upsert, drop the duplicate routes"
```

---

## Task 6: The out-of-process host stops re-expressing the policy over the wire

**Files:**
- Modify: `src/Scrinia.Plugin.Embeddings.Cli/EmbeddingsTools.cs`
- Modify: `src/Scrinia.Plugin.Embeddings.Cli/Program.cs:63-77`
- Modify: `src/Scrinia/Services/McpPluginHost.cs:121-125, 153-186`
- Test: `tests/Scrinia.Tests/` — add `McpPluginHostNegotiationTests.cs`

**Interfaces:**
- Consumes: `MemoryVectorIndexer` (Task 3).
- Produces: an MCP tool named `index` taking `scope: string`, `name: string`, `content: string[]`.

- [ ] **Step 1: Add the `index` tool**

In `EmbeddingsTools.cs`, add `MemoryVectorIndexer` to the primary constructor and add the tool. The class already receives `VectorStore`, `IEmbeddingProvider` and `EmbeddingOptions` and already references `Scrinia.Core.Embeddings`:

```csharp
[McpServerToolType]
public sealed class EmbeddingsTools(
    VectorStore vectorStore, IEmbeddingProvider provider, EmbeddingOptions options,
    MemoryVectorIndexer indexer)
{
    [McpServerTool(Name = "index")]
    [Description("Embed a memory's full content array and replace its vectors. Preferred over upsert.")]
    public async Task<string> Index(
        [Description("Scope")] string scope,
        [Description("Memory name")] string name,
        [Description("The memory's content elements, in order")] string[] content,
        CancellationToken ct = default)
    {
        try
        {
            bool wrote = await indexer.IndexAsync(scope, name, content, ct);
            return JsonSerializer.Serialize(new { ok = wrote });
        }
        catch (VectorStoreUnavailableException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    // … existing status / search / upsert / remove tools unchanged …
}
```

`upsert` stays. It is a published tool on the plugin MCP surface, and a third-party plugin implementing only `upsert` must keep working.

Replace `Search`'s projection loop (`:44-51`) with `VectorScoreProjection.Project(scope, topK, options.SemanticWeight, scores)` — this is the fourth copy, and the one that never max-aggregated.

- [ ] **Step 2: Register the indexer in the plugin CLI host**

In `Scrinia.Plugin.Embeddings.Cli/Program.cs`, after `builder.Services.AddSingleton(options);` at `:77`:

```csharp
builder.Services.AddSingleton(new MemoryVectorIndexer(
    provider, vectorStore, NullLogger.Instance, options));
```

`NullLogger.Instance` matches the `logger` already used at `:42` — this process deliberately logs to stderr via `Console.Error`, not `ILogger`.

- [ ] **Step 3: Write the failing negotiation test**

Create `tests/Scrinia.Tests/McpPluginHostNegotiationTests.cs`. Read `McpPluginHost.cs` first to see how `_client` is injected; if it is not test-visible, add the smallest seam that lets a fake MCP client be supplied, and say so in the report.

```csharp
using FluentAssertions;

namespace Scrinia.Tests;

/// <summary>
/// Finding H3, seventh copy. The host used to re-express the chunking policy over the wire —
/// one upsert for the joined text plus one per content element, windowing nothing. It now
/// prefers a single `index` call so the shared indexer runs inside the plugin process, and
/// falls back to the old two-call shape only for plugins that do not offer `index`.
/// </summary>
public sealed class McpPluginHostNegotiationTests
{
    [Fact]
    public async Task PluginOfferingIndex_ReceivesOneCallWithTheWholeContentArray()
    {
        var fake = new FakeMcpClient(tools: ["index", "search", "remove"]);
        var host = await FakeMcpClient.NewHostAsync(fake);

        await host.OnStoredAsync("local:note", ["one", "two", "three"], FakeStore.Instance, default);

        fake.Calls.Should().ContainSingle();
        fake.Calls[0].Tool.Should().Be("index");
        fake.Calls[0].Arguments["content"].Should().BeEquivalentTo(new[] { "one", "two", "three" });
    }

    [Fact]
    public async Task PluginOfferingOnlyUpsert_StillReceivesTheLegacySequence()
    {
        var fake = new FakeMcpClient(tools: ["upsert", "search", "remove"]);
        var host = await FakeMcpClient.NewHostAsync(fake);

        await host.OnStoredAsync("local:note", ["one", "two"], FakeStore.Instance, default);

        fake.Calls.Should().OnlyContain(c => c.Tool == "upsert");
        fake.Calls.Should().HaveCount(3, because: "one joined document vector plus one per element");
    }
}
```

Build `FakeMcpClient` and `FakeStore` as private test doubles in this file, shaped to whatever seam `McpPluginHost` exposes.

- [ ] **Step 4: Run it to verify it fails**

Run: `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~McpPluginHostNegotiationTests"`

Expected: FAIL — the host has no `index` path.

- [ ] **Step 5: Negotiate in the host**

In `McpPluginHost.cs`, add the flag beside its siblings at `:31` and `:123-125`:

```csharp
private bool _hasSearch, _hasUpsert, _hasRemove, _hasStatus, _hasIndex;
…
_hasIndex = names.Contains("index");
```

Update `HasEventSinkCapability` (`:36`) to `(_hasIndex || _hasUpsert || _hasRemove) && _isAvailable && !_degraded`.

Rewrite `OnStoredAsync` (`:153-186`):

```csharp
public async Task OnStoredAsync(string qualifiedName, string[] content,
    IMemoryStore store, CancellationToken ct)
{
    if (_degraded) return;

    var (scope, name) = store.ParseQualifiedName(qualifiedName);

    if (_hasIndex)
    {
        // Preferred: hand the plugin the whole content array and let MemoryVectorIndexer run
        // inside the plugin process. The chunking policy lives in one place instead of being
        // restated by every caller that speaks this protocol (finding H3).
        await CallToolWithRetryAsync("index", new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["name"] = name,
            ["content"] = content,
        }, ct);
        return;
    }

    if (!_hasUpsert) return;

    // Legacy fallback for plugins that predate the `index` tool. This path does not window, so
    // a long memory becomes one mean-pooled vector — the exact H3 regression. Kept so a
    // third-party plugin keeps being indexed rather than silently stopping.
    _logger?.LogWarning(
        "Plugin offers 'upsert' but not 'index'; indexing without windowing. "
        + "Long memories will be less reachable by semantic search.");

    string joined = string.Concat(content);
    if (string.IsNullOrWhiteSpace(joined)) return;

    await CallToolWithRetryAsync("upsert", new Dictionary<string, object?>
    {
        ["scope"] = scope, ["name"] = name, ["text"] = joined,
    }, ct);

    if (content.Length > 1)
    {
        for (int i = 0; i < content.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(content[i])) continue;
            await CallToolWithRetryAsync("upsert", new Dictionary<string, object?>
            {
                ["scope"] = scope, ["name"] = name, ["text"] = content[i], ["chunkIndex"] = i + 1,
            }, ct);
        }
    }
}
```

`OnAppendedAsync` (`:188-...`) takes the same two-branch shape, with a one-element array on the `index` branch. Check whether `McpPluginHost` has a `_logger` field; if not, use the same reporting channel its other warnings use.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~McpPluginHostNegotiationTests"`

Expected: PASS, 2 of 2.

- [ ] **Step 7: Commit**

```bash
git add src/Scrinia.Plugin.Embeddings.Cli/ src/Scrinia/Services/McpPluginHost.cs tests/Scrinia.Tests/McpPluginHostNegotiationTests.cs
git commit -m "feat(plugin-host): prefer an index call, keep upsert as a named fallback"
```

---

## Task 7: Signature bump retires vectors written under the old layout

**Files:**
- Modify: `src/Scrinia.Core/Embeddings/ChunkedSignature.cs:17-18`
- Modify: `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs:118`
- Test: `tests/Scrinia.Tests/Embeddings/` — add `ChunkedSignatureLayoutTests.cs`

**Interfaces:**
- Consumes: `MemoryVectorIndexer` (Task 3), `_options` on the plugin (Task 5).

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/Embeddings/ChunkedSignatureLayoutTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core.Embeddings;

namespace Scrinia.Tests.Embeddings;

/// <summary>
/// The element/window layout changed what a stored chunk index means, so vectors written
/// before it must not be read as though they carry element indices. The signature carries a
/// layout marker; VectorStore's existing mismatch path quarantines and reindex rebuilds —
/// the same flow that already handles a model switch or a chunk-config change.
/// </summary>
public sealed class ChunkedSignatureLayoutTests
{
    [Fact]
    public void Compose_CarriesTheLayoutMarker()
    {
        ChunkedSignature.Compose("model2vec:potion-base-8M", 1200, 200)
            .Should().Be("model2vec:potion-base-8M|c1200o200|e1");
    }

    [Fact]
    public void Compose_DiffersFromThePreBranchSignature()
    {
        ChunkedSignature.Compose("p", 1200, 200)
            .Should().NotBe("p|c1200o200",
                because: "identical signatures would leave window-indexed vectors in place and "
                       + "the element key would silently never resolve for them");
    }

    [Fact]
    public void ForProvider_UsesTheSuppliedOptions_NotDefaults()
    {
        var provider = new StubAvailableProvider();
        var options = new EmbeddingOptions { ChunkSize = 400, ChunkOverlap = 40 };

        ChunkedSignature.ForProvider(provider, options)
            .Should().Contain("c400o40",
                because: "EmbeddingsPlugin.cs:118 passed a default-constructed EmbeddingOptions, "
                       + "so configured chunk settings never reached the signature");
    }

    private sealed class StubAvailableProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 2;
        public string Signature => "stub";
        public Task<float[]?> EmbedAsync(string t, CancellationToken ct = default)
            => Task.FromResult<float[]?>([1f, 0f]);
        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> t, CancellationToken ct = default)
            => Task.FromResult<float[][]?>([[1f, 0f]]);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~ChunkedSignatureLayoutTests"`

Expected: FAIL on the first two — the signature has no layout marker yet. The third may already pass at the `ChunkedSignature` level; it fails at the plugin call site, which Step 4 fixes.

- [ ] **Step 3: Add the layout marker**

In `ChunkedSignature.cs`, replace `Compose` (`:17-18`):

```csharp
/// <summary>Chunk-vector layout revision. Bumped when the meaning of a stored chunk index
/// changes, so files written under the old meaning quarantine rather than being misread.
/// <c>e1</c> is elements-with-nested-windows (see <see cref="MemoryVectorIndexer"/>); files
/// with no marker predate it and carry raw window ordinals.</summary>
public const string LayoutRevision = "e1";

/// <summary>
/// Compose a chunked signature: <c>{providerSignature}|c{chunkSize}o{overlap}|{layout}</c>.
/// Example: <c>ollama:nomic-embed-text|c1200o200|e1</c>.
/// </summary>
public static string Compose(string providerSignature, int chunkSize, int overlap) =>
    $"{providerSignature}|c{chunkSize}o{overlap}|{LayoutRevision}";
```

- [ ] **Step 4: Pass the live options at the plugin call site**

In `EmbeddingsPlugin.cs:118`, replace `new EmbeddingOptions()` with the `_options` field added in Task 5:

```csharp
string? expectedSignature = ChunkedSignature.ForProvider(_provider, _options);
```

- [ ] **Step 5: Verify the quarantine-and-rebuild path**

Find the existing test covering `VectorStore`'s signature-mismatch quarantine (grep the test tree for `HasStaleQuarantines` or `StaleQuarantineScopes` before writing anything new — this repo has previously declared a coverage gap that already had coverage). Extend it with a case that writes a file under the pre-branch signature and asserts it quarantines under the new one. If no such test exists, add it there rather than in the new file.

- [ ] **Step 6: Run the full suite**

Run: `dotnet build Scrinia.sln` then `dotnet test Scrinia.sln`

Expected: PASS. Any test asserting a literal signature string must gain the `|e1` suffix.

- [ ] **Step 7: Commit**

```bash
git add src/Scrinia.Core/Embeddings/ChunkedSignature.cs \
        src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs tests/
git commit -m "fix(embeddings): sign the layout, and let configured chunk settings reach the signature"
```

---

## Task 8: One home for the scoring rubric

Finding M6. This refactor must not move a single score, so the pinning tests come first and are written against the **current** implementation.

**Files:**
- Create: `src/Scrinia.Core/Search/ScoringWeights.cs`
- Modify: `src/Scrinia.Core/Search/WeightedAttributeScorer.cs:5-48, 51, 305-547`
- Test: `tests/Scrinia.Tests/Search/ScoringWeightsPinningTests.cs`

**Interfaces:**
- Produces: `ScoringWeights` record with a `Default` static, injected into `WeightedAttributeScorer`'s constructor.

- [ ] **Step 1: Write the pinning tests against current behaviour**

Create `tests/Scrinia.Tests/Search/ScoringWeightsPinningTests.cs`. These must be written and **passing on the unmodified scorer** before any refactor begins:

```csharp
using FluentAssertions;
using FluentAssertions.Execution;

namespace Scrinia.Tests.Search;

/// <summary>
/// Finding M6 is a readability fix, not a behaviour change: the rubric is stated four times,
/// three of which the compiler sees. These tests capture exact scores and ordering from the
/// pre-refactor scorer so that "identical" is a checked claim rather than an intention.
/// </summary>
public sealed class ScoringWeightsPinningTests
{
    [Fact]
    public void ExactNameMatch_OutranksNameContains_ByTheDocumentedMargin()
    {
        var results = Search("alpha", [Entry("alpha"), Entry("alphabet-soup")]);

        using var scope = new AssertionScope();
        results[0].Entry.Name.Should().Be("alpha");
        // 100 (exact) vs 30 (starts-with) — the header table's first and fifth rows.
        (results[0].Score - results[1].Score).Should().BeApproximately(70.0, 1e-9);
    }

    [Fact]
    public void TwoTermMatch_AddsTheIntersectionBonusExactlyOnce()
    {
        var single = Search("alpha", [Entry("alpha", keywords: ["beta"])])[0].Score;
        var both = Search("alpha beta", [Entry("alpha", keywords: ["beta"])])[0].Score;

        // 100 (exact name) + 40 (keyword exact) + 15 (one bonus for the second matched term).
        (both - single).Should().BeApproximately(55.0, 1e-9);
    }
}
```

Build `Search(...)` and `Entry(...)` helpers by copying the construction pattern from
`tests/Scrinia.Tests/Search/RankerCompositionTests.cs`, which already drives `SearchAll` with
`AlphaRecency: 0` to isolate relevance from the recency bump. Add at least two more cases
covering topic scoring and chunk-entry scoring, since those are the other two literal ladders.

- [ ] **Step 2: Run them against the unmodified scorer**

Run: `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~ScoringWeightsPinningTests"`

Expected: PASS. If a case fails here, the expected value is wrong — fix the test to match today's behaviour, not the code. These tests describe what is, so that Step 5 can prove nothing changed.

- [ ] **Step 3: Commit the pinning tests alone**

```bash
git add tests/Scrinia.Tests/Search/ScoringWeightsPinningTests.cs
git commit -m "test(search): pin the scoring rubric before consolidating it"
```

- [ ] **Step 4: Create the record**

Create `src/Scrinia.Core/Search/ScoringWeights.cs`:

```csharp
namespace Scrinia.Core.Search;

/// <summary>
/// The retrieval scoring rubric, in one place.
///
/// <para>Finding M6: these constants were stated four times — a literal ladder inside each of
/// <c>ScoreEntryTerm</c>, <c>ScoreTopicTerm</c> and <c>ScoreChunkEntryTerm</c>, plus an ASCII
/// table in the scorer's header that had to be hand-synced with all three. Changing what a
/// keyword match is worth meant four edits, and the header table — the one people read — was
/// the copy most likely to drift into being confidently wrong.</para>
///
/// <para>All matching is case-insensitive. Every value here is the magnitude the scorer used
/// before consolidation; this record was introduced to make the rubric editable and honest
/// about where a number lives, not to change retrieval behaviour.</para>
/// </summary>
public sealed record ScoringWeights(
    // ── Entry scoring, per query term ──
    double NameExact = 100.0,
    double NameStartsWith = 30.0,
    double NameContains = 20.0,
    double KeywordExact = 40.0,
    double KeywordContains = 12.0,
    double AttributePairExact = 50.0,
    double AttributeValueExact = 40.0,
    double AttributeValueContains = 12.0,
    double ContentPreviewContains = 5.0,

    // ── Topic scoring, per query term ──
    double TopicNameExact = 100.0,
    double TopicNameStartsWith = 30.0,
    double TopicNameContains = 20.0,
    double TopicTagExact = 50.0,
    double TopicTagContains = 15.0,
    double TopicDescriptionContains = 10.0,
    double TopicEntryNameAggregate = 8.0,

    // ── Chunk-entry scoring, per query term ──
    double ChunkKeywordExact = 40.0,
    double ChunkKeywordContains = 12.0,
    double ChunkContentPreviewContains = 5.0,

    // ── Composition ──
    /// <summary>Bonus per additional matched query term, applied as (matched - 1) x this.</summary>
    double IntersectionBonus = 15.0,
    /// <summary>Multiplier on normalized BM25 before it joins the attribute score.</summary>
    double Bm25Weight = 5.0)
{
    public static ScoringWeights Default { get; } = new();
}
```

Cross-check every value against the current source before committing: entry ladder at `:326-390`, topic ladder at `:414-453`, chunk ladder at `:480-547`, `Bm25Weight` at `:51`, intersection bonus at `:321`/`:409`/`:475`. Note the entry ladder multiplies three attribute rows by `KnownAttributes.AttributeWeight(attr.Key)` — that multiplication stays where it is; only the base constants move.

Also check the chunk ladder past `:509` for a parent-name contribution and any other rows not listed above, and add fields for them. Do not leave a literal behind.

- [ ] **Step 5: Thread it through the scorer**

Add a `ScoringWeights` field to `WeightedAttributeScorer` alongside `_options` (`:60`), defaulting to `ScoringWeights.Default` so existing constructor callers are unaffected. Replace every literal in the three ladders and the three intersection-bonus sites with the corresponding field. Delete `private const double Bm25Weight = 5.0;` at `:51` and read it from the record.

The three ladder methods are currently `static` (`:305`, `:326`, `:394`, `:414`, `:457`, `:480`). Make them instance methods so they can read the field, and update their call sites.

Delete the ASCII score table from the class header (`:14-45`), replacing it with a pointer:

```csharp
/// <summary>
/// Linear-scan weighted attribute scorer with multi-term support and BM25 content search.
///
/// Multi-term queries split on whitespace; each term is scored independently and per-term max
/// scores are summed, so "DI patterns" ranks entries matching both terms above entries matching
/// one. Hybrid scoring: finalScore = weightedAttributeScore + bm25Score * Bm25Weight.
///
/// <para>The per-match-type score table lives in <see cref="ScoringWeights"/>. It used to be
/// restated here as ASCII art requiring manual sync with three literal ladders below — finding
/// M6. Read the record; it is the rubric.</para>
/// …
```

Keep the paragraphs at `:28-33` and `:46-47` (the forward-fill note and the sort/degradation note) — they describe behaviour, not constants.

- [ ] **Step 6: Run the pinning tests to prove nothing moved**

Run: `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~ScoringWeightsPinningTests"`

Expected: PASS, unchanged. A failure here means the refactor changed behaviour — find the transcription error rather than adjusting the expected value.

- [ ] **Step 7: Run the full search and benchmark suites**

Run: `dotnet test tests/Scrinia.Tests` then `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests`

Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Scrinia.Core/Search/ScoringWeights.cs src/Scrinia.Core/Search/WeightedAttributeScorer.cs
git commit -m "refactor(search): one home for the rubric, and the header table retired"
```

---

## Task 9: Dead code out, dropped config parameter in

Finding L1b, with the correction that RRF is **not** dead — it is bench-reachable and directly tested; only `FromConfig` cannot select it.

**Files:**
- Delete: `src/Scrinia.Core/Embeddings/HnswIndex.cs`, `tests/Scrinia.Tests/Embeddings/HnswIndexTests.cs`, `tests/Scrinia.Benchmarks/HnswIndexBenchmarks.cs`, `tests/Scrinia.Tests/EntityGraphEquivalenceTests.cs`
- Modify: `src/Scrinia.Core/Search/EntityGraph.cs:148-197`, `src/Scrinia.Core/Search/RankerOptions.cs:71-79`, `src/Scrinia.Merge/MergeConfig.cs:8-9`, `src/Scrinia/Commands/ScriniaCommands.Setup.cs:369`, `src/Scrinia/Commands/CliJsonContext.cs:30`, `src/Scrinia.Core/Search/EntityTypeClassifier.cs:87`
- Modify: `tests/Scrinia.Merge.Tests/MergeConfigTests.cs:43-44`, `tests/Scrinia.Tests/MergeConfigRoundTripTests.cs`

- [ ] **Step 1: Write the failing config test**

Add to `tests/Scrinia.Tests/Search/RankerCompositionTests.cs`, beside the existing RRF cases:

```csharp
[Fact]
public void FromConfig_ReadsTheFusionMode()
{
    var opts = RankerOptions.FromConfig(key =>
        key == "Scrinia:Search:Fusion" ? "rrf" : null);

    opts.Fusion.Should().Be(FusionMode.Rrf,
        because: "the positional-record constructor silently dropped this parameter, so a "
               + "mode the bench harness and these tests both exercise was unreachable in production");
}

[Theory]
[InlineData(null)]
[InlineData("")]
[InlineData("sum")]
[InlineData("nonsense")]
public void FromConfig_FallsBackToSum(string? raw)
{
    RankerOptions.FromConfig(key => key == "Scrinia:Search:Fusion" ? raw : null)
        .Fusion.Should().Be(FusionMode.Sum);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~RankerCompositionTests"`

Expected: FAIL on `FromConfig_ReadsTheFusionMode` — it yields `Sum`.

- [ ] **Step 3: Wire the parameter**

In `RankerOptions.cs`, add the argument to `FromConfig` (`:74-78`) and a parser beside `ParseDouble`:

```csharp
public static RankerOptions FromConfig(Func<string, string?> getConfig)
{
    var d = Default;
    return new RankerOptions(
        AlphaRelevance:    ParseDouble(getConfig("Scrinia:Search:Alpha:Relevance"),    d.AlphaRelevance),
        AlphaRecency:      ParseDouble(getConfig("Scrinia:Search:Alpha:Recency"),      d.AlphaRecency),
        TauDays:           ParseDouble(getConfig("Scrinia:Search:TauDays"),            d.TauDays),
        RecencyScale:      ParseDouble(getConfig("Scrinia:Search:Scale:Recency"),      d.RecencyScale),
        Fusion:            ParseFusion(getConfig("Scrinia:Search:Fusion"),             d.Fusion));
}

/// <summary>Matches the bench harness's parser (RetrieverFactory.ParseFusion) so a mode
/// selected on the command line and the same mode in config mean the same thing.</summary>
private static FusionMode ParseFusion(string? raw, FusionMode fallback) => raw?.ToLowerInvariant() switch
{
    "sum" => FusionMode.Sum,
    "rrf" => FusionMode.Rrf,
    _ => fallback,
};
```

- [ ] **Step 4: Delete `HnswIndex`**

```bash
git rm src/Scrinia.Core/Embeddings/HnswIndex.cs \
       tests/Scrinia.Tests/Embeddings/HnswIndexTests.cs \
       tests/Scrinia.Benchmarks/HnswIndexBenchmarks.cs
```

Also remove `src/Scrinia.Core/Embeddings/Models/HnswNode.cs` if it exists and has no other referent — check with a grep before deleting. Reword the comment at `EntityTypeClassifier.cs:87`, which names `HnswIndex`:

```csharp
// normalized vectors) can be reused as-is (DRY with HybridReranker).
```

Then run `git status` and confirm SynologyDrive has not resurrected any of the deleted files as untracked ghosts. If it has, delete them again before building — otherwise you will chase phantom CS0101 duplicates.

- [ ] **Step 5: Delete `EntityGraph`'s instance side**

Delete `EntityGraph.Build` (both overloads, `:148` and `:162`) and `EntityGraph.Score` (`:197`), plus any private instance state and helpers they alone use. **Keep** the static `ParseEntityKeywords` (`:88`) and `ParseEntityAttributes` (`:126`) — they are production code, called from `EntityFactDerivation.cs:14-15`.

```bash
git rm tests/Scrinia.Tests/EntityGraphEquivalenceTests.cs
```

That test asserted `EntityGraph.Build(...).Score(...)` matched `EntityFactStore.ScoringAdjacency`. `ScoringAdjacency` keeps its own coverage in `EntityFactStoreScoringAdjacencyTests.cs`, which stays. If deleting the equivalence test leaves any `ScoringAdjacency` behaviour uncovered, add the case to that file rather than keeping a dead-code test alive to hold it.

If `EntityGraph` ends up containing only the two static parsers, leave it where it is — moving it is not this branch's business. Update its class doc comment to stop describing a graph it no longer builds.

- [ ] **Step 6: Delete `MergeConfig.Resolver`**

Remove `Resolver` and `ResolverCommand` from the record (`MergeConfig.cs:8-9`), the `Resolver` member from `CliJsonContext.cs:30`, and the write at `ScriniaCommands.Setup.cs:369`, along with the `resolver` local it reads and any CLI option that populates it. Update `MergeConfigTests.cs:43-44` and the `MergeConfigRoundTripTests.cs` assertions and doc comment (`:15-18`, `:39`, `:50`), which name both fields explicitly.

Check whether any on-disk `.scrinia` merge config in the repo or its fixtures carries a `resolver` key — a removed record field must still deserialize without throwing on files that contain it. If `MergeConfig` is deserialized with a strict/unmapped-member policy, that is a migration concern; verify and report it before proceeding.

- [ ] **Step 7: Build and run everything**

Run: `dotnet build Scrinia.sln` then `dotnet test Scrinia.sln`

Expected: 0 build errors, all suites green.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "chore: delete three unreachable features, and let config select a fusion mode"
```

---

## Task 10: Documentation and CHANGELOG

**Files:**
- Modify: `CHANGELOG.md` (`## [Unreleased]` section)
- Modify: `docs/architecture/*.md` — whichever files describe the embeddings pipeline, the REST surface, or the plugin contract
- Modify: `README.md` if it documents the embeddings routes or the `reindex` permission

- [ ] **Step 1: Find every doc that describes what changed**

```bash
grep -rn "plugins/embeddings\|EmbeddingReindexer\|chunkIndex\|HnswIndex\|MergeConfig" docs/ README.md AGENTS.md
```

Update each hit. Pay particular attention to any document that states the vector chunk unit or the supplemental key format — those are now wrong in a way a reader would act on.

- [ ] **Step 2: Write the CHANGELOG entries**

Add to `## [Unreleased]`, matching the existing `### <Type> — <descriptive title>` house style. Four sections:

```markdown
### Fixed — one embed-and-index policy, and the GPU plugin no longer degrades the search it improves

- **Installing the Vulkan embeddings plugin used to make semantic recall on long memories
  worse or absent.** Seven implementations of embed-and-index had drifted apart, and the
  load-bearing divergence was the chunking unit: Core and the server sliced content into
  overlapping windows, while the plugin never windowed at all — so a 40 KB memory became one
  mean-pooled vector, or the provider's context limit was exceeded and the memory silently got
  zero vectors. A new `MemoryVectorIndexer` in `Scrinia.Core.Embeddings` now owns windowing,
  the per-memory chunk cap, batching, the vector-count guard, remove-then-upsert ordering and
  write retry for every path. The plugin's reindex additionally never removed prior vectors,
  so it orphaned stale chunks behind each refresh; it now removes first like everything else.

- **Chunk-level semantic scores never reached the ranker.** `ChunkEntry.ChunkIndex` is 1-based
  over content elements, and that is the key the search scorer probes — but the built-in path
  wrote 0-based character-window ordinals into that key position, so the probe could never hit.
  Content elements are now the addressable unit and windows nest inside them, with a new
  `VectorScoreProjection` owning the read-side key contract. **Existing vector files are
  quarantined on first load and rebuilt** — the embedding signature carries a layout marker, so
  this happens automatically through the same path that already handles a model switch.

- **The plugin's configured chunk settings never reached its embedding signature.** It composed
  the signature from a default-constructed options object, so a store with custom `ChunkSize`
  or `ChunkOverlap` was stamped as though it used the defaults.

### Fixed — the embeddings reindex route can now resolve a store

- **`POST /api/v1/plugins/embeddings/reindex` returned "No store context available" on every
  request, and most likely always had.** The route group carried no `{store}` segment, so
  neither of the two ways its handler resolved a store could ever produce one. The three
  embeddings routes have moved under the store-scoped group, where the existing middleware
  resolves the store and applies access checks:

  | Was | Now |
  |---|---|
  | `GET /api/v1/plugins/embeddings/status` | `GET /api/v1/stores/{store}/embeddings/status` |
  | `GET /api/v1/plugins/embeddings/settings` | `GET /api/v1/stores/{store}/embeddings/settings` |
  | `POST /api/v1/plugins/embeddings/reindex` | `POST /api/v1/stores/{store}/embeddings/reindex` |

- **Reindex now requires a `reindex` permission.** The old group carried only
  `RequireAuthorization()`, so any authenticated key could have triggered a full-store re-embed
  if the route had worked.

- **The web Settings page no longer offers a Save button that could not save.** It issued
  `PUT .../embeddings/settings`, a route no server ever mapped; the request fell through to the
  SPA fallback and returned HTML. Embedding settings are read from configuration, so the panel
  is now read-only and says where the values come from.

### Changed — plugin hosts prefer a single `index` call

- The out-of-process plugin protocol gains an `index` tool that takes a memory's whole content
  array, so the shared indexer runs inside the plugin process instead of the chunking policy
  being restated by every caller that speaks the protocol. `upsert` is unchanged and still
  supported: a plugin that offers only `upsert` keeps receiving the previous call sequence, and
  the host logs a warning naming what that costs.

### Removed — three unreachable features, and a config key that now works

- `HnswIndex` (referenced only by its own tests and benchmarks; its documented 1000-vector
  threshold existed nowhere in code), `EntityGraph`'s instance side (superseded by
  `EntityFactStore.ScoringAdjacency`, which is what production uses), and `MergeConfig.Resolver`
  and `ResolverCommand` (written by setup, never read by the merge driver) have been deleted.
- **`Scrinia:Search:Fusion` now selects the ranker's fusion mode.** RRF was implemented,
  benchmarked and tested, but `RankerOptions.FromConfig` silently dropped the parameter — a
  positional-record argument that was simply never passed — so production could not reach it.
  Accepts `sum` (default) or `rrf`.
```

- [ ] **Step 3: Verify no stale claims remain**

```bash
grep -rn "plugins/embeddings" docs/ README.md web/ src/ || echo "clean"
```

Expected: no hits outside CHANGELOG's migration table.

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md docs/ README.md
git commit -m "docs: the element chunk unit, the moved routes, and what was deleted"
```

---

## Self-Review

**Spec coverage.** Every spec section maps to a task: §1 `MemoryVectorIndexer` → Tasks 3–5; §2 signature bump → Task 7; §3 endpoints → Tasks 1–2; §4 out-of-process host → Task 6; §5 `ScoringWeights` → Task 8; §6 dead code → Task 9; docs → Task 10. The spec's Testing section is distributed across the tasks that create the behaviour, with the load-bearing conformance test in Task 3 Step 3.

**Two decisions this plan makes that the spec left open**, both flagged for the reviewer:

1. **The two-level chunk index.** `VectorStore.UpsertAsync` deduplicates on `(name, chunkIndex)` (`VectorStore.cs:141`), so windows within one element cannot share the element's index. The alternative — a second field on `VectorEntry` — changes the SVF3 binary format, its op-log replay and its compaction, which is more than this branch should carry beside a REST-surface break and a layout migration.

2. **Reindex reconstructs the content array rather than passing one decoded blob.** The first draft of this plan had every reindex path pass `[decoded]`, which would have put every reindexed memory under element index 1. Since Task 7's signature bump rebuilds the entire existing corpus through exactly those paths, the element contract would then have held for almost nothing — the branch's central guarantee, true only for memories written after it. `Nmp2ChunkedEncoder.GetChunkCount` + `DecodeChunk` already recover the elements 1-based (this is how `GET /memories/{name}/chunks/{i}` works), so Task 3 Step 7 adds `DecodeChunks` and all four reindex/replay paths use it. Task 4 Step 5 pins write/reindex layout equality.

**Placeholder scan.** No "TBD", no "handle edge cases", no "similar to Task N". Four steps deliberately instruct the implementer to *read before writing* (Task 1 Step 1's factory helper, Task 4 Step 2's tuple shape, Task 6 Step 3's client seam, Task 7 Step 5's existing quarantine test) — these are verification instructions with a named target, not deferred decisions.

**Type consistency.** `MemoryVectorIndexer.IndexAsync` returns `Task<bool>` at every call site (Tasks 4, 5, 6). `ElementIndexOf` is `static int` and used in Tasks 3 and 4 tests. `IEmbeddingsAdmin`'s seven members are declared once in Task 1 and implemented in Tasks 1 and 5 with matching names, including `Hardware`, which the built-in service does not currently have. `VectorScoreProjection.Project`'s four parameters are identical in Tasks 3, 4, 5 and 6.
