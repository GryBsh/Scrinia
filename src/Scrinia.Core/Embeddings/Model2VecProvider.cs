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
    // Both are Lazy, and that is the point — see Load. Default LazyThreadSafetyMode is
    // ExecutionAndPublication, so concurrent first embeds run the factory once.
    private readonly Lazy<float[]> _matrix;
    private readonly Lazy<Func<string, IReadOnlyList<int>>> _tokenize;
    private readonly int _dims;
    private readonly int _vocabSize;

    public bool IsAvailable => true;
    public int Dimensions => _dims;
    public string Signature => "model2vec:m2v-MiniLM-L6-v2";

    private Model2VecProvider(
        Lazy<float[]> matrix, Lazy<Func<string, IReadOnlyList<int>>> tokenize, int dims, int vocabSize)
    {
        _matrix = matrix;
        _tokenize = tokenize;
        _dims = dims;
        _vocabSize = vocabSize;
    }

    /// <summary>
    /// Opens a Model2Vec model directory. <b>The bulk of the model is not read here</b> — see
    /// below — so this is cheap enough to call on a verb that will never embed anything.
    ///
    /// <para><b>Eager, because a broken model must fail here.</b> Both files must exist; the
    /// vocab's first lines are read to decide the tokenizer kind; and the SafeTensors header is
    /// parsed to find the embedding tensor and read its shape. That is where
    /// <c>Dimensions</c> comes from, and the startup path needs it immediately: it goes into the
    /// vector store's signature, which decides whether the vectors on disk are stale.
    /// <c>WorkspaceSetup.LoadBuiltInEmbeddings</c> wraps this call in a try/catch and degrades to
    /// BM25-only search on failure, so a model that cannot be read must throw HERE, where that
    /// catch can turn it into a logged downgrade, rather than from inside a search.</para>
    ///
    /// <para><b>Deferred, because most callers never need it:</b> the embedding matrix (a single
    /// <c>float[]</c> of vocabSize × dims — over 20MB for MiniLM-L6-v2) and the tokenizer's full
    /// vocabulary. Every <c>scri</c> verb is a fresh process, and several construct a provider
    /// through <c>LoadPluginsAsync</c> without embedding anything. Measured against a local-disk
    /// workspace by renaming the model directory away and back, median of 15 runs:
    /// <c>restore</c> — the SessionStart hook, which only reads the index and walks directories —
    /// paid <b>67ms</b> for a model it never touches. <c>search</c> paid 70ms and earns it.</para>
    ///
    /// <para>The alternative was to stop calling <c>LoadPluginsAsync</c> from the verbs that do not
    /// embed. That fixes today's callers and leaves the trap armed for tomorrow's; this puts the
    /// cost where the use is.</para>
    /// </summary>
    public static Model2VecProvider Load(string modelDir, ILogger logger)
    {
        Model2VecAssetPaths paths = Model2VecAssetResolver.Resolve(modelDir, Model2VecModelManager.AssetDefinitions);
        string safeTensorsPath = paths.ModelPath;
        string vocabPath = paths.VocabPath;

        // Tokenizer KIND is decided now (20 lines of vocab); the vocabulary itself is not built.
        // SentencePiece vocabs start with [PAD] then use ▁ markers.
        bool isSentencePiece = DetectSentencePieceVocab(vocabPath);

        // Header only. GetDataStart and ReadHeader read the leading JSON blob, not the tensor.
        int dims;
        int vocabSize;
        long dataStart;
        SafeTensorsReader.TensorMeta meta;

        using (var fs = new FileStream(safeTensorsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            dataStart = SafeTensorsReader.GetDataStart(fs);
            fs.Position = 0;
            var header = SafeTensorsReader.ReadHeader(fs);

            // Find the embedding tensor — typically named "embeddings" or the only tensor
            var tensorName = header.ContainsKey("embeddings") ? "embeddings"
                : header.Keys.FirstOrDefault(k => !k.StartsWith("__", StringComparison.Ordinal))
                ?? throw new FormatException("No embedding tensor found in SafeTensors file.");

            meta = header[tensorName];
            dims = (int)meta.Shape[^1];
            vocabSize = (int)meta.Shape[0];
        }

        var matrix = new Lazy<float[]>(() =>
        {
            // Reopened rather than holding the handle across the provider's lifetime: the whole
            // point is that most providers never get here, and an open file handle per verb is a
            // cost paid by exactly the callers this defers for.
            using var fs = new FileStream(safeTensorsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var m = SafeTensorsReader.ReadFloatTensor(fs, dataStart, meta);
            logger.LogInformation("Model2Vec matrix read: {VocabSize} tokens, {Dims} dimensions", vocabSize, dims);
            return m;
        });

        var tokenize = new Lazy<Func<string, IReadOnlyList<int>>>(() =>
        {
            if (isSentencePiece)
            {
                var t = UnigramTokenizer.FromVocabFile(vocabPath);
                logger.LogInformation("Using Unigram tokenizer ({VocabSize} tokens)", t.VocabSize);
                return t.TokenizeRaw;
            }

            var bert = BertTokenizer.FromVocabFile(vocabPath);
            logger.LogInformation("Using WordPiece tokenizer ({VocabSize} tokens)", bert.VocabSize);
            return bert.TokenizeRaw;
        });

        return new Model2VecProvider(matrix, tokenize, dims, vocabSize);
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
        var tokens = _tokenize.Value(text);
        if (tokens.Count == 0)
            return Task.FromResult<float[]?>(null);

        var result = new float[_dims];
        int count = 0;

        // Dereferenced once, outside the loop: Lazy<T>.Value is a volatile read plus a branch, and
        // this loop runs once per token of every embedded string.
        float[] matrix = _matrix.Value;

        foreach (int tokenId in tokens)
        {
            if (tokenId < 0 || tokenId >= _vocabSize)
                continue;

            // Accumulate the row from the matrix
            int offset = tokenId * _dims;
            var row = matrix.AsSpan(offset, _dims);
            for (int i = 0; i < _dims; i++)
                result[i] += row[i];
            count++;
        }

        if (count == 0)
            return Task.FromResult<float[]?>(null);

        // Average then L2 normalize
        float scale = 1.0f / count;
        for (int i = 0; i < _dims; i++)
            result[i] *= scale;

        VectorMath.L2Normalize(result);

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
