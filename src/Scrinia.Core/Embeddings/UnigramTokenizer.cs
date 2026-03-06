namespace Scrinia.Core.Embeddings;

/// <summary>
/// Minimal Unigram/SentencePiece tokenizer for Model2Vec models distilled from
/// sentence-transformers. Loads vocab.txt (one token per line, index = ID).
/// Tokens use ▁ (U+2581) as word-start marker. Greedy longest-match segmentation.
/// </summary>
public sealed class UnigramTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly int _unkId;
    private readonly int _padId;
    private readonly int _maxTokenLen;
    private const char WordStart = '\u2581'; // ▁

    private UnigramTokenizer(Dictionary<string, int> vocab, int maxTokenLen)
    {
        _vocab = vocab;
        _padId = vocab.GetValueOrDefault("[PAD]", 0);
        _unkId = vocab.GetValueOrDefault("[UNK]", 1);
        _maxTokenLen = maxTokenLen;
    }

    /// <summary>Number of tokens in the vocabulary.</summary>
    public int VocabSize => _vocab.Count;

    /// <summary>Loads the tokenizer from a vocab.txt file (one token per line).</summary>
    public static UnigramTokenizer FromVocabFile(string vocabPath)
    {
        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        int id = 0;
        int maxLen = 0;
        foreach (string line in File.ReadLines(vocabPath, System.Text.Encoding.UTF8))
        {
            string token = line.TrimEnd();
            vocab[token] = id++;
            if (token.Length > maxLen) maxLen = token.Length;
        }
        return new UnigramTokenizer(vocab, maxLen);
    }

    /// <summary>
    /// Tokenizes text into raw token IDs without padding.
    /// Filters out [UNK] and [PAD] tokens. Used by Model2Vec for vocabulary lookup.
    /// </summary>
    public IReadOnlyList<int> TokenizeRaw(string text)
    {
        var result = new List<int>();
        string lower = text.ToLowerInvariant();

        var words = SplitWords(lower);

        foreach (var word in words)
        {
            SegmentWord(word.IsWordStart, word.Text, result);
        }

        return result;
    }

    private readonly record struct WordSpan(string Text, bool IsWordStart);

    private static List<WordSpan> SplitWords(string text)
    {
        var words = new List<WordSpan>();
        var current = new System.Text.StringBuilder();

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    words.Add(new WordSpan(current.ToString(), true));
                    current.Clear();
                }
            }
            else if (char.IsPunctuation(c) || char.IsSymbol(c))
            {
                if (current.Length > 0)
                {
                    words.Add(new WordSpan(current.ToString(), true));
                    current.Clear();
                }
                words.Add(new WordSpan(c.ToString(), words.Count == 0));
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            words.Add(new WordSpan(current.ToString(), true));

        return words;
    }

    private void SegmentWord(bool isWordStart, string word, List<int> result)
    {
        // Try whole-word match first (with ▁ prefix if word-start)
        if (isWordStart)
        {
            string prefixed = $"{WordStart}{word}";
            if (_vocab.TryGetValue(prefixed, out int wholeId))
            {
                result.Add(wholeId);
                return;
            }
        }
        else
        {
            if (_vocab.TryGetValue(word, out int wholeId))
            {
                result.Add(wholeId);
                return;
            }
        }

        // Greedy longest-match segmentation
        int pos = 0;
        while (pos < word.Length)
        {
            int bestLen = 0;
            int bestId = -1;

            int maxEnd = Math.Min(word.Length, pos + _maxTokenLen);
            for (int end = maxEnd; end > pos; end--)
            {
                string piece;
                if (pos == 0 && isWordStart)
                    piece = $"{WordStart}{word[pos..end]}";
                else
                    piece = word[pos..end];

                if (_vocab.TryGetValue(piece, out int id))
                {
                    bestLen = end - pos;
                    bestId = id;
                    break; // longest match found
                }
            }

            if (bestLen == 0)
            {
                // Single character fallback — try with ▁ prefix if at word start
                string ch = pos == 0 && isWordStart
                    ? $"{WordStart}{word[pos]}"
                    : word[pos].ToString();

                if (_vocab.TryGetValue(ch, out int chId))
                    result.Add(chId);
                // else skip unknown character (don't add UNK)
                pos++;
            }
            else
            {
                result.Add(bestId);
                pos += bestLen;
            }
        }
    }
}
