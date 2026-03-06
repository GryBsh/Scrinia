using Microsoft.Extensions.Logging;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// Model2Vec embedding provider. Loads a SafeTensors model and tokenizes via the
/// appropriate tokenizer (Unigram for SentencePiece vocabs, WordPiece for BERT vocabs).
/// Pure C# — no native dependencies. Looks up token rows in the embedding matrix,
/// averages, and L2-normalizes.
/// </summary>
public sealed class Model2VecProvider : IEmbeddingProvider
{
    private readonly float[] _matrix;
    private readonly int _dims;
    private readonly int _vocabSize;
    private readonly Func<string, IReadOnlyList<int>> _tokenize;

    public bool IsAvailable => true;
    public int Dimensions => _dims;

    private Model2VecProvider(float[] matrix, int dims, int vocabSize, Func<string, IReadOnlyList<int>> tokenize)
    {
        _matrix = matrix;
        _dims = dims;
        _vocabSize = vocabSize;
        _tokenize = tokenize;
    }

    /// <summary>Loads a Model2Vec model from a directory containing model.safetensors and vocab.txt.</summary>
    public static Model2VecProvider Load(string modelDir, ILogger logger)
    {
        string safeTensorsPath = Path.Combine(modelDir, "model.safetensors");
        string vocabPath = Path.Combine(modelDir, "vocab.txt");

        if (!File.Exists(safeTensorsPath))
            throw new FileNotFoundException("model.safetensors not found.", safeTensorsPath);
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException("vocab.txt not found.", vocabPath);

        // Detect tokenizer type from vocab — SentencePiece vocabs start with [PAD] then use ▁ markers
        Func<string, IReadOnlyList<int>> tokenize;
        bool isSentencePiece = DetectSentencePieceVocab(vocabPath);
        if (isSentencePiece)
        {
            var tokenizer = UnigramTokenizer.FromVocabFile(vocabPath);
            tokenize = tokenizer.TokenizeRaw;
            logger.LogInformation("Using Unigram tokenizer ({VocabSize} tokens)", tokenizer.VocabSize);
        }
        else
        {
            var tokenizer = BertTokenizer.FromVocabFile(vocabPath);
            tokenize = tokenizer.TokenizeRaw;
            logger.LogInformation("Using WordPiece tokenizer ({VocabSize} tokens)", tokenizer.VocabSize);
        }

        // Read the embedding matrix from SafeTensors
        float[] matrix;
        int dims;
        int vocabSize;

        using (var fs = new FileStream(safeTensorsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            long dataStart = SafeTensorsReader.GetDataStart(fs);
            fs.Position = 0;
            var header = SafeTensorsReader.ReadHeader(fs);

            // Find the embedding tensor — typically named "embeddings" or the only tensor
            var tensorName = header.ContainsKey("embeddings") ? "embeddings"
                : header.Keys.FirstOrDefault(k => !k.StartsWith("__"))
                ?? throw new FormatException("No embedding tensor found in SafeTensors file.");

            var meta = header[tensorName];
            matrix = SafeTensorsReader.ReadFloatTensor(fs, dataStart, meta);
            dims = (int)meta.Shape[^1];
            vocabSize = (int)meta.Shape[0];
        }

        logger.LogInformation("Model2Vec loaded: {VocabSize} tokens, {Dims} dimensions", vocabSize, dims);
        return new Model2VecProvider(matrix, dims, vocabSize, tokenize);
    }

    /// <summary>Detects if vocab.txt uses SentencePiece ▁ markers (vs WordPiece ## markers).</summary>
    private static bool DetectSentencePieceVocab(string vocabPath)
    {
        using var reader = new StreamReader(vocabPath, System.Text.Encoding.UTF8);
        for (int i = 0; i < 20 && reader.ReadLine() is { } line; i++)
        {
            if (line.Contains('\u2581')) return true;
            if (line.StartsWith("##", StringComparison.Ordinal)) return false;
        }
        return false;
    }

    public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        var tokens = _tokenize(text);
        if (tokens.Count == 0)
            return Task.FromResult<float[]?>(null);

        var result = new float[_dims];
        int count = 0;

        foreach (int tokenId in tokens)
        {
            if (tokenId < 0 || tokenId >= _vocabSize)
                continue;

            // Accumulate the row from the matrix
            int offset = tokenId * _dims;
            var row = _matrix.AsSpan(offset, _dims);
            for (int i = 0; i < _dims; i++)
                result[i] += row[i];
            count++;
        }

        if (count == 0)
            return Task.FromResult<float[]?>(null);

        // Average + L2 normalize
        float scale = 1.0f / count;
        float normSq = 0;
        for (int i = 0; i < _dims; i++)
        {
            result[i] *= scale;
            normSq += result[i] * result[i];
        }

        float norm = MathF.Sqrt(normSq);
        if (norm > 0)
            for (int i = 0; i < _dims; i++)
                result[i] /= norm;

        return Task.FromResult<float[]?>(result);
    }

    public async Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var results = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++)
        {
            var vec = await EmbedAsync(texts[i], ct);
            if (vec is null) return null;
            results[i] = vec;
        }
        return results;
    }

    public void Dispose() { }
}
