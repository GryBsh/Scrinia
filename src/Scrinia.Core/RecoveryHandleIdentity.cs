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

        if (OperatingSystem.IsWindows()) return TryGetWindowsPath(handle, out path);
        if (OperatingSystem.IsMacOS()) return TryGetMacPath(handle, out path);
        return OperatingSystem.IsLinux() && TryGetUnixPath(handle, out path);
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

    private static bool TryGetMacPath(SafeFileHandle handle, out string? path)
    {
        path = null;
        const int getPath = 50; // Darwin F_GETPATH; requires at least MAXPATHLEN (1024) bytes.
        byte[] buffer = new byte[4096];
        int result;
        try
        {
            result = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => FcntlGetPathArm64(handle, getPath, 0, 0, 0, 0, 0, 0, buffer),
                Architecture.X64 => FcntlGetPathX64(handle, getPath, buffer),
                _ => -1
            };
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or PlatformNotSupportedException)
        {
            return false;
        }

        if (result != 0) return false;
        int length = Array.IndexOf(buffer, (byte)0);
        if (length <= 0) return false;
        path = System.Text.Encoding.UTF8.GetString(buffer, 0, length);
        return Path.IsPathRooted(path);
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

    // F_GETPATH returns the opened descriptor's path, not FileStream.Name.
    // https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/fcntl.2.html
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int FcntlGetPathX64(SafeFileHandle handle, int command, [Out] byte[] buffer);

    // Darwin ARM64 puts variadic arguments on the stack. Occupy x2-x7 so the buffer
    // is the first stack argument; a three-argument declaration passes it in x2 instead.
    // Select the process architecture so Rosetta uses the x64 calling convention.
    // https://developer.apple.com/documentation/xcode/writing-arm64-code-for-apple-platforms
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int FcntlGetPathArm64(SafeFileHandle handle, int command,
        nint unusedX2, nint unusedX3, nint unusedX4, nint unusedX5, nint unusedX6, nint unusedX7,
        [Out] byte[] buffer);

    [DllImport("libc", SetLastError = true)]
    private static extern nint readlink(string path, byte[] buffer, nuint bufferSize);
}

#pragma warning restore CA1838, CA2101, CA1865
