using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace Scrinia.Core;

#pragma warning disable CA1838, CA2101, CA1865

internal readonly record struct RecoveryHandleIdentity(string CheckedPath, string OpenedPath, bool RegularFile);

internal static class RecoveryHandleIdentityVerifier
{
    internal static bool IsSafe(RecoveryHandleIdentity identity, string root)
    {
        if (!identity.RegularFile || !Path.IsPathRooted(identity.CheckedPath) || !Path.IsPathRooted(identity.OpenedPath))
            return false;

        string checkedPath = Path.GetFullPath(identity.CheckedPath);
        string openedPath = Path.GetFullPath(identity.OpenedPath);
        StringComparison comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return string.Equals(checkedPath, openedPath, comparison) && PathContainment.IsWithin(root, openedPath);
    }

    internal static bool TryCapture(FileStream stream, string checkedPath, string root, out RecoveryHandleIdentity identity)
    {
        identity = default;
        if (!TryGetOpenedPath(stream.SafeFileHandle, out string? openedPath) || openedPath is null)
            return false;

        bool regularFile;
        try
        {
            regularFile = (File.GetAttributes(openedPath) & FileAttributes.ReparsePoint) == 0 &&
                !Directory.Exists(openedPath);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        identity = new RecoveryHandleIdentity(checkedPath, openedPath, regularFile);
        return IsSafe(identity, root);
    }

    private static bool TryGetOpenedPath(SafeFileHandle handle, out string? path)
    {
        path = null;
        if (handle.IsInvalid || handle.IsClosed)
            return false;

        return OperatingSystem.IsWindows()
            ? TryGetWindowsPath(handle, out path)
            : TryGetUnixPath(handle, out path);
    }

    private static bool TryGetWindowsPath(SafeFileHandle handle, out string? path)
    {
        path = null;
        var buffer = new StringBuilder(512);
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0) return false;
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) return false;
        }

        path = buffer.ToString(0, checked((int)length));
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) path = path[4..];
        return !path.EndsWith("\\", StringComparison.Ordinal) || path.Length <= 3;
    }

    private static bool TryGetUnixPath(SafeFileHandle handle, out string? path)
    {
        path = null;
        int descriptor = checked((int)handle.DangerousGetHandle());
        byte[] buffer = new byte[4096];
        long length = readlink($"/proc/self/fd/{descriptor}", buffer, (nuint)buffer.Length);
        if (length <= 0 || length >= buffer.Length) return false;
        path = System.Text.Encoding.UTF8.GetString(buffer, 0, checked((int)length));
        return !path.EndsWith(" (deleted)", StringComparison.Ordinal);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("libc", SetLastError = true)]
    private static extern nint readlink(string path, byte[] buffer, nuint bufferSize);
}

#pragma warning restore CA1838, CA2101, CA1865
