using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;

namespace Scrinia.Core;

/// <summary>Non-opening carrier classification shared by recovery evidence readers.</summary>
internal static class RecoveryFileType
{
    internal static void RequireRegularFile(string path)
    {
        Require((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0,
            "Recovery content is not a regular file.");
        if (OperatingSystem.IsWindows()) return;

        // System.Native normalizes stat layouts across Unix platforms/architectures.
        // ABI and mode constants: dotnet/runtime v10.0.0, src/native/libs/System.Native/pal_io.h
        // https://github.com/dotnet/runtime/blob/v10.0.0/src/native/libs/System.Native/pal_io.h
        try
        {
            if (LStat(path, out UnixFileStatus status) != 0)
                throw new IOException($"Cannot classify recovery content (errno {Marshal.GetLastPInvokeError()}).");
            const int fileTypeMask = 0xF000; // PAL_S_IFMT
            const int regularFile = 0x8000; // PAL_S_IFREG
            Require((status.Mode & fileTypeMask) == regularFile, "Recovery content is not a regular file.");
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or PlatformNotSupportedException)
        {
            throw new InvalidDataException("Regular-file classification is unavailable.", exception);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnixFileStatus
    {
        internal int Flags;
        internal int Mode;
        internal uint Uid;
        internal uint Gid;
        internal long Size;
        internal long ATime;
        internal long ATimeNsec;
        internal long MTime;
        internal long MTimeNsec;
        internal long CTime;
        internal long CTimeNsec;
        internal long BirthTime;
        internal long BirthTimeNsec;
        internal long Dev;
        internal long RDev;
        internal long Ino;
        internal uint UserFlags;
    }

    [DllImport("System.Native", EntryPoint = "SystemNative_LStat", SetLastError = true)]
    [SuppressMessage("Globalization", "CA2101", Justification = "System.Native paths use explicitly marshalled UTF-8, not UTF-16.")]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out UnixFileStatus status);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
