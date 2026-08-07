# Search Contract Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Collapse `IMemoryStore`'s five `SearchAll` overloads into one `SearchQuery`-taking member, make remote-mode filtering real instead of post-hoc, and remove the drifted duplicate of the scoring pipeline.

**Architecture:** Land the behaviour changes first, on today's interface, each with its own tests — server-side filter transport (Task 2), then `HttpMemoryStore` using it (Task 3). Only then flip the interface shape (Task 4), which is a large mechanical diff carrying no behaviour claim and judged against a conformance suite that was already green. Then unify the pipeline (Task 5) and take the one efficiency win the new shape enables (Task 6).

**Tech Stack:** C# / .NET 10, xunit + FluentAssertions, `System.Text.Json` source-generated contexts (AOT), ASP.NET Core minimal APIs, `Microsoft.AspNetCore.Mvc.Testing`.

**Source spec:** `docs/superpowers/specs/2026-08-06-design-review-remediation-design.md`, "Branch A2 — `search-contract`". Findings F2, F5, the scorer half of F4, the `PluginPipeline` duplication (spec fact 13), and server-side filtering.

## Global Constraints

- **No mocking frameworks.** Zero `Mock<` / `Substitute.For` in this repo. Test doubles are hand-written classes.
- **AOT-safe JSON.** Every new DTO must be registered in its source-generated context: `src/Scrinia.Server/Models/ServerJsonContext.cs` for server DTOs, `HttpStoreJsonContext` (inside `src/Scrinia/HttpMemoryStore.cs`) for client DTOs.
  **The test suite will not catch an omission.** `src/Scrinia.Server/Program.cs:308-310` does `TypeInfoResolverChain.Insert(0, ServerJsonContext.Default)` — an insert, not a replacement — and the project sets neither `PublishAot` nor `JsonSerializerIsReflectionEnabledByDefault`, so the reflection resolver still backs the chain under `dotnet test`. An unregistered DTO passes every test and fails only in an AOT publish. Register it because it is required, not because a test will tell you.
- **`dotnet build Scrinia.sln -c Debug` must be clean before any test run.**
- **Never** run two `dotnet test` invocations against one project concurrently.
- **Never** run `dotnet build` while a test run is in flight — it fails on locked DLLs and can wedge the run.
- Apparent test hangs are orphaned `testhost.exe` / `dotnet.exe` holding `FileLock`s on `%TEMP%` stores. Clear them before a full run.
- **`cmds.txt` is untracked and is not part of this work.** Never `git add .` — stage explicit paths only.
- Test projects in scope: `Scrinia.Tests`, `Scrinia.Server.Tests`, `Scrinia.Retrieval.Benchmarks.Tests`, `Scrinia.Plugin.Embeddings.Tests`. `Scrinia.Merge.Tests` is unaffected (spec fact 18).
- **The MCP YAML envelope stays byte-identical.** Roughly 30 existing tests assert on rendered strings. No task here changes rendering.
- Known pre-existing flake, not caused by this branch: `CircuitBreakerRegistryTests.GetAll_ReturnsAllRegistered` races with `HealthEndpointTests` over a process-wide static registry.

---

## Established Facts

Measured against `fe8bcef` (branch point). Each carries the command that produced it — **re-run rather than re-trust.** The line numbers in the source spec are stale; these supersede them.

**F1. `IMemoryStore` declares five `SearchAll` overloads; four carry default bodies.**
`rg -n "SearchAll" src/Scrinia.Core/IMemoryStore.cs`
→ `:61` (abstract, the only one without a body), `:69`, `:99`, `:116`, `:132`. The `:132` body post-filters with `SearchResultFilter.Matches` after the top-K cut; `:99` and `:116` post-filter `excludeTopics` after it. `:109-111` is a doc comment stating that excluded topics must be removed *before* the top-K cut, immediately above code that removes them after it. (`:92` merely says the default post-filters — it does not call it wrong.)

**F2. `FileMemoryStore` carries four `SearchAll` bodies plus one forwarder.**
`rg -n "public IReadOnlyList<SearchResult> SearchAll" src/Scrinia.Core/FileMemoryStore.cs`
→ `:1216` (forwarder to `:1219`), `:1219`, `:1292`, `:1316`, `:1342`. All four bodies share the sequence `new WeightedAttributeScorer(RankerOptions)` → `ListScoped(...)` → `BuildTopicInfosFromCandidates` → `searcher.SearchAll`. `:1292` and `:1316` differ only in whether `supplementalScores` is threaded through.

**F3. `HttpMemoryStore` implements only the abstract overload.**
`rg -n "public IReadOnlyList<SearchResult> SearchAll" src/Scrinia/HttpMemoryStore.cs` → one hit, `:329`. It GETs `{BaseUrl}/search?q=&limit=&scopes=`. Every filtering overload therefore takes the broken interface default.

**F4. Exactly three types implement `IMemoryStore`.**
`rg -n ": IMemoryStore\b" --glob '*.cs'`
→ `src/Scrinia.Core/FileMemoryStore.cs:23`, `src/Scrinia/HttpMemoryStore.cs:17`, and one hand-written test double at `tests/Scrinia.Tests/Embeddings/HybridScorerTests.cs:241` (`FakeMemoryStore`, whose `SearchAll` is at `:253`). Removing the default bodies breaks exactly these three plus every call site.

**F5. `HttpMemoryStore` accepts an injected `HttpClient`, and its URLs are relative.**
`src/Scrinia/HttpMemoryStore.cs:128` — `public HttpMemoryStore(HttpClient http, string store = "default", TimeSpan? timeout = null, CircuitBreaker? circuitBreaker = null, RetryOptions? retryOptions = null)`.
`src/Scrinia/HttpMemoryStore.cs:143` — `private string BaseUrl => $"/api/v1/stores/{Uri.EscapeDataString(_store)}";`
So a `WebApplicationFactory` client (BaseAddress `http://localhost/`) drives it end-to-end with no server process.

**F6. `HttpMemoryStore.SearchAll` sends *synchronously*, and TestServer's in-memory handler REFUSES synchronous sends — so the conformance suite must run over a real loopback socket.**
`src/Scrinia/HttpMemoryStore.cs:66` — `return _http.Send(clone, HttpCompletionOption.ResponseContentRead);` inside `SendSync`. Every `SearchAll` path goes through it.

`Microsoft.AspNetCore.TestHost.ClientHandler` does declare an override of `Send` — **and that override exists in order to throw.** A probe built against the repo's own `tests/Scrinia.Server.Tests/bin/Debug/net10.0/Microsoft.AspNetCore.TestHost.dll` (10.0.0+b0f34d5) produced, for both GET and POST:

> `System.NotSupportedException: This synchronous method is not supported due to the risk of threadpool exhaustion when running multiple tests in parallel. Use the asynchronous version of this method instead.`
> `   at Microsoft.AspNetCore.TestHost.ClientHandler.Send(HttpRequestMessage, CancellationToken)`

Setting `TestServer.AllowSynchronousIO = true` does **not** change it; the throw is unconditional and there is no server-side switch that enables it.

**Therefore the fixture must not use the in-memory handler.** `WebApplicationFactory<T>` exposes public `UseKestrel` (three overloads) and `StartServer` methods — confirmed by reading the type's method table out of `tests/Scrinia.Server.Tests/bin/Debug/net10.0/Microsoft.AspNetCore.Mvc.Testing.dll`. Calling `UseKestrel(0)` then `StartServer()` **before** anything touches `Services` or `CreateClient()` binds a real ephemeral loopback port and makes `CreateClient()` hand back a client over a real `SocketsHttpHandler`, which supports `Send`. Task 1 Step 5 smoke-tests this before anything is built on top.

*This fact was wrong in an earlier draft of this plan*, which asserted that TestServer supports synchronous sends on the strength of the `Send` override merely existing in the method table — a check that could only ever have proven the method exists, never what it does. It was caught by the plan's verification pass, which built and ran a probe instead of reading metadata.

**F7. `Scrinia.Server.Tests` cannot see `HttpMemoryStore` today; adding the reference is safe.**
`rg -n "ProjectReference" tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj` → `Scrinia.Server`, `Scrinia.Core`, `Scrinia.Plugin.Abstractions`. `HttpMemoryStore` lives in `src/Scrinia` (assembly `scri`).
Adding `src/Scrinia` does **not** create a `Program` ambiguity: `rg -rn "class Program" src/Scrinia/ src/Scrinia.Server/` finds exactly one, `src/Scrinia.Server/Program.cs:547` (`public partial class Program { }`). The CLI's `Program` is compiler-generated and internal, and `rg -n "InternalsVisibleTo" src/Scrinia/Scrinia.csproj` shows it is exposed only to `Scrinia.Tests` — not to `Scrinia.Server.Tests`. So `WebApplicationFactory<Program>` in `ScriniaServerFactory.cs:12` keeps resolving to the server's.

**F8. The server's live store instance is reachable from the test factory.**
`src/Scrinia.Server/Program.cs:434` — `reqCtx.Store = sm.GetStore(storeName);`
`src/Scrinia.Server/Services/StoreManager.cs:27` — `public IMemoryStore GetStore(string storeName)`, cached per name.
So `factory.Services.GetRequiredService<StoreManager>().GetStore(factory.PrimaryStore)` returns *the same instance* the endpoints use. The conformance suite compares a local handle and a remote handle onto one store — not two stores that merely share a directory, which would fight over `FileMemoryStore`'s index cache.

**F9. There is exactly one search endpoint, and it is GET-only with three parameters.**
`src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:29` — `group.MapGet("/search", SearchMemories);`
`:249-256` — `SearchMemories(string store, RequestContext ctx, PluginPipeline pipeline, CancellationToken ct, string? q = null, string? scopes = null, int limit = 20)`.
No `SearchRequest` type exists anywhere: `rg -n "SearchRequest" --glob '*.cs' .` → no matches.

**F10. `PluginPipeline.SearchAsync` is the drifted duplicate.**
`src/Scrinia.Server/Services/PluginPipeline.cs:163-189` vs `src/Scrinia.Mcp/MemoryTools.Core.cs:849-903`. Same ambient contributor lookup, same `ListScoped`, same cached-vs-scoped fact-store branch, same fusion decision. The pipeline copy has **no** `excludeTopics`, `pathPrefix`, `attributeFilter`, or `context` augmentation.
It drifted despite being able to call the original: `rg -n "InternalsVisibleTo" src/Scrinia.Mcp/Scrinia.Mcp.csproj` shows `scri`, `Scrinia.Tests`, **and `Scrinia.Server`**, and `Scrinia.Server.csproj` already references `Scrinia.Mcp`. Visibility was never the obstacle.

