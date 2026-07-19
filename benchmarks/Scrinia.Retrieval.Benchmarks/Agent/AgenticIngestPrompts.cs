using System.Text;
using Scrinia.Mcp;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;

namespace Scrinia.Retrieval.Benchmarks.Agent;

public static class AgenticIngestPrompts
{
    // Bumped v2 -> v3 when the bench tool surface was renamed from the stale
    // memory_* names to the real MCP bare names (remember/append/recall/search),
    // invalidating cached episodes whose recorded ToolNames still use memory_*.
    public const string PromptVersion = "v3";

    private const string Addendum = """

        ---
        You are an impartial memory agent who cultivates a comprehensive knowledge base from the session transcript. You are not a general-purpose assistant; you do not answer questions or provide advice. You only store and retrieve memories.
        You use remember, add to an existing memory using append, read one back using recall, and find earlier ones using search. 
        Remember, remember OVERWRITES whatever is already at a path. Before writing, search for a related memory you already stored this session; 
        if one exists, recall it and append the new facts or add a new memory to a child path. 
        Include relevant dates, locations, facts, entities, and other context in the memory content and be as specific as possible.
        You must store or append at least one memory for each turn in the transcript.
        When you store or append, you must include one or more ref: <doc-id> lines in the content, so the memory can be traced back to its source turns (the [doc-id] labels on the transcript). You must also include a path and, where applicable, attributes (typed entities like person, place, org, thing, topic) and keywords.
        If you believe no new memories are needed, you MUST use search to confirm that no related memories exist, and then you may stop storing. 
        If you find a related memory, you MUST use recall to read it back and then append to add new facts to it.
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
        new ToolSpec("remember",
            "Persist a NEW memory at a path (OVERWRITES anything already there). content is an array — " +
            "each element becomes one independently retrievable chunk. Include ref: <doc-id> lines naming the source turns.",
            RememberSchema),
        new ToolSpec("append",
            "Append a chunk to an EXISTING memory without overwriting it. Use after recall to add facts " +
            "to a memory you already stored. Include ref: <doc-id> lines naming the source turns.",
            AppendSchema),
        new ToolSpec("recall",
            "Read back a memory you stored earlier this session, by its path, to decide whether to append to it.",
            RecallSchema),
        new ToolSpec("search",
            "Find earlier memories you stored in this session by keyword or phrase. " +
            "Optionally scope with pathPrefix or attributeFilter.",
            SearchSchema),
    ];

    // Mirrors the real remember params (path, content[], attributes?, keywords?).
    // review/file/ref attribute keys omitted — not meaningful for bench conversations.
    private const string RememberSchema = """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Memory path, e.g. /findings/lisbon-trip." },
            "content": { "type": "array", "items": { "type": "string" },
                         "description": "Each element becomes one chunk. Include ref: <doc-id> lines." },
            "attributes": {
              "type": "object",
              "additionalProperties": { "type": "array", "items": { "type": "string" } },
              "description": "Typed facets that link related memories. Recommended keys: person, place, org, thing, topic. Example: { \"person\": [\"maria\"], \"place\": [\"lisbon\"] }."
            },
            "keywords": { "type": "array", "items": { "type": "string" },
                          "description": "Distinctive terms and likely search phrasings for this memory." }
          },
          "required": ["path", "content"]
        }
        """;

    // Mirrors append (path, content as a SINGLE string, attributes?, keywords?).
    private const string AppendSchema = """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Existing memory path to append to." },
            "content": { "type": "string", "description": "New chunk text. Include ref: <doc-id> lines." },
            "attributes": {
              "type": "object",
              "additionalProperties": { "type": "array", "items": { "type": "string" } },
              "description": "Typed facets merged into the memory. Recommended keys: person, place, org, thing, topic." },
            "keywords": { "type": "array", "items": { "type": "string" } }
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

    // Mirrors the real search params the dispatch honors: query, context, scopes,
    // limit, excludeTopics, pathPrefix, attributeFilter.
    private const string SearchSchema = """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string" },
            "context": { "type": "string", "description": "Optional context terms appended to the query for scoring only." },
            "scopes": { "type": "string", "description": "Optional comma-separated scope order, e.g. 'local,api'." },
            "limit": { "type": "integer" },
            "excludeTopics": { "type": "string", "description": "Optional comma-separated topic names to exclude from results." },
            "pathPrefix": { "type": "string", "description": "Optional path prefix to scope results, e.g. '/findings/'." },
            "attributeFilter": {
              "type": "object",
              "additionalProperties": { "type": "array", "items": { "type": "string" } },
              "description": "Optional filter — return only memories carrying every listed key (and, where values are given, at least one of them)."
            }
          },
          "required": ["query"]
        }
        """;
}
