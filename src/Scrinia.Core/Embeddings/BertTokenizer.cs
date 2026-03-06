namespace Scrinia.Core.Embeddings;

/// <summary>
/// Minimal WordPiece tokenizer for BERT-family models.
/// Loads vocab.txt and performs basic tokenization + WordPiece subword splitting.
/// Trim-safe: no reflection or dynamic codegen needed.
/// </summary>
public sealed class BertTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly int _clsId;
    private readonly int _sepId;
    private readonly int _unkId;
    private const int MaxTokenLength = 200;
    private const int MaxSequenceLength = 512;

    private BertTokenizer(Dictionary<string, int> vocab)
    {
        _vocab = vocab;
        _clsId = vocab.GetValueOrDefault("[CLS]", 101);
        _sepId = vocab.GetValueOrDefault("[SEP]", 102);
        _unkId = vocab.GetValueOrDefault("[UNK]", 100);
    }

    /// <summary>Number of tokens in the vocabulary.</summary>
    public int VocabSize => _vocab.Count;

    /// <summary>Loads the tokenizer from a vocab.txt file (one token per line).</summary>
    public static BertTokenizer FromVocabFile(string vocabPath)
    {
        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        int id = 0;
        foreach (string line in File.ReadLines(vocabPath))
        {
            vocab[line.TrimEnd()] = id++;
        }
        return new BertTokenizer(vocab);
    }

    /// <summary>Tokenizes text into input IDs for BERT, including [CLS] and [SEP].</summary>
    public (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds) Encode(string text, int maxLength = MaxSequenceLength)
    {
        var tokens = Tokenize(text);

        // Truncate to maxLength - 2 (for [CLS] and [SEP])
        if (tokens.Count > maxLength - 2)
            tokens = tokens.Take(maxLength - 2).ToList();

        var inputIds = new long[tokens.Count + 2];
        var attentionMask = new long[tokens.Count + 2];
        var tokenTypeIds = new long[tokens.Count + 2];

        inputIds[0] = _clsId;
        attentionMask[0] = 1;

        for (int i = 0; i < tokens.Count; i++)
        {
            inputIds[i + 1] = tokens[i];
            attentionMask[i + 1] = 1;
        }

        inputIds[tokens.Count + 1] = _sepId;
        attentionMask[tokens.Count + 1] = 1;

        return (inputIds, attentionMask, tokenTypeIds);
    }

    /// <summary>
    /// Tokenizes text into raw token IDs without [CLS]/[SEP] wrapping.
    /// Filters out [UNK] tokens. Used by Model2Vec for vocabulary lookup.
    /// </summary>
    public IReadOnlyList<int> TokenizeRaw(string text)
    {
        var tokens = Tokenize(text);
        tokens.RemoveAll(id => id == _unkId);
        return tokens;
    }

    private List<int> Tokenize(string text)
    {
        var result = new List<int>();
        string lower = text.ToLowerInvariant();

        // Basic tokenization: split on whitespace and punctuation
        var words = BasicTokenize(lower);

        foreach (string word in words)
        {
            var subTokens = WordPieceTokenize(word);
            result.AddRange(subTokens);
        }

        return result;
    }

    private static List<string> BasicTokenize(string text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else if (IsPunctuation(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                tokens.Add(c.ToString());
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }

    private List<int> WordPieceTokenize(string word)
    {
        if (word.Length > MaxTokenLength)
            return [_unkId];

        var tokens = new List<int>();
        int start = 0;

        while (start < word.Length)
        {
            int end = word.Length;
            int? foundId = null;

            while (start < end)
            {
                string substr = start == 0 ? word[start..end] : $"##{word[start..end]}";

                if (_vocab.TryGetValue(substr, out int id))
                {
                    foundId = id;
                    break;
                }
                end--;
            }

            if (foundId is null)
            {
                tokens.Add(_unkId);
                break;
            }

            tokens.Add(foundId.Value);
            start = end;
        }

        return tokens;
    }

    private static bool IsPunctuation(char c) =>
        char.IsPunctuation(c) || char.IsSymbol(c);
}
