# Internal Consistency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove three pieces of duplicated knowledge the 2026-08-05 review found — hand-rolled workspace-root derivation, a verbatim-copied provider retry block, and a bare truncation constant.

**Architecture:** Three independent tasks with no shared code and no ordering dependency. C4 routes seven path derivations through `IMemoryStore.WorkspaceRoot`. C5 extracts the providers' retry/circuit-breaker block into a helper that **returns** a result rather than a base class that yields, because the call sites are `yield return` iterators. C6 names a constant.

**Tech Stack:** C# / .NET 10, xunit, FluentAssertions, no mocking frameworks. Projects: `src/Scrinia.Mcp`, `src/Scrinia`, `src/Scrinia.Server`, `src/Scrinia.Core`.

## Global Constraints

- Branch: `internal-consistency`, cut from `main` at `f662453`.
- **No behaviour change** is intended by any task. C5 has one unavoidable exception, called out in that task.
- `dotnet build Scrinia.sln -c Debug` clean before any test run.
- **Never** run two `dotnet test` invocations against one project concurrently.
- **Never** run `dotnet build` while a test run is in flight.
- Clear stray `testhost.exe` before a full run; apparent hangs are orphaned processes holding `FileLock`s on `%TEMP%` stores.
- Test projects in scope: `Scrinia.Tests`, `Scrinia.Server.Tests`. `Scrinia.Merge.Tests` is unaffected.
- The golden suite at `tests/Scrinia.Tests/Golden/` (15 tests) pins every MCP tool's exact YAML. C4 touches files those tools run through. **If a golden fails, do NOT set `SCRINIA_UPDATE_GOLDEN` and do NOT edit a snapshot** — a failure means a real output change.
- An untracked `cmds.txt` in the repo root is not part of this work; never `git add` it.

---

## Established Facts

Every fact below carries the command that produced it. **Re-run rather than re-trust** — the source spec's line numbers were all stale by the time this plan was written, because the `mcp-response-shape` branch moved three of the files.

**F1. The seven C4 sites exist, and every line number in the 2026-08-05 spec is stale.**
`rg -n "GetDirectoryName" --type cs src/Scrinia.Mcp/ src/Scrinia/Commands/`

| Site | Spec said | Actually |
|---|---|---|
| `MemoryTools.Core.cs` (drift list) | 354-356 | **355-356** |
| `MemoryTools.Core.cs` (full list) | 729-731 | **730-731** |
| `MemoryTools.Core.cs` (`BuildDriftList`) | 1631-1633 | **1428-1429** |
| `MemoryTools.Lifecycle.cs` | 67 | **75** |
| `MemoryTools.Lifecycle.cs` | 580-581 | **622** |
| `MemoryTools.Bundling.cs` | 82-83 | **83** |
| `ScriniaCommands.Bundle.cs` | 143 | **155** |

**F2. The seven sites are NOT homogeneous — they derive three different things.** This is the plan's most important correction to the spec, which says "all seven move to `store.WorkspaceRoot`". Verified by reading each site:

- **Workspace root, two hops** (`GetDirectoryName` twice): `Core.cs:355-356`, `Core.cs:730-731`, `Core.cs:1428-1429`
- **`.scrinia/`, one hop**: `Lifecycle.cs:75` (comment says `// .scrinia/`), `Lifecycle.cs:622`
- **Workspace root via `Combine(..., "..")`**: `Bundling.cs:83`
- **`.scrinia/exports`**: `ScriniaCommands.Bundle.cs:155` — `Path.Combine(GetStoreDirForScope("local"), "..", "exports")`

Only four of the seven resolve to the workspace root. The other three need `Path.Combine(store.WorkspaceRoot, ".scrinia")` or `Path.Combine(store.WorkspaceRoot, ".scrinia", "exports")`.

**F3. `PathRouter.ToStoreDir("local", root)` returns `{root}/.scrinia/memories`.**
`src/Scrinia.Core/PathRouter.cs:154-158` — so workspace root is two `GetDirectoryName` hops from the local store dir, and `.scrinia/` is one. This is why F2's shapes differ.

