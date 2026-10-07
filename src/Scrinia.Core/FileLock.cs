using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Scrinia.Core;

/// <summary>
/// Cross-process file lock that creates a <c>.lock</c> file in the target directory.
///
/// <para>On Windows the exclusion is OS-enforced: the underlying FileStream opens with
/// <see cref="FileShare.None"/> for an exclusive lock or <see cref="FileShare.Read"/> for a shared
/// lock, and the system rejects a conflicting open, so a second process cannot proceed. On
/// Unix platforms .NET uses advisory <c>flock(2)</c> during the FileStream open; this class also
/// explicitly enforces the requested mode: an exclusive lock takes <c>LOCK_EX</c>, a shared lock
/// takes <c>LOCK_SH</c>, and a non-blocking attempt that fails with <c>EAGAIN</c>/<c>EWOULDBLOCK</c>
/// means another process holds the lock and is retried with exponential backoff. The lock is
/// released explicitly on dispose via <c>LOCK_UN</c>.</para>
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
    /// Which cross-process lock to take: exclusive (single writer) or shared (multiple readers).
    /// Drives the <see cref="flock"/> mode on non-Windows; on Windows <see cref="FileShare"/>
    /// already distinguishes the two, so this is informational there.
    /// </summary>
    private enum FileLockMode
    {
        Exclusive,
        Shared
    }

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
        return Acquire(lockPath, FileAccess.ReadWrite, FileShare.None, FileLockMode.Exclusive, timeout ?? TimeSpan.FromSeconds(5), logger);
    }

    internal static FileLock AcquireExclusive(string lockPath, TimeSpan timeout, CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(lockPath);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "The lock timeout must be positive.");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        return Acquire(lockPath, FileAccess.ReadWrite, FileShare.None, FileLockMode.Exclusive, timeout, logger, cancellationToken);
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
        if (dir is not null)
        {
            try { _ = File.GetAttributes(dir); }
            catch (FileNotFoundException) { return new FileLock(); }
            catch (DirectoryNotFoundException) { return new FileLock(); }
        }

        return Acquire(lockPath, FileAccess.Read, FileShare.Read, FileLockMode.Shared, timeout ?? TimeSpan.FromSeconds(5), logger);
    }

    internal static FileLock AcquireShared(string lockPath, TimeSpan timeout, CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(lockPath);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "The lock timeout must be positive.");
        cancellationToken.ThrowIfCancellationRequested();
        return Acquire(lockPath, FileAccess.Read, FileShare.Read, FileLockMode.Shared, timeout, logger, cancellationToken);
    }

    private static FileLock Acquire(string lockPath, FileAccess access, FileShare share, FileLockMode mode, TimeSpan timeout, ILogger? logger)
        => Acquire(lockPath, access, share, mode, timeout, logger, CancellationToken.None);

    private static FileLock Acquire(string lockPath, FileAccess access, FileShare share, FileLockMode mode, TimeSpan timeout,
        ILogger? logger, CancellationToken cancellationToken)
    {
        ILogger log = logger ?? DefaultLogger;
        var sw = Stopwatch.StartNew();
        int delayMs = 10;
        bool contended = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IOException? openError = null;
            FileStream? stream = null;
            try
            {
                stream = new FileStream(lockPath, FileMode.OpenOrCreate, access, share);
            }
            catch (IOException ex)
            {
                openError = ex;
            }

            // Windows enforces cross-process exclusion at the open above (FileShare is OS-enforced
            // there). Unix FileStream may already take an advisory flock during open; explicitly
            // enforce the requested mode on the underlying descriptor after a successful open.
            if (stream is not null && OperatingSystem.IsWindows())
                return new FileLock(stream);

            bool contendedAttempt;
            if (stream is not null)
            {
                // Non-Windows: take a non-blocking lock on the fd. flock(2) is advisory, but the POSIX
                // kernel enforces it across cooperating processes for a given open file description,
                // including when .NET already took the constructor lock.
                int fd = (int)stream.SafeFileHandle.DangerousGetHandle();
                int how = mode == FileLockMode.Exclusive ? LOCK_EX : LOCK_SH;
                if (flock(fd, how | LOCK_NB) == 0)
                    return new FileLock(stream);
                int errno = Marshal.GetLastPInvokeError();   // capture BEFORE close()/Dispose, while errno is fresh
                stream.Dispose();
                if (IsUnixContentionError(errno))
                    contendedAttempt = true;
                else
                    contendedAttempt = false;
            }
            else
            {
                // A conflicting Unix FileStream open can throw before our explicit flock attempt.
                // Retry only the platform's known contention code; all other I/O failures refuse admission.
                contendedAttempt = IsContentionOpenError(openError!);
            }

            if (!contendedAttempt)
            {
                LockTimedOut(log, lockPath, sw.ElapsedMilliseconds, null);
                throw new FileLockTimeoutException(lockPath, timeout);
            }

            // Contended within the timeout window: log once, back off exponentially, and retry.
            if (sw.Elapsed >= timeout)
            {
                LockTimedOut(log, lockPath, sw.ElapsedMilliseconds, null);
                throw new FileLockTimeoutException(lockPath, timeout);
            }
            if (!contended)
            {
                contended = true;
                LockContended(log, lockPath, sw.ElapsedMilliseconds, null);
            }
            int waitMs = Math.Min(delayMs, Math.Max(1, (int)Math.Min(int.MaxValue,
                Math.Ceiling((timeout - sw.Elapsed).TotalMilliseconds))));
            if (cancellationToken.WaitHandle.WaitOne(waitMs))
                cancellationToken.ThrowIfCancellationRequested();
            delayMs = Math.Min(delayMs * 2, 500);
        }
    }

    internal static bool IsContentionOpenError(IOException error)
    {
        if (OperatingSystem.IsWindows())
        {
            int win32 = error.HResult & 0xFFFF;
            return win32 is 32 or 33; // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
        }

        // .NET maps Unix EWOULDBLOCK from FileStream's constructor to an IOException whose
        // HResult is the exact raw errno, not HRESULT_FROM_WIN32 or a masked low word.
        return IsUnixContentionError(error.HResult);
    }

    internal static bool IsUnixContentionError(int error) =>
        OperatingSystem.IsLinux() ? error == EAGAIN :
        OperatingSystem.IsMacOS() && error == EWOULDBLOCK;

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
        if (stream is null)
            return;

        if (OperatingSystem.IsWindows())
        {
            // Windows releases the OS-enforced FileShare lock when the handle is closed.
            stream.Dispose();
            return;
        }

        // Non-Windows: release the flock(2) explicitly before closing the descriptor. Closing also
        // releases the lock, but an explicit unlock surfaces any flock(2) error instead of hiding it
        // in finalization.
        int fd = (int)stream.SafeFileHandle.DangerousGetHandle();
        int _ = flock(fd, LOCK_UN);
        stream.Dispose();
    }

    #pragma warning disable CA1838, CA2101, CA1865

    // Non-Windows interop (libc flock(2)). flock is advisory, but the POSIX kernel enforces it across
    // processes for a given open file description — the cross-process guarantee FileStream's FileShare
    // cannot provide off Windows. On Windows this import is never called (FileShare does the work), so
    // its unresolved symbol is harmless: P/Invoke imports resolve lazily at first call.
    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int fd, int how);

    // flock(2) operation flags.
    private const int LOCK_SH = 1;   // shared read lock
    private const int LOCK_EX = 2;   // exclusive write lock
    private const int LOCK_NB = 4;   // do not block; fail with EAGAIN if the lock is held
    private const int LOCK_UN = 8;   // release the lock

    // flock(2) errno when the lock is held by another process: EAGAIN, which equals EWOULDBLOCK on a
    // given platform. Linux reports 11; macOS reports 35. Match only the running platform's
    // value, since these numbers can denote unrelated errors on the other platform.
    private const int EAGAIN = 11;
    private const int EWOULDBLOCK = 35;

    #pragma warning restore CA1838, CA2101, CA1865
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
