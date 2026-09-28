using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Scrinia.Core.Resilience;

namespace Scrinia.Core;

/// <summary>
/// Replaces one file through a flushed sibling temporary file. Both the file contents and the
/// containing directory are fsync'd (via SyncDirectory) for crash durability, so a completed
/// AtomicWrite is durable across a power loss.
/// </summary>
internal static class DurableFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly AsyncLocal<Func<DurableWritePhase, string, Exception?>?> FailureInjectorContext = new();

    /// <summary>Test-only failure injection seam scoped to the current asynchronous flow.</summary>
    internal static Func<DurableWritePhase, string, Exception?>? FailureInjector
    {
        get => FailureInjectorContext.Value;
        set => FailureInjectorContext.Value = value;
    }

    /// <summary>Raises an append-specific injected failure at a deterministic durable boundary.</summary>
    internal static void ThrowAppendFault(AppendFaultPoint point, string operationId, string path)
    {
        Exception? failure = FileMemoryStore.InjectAppendFault(point, operationId, path);
        if (failure is not null)
            throw failure;
    }

    internal static void AtomicWriteAllText(string path, string content)
    {
        byte[] bytes;

        try
        {
            bytes = Utf8NoBom.GetBytes(content);
        }
        catch (Exception exception)
        {
            throw new DurableWriteException("write text", path, exception);
        }

        AtomicWrite(path, bytes, "write text");
    }

    internal static void AtomicWriteAllBytes(string path, ReadOnlySpan<byte> content) =>
        AtomicWrite(path, content, "write bytes");

    internal static void AtomicWriteAllTextForAppend(
        string path, string content, string operationId,
        AppendFaultPoint writePoint, AppendFaultPoint flushPoint) =>
        AtomicWrite(path, Utf8NoBom.GetBytes(content), "write text", operationId, writePoint, flushPoint);

    internal static void AtomicWriteAllBytesForAppend(
        string path, ReadOnlySpan<byte> content, string operationId,
        AppendFaultPoint writePoint, AppendFaultPoint flushPoint) =>
        AtomicWrite(path, content, "write bytes", operationId, writePoint, flushPoint);

    private static void AtomicWrite(string path, ReadOnlySpan<byte> content, string operation)
        => AtomicWrite(path, content, operation, null, null, null);

    private static void AtomicWrite(
        string path, ReadOnlySpan<byte> content, string operation, string? operationId,
        AppendFaultPoint? writePoint, AppendFaultPoint? flushPoint)
    {
        byte[] bytes = content.ToArray();
        string? temporaryPath = null;

        try
        {
            temporaryPath = FileRetry.Run(() =>
            {
                string candidatePath = CreateTemporaryPath(path);
                bool temporaryFileCreated = false;

                try
                {
                    using var stream = new FileStream(candidatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    temporaryFileCreated = true;
                    WriteTemporaryFile(stream, bytes, path, operationId, writePoint, flushPoint);
                    return candidatePath;
                }
                catch
                {
                    if (temporaryFileCreated)
                        TryDeleteTemporaryFile(candidatePath);
                    throw;
                }
            });
            FileRetry.Run(() =>
            {
                ThrowInjected(DurableWritePhase.BeforeMove, path);
                File.Move(temporaryPath!, path, overwrite: true);
            });
            SyncDirectory(path);
        }
        catch (Exception exception)
        {
            if (temporaryPath is not null)
                TryDeleteTemporaryFile(temporaryPath);
            throw new DurableWriteException(operation, path, exception);
        }
    }

    private static string CreateTemporaryPath(string destination)
    {
        string directory = Path.GetDirectoryName(destination) ?? ".";
        string shortRandomName = Path.GetFileNameWithoutExtension(Path.GetRandomFileName());
        return Path.Combine(directory, shortRandomName + ".tmp");
    }

    private static void WriteTemporaryFile(
        FileStream stream, byte[] bytes, string destination, string? operationId = null,
        AppendFaultPoint? writePoint = null, AppendFaultPoint? flushPoint = null)
    {
        if (operationId is not null && writePoint is not null)
            ThrowAppendFault(writePoint.Value, operationId, destination);
        ThrowInjected(DurableWritePhase.BeforeWrite, destination);

        stream.Write(bytes);

        if (operationId is not null && flushPoint is not null)
            ThrowAppendFault(flushPoint.Value, operationId, destination);
        ThrowInjected(DurableWritePhase.BeforeFlush, destination);
        stream.Flush(flushToDisk: true);
    }

    private static void SyncDirectory(string destination)
    {
        string? directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory))
            directory = ".";
        ThrowInjected(DurableWritePhase.BeforeDirSync, directory);

        if (OperatingSystem.IsWindows())
            SyncDirectoryWindows(directory);
        else
            SyncDirectoryUnix(directory);
    }

    private static void SyncDirectoryWindows(string directory)
    {
        // Advertise the full standard share set so the directory opens concurrently with any other
        // holder (parallel test classes, a parent process, antivirus, etc.). Sharing nothing made a
        // busy Temp directory fail with ERROR_SHARING_VIOLATION on the first parallel run.
        const uint fullShareMode =
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE | FILE_SHARE_EXECUTE;

        // Read/write/delete is the canonical directory-fsync share set (it is what the major runtimes
        // advertise). FILE_SHARE_EXECUTE is not required to open the directory concurrently.
        const uint standardShareMode = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;

        // shareMode lives outside the loop so the INVALID_PARAMETER fallback below sticks on the next
        // attempt instead of being reset to the full set every iteration (which would loop forever).
        uint shareMode = fullShareMode;
        for (int attempt = 0; ; attempt++)
        {
            IntPtr handle = CreateFile(
                directory, GENERIC_WRITE, shareMode, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

            if (handle != (IntPtr)(-1))
            {
                bool flushed = FlushFileBuffers(handle);
                int flushError = Marshal.GetLastWin32Error();
                CloseHandle(handle);
                if (flushed)
                    return;
                throw new IOException(
                    $"Failed to flush directory '{directory}' (Win32 error {flushError}).");
            }

            int error = Marshal.GetLastWin32Error();
            // Directory vanished between the atomic move and this open: the move already won, so the
            // rename is durable enough to treat the sync as satisfied.
            if (error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND)
                return;

            // FILE_SHARE_EXECUTE (0x10) is rejected with ERROR_INVALID_PARAMETER on some builds/volumes
            // when opening a directory. Fall back to the canonical read/write/delete set once and let
            // the bounded sharing-violation backoff below handle any remaining contention.
            if (error is ERROR_INVALID_PARAMETER && shareMode is fullShareMode)
            {
                shareMode = standardShareMode;
                continue;
            }

            // A sharing violation is a transient lock held by a process that opened the directory with
            // a more restrictive share than ours; back off and retry for a bounded window before
            // giving up so a briefly-contended directory doesn't fail a durable write.
            if (error is ERROR_SHARING_VIOLATION && attempt < DirectoryShareRetries.Length - 1)
            {
                Thread.Sleep(DirectoryShareRetries[attempt]);
                continue;
            }

            throw new IOException(
                $"Failed to open directory '{directory}' for fsync (Win32 error {error}).");
        }
    }

    // Bounded backoff for a directory briefly held open by another process. Total ~7.3s absorbs
    // short-lived locks without pretending a permanent failure will ever resolve.
    private static readonly TimeSpan[] DirectoryShareRetries =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800),
        TimeSpan.FromMilliseconds(1600),
        TimeSpan.FromMilliseconds(1600),
        TimeSpan.FromMilliseconds(1600),
    ];

    private static void SyncDirectoryUnix(string directory)
    {
        int fd = open(directory, O_RDONLY);
        if (fd == -1)
        {
            int errno = Marshal.GetLastPInvokeError();
            // Same not-found race as the Windows path: tolerate it.
            if (errno is ENOENT or ENOTDIR)
                return;
            throw new IOException(
                $"Failed to open directory '{directory}' for fsync (errno {errno}).");
        }

        try
        {
            if (fsync(fd) != 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw new IOException(
                    $"Failed to flush directory '{directory}' (errno {errno}).");
            }
        }
        finally
        {
            int _ = close(fd);
        }
    }

    #pragma warning disable CA1838, CA2101, CA1865

    // Windows interop (kernel32). FILE_FLAG_BACKUP_SEMANTICS is required to open a directory handle;
    // GENERIC_WRITE is required so FlushFileBuffers can flush the directory's entries (a read-only
    // handle fails with access denied).
    private const uint GENERIC_WRITE = 0x4000_0000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x0200_0000;
    private const uint OPEN_EXISTING = 3;
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_INVALID_PARAMETER = 87;
    private const uint FILE_SHARE_READ = 0x0000_0001;
    private const uint FILE_SHARE_WRITE = 0x0000_0002;
    private const uint FILE_SHARE_DELETE = 0x0000_0004;
    private const uint FILE_SHARE_EXECUTE = 0x0000_0010;
    private const int ERROR_SHARING_VIOLATION = 32;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(IntPtr hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    // POSIX interop (libc). O_RDONLY is used in place of O_DIRECTORY: AtomicWrite only ever passes a
    // real directory, and O_DIRECTORY's constant differs between Linux (0x10000) and macOS (0x100000),
    // which a two-way IsWindows() dispatch cannot represent. fsync on a directory fd flushes entries.
    private const int O_RDONLY = 0;
    private const int ENOENT = 2;
    private const int ENOTDIR = 4;

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    #pragma warning restore CA1838, CA2101, CA1865

    private static void ThrowInjected(DurableWritePhase phase, string destination)
    {
        Exception? failure = B0FaultSeam.ThrowIfInjectedOrNull(B0FaultSeam.MapDurableWritePhase(phase), destination);
        if (failure is not null)
            throw failure;
        failure = FailureInjector?.Invoke(phase, destination);
        if (failure is not null)
            throw failure;
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch
        {
            // Cleanup must not hide the original write failure.
        }
    }
}

internal enum DurableWritePhase
{
    BeforeWrite,
    BeforeFlush,
    BeforeMove,
    BeforeDirSync
}
