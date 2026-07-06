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
