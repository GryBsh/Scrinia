using FluentAssertions;
using Scrinia.Core;
using Scrinia.Server.Services;
using Xunit;

namespace Scrinia.Server.Tests;

public class StorageBackendIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public StorageBackendIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_sm_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void StoreManager_WithFilesystemBackend_CreatesStores()
    {
        var paths = new Dictionary<string, string>
        {
            ["default"] = Path.Combine(_tempDir, "stores", "default"),
            ["second"] = Path.Combine(_tempDir, "stores", "second")
        };
        var backend = new FilesystemBackend();
        var manager = new StoreManager(paths, backend);

        var store = manager.GetStore("default");
        store.Should().NotBeNull();
        store.Should().BeAssignableTo<IMemoryStore>();
    }

    [Fact]
    public void StoreManager_CachesStoreInstances()
    {
        var paths = new Dictionary<string, string>
        {
            ["default"] = Path.Combine(_tempDir, "stores", "default")
        };
        var backend = new FilesystemBackend();
        var manager = new StoreManager(paths, backend);

        var store1 = manager.GetStore("default");
        var store2 = manager.GetStore("default");

        store1.Should().BeSameAs(store2);
    }

    [Fact]
    public void StoreManager_ExposesBackend()
    {
        var paths = new Dictionary<string, string>
        {
            ["default"] = Path.Combine(_tempDir, "stores", "default")
        };
        var backend = new FilesystemBackend();
        var manager = new StoreManager(paths, backend);

        manager.Backend.Should().BeSameAs(backend);
        manager.Backend.BackendId.Should().Be("filesystem");
    }

    [Fact]
    public void StoreManager_WithCustomBackend_UsesIt()
    {
        var paths = new Dictionary<string, string>
        {
            ["custom"] = Path.Combine(_tempDir, "stores", "custom")
        };
        var customBackend = new TestBackend();
        var manager = new StoreManager(paths, customBackend);

        var store = manager.GetStore("custom");
        store.Should().BeOfType<FileMemoryStore>();
        manager.Backend.BackendId.Should().Be("test-backend");
        customBackend.CreateCount.Should().Be(1);
    }

    [Fact]
    public void StoreManager_UnknownStore_Throws()
    {
        var paths = new Dictionary<string, string>
        {
            ["default"] = Path.Combine(_tempDir, "stores", "default")
        };
        var manager = new StoreManager(paths, new FilesystemBackend());

        var act = () => manager.GetStore("nonexistent");
        act.Should().Throw<KeyNotFoundException>();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>
    /// Test backend that counts CreateStore calls and delegates to filesystem.
    /// </summary>
    private sealed class TestBackend : IStorageBackend
    {
        public string BackendId => "test-backend";
        public int CreateCount { get; private set; }

        public IMemoryStore CreateStore(string storeName, string storePath)
        {
            CreateCount++;
            Directory.CreateDirectory(storePath);
            return new FileMemoryStore(storePath);
        }
    }
}
