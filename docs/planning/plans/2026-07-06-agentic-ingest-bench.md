# Agentic Ingestion Benchmark Arm — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a benchmark arm where an LLM ingests each conversation session through the real Scrinia MCP tools (guide + `memory_remember`/`memory_search`), then score retrieval with the existing R@k metric via source-doc provenance.

**Architecture:** A new `AgenticScriniaRetriever : IRetriever` buffers haystack docs grouped by session during `IngestAsync`, then on first `RetrieveAsync` runs one LLM *episode* per session via `AgenticIngestHarness`. Each episode drives a chat + tool-calling loop against an OpenAI-compatible endpoint; tool calls execute the production `ScriniaMcpTools` code in-process against a per-question `FileMemoryStore`. Agent-written memories carry `ref: <doc-id>` lines that the production `FieldExtractor` indexes into `Fields["ref"]`; at retrieve time each returned memory expands back into doc-unit hits so the scorer and metric unit are unchanged.

**Tech Stack:** C# / .NET 10, `Scrinia.Core` (`FileMemoryStore`, `MemoryStoreContext`, `FieldExtractor`), `Scrinia.Mcp` (`ScriniaMcpTools`, `EmbeddedPrompts`), existing bench infra (`IRetriever`, `BenchmarkRunner`, `LlmResponseCache`, `OpenAiCompatibleBenchLlmClient`), xUnit + FluentAssertions.

## Global Constraints

- **Metric unit stays *doc*.** Provenance expansion must yield `RetrievalHit.Id` values that are haystack doc IDs; `BenchmarkScorer` is never modified.
- **Never cache a failed episode.** Timeouts, transport errors, empty completions, and malformed tool calls must not be written to the episode cache. (Direct lesson from the typed-keywords null-cache incident.)
- **Log every swallowed failure at the point it is swallowed**, with its cause — no silent nulls.
- **No ground-truth leakage.** The episode LLM sees only session transcripts + the guide. It never receives questions, gold answers, or ground-truth IDs.
- **New project reference:** `Scrinia.Retrieval.Benchmarks` gains a reference to `Scrinia.Mcp` (currently references only `Scrinia.Core`).
- **Commit trailer** on every commit:
  ```
  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  ```
- Build/test the bench project directly (`dotnet build benchmarks/Scrinia.Retrieval.Benchmarks`, `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests`) — the solution-level `dotnet test` fails on an unrelated `Scrinia.AppHost` NuGet audit error.
- Bench tests live in `tests/Scrinia.Retrieval.Benchmarks.Tests` — a **flat** layout (no subfolders), namespace `Scrinia.Retrieval.Benchmarks.Tests`, xUnit + FluentAssertions. It already references the bench assembly, so it transitively gains `Scrinia.Mcp` once Task 0 adds that reference to the bench project. Reuse existing sibling tests for patterns: `OpenAiCompatibleBenchLlmClientTests.cs` (stubbed `HttpMessageHandler`), `LoCoMoLoaderTests.cs` / `LongMemEvalLoaderTests.cs` (loader fixtures), `LlmAgentHarnessTests.cs`, `ScriniaRetrieverTests.cs`.

---

## File Structure

- `benchmarks/Scrinia.Retrieval.Benchmarks/Scrinia.Retrieval.Benchmarks.csproj` — add `Scrinia.Mcp` project reference.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkQuestion.cs` — add `SessionId` to `HaystackDoc`.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Loaders/LoCoMoLoader.cs` — populate `SessionId` in dialog granularity.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Loaders/LongMemEvalLoader.cs` — populate `SessionId` in turn granularity.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Llm/Providers/LlmJsonContext.cs` — tool-calling wire types.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Llm/Providers/OpenAiCompatibleBenchLlmClient.cs` — `ChatWithToolsAsync`.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Llm/Providers/ToolCallTypes.cs` (new) — provider-agnostic tool-call DTOs used by the harness.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/EpisodeCache.cs` (new) — persistent tool-call-sequence cache.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/AgenticIngestHarness.cs` (new) — one-episode driver + dispatch.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/AgenticIngestPrompts.cs` (new) — system-prompt addendum + transcript formatting + tool schemas.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Core/AgenticScriniaRetriever.cs` (new) — buffering, episode orchestration, provenance expansion, stats.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/RetrieverFactory.cs` — `--agentic-ingest` branch + `--episode-max-turns`.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/LoCoMoCommand.cs`, `LongMemEvalCommand.cs`, `Program.cs` — flag help + wiring.
- `benchmarks/Scrinia.Retrieval.Benchmarks/README.md` — document the arm.
- Tests: one file per new component under the bench test project.

---