**F11. `ExecuteSearchAsync` has two production callers and seven test callers.**
`rg -n "ExecuteSearchAsync|SearchOutcome" --glob '*.cs'`
→ production: `src/Scrinia.Mcp/MemoryTools.Core.cs:820` (MCP `search`), `src/Scrinia/Commands/ScriniaCommands.Memory.cs:230` (CLI `scri search`).
→ tests: `tests/Scrinia.Tests/AutoSurfaceEntityFactsTests.cs` at `:28, :44, :57, :73, :147, :152, :212`.
→ `SearchOutcome` is declared at `MemoryTools.Core.cs:835` as an `internal readonly record struct` nested on `ScriniaMcpTools`.
(`Scrinia.Server/Chat/AgentLoop.cs:158` has a *private, unrelated* method also named `ExecuteSearchAsync` — it is the chat tool dispatcher, not this pipeline. Do not confuse them.)

**F12. Every dependency of the pipeline already lives in `Scrinia.Core/Search/`.** Spec fact 14 confirmed.
`ls src/Scrinia.Core/Search/` → `EntityQuerySeeder.cs`, `EntityFactSummarizer.cs` (in `EntityFactSummary.cs`), `EntityFactDerivation.cs`, `SearchResultFilter.cs`, `SearchResults.cs`, `ISearchScoreContributor.cs`, `EntityGraphScoreContributor.cs`.
`rg -rn "class SearchContributorContext|class EntityGraphScoreContext" src/` → both in `src/Scrinia.Core/Search/ISearchScoreContributor.cs`, at `:25` and `:48`. The move to Core adds no new dependency edge.

**F13. The source spec is wrong about which tests need rewriting.**
The spec says "`SearchExcludeTopicsTests` and `HybridScorerTests` are rewritten to the new signature."
`rg -n "SearchAll" tests/Scrinia.Tests/SearchExcludeTopicsTests.cs` → only doc-comment mentions, at `:13` and `:17`. Its tests drive the MCP layer — `await new ScriniaMcpTools().MemorySearch(` at `:81` — and never touch `store.SearchAll`. **It needs no rewrite and is a behavioural pin that must keep passing untouched.**
`HybridScorerTests` does call it directly — `:45, :54, :64, :65, :84` plus the `FakeMemoryStore` implementation at `:253`.

**F14. Call sites in Task 4 — 42 rows below, of which 4 are deleted rather than migrated, leaving 38 to migrate.**
Produced by `rg -n "\.SearchAll\(" --glob '*.cs'` and then discarding hits on `WeightedAttributeScorer` (`scorer.` / `searcher.` receivers) and doc comments — **except** the four `FileMemoryStore.cs` rows, which are receiver-less `SearchAll(...)` self-calls that command cannot match; those came from `rg -n "SearchAll" src/Scrinia.Core/FileMemoryStore.cs`. Re-run both.

| File | Lines |
|---|---|
| `src/Scrinia.Mcp/MemoryTools.Core.cs` | 895 |
| `src/Scrinia.Server/Services/PluginPipeline.cs` | 187, 188 |
| `src/Scrinia.Server/Chat/AgentLoop.cs` | 168 |
| `src/Scrinia/Commands/Hint.cs` | 80 |
| `src/Scrinia.Core/FileMemoryStore.cs` | 1217, 1296, 1321, 1352 (internal forwarders — deleted, not migrated) |
| `benchmarks/…/Core/AgenticScriniaRetriever.cs` | 212, 218, 222, 286, 292, 296 |
| `benchmarks/…/Core/ScriniaRetriever.cs` | 326 |
| `tests/Scrinia.Tests/AttributeExtractorTests.cs` | 153, 168, 182 |
| `tests/Scrinia.Tests/ChunkIndexingTests.cs` | 128, 145, 203, 227 |
| `tests/Scrinia.Tests/Embeddings/HybridScorerTests.cs` | 45, 54, 64, 65, 84 |
| `tests/Scrinia.Tests/EntityGraphSourceSearchTests.cs` | 47, 73, 92, 94 |
| `tests/Scrinia.Tests/FileMemoryStoreTests.cs` | 221 |
| `tests/Scrinia.Tests/FileMemoryStoreCacheTests.cs` | 101 |
| `tests/Scrinia.Tests/SearchPathAttributeFilterTests.cs` | 48, 75, 89, 91 |
| `tests/Scrinia.Tests/Benchmarks/EmbeddingBenchmarkTests.cs` | 506 |
| `tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingBenchmarkAllTests.cs` | 470 |
| `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticIngestHarnessTests.cs` | 45, 118 |

The benchmark retrievers hold a **concrete** `FileMemoryStore` (`AgenticScriniaRetriever.cs:88`, `ScriniaRetriever.cs:71`), so collapsing `FileMemoryStore`'s bodies breaks them too — they are not shielded by the interface.

Five `<see cref="...SearchAll(...)"/>` doc references will dangle once the overloads are deleted and should be updated in the same commit: `benchmarks/…/Core/ScriniaRetriever.cs:23`, `src/Scrinia.Core/Search/RankerOptions.cs:6` and `:36`, `tests/Scrinia.Tests/EntityGraphSourceSearchTests.cs:10`, `tests/Scrinia.Tests/FileMemoryStoreCacheTests.cs:12`. They will **not** break the build — no project in the repo sets `GenerateDocumentationFile`, so CS1574 is never emitted despite `TreatWarningsAsErrors=true` (`Directory.Build.props:44-46`). Fix them anyway; a dangling cref is a lie in the docs.

---

## Deliberate scope exclusions

Stated here so a reviewer does not read them as omissions.

**The `LoadIndex` narrowing in the spec is rejected, with evidence.** The spec proposes narrowing `LoadIndex` to `IReadOnlyList<ArtifactEntry>` so `LoadIndexUnsafe:593` can return `cached.Entries` instead of `.ToList()`.
`rg -rn "LoadIndex\(" src/ --glob '*.cs'` finds 23 sites, and the majority bind the result to a mutable local that is then mutated and re-saved — `MemoryTools.Core.cs:295`, `:432`, `:1153`, `MemoryOrchestrator.cs:106`, `:259`, `FileMemoryStore.cs:1631`, `:1649`, `:1666`, `MaintenanceEventSink.cs:56`, `:83`.
Narrowing the return type does not remove the copy; it pushes an explicit `.ToList()` into every mutating caller — the same number of copies, more churn — while handing read-only callers a live alias into the index cache that can change under them. The `.ToList()` is defensive copying those callers depend on. **The real F5 win is `SearchQuery.Candidates`, which removes an entire second `ListScoped` (a per-scope file-lock acquisition and full entry copy), not one list copy.** Record to residuals; do not implement.

**Remote mode still drops client-computed supplemental and entity-graph scores.** `HttpMemoryStore` scores server-side, so `SupplementalScores` / `EntityGraphScores` on a `SearchQuery` handed to it are meaningless and are ignored — exactly as today's default overloads ignore them. Whether the pipeline should skip computing them at all for a remote store is a store-capability question and belongs to Branch A3 (F3). Document it on the method; record to residuals.

**`HttpMemoryStore`'s fabricated result metadata** (`OriginalBytes: 0`, `CreatedAt: DateTimeOffset.UtcNow` at `HttpMemoryStore.cs:365-367`) is a known limitation per the spec's out-of-scope list. The conformance suite must therefore **not** compare those fields.

---

## File Structure

**Created**
- `src/Scrinia.Core/Search/SearchQuery.cs` — the single search request record.
- `src/Scrinia.Core/Search/SearchPipeline.cs` — the one scoring pipeline; holds `SearchOutcome`.
- `tests/Scrinia.Server.Tests/Conformance/StoreConformanceFixture.cs` — one server, one corpus, two handles onto it.
- `tests/Scrinia.Server.Tests/Conformance/StoreConformanceTests.cs` — the parity assertions.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/SearchCostCommand.cs` — the F5 measurement.

**Modified**
- `src/Scrinia.Core/IMemoryStore.cs` — five search members become one.
- `src/Scrinia.Core/FileMemoryStore.cs` — four bodies become one.
- `src/Scrinia/HttpMemoryStore.cs` — real filter transport.
- `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs` — GET extended, POST added.
- `src/Scrinia.Server/Models/ApiDtos.cs`, `ServerJsonContext.cs` — `SearchRequest`.
- `src/Scrinia.Server/Services/PluginPipeline.cs` — filters, then delegation.
- `src/Scrinia.Mcp/MemoryTools.Core.cs` — pipeline moves out.
- `tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj` — one project reference.

---

## Task 1: Store conformance harness

**Why first:** the spec's stated mitigation for Task 4's risk — "the conformance suite lands first, so the mechanical commit has something to be judged against." This task lands only the parity that is *already true*; the parity that is currently broken lands in Task 3 alongside its fix, so no commit is red.

**Files:**
- Modify: `tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj`
- Create: `tests/Scrinia.Server.Tests/Conformance/StoreConformanceFixture.cs`
- Create: `tests/Scrinia.Server.Tests/Conformance/StoreConformanceTests.cs`

**Interfaces:**
- Consumes: `ScriniaServerFactory` (`tests/Scrinia.Server.Tests/ScriniaServerFactory.cs`), `StoreManager.GetStore`, `HttpMemoryStore(HttpClient, string)`.
- Produces: `StoreConformanceFixture` with `IMemoryStore Local`, `IMemoryStore Remote`, `IReadOnlyList<string> SeededNames`, and `static string[] Names(IMemoryStore store, IEnumerable<SearchResult> results)` — **two** parameters; Tasks 3, 4 and 5 all call the two-argument form.

**Assertion discipline — read before writing any test.** The server registers *some* `ISearchScoreContributor` in every configuration this fixture can hit, so a remote search is scored with a supplemental channel a bare local `SearchAll` does not use. The registration graph (`src/Scrinia.Server/Program.cs:104-175`) is: if any loaded plugin already implements `ISearchScoreContributor` the whole block is skipped; otherwise, with an embedding provider available, `:146-149` registers a `CompositeSearchScoreContributor(BuiltInEmbeddingsService, new PathSegmentScoreContributor())` via `AddSingleton` — that is the primary path; `:167` and `:173` are `TryAddSingleton` fallbacks for when the provider is unavailable or its init threw. `SearchContributorContext.Current` is then set per request at `:292` / `:449`.

**Ranking therefore legitimately differs between the two handles, and asserting on order would pin a falsehood.** Every conformance assertion must be ranking-independent by construction: compare the *set* of returned names, with a `limit` large enough that both stores return every match. Where a case is about fill-to-limit, assert the count of matching entries, which is also ranking-independent. Never assert on `Score`, `CreatedAt`, or `OriginalBytes` (see scope exclusions).

- [ ] **Step 1: Add the project reference**

In `tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj`, inside the existing `<ItemGroup>` that holds the other `ProjectReference` elements:

```xml
    <ProjectReference Include="..\..\src\Scrinia\Scrinia.csproj" />