**F4. The change is behaviour-preserving in remote mode.** All seven sites derive from `store.GetStoreDirForScope("local")`, which throws `NotSupportedException` in `HttpMemoryStore` (`src/Scrinia/HttpMemoryStore.cs:458-459`). `WorkspaceRoot` throws there too — the throw is at `src/Scrinia/HttpMemoryStore.cs:461-463`; `src/Scrinia.Core/IMemoryStore.cs:224` is only the interface declaration. Switching one throwing call for another changes nothing.
Verified by `sed`-ing each site's surrounding lines; every one reads `store.GetStoreDirForScope("local")` first.

**F5. `Core.cs:730-731` is inside a conditional** (`if (anyCodeRefs)`, `Core.cs:726-732`) so the derivation is lazy. Keep it lazy — hoisting it would make remote mode throw on a `list` call that today succeeds when no entry has `FileHashes`.

**F6. `ScriniaCommands.Bundle.cs` is already normalized.** The spec calls it "unnormalized"; `:156` is `exportsDir = Path.GetFullPath(exportsDir);`. That criticism no longer applies.

**F7. C5's retry block cannot become a base class.** `AnthropicChatProvider.StreamChatAsync` is `async IAsyncEnumerable<ChatEvent>` (`:35`) with `yield return`/`yield break` at `:82-83` and `:95-96`. C# forbids `yield return` inside a `try` with a `catch`, which is why the code captures `circuitError` and yields after the block. A base class cannot yield on the caller's behalf. The spec's "return a value" shape is correct.
`rg -n "yield return|yield break|IAsyncEnumerable" src/Scrinia.Server/Chat/Providers/AnthropicChatProvider.cs`

**F8. `ResilientLlmProvider` exists, but in a different hierarchy, and is not reusable here.**
`rg -n "ResilientLlmProvider" benchmarks/ src/`
It lives at `benchmarks/Scrinia.Retrieval.Benchmarks/Llm/Providers/ResilientLlmProvider.cs` as `abstract class ResilientLlmProvider : IBenchLlmClient, IDisposable`, inherited by `AnthropicBenchLlmClient` and `GeminiBenchLlmClient`. `d3a9aa3 refactor!: move LLM clients to bench` moved it out of `src/`, it did not delete it.

It cannot serve C5: it implements `IBenchLlmClient` for the benchmark harness, lives in a project `Scrinia.Server` does not reference, and is a base class — which F7 rules out regardless. C5 creates a new helper in `Scrinia.Server`.

> **This fact was wrong in an earlier draft of this plan**, which asserted the type "does not exist" on the strength of an `rg` scoped to `src/` — a command that could not have found it. Recorded because it is the precise failure this plan's verification pass exists to catch: a claim asserted from a command that cannot answer it. Scope your searches to the whole repo before concluding something is absent.

**F9. The 500-char truncation has four HTTP-body sites, and a fifth that is different knowledge.**
`rg -n "500 \? body|> 500 \? errorBody|\[\.\.500\]" --type cs src`
- `AnthropicChatProvider.cs:94`, `GeminiChatProvider.cs:101`, `OpenAiChatProvider.cs:93` — `body.Length > 500 ? body[..500] + "..." : body`
- `ResilientEmbeddingProvider.cs:70` — `errorBody.Length > 500 ? errorBody[..500] : errorBody` — **note: no `"..."` suffix**
- `MemoryTools.Lifecycle.cs:405` — truncates a **conflict preview**, not an HTTP error body. Different rule, different reason to change. **Do not fold it in.**

**F10. C6 is at `AgentLoop.cs:202-203`**, not the spec's `:200-203`.
`rg -n "8000" src/Scrinia.Server/Chat/AgentLoop.cs`

**F11. Provider sizes:** `AnthropicChatProvider.cs` 242, `GeminiChatProvider.cs` 237, `OpenAiChatProvider.cs` 212 lines. `wc -l src/Scrinia.Server/Chat/Providers/*.cs`

**F12. `RetryPolicy.ExecuteAsync` signature** (`src/Scrinia.Core/Resilience/RetryPolicy.cs:24-29`):
`public static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, Func<T, bool> isTransient, RetryOptions? options = null, ILogger? logger = null, CancellationToken ct = default)`

