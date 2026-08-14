using System.Text.RegularExpressions;

namespace Scrinia.Core.Search;

/// <summary>
/// Populates the attribute map from its three mechanical sources: content extraction
/// (frontmatter, line-start <c>key: value</c>, <c>**Bold:** label</c> lines, and inline prose
/// <c>key:value</c> bounded to <see cref="KnownAttributes.RecommendedKeys"/>) and agent-keyword
/// routing (<c>key:value</c>-shaped entries in the keywords param, unbounded — every element there
/// was typed on purpose). LLM-free, regex-only, run at write time. Values verbatim — exact-match
/// signal for what BM25 tokenization shreds (versions, dates, hyphenated identifiers, statuses).
/// Conservative: nothing inside code fences (see <see cref="ProseScanner"/>), URL-remainder values
/// skipped, tight key charset, value + attribute caps. Merge semantics live in
/// <see cref="KnownAttributes"/>, not here.
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

    // Inline prose key:value — anywhere in a line, no leading whitespace required, value is a
    // single non-whitespace token (unlike KeywordAttributePattern's "rest of the string", prose
    // has trailing words after the value that must not be swallowed). Same key charset as
    // KeywordAttributePattern; unlike keyword routing, every match here is additionally gated on
    // KnownAttributes.RecommendedKeys at the call site — prose is uncontrolled text (pasted logs,
    // quoted code, transcripts), so only the eight keys the guide teaches are allowed to mint an
    // attribute from it.
    //
    // The lookbehind excludes '*' as well as word/hyphen chars — without that, this pattern matches
    // "File:" *inside* "**File:** src/x.cs" (the char immediately before 'F' is '*', which used to
    // be permitted), and the value capture then grabs the closing "**" as if it were the value,
    // polluting a multi-valued key (file/ref/person/place/org/topic) with a literal "**" on every
    // bold-labelled line that happens to use a recommended key. ('_' needs no separate mention here:
    // it's already a \w character, so \w already excludes it — writing it a second time would be a
    // no-op, not a second protection.)
    [GeneratedRegex(@"(?<![\w*-])([A-Za-z][A-Za-z0-9_-]{0,29}):(\S+)")]
    private static partial Regex InlineProseKeyValuePattern();

    /// <summary>
    /// Extracts attributes from content. Multi-valued keys (per <see cref="KnownAttributes"/>)
    /// accumulate; scalar keys are first-wins within one text. Null when none found.
    /// </summary>
    public static Dictionary<string, string[]>? Extract(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        Dictionary<string, List<string>>? found = null;

        foreach (string line in ProseScanner.ProseLines(content))
        {
            var (key, value) = MatchAttributeLine(line);
            if (key is not null)
            {
                found ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                AddAttribute(found, key, value!);
            }

            foreach (var (inlineKey, inlineValue) in MatchInlineProsePairs(line))
            {
                found ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                AddAttribute(found, inlineKey, inlineValue);
            }
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
    /// <remarks>Values are tokenized as prose, which means a round-tripped timestamp in a `review`
    /// attribute seeds junk terms (`00`, `0000000`, `2026`) at +3 each. This is accepted: it is
    /// ranking noise, not data loss, it affects the local and remote paths identically, and the
    /// alternative — type-aware seeding that skips date-shaped values — buys a small precision
    /// gain for a per-key type model this scorer does not otherwise need.</remarks>
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

    public const int MaxMentionSpans = 32;

    // Capitalized words that are almost never a standalone prose entity: pronouns, articles,
    // weekday/month names, and common sentence-openers (imperative verbs, conjunctions, auxiliaries,
    // contractions). Matched case-sensitively against the raw (capitalized) token, so it only ever
    // affects a capitalized occurrence — a lowercase "set"/"in" mid-sentence already breaks a run.
    // This is the whole precision guard: it filters sentence-leading common words while keeping
    // sentence-leading SUBJECTS (real names/places). Tunable; extend as real usage reveals leaks.
    private static readonly HashSet<string> MentionStopWords =
        new(StringComparer.Ordinal)
        {
            "I", "A", "An", "The", "This", "That", "These", "Those", "It", "Its",
            "He", "His", "Him", "She", "Her", "They", "Their", "Them", "We", "Our", "Us", "You", "Your",
            "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
            "January", "February", "March", "April", "May", "June", "July",
            "August", "September", "October", "November", "December",
            // common sentence-openers
            "Set", "Get", "Run", "Use", "Add", "See", "Note", "Let", "Try", "Make", "Take",
            "Check", "Ensure", "Also", "Then", "When", "While", "If", "Else", "Because",
            "After", "Before", "Now", "Here", "There", "First", "Next", "Finally", "However",
            "Do", "Does", "Did", "Is", "Are", "Was", "Were", "Be", "Been", "Being",
            "Have", "Has", "Had", "Will", "Would", "Should", "Could", "Can", "May", "Might", "Must",
            "Not", "No", "Yes", "So", "But", "And", "Or", "For", "Of", "To", "Nor",
            "In", "On", "At", "By", "With", "From", "As", "Per", "Every", "Each", "Some", "Any", "All",
            "Don't", "Doesn't", "Didn't", "Can't", "Won't", "Isn't", "Aren't", "Wasn't", "Weren't",
            "It's", "That's", "There's", "Let's", "We're", "We've", "You're", "They're", "I'm", "I've",
        };

    /// <summary>
    /// Mechanical prose entity-span extraction (Piece 2, Stage 1). Emits capitalized-noun-phrase
    /// runs as <c>mention:&lt;value&gt;</c> presence facts. LLM-free; embedder-free. A run is a
    /// maximal sequence of consecutive capitalized, non-stop-word tokens taken outside code fences
    /// (see <see cref="ProseScanner"/>) — inline-code spans and YAML frontmatter are prose here,
    /// same as in <see cref="Extract"/>, so a backtick breaks a mention run the way any other
    /// non-word character does; the value is the run joined and lowercased, kept whole. No
    /// positional/echo logic. Returns null when no span survives.
    /// </summary>
    public static Dictionary<string, string[]>? ExtractMentionSpans(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string line in ProseScanner.ProseLines(content))
        {
            if (!ScanMentionLine(line, values, seen))
                break; // per-memory cap reached
        }

        return values.Count > 0
            ? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [KnownAttributes.MentionKey] = [.. values] }
            : null;
    }

    /// <summary>Removes any <see cref="KnownAttributes.MentionKey"/> value that also appears under
    /// one of <see cref="KnownAttributes.EntityKeys"/> in the same entry — the typed key
    /// (agent-supplied or otherwise) is authoritative, and a duplicate untyped mention would
    /// double-count the entity in aggregation (entity graph, top-attributes facets). Shared by
    /// both write paths (MCP <c>MemoryTools.Core.cs</c>, server <c>MemoryOrchestrator</c>) so the
    /// dedup logic lives once. Returns <paramref name="attrs"/> unchanged (same reference) when
    /// there is nothing to shadow-drop.</summary>
    public static Dictionary<string, string[]>? DropMentionsShadowedByTypedKeys(Dictionary<string, string[]>? attrs)
    {
        if (attrs is null || !attrs.TryGetValue(KnownAttributes.MentionKey, out var mentions) || mentions.Length == 0)
            return attrs;

        var typedValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in KnownAttributes.EntityKeys)
            if (attrs.TryGetValue(key, out var vals))
                foreach (var v in vals) typedValues.Add(v);

        var kept = mentions.Where(m => !typedValues.Contains(m)).ToArray();
        if (kept.Length == mentions.Length) return attrs;

        if (kept.Length == 0) attrs.Remove(KnownAttributes.MentionKey);
        else attrs[KnownAttributes.MentionKey] = kept;
        return attrs;
    }

    /// <summary>Appends normalized mention values from one line into <paramref name="values"/>.
    /// Returns false when the per-memory cap is reached (caller stops scanning).</summary>
    private static bool ScanMentionLine(string line, List<string> values, HashSet<string> seen)
    {
        var run = new List<string>();

        bool Emit()
        {
            if (run.Count == 0) return true;
            string norm = string.Join(' ', run).Trim().ToLowerInvariant();
            run.Clear();
            if (norm.Length > 0 && seen.Add(norm))
            {
                values.Add(norm);
                if (values.Count >= MaxMentionSpans) return false;
            }
            return true;
        }

        int i = 0, n = line.Length;
        while (i < n)
        {
            char c = line[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (IsWordChar(c))
            {
                int start = i;
                while (i < n && IsWordChar(line[i])) i++;
                string token = line[start..i];
                if (char.IsUpper(token[0]) && !MentionStopWords.Contains(token))
                    run.Add(token);
                else if (!Emit())
                    return false;
                continue;
            }
            if (!Emit()) return false;
            i++;
        }
        return Emit();

        static bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '\'' || ch == '-';
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

    /// <summary>Finds inline prose <c>key:value</c> pairs anywhere in <paramref name="line"/>,
    /// gated to <see cref="KnownAttributes.RecommendedKeys"/> — see
    /// <see cref="InlineProseKeyValuePattern"/> for why the gate lives here and not in the regex.
    /// Applies the same value guards as <see cref="MatchAttributeLine"/> (length cap, URL-remainder
    /// skip).</summary>
    private static IEnumerable<(string Key, string Value)> MatchInlineProsePairs(string line)
    {
        foreach (Match m in InlineProseKeyValuePattern().Matches(line))
        {
            string key = m.Groups[1].Value.ToLowerInvariant();
            if (!KnownAttributes.RecommendedKeys.Contains(key)) continue;

            // (\S+) has no sense of where prose ends, so it swallows trailing sentence/clause
            // punctuation the value was never part of ("file:src/thing.cs." -> "src/thing.cs.",
            // "(file:src/x.cs)" -> "src/x.cs)"). That corrupt suffix defeats a later file-existence
            // check exactly like D-a did. TrimTrailingUnbalancedPunctuation only strips a closing
            // delimiter when it has no matching opener left in the value, so a deliberately-typed
            // "thing:Foo(bar)" survives intact.
            string value = StripReferenceWrappers(TrimTrailingUnbalancedPunctuation(m.Groups[2].Value));
            if (value.Length > MaxValueLength) continue;
            if (value.StartsWith("//", StringComparison.Ordinal)) continue; // URL remainder guard

            // THE VALUE MUST BE SOMETHING. A length check alone is not that: "file:-" and
            // "(ref:**)" are length 1 and 2, so they passed it and minted `file=["-"]` /
            // `ref=["**"]`. `file` and `ref` are union-merged ReferenceKeys and
            // EntityGraph.ParseEntityAttributes yields every key, so a value carrying no name at
            // all becomes a graph node SHARED by every memory whose prose happens to contain the
            // shape — the hub pathology KnownAttributes.StateMarkerKeys documents for
            // `orphan:true`, arrived at from the other direction. Uncontrolled prose (pasted logs,
            // diffs, ASCII art) is full of these shapes, and this route reads exactly that. One
            // alphanumeric character is the whole bar: it admits "topic:C#" and "file:a.cs" while
            // refusing punctuation runs, which never name anything.
            if (!value.Any(char.IsLetterOrDigit)) continue;

            yield return (key, value);
        }
    }

    /// <summary>
    /// Normalizes a routed value to the SAME spelling the bare-path and wiki-link extractors
    /// produce for the same reference, so one reference under one multi-valued key is one value
    /// rather than two.
    ///
    /// <para>Three strips, in this order. A matched surrounding quote pair
    /// (<c>file:"src/a.cs"</c>) survives <see cref="TrimTrailingUnbalancedPunctuation"/> — its
    /// quote count is EVEN, which that method reads as balanced and deliberate — and the quoted
    /// spelling then fails <c>HashWorkspaceFile</c>'s containment check, so the reference silently
    /// gets no drift baseline. A <c>[[wiki-link]]</c> wrapper survives it for the same reason
    /// (openers match closers) and leaves <c>ref:[[/api/auth]]</c> stored as
    /// <c>"[[/api/auth]]"</c> while <see cref="ExtractRefAttributes"/> stores <c>"/api/auth"</c>
    /// for the identical link in the identical text; the inner target is returned verbatim,
    /// leading <c>/</c> included, because that is exactly what
    /// <c>ReferenceExtractor.ExtractMemoryRefs</c> captures. Otherwise the leading path prefix is
    /// trimmed the way <c>ReferenceExtractor.ExtractFileRefs</c> trims it ('.', '/', '\\'), so
    /// <c>file:../src/a.cs</c> and a bare <c>../src/a.cs</c> both store <c>src/a.cs</c>.</para>
    ///
    /// <para>The prefix trim applies to every recommended key's value, not just file/ref, so a
    /// value like <c>topic:.NET</c> loses its leading dot too; accepted for the same reason the
    /// URL and length guards at the call site are generic rather than per-key.</para>
    /// </summary>
    private static string StripReferenceWrappers(string value)
    {
        if (value.Length >= 2 && value[0] is '"' or '\'' or '`' && value[^1] == value[0])
            value = value[1..^1].Trim();

        if (value.Length > 4
            && value.StartsWith("[[", StringComparison.Ordinal)
            && value.EndsWith("]]", StringComparison.Ordinal))
            return value[2..^2].Trim();

        return value.TrimStart('.', '/', '\\');
    }

    /// <summary>Strips trailing prose punctuation a <c>(\S+)</c> capture swallows by mistake. Plain
    /// clause punctuation (<c>. , ; :</c>) has no "opener" concept and is always stripped. A closing
    /// bracket (<c>) ] }</c>) is stripped only when the value holds more of that closer than its
    /// matching opener — so "Foo(bar)" and "[[/api/auth]]" survive untouched (opens == closes), but
    /// the corrupt "src/x.cs)" from "(file:src/x.cs)" is cleaned (a close with no matching open at
    /// all). A quote/backtick is stripped only when its running count in the value is odd (an
    /// unmatched trailing instance); an even (balanced) count is left alone.</summary>
    private static string TrimTrailingUnbalancedPunctuation(string value)
    {
        while (value.Length > 0)
        {
            char last = value[^1];
            switch (last)
            {
                case '.' or ',' or ';' or ':':
                    value = value[..^1];
                    continue;
                case ')' or ']' or '}':
                    char opener = last switch { ')' => '(', ']' => '[', _ => '{' };
                    if (value.Count(c => c == last) > value.Count(c => c == opener))
                    {
                        value = value[..^1];
                        continue;
                    }
                    return value;
                case '"' or '\'' or '`':
                    if (value.Count(c => c == last) % 2 != 0)
                    {
                        value = value[..^1];
                        continue;
                    }
                    return value;
                default:
                    return value;
            }
        }
        return value;
    }
}
