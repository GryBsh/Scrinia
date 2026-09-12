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

    /// <summary>A lock over nothing. Disposing it is a no-op.</summary>
    private FileLock() { }

    /// <summary>
    /// Acquires an exclusive lock for write operations. Blocks all other readers and writers.
    /// Retries with exponential backoff up to <paramref name="timeout"/>.
    /// </summary>
    public static FileLock AcquireExclusive(string lockPath, TimeSpan? timeout = null, ILogger? logger = null)
    {
        // Writers create the directory; readers do not. An exclusive lock guards a write, and the
        // write needs somewhere to land. AcquireShared has already returned a lock over nothing if
        // the directory is missing, so this call was pure cost on every cold read.
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        return Acquire(lockPath, FileAccess.ReadWrite, FileShare.None, timeout ?? TimeSpan.FromSeconds(5), logger);
    }

    internal static FileLock AcquireExclusive(string lockPath, TimeSpan timeout, CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(lockPath);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "The lock timeout must be positive.");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        return Acquire(lockPath, FileAccess.ReadWrite, FileShare.None, timeout, logger, cancellationToken);
    }

    /// <summary>
    /// Acquires a shared lock for read operations. Multiple shared locks can coexist.
    ///
    /// <para>When the lock's directory does not exist there is nothing to read and nothing to
    /// guard, so this returns a lock over nothing rather than creating the directory.</para>
    ///
    /// <para>If the directory is deleted between that check and the open, the open throws
    /// <see cref="DirectoryNotFoundException"/> — an <see cref="IOException"/>, so the retry loop
    /// below retries it to timeout rather than recreating the directory. That is intended: a store
    /// directory vanishing under an in-flight read is a torn state, and failing loudly beats
    /// silently resurrecting a directory the caller never asked for.</para>
    /// </summary>
    public static FileLock AcquireShared(string lockPath, TimeSpan? timeout = null, ILogger? logger = null)
    {
        string? dir = Path.GetDirectoryName(lockPath);
        if (dir is not null && !Directory.Exists(dir))
            return new FileLock();

        return Acquire(lockPath, FileAccess.Read, FileShare.Read, timeout ?? TimeSpan.FromSeconds(5), logger);
    }

    private static FileLock Acquire(string lockPath, FileAccess access, FileShare share, TimeSpan timeout, ILogger? logger)
        => Acquire(lockPath, access, share, timeout, logger, CancellationToken.None);

    private static FileLock Acquire(string lockPath, FileAccess access, FileShare share, TimeSpan timeout,
        ILogger? logger, CancellationToken cancellationToken)
    {
        ILogger log = logger ?? DefaultLogger;
        var sw = Stopwatch.StartNew();
        int delayMs = 10;
        bool contended = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, access, share);
                return new FileLock(stream);
            }
            catch (IOException ex) when (sw.Elapsed < timeout && IsTransientIoException(ex))
            {
                if (!contended)
                {
                    contended = true;
                    LockContended(log, lockPath, sw.ElapsedMilliseconds, null);
                }
                if (cancellationToken.WaitHandle.WaitOne(delayMs))
                    cancellationToken.ThrowIfCancellationRequested();
                delayMs = Math.Min(delayMs * 2, 500);
            }
            catch (UnauthorizedAccessException)
            {
                // Benign collision on deterministic sidecar content (see EnsureGitIgnore:241
                // race comment and FileMemoryStore LoadIndexUnsafe migration write-back).
                // The file that won the race left correct bytes; do not loop, do not throw.
                // Return a lock over nothing so the caller can proceed to read the correct file.
                if (File.Exists(lockPath))
                    return new FileLock();
                throw;
            }
            catch (IOException)
            {
                LockTimedOut(log, lockPath, sw.ElapsedMilliseconds, null);
                throw new FileLockTimeoutException(lockPath, timeout);
            }
        }
    }

    private static bool IsTransientIoException(IOException ex)
    {
        // Only retry known transient sharing violations. Every other IOException
        // propagates to FileRetry outer window or surfaces as timeout.
        int hr = System.Runtime.InteropServices.Marshal.GetHRForException(ex);
        int win32 = hr & 0xFFFF;
        return win32 == 32  // ERROR_SHARING_VIOLATION
            || win32 == 33; // ERROR_LOCK_VIOLATION
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
