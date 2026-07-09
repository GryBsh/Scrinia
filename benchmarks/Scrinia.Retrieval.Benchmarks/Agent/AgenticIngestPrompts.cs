using System.Text;
using Scrinia.Mcp;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;

namespace Scrinia.Retrieval.Benchmarks.Agent;

public static class AgenticIngestPrompts
{
    public const string PromptVersion = "v2";

    private const string Addendum = """

        ---
        You are an impartial memory agent who stores session memories using memory_remember, adds to an existing memory using memory_append, reads one back using memory_recall, and finds earlier ones using memory_search. You are not a human, and you do not have opinions or preferences. You are a tool that helps the user store and retrieve factual information.
        memory_remember OVERWRITES whatever is already at a path. Before writing, memory_search for a related memory you already stored this session; if one exists, memory_recall it and memory_append the new facts rather than calling memory_remember on the same path. Use a fresh, specific path only for a genuinely new topic.
        When you store or append, you must include one or more ref: <doc-id> lines in the content, so the memory can be traced back to its source turns (the [doc-id] labels on the transcript). You must also include a path, description, tags, keywords, and anchors if applicable.
        When you are done storing, stop calling tools.

        Store 1 or more memories from the session transcript below.
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
            "Persist a NEW memory at a path (OVERWRITES anything already there). content is an array — " +
            "each element becomes one independently retrievable chunk. Include ref: <doc-id> lines naming the source turns.",
            RememberSchema),
        new ToolSpec("memory_append",
            "Append a chunk to an EXISTING memory without overwriting it. Use after memory_recall to add facts " +
            "to a memory you already stored. Include ref: <doc-id> lines naming the source turns.",
            AppendSchema),
        new ToolSpec("memory_recall",
            "Read back a memory you stored earlier this session, by its path, to decide whether to append to it.",
            RecallSchema),
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

    // Mirrors memory_append (path, content as a SINGLE string, description?, tags?, keywords?, anchors?).
    private const string AppendSchema = """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Existing memory path to append to." },
            "content": { "type": "string", "description": "New chunk text. Include ref: <doc-id> lines." },
            "description": { "type": "string" },
            "tags": { "type": "array", "items": { "type": "string" } },
            "keywords": { "type": "array", "items": { "type": "string" } },
            "anchors": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["path", "content"]
        }
        """;

    private const string RecallSchema = """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Memory path to read back." },
            "chunk": { "type": "integer", "description": "Optional 1-based chunk index." }
          },
          "required": ["path"]
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
