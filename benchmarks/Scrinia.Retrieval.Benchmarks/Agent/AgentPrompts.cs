using System.Text.RegularExpressions;
using Scrinia.Core.Search;

namespace Scrinia.Retrieval.Benchmarks.Agent;

/// <summary>
/// System and user prompts for the three agent harness ops, plus parsers for their
/// LLM outputs.
///
/// <para><b>Anti-data-leakage discipline:</b> these prompts are constructed from
/// document content only (for anchors) or user-query text only (for rewrite/HyDE).
/// They never see the gold answer set or the benchmark question dataset. The
/// retrieval anchors prompt explicitly tells the model to imagine plausible user
/// queries — what a user would type — using vocabulary that differs from the
/// content. The hypotheticals in the prompt are domain-neutral so they don't bias
/// the model toward our benchmark categories.</para>
///
/// <para><b>Hard caps</b> bound input at 12K chars and add a per-output cap
/// (anchors: 5–240 chars each, max 7 anchors per doc).</para>
/// </summary>
public static partial class AgentPrompts
{
    /// <summary>Truncation budget for content sent to the LLM. Sized so small models
    /// with ~8K context have room for the system prompt + response.</summary>
    public const int MaxInputChars = 12_000;

    /// <summary>Per-anchor character bounds. Below 5 chars is noise (single tokens
    /// like "yes"); above 240 chars is a fact or sentence, not a query phrase.</summary>
    public const int MinAnchorChars = 5;
    public const int MaxAnchorChars = 240;

    /// <summary>Maximum number of anchors retained per doc. Even if the model emits
    /// more, we trim — too many anchors per doc bloats BM25's TF dict without
    /// proportional recall lift.</summary>
    public const int MaxAnchorsPerDoc = 7;