## Task 0: Project wiring — reference Scrinia.Mcp from the bench project

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Scrinia.Retrieval.Benchmarks.csproj`

**Interfaces:**
- Produces: bench project (and the test project transitively) can `using Scrinia.Mcp;` and construct `ScriniaMcpTools`.

**Confirmed:** `tests/Scrinia.Retrieval.Benchmarks.Tests` already references the bench project, so it needs no edit — it inherits `Scrinia.Mcp` transitively once the bench project references it.

- [ ] **Step 1: Confirm the current bench project references**

Run: `Get-Content benchmarks/Scrinia.Retrieval.Benchmarks/Scrinia.Retrieval.Benchmarks.csproj`
Expected: a `<ProjectReference>` to `Scrinia.Core` exists; none to `Scrinia.Mcp`.

- [ ] **Step 2: Add the Scrinia.Mcp project reference**

In the `<ItemGroup>` that holds the `Scrinia.Core` reference, add:

```xml
<ProjectReference Include="..\..\src\Scrinia.Mcp\Scrinia.Mcp.csproj" />
```

- [ ] **Step 3: Build both projects to confirm the reference resolves**

Run: `dotnet build benchmarks/Scrinia.Retrieval.Benchmarks -c Debug`
Then: `dotnet build tests/Scrinia.Retrieval.Benchmarks.Tests -c Debug`
Expected: both succeed (no code changes yet, only the reference).

- [ ] **Step 4: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Scrinia.Retrieval.Benchmarks.csproj
git commit -m "build(bench): reference Scrinia.Mcp for agentic-ingest arm

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 1: Add SessionId to HaystackDoc and populate it in loaders

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkQuestion.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Loaders/LoCoMoLoader.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Loaders/LongMemEvalLoader.cs`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/HaystackSessionGroupingTests.cs` (new)

**Interfaces:**
- Produces: `HaystackDoc.SessionId` (`string?`) — null when the doc already IS a session; set to the parent session ID when the doc is a sub-session slice. `AgenticScriniaRetriever` (Task 5) groups episodes by `SessionId ?? Id`.

**Design note:** `HaystackDoc` is a positional record with three params. Add `SessionId` as a **trailing optional param** (`string? SessionId = null`) so every existing construction site (mechanical retriever, other loaders, tests) keeps compiling unchanged.

- [ ] **Step 1: Write the failing test**

Create `tests/Scrinia.Retrieval.Benchmarks.Tests/HaystackSessionGroupingTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Core;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class HaystackSessionGroupingTests
{
    [Fact]
    public void HaystackDoc_SessionId_DefaultsToNull()
    {
        var doc = new HaystackDoc("d1", "text", Timestamp: null);
        doc.SessionId.Should().BeNull("session granularity docs are their own episode");
    }

    [Fact]
    public void HaystackDoc_SessionId_CanBeSet()
    {
        var doc = new HaystackDoc("conv5_sess3_msg2", "text", Timestamp: null, SessionId: "conv5_sess3");
        doc.SessionId.Should().Be("conv5_sess3");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter HaystackSessionGroupingTests`
Expected: FAIL — `SessionId` not defined on `HaystackDoc`.

- [ ] **Step 3: Add the SessionId param**

In `Core/BenchmarkQuestion.cs`, change the `HaystackDoc` record to:

```csharp
public sealed record HaystackDoc(
    string Id,
    string Text,
    DateTimeOffset? Timestamp,
    string? SessionId = null);
```

Update the doc-comment above it to note: `SessionId` groups sub-session slices into one agentic-ingest episode; null means the doc already is a whole session.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter HaystackSessionGroupingTests`
Expected: PASS.

- [ ] **Step 5: Populate SessionId in LoCoMoLoader dialog granularity**

Open `Loaders/LoCoMoLoader.cs`. In dialog granularity each doc is one dialog turn whose ID derives from a `dia_id` with a session prefix. Find where dialog-granularity `HaystackDoc`s are constructed. Set `SessionId` to the session prefix of the dialog's `dia_id` (the same prefix session granularity groups on — reuse the existing prefix-derivation helper the loader already uses for session IDs; do NOT invent new string parsing). In session granularity, leave `SessionId` null (default).

Add an inline comment: `// dialog docs carry their parent session so one agentic-ingest episode sees the whole session.`

- [ ] **Step 6: Populate SessionId in LongMemEvalLoader turn granularity**

Open `Loaders/LongMemEvalLoader.cs`. In turn granularity (`Granularity.Turn`), each doc is one turn expanded from a labelled `session_id`. Set `SessionId` to that owning `session_id`. In session granularity, leave it null.

Add an inline comment mirroring the LoCoMo one.

- [ ] **Step 7: Write loader assertions**

Append to `HaystackSessionGroupingTests.cs` two tests that load a tiny fixture (reuse the fixture pattern in the existing `tests/Scrinia.Retrieval.Benchmarks.Tests/LoCoMoLoaderTests.cs` and `LongMemEvalLoaderTests.cs` — copy their fixture-loading approach). Assert: in dialog/turn granularity every doc has a non-null `SessionId`; in session granularity every doc has a null `SessionId`. If no loader-test fixture exists, assert the narrower invariant against a hand-built loader input instead — do not fabricate a dataset file.

- [ ] **Step 8: Run tests and build**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter HaystackSessionGroupingTests`
Expected: PASS.
Run: `dotnet build benchmarks/Scrinia.Retrieval.Benchmarks -c Debug`
Expected: build succeeds (all existing `HaystackDoc` construction sites still compile).

- [ ] **Step 9: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkQuestion.cs benchmarks/Scrinia.Retrieval.Benchmarks/Loaders/LoCoMoLoader.cs benchmarks/Scrinia.Retrieval.Benchmarks/Loaders/LongMemEvalLoader.cs tests/Scrinia.Retrieval.Benchmarks.Tests/HaystackSessionGroupingTests.cs
git commit -m "feat(bench): HaystackDoc.SessionId for agentic-ingest episode grouping

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Tool-calling wire types and ChatWithToolsAsync

**Files:**
- Create: `benchmarks/Scrinia.Retrieval.Benchmarks/Llm/Providers/ToolCallTypes.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Llm/Providers/LlmJsonContext.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Llm/Providers/OpenAiCompatibleBenchLlmClient.cs`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/ChatWithToolsWireTests.cs` (new)

**Interfaces:**
- Consumes: existing `LlmOptions` (`Model`, `Temperature`, `BaseUrl`, `ApiKey`, `Provider`), `BuildUri`, `ApplyAuth` (private helpers already in the client).
- Produces (public, provider-agnostic — declared in `ToolCallTypes.cs`):

  ```csharp
  namespace Scrinia.Retrieval.Benchmarks.Llm.Providers;

  /// One tool the model may call. JsonSchema is a raw JSON schema string for the args object.
  public sealed record ToolSpec(string Name, string Description, string JsonSchemaParameters);

  /// A message in the tool-calling conversation. Role is "system"|"user"|"assistant"|"tool".
  /// For assistant turns that requested tools, ToolCalls is non-empty and Content may be "".
  /// For tool-result turns, Role="tool", ToolCallId echoes the call, Content is the tool output.
  public sealed record ToolChatMessage(
      string Role,
      string? Content,
      IReadOnlyList<ToolCallRequest>? ToolCalls = null,
      string? ToolCallId = null);

  /// A single tool call the model emitted. ArgumentsJson is the raw JSON args string.
  public sealed record ToolCallRequest(string Id, string ToolName, string ArgumentsJson);

  /// Result of one assistant turn. StopReason is the raw finish_reason.
  public sealed record ToolTurnResult(
      string? AssistantContent,
      IReadOnlyList<ToolCallRequest> ToolCalls,
      string? StopReason);
  ```

- Produces on `OpenAiCompatibleBenchLlmClient`:

  ```csharp
  public Task<ToolTurnResult?> ChatWithToolsAsync(
      IReadOnlyList<ToolChatMessage> messages,
      IReadOnlyList<ToolSpec> tools,
      int maxTokens,
      CancellationToken ct);
  ```

  Returns `null` ONLY on transport failure/timeout/empty body (same skip-and-continue contract as `CompleteAsync`), logging the cause to stderr first. A successful turn with no tool calls returns a non-null `ToolTurnResult` with an empty `ToolCalls` list.

**Wire format (OpenAI tools API):**
- Request adds `"tools": [{"type":"function","function":{"name","description","parameters":<schema>}}]` and `"tool_choice":"auto"`.
- Assistant response: `choices[0].message.tool_calls[]`, each `{id, type:"function", function:{name, arguments:<json string>}}`.
- Tool result messages: `{role:"tool", tool_call_id, content}`.

Because `arguments` and `parameters` are raw JSON with model-defined shapes, represent them as `JsonElement` on the wire records (NOT strongly-typed) and convert to/from string at the boundary. This keeps the source-generated `LlmJsonContext` closed over known types.

- [ ] **Step 1: Write the failing wire-serialization test**

Create `tests/Scrinia.Retrieval.Benchmarks.Tests/ChatWithToolsWireTests.cs`. Test the client against a stubbed `HttpMessageHandler` (copy the stub pattern from the existing `OpenAiCompatibleBenchLlmClient` unit tests — find them via `Grep` for `OpenAiCompatibleBenchLlmClient` in `tests/`). Two cases:

```csharp
using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class ChatWithToolsWireTests
{
    [Fact]
    public async Task ParsesToolCalls_FromAssistantMessage()
    {
        // Stub returns a chat response whose choices[0].message.tool_calls has one
        // function call: name="memory_remember", arguments={"path":"/x","content":["a"]}.
        // Assert: result.ToolCalls has one entry with ToolName "memory_remember"
        // and ArgumentsJson containing "\"path\":\"/x\"".
    }

    [Fact]
    public async Task ReturnsEmptyToolCalls_WhenAssistantJustText()
    {
        // Stub returns a normal content message, no tool_calls.
        // Assert: result is non-null, ToolCalls is empty, AssistantContent is the text.
    }

    [Fact]
    public async Task ReturnsNull_OnTransportFailure()
    {
        // Stub throws HttpRequestException.
        // Assert: result is null (skip-and-continue).
    }
}
```

Fill in the stub bodies using the existing test's handler-stub helper.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter ChatWithToolsWireTests`
Expected: FAIL — `ToolSpec`/`ChatWithToolsAsync` not defined.

- [ ] **Step 3: Add the provider-agnostic DTOs**

Create `ToolCallTypes.cs` with the four records from the Interfaces block above.

- [ ] **Step 4: Add wire types to LlmJsonContext**

In `LlmJsonContext.cs`, add internal wire records and register them on the context. Use `JsonElement` for the free-form schema/args:

```csharp
internal sealed record ToolFunctionSpec(
    string Name, string Description,
    [property: JsonPropertyName("parameters")] JsonElement Parameters);

internal sealed record ToolDefinition(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] ToolFunctionSpec Function);

internal sealed record ToolCallFunction(string Name, string Arguments);

internal sealed record ToolCall(
    string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] ToolCallFunction Function);

// Extend the existing message/response shapes with tool fields. Because ChatMessage
// is used by CompleteAsync too, add a SEPARATE request/response message record for the
// tools path rather than widening ChatMessage:
internal sealed record ToolWireMessage(
    string Role,
    string? Content,
    [property: JsonPropertyName("tool_calls")] ToolCall[]? ToolCalls = null,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null);

internal sealed record ToolChatRequest(
    string Model,
    ToolWireMessage[] Messages,
    ToolDefinition[] Tools,
    [property: JsonPropertyName("tool_choice")] string ToolChoice,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens,
    double? Temperature,
    bool Stream = false);

internal sealed record ToolChatResponseChoice(int Index, ToolWireMessage Message, string? FinishReason);
internal sealed record ToolChatResponse(string? Id, string? Model, ToolChatResponseChoice[]? Choices);
```

Add matching `[JsonSerializable(typeof(...))]` attributes for `ToolChatRequest` and `ToolChatResponse` on the `LlmJsonContext` partial class.

- [ ] **Step 5: Implement ChatWithToolsAsync**

In `OpenAiCompatibleBenchLlmClient.cs`, add the method. Map `ToolChatMessage` → `ToolWireMessage` (parse `ArgumentsJson`/`Content` as needed), map `ToolSpec.JsonSchemaParameters` → `JsonDocument.Parse(...).RootElement` for `Parameters`. POST to `chat/completions` reusing `BuildUri`/`ApplyAuth`. On the response, map `tool_calls[]` → `ToolCallRequest(id, function.name, function.arguments)`. Mirror `SendChatAsync`'s error handling exactly:

```csharp
public async Task<ToolTurnResult?> ChatWithToolsAsync(
    IReadOnlyList<ToolChatMessage> messages, IReadOnlyList<ToolSpec> tools,
    int maxTokens, CancellationToken ct)
{
    try
    {
        var wireMessages = messages.Select(ToWire).ToArray();
        var wireTools = tools.Select(t => new ToolDefinition(
            "function",
            new ToolFunctionSpec(t.Name, t.Description,
                JsonDocument.Parse(t.JsonSchemaParameters).RootElement))).ToArray();
        var request = new ToolChatRequest(
            _options.Model, wireMessages, wireTools, "auto", maxTokens, _options.Temperature);

        using var req = new HttpRequestMessage(HttpMethod.Post, BuildUri("chat/completions"))
        {
            Content = JsonContent.Create(request, LlmJsonContext.Default.ToolChatRequest),
        };
        ApplyAuth(req);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string errBody;
            try { errBody = await resp.Content.ReadAsStringAsync(ct); } catch { errBody = "<unreadable>"; }
            if (errBody.Length > 500) errBody = errBody[..500] + "...";
            Console.Error.WriteLine(
                $"[scrinia-bench:llm] {_options.Provider}/{_options.Model} tools POST {req.RequestUri} -> " +
                $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {errBody}");
            return null;
        }

        var body = await resp.Content.ReadFromJsonAsync(LlmJsonContext.Default.ToolChatResponse, ct);
        var choice = body?.Choices is { Length: > 0 } ? body.Choices[0] : null;
        if (choice is null)
        {
            Console.Error.WriteLine($"[scrinia-bench:llm] {_options.Provider}/{_options.Model} tools: empty choices");
            return null;
        }
        var calls = (choice.Message.ToolCalls ?? [])
            .Where(c => c.Function is not null)
            .Select(c => new ToolCallRequest(c.Id, c.Function.Name, c.Function.Arguments))
            .ToList();
        return new ToolTurnResult(choice.Message.Content, calls, choice.FinishReason);
    }
    catch (HttpRequestException ex) { Console.Error.WriteLine($"[scrinia-bench:llm] tools transport: {ex.Message}"); return null; }
    catch (TaskCanceledException ex) { Console.Error.WriteLine($"[scrinia-bench:llm] tools timeout/cancel: {ex.Message}"); return null; }
    catch (System.Text.Json.JsonException ex) { Console.Error.WriteLine($"[scrinia-bench:llm] tools json: {ex.Message}"); return null; }
}

private static ToolWireMessage ToWire(ToolChatMessage m) =>
    new(m.Role, m.Content,
        m.ToolCalls?.Select(tc => new ToolCall(
            tc.Id, "function", new ToolCallFunction(tc.ToolName, tc.ArgumentsJson))).ToArray(),
        m.ToolCallId);
```

Note the deviation from the summary's remembered client shape: **transport failures now log before returning null** (Global Constraint). If the existing `SendChatAsync` still swallows silently, leave it — this method is the one under new contract.

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter ChatWithToolsWireTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Llm/Providers/ToolCallTypes.cs benchmarks/Scrinia.Retrieval.Benchmarks/Llm/Providers/LlmJsonContext.cs benchmarks/Scrinia.Retrieval.Benchmarks/Llm/Providers/OpenAiCompatibleBenchLlmClient.cs tests/Scrinia.Retrieval.Benchmarks.Tests/ChatWithToolsWireTests.cs
git commit -m "feat(bench): OpenAI tool-calling support for agentic ingest

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Episode prompts and tool schemas

**Files:**
- Create: `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/AgenticIngestPrompts.cs`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticIngestPromptsTests.cs` (new)

**Interfaces:**
- Consumes: `Scrinia.Mcp.EmbeddedPrompts.LoadGuide()` (returns the guide markdown, or null), `HaystackDoc`, `ToolSpec` (Task 2).
- Produces:

  ```csharp
  namespace Scrinia.Retrieval.Benchmarks.Agent;

  public static class AgenticIngestPrompts
  {
      // Bump when the system prompt or tool schemas change (episode-cache salt, Task 4/5).
      public const string PromptVersion = "v1";

      // Guide text + bench addendum (store-what-matters framing + mandatory ref: lines).
      public static string SystemPrompt();

      // Transcript with each turn prefixed by its doc-id label, timestamps when present.
      public static string SessionUserMessage(IReadOnlyList<HaystackDoc> sessionDocs);

      // The two tools exposed to the episode: memory_remember, memory_search.
      public static IReadOnlyList<ToolSpec> Tools();
  }
  ```

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticIngestPromptsTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Agent;
using Scrinia.Retrieval.Benchmarks.Core;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class AgenticIngestPromptsTests
{
    [Fact]
    public void SystemPrompt_IncludesGuideAndRefInstruction()
    {
        string sys = AgenticIngestPrompts.SystemPrompt();
        sys.Should().Contain("ref:", "memories must cite source doc ids for provenance scoring");
        sys.Length.Should().BeGreaterThan(200, "the real guide text is folded in");
    }

    [Fact]
    public void SessionUserMessage_LabelsEachTurnWithDocId()
    {
        var docs = new List<HaystackDoc>
        {
            new("conv5_sess3_msg1", "Maria: We booked Lisbon.", Timestamp: null, SessionId: "conv5_sess3"),
            new("conv5_sess3_msg2", "Sam: Nice, when?", Timestamp: null, SessionId: "conv5_sess3"),
        };
        string msg = AgenticIngestPrompts.SessionUserMessage(docs);
        msg.Should().Contain("[conv5_sess3_msg1]").And.Contain("[conv5_sess3_msg2]");
        msg.Should().Contain("We booked Lisbon");
    }

    [Fact]
    public void Tools_ExposeRememberAndSearch_WithValidJsonSchemas()
    {
        var tools = AgenticIngestPrompts.Tools();
        tools.Select(t => t.Name).Should().Contain(new[] { "memory_remember", "memory_search" });
        foreach (var t in tools)
        {
            var act = () => System.Text.Json.JsonDocument.Parse(t.JsonSchemaParameters);
            act.Should().NotThrow($"{t.Name} parameters must be valid JSON schema");
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter AgenticIngestPromptsTests`
Expected: FAIL — type not defined.

- [ ] **Step 3: Implement AgenticIngestPrompts**

```csharp
using System.Text;
using Scrinia.Mcp;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;

namespace Scrinia.Retrieval.Benchmarks.Agent;

public static class AgenticIngestPrompts
{
    public const string PromptVersion = "v1";

    private const string Addendum = """

        ---
        BENCHMARK INGESTION TASK

        The conversation below just happened in a session. Store what is worth
        remembering for future questions about it, using the memory tools. Make as
        many memory_remember calls as the material warrants; you may memory_search to
        check what you already stored earlier in this conversation.

        REQUIRED: every memory you store MUST include, inside its content, one line per
        source turn it draws from, in the form:
            ref: <doc-id>
        Use the exact bracketed doc-id labels shown on the transcript turns. These
        lines are how the benchmark maps your memories back to their sources — a memory
        with no ref: line cannot be scored.

        When you are done storing, stop calling tools.
        """;

    public static string SystemPrompt()
    {
        string guide = EmbeddedPrompts.LoadGuide()
            ?? throw new InvalidOperationException("Built-in guide not found in embedded resources");
        return guide + Addendum;
    }

    public static string SessionUserMessage(IReadOnlyList<HaystackDoc> sessionDocs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Conversation transcript (each turn is labeled with its doc-id):");
        sb.AppendLine();
        foreach (var d in sessionDocs)
        {
            if (d.Timestamp is { } ts)
                sb.Append('(').Append(ts.ToString("yyyy-MM-dd")).Append(") ");
            sb.Append('[').Append(d.Id).Append("] ");
            sb.AppendLine(d.Text);
        }
        return sb.ToString();
    }

    public static IReadOnlyList<ToolSpec> Tools() =>
    [
        new ToolSpec("memory_remember",
            "Persist a memory at a path. content is an array — each element becomes one " +
            "independently retrievable chunk. Include ref: <doc-id> lines naming the source turns.",
            RememberSchema),
        new ToolSpec("memory_search",
            "Find earlier memories you stored in this session by keyword or phrase.",
            SearchSchema),
    ];

    // Mirrors the real memory_remember params (path, content[], description?, tags?, keywords?, anchors?).
    // codeRefs/review* omitted — not meaningful for bench conversations.
    private const string RememberSchema = """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Memory path, e.g. /findings/lisbon-trip." },
            "content": { "type": "array", "items": { "type": "string" },
                         "description": "Each element becomes one chunk. Include ref: <doc-id> lines." },
            "description": { "type": "string" },
            "tags": { "type": "array", "items": { "type": "string" } },
            "keywords": { "type": "array", "items": { "type": "string" },
                          "description": "Prefer typed: person:<name>, place:<name>, org:<name>, thing:<name>, topic:<theme>." },
            "anchors": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["path", "content"]
        }
        """;

    private const string SearchSchema = """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string" },
            "limit": { "type": "integer" }
          },
          "required": ["query"]
        }
        """;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter AgenticIngestPromptsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Agent/AgenticIngestPrompts.cs tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticIngestPromptsTests.cs
git commit -m "feat(bench): episode system prompt + tool schemas for agentic ingest

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: AgenticIngestHarness — episode driver and tool dispatch

**Files:**
- Create: `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/AgenticIngestHarness.cs`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticIngestHarnessTests.cs` (new)

**Interfaces:**
- Consumes: `OpenAiCompatibleBenchLlmClient.ChatWithToolsAsync` (Task 2) via a narrow interface (below), `AgenticIngestPrompts` (Task 3), `Scrinia.Mcp.ScriniaMcpTools`, `Scrinia.Core.MemoryStoreContext`, `Scrinia.Core.IMemoryStore`.
- Produces:

  ```csharp
  namespace Scrinia.Retrieval.Benchmarks.Agent;

  // Narrow seam over the LLM client so tests inject a scripted tool-caller with no network.
  public interface IToolCallingClient
  {
      Task<ToolTurnResult?> ChatWithToolsAsync(
          IReadOnlyList<ToolChatMessage> messages, IReadOnlyList<ToolSpec> tools,
          int maxTokens, CancellationToken ct);
  }

  public sealed record EpisodeToolCall(string ToolName, string ArgumentsJson);

  // Outcome of running one session episode.
  public sealed record EpisodeResult(
      IReadOnlyList<EpisodeToolCall> ToolCalls,  // ordered remember/search calls actually executed
      bool Succeeded,                            // false → caller falls back to mechanical ingest
      int LlmTurns);

  public sealed class AgenticIngestHarness
  {
      public AgenticIngestHarness(IToolCallingClient client, int maxTurns, int maxTokens = 1024);

      // Runs one episode against the store currently in MemoryStoreContext.Current.
      // Dispatches memory_remember/memory_search tool calls to a fresh ScriniaMcpTools.
      // Returns the executed tool-call sequence (for the cache) + success flag.
      public Task<EpisodeResult> RunEpisodeAsync(IReadOnlyList<HaystackDoc> sessionDocs, CancellationToken ct);

      // Replays a cached tool-call sequence against the current store WITHOUT the LLM.
      // Used by the retriever on cache hit.
      public static Task ReplayAsync(IReadOnlyList<EpisodeToolCall> calls, CancellationToken ct);
  }
  ```

  `OpenAiCompatibleBenchLlmClient` gets a one-line `: IToolCallingClient` (its `ChatWithToolsAsync` already matches the signature).

**Dispatch rules:**
- The harness constructs `new ScriniaMcpTools()` once per episode. The caller sets `MemoryStoreContext.Current` to the target store before invoking; the harness does not own store lifecycle.
- `memory_remember` args → parse JSON → call `tools.MemoryRemember(path, content, description, tags, keywords, anchors, ...)`. Its returned YAML becomes the `role:"tool"` content.
- `memory_search` args → `tools.MemorySearch(query, ...)` → returned YAML back to the model.
- **Malformed args** (JSON parse fails, or `required` field missing): do NOT throw. Return a production-style error string as the tool result (e.g. `"error: memory_remember requires 'path' and 'content'"`) so the model can self-correct. Count it but keep the episode alive.
- **Loop**: build message list (system, user), then repeat: `ChatWithToolsAsync` → if `null`, episode fails (return `Succeeded=false`); if no tool calls, episode ends successfully; else append the assistant turn + each tool result, increment turn counter, and stop with success when `maxTurns` is hit.
- A `memory_remember` call that returns a non-error result counts toward the executed sequence recorded for the cache; searches are recorded too (replay must reproduce identical store state, and a search has no write effect so it is a no-op on replay — but keep it in the sequence for fidelity/inspection).

- [ ] **Step 1: Write the failing tests with a scripted client**

Create `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticIngestHarnessTests.cs`. Define a `ScriptedToolClient : IToolCallingClient` that returns a queued list of `ToolTurnResult`s in order (last one with empty `ToolCalls` to end the loop). Use a real `FileMemoryStore` in a temp dir set on `MemoryStoreContext.Current`.

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Retrieval.Benchmarks.Agent;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class AgenticIngestHarnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"scrinia_agentic_{Guid.NewGuid():N}");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class ScriptedToolClient : IToolCallingClient
    {
        private readonly Queue<ToolTurnResult?> _turns;
        public ScriptedToolClient(params ToolTurnResult?[] turns) => _turns = new(turns);
        public Task<ToolTurnResult?> ChatWithToolsAsync(
            IReadOnlyList<ToolChatMessage> m, IReadOnlyList<ToolSpec> t, int mt, CancellationToken ct)
            => Task.FromResult(_turns.Count > 0 ? _turns.Dequeue() : new ToolTurnResult(null, [], "stop"));
    }

    [Fact]
    public async Task Episode_ExecutesRememberCall_WritesToStore()
    {
        Directory.CreateDirectory(_dir);
        var store = new FileMemoryStore(_dir);
        MemoryStoreContext.Current = store;
        try
        {
            var remember = new ToolCallRequest("c1", "memory_remember",
                """{"path":"/findings/lisbon","content":["Booked Lisbon.\nref: d1"]}""");
            var client = new ScriptedToolClient(
                new ToolTurnResult(null, [remember], "tool_calls"),
                new ToolTurnResult("done", [], "stop"));
            var harness = new AgenticIngestHarness(client, maxTurns: 8);
            var docs = new List<HaystackDoc> { new("d1", "Maria: Booked Lisbon.", null, "s1") };

            var result = await harness.RunEpisodeAsync(docs, CancellationToken.None);

            result.Succeeded.Should().BeTrue();
            result.ToolCalls.Should().ContainSingle(c => c.ToolName == "memory_remember");
            store.SearchAll("Lisbon", null, 10).Should().NotBeEmpty();
        }
        finally { MemoryStoreContext.Current = null; }
    }

    [Fact]
    public async Task Episode_ReturnsFailure_WhenClientReturnsNull()
    {
        Directory.CreateDirectory(_dir);
        MemoryStoreContext.Current = new FileMemoryStore(_dir);
        try
        {
            var client = new ScriptedToolClient((ToolTurnResult?)null);
            var harness = new AgenticIngestHarness(client, maxTurns: 8);
            var docs = new List<HaystackDoc> { new("d1", "x", null, "s1") };
            var result = await harness.RunEpisodeAsync(docs, CancellationToken.None);
            result.Succeeded.Should().BeFalse("a null turn is a transport failure → fallback");
        }
        finally { MemoryStoreContext.Current = null; }
    }

    [Fact]
    public async Task Episode_MalformedArgs_ReturnsErrorToModel_AndSurvives()
    {
        Directory.CreateDirectory(_dir);
        MemoryStoreContext.Current = new FileMemoryStore(_dir);
        try
        {
            var bad = new ToolCallRequest("c1", "memory_remember", "{ not json");
            var client = new ScriptedToolClient(
                new ToolTurnResult(null, [bad], "tool_calls"),
                new ToolTurnResult("done", [], "stop"));
            var harness = new AgenticIngestHarness(client, maxTurns: 8);
            var docs = new List<HaystackDoc> { new("d1", "x", null, "s1") };
            var result = await harness.RunEpisodeAsync(docs, CancellationToken.None);
            result.Succeeded.Should().BeTrue("malformed args don't kill the episode");
        }
        finally { MemoryStoreContext.Current = null; }
    }

    [Fact]
    public async Task Replay_ReproducesStoreState_WithoutClient()
    {
        Directory.CreateDirectory(_dir);
        MemoryStoreContext.Current = new FileMemoryStore(_dir);
        try
        {
            var calls = new List<EpisodeToolCall>
            {
                new("memory_remember", """{"path":"/f/x","content":["hello world\nref: d1"]}"""),
            };
            await AgenticIngestHarness.ReplayAsync(calls, CancellationToken.None);
            ((FileMemoryStore)MemoryStoreContext.Current).SearchAll("hello", null, 10)
                .Should().NotBeEmpty();
        }
        finally { MemoryStoreContext.Current = null; }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter AgenticIngestHarnessTests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement AgenticIngestHarness**

Key implementation points:
- `RunEpisodeAsync`: build `List<ToolChatMessage>` starting with system + user; loop up to `maxTurns`.
- Dispatch helper `ExecuteCallAsync(ScriniaMcpTools tools, ToolCallRequest call, CancellationToken ct)` returns `(string toolResult, bool isError)`. Parse args with `JsonDocument.Parse` in a try/catch; missing required fields → error string.
- Extract args: for `memory_remember`, read `path` (string), `content` (string[]), optional `description`/`tags`/`keywords`/`anchors`. Use `JsonElement` accessors; treat absent optionals as null.
- Append assistant message (echoing the tool_calls) and one `role:"tool"` message per call (with `ToolCallId = call.Id`).
- Record executed calls into an `EpisodeToolCall` list (name + original `ArgumentsJson`).
- `ReplayAsync`: construct `new ScriniaMcpTools()`, for each `EpisodeToolCall` re-dispatch via the same `ExecuteCallAsync` path (searches are harmless no-ops for store state). Errors during replay are logged and skipped.
- **Cache/fallback discipline (Global Constraints):** the harness itself does not touch the cache — it returns the sequence and a success flag; the retriever (Task 5) owns caching and only caches when `Succeeded` is true.
- Log a one-line summary at episode end: turns used, remember calls, search calls, malformed calls.

Make `OpenAiCompatibleBenchLlmClient` implement `IToolCallingClient` (add the interface to its declaration — the method already exists from Task 2).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter AgenticIngestHarnessTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Agent/AgenticIngestHarness.cs benchmarks/Scrinia.Retrieval.Benchmarks/Llm/Providers/OpenAiCompatibleBenchLlmClient.cs tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticIngestHarnessTests.cs
git commit -m "feat(bench): AgenticIngestHarness episode driver + tool dispatch

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: EpisodeCache and AgenticScriniaRetriever

**Files:**
- Create: `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/EpisodeCache.cs`
- Create: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/AgenticScriniaRetriever.cs`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/EpisodeCacheTests.cs`, `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticScriniaRetrieverTests.cs` (new)

**Interfaces:**
- Consumes: `AgenticIngestHarness` + `EpisodeResult`/`EpisodeToolCall` (Task 4), `FileMemoryStore`, `MemoryStoreContext`, `RankerOptions`, `EntryResult`/`ChunkEntryResult`, `ArtifactEntry.Fields`, `AgenticIngestPrompts.PromptVersion`.
- Produces:

  ```csharp
  // EpisodeCache: persistent ndjson of episode tool-call sequences.
  public sealed class EpisodeCache
  {
      public EpisodeCache(string filePath);
      // key = hash(model + promptVersion + full session transcript). Bump promptVersion to invalidate.
      public bool TryGet(string key, out IReadOnlyList<EpisodeToolCall> calls);
      public void Put(string key, IReadOnlyList<EpisodeToolCall> calls); // NEVER call for failed episodes
      public static string Key(string model, string promptVersion, string transcript);
      public int Hits { get; } public int Misses { get; }
  }

  public sealed class AgenticScriniaRetriever : IRetriever
  {
      public AgenticScriniaRetriever(
          string rootDir, AgenticIngestHarness harness, EpisodeCache cache,
          string modelSlug, RankerOptions? rankerOptions = null);

      public AgenticIngestStats Stats { get; }   // for the run summary (Task 6)
  }

  public sealed record AgenticIngestStats(
      int Episodes, int AgenticEpisodes, int FallbackEpisodes,
      int MemoriesWritten, int MemoriesWithoutRefs, int UnresolvableRefs, int ToolCallErrors);
  ```

**Retriever behavior:**
- `IngestAsync(doc, ...)` (all overloads): buffer `doc` under `_ingestLock`. Ignore anchors/keywords (this arm's ingestion IS the episode). Group buffered docs into ordered sessions keyed by `doc.SessionId ?? doc.Id`, preserving first-seen order within and across sessions.
- Lazy episode run: on first `RetrieveAsync`, if not already ingested, set `MemoryStoreContext.Current = _store`, then for each session in order:
  - Build transcript via `AgenticIngestPrompts.SessionUserMessage`; compute cache key `EpisodeCache.Key(_modelSlug, AgenticIngestPrompts.PromptVersion, transcript)`.
  - Cache hit → `AgenticIngestHarness.ReplayAsync(cachedCalls)`.
  - Cache miss → `harness.RunEpisodeAsync(sessionDocs)`. If `Succeeded`, `cache.Put(key, result.ToolCalls)` and continue; if not, retry once; on second failure, **mechanical fallback**: upsert each session doc verbatim (mirror `ScriniaRetriever`'s minimal `ArtifactEntry` construction — name, `bench://` uri, `ContentPreview=text`, `TermFrequencies` via `TextAnalysis.AnalyzeText`) and increment `FallbackEpisodes`. Never cache a failed/fallback episode.
  - Reset `MemoryStoreContext.Current = null` in a `finally`.
- Build the provenance map after episodes: iterate every entry in `_store`, read `entry.Fields` with an explicit `OrdinalIgnoreCase` lookup for `"ref"` (JSON round-trip drops the dictionary comparer — see the field-extraction design doc), collect its doc IDs. Track `MemoriesWithoutRefs` (empty/absent ref set) and `UnresolvableRefs` (ref IDs not in the buffered doc-ID set). Store `entryName → orderedDocIds`.
- `RetrieveAsync(query, topK)`: `_store.SearchAll(query, null, Math.Max(topK, searchLimit), null)`; for each `EntryResult`/`ChunkEntryResult` in score order, expand to its cited doc IDs (in cited order) via the provenance map, each inheriting the result's score; dedup doc IDs across memories (first occurrence wins); take first `topK`; return as `RetrievalHit(docId, docText, score)` where `docText` is the buffered doc's text (look up from the buffer). Use a wider `searchLimit` (e.g. `topK * 10`) because expansion+dedup collapses memories.
- `ResetAsync`: discard the per-question store (same dir lifecycle as `ScriniaRetriever` — delete previous `q{n}` dir), clear buffers and the ingested flag, rebuild the store.
- `DisposeAsync`: delete `rootDir`.
- Update `Stats` throughout.

- [ ] **Step 1: Write the failing EpisodeCache test**

Create `tests/Scrinia.Retrieval.Benchmarks.Tests/EpisodeCacheTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Agent;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class EpisodeCacheTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"epcache_{Guid.NewGuid():N}.ndjson");
    public void Dispose() { try { File.Delete(_file); } catch { } }

    [Fact]
    public void RoundTrips_ToolCallSequence()
    {
        var cache = new EpisodeCache(_file);
        string key = EpisodeCache.Key("qwen3", "v1", "transcript text");
        cache.TryGet(key, out _).Should().BeFalse();

        var calls = new List<EpisodeToolCall> { new("memory_remember", """{"path":"/f/x","content":["a\nref: d1"]}""") };
        cache.Put(key, calls);

        var reload = new EpisodeCache(_file);
        reload.TryGet(key, out var got).Should().BeTrue();
        got.Should().HaveCount(1);
        got[0].ToolName.Should().Be("memory_remember");
    }

    [Fact]
    public void Key_IsStable_ForSameInputs_AndVariesByPromptVersion()
    {
        EpisodeCache.Key("m", "v1", "t").Should().Be(EpisodeCache.Key("m", "v1", "t"));
        EpisodeCache.Key("m", "v1", "t").Should().NotBe(EpisodeCache.Key("m", "v2", "t"));
    }
}
```

- [ ] **Step 2: Run to verify fail, then implement EpisodeCache**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter EpisodeCacheTests`
Expected: FAIL.

Implement `EpisodeCache` modeled on `LlmResponseCache` (append-only ndjson, `FileShare.ReadWrite` append, `Load()` on construct, malformed-line skip). Each line: `{"key": "<key>", "calls": [{"tool":"...","args":"<json string>"}, ...]}`. `Key` = `LlmResponseCache.Hash(model + "" + promptVersion + "" + transcript)` (reuse the existing SHA256 helper). Serialize `EpisodeToolCall` as `{tool, args}`.

Run again: Expected PASS.

- [ ] **Step 3: Write the failing retriever tests (scripted harness)**

Create `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticScriniaRetrieverTests.cs`. Reuse the `ScriptedToolClient` idea (make it accept a map from a marker in the user message to a scripted turn list, OR simpler: script a fixed sequence and use a single-session haystack). Cover:

1. **Provenance expansion**: one episode stores a memory citing `ref: d1` and `ref: d2`; a query that matches it returns hits `d1`, `d2` (both present, in cited order), scored.
2. **Dedup across memories**: two memories both cite `d1`; `d1` appears once in results.
3. **Cache replay round-trip**: run once (records calls), reset, run again on cache hit → identical retrieval result; assert the client was NOT called the second time (use a call-count spy).
4. **Fallback**: scripted double-null → session ingested mechanically; a query matching the raw doc text still returns the doc; `Stats.FallbackEpisodes == 1`.
5. **No-ref memory counted**: a memory with no `ref:` line increments `Stats.MemoriesWithoutRefs` and maps to no doc.

Each test constructs `AgenticScriniaRetriever` with a temp `rootDir`, a `AgenticIngestHarness` wrapping the scripted client, and a fresh `EpisodeCache` in a temp file. Drives `IngestAsync` for each doc, then `RetrieveAsync`.

- [ ] **Step 4: Run to verify fail, then implement AgenticScriniaRetriever**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter AgenticScriniaRetrieverTests`
Expected: FAIL.

Implement per the behavior spec above. Reuse `ScriniaRetriever`'s helpers where identical in spirit (`SanitizeName`, `bench://` uri, `q{n}` dir lifecycle) — copy the small private statics rather than making them public. For reading `Fields["ref"]`, iterate the store's entries; if the store lacks a public enumerate-all, add one **only if needed** — first check whether `SearchAll` with a broad query or an existing enumeration method suffices (inspect `FileMemoryStore` for a list/enumerate method; `memory_list` implies one exists). Prefer the existing enumeration path.

Run again: Expected PASS.

- [ ] **Step 5: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Agent/EpisodeCache.cs benchmarks/Scrinia.Retrieval.Benchmarks/Core/AgenticScriniaRetriever.cs tests/Scrinia.Retrieval.Benchmarks.Tests/EpisodeCacheTests.cs tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticScriniaRetrieverTests.cs
git commit -m "feat(bench): AgenticScriniaRetriever + episode cache with provenance R@k

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: CLI wiring, run summary, and docs

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/RetrieverFactory.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/LoCoMoCommand.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/LongMemEvalCommand.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Program.cs` (help text)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/README.md`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticIngestCliTests.cs` (new)

**Interfaces:**
- Consumes: `AgentClientBuilder.BuildAsync` (existing — returns `.Llm`, `.ProviderId`, `.ModelSlug`), `AgenticScriniaRetriever` (Task 5).
- Produces: `--agentic-ingest` flag; `--episode-max-turns <n>` (default 16); `RetrieverFactory.BuildAsync` returns `(IRetriever, Mode)` instead of `(ScriniaRetriever, Mode)`.

**Design notes:**
- `BuildAsync`'s return type changes to `(IRetriever Retriever, Mode Mode)`. When `--agentic-ingest` is set: require `--agent-provider`; build the LLM client via `AgentClientBuilder.BuildAsync`; wrap in `AgenticIngestHarness`; construct `EpisodeCache` at `cache/episodes-{providerId}-{modelSlug}.ndjson`; construct `AgenticScriniaRetriever`; **return early** (no embedder, no reranker — this arm is BM25 + fields, compared against the BM25 mechanical arm). Fail fast if combined with `--agent <ops>` (query/ingest agent decorations) or `--rerank` other than none — print a clear message and exit.
- The commands already treat `retriever` as `IRetriever`/`IAsyncDisposable`, so widening the type is compatible. The reranker-wiring block stays inside the non-agentic branch.
- After the run, if the retriever is an `AgenticScriniaRetriever`, print its `Stats` block (episodes agentic vs fallback, memories, memories-without-refs, unresolvable-refs, tool-call errors) to stdout and include it in the JSON output metadata if convenient (optional — stdout is the requirement).

- [ ] **Step 1: Write the failing CLI parse test**

Create `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticIngestCliTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Cli;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class AgenticIngestCliTests
{
    [Fact]
    public void AgenticIngest_RequiresAgentProvider()
    {
        string[] args = ["locomo", "--data", "x.json", "--agentic-ingest"];
        var act = async () => await RetrieverFactory.BuildAsync(args, "locomo", default);
        act.Should().ThrowAsync<ArgumentException>().WithMessage("*agent-provider*");
    }

    [Fact]
    public void EpisodeMaxTurns_ParsesDefault16()
    {
        RetrieverFactory.EpisodeMaxTurns(["--x", "y"]).Should().Be(16);
        RetrieverFactory.EpisodeMaxTurns(["--episode-max-turns", "8"]).Should().Be(8);
    }
}
```

(Extract a small internal static `EpisodeMaxTurns(string[])` in `RetrieverFactory` so it is unit-testable, mirroring `ParseAgentMode`.)

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter AgenticIngestCliTests`
Expected: FAIL.

- [ ] **Step 3: Change BuildAsync return type and add the agentic branch**

In `RetrieverFactory.cs`: change the signature to `Task<(IRetriever Retriever, Mode Mode)>`. Near the top of `BuildAsync`, after parsing basics, add:

```csharp
if (Program.Flag(args, "--agentic-ingest") is { } ai && ParseBool(ai) || Program.HasFlag(args, "--agentic-ingest"))
{
    // Mutually exclusive with query/ingest agent decorations and rerankers — this arm's
    // ingestion IS the agent episode, and it scores BM25+fields vs the mechanical BM25 arm.
    if (ParseAgentMode(Program.Flag(args, "--agent")).AnyEnabled)
        throw new ArgumentException("--agentic-ingest cannot be combined with --agent <ops>; the episode is the agent.");

    string provider = Program.Flag(args, "--agent-provider")
        ?? throw new ArgumentException("--agentic-ingest requires --agent-provider (openai | anthropic | ...).");
    string? model = Program.Flag(args, "--agent-model");
    string? baseUrl = Program.Flag(args, "--agent-base-url");
    string? apiKey = Program.Flag(args, "--agent-api-key");
    var backend = await AgentClientBuilder.BuildAsync(provider, model, baseUrl, apiKey, ct);

    // The tool-calling client is the OpenAI-compatible client; other providers are v1-unsupported.
    if (backend.Llm is not IToolCallingClient toolClient)
        throw new ArgumentException($"--agentic-ingest requires an OpenAI-compatible provider; '{backend.ProviderId}' does not support tool-calling in this bench.");

    int maxTurns = EpisodeMaxTurns(args);
    var harness = new AgenticIngestHarness(toolClient, maxTurns);
    string epCache = Path.Combine("benchmarks", "Scrinia.Retrieval.Benchmarks", "cache",
        $"episodes-{backend.ProviderId}-{backend.ModelSlug}.ndjson");
    string agRoot = Path.Combine(Path.GetTempPath(), $"scrinia-retbench-{benchmarkSlug}-ag-{Guid.NewGuid().ToString("N")[..8]}");
    var agentic = new AgenticScriniaRetriever(agRoot, harness, new EpisodeCache(epCache), backend.ModelSlug,
        RankerOptions.Default with { AlphaRelevance = 1.0, AlphaRecency = 0.0 });
    Console.Error.WriteLine($"[scrinia-bench] agentic-ingest enabled: provider={backend.ProviderId} model={backend.ModelSlug} maxTurns={maxTurns} cache={epCache}");
    return (agentic, Mode.Bm25);
}
```

Add `internal static int EpisodeMaxTurns(string[] args) => ParseInt(Program.Flag(args, "--episode-max-turns"), 16);`. If `Program.HasFlag` doesn't exist, use the `Program.Flag(...) is not null` idiom the codebase already uses for boolean-presence flags — check how `--entity-graph` presence is tested and match it.

- [ ] **Step 4: Update ModeLabel and commands**

In `ModeLabel`, add: `if (Program.Flag(args, "--agentic-ingest") is not null) tags.Add("agentic-ingest");` so result files don't collide with mechanical arms.

In `LoCoMoCommand.cs` and `LongMemEvalCommand.cs`: the `retriever` variable is now `IRetriever` — confirm it still compiles (it's passed to the runner and `await using`d, both `IRetriever`-compatible). After `PrintSummary(result)`, add:

```csharp
if (retriever is AgenticScriniaRetriever ag)
    PrintAgenticStats(ag.Stats);
```

Add a small `PrintAgenticStats(AgenticIngestStats s)` helper to each command (or a shared static in `RetrieverFactory`) printing the counts.

- [ ] **Step 5: Update help text and README**

In `Program.cs` usage block, add under both `locomo` and `longmemeval`:
```
--agentic-ingest        ingest each session via an LLM through the real Scrinia MCP tools
--episode-max-turns <n> max tool-call turns per session episode (default 16)
```
(Requires `--agent-provider`/`--agent-model`/`--agent-base-url`.)

In `README.md`, add a subsection "Agentic ingestion arm" describing what it measures (ingestion quality via provenance-mapped R@k), the flag, the episode cache, and the BM25-mechanical baseline it should be compared against.

- [ ] **Step 6: Run tests and build**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter AgenticIngestCliTests`
Expected: PASS.
Run: `dotnet build benchmarks/Scrinia.Retrieval.Benchmarks -c Release`
Expected: build succeeds.

- [ ] **Step 7: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Cli benchmarks/Scrinia.Retrieval.Benchmarks/Program.cs benchmarks/Scrinia.Retrieval.Benchmarks/README.md tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticIngestCliTests.cs
git commit -m "feat(bench): --agentic-ingest CLI wiring, stats summary, docs

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: End-to-end smoke test and full-suite green

**Files:**
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticIngestEndToEndTests.cs` (new)

**Interfaces:**
- Consumes: everything above, with the scripted tool client (still no network).

- [ ] **Step 1: Write an end-to-end scripted-episode test**

Drive the whole path: build an `AgenticScriniaRetriever` (scripted harness), ingest a two-session haystack (4 docs, `SessionId` set), run two questions through `BenchmarkRunner.RunGroupedAsync` with `BenchmarkScorer.ScoreFractionalById`, and assert a non-zero R@k when the scripted episode stored a memory citing the gold doc. This proves the runner → retriever → provenance → scorer wiring end-to-end.

```csharp
// Sketch — fill in scripted turns so the stored memory cites the gold doc id.
var result = await new BenchmarkRunner().RunGroupedAsync(
    "locomo-test", "agentic-ingest-test",
    ToAsync(new[] { group }), retriever, topK: 10,
    BenchmarkScorer.ScoreFractionalById, ct: CancellationToken.None);
result.Aggregate.RecallAt10.Should().BeGreaterThan(0);
```

- [ ] **Step 2: Run the end-to-end test**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter AgenticIngestEndToEndTests`
Expected: PASS.

- [ ] **Step 3: Full bench-test suite green**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests`
Expected: all green (new bench tests + existing). If an unrelated pre-existing failure appears (e.g. AppHost audit), confirm it is not in `Bench/` and note it.

- [ ] **Step 4: Commit**

```bash
git add tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticIngestEndToEndTests.cs
git commit -m "test(bench): end-to-end agentic-ingest through runner + scorer

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Manual verification (real endpoint — not automated)

After the suite is green, run against the live server to confirm the arm produces sane numbers and the cache/stats behave:

```powershell
./scripts/run-benchmarks.ps1 -Benchmark locomo -ExtraArgs "--agentic-ingest --agent-provider openai --agent-base-url http://100.73.66.47:8080/v1 --agent-model qwen3 --granularity dialog --episode-max-turns 12 --category single-hop"
```

Confirm: episodes run (stderr shows the harness banner), `cache/episodes-openai-qwen3.ndjson` fills with tool-call sequences (NOT nulls), the stats block prints at the end, and a rerun is cache-fast. Compare R@5/R@10 against the BM25 mechanical arm (`--mode bm25`, no `--agentic-ingest`).

---

## Self-Review Notes

- **Spec coverage:** provenance-mapped R@k (Task 5), in-bench tool loop with real tools (Tasks 2/4), mechanical query side (Task 5 `RetrieveAsync`), per-session episodes (Task 5 grouping + Task 3 prompt), episode cache with never-cache-failure + log-swallowed-failure rules (Tasks 2/4/5), fallback-to-mechanical (Task 5), run summary stats (Task 6), CLI flags (Task 6), tests (all tasks). All spec sections map to a task.
- **No network in unit tests:** every test uses a scripted `IToolCallingClient` or a stubbed `HttpMessageHandler`.
- **Metric unit invariant:** `BenchmarkScorer` is never edited; expansion yields doc-ID hits.
