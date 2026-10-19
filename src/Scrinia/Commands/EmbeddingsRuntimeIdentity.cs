using System.Runtime.InteropServices;

namespace Scrinia.Commands;

/// <summary>Maps demonstrated runtime identities to the exact approved bundle ABI.</summary>
internal static class EmbeddingsRuntimeIdentity
{
    internal static string RawRid => RuntimeInformation.RuntimeIdentifier;

    internal static string CurrentRid => ResolveSupportedRid(RawRid,
        OperatingSystem.IsWindows() ? OSPlatform.Windows :
        OperatingSystem.IsMacOS() ? OSPlatform.OSX :
        OperatingSystem.IsLinux() ? OSPlatform.Linux : OSPlatform.Create("UNSUPPORTED"),
        RuntimeInformation.ProcessArchitecture, OperatingSystem.IsLinux() && HasGnuLibc()) ?? RawRid;

    internal static string? ResolveSupportedRid(string rawRid, OSPlatform platform,
        Architecture processArchitecture, bool hasGnuLibc)
    {
        if (platform == OSPlatform.Windows && processArchitecture == Architecture.X64 && rawRid == "win-x64")
            return "win-x64";
        if (platform == OSPlatform.OSX && processArchitecture == Architecture.Arm64 && rawRid == "osx-arm64")
            return "osx-arm64";
        if (platform != OSPlatform.Linux || processArchitecture != Architecture.X64) return null;
        if (rawRid == "linux-x64") return "linux-x64";
        // This distro-built runtime is independently demonstrated in the native setup lane.
        // Unknown distro identities, musl and Bionic remain unsupported; no prefix guessing.
        return rawRid == "ubuntu.24.04-x64" && hasGnuLibc ? "linux-x64" : null;
    }

    private static bool HasGnuLibc()
    {
        if (!NativeLibrary.TryLoad("libc.so.6", out nint library)) return false;
        try { return NativeLibrary.TryGetExport(library, "gnu_get_libc_version", out _); }
        finally { NativeLibrary.Free(library); }
    }
}
