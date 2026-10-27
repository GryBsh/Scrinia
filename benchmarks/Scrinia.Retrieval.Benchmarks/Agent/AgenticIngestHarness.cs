using System.Text.Json;
using Scrinia.Mcp;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;

namespace Scrinia.Retrieval.Benchmarks.Agent;

/// <summary>
/// Narrow seam over the LLM client so tests inject a scripted tool-caller with no
/// network. <see cref="OpenAiCompatibleBenchLlmClient"/> implements this directly —
/// its <c>ChatWithToolsAsync</c> already matches the signature.
/// </summary>
public interface IToolCallingClient
{
    Task<ToolTurnResult?> ChatWithToolsAsync(
        IReadOnlyList<ToolChatMessage> messages, IReadOnlyList<ToolSpec> tools,
        int maxTokens, CancellationToken ct);
}

/// <summary>One remember/search call actually dispatched during an episode.</summary>
public sealed record EpisodeToolCall(string ToolName, string ArgumentsJson);

/// <summary>Outcome of running one session episode.</summary>
public sealed record EpisodeResult(
    IReadOnlyList<EpisodeToolCall> ToolCalls,  // ordered remember/search calls actually executed
    bool Succeeded,                            // false → the session stores nothing (no mechanical fallback)
    int LlmTurns,
    int ToolCallErrors);                       // count of malformed/errored tool calls this episode

/// <summary>
/// Drives one agentic-ingest "episode": a chat + tool-calling loop against an
/// <see cref="IToolCallingClient"/>, dispatching the model's remember /
/// search tool calls to a real <see cref="ScriniaMcpTools"/> instance that
/// writes into whatever store is in <see cref="Scrinia.Core.MemoryStoreContext.Current"/>.
///
/// <para>The harness does not own store lifecycle (the caller sets
/// <see cref="Scrinia.Core.MemoryStoreContext.Current"/> before invoking) and does not
/// touch any cache — it returns the executed tool-call sequence plus a success flag so
/// the retriever can decide whether to cache the episode.</para>
///
/// <para><paramref name="maxTokens"/> is the <c>max_tokens</c> sent on every turn. On an
/// OpenAI-compatible server (llama.cpp, vLLM, …) that value bounds the ENTIRE request —
/// the prompt AND the generation — not just the completion. And the prompt is not fixed:
/// it starts at the system guide + full session transcript + tool schemas (already ~5k
/// tokens) and grows every turn as prior tool calls, recalled memories, and search hits
/// are appended. So the cap must be sized to the server's context window, not to a single
/// tool call's length. Set it too low and either the request has no room to generate or a
/// tool call is truncated mid-arguments (<c>finish_reason=length</c>), yielding invalid
/// JSON that reads as a bogus "malformed tool call". The CLI exposes
/// <c>--episode-max-tokens</c> to tune it. A turn that hits the cap is logged loudly.</para>
/// </summary>
public sealed class AgenticIngestHarness
{
    private readonly IToolCallingClient _client;
    private readonly int _maxTurns;
    private readonly int _maxTokens;

    public AgenticIngestHarness(IToolCallingClient client, int maxTurns, int maxTokens = 32768)
    {
        _client = client;
        _maxTurns = maxTurns;
        _maxTokens = maxTokens;
    }

    /// <summary>
    /// Runs one episode against the store currently in <see cref="Scrinia.Core.MemoryStoreContext.Current"/>.
    /// Dispatches remember/search tool calls to a fresh <see cref="ScriniaMcpTools"/>.
    /// Returns the executed tool-call sequence (for the cache) + success flag.
    /// </summary>
    public async Task<EpisodeResult> RunEpisodeAsync(IReadOnlyList<HaystackDoc> sessionDocs, CancellationToken ct)
    {
        var tools = new ScriniaMcpTools();
        var toolSpecs = AgenticIngestPrompts.Tools();
        var messages = new List<ToolChatMessage>
        {
            new("system", AgenticIngestPrompts.SystemPrompt()),
            new("user", AgenticIngestPrompts.SessionUserMessage(sessionDocs)),
        };

        var executed = new List<EpisodeToolCall>();
        int rememberCount = 0, searchCount = 0, appendCount = 0, malformedCount = 0;
        int turnsUsed = 0;

        for (int turn = 0; turn < _maxTurns; turn++)
        {
            turnsUsed = turn + 1;
            ToolTurnResult? turnResult = await _client.ChatWithToolsAsync(messages, toolSpecs, _maxTokens, ct);

            if (turnResult is null)
            {
                LogSummary(turnsUsed, rememberCount, appendCount, searchCount, malformedCount, succeeded: false);
                return new EpisodeResult(executed, Succeeded: false, LlmTurns: turnsUsed, ToolCallErrors: malformedCount);
            }

            // A turn that hit the token cap almost certainly truncated its tool-call
            // arguments — the JSON will fail to parse and read as a bogus "malformed
            // call". Surface it loudly rather than letting it masquerade as a bad call.
            if (string.Equals(turnResult.StopReason, "length", StringComparison.OrdinalIgnoreCase))
                Console.Error.WriteLine(
                    $"[scrinia-bench:agentic-ingest] WARNING: turn {turnsUsed} hit the token cap " +
                    $"(finish_reason=length, max_tokens={_maxTokens}) — the tool call was likely " +
                    "truncated. Raise --episode-max-tokens.");

            if (turnResult.ToolCalls.Count == 0)
            {
                LogSummary(turnsUsed, rememberCount, appendCount, searchCount, malformedCount, succeeded: true);
                return new EpisodeResult(executed, Succeeded: true, LlmTurns: turnsUsed, ToolCallErrors: malformedCount);
            }

            messages.Add(new ToolChatMessage("assistant", turnResult.AssistantContent, turnResult.ToolCalls));

            foreach (var call in turnResult.ToolCalls)
            {
                var (toolResult, isError) = await ExecuteCallAsync(tools, call, ct);
                messages.Add(new ToolChatMessage("tool", toolResult, ToolCallId: call.Id));

                if (isError)
                {
                    malformedCount++;
                    Console.Error.WriteLine(
                        $"[scrinia-bench:agentic-ingest] episode: {call.ToolName} call failed, skipping: {toolResult}");
                    continue;
                }

                executed.Add(new EpisodeToolCall(call.ToolName, call.ArgumentsJson));
                if (call.ToolName == "remember") rememberCount++;
                else if (call.ToolName == "append") appendCount++;
                else if (call.ToolName == "search") searchCount++;
            }
        }

        // maxTurns reached: stop with success (mirrors "the model kept working right up
        // to the budget" rather than treating the turn cap itself as a failure).
        LogSummary(turnsUsed, rememberCount, appendCount, searchCount, malformedCount, succeeded: true);
        return new EpisodeResult(executed, Succeeded: true, LlmTurns: turnsUsed, ToolCallErrors: malformedCount);
    }