---

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `src/Scrinia.Core/Resilience/HttpErrorBody.cs` | The one place an HTTP error body is truncated for display |
| `src/Scrinia.Server/Chat/Providers/ChatProviderHttp.cs` | Sends a chat request under circuit-breaker + retry, returning either a response or an error string |
| `tests/Scrinia.Server.Tests/ChatProviderHttpTests.cs` | Covers the helper's success and non-success paths (2 tests; the circuit-open branch is left to `CircuitBreaker`'s own tests) |
| `tests/Scrinia.Tests/HttpErrorBodyTests.cs` | Covers the shared truncation rule |

**Modified:**

| File | Change |
|---|---|
| `src/Scrinia.Mcp/MemoryTools.Core.cs` | 3 sites → `store.WorkspaceRoot` |
| `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` | 2 sites → `Path.Combine(store.WorkspaceRoot, ".scrinia")` |
| `src/Scrinia.Mcp/MemoryTools.Bundling.cs` | 1 site → `store.WorkspaceRoot` |
| `src/Scrinia/Commands/ScriniaCommands.Bundle.cs` | 1 site → `Path.Combine(..WorkspaceRoot, ".scrinia", "exports")` |
| `src/Scrinia.Server/Chat/Providers/*.cs` | 3 providers call the helper |
| `src/Scrinia.Core/Embeddings/Providers/ResilientEmbeddingProvider.cs` | Uses `HttpErrorBody.Truncate` |
| `src/Scrinia.Server/Chat/AgentLoop.cs` | Named constant |

---

### Task 1: C4 — one workspace-root authority

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs:355-356`, `:730-731`, `:1428-1429`
- Modify: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:75`, `:622`
- Modify: `src/Scrinia.Mcp/MemoryTools.Bundling.cs:83`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Bundle.cs:155`
- Test: `tests/Scrinia.Tests/WorkspaceRootUnificationTests.cs` (create)

**Interfaces:**
- Consumes: `IMemoryStore.WorkspaceRoot` (`string`, throws `NotSupportedException` when the store is remote) — already exists, unchanged by this task.
- Produces: nothing. No new public surface.

**Locate every site by content, not by line number** — F1 shows the spec's numbers were already stale once. Use `rg -n "GetDirectoryName" --type cs src/Scrinia.Mcp/ src/Scrinia/Commands/` and match against F2's table.

- [ ] **Step 1: Write the failing test**

Create `tests/Scrinia.Tests/WorkspaceRootUnificationTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// IMemoryStore.WorkspaceRoot is the single authority for "the directory containing .scrinia/".
/// Callers previously reverse-engineered it from GetStoreDirForScope with two different
/// hand-rolled algorithms that agreed only by coincidence. This pins the property against the
/// derivation it replaces, so a future layout change cannot silently desynchronise them.
/// </summary>
public class WorkspaceRootUnificationTests
{
    [Fact]
    public void WorkspaceRoot_MatchesTwoHopDerivationFromLocalStoreDir()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        string storeDir = store.GetStoreDirForScope("local");
        string expected = Path.GetDirectoryName(Path.GetDirectoryName(storeDir)!)!;

        store.WorkspaceRoot.Should().Be(expected,
            "WorkspaceRoot must equal the derivation the seven call sites used to hand-roll");
    }

    [Fact]
    public void ScriniaDir_IsOneHopFromLocalStoreDir()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        string storeDir = store.GetStoreDirForScope("local");
        string scriniaDir = Path.GetDirectoryName(storeDir)!;

        Path.Combine(store.WorkspaceRoot, ".scrinia").Should().Be(scriniaDir,
            "the two Lifecycle sites want .scrinia/, which is one hop, not the workspace root");
    }
}
```

- [ ] **Step 2: Run to verify it passes on the current tree**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-build -c Debug --filter "FullyQualifiedName~WorkspaceRootUnificationTests"`
Expected: PASS (2 tests).

These are **characterization** tests, not red-then-green: they pin the equivalence that makes the refactor safe. If either FAILS on the current tree, stop — the derivation and the property already disagree, which is a live bug and a bigger finding than C4.

- [ ] **Step 3: Convert the three workspace-root sites**