    // ^\s*([-*•] | \d+[.)])\s+ — matches a list marker only (digits not followed by
    // ./) followed by a space are NOT a list marker, e.g. "1.5 GB" survives).
    [GeneratedRegex(@"^\s*(?:[-*•]|\d+[.)])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex ListMarkerPrefixRegex();
    private static readonly Regex ListMarkerPrefix = ListMarkerPrefixRegex();

    // ---------- Anchors ----------

    public const string AnchorsSystem =
        "You are helping index a memory for retrieval. Given a memory's content, imagine 3 to 5 plausible " +
        "natural-language QUERIES a user might type to look this memory up later. Use VOCABULARY THE USER " +
        "WOULD QUERY WITH — synonyms, category-level terms, and paraphrases — rather than the literal " +
        "vocabulary already present in the content (the indexer already covers exact-match). " +
        "Examples of the form (not the topic): \"how do I undo my last commit\", \"what's the policy on expense reports\", " +
        "\"when did I last visit\". Output one anchor per line, plain text, no numbering, no bullets, no commentary.";

    /// <summary>Builds the anchor user prompt from doc content. Content is truncated
    /// to <see cref="MaxInputChars"/> before being sent to the LLM.</summary>
    public static string AnchorsUser(string content) =>
        "Memory content:\n\n" + Truncate(content, MaxInputChars);

    /// <summary>
    /// Parses an anchor list from raw LLM output. Tolerates:
    /// - one anchor per line OR comma-separated within a line,
    /// - leading bullet/numbering markers ("- foo", "* foo", "1. foo", "2) foo"),
    /// - surrounding whitespace,
    /// - duplicate lines (case-insensitive dedup).
    /// Filters out anchors shorter than <see cref="MinAnchorChars"/> or longer than
    /// <see cref="MaxAnchorChars"/>, and caps the total at <see cref="MaxAnchorsPerDoc"/>.
    /// Returns an empty array (NOT null) when nothing parses cleanly; callers
    /// typically convert empty → null at the cache layer to avoid persisting empties.
    /// </summary>
    public static string[] ParseAnchors(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var anchors = new List<string>(MaxAnchorsPerDoc);

        foreach (string rawLine in raw.Split('\n', StringSplitOptions.None))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            // Strip leading bullets / numbering. Repeat in case the model emits
            // both ("- 1. foo") which we've seen on lower-tier models.
            string stripped = line;
            while (true)
            {
                string after = ListMarkerPrefix.Replace(stripped, string.Empty, count: 1);
                if (ReferenceEquals(after, stripped) || after == stripped) break;
                stripped = after;
            }
            stripped = stripped.Trim().Trim('"', '\'');
            if (stripped.Length < MinAnchorChars) continue;
            if (stripped.Length > MaxAnchorChars) stripped = stripped[..MaxAnchorChars].TrimEnd();
            if (!seen.Add(stripped)) continue;
            anchors.Add(stripped);
            if (anchors.Count >= MaxAnchorsPerDoc) break;
        }
        return anchors.ToArray();
    }

    // ---------- Rewrite ----------

    public const string RewriteSystem =
        "You rewrite a user's natural-language question into a more SEARCH-FRIENDLY form. " +
        "Add synonyms and category-level terms the user didn't explicitly type. Keep it ONE LINE, " +
        "plain text, no quotes, no commentary, no leading prefix. Preserve the user's intent exactly — " +
        "do not invent specifics that aren't in the question.";

    public static string RewriteUser(string query) =>
        "Question:\n\n" + Truncate(query, MaxInputChars);

    /// <summary>
    /// Parses a rewritten query from raw LLM output. Strips quotes and code fences,
    /// trims whitespace, collapses to a single line. Returns null when the result is
    /// empty after cleaning.
    /// </summary>
    public static string? ParseRewrite(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        // Strip code fences if the model wrapped output.
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            int nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];
            int end = s.LastIndexOf("```", StringComparison.Ordinal);
            if (end >= 0) s = s[..end];
        }
        // Collapse to first non-empty line.
        foreach (string rawLine in s.Split('\n'))
        {
            string line = rawLine.Trim().Trim('"', '\'');
            if (line.Length == 0) continue;
            return line;
        }
        return null;
    }

    // ---------- HyDE ----------

    public const string HydeSystem =
        "You write a HYPOTHETICAL ANSWER to a user's question — what a memory that answered this " +
        "question might say. 1 to 2 sentences, plain text, no preamble like \"The answer is...\". " +
        "Be specific and direct; this synthetic answer will be embedded and used to find real memories " +
        "with similar semantic content.";

    public static string HydeUser(string query) =>
        "Question:\n\n" + Truncate(query, MaxInputChars);

    /// <summary>
    /// Parses a HyDE hypothetical from raw LLM output. Strips code fences and
    /// surrounding quotes, collapses whitespace. Returns null when empty.
    /// </summary>
    public static string? ParseHyde(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            int nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];
            int end = s.LastIndexOf("```", StringComparison.Ordinal);
            if (end >= 0) s = s[..end];
        }
        s = s.Trim().Trim('"', '\'').Trim();
        return s.Length == 0 ? null : s;
    }

    // ---------- Typed keywords (entity / topic) ----------

    /// <summary>Maximum typed keywords retained per doc. Caps TF-dict bloat the same
    /// way <see cref="MaxAnchorsPerDoc"/> does for anchors.</summary>
    public const int MaxTypedKeywordsPerDoc = 24;

    public const string TypedKeywordsSystem =
        "You are helping index a memory for retrieval. Summarize this content as typed tags for the memory index. " +
        "Example tags include (but aren't limited to):" +
        "`person:` (a named person), `place:` (a location), `org:` (a company or group), " +
        "`thing:` (a named object or work), `topic:` (an activity, theme, or subject), `date:` (a specific date). " +
        "`keyword:` is a catch-all for any other relevant keyword. " +
        "Use short lowercased canonical forms (e.g. `person:maria`, `place:london`, `org:acme`, " +
        "`thing:toyota camry`, `topic:home renovation`, `date:1-1-2026`, `keyword:example`). "+
        "If a tag is implied, provide the tag without any commentary or citation." +
        "Output one tag per line. If possible, provide multiple tags including a subject and topic. You must provide at least one tag." +
        "No commentary, no numbering, no bullets. ";

    /// <summary>Builds the typed-keyword user prompt from doc content (truncated to
    /// <see cref="MaxInputChars"/>). Content only — same anti-data-leakage contract as anchors.</summary>
    public static string TypedKeywordsUser(string content) =>
        "Memory content:\n\n" + Truncate(content, MaxInputChars);

    /// <summary>
    /// Parses typed keywords from raw LLM output. Each kept tag is `prefix:value`, both
    /// lowercased, where prefix is one of <see cref="KnownFields.EntityKeys"/> — the same
    /// entity-key set the production entity graph and <c>FieldExtractor</c> use, so the
    /// bench harness exercises the real typed design rather than its own list. Tolerates
    /// leading bullets/numbering and surrounding quotes (same as <see cref="ParseAnchors"/>);
    /// drops lines without a valid prefix:value, unknown prefixes, and empty values; dedups
    /// case-insensitively; caps at <see cref="MaxTypedKeywordsPerDoc"/>. Returns an empty
    /// array when nothing parses cleanly (callers convert empty → null at the cache layer).
    /// </summary>
    public static string[] ParseTypedKeywords(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tags = new List<string>(MaxTypedKeywordsPerDoc);

        foreach (string rawLine in raw.Split('\n', StringSplitOptions.None))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            // Strip leading bullets / numbering (repeat for "- 1. foo"), then quotes.
            string stripped = line;
            while (true)
            {
                string after = ListMarkerPrefix.Replace(stripped, string.Empty, count: 1);
                if (ReferenceEquals(after, stripped) || after == stripped) break;
                stripped = after;
            }
            stripped = stripped.Trim().Trim('"', '\'').Trim();

            int colon = stripped.IndexOf(':');
            string? tag = null;
            if (colon > 0) {
                if (colon <= 0 || colon >= stripped.Length - 1) continue;   // need prefix:value
                string prefix = stripped[..colon].Trim().ToLowerInvariant();
                if (!KnownFields.EntityKeys.Contains(prefix)) continue;  // unknown type → drop
                string value = stripped[(colon + 1)..].Trim().ToLowerInvariant();
                if (value.Length == 0) continue;
                tag = $"{prefix}:{value}";
            }
            else
            {
                continue;  // no colon → drop                
            }
            
            if (!seen.Add(tag)) continue;
            tags.Add(tag);
            if (tags.Count >= MaxTypedKeywordsPerDoc) break;
        }
        return [.. tags];
    }

    // ---------- helpers ----------

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];
}