```

- [ ] **Step 2: Verify the build still resolves `Program`**

Run: `dotnet build Scrinia.sln -c Debug`
Expected: 0 errors. Specifically, no `CS0104: 'Program' is an ambiguous reference` in `ScriniaServerFactory.cs`. Per F7 this cannot happen, but it is the one way this step fails and it fails loudly.

- [ ] **Step 3: Write the fixture**

Create `tests/Scrinia.Server.Tests/Conformance/StoreConformanceFixture.cs`:

**xunit is 2.9.3** (`tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj:14`), so `IAsyncLifetime` members return `Task`, not `ValueTask`. Do not "modernize" them.

```csharp
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core;
using Scrinia.Core.Search;
using Scrinia.Server.Models;
using Scrinia.Server.Services;
using Xunit;

namespace Scrinia.Server.Tests.Conformance;

/// <summary>
/// One server, one corpus, two handles onto it: <see cref="Local"/> is the very
/// <see cref="IMemoryStore"/> instance the endpoints use (resolved from <see cref="StoreManager"/>,
/// not a second store opened on the same directory — two FileMemoryStore instances would fight
/// over the per-scope index cache and produce spurious differences). <see cref="Remote"/> is an
/// <c>HttpMemoryStore</c> driven over a real loopback socket.
///
/// It must be a real socket, not TestServer's in-memory handler: HttpMemoryStore sends
/// synchronously and Microsoft.AspNetCore.TestHost.ClientHandler.Send throws
/// NotSupportedException unconditionally ("risk of threadpool exhaustion"), with no server-side
/// switch to enable it. UseKestrel(0) + StartServer bind an ephemeral port before anything
/// touches Services or CreateClient, so CreateClient hands back a client over a real
/// SocketsHttpHandler.
///
/// The corpus is seeded through the REST API so it travels the real write path. Because Local is
/// the server's own instance, those writes are immediately visible to it.
/// </summary>
public sealed class StoreConformanceFixture : IAsyncLifetime
{
    private readonly ScriniaServerFactory _factory = new();
    private HttpClient _client = null!;

    public IMemoryStore Local { get; private set; } = null!;
    public IMemoryStore Remote { get; private set; } = null!;

    /// <summary>Every memory name seeded, in insertion order.</summary>
    public IReadOnlyList<string> SeededNames { get; private set; } = [];

    public async Task InitializeAsync()
    {
        // Order is load-bearing: UseKestrel must run before the host is materialized, which the
        // first touch of Services or CreateClient would do.
        _factory.UseKestrel(0);
        _factory.StartServer();

        _client = _factory.CreateAuthenticatedClient();
        Local = _factory.Services.GetRequiredService<StoreManager>().GetStore(_factory.PrimaryStore);
        // HttpMemoryStore's constructor assigns _http.Timeout, which throws if the HttpClient has
        // already issued a request — so construct it before the first SeedAsync.
        Remote = new HttpMemoryStore(_client, _factory.PrimaryStore);

        var names = new List<string>();

        // Ten entries under /api/, ten under /goal/, five ephemeral-free plain names. The path
        // split is what pathPrefix cases filter on; the counts are what fill-to-limit asserts.
        for (int i = 0; i < 10; i++)
            names.Add(await SeedAsync($"/api/widget-{i}", $"widget alpha handling number {i}"));
        for (int i = 0; i < 10; i++)
            names.Add(await SeedAsync($"/goal/G-1/widget-{i}", $"widget alpha planning number {i}"));
        for (int i = 0; i < 5; i++)
            names.Add(await SeedAsync($"note-{i}", $"widget alpha loose note number {i}"));

        SeededNames = names;
    }

    private async Task<string> SeedAsync(string name, string body)
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new StoreRequest([body], name, body));
        resp.EnsureSuccessStatusCode();
        return name;
    }

    public Task DisposeAsync()
    {
        (Remote as IDisposable)?.Dispose();
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Ordered-insensitive comparison key: the qualified label of each result.
    /// Score, CreatedAt and OriginalBytes are deliberately excluded — HttpMemoryStore
    /// fabricates the latter two (a documented limitation), and scoring differs because the
    /// server always has a PathSegmentScoreContributor registered.</summary>
    public static string[] Names(IMemoryStore store, IEnumerable<SearchResult> results) =>
        results.Select(r => r switch
        {
            EntryResult er => store.FormatQualifiedName(er.Item.Scope, er.Item.Entry.Name),
            ChunkEntryResult cr => store.FormatQualifiedName(cr.ParentItem.Scope, cr.ParentItem.Entry.Name),
            TopicResult tr => MemoryNaming.FormatScopeLabel(tr.Scope),
            _ => "",
        }).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
}

[CollectionDefinition(nameof(StoreConformanceCollection))]
public sealed class StoreConformanceCollection : ICollectionFixture<StoreConformanceFixture>;
```

- [ ] **Step 4: Write the smoke test and unfiltered parity**

Create `tests/Scrinia.Server.Tests/Conformance/StoreConformanceTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Server.Tests.Conformance;

/// <summary>
/// The test that would have caught the original defect: the same query, the same corpus, both
/// store implementations, identical result sets. Assertions are ranking-independent by
/// construction — see StoreConformanceFixture for why order can legitimately differ.
/// </summary>
[Collection(nameof(StoreConformanceCollection))]
public sealed class StoreConformanceTests(StoreConformanceFixture fx)
{
    /// <summary>HttpMemoryStore.SearchAll sends synchronously (HttpClient.Send), which TestServer's
    /// in-memory handler refuses. This asserts the fixture's Kestrel loopback host is really in
    /// play, because every other test in this file depends on it.</summary>
    [Fact]
    public void Remote_SynchronousSend_Works()
    {
        var results = fx.Remote.SearchAll("widget", scopes: null, limit: 50);

        results.Should().NotBeEmpty(because: "the fixture seeded 25 entries all containing 'widget'");
    }

    [Theory]
    [InlineData("widget")]
    [InlineData("alpha")]
    [InlineData("planning")]
    [InlineData("nonexistentterm")]
    public void Unfiltered_BothStores_ReturnSameEntries(string query)
    {
        var local = StoreConformanceFixture.Names(fx.Local, fx.Local.SearchAll(query, scopes: null, limit: 100));
        var remote = StoreConformanceFixture.Names(fx.Remote, fx.Remote.SearchAll(query, scopes: null, limit: 100));

        remote.Should().BeEquivalentTo(local);
    }

    [Fact]
    public void Unfiltered_LimitIsHonoured_ByBothStores()
    {
        fx.Local.SearchAll("widget", scopes: null, limit: 5).Should().HaveCountLessThanOrEqualTo(5);
        fx.Remote.SearchAll("widget", scopes: null, limit: 5).Should().HaveCountLessThanOrEqualTo(5);
    }
}
```

- [ ] **Step 5: Run the new tests**

Run: `dotnet test tests/Scrinia.Server.Tests --filter "FullyQualifiedName~Conformance"`
Expected: all PASS. If `Remote_SynchronousSend_Works` throws `NotSupportedException` with "This synchronous method is not supported due to the risk of threadpool exhaustion", the Kestrel host did not take effect — check that `UseKestrel(0)` / `StartServer()` run before anything touches `Services`. **Do not** work around it by making the test async or by dropping the remote handle; the whole point is exercising the real client. If it cannot be made to work, **stop and report BLOCKED**.

- [ ] **Step 6: Run the full server suite**

Run: `dotnet build Scrinia.sln -c Debug` then `dotnet test tests/Scrinia.Server.Tests`
Expected: every pre-existing test still passes (record the baseline count from this run — it is roughly 132), plus the new ones. Watch for cross-test interference: this fixture now binds a real port and holds a live host, where the other suites use the in-memory one.

- [ ] **Step 7: Commit**

```bash
git add tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj tests/Scrinia.Server.Tests/Conformance
git commit -m "test(store): pin local/remote search parity with a conformance suite"
```

---

## Task 2: Server-side filter transport

**Why now:** `HttpMemoryStore` cannot filter until the wire can carry filters. This task changes only the server, lands green, and is judged on its own endpoint tests.

**Files:**
- Modify: `src/Scrinia.Server/Models/ApiDtos.cs`
- Modify: `src/Scrinia.Server/Models/ServerJsonContext.cs`
- Modify: `src/Scrinia.Server/Services/PluginPipeline.cs:163-189`
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:29`, `:249-299`
- Test: `tests/Scrinia.Server.Tests/MemoryEndpointTests.cs`

**Interfaces:**
- Produces, for Task 3: `POST /api/v1/stores/{store}/search` accepting `SearchRequest`, and `GET /api/v1/stores/{store}/search` additionally accepting `excludeTopics` and `pathPrefix`. Both return the existing `SearchResponse` shape unchanged.
- Produces, for Task 5: `PluginPipeline.SearchAsync(IMemoryStore, string, string?, int, string?, string?, IReadOnlyDictionary<string,string[]>?, CancellationToken)`.

**Corrected during Task 2.** An earlier draft kept `CancellationToken ct = default` in its original 5th position and appended the filter parameters after it, which trips `CA1068` ("CancellationToken must be the last parameter") — and `Directory.Build.props:45` sets `TreatWarningsAsErrors=true`, so it is a hard build failure. The first implementer suppressed it with a `#pragma`. Suppressing a correct analyzer to preserve a parameter order that nothing depends on is the wrong trade: this method has exactly one caller in the repo (`MemoryEndpoints.cs:304`), which this same task edits. **`ct` goes last.**

- [ ] **Step 1: Add the request DTO**

In `src/Scrinia.Server/Models/ApiDtos.cs`, immediately above `public sealed record SearchResponse`:

```csharp
/// <summary>Body of POST /search. Exists because attributeFilter is a dictionary of arrays and
/// does not belong in a query string. Mirrors the GET parameters exactly otherwise.</summary>
public sealed record SearchRequest(
    string Q,
    string? Scopes = null,
    int Limit = 20,
    string? ExcludeTopics = null,
    string? PathPrefix = null,
    Dictionary<string, string[]>? AttributeFilter = null);
```

In `src/Scrinia.Server/Models/ServerJsonContext.cs`, add alongside the other attributes:

```csharp
[JsonSerializable(typeof(SearchRequest))]
```

- [ ] **Step 2: Widen `PluginPipeline.SearchAsync`**

Replace `src/Scrinia.Server/Services/PluginPipeline.cs:163-189` with:

```csharp
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        IMemoryStore store, string query, string? scopes, int limit,
        string? excludeTopics = null,
        string? pathPrefix = null,
        IReadOnlyDictionary<string, string[]>? attributeFilter = null,
        CancellationToken ct = default)
    {
        var contributor = SearchContributorContext.Current;
        var entityContributor = EntityGraphScoreContext.Current;

        IReadOnlyDictionary<string, double>? supplemental = null;
        IReadOnlyDictionary<string, double>? entityScores = null;
        if (contributor is not null || entityContributor is not null)
        {
            // Contributors must see the same excludeTopics-filtered pool the scorer will, so an
            // excluded topic cannot influence scoring.
            var candidates = store.ListScoped(scopes, excludeTopics);
            if (contributor is not null)
                supplemental = await contributor.ComputeScoresAsync(query, candidates, store, ct);
            if (entityContributor is not null)
            {
                var factStore = string.IsNullOrWhiteSpace(scopes) && string.IsNullOrWhiteSpace(excludeTopics)
                    ? store.GetEntityFactStore()                       // cached, no rebuild
                    : EntityFactDerivation.BuildStore(candidates);     // scoped / topic-excluded
                entityScores = entityContributor.Score(factStore, query);
            }
        }

        // Every filter is pushed into SearchAll so it applies BEFORE the top-K cut. Empty
        // supplemental is normalized to null so the scorer skips the fusion source entirely.
        var supp = supplemental is { Count: > 0 } ? supplemental : null;
        return store.SearchAll(query, scopes, limit, excludeTopics, supp, entityScores,
            pathPrefix, attributeFilter);
    }
```

