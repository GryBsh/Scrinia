using System.Text.Json;

namespace Scrinia.Retrieval.Benchmarks.Agent;

/// <summary>
/// System/user prompt and output parser for the single agent ingest op: given a
/// memory's content, produce what a production agent hands the <c>remember</c> MCP
/// tool — a typed <c>attributes</c> map and freeform <c>keywords</c>. One LLM call
/// yields both, exactly as the tool takes them in one shot, so the mechanical arm
/// measures the real production surface rather than a bench-only decomposition.
///
/// <para><b>Anti-data-leakage discipline:</b> the prompt is built from document
/// content ONLY. It never sees the gold answer set or the benchmark question
/// dataset — the harness simulates what a production agent could do given only what
/// the user actually stored.</para>
///
/// <para><b>Hard caps</b> bound input at <see cref="MaxInputChars"/> and the parsed
/// output at <see cref="MaxAttributeKeysPerDoc"/> keys / <see cref="MaxValuesPerKey"/>
/// values-per-key / <see cref="MaxKeywordsPerDoc"/> keywords, so a runaway generation
/// can't bloat the index.</para>
/// </summary>
public static class AgentPrompts
{
    /// <summary>Truncation budget for content sent to the LLM. Sized so small models
    /// with ~8K context have room for the system prompt + response.</summary>
    public const int MaxInputChars = 12_000;

    /// <summary>Caps mirroring the cost guards the old per-op parsers enforced: keep
    /// the attribute map and keyword list from bloating BM25's TF dict / field scorer.</summary>
    public const int MaxAttributeKeysPerDoc = 12;
    public const int MaxValuesPerKey = 8;
    public const int MaxKeywordsPerDoc = 24;

    /// <summary>Per-value/keyword length bounds. Below 2 chars is noise; above 120 is a
    /// sentence, not a facet value or search term.</summary>
    public const int MinTermChars = 2;
    public const int MaxTermChars = 120;

    // The prompt deliberately mirrors the remember tool's attributes/keywords argument
    // descriptions (AgenticIngestPrompts.RememberSchema) so the mechanical arm asks the
    // model for exactly what a real agent supplies the tool.
    public const string IngestSystem =
        "You are helping an agent index a memory for later retrieval. Given a memory's content, produce a typed " +
        "attributes map and a keywords list, and return them as a single JSON object:\n" +
        "  \"attributes\": an object of TYPED FACETS that link related memories. Each key is a facet type and " +
        "each value is an array of short lowercased canonical forms. Recommended keys: person, place, org, " +
        "thing, topic, date. Example: {\"person\":[\"maria\"],\"place\":[\"lisbon\"],\"topic\":[\"home renovation\"]}.\n" +
        "  \"keywords\": an array of distinctive terms and likely search phrasings for this memory — vocabulary a " +
        "user might actually query with, including synonyms and category-level terms.\n" +
        "Return ONLY the JSON object, no commentary, no code fences. " +
        "You must produce attributes and keywords. Do not return null, nothing, or an empty stub object" +
        "The minimum is 1 keyword and 1 `topic:` attribute";

    /// <summary>Builds the ingest user prompt from doc content (truncated to
    /// <see cref="MaxInputChars"/>). Content only — the anti-data-leakage contract.</summary>
    public static string IngestUser(string content) =>
        "Memory content:\n\n" + Truncate(content, MaxInputChars);

    /// <summary>
    /// Parses the combined ingest output (attributes + keywords) from raw LLM output. Tolerant of a code-fence
    /// wrapper and of scalar-instead-of-array attribute values (a small model often emits
    /// <c>{"person":"maria"}</c>), matching the leniency of the agentic arm's tool-arg
    /// parser. Values and keywords are lowercased, trimmed, length-filtered, deduped, and
    /// capped. Returns null when nothing usable parses (callers cache null to skip retries).
    /// </summary>
    public static AgentIngest? ParseIngest(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string json = StripCodeFence(raw.Trim());

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var attributes = ParseAttributes(doc.RootElement);
            var keywords = ParseKeywords(doc.RootElement);
            if (attributes.Count == 0 && keywords.Count == 0) return null;
            return new AgentIngest(attributes, keywords);
        }
    }

    private static Dictionary<string, string[]> ParseAttributes(JsonElement root)
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("attributes", out var attrsEl) || attrsEl.ValueKind != JsonValueKind.Object)
            return map;

        foreach (var prop in attrsEl.EnumerateObject())
        {
            if (map.Count >= MaxAttributeKeysPerDoc) break;
            string key = prop.Name.Trim().ToLowerInvariant();
            if (key.Length == 0) continue;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var values = new List<string>(MaxValuesPerKey);
            // Lenient: accept an array of strings OR a single scalar string.
            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (values.Count >= MaxValuesPerKey) break;
                    AddTerm(item, seen, values);
                }
            }
            else if (prop.Value.ValueKind == JsonValueKind.String)
            {
                AddTerm(prop.Value, seen, values);
            }

            if (values.Count > 0) map[key] = values.ToArray();
        }
        return map;
    }

    private static List<string> ParseKeywords(JsonElement root)
    {
        var keywords = new List<string>(MaxKeywordsPerDoc);
        if (!root.TryGetProperty("keywords", out var kwEl) || kwEl.ValueKind != JsonValueKind.Array)
            return keywords;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in kwEl.EnumerateArray())
        {
            if (keywords.Count >= MaxKeywordsPerDoc) break;
            AddTerm(item, seen, keywords);
        }
        return keywords;
    }

    /// <summary>Normalizes one JSON string term (lowercase, trim, length-filter, dedup)
    /// and appends it to <paramref name="into"/> if it survives.</summary>
    private static void AddTerm(JsonElement item, HashSet<string> seen, List<string> into)
    {
        if (item.ValueKind != JsonValueKind.String) return;
        string? s = item.GetString();
        if (string.IsNullOrWhiteSpace(s)) return;
        string term = s.Trim().ToLowerInvariant();
        if (term.Length < MinTermChars) return;
        if (term.Length > MaxTermChars) term = term[..MaxTermChars].TrimEnd();
        if (seen.Add(term)) into.Add(term);
    }

    private static string StripCodeFence(string s)
    {
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;
        int nl = s.IndexOf('\n');
        if (nl >= 0) s = s[(nl + 1)..];
        int end = s.LastIndexOf("```", StringComparison.Ordinal);
        if (end >= 0) s = s[..end];
        return s.Trim();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];
}