`MemoryTools.Core.cs:355-356` and `:1428-1429` share this shape:

```csharp
// before
string storeDir = store.GetStoreDirForScope("local");
string scriniaDir = Path.GetDirectoryName(storeDir) ?? storeDir;
string workspaceRoot = Path.GetDirectoryName(scriniaDir) ?? scriniaDir;

// after
string workspaceRoot = store.WorkspaceRoot;
```

Delete the now-unused `storeDir` / `scriniaDir` locals. Verified: in both methods (`Core.cs:354-430`ff and `Core.cs:1425-1480`) those two locals are used **only** to derive `workspaceRoot` and appear nowhere else, so both are safe to remove. Re-check with `rg -n "storeDir|scriniaDir" src/Scrinia.Mcp/MemoryTools.Core.cs` rather than trusting this line.

`MemoryTools.Core.cs:726-732` keeps its conditional (F5):

```csharp
string? workspaceRoot = null;
bool anyCodeRefs = page.Any(p => p.Entry.FileHashes is { Count: > 0 });
if (anyCodeRefs)
    workspaceRoot = store.WorkspaceRoot;
```

`MemoryTools.Bundling.cs:83`:

```csharp
// before
string workspaceRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(storeDir)!, ".."));
// after
string workspaceRoot = store.WorkspaceRoot;
```

- [ ] **Step 4: Convert the two `.scrinia/` sites**

These want one hop, not the workspace root. `MemoryTools.Lifecycle.cs:75`:

```csharp
// before
string storeDir = store.GetStoreDirForScope("local");
string scriniaDir = Path.GetDirectoryName(storeDir)!; // .scrinia/
// after
string scriniaDir = Path.Combine(store.WorkspaceRoot, ".scrinia");
```

`MemoryTools.Lifecycle.cs:618-622` has the same shape with `resumeStoreDir` / `resumeScriniaDir`. Convert it the same way, keeping the local names.

- [ ] **Step 5: Convert the exports site**

`ScriniaCommands.Bundle.cs:155-156`:

```csharp
// before
string exportsDir = Path.Combine(WorkspaceSetup.Store.GetStoreDirForScope("local"), "..", "exports");
exportsDir = Path.GetFullPath(exportsDir);
// after
string exportsDir = Path.Combine(WorkspaceSetup.Store.WorkspaceRoot, ".scrinia", "exports");
```

`Path.GetFullPath` is no longer needed — `WorkspaceRoot` is already absolute and `Combine` adds no `..` segments. Confirm by running the export test named in Step 7; if any test asserts on the exact string, keep `GetFullPath` rather than changing the value.

- [ ] **Step 6: Build**

Run: `dotnet build Scrinia.sln -c Debug`
Expected: 0 errors.

- [ ] **Step 7: Run the golden suite and the bundle tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-build -c Debug --filter "FullyQualifiedName~ResponseGoldenTests|FullyQualifiedName~Bundle|FullyQualifiedName~WorkspaceRootUnification"`
Expected: all pass.

The goldens matter here: `list`, `reconcile` and the drift path all run through converted code. A golden failure means the derivation changed a real output — investigate, do not re-baseline.

- [ ] **Step 8: Run both full suites**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-build -c Debug`
Then: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --no-build -c Debug`
Expected: all green.

- [ ] **Step 9: Confirm no hand-rolled derivation survives**

Run: `rg -n "GetDirectoryName" --type cs src/Scrinia.Mcp/ src/Scrinia/Commands/`
Expected: no hit that walks up from a store directory. Exactly one hit should remain under `src/Scrinia.Mcp/` — `Lifecycle.cs:375`, which takes the directory of an artifact path and is unrelated. Hits under `src/Scrinia/Commands/Hooks/` (installers creating parent directories) and `WorkspaceSetup.cs` are also unrelated and stay.

- [ ] **Step 10: Commit**

```bash
git add src/Scrinia.Mcp src/Scrinia/Commands/ScriniaCommands.Bundle.cs tests/Scrinia.Tests/WorkspaceRootUnificationTests.cs
git commit -m "refactor(core): make WorkspaceRoot the single path authority it claims to be

