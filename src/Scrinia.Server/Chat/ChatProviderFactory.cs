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

            CircuitBreaker cb;
            switch (providerName.ToLowerInvariant())
            {
                case "anthropic" when !string.IsNullOrWhiteSpace(options.AnthropicApiKey):
                    cb = new CircuitBreaker(cbOptions);
                    CircuitBreakerRegistry.Register("chat:anthropic", cb);
                    return new AnthropicChatProvider(options.AnthropicApiKey!, options.AnthropicModel,
                        options.AnthropicBaseUrl, options.MaxTokens, options.Temperature, cb, retryOptions);

                case "openai" when !string.IsNullOrWhiteSpace(options.OpenAiApiKey):
                    cb = new CircuitBreaker(cbOptions);
                    CircuitBreakerRegistry.Register("chat:openai", cb);
                    return new OpenAiChatProvider(options.OpenAiApiKey!, options.OpenAiModel,
                        options.OpenAiBaseUrl, options.MaxTokens, options.Temperature, cb, retryOptions);

                case "gemini" when !string.IsNullOrWhiteSpace(options.GeminiApiKey):
                    cb = new CircuitBreaker(cbOptions);
                    CircuitBreakerRegistry.Register("chat:gemini", cb);
                    return new GeminiChatProvider(options.GeminiApiKey!, options.GeminiModel,
                        options.GeminiBaseUrl, options.MaxTokens, options.Temperature, cb, retryOptions);

                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create chat provider '{Provider}'", providerName);
            return null;
        }
    }
}
