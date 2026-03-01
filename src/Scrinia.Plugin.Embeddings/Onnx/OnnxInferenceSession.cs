using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Scrinia.Plugin.Embeddings.Onnx;

/// <summary>
/// ONNX inference session for sentence embedding models.
/// Tokenize → session.Run() → mean pool → L2 normalize.
/// </summary>
public sealed class OnnxInferenceSession : IDisposable
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly ILogger _logger;

    private OnnxInferenceSession(InferenceSession session, BertTokenizer tokenizer, ILogger logger)
    {
        _session = session;
        _tokenizer = tokenizer;
        _logger = logger;
    }

    /// <summary>Creates a new inference session from model files.</summary>
    /// <remarks>
    /// Attempts to use the requested hardware acceleration, falling back to CPU
    /// if the execution provider fails (common in trimmed single-file hosts where
    /// the JIT can't resolve all OnnxRuntime internal methods).
    /// </remarks>
    public static OnnxInferenceSession Create(string modelDir, HardwareAcceleration hardware, ILogger logger)
    {
        string modelPath = Path.Combine(modelDir, "model.onnx");
        string vocabPath = Path.Combine(modelDir, "vocab.txt");

        if (!File.Exists(modelPath))
            throw new FileNotFoundException("ONNX model not found", modelPath);
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException("Vocab file not found", vocabPath);

        var options = new SessionOptions();
        var actual = TryConfigureProvider(options, hardware, logger);
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

        logger.LogInformation("ONNX using {Provider} execution provider", actual switch
        {
            HardwareAcceleration.DirectMl => "DirectML",
            HardwareAcceleration.Cuda => "CUDA",
            _ => "CPU"
        });

        var session = new InferenceSession(modelPath, options);
        var tokenizer = BertTokenizer.FromVocabFile(vocabPath);

        return new OnnxInferenceSession(session, tokenizer, logger);
    }

    /// <summary>
    /// Tries to attach the requested execution provider. Falls back to CPU on failure.
    /// Each EP method is [NoInlining] so JIT failures in trimmed hosts are catchable.
    /// </summary>
    private static HardwareAcceleration TryConfigureProvider(
        SessionOptions options, HardwareAcceleration preferred, ILogger logger)
    {
        if (preferred == HardwareAcceleration.DirectMl)
        {
            try
            {
                AttachDirectMl(options);
                return HardwareAcceleration.DirectMl;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "DirectML execution provider failed, falling back to CPU");
            }
        }
        else if (preferred == HardwareAcceleration.Cuda)
        {
            try
            {
                AttachCuda(options);
                return HardwareAcceleration.Cuda;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "CUDA execution provider failed, falling back to CPU");
            }
        }

        return HardwareAcceleration.Cpu;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AttachDirectMl(SessionOptions options)
        => options.AppendExecutionProvider_DML(0);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AttachCuda(SessionOptions options)
        => options.AppendExecutionProvider_CUDA();

    /// <summary>Embeds a single text, returning an L2-normalized vector.</summary>
    public float[] Embed(string text)
    {
        var (inputIds, attentionMask, tokenTypeIds) = _tokenizer.Encode(text);
        int seqLen = inputIds.Length;

        var inputIdsTensor = new DenseTensor<long>(inputIds, [1, seqLen]);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, seqLen]);
        var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, [1, seqLen]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor),
        };

        using var results = _session.Run(inputs);

        // Output shape: [1, seqLen, hiddenSize]
        var output = results.First().AsTensor<float>();
        int hiddenSize = output.Dimensions[2];

        // Mean pooling: average over non-padding tokens
        float[] pooled = MeanPool(output, attentionMask, seqLen, hiddenSize);

        // L2 normalize
        L2Normalize(pooled);

        return pooled;
    }

    /// <summary>Embeds multiple texts in a single batched ONNX forward pass.</summary>
    public float[][] EmbedBatch(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return [];
        if (texts.Count == 1) return [Embed(texts[0])];

        // 1. Tokenize all texts
        var tokenized = new (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds)[texts.Count];
        int maxSeqLen = 0;
        for (int i = 0; i < texts.Count; i++)
        {
            tokenized[i] = _tokenizer.Encode(texts[i]);
            maxSeqLen = Math.Max(maxSeqLen, tokenized[i].InputIds.Length);
        }

        int batchSize = texts.Count;

        // 2. Create padded tensors [batchSize, maxSeqLen]
        var inputIds = new long[batchSize * maxSeqLen];
        var attentionMask = new long[batchSize * maxSeqLen];
        var tokenTypeIds = new long[batchSize * maxSeqLen];

        for (int b = 0; b < batchSize; b++)
        {
            int seqLen = tokenized[b].InputIds.Length;
            int offset = b * maxSeqLen;
            Array.Copy(tokenized[b].InputIds, 0, inputIds, offset, seqLen);
            Array.Copy(tokenized[b].AttentionMask, 0, attentionMask, offset, seqLen);
            // tokenTypeIds and padding positions are already 0
        }

        // 3. Single ONNX forward pass
        var inputIdsTensor = new DenseTensor<long>(inputIds, [batchSize, maxSeqLen]);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, [batchSize, maxSeqLen]);
        var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, [batchSize, maxSeqLen]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor),
        };

        using var results = _session.Run(inputs);
        var output = results.First().AsTensor<float>();
        int hiddenSize = output.Dimensions[2];

        // 4. Mean pool + L2 normalize each sequence independently
        var embeddings = new float[batchSize][];
        for (int b = 0; b < batchSize; b++)
        {
            embeddings[b] = MeanPoolBatched(output, tokenized[b].AttentionMask, b, maxSeqLen, hiddenSize);
            L2Normalize(embeddings[b]);
        }

        return embeddings;
    }

    private static float[] MeanPoolBatched(Tensor<float> output, long[] attentionMask,
        int batchIndex, int maxSeqLen, int hiddenSize)
    {
        float[] pooled = new float[hiddenSize];
        float tokenCount = 0;

        for (int t = 0; t < attentionMask.Length; t++)
        {
            if (attentionMask[t] == 0) continue;
            tokenCount++;
            for (int h = 0; h < hiddenSize; h++)
                pooled[h] += output[batchIndex, t, h];
        }

        if (tokenCount > 0)
        {
            for (int h = 0; h < hiddenSize; h++)
                pooled[h] /= tokenCount;
        }

        return pooled;
    }

    private static float[] MeanPool(Tensor<float> output, long[] attentionMask, int seqLen, int hiddenSize)
    {
        float[] pooled = new float[hiddenSize];
        float tokenCount = 0;

        for (int t = 0; t < seqLen; t++)
        {
            if (attentionMask[t] == 0) continue;
            tokenCount++;
            for (int h = 0; h < hiddenSize; h++)
                pooled[h] += output[0, t, h];
        }

        if (tokenCount > 0)
        {
            for (int h = 0; h < hiddenSize; h++)
                pooled[h] /= tokenCount;
        }

        return pooled;
    }

    private static void L2Normalize(float[] vector)
    {
        float norm = 0;
        foreach (float f in vector)
            norm += f * f;

        norm = MathF.Sqrt(norm);
        if (norm > 0)
        {
            for (int i = 0; i < vector.Length; i++)
                vector[i] /= norm;
        }
    }

    public void Dispose() => _session.Dispose();
}