IMemoryStore.WorkspaceRoot's doc comment said callers 'previously
reverse-engineered this'. Seven still did, via three different shapes: two
GetDirectoryName hops, one Combine-with-'..', and one exports path. Two of the
seven wanted .scrinia/ rather than the workspace root, so they now say that
explicitly instead of arriving there by one fewer hop.

Behaviour-preserving in remote mode: every site already reached
GetStoreDirForScope, which throws there, and WorkspaceRoot throws too."
```

---

### Task 2: C5 — one provider retry block, one truncation rule

**Files:**
- Create: `src/Scrinia.Core/Resilience/HttpErrorBody.cs`
- Create: `src/Scrinia.Server/Chat/Providers/ChatProviderHttp.cs`
- Create: `tests/Scrinia.Server.Tests/ChatProviderHttpTests.cs`
- Modify: `src/Scrinia.Server/Chat/Providers/AnthropicChatProvider.cs`, `GeminiChatProvider.cs`, `OpenAiChatProvider.cs`
- Modify: `src/Scrinia.Core/Embeddings/Providers/ResilientEmbeddingProvider.cs:70`

**Interfaces:**
- Consumes: `RetryPolicy.ExecuteAsync<T>` (F12), `CircuitBreaker` (`EnsureClosed()`, `RecordFailure()`), `CircuitBreakerOpenException`, `TransientDetector.IsTransient(HttpResponseMessage)`, `RetryOptions`.
- Produces:
  - `HttpErrorBody.Truncate(string body) → string` in namespace `Scrinia.Core.Resilience`
  - `ChatProviderHttp.SendAsync(HttpClient http, Func<HttpRequestMessage> requestFactory, CircuitBreaker circuitBreaker, RetryOptions retryOptions, CancellationToken ct) → Task<(HttpResponseMessage? Response, string? Error)>` in namespace `Scrinia.Server.Chat.Providers`

**The one intended behaviour change on this branch.** Per F9, `ResilientEmbeddingProvider.cs:70` truncates **without** a `"..."` suffix while the three providers add one. Unifying them gives the embedding provider's log message a `"..."` suffix it did not have. This is a log-string change only, it makes the two consistent, and it must be stated in the commit message. If you would rather preserve it exactly, stop and ask — do not add a `bool` parameter to preserve both, which would reintroduce the flag-parameter smell the review flagged.

- [ ] **Step 1: Write the truncation helper's test**

Create it at `tests/Scrinia.Tests/HttpErrorBodyTests.cs`. No conditional: `HttpErrorBody` is a `Scrinia.Core.Resilience` type, so its test belongs beside the other Core tests, not under `tests/Scrinia.Tests/Embeddings/` where `ResilientEmbeddingProviderTests.cs` lives — that file covers one *consumer*, and this helper now has four.

```csharp
using FluentAssertions;
using Scrinia.Core.Resilience;
using Xunit;

namespace Scrinia.Tests;

public class HttpErrorBodyTests
{
    [Fact]
    public void Truncate_ShortBody_IsUnchanged()
    {
        HttpErrorBody.Truncate("boom").Should().Be("boom");
    }

    [Fact]
    public void Truncate_LongBody_IsCutToLimitWithEllipsis()
    {
        string body = new('x', 600);

        string result = HttpErrorBody.Truncate(body);

        result.Should().HaveLength(503, "500 characters plus the three-character ellipsis");
        result.Should().StartWith(new string('x', 500)).And.EndWith("...");
    }

