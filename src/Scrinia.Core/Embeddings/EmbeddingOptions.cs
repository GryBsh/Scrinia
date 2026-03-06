namespace Scrinia.Core.Embeddings;

/// <summary>Configuration POCO for embedding providers.</summary>
public sealed class EmbeddingOptions
{
    /// <summary>Provider: "model2vec", "ollama", "openai", "voyageai", "azure", "google", or "none".</summary>
    public string Provider { get; set; } = "model2vec";

    /// <summary>Weight applied to cosine similarity in hybrid scoring.</summary>
    public double SemanticWeight { get; set; } = 50.0;

    /// <summary>Ollama base URL (for provider=ollama).</summary>
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Ollama model name.</summary>
    public string OllamaModel { get; set; } = "all-minilm";

    /// <summary>OpenAI API key (for provider=openai).</summary>
    public string? OpenAiApiKey { get; set; }

    /// <summary>OpenAI model name.</summary>
    public string OpenAiModel { get; set; } = "text-embedding-3-small";

    /// <summary>OpenAI base URL (for custom endpoints).</summary>
    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>Voyage AI API key (for provider=voyageai).</summary>
    public string? VoyageAiApiKey { get; set; }

    /// <summary>Voyage AI model name.</summary>
    public string VoyageAiModel { get; set; } = "voyage-3.5";

    /// <summary>Voyage AI base URL.</summary>
    public string VoyageAiBaseUrl { get; set; } = "https://api.voyageai.com/v1";

    /// <summary>Azure AI Foundry endpoint URL (e.g. https://myresource.openai.azure.com).</summary>
    public string? AzureEndpoint { get; set; }

    /// <summary>Azure AI Foundry API key.</summary>
    public string? AzureApiKey { get; set; }

    /// <summary>Azure deployment name (for classic URL pattern).</summary>
    public string AzureDeployment { get; set; } = "text-embedding-3-small";

    /// <summary>Azure model name (for v1 URL pattern body).</summary>
    public string AzureModel { get; set; } = "text-embedding-3-small";

    /// <summary>Azure API version.</summary>
    public string AzureApiVersion { get; set; } = "2024-10-21";

    /// <summary>Use Azure v1 URL pattern instead of classic deployment-scoped.</summary>
    public bool AzureUseV1 { get; set; }

    /// <summary>Google Gemini API key (for provider=google).</summary>
    public string? GoogleApiKey { get; set; }

    /// <summary>Google Gemini embedding model name.</summary>
    public string GoogleModel { get; set; } = "gemini-embedding-001";

    /// <summary>Google Gemini API base URL.</summary>
    public string GoogleBaseUrl { get; set; } = "https://generativelanguage.googleapis.com";

    /// <summary>Google Gemini output dimensions (0 = model default).</summary>
    public int GoogleDimensions { get; set; }
}
