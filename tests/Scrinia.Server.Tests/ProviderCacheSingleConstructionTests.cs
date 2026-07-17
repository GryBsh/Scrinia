using FluentAssertions;
using Scrinia.Core;
using Scrinia.Server.Services;
using Xunit;

namespace Scrinia.Server.Tests;

/// <summary>
/// Guards against the ConcurrentDictionary.GetOrAdd double-construction hazard:
/// concurrent first-access for the same key must invoke the (expensive/side-effecting)
/// factory exactly once, so no undisposed "loser" resources are ever created.
/// </summary>
public class ProviderCacheSingleConstructionTests : IDisposable
{
    private readonly string _tempDir;

    public ProviderCacheSingleConstructionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_sm_concurrency_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task StoreManager_ConcurrentFirstAccess_ConstructsStoreExactlyOnce()
    {
        var paths = new Dictionary<string, string>
        {
            ["default"] = Path.Combine(_tempDir, "stores", "default")
        };
        var backend = new CountingBackend();
        var manager = new StoreManager(paths, backend);

        const int taskCount = 32;
        using var barrier = new Barrier(taskCount);

        var tasks = Enumerable.Range(0, taskCount).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            return manager.GetStore("default");
        })).ToArray();

        var stores = await Task.WhenAll(tasks);

        backend.CreateCount.Should().Be(1, "the factory must run exactly once per key even under concurrent first access");
        stores.Should().OnlyContain(s => ReferenceEquals(s, stores[0]), "all callers must observe the same cached instance");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>Test backend that counts CreateStore invocations and delegates to filesystem.</summary>
    private sealed class CountingBackend : IStorageBackend
    {
        private int _createCount;
        public string BackendId => "counting-backend";
        public int CreateCount => _createCount;

        public IMemoryStore CreateStore(string storeName, string storePath)
        {
            Interlocked.Increment(ref _createCount);
            Directory.CreateDirectory(storePath);
            return new FileMemoryStore(storePath);
        }
    }
}
