using System.Runtime.CompilerServices;
using System.Text.Json;
using Scrinia.Core;
using Scrinia.Core.Search;
using Scrinia.Server.Services;

namespace Scrinia.Server.Chat;

/// <summary>
/// Orchestrates the agentic loop: call LLM → if tool calls, execute them against the store
/// → append results → re-call LLM → repeat until no tools or max rounds.
/// </summary>
public static class AgentLoop
{
    private const int MaxToolRounds = 5;

    /// <summary>
    /// Cap on a single tool result folded back into the agent's context. Chosen to keep one
    /// oversized recall from crowding out the conversation; it bounds context, not the MCP
    /// response, so it is deliberately unrelated to ResponseBuilder's own response cap.
    /// </summary>
    private const int MaxRecallCharsPerTool = 8000;

    private const string SystemPrompt = """
        You are an AI assistant with access to a knowledge base. Your answers should be grounded
        in the stored memories. Always search before answering a question — don't guess.

        When you find relevant memories:
        - Cite which memory you found the information in
        - If multiple memories are relevant, synthesize across them
        - If no relevant memories are found, say so honestly

        Be concise and direct. Focus on answering the user's question.
        """;

    private static readonly AgentToolDef[] ToolDefinitions =
    [
        new("search_memory", "Search the knowledge base for relevant memories. Always call this before answering a question.",
            new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["query"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Search query" },
                    ["limit"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Max results (default 10)" },
                },
                ["required"] = new[] { "query" },
            }),
        new("recall_memory", "Retrieve the full content of a specific memory by name.",
            new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Memory name (e.g., 'api:auth-flow')" },
                },
                ["required"] = new[] { "name" },
            }),
    ];

    /// <summary>
    /// Runs the agent loop. Yields ChatEvents for streaming to the client.
    /// </summary>
    public static async IAsyncEnumerable<ChatEvent> RunAsync(
        ChatMessage[] userMessages,
        IMemoryStore store,
        IChatProvider provider,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Build conversation with system prompt
        var conversation = new List<ChatMessage>
        {
            new("system", SystemPrompt),
        };
        conversation.AddRange(userMessages);

        for (int round = 0; round <= MaxToolRounds; round++)
        {
            // Collect all events from the provider
            var chunks = new List<string>();
            var toolCalls = new List<(string Id, string Name, string Args)>();

            await foreach (var evt in provider.StreamChatAsync(
                conversation.ToArray(), ToolDefinitions, ct))
            {
                switch (evt.Type)
                {
                    case "chunk":
                        chunks.Add(evt.Content ?? "");
                        yield return evt; // Stream text to client immediately
                        break;

                    case "tool-call":
                        toolCalls.Add((evt.ToolCallId ?? $"call_{toolCalls.Count}",
                            evt.ToolName ?? "unknown", evt.Content ?? "{}"));
                        break;

                    case "error":
                        yield return evt;
                        yield break;
                }
            }

            // No tool calls — we're done
            if (toolCalls.Count == 0)
            {
                yield return new ChatEvent("done");
                yield break;
            }

            // Add assistant message with tool calls to conversation
            string assistantText = string.Concat(chunks);
            conversation.Add(new ChatMessage("assistant", assistantText,
                toolCalls.Select(tc => new ChatToolCall(tc.Id, tc.Name, tc.Args)).ToArray()));

            // Execute each tool
            foreach (var (id, name, args) in toolCalls)
            {
                yield return new ChatEvent("tool-start", ToolName: name, ToolCallId: id);

                string result = await ExecuteToolAsync(name, args, store, ct);

                yield return new ChatEvent("tool-result", Content: result, ToolName: name, ToolCallId: id);

                // Add tool result to conversation
                conversation.Add(new ChatMessage("tool", result, ToolCallId: id));
            }

            // Loop continues — provider will be called again with updated conversation
        }

        // Max rounds exceeded
        yield return new ChatEvent("done");
    }

    private static async Task<string> ExecuteToolAsync(
        string toolName, string argsJson, IMemoryStore store, CancellationToken ct)
    {
        try
        {
            JsonElement args;
            try { args = JsonSerializer.Deserialize<JsonElement>(argsJson); }
            catch { return "Error: invalid tool arguments."; }

            return toolName switch
            {
                "search_memory" => await ExecuteSearchAsync(args, store, ct),
                "recall_memory" => await ExecuteRecallAsync(args, store, ct),
                _ => $"Error: unknown tool '{toolName}'.",
            };
        }
        catch (Exception)
        {
            return "Error executing tool.";
        }
    }

    private static async Task<string> ExecuteSearchAsync(
        JsonElement args, IMemoryStore store, CancellationToken ct)
    {
        string query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        int limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out int lv) ? lv : 10;

        if (string.IsNullOrWhiteSpace(query))
            return "Error: query is required.";

        limit = Math.Clamp(limit, 1, 50);
        var results = store.SearchAll(new SearchQuery(query) { Limit = limit });

        if (results.Count == 0)
            return "No matching memories found.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Found {results.Count} result(s):");
        foreach (var r in results)
        {
            string name = r switch
            {
                EntryResult er => store.FormatQualifiedName(er.Item.Scope, er.Item.Entry.Name),
                ChunkEntryResult cr => store.FormatQualifiedName(cr.ParentItem.Scope, cr.ParentItem.Entry.Name),
                TopicResult tr => tr.TopicName,
                _ => "unknown",
            };
            string desc = r switch
            {
                EntryResult er => AttributeReaders.PreviewText(er.Item.Entry.ContentPreview),
                ChunkEntryResult cr => AttributeReaders.PreviewText(cr.ParentItem.Entry.ContentPreview),
                TopicResult tr => tr.Description ?? "",
                _ => "",
            };
            sb.AppendLine($"- **{name}** (score: {r.Score:F0}) — {desc}");
        }
        return sb.ToString();
    }

    private static async Task<string> ExecuteRecallAsync(
        JsonElement args, IMemoryStore store, CancellationToken ct)
    {
        string name = args.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(name))
            return "Error: name is required.";

        // Surface-level canonicalization — the LLM is the caller here, the same class of
        // agent-facing surface MemoryTools' facade methods canonicalize for. Without this,
        // `/temp/x` would resolve to the persistent scope `local:temp` instead of the in-memory
        // ephemeral `~x` that a human's `recall`/`remember` calls resolve to for that same address.
        name = MemoryNaming.CanonicalizeEphemeralAlias(name);

        var result = await MemoryOrchestrator.ShowAsync(store, name, ct);
        if (result is null)
            return $"Memory '{name}' not found.";

        // Truncate very large memories to avoid overwhelming the LLM context. Concatenated with
        // no separator: chunk boundaries carry no semantic delimiter (Nmp2Strategy.Decode joins
        // multi-chunk artifacts the same way), so this reconstructs the exact original text.
        string content = string.Concat(result.Content);
        if (content.Length > MaxRecallCharsPerTool)
            content = content[..MaxRecallCharsPerTool] + "\n[... truncated]";

        return $"## {name}\n{content}";
    }
}
