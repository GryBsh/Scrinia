using Microsoft.Extensions.Logging;
using Scrinia.Core.Resilience;
using Scrinia.Server.Chat.Providers;

namespace Scrinia.Server.Chat;

/// <summary>Creates IChatProvider instances from configuration. Returns null when no provider is configured.</summary>
public static class ChatProviderFactory
{
    public static IChatProvider? Create(string providerName, ChatOptions options, ILogger logger)
    {
        try
        {
            var retryOptions = new RetryOptions(options.MaxRetries, options.RetryBaseDelayMs);
            var cbOptions = new CircuitBreakerOptions(options.CircuitBreakerThreshold, options.CircuitBreakerCooldownSeconds);

            return providerName.ToLowerInvariant() switch
            {
                "anthropic" when !string.IsNullOrWhiteSpace(options.AnthropicApiKey) =>
                    new AnthropicChatProvider(options.AnthropicApiKey!, options.AnthropicModel,
                        options.AnthropicBaseUrl, options.MaxTokens, options.Temperature,
                        new CircuitBreaker(cbOptions), retryOptions),

                "openai" when !string.IsNullOrWhiteSpace(options.OpenAiApiKey) =>
                    new OpenAiChatProvider(options.OpenAiApiKey!, options.OpenAiModel,
                        options.OpenAiBaseUrl, options.MaxTokens, options.Temperature,
                        new CircuitBreaker(cbOptions), retryOptions),

                "gemini" when !string.IsNullOrWhiteSpace(options.GeminiApiKey) =>
                    new GeminiChatProvider(options.GeminiApiKey!, options.GeminiModel,
                        options.GeminiBaseUrl, options.MaxTokens, options.Temperature,
                        new CircuitBreaker(cbOptions), retryOptions),

                _ => null,
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create chat provider '{Provider}'", providerName);
            return null;
        }
    }
}
