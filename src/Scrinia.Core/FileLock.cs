using System.Diagnostics;

namespace Scrinia.Core;

/// <summary>
/// Cross-process file lock using OS-enforced FileStream locks.
/// Creates a .lock file in the target directory.
/// </summary>
public sealed class FileLock : IDisposable
{
    private FileStream? _stream;

    private FileLock(FileStream stream) => _stream = stream;

    /// <summary>
    /// Acquires an exclusive lock for write operations. Blocks all other readers and writers.
    /// Retries with exponential backoff up to <paramref name="timeout"/>.
    /// </summary>
    public static FileLock AcquireExclusive(string lockPath, TimeSpan? timeout = null)
        => Acquire(lockPath, FileAccess.ReadWrite, FileShare.None, timeout ?? TimeSpan.FromSeconds(5));

    /// <summary>
    /// Acquires a shared lock for read operations. Multiple shared locks can coexist.
    /// </summary>
    public static FileLock AcquireShared(string lockPath, TimeSpan? timeout = null)
        => Acquire(lockPath, FileAccess.Read, FileShare.Read, timeout ?? TimeSpan.FromSeconds(5));

    private static FileLock Acquire(string lockPath, FileAccess access, FileShare share, TimeSpan timeout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        var sw = Stopwatch.StartNew();
        int delayMs = 10;

        while (true)
        {
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, access, share);
                return new FileLock(stream);
            }
            catch (IOException) when (sw.Elapsed < timeout)
            {
                Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs * 2, 500);
            }
            catch (IOException)
            {
                throw new FileLockTimeoutException(lockPath, timeout);
            }
        }
    }

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