    [Fact]
    public void Truncate_ExactlyAtLimit_IsUnchanged()
    {
        string body = new('x', 500);
        HttpErrorBody.Truncate(body).Should().Be(body, "the cut is strictly greater-than");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-build -c Debug --filter "FullyQualifiedName~HttpErrorBodyTests"`
Expected: FAIL to compile — `HttpErrorBody` does not exist.

- [ ] **Step 3: Write the truncation helper**

Create `src/Scrinia.Core/Resilience/HttpErrorBody.cs`:

```csharp
namespace Scrinia.Core.Resilience;

/// <summary>
/// The one place an HTTP error body is shortened for display. Four call sites carried this
/// rule verbatim — three chat providers and the embedding provider — and they had already
/// drifted: the embedding provider omitted the ellipsis. Bodies can be megabytes of HTML from
/// a proxy, so the cap protects logs and error envelopes alike.
/// </summary>
public static class HttpErrorBody
{
    /// <summary>Maximum characters of a provider error body worth surfacing.</summary>
    public const int MaxChars = 500;

    /// <summary>Returns <paramref name="body"/> unchanged, or its first
    /// <see cref="MaxChars"/> characters followed by an ellipsis.</summary>
    public static string Truncate(string body) =>
        body.Length > MaxChars ? body[..MaxChars] + "..." : body;
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet build Scrinia.sln -c Debug` then
`dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-build -c Debug --filter "FullyQualifiedName~HttpErrorBodyTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Write the send helper's test**

Create `tests/Scrinia.Server.Tests/ChatProviderHttpTests.cs`. Use a real `HttpClient` over a stub `HttpMessageHandler` — the repo uses no mocking framework, so write the handler by hand:

```csharp
using System.Net;
using FluentAssertions;
using Scrinia.Core.Resilience;
using Scrinia.Server.Chat.Providers;
using Xunit;

namespace Scrinia.Server.Tests;

public class ChatProviderHttpTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpClient Client(StubHandler h) =>
        new(h) { BaseAddress = new Uri("https://example.test/") };

    [Fact]
    public async Task Success_ReturnsResponseAndNoError()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = Client(handler);

        var (response, error) = await ChatProviderHttp.SendAsync(
            http, () => new HttpRequestMessage(HttpMethod.Post, "v1/chat"),
            new CircuitBreaker(), new RetryOptions(), CancellationToken.None);

        error.Should().BeNull();
        response.Should().NotBeNull();
        response!.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Dispose();
    }

    [Fact]
    public async Task NonSuccess_ReturnsTruncatedErrorAndNoResponse()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(new string('x', 600)),
        });
        using var http = Client(handler);

        var (response, error) = await ChatProviderHttp.SendAsync(
            http, () => new HttpRequestMessage(HttpMethod.Post, "v1/chat"),
            new CircuitBreaker(), new RetryOptions(), CancellationToken.None);

        response.Should().BeNull("a failed response must not leak to the caller undisposed");
        error.Should().StartWith("Provider returned 400: ").And.EndWith("...");
        error!.Length.Should().Be("Provider returned 400: ".Length + 503);
    }
}
```

Before writing this, confirm `CircuitBreaker` and `RetryOptions` are constructible with no arguments — `AnthropicChatProvider.cs:30-31` does `new CircuitBreaker()` and `new RetryOptions()`, so they are. Confirm their namespace with `rg -n "class CircuitBreaker|class RetryOptions" --type cs src` and fix the `using` if it is not `Scrinia.Core.Resilience`.

- [ ] **Step 6: Run to verify it fails**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --no-build -c Debug --filter "FullyQualifiedName~ChatProviderHttpTests"`
Expected: FAIL to compile — `ChatProviderHttp` does not exist.

- [ ] **Step 7: Write the send helper**

Create `src/Scrinia.Server/Chat/Providers/ChatProviderHttp.cs`. This is the block currently copied verbatim into all three providers, hoisted whole:

```csharp
using Scrinia.Core.Resilience;

namespace Scrinia.Server.Chat.Providers;

/// <summary>
/// Sends a chat request under the shared circuit-breaker and retry policy and reports the
/// outcome as a value.
/// <para>Deliberately NOT a base class. The three callers are
/// <c>async IAsyncEnumerable&lt;ChatEvent&gt;</c> iterators and must do their own
/// <c>yield return</c>; C# also forbids <c>yield return</c> inside a <c>try</c> with a
/// <c>catch</c>, which is why the original code captured an error into a local and yielded
/// after the block. A base class cannot yield on the caller's behalf.</para>
/// </summary>
internal static class ChatProviderHttp
{
    /// <summary>
    /// Returns <c>(response, null)</c> on success — the caller owns and must dispose the
    /// response — or <c>(null, message)</c> when the circuit is open or the provider returned
    /// a non-success status. A failed response is disposed here, never handed back.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled.</exception>
    public static async Task<(HttpResponseMessage? Response, string? Error)> SendAsync(
        HttpClient http,
        Func<HttpRequestMessage> requestFactory,
        CircuitBreaker circuitBreaker,
        RetryOptions retryOptions,
        CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            circuitBreaker.EnsureClosed();
            response = await RetryPolicy.ExecuteAsync(
                async () => await http.SendAsync(requestFactory(), HttpCompletionOption.ResponseHeadersRead, ct),
                resp => TransientDetector.IsTransient(resp),
                retryOptions,
                logger: null,
                ct);
        }
        catch (CircuitBreakerOpenException ex)
        {
            return (null, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // client disconnect / caller cancel — not a provider health signal
        }
        catch (Exception)
        {
            circuitBreaker.RecordFailure();
            throw; // Let it propagate to ChatEndpoints catch-all
        }

        if (response.IsSuccessStatusCode)
            return (response, null);

        using (response)
        {
            if (TransientDetector.IsTransient(response))
                circuitBreaker.RecordFailure();
            string body = "";
            try { body = await response.Content.ReadAsStringAsync(ct); } catch { }
            return (null, $"Provider returned {(int)response.StatusCode}: {HttpErrorBody.Truncate(body)}");
        }
    }
}
```

- [ ] **Step 8: Run to verify it passes**

Run: `dotnet build Scrinia.sln -c Debug` then
`dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --no-build -c Debug --filter "FullyQualifiedName~ChatProviderHttpTests"`
Expected: PASS (2 tests).

- [ ] **Step 9: Convert the three providers**

In each of `AnthropicChatProvider.cs`, `GeminiChatProvider.cs`, `OpenAiChatProvider.cs`, replace the whole block — the `HttpResponseMessage response; string? circuitError = null;` declarations, the `try`/`catch` group, the `if (circuitError is not null)` yield, and the `if (!response.IsSuccessStatusCode)` block — with:

```csharp
var (response, error) = await ChatProviderHttp.SendAsync(
    _http,
    () =>
    {
        var httpReq = new HttpRequestMessage(HttpMethod.Post, "v1/messages");
        httpReq.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return httpReq;
    },
    _circuitBreaker, _retryOptions, ct);

if (error is not null)
{
    yield return new ChatEvent("error", Error: error);
    yield break;
}

using (response!)
{
    using var stream = await response.Content.ReadAsStreamAsync(ct);
    // ... the existing streaming loop, unchanged
}
```

**Only the request path and the JSON context differ between the three** — `"v1/messages"` here, whatever each provider already uses elsewhere. Take each provider's existing values; do not unify the paths.

- [ ] **Step 10: Convert the embedding provider**

`src/Scrinia.Core/Embeddings/Providers/ResilientEmbeddingProvider.cs:70`:

```csharp
// before
ProviderName, (int)response.StatusCode, errorBody.Length > 500 ? errorBody[..500] : errorBody);
// after
ProviderName, (int)response.StatusCode, HttpErrorBody.Truncate(errorBody));
```

This is the intended behaviour change noted at the top of this task: the log line gains an ellipsis when truncated.

- [ ] **Step 11: Build and run both suites**

Run: `dotnet build Scrinia.sln -c Debug` (0 errors)
Then: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --no-build -c Debug`
Then: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-build -c Debug`
Expected: all green.

- [ ] **Step 12: Confirm the duplication is gone**

Run: `rg -n "500 \? body|\[\.\.500\]|EnsureClosed" --type cs src`
Expected: `EnsureClosed` appears once, in `ChatProviderHttp.cs`. No `[..500]` outside `HttpErrorBody.cs` except `MemoryTools.Lifecycle.cs:405`, which is a conflict preview and stays (F9).

Run: `wc -l src/Scrinia.Server/Chat/Providers/*.cs` — expect each provider to have lost roughly 35-45 lines against F11's 242 / 237 / 212.

- [ ] **Step 13: Commit**

```bash
git add src/Scrinia.Core/Resilience/HttpErrorBody.cs src/Scrinia.Server/Chat/Providers tests/Scrinia.Server.Tests/ChatProviderHttpTests.cs tests/Scrinia.Tests/HttpErrorBodyTests.cs src/Scrinia.Core/Embeddings/Providers/ResilientEmbeddingProvider.cs
git commit -m "refactor(chat): one send path and one truncation rule for provider errors

Three chat providers carried a byte-identical circuit-breaker + retry block:
fields, constructor defaults, EnsureClosed, RetryPolicy.ExecuteAsync, all four
catch arms and the non-success handling. Only the request path and JSON context
differed. It is now a helper that RETURNS its outcome rather than a base class,
because the callers are async iterators and C# forbids yield inside try/catch.

The 500-character truncation had four copies and had already drifted: the
embedding provider omitted the ellipsis the three chat providers add. They now
share HttpErrorBody.Truncate, so that log line gains an ellipsis when it
truncates. MemoryTools.Lifecycle's conflict-preview truncation is a different
rule and is deliberately left alone."
```

---

### Task 3: C6 — name the AgentLoop truncation limit

**Files:**
- Modify: `src/Scrinia.Server/Chat/AgentLoop.cs:202-203`

**Interfaces:**
- Consumes: nothing. Produces: nothing. Private constant.

Deliberately **not** a rebuild of the `ContentTruncated` + followUp contract: that value is LLM-facing, not part of the MCP response shape.

- [ ] **Step 1: Make the change**

Locate by content (`rg -n "8000" src/Scrinia.Server/Chat/AgentLoop.cs`), then:

```csharp
// before
if (content.Length > 8000)
    content = content[..8000] + "\n[... truncated]";

// after
if (content.Length > MaxRecallCharsPerTool)
    content = content[..MaxRecallCharsPerTool] + "\n[... truncated]";
```

Add near the class's other private constants:

```csharp
/// <summary>
/// Cap on a single tool result folded back into the agent's context. Chosen to keep one
/// oversized recall from crowding out the conversation; it bounds context, not the MCP
/// response, so it is deliberately unrelated to ResponseBuilder's own response cap.
/// </summary>
private const int MaxRecallCharsPerTool = 8000;
```

- [ ] **Step 2: Build**

Run: `dotnet build Scrinia.sln -c Debug`
Expected: 0 errors.

- [ ] **Step 3: Confirm no bare literal survives**

Run: `rg -n "8000" src/Scrinia.Server/Chat/AgentLoop.cs`
Expected: one hit — the constant's declaration.

- [ ] **Step 4: Run the server suite**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --no-build -c Debug`
Expected: all green.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Server/Chat/AgentLoop.cs
git commit -m "refactor(chat): name AgentLoop's recall truncation limit

8000 appeared twice as a bare literal with nothing saying what it bounds or
why. The comment records that it caps context, not the MCP response, so nobody
tries to reconcile it with ResponseBuilder's cap."
```

---

## Branch close

- [ ] **CHANGELOG.** All three findings are internal refactors with no user-visible effect, with one exception worth a line under `### Changed`: a truncated provider error body in the embedding provider's log now ends with `...`, matching the chat providers.

- [ ] **Record residuals** to `.scrinia` under `/findings/internal-consistency-deferred-followups` if anything is consciously shipped unfixed. If nothing is deferred, skip — do not write an empty finding.

- [ ] **Correct the source spec.** `docs/superpowers/specs/2026-08-05-review-remediation-design.md`'s C4 section says all seven sites "move to `store.WorkspaceRoot`" and calls `ScriniaCommands.Bundle.cs` unnormalized. Per F2 and F6 both are wrong. Correct them in the recorded style used at `docs/superpowers/specs/2026-08-06-design-review-remediation-design.md` — state the original claim, that it was wrong, and what is true.

- [ ] **Final gate:** `dotnet build Scrinia.sln -c Debug --no-incremental` clean, then `Scrinia.Tests` and `Scrinia.Server.Tests` green, run one at a time.

## Out of scope

- **`MemoryTools.Lifecycle.cs:405`'s truncation.** A conflict preview, not an HTTP error body — different rule, different reason to change (F9).
- **Rebuilding the `ContentTruncated` + followUp contract** for `AgentLoop`. That value is LLM-facing.
- **Anything from the `mcp-response-shape` residuals** at `/findings/mcp-response-shape-deferred-followups`, including the two dead `scri bundle` guards. Those belong to the residuals spec at the end of the sequence.
