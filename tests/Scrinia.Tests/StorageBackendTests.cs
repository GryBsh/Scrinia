using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

public class StorageBackendTests : IDisposable
{
    private readonly string _tempDir;

    public StorageBackendTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_backend_test_{Guid.NewGuid():N}");
    }

    [Fact]
    public void FilesystemBackend_BackendId_IsFilesystem()
    {
        var backend = new FilesystemBackend();
        backend.BackendId.Should().Be("filesystem");
    }

    [Fact]
    public void FilesystemBackend_CreateStore_ReturnsFileMemoryStore()
    {
        var backend = new FilesystemBackend();
        string storePath = Path.Combine(_tempDir, "test-store");

        var store = backend.CreateStore("test", storePath);

        store.Should().BeOfType<FileMemoryStore>();
    }

    [Fact]
    public void FilesystemBackend_CreateStore_CreatesDirectory()
    {
        var backend = new FilesystemBackend();
        string storePath = Path.Combine(_tempDir, "new-store");

        backend.CreateStore("new", storePath);

        Directory.Exists(storePath).Should().BeTrue();
    }

    [Fact]
    public void FilesystemBackend_CreateStore_ReturnsFunctionalStore()
    {
        var backend = new FilesystemBackend();
        string storePath = Path.Combine(_tempDir, "functional-store");

        var store = backend.CreateStore("func", storePath);

        // Should be able to use the store for basic operations
        var (scope, subject) = store.ParseQualifiedName("test-entry");
        scope.Should().Be("local");
        subject.Should().Be("test-entry");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