    /// <summary>
    /// Replays a cached tool-call sequence against the current store WITHOUT the LLM.
    /// Used by the retriever on cache hit. Errors during replay are logged and skipped.
    /// </summary>
    public static async Task ReplayAsync(IReadOnlyList<EpisodeToolCall> calls, CancellationToken ct)
    {
        var tools = new ScriniaMcpTools();
        foreach (var call in calls)
        {
            var (result, isError) = await ExecuteCallAsync(
                tools, new ToolCallRequest(Id: "replay", call.ToolName, call.ArgumentsJson), ct);
            if (isError)
                Console.Error.WriteLine(
                    $"[scrinia-bench:agentic-ingest] replay: {call.ToolName} failed, skipping: {result}");
        }
    }

    /// <summary>
    /// Dispatches one tool call to the matching <see cref="ScriniaMcpTools"/> method.
    /// No tool call ever throws out of here: malformed args (JSON parse failure, or a
    /// required field missing) and exceptions from the tool method itself (e.g. the store
    /// rejecting an invalid memory <c>path</c>) both produce a production-style error
    /// string as the tool result so the model (or replay) can self-correct/continue.
    ///
    /// <para>Catching tool-execution exceptions mirrors production: the MCP SDK wraps a
    /// throwing tool and returns the error to the model as a tool result — the agent moves
    /// on. Letting the throw escape here would instead abort the whole benchmark run over
    /// one bad call, which is both unfaithful to real usage and destroys the entire run.</para>
    /// </summary>
    private static async Task<(string ToolResult, bool IsError)> ExecuteCallAsync(
        ScriniaMcpTools tools, ToolCallRequest call, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(call.ArgumentsJson))
            return ($"error: {call.ToolName} arguments are not valid JSON", true);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(call.ArgumentsJson);
        }
        catch (JsonException)
        {
            return ($"error: {call.ToolName} arguments are not valid JSON", true);
        }

        using (doc)
        {
            var root = doc.RootElement;
            (string ToolResult, bool IsError) outcome;
            try
            {
                outcome = call.ToolName switch
                {
                    "remember" => await DispatchRememberAsync(tools, root, ct),
                    "append" => await DispatchAppendAsync(tools, root, ct),
                    "recall" => await DispatchRecallAsync(tools, root, ct),
                    "search" => await DispatchSearchAsync(tools, root, ct),
                    _ => ($"error: unknown tool '{call.ToolName}'", true),
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A valid call whose *values* the store rejects (e.g. a colon in a v2
                // path -> PathParser throws) surfaces here. Return it as a tool error so
                // the episode continues, exactly as the production MCP surface would.
                // Cancellation is deliberately not swallowed — a cancelled run must stop.
                return ($"error: {call.ToolName} failed: {ex.Message}", true);
            }

            return (outcome.ToolResult,
                outcome.IsError || IsStructuredMcpErrorResponse(outcome.ToolResult));
        }
    }

    private static bool IsStructuredMcpErrorResponse(string response)
    {
        int firstLineEnd = response.IndexOf('\n');
        string firstLine = (firstLineEnd >= 0 ? response[..firstLineEnd] : response).TrimEnd('\r');
        return firstLine.Equals("status: error", StringComparison.Ordinal);
    }

    private static async Task<(string, bool)> DispatchRememberAsync(
        ScriniaMcpTools tools, JsonElement root, CancellationToken ct)
    {
        if (!TryGetString(root, "path", out string? path) || string.IsNullOrWhiteSpace(path))
            return ("error: remember requires 'path' and 'content'", true);
        if (!TryGetStringArray(root, "content", out string[]? content) || content is null || content.Length == 0)
            return ("error: remember requires 'path' and 'content'", true);

        TryGetAttributes(root, "attributes", out var attributes);
        TryGetStringArray(root, "keywords", out string[]? keywords);

        if (!TryGetString(root, "description", out string? description) || string.IsNullOrWhiteSpace(description))
            return ("error: remember requires a nonblank 'description'", true);
        string result = await tools.MemoryRemember(path, content, description, attributes, keywords, replace: root.TryGetProperty("replace", out var replace) && replace.ValueKind == JsonValueKind.True, cancellationToken: ct);
        return (result, false);
    }

    private static async Task<(string, bool)> DispatchAppendAsync(
        ScriniaMcpTools tools, JsonElement root, CancellationToken ct)
    {
        if (!TryGetString(root, "path", out string? path) || string.IsNullOrWhiteSpace(path))
            return ("error: append requires 'path' and 'content'", true);
        if (!TryGetString(root, "content", out string? content) || string.IsNullOrWhiteSpace(content))
            return ("error: append requires 'path' and 'content'", true);

        TryGetAttributes(root, "attributes", out var attributes);
        TryGetStringArray(root, "keywords", out string[]? keywords);

        if (!TryGetString(root, "description", out string? description) || string.IsNullOrWhiteSpace(description))
            return ("error: append replay requires a nonblank 'description'", true);
        string result = await tools.MemoryRemember(path, [content], description, attributes, keywords, cancellationToken: ct);
        return (result, false);
    }

    private static async Task<(string, bool)> DispatchRecallAsync(
        ScriniaMcpTools tools, JsonElement root, CancellationToken ct)
    {
        if (!TryGetString(root, "path", out string? path) || string.IsNullOrWhiteSpace(path))
            return ("error: recall requires 'path'", true);

        int? chunk = null;
        if (root.TryGetProperty("chunk", out var chunkEl) && chunkEl.ValueKind == JsonValueKind.Number
            && chunkEl.TryGetInt32(out int parsedChunk))
            chunk = parsedChunk;

        string result = await tools.MemoryRecall(path, chunk, withBuiltin: false, cancellationToken: ct);
        return (result, false);
    }

    private static async Task<(string, bool)> DispatchSearchAsync(
        ScriniaMcpTools tools, JsonElement root, CancellationToken ct)
    {
        if (!TryGetString(root, "query", out string? query) || string.IsNullOrWhiteSpace(query))
            return ("error: search requires 'query'", true);

        int limit = 20;
        if (root.TryGetProperty("limit", out var limitEl) && limitEl.ValueKind == JsonValueKind.Number
            && limitEl.TryGetInt32(out int parsedLimit))
            limit = parsedLimit;

        TryGetString(root, "context", out string? context);
        TryGetStringArray(root, "scopes", out string[]? scopes);
        TryGetStringArray(root, "excludeTopics", out string[]? excludeTopics);
        TryGetString(root, "pathPrefix", out string? pathPrefix);
        TryGetAttributes(root, "attributeFilter", out var attributeFilter);

        string result = await tools.MemorySearch(query, context, scopes, limit, excludeTopics, pathPrefix, attributeFilter, ct);
        return (result, false);
    }

    private static bool TryGetString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind != JsonValueKind.String) return false;
        value = el.GetString();
        return true;
    }

    private static bool TryGetStringArray(JsonElement root, string name, out string[]? value)
    {
        value = null;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind != JsonValueKind.Array) return false;

        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                list.Add(item.GetString() ?? "");
        }
        value = list.ToArray();
        return true;
    }

    // Parse an attribute map (key -> string values) matching remember/append's `attributes`
    // and search's `attributeFilter`. Lenient: a scalar string value is accepted as a
    // single-element array, since a bench LLM often emits { "person": "maria" }.
    private static bool TryGetAttributes(JsonElement root, string name, out Dictionary<string, string[]>? value)
    {
        value = null;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind != JsonValueKind.Object) return false;

        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in el.EnumerateObject())
        {
            var vals = new List<string>();
            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in prop.Value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String)
                        vals.Add(item.GetString() ?? "");
            }
            else if (prop.Value.ValueKind == JsonValueKind.String)
            {
                vals.Add(prop.Value.GetString() ?? "");
            }
            if (vals.Count > 0)
                map[prop.Name] = vals.ToArray();
        }
        if (map.Count == 0) return false;
        value = map;
        return true;
    }

    private static void LogSummary(int turns, int rememberCount, int appendCount, int searchCount, int malformedCount, bool succeeded)
    {
        Console.Error.WriteLine(
            $"[scrinia-bench:agentic-ingest] episode done: turns={turns} remember={rememberCount} " +
            $"append={appendCount} search={searchCount} malformed={malformedCount} succeeded={succeeded}");
    }
}
