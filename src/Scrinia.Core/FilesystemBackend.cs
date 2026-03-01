namespace Scrinia.Core;

/// <summary>
/// Default <see cref="IStorageBackend"/> that creates <see cref="FileMemoryStore"/>
/// instances backed by the local filesystem.
/// </summary>
public sealed class FilesystemBackend : IStorageBackend
{
    /// <inheritdoc />
    public string BackendId => "filesystem";

    /// <inheritdoc />
    public IMemoryStore CreateStore(string storeName, string storePath)
    {
        Directory.CreateDirectory(storePath);
        return new FileMemoryStore(storePath);
    }
}
