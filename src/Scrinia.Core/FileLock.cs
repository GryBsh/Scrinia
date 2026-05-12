using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Scrinia.Core;

/// <summary>
/// Cross-process file lock using OS-enforced FileStream locks.
/// Creates a .lock file in the target directory.
/// </summary>
public sealed class FileLock : IDisposable
{
    private FileStream? _stream;

    /// <summary>
    /// Process-wide default logger used when callers don't supply one explicitly. Set this at
    /// host startup (e.g. in Server/Program.cs) to surface lock contention in your log pipeline.
    /// Defaults to <see cref="NullLogger.Instance"/>.
    /// </summary>
    public static ILogger DefaultLogger { get; set; } = NullLogger.Instance;

    private FileLock(FileStream stream) => _stream = stream;

    /// <summary>
    /// Acquires an exclusive lock for write operations. Blocks all other readers and writers.
    /// Retries with exponential backoff up to <paramref name="timeout"/>.
    /// </summary>
    public static FileLock AcquireExclusive(string lockPath, TimeSpan? timeout = null, ILogger? logger = null)
        => Acquire(lockPath, FileAccess.ReadWrite, FileShare.None, timeout ?? TimeSpan.FromSeconds(5), logger);

    /// <summary>
    /// Acquires a shared lock for read operations. Multiple shared locks can coexist.
    /// </summary>
    public static FileLock AcquireShared(string lockPath, TimeSpan? timeout = null, ILogger? logger = null)
        => Acquire(lockPath, FileAccess.Read, FileShare.Read, timeout ?? TimeSpan.FromSeconds(5), logger);

    private static FileLock Acquire(string lockPath, FileAccess access, FileShare share, TimeSpan timeout, ILogger? logger)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        ILogger log = logger ?? DefaultLogger;
        var sw = Stopwatch.StartNew();
        int delayMs = 10;
        bool contended = false;

        while (true)
        {
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, access, share);
                return new FileLock(stream);
            }
            catch (IOException) when (sw.Elapsed < timeout)
            {
                if (!contended)
                {
                    contended = true;
                    LockContended(log, lockPath, sw.ElapsedMilliseconds, null);
                }
                Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs * 2, 500);
            }
            catch (IOException)
            {
                LockTimedOut(log, lockPath, sw.ElapsedMilliseconds, null);
                throw new FileLockTimeoutException(lockPath, timeout);
            }
        }
    }

    // Compile-time-bound structured log messages — zero allocation when the level is disabled.
    private static readonly Action<ILogger, string, long, Exception?> LockContended =
        LoggerMessage.Define<string, long>(
            LogLevel.Warning,
            new EventId(1, nameof(LockContended)),
            "FileLock contention detected for {LockPath} (first wait at {ElapsedMs}ms).");

    private static readonly Action<ILogger, string, long, Exception?> LockTimedOut =
        LoggerMessage.Define<string, long>(
            LogLevel.Error,
            new EventId(2, nameof(LockTimedOut)),
            "FileLock timed out for {LockPath} after {ElapsedMs}ms — another process may be holding the lock.");

    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        stream?.Dispose();
    }
}

public sealed class FileLockTimeoutException : TimeoutException
{
    public string LockPath { get; }

    public FileLockTimeoutException(string lockPath, TimeSpan timeout)
        : base($"Failed to acquire file lock '{lockPath}' within {timeout.TotalSeconds:F1}s. " +
               "Another process may be holding the lock.")
    {
        LockPath = lockPath;
    }
}
