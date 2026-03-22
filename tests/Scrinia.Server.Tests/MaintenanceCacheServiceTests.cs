using FluentAssertions;
using Scrinia.Core;
using Scrinia.Server.Services;
using Xunit;

namespace Scrinia.Server.Tests;

public sealed class MaintenanceCacheServiceTests : IDisposable
{
    private readonly string _tempDir;

    public MaintenanceCacheServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-mcs-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task ExecuteAsync_completes_without_error_on_empty_store()
    {
        var storePath = Path.Combine(_tempDir, "store");
        var storePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["test"] = storePath
        };

        var backend = new FilesystemBackend();
        var storeManager = new StoreManager(storePaths, backend);

        var service = new MaintenanceCacheService(storeManager);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Start the service; cancel after a brief moment to let one scan complete
        using var delayCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await service.StartAsync(CancellationToken.None);

        // Give it time to run one scan
        try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { }

        await service.StopAsync(CancellationToken.None);

        // Verify the cache was written
        var store = storeManager.GetStore("test");
        bool found = MaintenanceCache.TryReadCache(store, out var cache);
        found.Should().BeTrue("the service should have written a cache file");
        cache.Should().NotBeNull();
        cache!.StaleCount.Should().Be(0);
        cache.DriftCount.Should().Be(0);
        cache.OrphanCount.Should().Be(0);
    }

    [Fact]
    public void Can_construct_with_storeManager()
    {
        var storePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = Path.Combine(_tempDir, "default")
        };

        var backend = new FilesystemBackend();
        var storeManager = new StoreManager(storePaths, backend);

        var service = new MaintenanceCacheService(storeManager);
        service.Should().NotBeNull();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