Note the branch on `supplemental is { Count: > 0 } || entityScores is { Count: > 0 }` is gone: the eight-argument overload handles null score dictionaries, so the branch only existed to pick an overload.

- [ ] **Step 3: Extend GET and add POST**

In `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs`, after line 29's `group.MapGet("/search", SearchMemories);` add:

```csharp
        group.MapPost("/search", SearchMemoriesPost);
```

Replace the body of `SearchMemories` (`:249-299`) with a parameter-widened signature that delegates to a shared core, and add the POST handler beside it:

```csharp
    private static Task<IResult> SearchMemories(
        string store,
        RequestContext ctx,
        PluginPipeline pipeline,
        CancellationToken ct,
        string? q = null,
        string? scopes = null,
        int limit = 20,
        string? excludeTopics = null,
        string? pathPrefix = null)
        => RunSearch(ctx, pipeline, q, scopes, limit, excludeTopics, pathPrefix, null, ct);

    private static Task<IResult> SearchMemoriesPost(
        string store,
        SearchRequest req,
        RequestContext ctx,
        PluginPipeline pipeline,
        CancellationToken ct)
        => RunSearch(ctx, pipeline, req.Q, req.Scopes, req.Limit, req.ExcludeTopics, req.PathPrefix,
            req.AttributeFilter, ct);

    /// <summary>The one search implementation behind both GET and POST. GET carries the plain-string
    /// filters; POST additionally carries attributeFilter, which is a dictionary of arrays and does
    /// not belong in a query string.</summary>
    private static async Task<IResult> RunSearch(
        RequestContext ctx,
        PluginPipeline pipeline,
        string? q,
        string? scopes,
        int limit,
        string? excludeTopics,
        string? pathPrefix,
        Dictionary<string, string[]>? attributeFilter,
        CancellationToken ct)
    {
        if (!ctx.HasPermission("search"))
            return Results.Json(new ErrorResponse("Permission 'search' required."), statusCode: 403);

        if (string.IsNullOrWhiteSpace(q))
            return Results.BadRequest(new ErrorResponse("q parameter is required."));

        // Input bounds validation
        if (q.Length > 10_240)
            return Results.BadRequest(new ErrorResponse("Query too long (max 10KB)."));
        if (scopes is { Length: > 1024 })
            return Results.BadRequest(new ErrorResponse("Scopes parameter too long (max 1KB)."));
        if (excludeTopics is { Length: > 1024 })
            return Results.BadRequest(new ErrorResponse("excludeTopics parameter too long (max 1KB)."));
        if (pathPrefix is { Length: > 1024 })
            return Results.BadRequest(new ErrorResponse("pathPrefix parameter too long (max 1KB)."));
        if (attributeFilter is { Count: > 64 })
            return Results.BadRequest(new ErrorResponse("attributeFilter may not exceed 64 keys."));
        limit = Math.Clamp(limit, 1, 1000);

        var results = await pipeline.SearchAsync(ctx.Store!, q, scopes, limit,
            excludeTopics, pathPrefix, attributeFilter, ct);
        var mapped = results.Select<SearchResult, SearchResultItem>(r => r switch
        {
            EntryResult er => new SearchResultItem(
                "entry",
                er.Item.Scope == "ephemeral"
                    ? $"~{er.Item.Entry.Name}"
                    : ctx.Store!.FormatQualifiedName(er.Item.Scope, er.Item.Entry.Name),
                er.Score,
                AttributeReaders.PreviewText(er.Item.Entry.ContentPreview)),
            ChunkEntryResult cr => new SearchResultItem(
                "chunk",
                cr.ParentItem.Scope == "ephemeral"
                    ? $"~{cr.ParentItem.Entry.Name}"
                    : ctx.Store!.FormatQualifiedName(cr.ParentItem.Scope, cr.ParentItem.Entry.Name),
                cr.Score,
                cr.Chunk.ContentPreview ?? AttributeReaders.PreviewText(cr.ParentItem.Entry.ContentPreview),
                cr.Chunk.ChunkIndex,
                cr.TotalChunks),
            TopicResult tr => new SearchResultItem(
                "topic",
                MemoryNaming.FormatScopeLabel(tr.Scope),
                tr.Score,
                tr.Description),
            _ => new SearchResultItem("unknown", "", r.Score, null),
        }).ToArray();

        return Results.Ok(new SearchResponse(mapped));
    }
```

The `mapped` projection is moved verbatim — do not alter it, the response shape is pinned by existing tests.

- [ ] **Step 4: Write the endpoint tests**

Append to `tests/Scrinia.Server.Tests/MemoryEndpointTests.cs`. That class is `IClassFixture<ScriniaServerFactory>` with private fields `_factory`, `_client`, and `_base` (= `$"/api/v1/stores/{factory.PrimaryStore}"`) — use them; it has no seeding helper, so add the one below alongside the tests:

```csharp
    private async Task SeedAsync(string name, string body, string[]? tags = null)
    {
        var resp = await _client.PostAsJsonAsync($"{_base}/memories",
            new StoreRequest([body], name, body, Tags: tags));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Search_Get_WithPathPrefix_ExcludesOtherSubtrees()
    {
        await SeedAsync("/api/alpha-note", "alpha content");
        await SeedAsync("/goal/G-9/alpha-note", "alpha content");

        var resp = await _client.GetFromJsonAsync<SearchResponse>(
            $"{_base}/search?q=alpha&limit=50&pathPrefix=/api/");

        resp!.Results.Select(r => r.Name).Should().Contain(n => n.Contains("/api/alpha-note"));
        resp.Results.Select(r => r.Name).Should().NotContain(n => n.Contains("/goal/"));
    }

    [Fact]
    public async Task Search_Post_WithAttributeFilter_ReturnsOnlyMatchingEntries()
    {
        await SeedAsync("/api/tagged-note", "beta content", tags: ["urgent"]);
        await SeedAsync("/api/untagged-note", "beta content");

        var resp = await _client.PostAsJsonAsync($"{_base}/search",
            new SearchRequest("beta", Limit: 50,
                AttributeFilter: new Dictionary<string, string[]> { [KnownAttributes.TagKey] = ["urgent"] }));
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>();

        body!.Results.Select(r => r.Name).Should().Contain(n => n.Contains("tagged-note"));
        body.Results.Select(r => r.Name).Should().NotContain(n => n.Contains("untagged-note"));
    }

    [Fact]
    public async Task Search_Post_MissingQuery_ReturnsBadRequest()
    {
        var resp = await _client.PostAsJsonAsync($"{_base}/search", new SearchRequest("", Limit: 10));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
```

`KnownAttributes` is in `Scrinia.Core.Search` (`src/Scrinia.Core/Search/KnownAttributes.cs`) — add the `using` if the file lacks it. If `TagKey`'s value does not match how the store actually keys tags, read `KnownAttributes.cs` and use the real key rather than adjusting the assertion.

- [ ] **Step 5: Run the tests**

Run: `dotnet build Scrinia.sln -c Debug` then `dotnet test tests/Scrinia.Server.Tests`
Expected: all PASS, including the Task 1 conformance tests and the three new endpoint tests.

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Server tests/Scrinia.Server.Tests/MemoryEndpointTests.cs
git commit -m "feat(server): filter search server-side, with POST /search for attribute filters"
```

---

## Task 3: `HttpMemoryStore` filters over the wire

**Why now:** the transport exists. This is the task where remote filtering stops being a lie. The conformance cases that are red today go green here, in the same commit as the fix.

**Files:**
- Modify: `src/Scrinia/HttpMemoryStore.cs:329-395`
- Modify: `src/Scrinia.Core/MemoryNaming.cs`
- Modify: `tests/Scrinia.Server.Tests/Conformance/StoreConformanceTests.cs`

**Added after Task 1 — a real remote defect the conformance suite surfaced.** Task 1 had to exclude `TopicResult` from its cross-store comparison because every remote topic result comes back with `scope` hardcoded to `"local"`. The server sends a topic's name as `MemoryNaming.FormatScopeLabel(tr.Scope)` — a display label like `"api"` — and the client only recovers a scope when the name contains `':'` (`HttpMemoryStore.cs:355`), which a label never does. Two distinct topics collapse to one wrong label.

This branch exists to make remote search correct, and this task rewrites those exact lines, so it is fixed here rather than deferred. `FormatScopeLabel` (`src/Scrinia.Core/MemoryNaming.cs:61-68`) is invertible over the real scope set (`local`, `ephemeral`, `local:<topic>`), so the fix is its inverse, living beside it:

```csharp
    /// <summary>Inverse of <see cref="FormatScopeLabel"/>: recovers a scope from its display
    /// label. Needed by remote clients, which receive topic results labelled rather than scoped.
    /// Round-trips every scope this system produces — "local", "ephemeral", and "local:{topic}".
    /// </summary>
    public static string ParseScopeLabel(string label) => label switch
    {
        "local" => "local",
        "ephemeral" => "ephemeral",
        _ => $"local:{label}",
    };
```

Then in `SearchRemote`'s topic branch, replace `new TopicResult(scope, item.Name, ...)` with `new TopicResult(MemoryNaming.ParseScopeLabel(item.Name), item.Name, ...)` — the `scope` local computed from the `':'` test is for entry and chunk names and does not apply to topics.

Finally, **remove the `TopicResult` exclusion Task 1 added** to `Unfiltered_BothStores_ReturnSameEntries`, and delete the paragraph of the class doc comment that explains it. Topics must now compare equal across stores; if they do not, the fix is incomplete.

**Interfaces:**
- Produces, for Task 4: `private IReadOnlyList<SearchResult> SearchRemote(string query, string? scopes, int limit, string? excludeTopics, string? pathPrefix, IReadOnlyDictionary<string,string[]>? attributeFilter)` — the single wire implementation. Task 4 deletes the thin overloads around it and calls it from `SearchAll(SearchQuery)`.

- [ ] **Step 1: Add the request DTO to the client's JSON context**

In `src/Scrinia/HttpMemoryStore.cs`, add to the internal DTO block near `:112`:

```csharp
    internal sealed record SearchApiRequest(string Q, string? Scopes, int Limit, string? ExcludeTopics,
        string? PathPrefix, Dictionary<string, string[]>? AttributeFilter);
