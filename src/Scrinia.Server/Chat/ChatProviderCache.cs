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
    private readonly ConcurrentDictionary<string, IChatProvider?> _providers = new(StringComparer.OrdinalIgnoreCase);
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
        return _providers.GetOrAdd(providerName, name =>
            ChatProviderFactory.Create(name, _options, _logger));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var provider in _providers.Values)
        {
            if (provider is IDisposable d) d.Dispose();
        }
        _providers.Clear();
    }
}
