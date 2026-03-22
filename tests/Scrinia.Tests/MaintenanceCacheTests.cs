using System.Text.Json;
using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

/// <summary>
/// Tests for <see cref="MaintenanceCache"/> — write/read/staleness of maintenance state cache.
/// </summary>
public sealed class MaintenanceCacheTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly IMemoryStore _store;

    public MaintenanceCacheTests()
    {
        _scope = new TestHelpers.StoreScope();
        _store = MemoryStoreContext.Current!;
    }

    public void Dispose() => _scope.Dispose();

    [Fact]
    public void WriteAndRead_Succeeds()
    {
        // Arrange & Act
        MaintenanceCache.WriteCache(_store, staleCount: 3, reviewCount: 2,
            driftCount: 1, missingCount: 0, orphanCount: 5);

        bool found = MaintenanceCache.TryReadCache(_store, out var data);

        // Assert
        found.Should().BeTrue("cache was just written and should be fresh");
        data.Should().NotBeNull();
        data!.StaleCount.Should().Be(3);
        data.ReviewCount.Should().Be(2);
        data.DriftCount.Should().Be(1);
        data.MissingCount.Should().Be(0);
        data.OrphanCount.Should().Be(5);
        data.ComputedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void StaleCache_ReturnsFalse()
    {
        // Arrange — write a cache, then overwrite the file with an old timestamp
        MaintenanceCache.WriteCache(_store, staleCount: 1, reviewCount: 0,
            driftCount: 0, missingCount: 0, orphanCount: 0);

        // Read the file back, modify ComputedAt to 15 minutes ago, write it back
        string cacheDir = Path.Combine(_scope.WorkspaceDir, ".scrinia", "cache");
        string filePath = Path.Combine(cacheDir, "maintenance.json");

        var oldData = new MaintenanceCacheData(
            DateTimeOffset.UtcNow.AddMinutes(-15), 1, 0, 0, 0, 0);
        string json = JsonSerializer.Serialize(oldData);
        File.WriteAllText(filePath, json);

        // Act
        bool found = MaintenanceCache.TryReadCache(_store, out var data);

        // Assert
        found.Should().BeFalse("cache older than 10 minutes should be considered stale");
        data.Should().BeNull();
    }

    [Fact]
    public void MissingCache_ReturnsFalse()
    {
        // Act — no cache has been written
        bool found = MaintenanceCache.TryReadCache(_store, out var data);

        // Assert
        found.Should().BeFalse("no cache file exists");
        data.Should().BeNull();
    }
}