```

and register it with the other `[JsonSerializable]` attributes above `HttpStoreJsonContext` (`:96`):

```csharp
    [JsonSerializable(typeof(SearchApiRequest))]
```

- [ ] **Step 2: Replace `SearchAll` with a filter-aware wire method plus thin overloads**

Replace `src/Scrinia/HttpMemoryStore.cs:329-395` with:

```csharp
    public IReadOnlyList<SearchResult> SearchAll(string query, string? scopes = null, int limit = 20)
        => SearchRemote(query, scopes, limit, null, null, null);

    public IReadOnlyList<SearchResult> SearchAll(string query, string? scopes, int limit, string? excludeTopics,
        IReadOnlyDictionary<string, double>? entityGraphScores = null)
        => SearchRemote(query, scopes, limit, excludeTopics, null, null);

    public IReadOnlyList<SearchResult> SearchAll(string query, string? scopes, int limit, string? excludeTopics,
        IReadOnlyDictionary<string, double>? supplementalScores,
        IReadOnlyDictionary<string, double>? entityGraphScores = null)
        => SearchRemote(query, scopes, limit, excludeTopics, null, null);

    public IReadOnlyList<SearchResult> SearchAll(
        string query, string? scopes, int limit, string? excludeTopics,
        IReadOnlyDictionary<string, double>? supplementalScores,
        IReadOnlyDictionary<string, double>? entityGraphScores,
        string? pathPrefix,
        IReadOnlyDictionary<string, string[]>? attributeFilter)
        => SearchRemote(query, scopes, limit, excludeTopics, pathPrefix, attributeFilter);

    /// <summary>
    /// The one remote search path. GETs when every filter fits a query string; POSTs when an
    /// attribute filter is present, because that is a dictionary of arrays.
    /// <para><b>Supplemental and entity-graph scores are deliberately ignored.</b> They are keyed
    /// by local scope+name and computed by client-side plugins, while a remote store scores
    /// server-side. Passing them over the wire would require the server to trust client scores.
    /// Whether the pipeline should skip computing them for a remote store is a store-capability
    /// question and belongs to the F3 interface split.</para>
    /// </summary>
    private IReadOnlyList<SearchResult> SearchRemote(
        string query, string? scopes, int limit, string? excludeTopics,
        string? pathPrefix, IReadOnlyDictionary<string, string[]>? attributeFilter)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        using var request = attributeFilter is { Count: > 0 }
            ? BuildSearchPost(query, scopes, limit, excludeTopics, pathPrefix, attributeFilter)
            : BuildSearchGet(query, scopes, limit, excludeTopics, pathPrefix);

        using var resp = SendSync(request);
        if (!resp.IsSuccessStatusCode)
            return [];

        using var stream = resp.Content.ReadAsStream();
        var search = JsonSerializer.Deserialize(stream, HttpStoreJsonContext.Default.SearchApiResponse);
        if (search?.Results is null)
            return [];

        var results = new List<SearchResult>();
        foreach (var item in search.Results)
        {
            // Reconstruct the result type
            string scope = "local";
            string subject = item.Name;

            if (item.Name.Contains(':'))
            {
                var (s, subj) = ParseQualifiedName(item.Name);
                scope = s;
                subject = subj;
            }

            var artifactEntry = new ArtifactEntry(
                Name: subject,
                Uri: "",
                OriginalBytes: 0,
                ChunkCount: 1,
                CreatedAt: DateTimeOffset.UtcNow,
                ContentPreview: item.Description ?? "");

            if (item.Type == "chunk" && item.ChunkIndex.HasValue)
            {
                var chunkEntry = new ChunkEntry(
                    item.ChunkIndex.Value,
                    item.Description,
                    null, null);
                results.Add(new ChunkEntryResult(
                    new ScopedArtifact(scope, artifactEntry),
                    chunkEntry,
                    item.TotalChunks ?? 1,
                    item.Score));
            }
            else if (item.Type == "topic")
            {
                results.Add(new TopicResult(scope, item.Name, item.Description ?? "", 0, null, item.Score));
            }
            else
            {
                results.Add(new EntryResult(
                    new ScopedArtifact(scope, artifactEntry),
                    item.Score));
            }
        }

        return results;
    }

    private HttpRequestMessage BuildSearchGet(
        string query, string? scopes, int limit, string? excludeTopics, string? pathPrefix)
    {
        string url = $"{BaseUrl}/search?q={Uri.EscapeDataString(query)}&limit={limit}";
        if (scopes is not null)
            url += $"&scopes={Uri.EscapeDataString(scopes)}";
        if (!string.IsNullOrWhiteSpace(excludeTopics))
            url += $"&excludeTopics={Uri.EscapeDataString(excludeTopics)}";
        if (!string.IsNullOrWhiteSpace(pathPrefix))
            url += $"&pathPrefix={Uri.EscapeDataString(pathPrefix)}";
        return new HttpRequestMessage(HttpMethod.Get, url);
    }

    private HttpRequestMessage BuildSearchPost(
        string query, string? scopes, int limit, string? excludeTopics,
        string? pathPrefix, IReadOnlyDictionary<string, string[]> attributeFilter)
    {
        var body = new SearchApiRequest(
            query, scopes, limit, excludeTopics, pathPrefix,
            attributeFilter.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase));
        string json = JsonSerializer.Serialize(body, HttpStoreJsonContext.Default.SearchApiRequest);
        return new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/search")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
    }
```

The result-mapping loop is moved verbatim from the old `SearchAll`. Do not "improve" `OriginalBytes: 0` / `CreatedAt: DateTimeOffset.UtcNow` — they are a documented known limitation, out of scope, and the conformance suite does not compare them.

These four overrides cover interface members `:61`, `:99`, `:116` and `:132`. `:69` (`SearchAll(query, scopes, limit, supplementalScores, entityGraphScores)`) is deliberately **not** overridden — its interface default at `:72` forwards to the 3-argument member, which is overridden, and remote supplemental scores are ignored by design. Leaving it is correct, not an oversight.

- [ ] **Step 3: Add the filtered conformance cases**

Append to `tests/Scrinia.Server.Tests/Conformance/StoreConformanceTests.cs`:

```csharp
    [Fact]
    public void PathPrefix_BothStores_ReturnSameEntries()
    {
        var local = StoreConformanceFixture.Names(fx.Local,
            fx.Local.SearchAll("widget", null, 100, null, null, null, "/api/", null));
        var remote = StoreConformanceFixture.Names(fx.Remote,
            fx.Remote.SearchAll("widget", null, 100, null, null, null, "/api/", null));

        remote.Should().BeEquivalentTo(local);
        remote.Should().OnlyContain(n => n.StartsWith("/api/"));
    }

    [Fact]
    public void ExcludeTopics_BothStores_ReturnSameEntries()
    {
        var local = StoreConformanceFixture.Names(fx.Local,
            fx.Local.SearchAll("widget", null, 100, "G-1"));
        var remote = StoreConformanceFixture.Names(fx.Remote,
            fx.Remote.SearchAll("widget", null, 100, "G-1"));

        remote.Should().BeEquivalentTo(local);
    }

    /// <summary>The fill-to-limit case the interface's own doc comment describes and its default
    /// implementation broke: 25 seeded entries all match "widget", only 10 sit under /api/. A
    /// limit of 20 must return all 10 matching entries, not the /api/ remnant of an unfiltered
    /// top-20. Ranking-independent: 10 &lt; 20, so every match fits regardless of order.</summary>
    [Fact]
    public void PathPrefix_FillsToLimit_NotThePostLimitRemnant()
    {
        var remote = StoreConformanceFixture.Names(fx.Remote,
            fx.Remote.SearchAll("widget", null, 20, null, null, null, "/api/", null));

        remote.Should().HaveCount(10);
    }
```

- [ ] **Step 4: Run the conformance suite and confirm the three new cases were genuinely red**

Run: `dotnet build Scrinia.sln -c Debug` then `dotnet test tests/Scrinia.Server.Tests --filter "FullyQualifiedName~Conformance"`
Expected: all PASS.

Then prove they were red before the fix, so they are known to be load-bearing:

Run: `git stash push src/Scrinia/HttpMemoryStore.cs && dotnet build Scrinia.sln -c Debug && dotnet test tests/Scrinia.Server.Tests --filter "FullyQualifiedName~Conformance"; git stash pop`
Expected: `PathPrefix_FillsToLimit_NotThePostLimitRemnant` FAILS with a count well under 10 (the post-limit remnant). Record the observed count in your report. Rebuild afterwards.

- [ ] **Step 5: Run the full suites**

Run: `dotnet build Scrinia.sln -c Debug`, then `dotnet test tests/Scrinia.Server.Tests`, then `dotnet test tests/Scrinia.Tests` (sequentially — never concurrently).
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia/HttpMemoryStore.cs tests/Scrinia.Server.Tests/Conformance/StoreConformanceTests.cs
git commit -m "fix(remote): filter search on the server instead of post-filtering the top-K"
```

---

## Task 4: `SearchQuery` and the single interface member

**Why now:** both stores already filter correctly and the conformance suite is green. This diff is large and mechanical and carries **no behaviour claim** — the suite from Tasks 1 and 3 is what judges it.

**Files:**
- Create: `src/Scrinia.Core/Search/SearchQuery.cs`
- Modify: `src/Scrinia.Core/IMemoryStore.cs:59-144`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:1216-1371`
- Modify: `src/Scrinia/HttpMemoryStore.cs` (the thin overloads from Task 3)
- Modify: the 31 call sites in F14's table

**Interfaces:**
- Produces: `SearchQuery`, and `IReadOnlyList<SearchResult> IMemoryStore.SearchAll(SearchQuery query)`.

- [ ] **Step 1: Create `SearchQuery`**

Create `src/Scrinia.Core/Search/SearchQuery.cs`:

```csharp
using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>
/// Everything a store needs to answer one search. Replaced five <c>SearchAll</c> overloads whose
/// four default implementations post-filtered after the top-K cut — returning fewer than
/// <c>Limit</c> results whenever a filter was selective. A single member with no default body
/// means a store that cannot filter fails to compile rather than quietly under-filling.
/// </summary>
public sealed record SearchQuery(string Query)
{
    /// <summary>Optional comma-separated scope filter. Null means every readable scope.</summary>
    public string? Scopes { get; init; }

    /// <summary>Maximum results to return.</summary>
    public int Limit { get; init; } = 20;

    /// <summary>Optional comma-separated topic names to exclude, e.g. "plan,task".</summary>
    public string? ExcludeTopics { get; init; }

    /// <summary>Case-insensitive prefix on the qualified label, e.g. "/findings/".</summary>
    public string? PathPrefix { get; init; }

    /// <summary>AND across keys, OR within each key's values, case-insensitive.</summary>
    public IReadOnlyDictionary<string, string[]>? AttributeFilter { get; init; }

    /// <summary>Plugin-supplied scores (e.g. embeddings), keyed "{scope}|{name}" for entries and
    /// "{scope}|{name}|{chunkIndex}" for chunks. Scored as a fusion source.</summary>
    public IReadOnlyDictionary<string, double>? SupplementalScores { get; init; }

