using FluentAssertions;
using Microsoft.Extensions.Logging;
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

    /// <summary>A shared lock over a missing directory is a lock over nothing — it must not
    /// create the directory as a side effect of reading. Pins the read path after the
    /// directory-creating call moved onto AcquireExclusive, its only real caller.</summary>
    [Fact]
    public void AcquireShared_WhenDirectoryDoesNotExist_DoesNotCreateIt()
    {
        string missingDir = Path.Combine(_tempDir, "nested");
        string lockPath = Path.Combine(missingDir, ".lock");

        using (var fileLock = FileLock.AcquireShared(lockPath))
            Directory.Exists(missingDir).Should().BeFalse();

        Directory.Exists(missingDir).Should().BeFalse(
            because: "disposing a lock over nothing must not create anything either");
    }

    [Fact]
    public void Contention_LogsWarningAndTimeoutErrors()
    {
        var sink = new CapturingLogger();

        using var holder = FileLock.AcquireExclusive(LockPath);

        var act = () => FileLock.AcquireExclusive(LockPath, TimeSpan.FromMilliseconds(150), sink);

        act.Should().Throw<FileLockTimeoutException>();

        sink.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("contention"),
            because: "first retry must emit a warning so contention is visible in logs");
        sink.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("timed out"),
            because: "final timeout must emit an error with elapsed time");
    }

    [Fact]
    public void NoContention_EmitsNoLogs()
    {
        var sink = new CapturingLogger();

        using var lk = FileLock.AcquireExclusive(LockPath, TimeSpan.FromSeconds(1), sink);

        sink.Entries.Should().BeEmpty(
            because: "an uncontended acquire must not emit any log records");
    }

    /// <summary>Minimal in-memory ILogger that captures level + formatted message.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    [Fact]
    public void ConcurrentUpsert_NoDataLoss()
    {
        // Simulate two "processes" (threads) upserting different entries
        // to the same FileMemoryStore concurrently
        string storeDir = Path.Combine(_tempDir, "concurrent-store");
        var store = new FileMemoryStore(storeDir);

        const int iterations = 20;
        using var barrier = new Barrier(2);
        var barrierTimeout = TimeSpan.FromSeconds(30);

        // This test used to hang the entire test host indefinitely, which the runner reports as
        // an aborted run. Two causes, both fixed here:
        //
        //  1. The workers ran on Task.Run (thread-pool) threads and blocked on each other at the
        //     barrier. Under xunit's parallel load that can deadlock the pool. LongRunning gives
        //     each worker a dedicated thread.
        //  2. A worker that threw simply stopped signalling, parking its partner on
        //     SignalAndWait() forever. A bailing worker now removes itself as a participant, so
        //     the survivor is released and the real exception surfaces instead of a hang.
        Task RunWorker(string tag) => Task.Factory.StartNew(() =>
        {
            bool participating = true;
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    if (!barrier.SignalAndWait(barrierTimeout))
                        throw new TimeoutException($"worker '{tag}' timed out at barrier phase {i}");

                    store.Upsert(new ArtifactEntry(
                        $"entry-{tag}-{i}", $"file://{tag}-{i}", 100, 1,
                        DateTimeOffset.UtcNow), "local");
                }

                barrier.RemoveParticipant();
                participating = false;
            }
            finally
            {
                if (participating) barrier.RemoveParticipant();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var task1 = RunWorker("a");
        var task2 = RunWorker("b");

        Task.WaitAll([task1, task2], TimeSpan.FromSeconds(60))
            .Should().BeTrue("both writers must finish; a hang here would otherwise stall the whole suite");

        // Surfaces whatever the workers actually threw, rather than only the count mismatch.
        var faults = new[] { task1, task2 }
            .Where(t => t.IsFaulted)
            .SelectMany(t => t.Exception!.InnerExceptions)
            .ToList();
        faults.Should().BeEmpty(
            "concurrent upserts to one store must not throw; got: {0}",
            string.Join(" | ", faults.Select(e => $"{e.GetType().Name}: {e.Message}")));

        var entries = store.LoadIndex("local");
        entries.Should().HaveCount(iterations * 2,
            because: "all entries from both threads must be present — no lost writes");
        entries.Select(e => e.Name).Distinct().Should().HaveCount(iterations * 2);
    }
}
