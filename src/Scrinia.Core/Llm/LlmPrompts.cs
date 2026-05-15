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
        "You are a memory librarian. Write a single concise description sentence " +
        "(under 120 characters) that captures what the content is about — useful for " +
        "search and recall. Output only the description, no preamble or quotes.";

    public static string DescriptionUser(string content) =>
        "Content:\n\n" + Truncate(content, MaxDescriptionInputChars);

    public const string SummarySystem =
        "You are a memory librarian. Summarise the following text into one paragraph " +
        "(3–5 sentences) capturing the key information and decisions/outcomes. " +
        "Output only the summary, no preamble or quotes.";

    public static string SummaryUser(string text) =>
        "Text:\n\n" + Truncate(text, MaxInputChars);

    public const string FactsSystem =
        "You are a memory librarian. Extract 3–7 atomic facts from the content. " +
        "Each fact must be: (1) self-contained — understandable without surrounding " +
        "context, (2) a single claim, (3) under 120 characters. " +
        "Output one fact per line. No numbering, no bullets, no prefixes — just the " +
        "fact text on its own line. Output only the facts, no preamble.";

    public static string FactsUser(string content) =>
        "Content:\n\n" + Truncate(content, MaxInputChars);

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
