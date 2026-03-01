using Microsoft.Extensions.Logging;
using Scrinia.Plugin.Embeddings.Onnx;
using Scrinia.Plugin.Embeddings.Providers;

namespace Scrinia.Plugin.Embeddings;

/// <summary>Creates the appropriate <see cref="IEmbeddingProvider"/> from configuration.</summary>
public static class EmbeddingProviderFactory
{
    public static IEmbeddingProvider Create(EmbeddingOptions options, string dataDir, ILogger logger)
    {
        try
        {
            return options.Provider.ToLowerInvariant() switch
            {
                "onnx" => CreateOnnx(options, dataDir, logger),
                "ollama" => new OllamaEmbeddingProvider(options.OllamaBaseUrl, options.OllamaModel, logger),
                "openai" => new OpenAiEmbeddingProvider(options.OpenAiApiKey, options.OpenAiModel, options.OpenAiBaseUrl, logger),
                "none" => new NullEmbeddingProvider(),
                _ => new NullEmbeddingProvider(),
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create embedding provider '{Provider}', falling back to null", options.Provider);
            return new NullEmbeddingProvider();
        }
    }

    private static IEmbeddingProvider CreateOnnx(EmbeddingOptions options, string dataDir, ILogger logger)
    {
        string modelDir = Path.Combine(dataDir, "models", "all-MiniLM-L6-v2");
        var hardware = HardwareDetector.Detect(options.Hardware, logger);
        return new OnnxEmbeddingProvider(modelDir, hardware, logger);
    }
}
