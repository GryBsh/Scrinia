using System.Collections.Concurrent;
using Scrinia.Core;

namespace Scrinia.Server.Services;

/// <summary>
/// Singleton factory + cache for named <see cref="IMemoryStore"/> instances.
/// Each configured store name maps to a resolved filesystem path.
/// Storage creation is delegated to the <see cref="IStorageBackend"/>.
/// </summary>
public sealed class StoreManager
{
    private readonly Dictionary<string, string> _storePaths;
    private readonly IStorageBackend _backend;
    private readonly ConcurrentDictionary<string, IMemoryStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    public StoreManager(Dictionary<string, string> storePaths, IStorageBackend backend)
    {
        _storePaths = new Dictionary<string, string>(storePaths, StringComparer.OrdinalIgnoreCase);
        _backend = backend;
    }

    /// <summary>
    /// Gets or creates an <see cref="IMemoryStore"/> for the given store name.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Thrown if the store name is not configured.</exception>
    public IMemoryStore GetStore(string storeName)
    {
        if (!_storePaths.ContainsKey(storeName))
            throw new KeyNotFoundException($"Store '{storeName}' is not configured.");

        return _stores.GetOrAdd(storeName, name =>
            _backend.CreateStore(name, _storePaths[name]));
    }

    /// <summary>All configured store names.</summary>
    public IReadOnlyCollection<string> StoreNames => _storePaths.Keys;

    /// <summary>Returns true if the given store name is configured.</summary>
    public bool StoreExists(string storeName) =>
        _storePaths.ContainsKey(storeName);

    /// <summary>The storage backend used to create stores.</summary>
    public IStorageBackend Backend => _backend;
}
