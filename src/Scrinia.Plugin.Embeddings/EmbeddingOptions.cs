namespace Scrinia.Plugin.Embeddings;

/// <summary>Configuration POCO bound from Scrinia:Embeddings section.</summary>
public sealed class EmbeddingOptions
{
    /// <summary>Provider: "onnx", "ollama", "openai", or "none".</summary>
    public string Provider { get; set; } = "onnx";

    /// <summary>Hardware acceleration: "auto", "cuda", "directml", or "cpu".</summary>
    public string Hardware { get; set; } = "auto";

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

    /// <summary>Maximum texts per ONNX forward pass (reindex, multi-chunk store).</summary>
    public int MaxBatchSize { get; set; } = 8;
}
