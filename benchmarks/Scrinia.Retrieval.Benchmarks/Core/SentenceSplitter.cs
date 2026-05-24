using System.Text.RegularExpressions;

namespace Scrinia.Retrieval.Benchmarks.Core;

/// <summary>
/// Splits doc text into sentence-sized units for sentence-level reranking.
/// Sentence-MaxSim doesn't need linguistically perfect boundaries — it needs
/// chunks small enough that their embedding represents a single thought, big
/// enough to embed meaningfully. ~50–300 chars hits that sweet spot for
/// model2vec's static distillation.
///
/// <para>Strategy: split on <c>[.!?]\s+</c>, then merge undersize runs forward
/// and hard-split oversize runs at word boundaries. Final list is bounded by
/// <see cref="MaxSentencesPerDoc"/> so a 5000-line conversation doesn't blow
/// up the vector store; for over-cap docs we keep the first N (front-of-doc
/// is typically where context lives in chat transcripts).</para>
/// </summary>
public static partial class SentenceSplitter
{
    /// <summary>Below this length a "sentence" carries too little signal to embed
    /// reliably (greetings, "OK.", "Thanks!") — we merge them into the next run.</summary>
    public const int MinSentenceChars = 20;

    /// <summary>Above this length a "sentence" is probably a run-on or a paragraph
    /// the regex didn't catch — we hard-split at word boundaries to avoid embedding
    /// 1000-char paragraphs that would average out to noise.</summary>
    public const int MaxSentenceChars = 300;

    /// <summary>Hard cap per doc. 50 × ~150 chars ≈ 7500 chars of coverage — generous
    /// for a chat session, tight enough to keep memory and rerank cost bounded.</summary>
    public const int MaxSentencesPerDoc = 50;

    // Sentence-end punctuation followed by whitespace. Won't split on decimals
    // ("3.14"), abbreviations ("e.g.", "Mr."), or URLs because those don't have
    // the trailing whitespace+capital pattern — false-positive rate is acceptable
    // for embedding-based reranking (a slightly-wrong split still embeds usefully).
    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z""'\(])", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBoundaryRegex();
    private static readonly Regex SentenceBoundary = SentenceBoundaryRegex();

    public static IReadOnlyList<string> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var raw = SentenceBoundary.Split(text);
        var result = new List<string>(raw.Length);
        string? pending = null;

        foreach (string s in raw)
        {
            string t = s.Trim();
            if (t.Length == 0) continue;

            // Merge undersize into pending (carry forward) — protects against
            // "OK." / "Sure!" / "Yeah." that would otherwise dilute the index.
            if (pending is not null)
            {
                t = pending + " " + t;
                pending = null;
            }

            if (t.Length < MinSentenceChars)
            {
                pending = t;
                continue;
            }

            if (t.Length <= MaxSentenceChars)
            {
                result.Add(t);
                if (result.Count >= MaxSentencesPerDoc) return result;
                continue;
            }

            // Oversize: hard-split at word boundaries inside the run so each piece
            // stays embeddable. Don't merge these — they're already long enough.
            foreach (string piece in HardSplit(t, MaxSentenceChars))
            {
                if (piece.Length >= MinSentenceChars)
                {
                    result.Add(piece);
                    if (result.Count >= MaxSentencesPerDoc) return result;
                }
            }
        }

        // Flush any pending undersize tail by appending to the last sentence.
        if (pending is not null)
        {
            if (result.Count > 0) result[^1] = result[^1] + " " + pending;
            else if (pending.Length >= MinSentenceChars / 2) result.Add(pending);
        }

        return result;
    }

    private static IEnumerable<string> HardSplit(string text, int maxLen)
    {
        int i = 0;
        while (i < text.Length)
        {
            int end = Math.Min(i + maxLen, text.Length);
            if (end < text.Length)
            {
                // Walk back to nearest whitespace so we don't slice mid-word.
                int ws = text.LastIndexOf(' ', end - 1, end - i);
                if (ws > i + maxLen / 2) end = ws;
            }
            yield return text[i..end].Trim();
            i = end;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        }
    }
}
