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

/// <summary>One memory_remember/memory_search call actually dispatched during an episode.</summary>
public sealed record EpisodeToolCall(string ToolName, string ArgumentsJson);

/// <summary>Outcome of running one session episode.</summary>
public sealed record EpisodeResult(
    IReadOnlyList<EpisodeToolCall> ToolCalls,  // ordered remember/search calls actually executed
    bool Succeeded,                            // false → the session stores nothing (no mechanical fallback)
    int LlmTurns,
    int ToolCallErrors);                       // count of malformed/errored tool calls this episode

/// <summary>
/// Drives one agentic-ingest "episode": a chat + tool-calling loop against an
/// <see cref="IToolCallingClient"/>, dispatching the model's memory_remember /
/// memory_search tool calls to a real <see cref="ScriniaMcpTools"/> instance that
/// writes into whatever store is in <see cref="Scrinia.Core.MemoryStoreContext.Current"/>.
///
/// <para>The harness does not own store lifecycle (the caller sets
/// <see cref="Scrinia.Core.MemoryStoreContext.Current"/> before invoking) and does not
/// touch any cache — it returns the executed tool-call sequence plus a success flag so
/// the retriever can decide whether to cache the episode.</para>
///
/// <para><paramref name="maxTokens"/> caps the WHOLE assistant turn — any reasoning,
/// the assistant text, AND the tool-call <c>arguments</c> JSON all draw from it. Set it
/// too low and a tool call carrying real memory content is truncated mid-arguments
/// (<c>finish_reason=length</c>), yielding invalid JSON that reads as a bogus
/// "malformed tool call". The default is generous for that reason; the CLI exposes
/// <c>--episode-max-tokens</c> to tune it. A turn that hits the cap is logged loudly.</para>
/// </summary>
public sealed class AgenticIngestHarness
{
    private readonly IToolCallingClient _client;
    private readonly int _maxTurns;
    private readonly int _maxTokens;

    public AgenticIngestHarness(IToolCallingClient client, int maxTurns, int maxTokens = 4096)
    {
        _client = client;
        _maxTurns = maxTurns;
        _maxTokens = maxTokens;
    }

    /// <summary>
    /// Runs one episode against the store currently in <see cref="Scrinia.Core.MemoryStoreContext.Current"/>.
    /// Dispatches memory_remember/memory_search tool calls to a fresh <see cref="ScriniaMcpTools"/>.
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
        int rememberCount = 0, searchCount = 0, malformedCount = 0;
        int turnsUsed = 0;

        for (int turn = 0; turn < _maxTurns; turn++)
        {
            turnsUsed = turn + 1;
            ToolTurnResult? turnResult = await _client.ChatWithToolsAsync(messages, toolSpecs, _maxTokens, ct);

            if (turnResult is null)
            {
                LogSummary(turnsUsed, rememberCount, searchCount, malformedCount, succeeded: false);
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
                LogSummary(turnsUsed, rememberCount, searchCount, malformedCount, succeeded: true);
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
                if (call.ToolName == "memory_remember") rememberCount++;
                else if (call.ToolName == "memory_search") searchCount++;
            }
        }

        // maxTurns reached: stop with success (mirrors "the model kept working right up
        // to the budget" rather than treating the turn cap itself as a failure).
        LogSummary(turnsUsed, rememberCount, searchCount, malformedCount, succeeded: true);
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
    /// Malformed args (JSON parse failure, or a required field missing) never throw —
    /// they produce a production-style error string as the tool result so the model
    /// (or replay) can self-correct/continue.
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
            switch (call.ToolName)
            {
                case "memory_remember":
                    return await DispatchRememberAsync(tools, root, ct);
                case "memory_search":
                    return await DispatchSearchAsync(tools, root, ct);
                default:
                    return ($"error: unknown tool '{call.ToolName}'", true);
            }
        }
    }

    private static async Task<(string, bool)> DispatchRememberAsync(
        ScriniaMcpTools tools, JsonElement root, CancellationToken ct)
    {
        if (!TryGetString(root, "path", out string? path) || string.IsNullOrWhiteSpace(path))
            return ("error: memory_remember requires 'path' and 'content'", true);
        if (!TryGetStringArray(root, "content", out string[]? content) || content is null || content.Length == 0)
            return ("error: memory_remember requires 'path' and 'content'", true);

        TryGetString(root, "description", out string? description);
        TryGetStringArray(root, "tags", out string[]? tags);
        TryGetStringArray(root, "keywords", out string[]? keywords);
        TryGetStringArray(root, "anchors", out string[]? anchors);

        string result = await tools.MemoryRemember(
            path, content, description, tags, keywords, anchors,
            reviewAfter: null, reviewWhen: null, codeRefs: null, ct);
        return (result, false);
    }

    private static async Task<(string, bool)> DispatchSearchAsync(
        ScriniaMcpTools tools, JsonElement root, CancellationToken ct)
    {
        if (!TryGetString(root, "query", out string? query) || string.IsNullOrWhiteSpace(query))
            return ("error: memory_search requires 'query'", true);

        int limit = 20;
        if (root.TryGetProperty("limit", out var limitEl) && limitEl.ValueKind == JsonValueKind.Number
            && limitEl.TryGetInt32(out int parsedLimit))
            limit = parsedLimit;

        TryGetString(root, "context", out string? context);
        TryGetString(root, "scopes", out string? scopes);
        TryGetString(root, "excludeTopics", out string? excludeTopics);
        TryGetString(root, "pathPrefix", out string? pathPrefix);
        TryGetString(root, "tags", out string? tags);

        string result = await tools.MemorySearch(query, context, scopes, limit, excludeTopics, pathPrefix, tags, ct);
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

    private static void LogSummary(int turns, int rememberCount, int searchCount, int malformedCount, bool succeeded)
    {
        Console.Error.WriteLine(
            $"[scrinia-bench:agentic-ingest] episode done: turns={turns} remember={rememberCount} " +
            $"search={searchCount} malformed={malformedCount} succeeded={succeeded}");
    }
}