    /// <summary>Entity-graph scores, scored as their own fusion source — never summed into
    /// <see cref="SupplementalScores"/>.</summary>
    public IReadOnlyDictionary<string, double>? EntityGraphScores { get; init; }

    /// <summary>
    /// Pre-materialized candidates. When set, the store skips its own <c>ListScoped</c> — the
    /// caller already paid for it.
    /// <para><b>Contract:</b> the caller must have materialized these with the same
    /// <see cref="Scopes"/> and <see cref="ExcludeTopics"/> carried on this query. Handing over a
    /// differently-scoped list silently changes the result set. <see cref="SearchPipeline"/> is
    /// the only production caller that sets this, and it builds both from the same values.</para>
    /// <para><b>Staleness:</b> the list is a snapshot. A caller that materializes candidates,
    /// awaits something slow (an embedding provider), then searches will not see a memory written
    /// during that await — where re-listing inside the store would have. That is an accepted
    /// trade: search results were never transactional with concurrent writes, and the window is
    /// the same one the contributor already scored against.</para>
    /// </summary>
    public IReadOnlyList<ScopedArtifact>? Candidates { get; init; }
}
```

- [ ] **Step 2: Collapse the interface**

In `src/Scrinia.Core/IMemoryStore.cs`, delete lines **60-72** and 90-144 — that is: `ListScoped(string? scopes = null)` at `:60`, the abstract `SearchAll(string, string?, int)`, the supplemental overload, and the three post-filtering default bodies. Keep `ListScoped(string?, string?)` at `:83-88` and `ResolveReadScopes(string?, string?)` at `:151-158` exactly as they are; neither is part of this finding.

**The delete range starts at 60, not 61.** The replacement block below re-declares `ListScoped(string? scopes = null)`; leaving `:60` in place gives CS0111 (duplicate member). Either delete from 60 or drop that line from the block — not both, not neither.

In their place, under the `// Listing & Search` comment:

```csharp
    List<ScopedArtifact> ListScoped(string? scopes = null);

    /// <summary>
    /// The one search member. Takes a <see cref="SearchQuery"/> and has no default implementation
    /// on purpose: the five overloads this replaced carried four default bodies that post-filtered
    /// after the top-K cut, so any store that did not override them silently returned fewer than
    /// <c>Limit</c> results for a selective filter. A store that cannot filter must now say so at
    /// compile time.
    /// </summary>
    IReadOnlyList<SearchResult> SearchAll(SearchQuery query);
```

- [ ] **Step 3: Collapse `FileMemoryStore`**

Replace `src/Scrinia.Core/FileMemoryStore.cs:1216-1235` and `:1288-1371` (the four `SearchAll` bodies and the forwarder, keeping `BuildTopicInfosFromCandidates` at `:1237-1255` and `ListScoped(string?, string?)` at `:1263-1286`) with one body:

```csharp
    /// <summary>
    /// Filters candidates and topics BEFORE the scorer's top-K cut, so a selective filter fills to
    /// <c>Limit</c> instead of returning the post-limit remnant. Every filter dimension is a
    /// property of the parent candidate (path/attributes) or the topic (scope label); chunk results
    /// inherit their parent's, so filtering parents covers them.
    /// </summary>
    public IReadOnlyList<SearchResult> SearchAll(SearchQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return [];

        bool hasPath = !string.IsNullOrWhiteSpace(query.PathPrefix);
        bool hasAttr = query.AttributeFilter is { Count: > 0 };

        // Candidates set means the caller (SearchPipeline) already materialized this exact
        // scope+excludeTopics slice for the contributors — skip the second ListScoped.
        IReadOnlyList<ScopedArtifact> allCandidates =
            query.Candidates ?? ListScoped(query.Scopes, query.ExcludeTopics);

        IReadOnlyList<ScopedArtifact> entryCandidates = hasPath || hasAttr
            ? allCandidates
                .Where(c => SearchResultFilter.CandidateMatches(this, c, query.PathPrefix, query.AttributeFilter))
                .ToList()
            : allCandidates;

        // Topics carry no attributes: an active attribute filter drops them all. Otherwise filter
        // topics by their own scope label, derived from the UNFILTERED candidate set (a topic can
        // match even when its entries are filtered out by a longer path prefix, and vice versa).
        List<TopicInfo> topics;
        if (hasAttr)
            topics = [];
        else if (hasPath)
            topics = BuildTopicInfosFromCandidates(allCandidates)
                .Where(t => SearchResultFilter.TopicMatches(t, query.PathPrefix))
                .ToList();
        else
            topics = BuildTopicInfosFromCandidates(allCandidates);

        var searcher = new WeightedAttributeScorer(RankerOptions);
        return searcher.SearchAll(query.Query, entryCandidates, topics, query.Limit,
            query.SupplementalScores, query.EntityGraphScores);
    }
```

`BuildTopicInfosFromCandidates` currently takes `IReadOnlyList<ScopedArtifact>` — no signature change needed.

- [ ] **Step 4: Collapse `HttpMemoryStore`**

Delete the four thin overloads added in Task 3 and replace them with:

```csharp
    public IReadOnlyList<SearchResult> SearchAll(SearchQuery query)
        => SearchRemote(query.Query, query.Scopes, query.Limit, query.ExcludeTopics,
            query.PathPrefix, query.AttributeFilter);
```

`SearchRemote`, `BuildSearchGet` and `BuildSearchPost` are unchanged. `query.Candidates` is ignored — the server materializes its own; note that on the method with a one-line comment.

- [ ] **Step 5: Migrate the four production call sites**

`src/Scrinia.Mcp/MemoryTools.Core.cs:895`:

```csharp
        IReadOnlyList<SearchResult> filtered = store.SearchAll(new SearchQuery(effectiveQuery)
        {
            Scopes = scopes,
            Limit = limit,
            ExcludeTopics = excludeTopics,
            SupplementalScores = supp,
            EntityGraphScores = entityScores,
            PathPrefix = pathPrefix,
            AttributeFilter = attributeFilter,
        });
```

`src/Scrinia.Server/Services/PluginPipeline.cs` (the `return store.SearchAll(...)` from Task 2):

```csharp
        return store.SearchAll(new SearchQuery(query)
        {
            Scopes = scopes,
            Limit = limit,
            ExcludeTopics = excludeTopics,
            SupplementalScores = supp,
            EntityGraphScores = entityScores,
            PathPrefix = pathPrefix,
            AttributeFilter = attributeFilter,
        });
```

`src/Scrinia.Server/Chat/AgentLoop.cs:168`:

```csharp
        var results = store.SearchAll(new SearchQuery(query) { Limit = limit });
```

`src/Scrinia/Commands/Hint.cs:80`:

```csharp
        var raw = _store.SearchAll(new SearchQuery(prompt) { Limit = searchLimit });
```

- [ ] **Step 6: Migrate the benchmark and test call sites**

Every remaining site in F14's table is the same transliteration: positional arguments become initializers. `scopes: null` maps to omitting `Scopes`. Two worked examples covering both shapes present:

`benchmarks/Scrinia.Retrieval.Benchmarks/Core/AgenticScriniaRetriever.cs:212` —
`results = _store.SearchAll(query, scopes: null, searchLimit, supplemental, entityGraphScores: entityScores);`
becomes

```csharp
            results = _store.SearchAll(new SearchQuery(query)
            {
                Limit = searchLimit,
                SupplementalScores = supplemental,
                EntityGraphScores = entityScores,
            });
```

`tests/Scrinia.Tests/SearchPathAttributeFilterTests.cs:48` — the eight-argument call becomes a full initializer with `PathPrefix` and `AttributeFilter` set from the same locals.

`tests/Scrinia.Tests/Embeddings/HybridScorerTests.cs:253` — the `FakeMemoryStore` member becomes:

```csharp
        public IReadOnlyList<SearchResult> SearchAll(SearchQuery query) => [];
```

Do **not** touch `tests/Scrinia.Tests/SearchExcludeTopicsTests.cs` — per F13 it drives the MCP tool, not `SearchAll`, and it is a behavioural pin that must keep passing unchanged.

- [ ] **Step 7: Retarget the conformance suite**

In `tests/Scrinia.Server.Tests/Conformance/StoreConformanceTests.cs`, rewrite each call to the new shape. The three filtered cases become, respectively:

```csharp
        fx.Local.SearchAll(new SearchQuery("widget") { Limit = 100, PathPrefix = "/api/" })
        fx.Local.SearchAll(new SearchQuery("widget") { Limit = 100, ExcludeTopics = "G-1" })
        fx.Remote.SearchAll(new SearchQuery("widget") { Limit = 20, PathPrefix = "/api/" })
```

**The assertions must not change.** If an assertion has to be weakened to make this compile or pass, that is a behaviour change and this task has gone wrong — stop and report.

- [ ] **Step 8: Build and run everything**

Run: `dotnet build Scrinia.sln -c Debug --no-incremental`
Expected: 0 errors. The compiler is the migration checklist — every missed call site is a build error, which is the point of removing the defaults.

Then sequentially: `dotnet test tests/Scrinia.Tests`, `dotnet test tests/Scrinia.Server.Tests`, `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests`, `dotnet test tests/Scrinia.Plugin.Embeddings.Tests`.
Expected: all PASS, with no test deleted or weakened.

- [ ] **Step 9: Commit**

```bash
git add src tests benchmarks
git commit -m "refactor(store): one SearchAll taking SearchQuery, no default filter bodies"
```

---

## Task 5: One scoring pipeline

**Files:**
- Create: `src/Scrinia.Core/Search/SearchPipeline.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs:820-924`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs:230`
- Modify: `src/Scrinia.Server/Services/PluginPipeline.cs`
- Modify: `tests/Scrinia.Tests/AutoSurfaceEntityFactsTests.cs` (7 call sites, F11)
- Test: `tests/Scrinia.Server.Tests/Conformance/StoreConformanceTests.cs`

**Interfaces:**
- Produces: `Scrinia.Core.Search.SearchOutcome` (public record struct) and `SearchPipeline.ExecuteAsync(IMemoryStore, SearchQuery, ISearchScoreContributor?, EntityGraphScoreContributor?, string?, bool, CancellationToken)`.
- Taking the contributors as parameters **is** the scorer half of F4: the ambient lookup moves to the three call edges and out of the pipeline.

- [ ] **Step 1: Create the pipeline in Core**

Create `src/Scrinia.Core/Search/SearchPipeline.cs`, moving the body of `MemoryTools.Core.cs:849-903` and `BuildEntityFacts` at `:909-924` verbatim except for the two ambient lookups, which become parameters:

```csharp
using Scrinia.Core.Models;   // ScopedArtifact lives in Scrinia.Core.Models, a SIBLING namespace —
                             // without this the IReadOnlyList<ScopedArtifact> declarations are CS0246.

namespace Scrinia.Core.Search;

