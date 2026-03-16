using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public class FileLockTests : IDisposable
{
    private readonly string _tempDir;

    public FileLockTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia-locktest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private string LockPath => Path.Combine(_tempDir, ".lock");

    [Fact]
    public void ExclusiveLock_BlocksSecondExclusive()
    {
        using var lock1 = FileLock.AcquireExclusive(LockPath);

        var act = () => FileLock.AcquireExclusive(LockPath, TimeSpan.FromMilliseconds(100));

        act.Should().Throw<FileLockTimeoutException>()
            .Which.LockPath.Should().Be(LockPath);
    }

    [Fact]
    public void SharedLocks_AreConcurrent()
    {
        using var lock1 = FileLock.AcquireShared(LockPath);
        using var lock2 = FileLock.AcquireShared(LockPath);

        // Both acquired — no exception
        lock1.Should().NotBeNull();
        lock2.Should().NotBeNull();
    }

    [Fact]
    public void ExclusiveLock_BlocksShared()
    {
        using var exclusive = FileLock.AcquireExclusive(LockPath);

        var act = () => FileLock.AcquireShared(LockPath, TimeSpan.FromMilliseconds(100));

        act.Should().Throw<FileLockTimeoutException>();
    }

    [Fact]
    public void SharedLock_BlocksExclusive()
    {
        using var shared = FileLock.AcquireShared(LockPath);

        var act = () => FileLock.AcquireExclusive(LockPath, TimeSpan.FromMilliseconds(100));

        act.Should().Throw<FileLockTimeoutException>();
    }

    [Fact]
    public void Lock_ReleasedOnDispose_CanReacquire()
    {
        var lock1 = FileLock.AcquireExclusive(LockPath);
        lock1.Dispose();

        using var lock2 = FileLock.AcquireExclusive(LockPath, TimeSpan.FromMilliseconds(100));
        lock2.Should().NotBeNull();
    }

    [Fact]
    public void Retry_SucceedsWhenLockReleased()
    {
        var lock1 = FileLock.AcquireExclusive(LockPath);

        // Release after 200ms on another thread
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            lock1.Dispose();
        });

        // Should succeed within the 5s default timeout
        using var lock2 = FileLock.AcquireExclusive(LockPath, TimeSpan.FromSeconds(2));
        lock2.Should().NotBeNull();
    }

    [Fact]
    public void CreatesDirectoryIfMissing()
    {
        string nested = Path.Combine(_tempDir, "sub", "dir", ".lock");
        using var lk = FileLock.AcquireExclusive(nested, TimeSpan.FromMilliseconds(500));
        lk.Should().NotBeNull();
        File.Exists(nested).Should().BeTrue();
    }

    [Fact]
    public void ConcurrentUpsert_NoDataLoss()
    {
        // Simulate two "processes" (threads) upserting different entries
        // to the same FileMemoryStore concurrently
        string storeDir = Path.Combine(_tempDir, "concurrent-store");
        var store = new FileMemoryStore(storeDir);

        const int iterations = 20;
        var barrier = new Barrier(2);

        var task1 = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                barrier.SignalAndWait();
                store.Upsert(new ArtifactEntry(
                    $"entry-a-{i}", $"file://a-{i}", 100, 1,
                    DateTimeOffset.UtcNow, $"desc-a-{i}"), "local");
            }
        });

        var task2 = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                barrier.SignalAndWait();
                store.Upsert(new ArtifactEntry(
                    $"entry-b-{i}", $"file://b-{i}", 100, 1,
                    DateTimeOffset.UtcNow, $"desc-b-{i}"), "local");
            }
        });

        Task.WaitAll(task1, task2);

        var entries = store.LoadIndex("local");
        entries.Should().HaveCount(iterations * 2,
            because: "all entries from both threads must be present — no lost writes");
        entries.Select(e => e.Name).Distinct().Should().HaveCount(iterations * 2);
    }
}
