namespace Scrinia.Server.Chat;

/// <summary>Configuration POCO for chat LLM providers. Bound from Scrinia:Chat in appsettings.json.</summary>
public sealed class ChatOptions
{
    /// <summary>Available providers (comma-separated): "anthropic", "openai", "gemini", or "none".</summary>
    public string Providers { get; set; } = "none";

    // ── Anthropic ────────────────────────────────────────────────────────────

    /// <summary>Anthropic API key.</summary>
    public string? AnthropicApiKey { get; set; }

    /// <summary>Anthropic model name.</summary>
    public string AnthropicModel { get; set; } = "claude-sonnet-4-20250514";

    /// <summary>Anthropic API base URL.</summary>
    public string AnthropicBaseUrl { get; set; } = "https://api.anthropic.com";

    // ── OpenAI ───────────────────────────────────────────────────────────────

    /// <summary>OpenAI API key.</summary>
    public string? OpenAiApiKey { get; set; }

    /// <summary>OpenAI model name.</summary>
    public string OpenAiModel { get; set; } = "gpt-4o-mini";

    /// <summary>OpenAI API base URL (for custom endpoints).</summary>
    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";

    // ── Gemini ───────────────────────────────────────────────────────────────

    /// <summary>Google Gemini API key.</summary>
    public string? GeminiApiKey { get; set; }

    /// <summary>Google Gemini model name.</summary>
    public string GeminiModel { get; set; } = "gemini-2.0-flash";

    /// <summary>Google Gemini API base URL.</summary>
    public string GeminiBaseUrl { get; set; } = "https://generativelanguage.googleapis.com";

    /// <summary>Max tokens for LLM responses.</summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>Temperature for LLM responses.</summary>
    public double Temperature { get; set; } = 0.7;

    // ── Resilience ─────────────────────────────────────────────────────────

    /// <summary>Maximum retry attempts for transient failures (0 = no retries).</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base delay in milliseconds for exponential backoff.</summary>
    public int RetryBaseDelayMs { get; set; } = 200;

    /// <summary>Consecutive failures before the circuit breaker opens.</summary>
    public int CircuitBreakerThreshold { get; set; } = 5;

    /// <summary>Seconds to wait before transitioning from open to half-open.</summary>
    public int CircuitBreakerCooldownSeconds { get; set; } = 30;

    /// <summary>Returns the list of configured provider names (those with API keys set).</summary>
    public string[] GetAvailableProviders()
    {
        if (string.Equals(Providers, "none", StringComparison.OrdinalIgnoreCase))
            return [];

        var requested = Providers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var available = new List<string>();

        foreach (var p in requested)
        {
            bool hasKey = p.ToLowerInvariant() switch
            {
                "anthropic" => !string.IsNullOrWhiteSpace(AnthropicApiKey),
                "openai" => !string.IsNullOrWhiteSpace(OpenAiApiKey),
                "gemini" => !string.IsNullOrWhiteSpace(GeminiApiKey),
                _ => false,
            };
            if (hasKey) available.Add(p.ToLowerInvariant());
        }

        return available.ToArray();
    }
}
