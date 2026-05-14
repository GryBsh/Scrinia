using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.Logging;
using Scrinia.Core.Embeddings;
using Scrinia.Plugin.Llama;

namespace Scrinia.Plugin.Embeddings;

/// <summary>
/// Vulkan GPU-accelerated embedding provider via LLamaSharp.
/// Loads a GGUF embedding model and uses Vulkan for GPU inference.
/// </summary>
public sealed class VulkanEmbeddingProvider : IEmbeddingProvider
{
    private readonly LLamaEmbedder _embedder;
    private readonly LLamaWeights _weights;
    private readonly int _dimensions;
    private readonly string _signature;
    private readonly ILogger _logger;
    private bool _disposed;

    public bool IsAvailable => !_disposed;
    public int Dimensions => _dimensions;
    public string Signature => _signature;

    private VulkanEmbeddingProvider(LLamaWeights weights, LLamaEmbedder embedder, int dimensions, string signature, ILogger logger)
    {
        _weights = weights;
        _embedder = embedder;
        _dimensions = dimensions;
        _signature = signature;
        _logger = logger;
    }

    /// <summary>Creates a Vulkan-accelerated embedding provider from a GGUF model.</summary>
    public static VulkanEmbeddingProvider Create(string modelPath, int dimensions, ILogger logger)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("GGUF embedding model not found.", modelPath);

        // Shared one-time init: Vulkan backend selection, log routing, native resolution.
        // Idempotent, so the embeddings plugin and the LLM plugin (in their separate
        // processes) both call this exactly once and cannot drift on the LLamaSharp setup.
        LlamaVulkanInit.EnsureConfigured(logger);

        var modelParams = new ModelParams(modelPath)
        {
            PoolingType = LLamaPoolingType.Mean,
            GpuLayerCount = -1, // Offload all layers to GPU
        };

        var weights = LLamaWeights.LoadFromFile(modelParams);
        var embedder = new LLamaEmbedder(weights, modelParams);

        // Signature includes the filename so swapping GGUFs (e.g. MiniLM→bge-small) triggers
        // a vector-store reindex via VectorStore's signature-mismatch check.
        string signature = $"vulkan:{Path.GetFileNameWithoutExtension(modelPath)}";
        logger.LogInformation("Vulkan embedding provider loaded: {ModelPath}, {Dims} dimensions", modelPath, dimensions);
        return new VulkanEmbeddingProvider(weights, embedder, dimensions, signature, logger);
    }

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (_disposed) return null;

        try
        {
            var embeddings = await _embedder.GetEmbeddings(text, ct);
            var vec = embeddings.Single().ToArray();
            VectorMath.L2Normalize(vec);
            return vec;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vulkan embedding failed");
            return null;
        }
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _embedder.Dispose();
        _weights.Dispose();
    }
}
