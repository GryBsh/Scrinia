using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.Logging;
using Scrinia.Core.Embeddings;

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
    private readonly ILogger _logger;
    private bool _disposed;

    public bool IsAvailable => !_disposed;
    public int Dimensions => _dimensions;

    private VulkanEmbeddingProvider(LLamaWeights weights, LLamaEmbedder embedder, int dimensions, ILogger logger)
    {
        _weights = weights;
        _embedder = embedder;
        _dimensions = dimensions;
        _logger = logger;
    }

    /// <summary>Creates a Vulkan-accelerated embedding provider from a GGUF model.</summary>
    public static VulkanEmbeddingProvider Create(string modelPath, int dimensions, ILogger logger)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("GGUF embedding model not found.", modelPath);

        // Configure LLamaSharp to use Vulkan backend BEFORE any model loading.
        // This must happen before the first native P/Invoke call.
        // Self-contained publish flattens native DLLs to the app root, so we
        // add the app base directory as a search path for native library discovery.
        NativeLibraryConfig.LLama
            .WithCuda(false)
            .WithVulkan()
            .WithSearchDirectory(AppContext.BaseDirectory)
            .WithAutoFallback();

        // Force immediate native library loading so backend selection is finalized.
        NativeApi.llama_empty_call();

        var modelParams = new ModelParams(modelPath)
        {
            PoolingType = LLamaPoolingType.Mean,
            GpuLayerCount = -1, // Offload all layers to GPU
        };

        var weights = LLamaWeights.LoadFromFile(modelParams);
        var embedder = new LLamaEmbedder(weights, modelParams);

        logger.LogInformation("Vulkan embedding provider loaded: {ModelPath}, {Dims} dimensions", modelPath, dimensions);
        return new VulkanEmbeddingProvider(weights, embedder, dimensions, logger);
    }

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (_disposed) return null;

        try
        {
            var embeddings = await _embedder.GetEmbeddings(text);
            var vec = embeddings.Single().ToArray();

            // L2 normalize
            float normSq = 0;
            foreach (float f in vec) normSq += f * f;
            float norm = MathF.Sqrt(normSq);
            if (norm > 0)
                for (int i = 0; i < vec.Length; i++)
                    vec[i] /= norm;

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
