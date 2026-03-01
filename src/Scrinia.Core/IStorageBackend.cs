namespace Scrinia.Core;

/// <summary>
/// Factory abstraction for creating <see cref="IMemoryStore"/> instances.
/// The server registers one backend per process; plugins can replace
/// the default <see cref="FilesystemBackend"/> via DI.
/// </summary>
public interface IStorageBackend
{
    /// <summary>Identifier for diagnostics / health checks (e.g. "filesystem").</summary>
    string BackendId { get; }

    /// <summary>
    /// Creates or opens a store for the given name and path.
    /// Implementations are responsible for any setup (e.g. creating directories).
    /// </summary>
    IMemoryStore CreateStore(string storeName, string storePath);
}
