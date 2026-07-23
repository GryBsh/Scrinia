using System.Text.RegularExpressions;

namespace Scrinia.Core.Search;

/// <summary>
/// Populates the attribute map from its two mechanical sources: content extraction
/// (frontmatter, line-start <c>key: value</c>, <c>**Bold:** label</c> lines) and agent-keyword
/// routing (<c>key:value</c>-shaped entries in the keywords param). LLM-free, regex-only,
/// run at write time. Values verbatim — exact-match signal for what BM25 tokenization shreds
/// (versions, dates, hyphenated identifiers, statuses). Conservative: nothing inside code
/// fences, URL-remainder values skipped, tight key charset, value + attribute caps.
/// Merge semantics live in <see cref="KnownAttributes"/>, not here.
/// </summary>
public static partial class AttributeExtractor
{
    public const int MaxValueLength = 120;

    // Line-start key: value. Tight key charset so prose colons don't match.
    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9_-]{0,29}):[ \t]+(\S.*)$")]
    private static partial Regex LineStartPairPattern();

    // Bold labels: **Why:** value  or  **Why**: value (keys may contain internal spaces).
    [GeneratedRegex(@"^\*\*([A-Za-z][A-Za-z0-9 _-]{0,29})(?::\*\*|\*\*:)[ \t]*(\S.*)$")]
    private static partial Regex BoldLabelPattern();

    // Keyword routing: key charset before first colon, non-empty remainder.
    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9_-]{0,29}):(.+)$")]
    private static partial Regex KeywordAttributePattern();

    /// <summary>
    /// Extracts attributes from content. Multi-valued keys (per <see cref="KnownAttributes"/>)
    /// accumulate; scalar keys are first-wins within one text. Null when none found.
    /// </summary>
    public static Dictionary<string, string[]>? Extract(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        Dictionary<string, List<string>>? found = null;
        bool inFence = false, firstLine = true, inFrontmatter = false;

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                firstLine = false;
                continue;
            }
            if (inFence) { firstLine = false; continue; }

            if (line.Trim() == "---")
            {
                if (firstLine) inFrontmatter = true;
                else if (inFrontmatter) inFrontmatter = false;
                firstLine = false;
                continue;
            }
            firstLine = false;

            var (key, value) = MatchAttributeLine(line);
            if (key is null) continue;

            found ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            AddAttribute(found, key, value!);
        }

        return ToAttributeDict(found);
    }

    /// <summary>
    /// Splits agent-supplied keywords into attribute-shaped entries (routed to the attribute map) and plain
    /// keywords. Routing rule: key charset before the first colon, non-empty remainder that
    /// doesn't start with <c>//</c> (URL guard) and fits the value cap. Either side null when empty.
    /// </summary>
    public static (Dictionary<string, string[]>? Attributes, string[]? Keywords) RouteKeywords(string[]? keywords)
    {
        if (keywords is not { Length: > 0 })
            return (null, null);

        Dictionary<string, List<string>>? found = null;
        List<string>? plain = null;

        foreach (string kw in keywords)
        {
            if (string.IsNullOrWhiteSpace(kw)) continue;
            Match m = KeywordAttributePattern().Match(kw.Trim());
            string? value = m.Success ? m.Groups[2].Value.Trim() : null;
            if (value is { Length: > 0 and <= MaxValueLength }
                && !value.StartsWith("//", StringComparison.Ordinal))
            {
                found ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                AddAttribute(found, m.Groups[1].Value.ToLowerInvariant(), value);
            }
            else
            {
                (plain ??= []).Add(kw.Trim());
            }
        }

        return (ToAttributeDict(found), plain is { Count: > 0 } ? [.. plain] : null);
    }

    /// <summary>
    /// Seeds attribute keys and value tokens into a term-frequency dictionary at +3 per token —
    /// between the auto-keyword (+2) and agent-keyword (+5) boosts — so BM25 picks up attribute
    /// vocabulary transparently.
    /// </summary>
    public static void SeedTermFrequencies(
        Dictionary<string, string[]>? attributes, Dictionary<string, int> tf)
    {
        if (attributes is not { Count: > 0 })
            return;

        foreach (var kv in attributes)
        {
            Seed(kv.Key, tf);
            foreach (string value in kv.Value)
                Seed(value, tf);
        }

        static void Seed(string text, Dictionary<string, int> tf)
        {
            foreach (string token in TextAnalysis.Tokenize(text))
            {
                tf.TryGetValue(token, out int count);
                tf[token] = count + 3;
            }
        }
    }

    /// <summary>File and memory references as unified attributes — {file: [...], ref: [...]}.
    /// Replaces the retired file:/ref: prefixed-keyword emission.</summary>
    public static Dictionary<string, string[]>? ExtractRefAttributes(string text)
    {
        var fileRefs = ReferenceExtractor.ExtractFileRefs(text);
        var memoryRefs = ReferenceExtractor.ExtractMemoryRefs(text);
        if (fileRefs.Length == 0 && memoryRefs.Length == 0) return null;
        var attributes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (fileRefs.Length > 0) attributes["file"] = fileRefs;
        if (memoryRefs.Length > 0) attributes["ref"] = memoryRefs;
        return attributes;
    }

    /// <summary>Multi-valued keys accumulate (deduped); scalar keys are first-wins.</summary>
    private static void AddAttribute(Dictionary<string, List<string>> found, string key, string value)
    {
        if (found.TryGetValue(key, out var values))
        {
            if (KnownAttributes.IsMultiValued(key)
                && !values.Contains(value, StringComparer.OrdinalIgnoreCase))
                values.Add(value);
            // scalar + existing → first-wins: ignore
        }
        else if (found.Count < KnownAttributes.MaxAttributes)
        {
            found[key] = [value];
        }
    }

    private static Dictionary<string, string[]>? ToAttributeDict(Dictionary<string, List<string>>? found) =>
        found is { Count: > 0 }
            ? found.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase)
            : null;

    private static (string? Key, string? Value) MatchAttributeLine(string line)
    {
        Match m = BoldLabelPattern().Match(line);
        if (!m.Success)
            m = LineStartPairPattern().Match(line);
        if (!m.Success)
            return (null, null);

        string value = m.Groups[2].Value.Trim();
        if (value.Length == 0 || value.Length > MaxValueLength)
            return (null, null);
        if (value.StartsWith("//", StringComparison.Ordinal)) // URL remainder guard
            return (null, null);

        return (m.Groups[1].Value.Trim().ToLowerInvariant(), value);
    }
}
