using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scrinia.Core.Llm;

/// <summary>
/// Prompt templates for the Tier 2 consolidation tasks. Single source of truth —
/// both the OpenAI-compatible HTTP backend and the bundled plugin backend share
/// these so adding a new Tier 2 capability is a Scrinia.Core change, not a backend
/// rebuild. Prompts are tuned for small (1–2B param) instruction-following models
/// so they stay terse, schema-light, and tolerant of variation.
/// </summary>
internal static partial class LlmPrompts
{
    // ^\s*([-*•] | \d+[.)])\s+ — matches a real list marker only. "1.5 GB ..." does not
    // match because the digits aren't followed by space-after-./).
    [GeneratedRegex(@"^\s*(?:[-*•]|\d+[.)])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex ListMarkerPrefixRegex();
    private static readonly Regex ListMarkerPrefix = ListMarkerPrefixRegex();

    /// <summary>
    /// Hard cap on input chars sent to the model. Set well under the typical 8K context
    /// budget of small models, leaving headroom for the system prompt and the response.
    /// Truncation is character-based (not token-based) to avoid a tokenizer dependency
    /// — overshoot is tolerable here, undershoot would not be.
    /// </summary>
    public const int MaxInputChars = 12_000;

    /// <summary>
    /// Short summarisation suitable for descriptions (one sentence) vs the longer
    /// session-rollup form. Different limit because callers pass differently sized inputs.
    /// </summary>
    public const int MaxDescriptionInputChars = 4_000;

    public const string DescriptionSystem =
        "Summarize this content in 120 characters or less. Plain text, no quotes.";

    public static string DescriptionUser(string content) =>
        "Content:\n\n" + Truncate(content, MaxDescriptionInputChars);

    public const string SummarySystem =
        "You are a memory librarian. Summarise the following text into one paragraph " +
        "(3–5 sentences) capturing the key information and decisions/outcomes. " +
        "Output only the summary, no preamble or quotes.";

    public static string SummaryUser(string text) =>
        "Text:\n\n" + Truncate(text, MaxInputChars);

    public const string FactsSystem =
        "Gather up to 7 self-contained facts from this content without commentary, " +
        "each under 120 characters, one per line, in plain text.";

    public static string FactsUser(string content) =>
        "Content:\n\n" + Truncate(content, MaxInputChars);

    /// <summary>
    /// Combined description + facts request. The system prompt asks for a single JSON
    /// object so one round-trip produces both signals. Used by
    /// <c>IBackgroundLlm.EnrichCombinedAsync</c> on backends that can honor
    /// <c>response_format: { type: "json_object" }</c> at the wire (Ollama, llama.cpp
    /// server, LM Studio, vLLM, OpenAI itself).
    /// </summary>
    public const string CombinedSystem =
        "Summarize this content in under 120 chars and gather up to 7 self-contained " +
        "facts from it (each under 120 chars, no commentary). Return as JSON with " +
        "keys \"description\" and \"facts\". JSON only.";

    public static string CombinedUser(string content) =>
        "Content:\n\n" + Truncate(content, MaxInputChars);

    /// <summary>
    /// Parse a combined-enrichment LLM response into <see cref="CombinedEnrichment"/>.
    /// Tolerates: (1) markdown code fences around the JSON (e.g. <c>```json ... ```</c>),
    /// (2) leading or trailing prose outside the object, (3) empty strings / empty
    /// arrays / missing fields (treated as null per the per-op null semantics),
    /// (4) malformed JSON (returns null overall — caller falls back to per-op).
    /// </summary>
    public static CombinedEnrichment? ParseCombinedJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string text = StripMarkdownFences(raw);

        // Locate the first balanced top-level JSON object. Walking the string with a
        // depth counter is cheap and handles arbitrary leading/trailing prose better
        // than a regex would (regex would need to know about escaped quotes).
        int start = text.IndexOf('{');
        if (start < 0) return null;

        int end = FindMatchingBrace(text, start);
        if (end < 0) return null;

        string jsonObj = text[start..(end + 1)];

        try
        {
            using var doc = JsonDocument.Parse(jsonObj);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? description = null;
            if (root.TryGetProperty("description", out var descEl)
                && descEl.ValueKind == JsonValueKind.String)
            {
                string? d = descEl.GetString()?.Trim();
                description = string.IsNullOrWhiteSpace(d) ? null : d;
            }

            string[]? facts = null;
            if (root.TryGetProperty("facts", out var factsEl)
                && factsEl.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>(factsEl.GetArrayLength());
                foreach (var item in factsEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    string? f = item.GetString()?.Trim();
                    // Reuse ParseFacts' 5-char-min / 240-char-max bounds so combined and
                    // line-mode pipelines accept the same fact shape.
                    if (string.IsNullOrWhiteSpace(f)) continue;
                    if (f.Length < 5 || f.Length > 240) continue;
                    list.Add(f);
                }
                facts = list.Count > 0 ? list.ToArray() : null;
            }

            if (description is null && facts is null) return null;
            return new CombinedEnrichment(description, facts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StripMarkdownFences(string raw)
    {
        string s = raw.Trim();
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;

        // ```optional-lang\n...\n``` — drop the opening fence line and the trailing
        // ``` if present. If the model wrote an unclosed fence, fall back to dropping
        // just the opening line; the brace-finder will still locate the JSON object.
        int firstNewline = s.IndexOf('\n');
        if (firstNewline > 0) s = s[(firstNewline + 1)..];
        int lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence >= 0) s = s[..lastFence];
        return s.Trim();
    }

    private static int FindMatchingBrace(string text, int openIdx)
    {
        int depth = 0;
        bool inString = false;
        bool escape = false;
        for (int i = openIdx; i < text.Length; i++)
        {
            char c = text[i];
            if (escape) { escape = false; continue; }
            if (inString)
            {
                if (c == '\\') { escape = true; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    public const string ImportanceSystem =
        "You are a memory librarian. Rate how important and lasting this memory is on " +
        "a 1–10 scale (Generative Agents poignancy). 1 = trivial, ephemeral, low value " +
        "(small talk, throwaway state). 5 = moderately useful, average context " +
        "(routine notes, common observations). 10 = critically important, foundational " +
        "knowledge (durable decisions, architectural facts, personal commitments). " +
        "Output only the integer number, no preamble or explanation.";

    public static string ImportanceUser(string content) =>
        "Memory:\n\n" + Truncate(content, MaxDescriptionInputChars);

    /// <summary>
    /// Parses an LLM importance response into a 1–10 integer. Tolerates the common
    /// failure modes of small models — extra whitespace, trailing periods, brief
    /// explanations preceding the digit ("I'd rate this 7"), and integer-ish floats
    /// ("7.5" → 7). Returns null when no parseable digit-run exists, when the value
    /// is outside [1, 10], or when the input is empty/whitespace.
    /// </summary>
    public static int? ParseImportance(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = ImportanceDigitRegex().Match(raw);
        if (!m.Success) return null;
        if (!int.TryParse(m.Value, out int value)) return null;
        if (value < 1 || value > 10) return null;
        return value;
    }

    // First contiguous digit run anywhere in the response, capturing an optional leading
    // minus so "-5" parses to -5 (then rejected as out-of-range). Without the leading `-`
    // capture the parser would silently see "5" inside "-5" and accept it — and negative
    // numbers are unambiguously nonsense for an importance rating.
    [GeneratedRegex(@"-?\d+", RegexOptions.CultureInvariant)]
    private static partial Regex ImportanceDigitRegex();

    /// <summary>
    /// Parse the model's facts response into a clean string array. Tolerates bullets,
    /// numbering, and bracket markers because small models often slip them in despite
    /// the instructions. Filters empties, deduplicates case-insensitively, caps length.
    /// </summary>
    public static string[] ParseFacts(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();
        foreach (string line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            // Anchored marker strip: only consume runs that actually look like a list bullet
            // ("- ", "* ", "• ") or numbered marker ("1. ", "42) ", etc.). Earlier versions
            // greedy-consumed any contiguous run of {-, *, •, digit, ., ), space, tab} which
            // ate the leading "1.5" off facts like "1.5 GB of memory consumed" or the "5-"
            // off "5-year project" — numerical facts are exactly what Mem0-style extraction
            // targets so the regression was particularly bad.
            var m = ListMarkerPrefix.Match(trimmed);
            if (m.Success && m.Length < trimmed.Length)
                trimmed = trimmed[m.Length..].Trim();

            // Strip surrounding quotes
            if (trimmed.Length >= 2
                && ((trimmed[0] == '"' && trimmed[^1] == '"')
                    || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
                trimmed = trimmed[1..^1].Trim();

            if (trimmed.Length < 5) continue;       // too short to be a fact
            if (trimmed.Length > 240) continue;     // too long — likely a paragraph
            if (!seen.Add(trimmed)) continue;       // deduplicate
            results.Add(trimmed);
            if (results.Count >= 12) break;         // hard cap above the prompt's "3–7"
        }
        return [.. results];
    }

    private static string Truncate(string s, int maxChars) =>
        s.Length <= maxChars ? s : s[..maxChars] + "\n\n[...truncated]";
}
