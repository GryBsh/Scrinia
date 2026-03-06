using Microsoft.Extensions.Logging;
using Scrinia.Core.Embeddings.Providers;

namespace Scrinia.Core.Embeddings;

/// <summary>Creates the appropriate <see cref="IEmbeddingProvider"/> from configuration.</summary>
public static class EmbeddingProviderFactory
{
    public static IEmbeddingProvider Create(EmbeddingOptions options, string modelsDir, ILogger logger)
    {
        try
        {
            return options.Provider.ToLowerInvariant() switch
            {
                "model2vec" => CreateModel2Vec(modelsDir, logger),
                "ollama" => new OllamaEmbeddingProvider(options.OllamaBaseUrl, options.OllamaModel, logger),
                "openai" => new OpenAiEmbeddingProvider(options.OpenAiApiKey, options.OpenAiModel, options.OpenAiBaseUrl, logger),
                "voyageai" => new VoyageAiEmbeddingProvider(options.VoyageAiApiKey, options.VoyageAiModel, options.VoyageAiBaseUrl, logger),
                "azure" => new AzureAiEmbeddingProvider(options.AzureEndpoint, options.AzureApiKey, options.AzureDeployment, options.AzureModel, options.AzureApiVersion, options.AzureUseV1, logger),
                "google" => new GoogleGeminiEmbeddingProvider(options.GoogleApiKey, options.GoogleModel, options.GoogleBaseUrl, options.GoogleDimensions, logger),
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

    private static IEmbeddingProvider CreateModel2Vec(string modelsDir, ILogger logger)
    {
        string modelDir = Path.Combine(modelsDir, "m2v-MiniLM-L6-v2");
        if (!Model2VecModelManager.IsModelAvailable(modelDir))
        {
            logger.LogWarning("Model2Vec model not downloaded. Run 'scri setup' to download it.");
            return new NullEmbeddingProvider();
        }
        return Model2VecProvider.Load(modelDir, logger);
    }
}