/// <summary>The result of a scoring-pipeline run: the ranked matches plus the (possibly empty)
/// entity fact summaries for entities the query named. EntityFacts is always empty unless the
/// caller passes surfaceEntityFacts: true.</summary>
public readonly record struct SearchOutcome(
    IReadOnlyList<SearchResult> Matches,
    IReadOnlyList<EntityFactSummary> EntityFacts);

/// <summary>
/// The single scoring pipeline behind the MCP <c>search</c> tool, the <c>scri search</c> CLI
/// command, and the server's <c>/search</c> endpoint. It lives in Core because that is the one
/// project all three reference — the server previously carried its own copy in
/// <c>PluginPipeline.SearchAsync</c> which had drifted to support no filters at all.
/// <para>Contributors are parameters, not ambient lookups: callers resolve them at the edge. That
/// keeps the pipeline callable from a test without installing AsyncLocal state.</para>
/// </summary>
public static class SearchPipeline
{
    public static async Task<SearchOutcome> ExecuteAsync(
        IMemoryStore store,
        SearchQuery query,
        ISearchScoreContributor? contributor,
        EntityGraphScoreContributor? entityContributor,
        string? context = null,
        bool surfaceEntityFacts = false,
        CancellationToken ct = default)
    {
        // Augment the query with caller-supplied context for scoring. The bare query is preserved
        // for any user-facing echo; the effective query feeds BM25, attribute scoring, and
        // embeddings alike so a single signal carries through all three.
        string effectiveQuery = string.IsNullOrWhiteSpace(context)
            ? query.Query
            : $"{query.Query} {context}";

        // Both contributors run over the same excludeTopics-filtered candidates so excluded topics
        // don't influence scoring. The entity-graph signal is a separate fusion source, not summed
        // into supplemental — see EntityGraphScoreContributor.
        IReadOnlyList<ScopedArtifact>? candidates = null;
        if (contributor is not null || entityContributor is not null || surfaceEntityFacts)
            candidates = store.ListScoped(query.Scopes, query.ExcludeTopics);

        IReadOnlyDictionary<string, double>? supplemental = null;
        IReadOnlyDictionary<string, double>? entityScores = null;
        if (candidates is not null)
        {
            if (contributor is not null)
                supplemental = await contributor.ComputeScoresAsync(effectiveQuery, candidates, store, ct);
            if (entityContributor is not null)
            {
                var factStore = string.IsNullOrWhiteSpace(query.Scopes) && string.IsNullOrWhiteSpace(query.ExcludeTopics)
                    ? store.GetEntityFactStore()                       // cached, no rebuild
                    : EntityFactDerivation.BuildStore(candidates);     // scoped / topic-excluded
                entityScores = entityContributor.Score(factStore, query: effectiveQuery);
            }
        }

        var effective = query with
        {
            Query = effectiveQuery,
            SupplementalScores = supplemental is { Count: > 0 } ? supplemental : null,
            EntityGraphScores = entityScores,
        };

        var filtered = store.SearchAll(effective);

        IReadOnlyList<EntityFactSummary> entityFacts = [];
        if (surfaceEntityFacts && candidates is { Count: > 0 })
            entityFacts = BuildEntityFacts(store, effectiveQuery, candidates);

        return new SearchOutcome(filtered, entityFacts);
    }

    /// <summary>Builds up to 3 entity fact summaries from the same scope+excludeTopics-filtered
    /// candidate set the scorer used — never a separate store read, never a hidden scope.</summary>
    private static IReadOnlyList<EntityFactSummary> BuildEntityFacts(
        IMemoryStore store, string effectiveQuery, IReadOnlyList<ScopedArtifact> candidates)
    {
        var factStore = EntityFactDerivation.BuildStore(candidates);

        // Map "type:value" -> EntityRef so we round-trip named keys without re-parsing colons.
        var refByKey = new Dictionary<string, EntityRef>(StringComparer.Ordinal);
        foreach (var e in factStore.Entities())
            refByKey[$"{e.Key}:{e.Value}"] = e;
        if (refByKey.Count == 0) return [];

        var named = EntityQuerySeeder.Seed(effectiveQuery, refByKey.Keys)
            .Select(k => refByKey[k]);

        return EntityFactSummarizer.Summarize(factStore, named);
    }
}
```

`Candidates` is deliberately **not** set on `effective` here — that is Task 6, so this task stays a pure move.

- [ ] **Step 2: Delete the originals and forward the MCP caller**

Delete `SearchOutcome` (`MemoryTools.Core.cs:832-837`), `ExecuteSearchAsync` (`:839-903`), and `BuildEntityFacts` (`:905-924`). At `:820`, call the pipeline with the ambient contributors resolved at the edge:

```csharp
        var outcome = await SearchPipeline.ExecuteAsync(
            store,
            new SearchQuery(query)
            {
                Scopes = scopes,
                Limit = limit,
                ExcludeTopics = excludeTopics,
                PathPrefix = pathPrefix,
                AttributeFilter = attributeFilter,
            },
            SearchContributorContext.Current,
            EntityGraphScoreContext.Current,
            context,
            surfaceEntityFacts: true,
            cancellationToken);
```

Read `:810-830` first and preserve whatever the existing call passes for `surfaceEntityFacts` and `context` — do not change the tool's behaviour.

- [ ] **Step 3: Forward the CLI caller**

`src/Scrinia/Commands/ScriniaCommands.Memory.cs:230` becomes:

```csharp
        var matches = (await SearchPipeline.ExecuteAsync(
            store,
            new SearchQuery(query)
            {
                Scopes = scopes,
                Limit = limit,
                ExcludeTopics = excludeTopics,
                PathPrefix = pathPrefix,
            },
            SearchContributorContext.Current,
            EntityGraphScoreContext.Current,
            context,
            surfaceEntityFacts: false,
            cancellationToken)).Matches;
```

This also removes the CLI's dependency on `Scrinia.Mcp` internals for search.

- [ ] **Step 4: Delete the drifted duplicate**

Replace the whole body of `PluginPipeline.SearchAsync` with a delegation:

```csharp
    /// <summary>Delegates to the shared <see cref="SearchPipeline"/>. This method used to carry its
    /// own copy of the scoring sequence and had drifted: no excludeTopics, no pathPrefix, no
    /// attributeFilter, no context augmentation. The copy is gone; only the hook-free forwarding
    /// remains.</summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        IMemoryStore store, string query, string? scopes, int limit,
        string? excludeTopics = null,
        string? pathPrefix = null,
        IReadOnlyDictionary<string, string[]>? attributeFilter = null,
        CancellationToken ct = default)
    {
        var outcome = await SearchPipeline.ExecuteAsync(
            store,
            new SearchQuery(query)
            {
                Scopes = scopes,
                Limit = limit,
                ExcludeTopics = excludeTopics,
                PathPrefix = pathPrefix,
                AttributeFilter = attributeFilter,
            },
            SearchContributorContext.Current,
            EntityGraphScoreContext.Current,
            ct: ct);
        return outcome.Matches;
    }
```

- [ ] **Step 5: Retarget the seven test call sites**

`tests/Scrinia.Tests/AutoSurfaceEntityFactsTests.cs` at `:28, :44, :57, :73, :147, :152, :212`. Read each call, then rewrite it to `SearchPipeline.ExecuteAsync` with the same values. These tests currently rely on `ScriniaMcpTools`' internals being visible; after this they use a public Core API. Keep every assertion identical. The file already has `using Scrinia.Core.Search;` at `:4`, and keeps needing `using Scrinia.Mcp;` at `:5` for the `new ScriniaMcpTools().Search(...)` calls the move does not touch.

**The two new contributor parameters are `SearchContributorContext.Current` and `EntityGraphScoreContext.Current` — not `null`.** "Same values" means what the old ambient lookup would have resolved, and `SearchContributorContext.Current` has a process-wide `Default` fallback (`ISearchScoreContributor.cs:31,34`), so passing `null` silently changes what these seven tests exercise. Passing `null` is correct only in Step 6's parity test, where both sides pass it.

- [ ] **Step 6: Add the pipeline-parity regression guard**

Append to `tests/Scrinia.Server.Tests/Conformance/StoreConformanceTests.cs`, and add `using Scrinia.Server.Services;` to that file — `PluginPipeline` is in `Scrinia.Server.Services`, which the enclosing-namespace walk from `Scrinia.Server.Tests.Conformance` does not reach (CS0246 without it). This is the guard on F10 — it fails if the server path ever again grows a copy that ignores filters:

```csharp
    /// <summary>Regression guard on the drifted duplicate: the server's PluginPipeline and the
    /// shared SearchPipeline must agree, filters included. Before unification the pipeline copy
    /// silently ignored pathPrefix.</summary>
    [Fact]
    public async Task ServerPipeline_AndSharedPipeline_AgreeUnderAFilter()
    {
        var pipeline = new PluginPipeline([]);
        var viaServer = await pipeline.SearchAsync(fx.Local, "widget", null, 100, null, "/api/", null);
        var viaShared = (await SearchPipeline.ExecuteAsync(
            fx.Local, new SearchQuery("widget") { Limit = 100, PathPrefix = "/api/" },
            null, null)).Matches;

        StoreConformanceFixture.Names(fx.Local, viaServer)
            .Should().BeEquivalentTo(StoreConformanceFixture.Names(fx.Local, viaShared));
    }
```

- [ ] **Step 7: Build and run everything**

Run: `dotnet build Scrinia.sln -c Debug --no-incremental`, then the four test projects sequentially.
Expected: 0 errors, all PASS. `SearchExcludeTopicsTests` and the ~30 rendered-YAML assertions are the pin that the MCP surface did not move.

- [ ] **Step 8: Commit**

```bash
git add src tests
git commit -m "refactor(search): one pipeline in Core, contributors passed in not ambient"
```

---

## Task 6: F5 — reuse the materialized candidates, and measure it

**Files:**
- Modify: `src/Scrinia.Core/Search/SearchPipeline.cs`
- Create: `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/SearchCostCommand.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Program.cs` (subcommand table at `:26`, help text at `:55-63`)
- Test: `tests/Scrinia.Tests/FileMemoryStoreCacheTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `tests/Scrinia.Tests/FileMemoryStoreCacheTests.cs`:

```csharp
    /// <summary>F5: when the caller has already materialized candidates, the store must use them
    /// rather than calling ListScoped a second time. Asserted behaviourally — a candidate list
    /// narrowed to one entry must produce a result set of at most that one entry, which is only
    /// possible if the store honoured it.</summary>
    [Fact]
    public void SearchAll_WithPreMaterializedCandidates_DoesNotReListScope()
    {
        Add("", "alpha-one", "alpha content one");
        Add("", "alpha-two", "alpha content two");

        var all = _store.ListScoped(null);
        var narrowed = all.Where(c => c.Entry.Name == "alpha-one").ToList();

        var results = _store.SearchAll(new SearchQuery("alpha") { Limit = 20, Candidates = narrowed });

        results.OfType<EntryResult>().Select(r => r.Item.Entry.Name)
            .Should().BeEquivalentTo(["alpha-one"]);
    }
```

