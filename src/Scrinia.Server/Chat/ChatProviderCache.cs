using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Scrinia.Server.Chat;

/// <summary>
/// Process-lifetime cache for <see cref="IChatProvider"/> instances.
/// Ensures that HttpClient and CircuitBreaker are created once per provider name
/// and shared across all requests, rather than recreated on every chat request.
/// </summary>
public sealed class ChatProviderCache : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<IChatProvider?>> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ChatOptions _options;
    private readonly ILogger<ChatProviderCache> _logger;
    private bool _disposed;

    public ChatProviderCache(ChatOptions options, ILogger<ChatProviderCache> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Returns a cached provider for <paramref name="providerName"/>, creating it on first access.
    /// Returns null when the provider is not configured (missing API key, etc.).
    /// The returned instance is shared — callers must NOT dispose it.
    /// </summary>
    public IChatProvider? GetOrCreate(string providerName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var lazy = _providers.GetOrAdd(providerName, name =>
            new Lazy<IChatProvider?>(() => ChatProviderFactory.Create(name, _options, _logger),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var lazy in _providers.Values)
        {
            if (!lazy.IsValueCreated) continue;
            if (lazy.Value is IDisposable d) d.Dispose();
        }
        _providers.Clear();
    }
}
