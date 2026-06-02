using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>
/// Tokenization, term frequency computation, and keyword extraction for BM25 search.
/// </summary>
public static class TextAnalysis
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "above", "after", "again", "against", "all", "am", "an", "and",
        "any", "are", "aren't", "as", "at", "be", "because", "been", "before", "being",
        "below", "between", "both", "but", "by", "can", "can't", "cannot", "could",
        "couldn't", "did", "didn't", "do", "does", "doesn't", "doing", "don't", "down",
        "during", "each", "few", "for", "from", "further", "get", "got", "had", "hadn't",
        "has", "hasn't", "have", "haven't", "having", "he", "her", "here", "hers",
        "herself", "him", "himself", "his", "how", "i", "if", "in", "into", "is",
        "isn't", "it", "its", "itself", "just", "let", "me", "might", "more", "most",
        "mustn't", "my", "myself", "no", "nor", "not", "of", "off", "on", "once",
        "only", "or", "other", "ought", "our", "ours", "ourselves", "out", "over",
        "own", "same", "she", "should", "shouldn't", "so", "some", "such", "than",
        "that", "the", "their", "theirs", "them", "themselves", "then", "there",
        "these", "they", "this", "those", "through", "to", "too", "under", "until",
        "up", "very", "was", "wasn't", "we", "were", "weren't", "what", "when",
        "where", "which", "while", "who", "whom", "why", "will", "with", "won't",
        "would", "wouldn't", "you", "your", "yours", "yourself", "yourselves",
        "also", "already", "always", "another", "around", "away", "back", "become",
        "becomes", "began", "begin", "behind", "best", "better", "big", "come",
        "comes", "could've", "day", "end", "even", "every", "find", "first",
        "found", "gave", "give", "go", "goes", "going", "gone", "good", "great",
        "help", "however", "just", "keep", "know", "known", "last", "like", "long",
        "look", "made", "make", "many", "may", "much", "must", "need", "new", "next",
        "now", "old", "one", "part", "put", "right", "said", "say", "see", "set",
        "shall", "since", "still", "take", "tell", "thing", "think", "time", "try",
        "turn", "two", "use", "used", "using", "want", "way", "well", "went", "work",
        "would've"
    };

    /// <summary>
    /// Splits text on non-alphanumeric characters, lowercases, filters stop words and tokens shorter than 2 chars.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var tokens = new List<string>();
        int start = -1;

        for (int i = 0; i <= text.Length; i++)
        {
            bool isAlphaNum = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (isAlphaNum)
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                string token = text[start..i].ToLowerInvariant();
                if (token.Length >= 2 && !StopWords.Contains(token))
                    tokens.Add(token);
                start = -1;
            }
        }

        return tokens;
    }

    /// <summary>
    /// Tokenizes text and counts occurrences of each term.
    /// </summary>
    public static Dictionary<string, int> ComputeTermFrequencies(string text)
    {
        var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in Tokenize(text))
        {
            tf.TryGetValue(token, out int count);
            tf[token] = count + 1;
        }
        return tf;
    }

    /// <summary>
    /// Extracts the top N terms by frequency from the given text.
    /// </summary>
    public static string[] ExtractKeywords(string text, int topN = 25)
    {
        var tf = ComputeTermFrequencies(text);
        return tf
            .OrderByDescending(kvp => kvp.Value)
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Take(topN)
            .Select(kvp => kvp.Key)
            .ToArray();
    }

    /// <summary>
    /// Merges agent-provided keywords (intentional, placed first) with auto-extracted keywords.
    /// Deduplicates case-insensitively and caps at maxTotal.
    /// </summary>
    public static string[] MergeKeywords(string[]? agentKeywords, string[] autoKeywords, int maxTotal = 30)
    {
        var (keywords, _) = MergeKeywordsWithSource(agentKeywords, autoKeywords, maxTotal);
        return keywords;
    }

    /// <summary>
    /// Merges agent-provided keywords with auto-extracted keywords, tracking which came from the agent.
    /// Agent keywords get higher TF boost (+5) than auto-extracted (+2) for better search differentiation.
    /// </summary>
    public static (string[] Keywords, HashSet<string> AgentKeywords) MergeKeywordsWithSource(
        string[]? agentKeywords, string[] autoKeywords, int maxTotal = 30)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var agentSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>(maxTotal);

        if (agentKeywords is not null)
        {
            foreach (string kw in agentKeywords)
            {
                string trimmed = kw.Trim().ToLowerInvariant();
                if (trimmed.Length > 0 && seen.Add(trimmed))
                {
                    merged.Add(trimmed);
                    agentSet.Add(trimmed);
                    if (merged.Count >= maxTotal) return ([.. merged], agentSet);
                }
            }
        }

        foreach (string kw in autoKeywords)
        {
            string trimmed = kw.Trim().ToLowerInvariant();
            if (trimmed.Length > 0 && seen.Add(trimmed))
            {
                merged.Add(trimmed);
                if (merged.Count >= maxTotal) return ([.. merged], agentSet);
            }
        }

        return ([.. merged], agentSet);
    }

    /// <summary>
    /// Expands a keyword into the tokens that should be seeded into the BM25 term-frequency
    /// index. A plain keyword tokenizes normally; a typed keyword of the form
    /// <c>prefix:value</c> (e.g. <c>file:src/Foo.cs</c>, <c>person:Mary</c>, <c>ref:/findings/x</c>)
    /// indexes the tokenized <i>value</i> only — the type prefix is structural metadata that
    /// stays on the keyword itself (for field-score / facet / cross-link use), not a content term.
    ///
    /// <para>Typed keywords must not be seeded into TF as-is: the whole <c>prefix:value</c>
    /// string is a single dictionary key that no tokenized query term can ever equal, so it
    /// contributes nothing to BM25 (dead weight). Seeding the value tokens makes typed-keyword
    /// values first-class search terms — a "person:Mary" keyword becomes matchable by a "Mary"
    /// query even when "Mary" never appears in the content.</para>
    /// </summary>
    public static IReadOnlyList<string> IndexTokensForKeyword(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return [];

        int colon = keyword.IndexOf(':');
        if (colon > 0 && colon < keyword.Length - 1)
        {
            string prefix = keyword[..colon];
            // A type prefix is a short, all-letter tag (file, ref, person, date, related, ...).
            // Anything else carrying a colon (URLs, times, ratios) tokenizes whole.
            if (prefix.Length <= 16 && prefix.All(char.IsLetter))
                return Tokenize(keyword[(colon + 1)..]);
        }
        return Tokenize(keyword);
    }

    /// <summary>
    /// Default cap on the term-frequency dictionary written to sidecar metadata.
    /// The long tail of TF=1 terms is mostly named entities and one-off mentions that bloat
    /// sidecar size 2–4x while contributing near-zero IDF weight to BM25 scoring. Keywords
    /// (top-25) are a subset of the cap, and any ref:/file: prefixed keywords are re-added
    /// to TF in the Store path via the keyword-boost step, so reference-driven recall is
    /// preserved even when the rare-term tail is pruned.
    /// </summary>
    public const int DefaultMaxTfTerms = 100;

    /// <summary>
    /// Extracts keywords and computes term frequencies in a single tokenization pass.
    /// Avoids the double tokenization of calling <see cref="ExtractKeywords"/> + <see cref="ComputeTermFrequencies"/> separately.
    /// </summary>
    public static (string[] Keywords, Dictionary<string, int> TermFrequencies) AnalyzeText(
        string text, int topN = 25, int maxTfTerms = DefaultMaxTfTerms)
    {
        var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in Tokenize(text))
        {
            tf.TryGetValue(token, out int count);
            tf[token] = count + 1;
        }

        // Sort once — used both for keyword selection and (when capping) for TF pruning.
        var sorted = tf
            .OrderByDescending(kvp => kvp.Value)
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var keywords = sorted
            .Take(topN)
            .Select(kvp => kvp.Key)
            .ToArray();

        if (sorted.Count > maxTfTerms)
        {
            tf = new Dictionary<string, int>(maxTfTerms, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < maxTfTerms; i++)
                tf[sorted[i].Key] = sorted[i].Value;
        }

        return (keywords, tf);
    }

    /// <summary>
    /// Computes per-chunk keywords, term frequencies, and content previews for multi-chunk artifacts.
    /// Shared by both MCP and REST store paths.
    /// </summary>
    public static ChunkEntry[] ComputeChunkEntries(IMemoryStore store, string[] chunks)
    {
        var entries = new ChunkEntry[chunks.Length];
        for (int i = 0; i < chunks.Length; i++)
        {
            var (kw, tf) = AnalyzeText(chunks[i]);
            foreach (string k in kw) { tf.TryGetValue(k, out int c); tf[k] = c + 2; }
            string preview = store.GenerateContentPreview(chunks[i]);
            entries[i] = new ChunkEntry(
                ChunkIndex: i + 1,
                ContentPreview: string.IsNullOrEmpty(preview) ? null : preview,
                Keywords: kw.Length > 0 ? kw : null,
                TermFrequencies: tf.Count > 0 ? tf : null);
        }
        return entries;
    }
}