`Add(string topic, string name, string content)` is that file's existing seeding helper, at `:35-43`; an empty topic yields scope `local`. `_store` is a `FileMemoryStore` over a temp dir (`:20`, `:24-26`), and the file already imports `Scrinia.Core.Models` and `Scrinia.Core.Search`, so `SearchQuery` and `EntryResult` need no new using.

The query "alpha" matches on the entry **name**, which is what makes this test work. Note that `Add` constructs its `ArtifactEntry` with `TermFrequencies: null`, so the body text it writes is *not* searchable — BM25 reads `ArtifactEntry.TermFrequencies` (`WeightedAttributeScorer.cs:142`, `:293-300`), never the artifact file. If you ever need a seeded memory whose *content* is searchable, populate `TermFrequencies` via `TextAnalysis.ComputeTermFrequencies(content)` (`src/Scrinia.Core/Search/TextAnalysis.cs:73`) before the `Upsert`.

- [ ] **Step 2: Run it**

Run: `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~SearchAll_WithPreMaterializedCandidates"`
Expected: PASS already — Task 4's `FileMemoryStore.SearchAll` honours `Candidates`. This step pins that contract before the pipeline starts depending on it. If it FAILS, Task 4 regressed and that is the bug to fix.

- [ ] **Step 3: Set `Candidates` in the pipeline**

In `SearchPipeline.ExecuteAsync`, extend the `query with` expression:

```csharp
        var effective = query with
        {
            Query = effectiveQuery,
            SupplementalScores = supplemental is { Count: > 0 } ? supplemental : null,
            EntityGraphScores = entityScores,
            // Built from this query's own Scopes/ExcludeTopics a few lines above, which is the
            // contract SearchQuery.Candidates documents. Null when no contributor ran, in which
            // case the store materializes them itself.
            Candidates = candidates,
        };
```

**This is not purely duplicate-work removal, and the commit message should not claim it is.** The candidate list is captured *before* the `await` on `ComputeScoresAsync`, which for an embedding provider can run for seconds. Today the store re-lists afterwards, so a memory written during that window appears in the results; after this change it does not. The trade is deliberate — search was never transactional with concurrent writes, and the contributor already scored against the earlier snapshot, so reusing it makes the two halves *consistent* rather than half-fresh. Record it in the `SearchQuery.Candidates` doc (Task 4 already carries the wording) and mention it in the CHANGELOG only if it proves observable.

- [ ] **Step 4: Run the full suites**

Run: `dotnet build Scrinia.sln -c Debug --no-incremental`, then the four test projects sequentially.
Expected: all PASS. A failure here means the candidate list the pipeline built is not equivalent to the one the store would have built — report it rather than papering over it.

- [ ] **Step 5: Write the measurement command**

Create `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/SearchCostCommand.cs`. The types you need, with their real member names:

- `new LoCoMoLoader(string dataPath, Granularity granularity = Granularity.Session)`, then `IAsyncEnumerable<BenchmarkQuestionGroup> LoadAsync(CancellationToken ct = default)` (`Loaders/LoCoMoLoader.cs:34`, `:41`).
- `BenchmarkQuestionGroup(string GroupId, IReadOnlyList<HaystackDoc> SharedHaystack, IReadOnlyList<BenchmarkQuestion> Questions)` (`Core/BenchmarkQuestion.cs:43-46`) — the documents property is **`SharedHaystack`**, not `Documents`.
- `HaystackDoc(string Id, string Text, DateTimeOffset? Timestamp, string? SessionId = null)` (`:27-31`).
- `BenchmarkQuestion(string Id, string QueryText, …)` (`:12-19`) — the query property is **`QueryText`**.

The benchmarks project already references `Scrinia.Core`, so `FileMemoryStore` and `SearchPipeline` need no new project reference. Implement:

`scrinia-bench searchcost --data datasets/locomo/data/locomo10.json [--conversations 3] [--queries 200] [--warmup 20]`

which must:
1. Create a temp workspace and a `FileMemoryStore` over it.
2. Ingest documents from the first `--conversations` LoCoMo groups as memories.
3. Take `--queries` questions from those same groups.
4. Run `--warmup` queries in each mode and discard them.
5. Time each query twice — once with `Candidates` set from a prior `ListScoped(scopes, excludeTopics)`, once with `Candidates = null` — alternating which runs first per query so ordering cannot bias the result.
6. Print corpus size, query count, and **median and p95 milliseconds for each mode plus the delta**.
7. Delete the temp workspace.

Register it in `Program.cs` in the subcommand switch expression at `:21-30` (alongside `"locomo" =>` at `:26`). Help text needs **two** insertions, not one: a usage line after `:57` (the usage block is `:52-57`) and a description line after `:69` (the description block is `:59-69`). Match the surrounding style in each.

- [ ] **Step 6: Run the measurement and record the numbers**

Run: `dotnet run --project benchmarks/Scrinia.Retrieval.Benchmarks -c Release -- searchcost --data datasets/locomo/data/locomo10.json --conversations 3 --queries 200`

Paste the full output into the task report. **Report it whatever it says.** The spec commits to recording the actual numbers "including if the delta is noise" — the change stands on removing a duplicated call at a boundary that could not express the alternative, and it will not be described as a speedup it is not.

- [ ] **Step 7: Commit**

```bash
git add src/Scrinia.Core/Search/SearchPipeline.cs benchmarks tests/Scrinia.Tests/FileMemoryStoreCacheTests.cs
git commit -m "perf(search): reuse the pipeline's materialized candidates in the store"
```

---

## Task 7: Branch close-out

**Why a task and not a checklist:** on the previous branch every Important finding in the final review was close-out paperwork that the ledger had no slot for. This makes close-out reviewable work with its own gate.

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `docs/superpowers/specs/2026-08-06-design-review-remediation-design.md`
- Scrinia memory: `/findings/search-contract-deferred-followups`

- [ ] **Step 1: Write the CHANGELOG entry**

Under `## [Unreleased]` (`CHANGELOG.md:11`), following the file's `### <Type> — <short title>` subsection convention (e.g. `### Fixed — response envelope can no longer be impersonated by memory content` at `:37`). Claim only what shipped, and name the user-visible change plainly: **remote-mode search now applies `excludeTopics`, `pathPrefix` and attribute filters server-side, so a filtered search against a remote store returns up to `limit` matching results instead of the filtered remnant of an unfiltered top-K.** Also note the new `POST /api/v1/stores/{store}/search` endpoint and the two additive `GET /search` parameters, both backward-compatible.

Before writing any blast-radius claim, verify it — the last two branches both shipped an overstated CHANGELOG that a reviewer had to cut back.

- [ ] **Step 2: Correct the source spec in the recorded style**

Two claims in `docs/superpowers/specs/2026-08-06-design-review-remediation-design.md` are wrong and must be corrected the way Established Fact 4 was — recorded, not silently rewritten:

1. Spec `:339-340` says "`SearchExcludeTopicsTests` and `HybridScorerTests` are rewritten to the new signature — mechanical, and they are the behavioural pin while the overloads collapse." Per F13, `SearchExcludeTopicsTests` never calls `SearchAll`; it drives `ScriniaMcpTools.MemorySearch` and needed no change.
2. Spec `:301-302` proposes narrowing `LoadIndex` to `IReadOnlyList<ArtifactEntry>`. Per this plan's scope exclusions, that moves the copy rather than removing it and introduces an aliasing hazard. Record why it was not done.

Also correct **this plan's own F6** in the spec if the spec repeats the TestServer assumption anywhere — the fatal claim was that TestServer supports synchronous sends, and it does not.

Then paste the Task 6 measurement numbers into the spec's "The measurement" section, as that section requires.

- [ ] **Step 3: Record the residuals**

Write the Scrinia memory `/findings/search-contract-deferred-followups`, carrying at least:
- The `LoadIndex` narrowing, with the evidence for rejecting it.
- Remote mode ignoring client-computed supplemental / entity-graph scores, and that the fix is a store-capability question belonging to Branch A3.
- `HttpMemoryStore`'s fabricated `OriginalBytes` / `CreatedAt` on search results.
- Anything the implementers deferred during Tasks 1-6, gathered from the ledger.

`.scrinia/` changes are part of the commit — confirm with `git status` that the memory file is tracked, not ignored.

- [ ] **Step 4: Final gate**

Run: `dotnet build Scrinia.sln -c Debug --no-incremental`
Then sequentially: `dotnet test tests/Scrinia.Tests`, `dotnet test tests/Scrinia.Server.Tests`, `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests`, `dotnet test tests/Scrinia.Plugin.Embeddings.Tests`.
Record the pass counts per project in the report.

- [ ] **Step 5: Commit**

```bash
git add CHANGELOG.md docs/superpowers/specs/2026-08-06-design-review-remediation-design.md .scrinia
git commit -m "docs(search-contract): changelog, spec corrections, and recorded residuals"
```

---

## Verification scope

| Task | Projects to run |
|---|---|
| 1, 2 | `Scrinia.Server.Tests` |
| 3 | `Scrinia.Server.Tests`, `Scrinia.Tests` |
| 4, 5, 6, 7 | all four in-scope projects |

`Scrinia.Merge.Tests` is unaffected (spec fact 18) and is not run per-task; the final gate may include it for completeness.

## Risks

**Task 4 is the real one.** It is one large commit that cannot be partially landed — that is the intended consequence of removing the default implementations, since nothing may silently inherit a wrong filter. Mitigations, in order: every behaviour change lands before it (Tasks 2-3), the conformance suite is green going in, the compiler enumerates every call site, and Task 4 Step 7 forbids weakening an assertion to make the migration compile.

**Task 1's fixture is a single point of failure, and it already failed once on paper.** `HttpMemoryStore` sends synchronously and TestServer's in-memory handler refuses synchronous sends outright, so the fixture depends on `UseKestrel(0)` + `StartServer()` binding a real loopback port. Step 5 checks this explicitly before anything is built on top. Secondary risk: this is the only suite in the repo holding a live socket-bound host, so watch for interference with the in-memory suites when the whole project runs.

**Task 5 Step 5 can silently change what seven tests exercise.** Passing `null` for the two new contributor parameters is not the same as the ambient lookup it replaces — `SearchContributorContext.Current` falls back to a process-wide `Default`. The step says so explicitly; a reviewer should check the seven rewritten calls pass the contexts, not nulls.

**Task 6 Step 3 changes read-consistency, not just cost.** Reusing the pre-`await` candidate snapshot means a memory written during a slow contributor call no longer appears in that search. Neither the new test nor the conformance suite can detect it. The trade is deliberate and documented on `SearchQuery.Candidates`; the risk is that it gets described as a pure optimization.

**Task 5 moves code across an assembly boundary.** `SearchOutcome` goes from `internal` on `ScriniaMcpTools` to `public` in Core, which is a widening — no consumer breaks, but the type is now part of Core's surface. That is intended: it is the return type of a public pipeline.
