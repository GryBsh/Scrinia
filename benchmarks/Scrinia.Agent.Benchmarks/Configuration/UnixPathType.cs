using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Scrinia.Agent.Benchmarks;

internal static class UnixPathType
{
    internal static void RequireRegularOrDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        // System.Native normalizes stat across Unix platforms. ABI: dotnet/runtime
        // v10.0.0 src/native/libs/System.Native/pal_io.h (same runtime contract as Core).
        try
        {
            if (LStat(path, out var status) != 0)
                throw new IOException("Cannot classify a benchmark path.");
            if ((status.Mode & 0xF000) is not (0x8000 or 0x4000))
                throw InputDiagnostics.Invalid("Links and nonregular entries are not supported in benchmark paths.");
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException)
        {
            throw InputDiagnostics.Mark(new InvalidDataException("Path classification is unavailable.", error), "Regular-file classification is unavailable on this platform.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnixFileStatus
    {
        internal int Flags, Mode;
        internal uint Uid, Gid;
        internal long Size, ATime, ATimeNsec, MTime, MTimeNsec, CTime, CTimeNsec,
            BirthTime, BirthTimeNsec, Dev, RDev, Ino;
        internal uint UserFlags;
    }

    [DllImport("System.Native", EntryPoint = "SystemNative_LStat", SetLastError = true)]
    [SuppressMessage("Globalization", "CA2101", Justification = "System.Native paths are explicitly marshalled as UTF-8.")]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out UnixFileStatus status);
}
