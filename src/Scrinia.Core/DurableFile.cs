using System.Runtime.InteropServices;
using System.Text;
using Scrinia.Core.Resilience;

namespace Scrinia.Core;

/// <summary>
/// Replaces one file through a flushed sibling temporary file. The file contents are flushed, but
/// the containing directory is not: a power loss can retain the old file if the rename is lost.
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
        FileRetry.Run(() =>
        {
            if (OperatingSystem.IsWindows())
                SyncDirectoryWindows(directory);
            else
                SyncDirectoryUnix(directory);
        });
    }

    private static void SyncDirectoryWindows(string directory)
    {
        IntPtr handle = CreateFile(
            directory, GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle == (IntPtr)(-1))
        {
            int error = Marshal.GetLastWin32Error();
            // Directory vanished between the caller's existence check and this open (a race the
            // atomic move already won): nothing to persist, so treat the sync as satisfied.
            if (error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND)
                return;
            throw new IOException(
                $"Failed to open directory '{directory}' for fsync (Win32 error {error}).");
        }

        try
        {
            if (!FlushFileBuffers(handle))
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException(
                    $"Failed to flush directory '{directory}' (Win32 error {error}).");
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

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
