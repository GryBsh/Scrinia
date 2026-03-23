using Microsoft.Extensions.Logging;
using Scrinia.Core.Embeddings.Providers;
using Scrinia.Core.Resilience;

namespace Scrinia.Core.Embeddings;

/// <summary>Creates the appropriate <see cref="IEmbeddingProvider"/> from configuration.</summary>
public static class EmbeddingProviderFactory
{
    public static IEmbeddingProvider Create(EmbeddingOptions options, string modelsDir, ILogger logger)
    {
        try
        {
            var retryOptions = new RetryOptions(options.MaxRetries, options.RetryBaseDelayMs);
            var cbOptions = new CircuitBreakerOptions(options.CircuitBreakerThreshold, options.CircuitBreakerCooldownSeconds);

            var provider = options.Provider.ToLowerInvariant();
            CircuitBreaker cb;

            switch (provider)
            {
                case "model2vec":
                    return CreateModel2Vec(modelsDir, logger);
                case "ollama":
                    cb = new CircuitBreaker(cbOptions);
                    CircuitBreakerRegistry.Register("embedding:ollama", cb);
                    return new OllamaEmbeddingProvider(options.OllamaBaseUrl, options.OllamaModel, logger, cb, retryOptions);
                case "openai":
                    cb = new CircuitBreaker(cbOptions);
                    CircuitBreakerRegistry.Register("embedding:openai", cb);
                    return new OpenAiEmbeddingProvider(options.OpenAiApiKey, options.OpenAiModel, options.OpenAiBaseUrl, logger, cb, retryOptions);
                case "voyageai":
                    cb = new CircuitBreaker(cbOptions);
                    CircuitBreakerRegistry.Register("embedding:voyageai", cb);
                    return new VoyageAiEmbeddingProvider(options.VoyageAiApiKey, options.VoyageAiModel, options.VoyageAiBaseUrl, logger, cb, retryOptions);
                case "azure":
                    cb = new CircuitBreaker(cbOptions);
                    CircuitBreakerRegistry.Register("embedding:azure", cb);
                    return new AzureAiEmbeddingProvider(options.AzureEndpoint, options.AzureApiKey, options.AzureDeployment, options.AzureModel, options.AzureApiVersion, options.AzureUseV1, logger, cb, retryOptions);
                case "google":
                    cb = new CircuitBreaker(cbOptions);
                    CircuitBreakerRegistry.Register("embedding:google", cb);
                    return new GoogleGeminiEmbeddingProvider(options.GoogleApiKey, options.GoogleModel, options.GoogleBaseUrl, options.GoogleDimensions, logger, cb, retryOptions);
                default:
                    logger.LogWarning("Embedding provider '{Provider}' is not configured — provider name unrecognized, falling back to null", options.Provider);
                    return new NullEmbeddingProvider();
            }
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
