using System.Diagnostics;

namespace Scrinia.TestDoubles;

/// <summary>
/// A temp directory whose filesystem distinguishes "Foo" from "foo" — Linux/macOS-like semantics,
/// obtainable on Windows because NTFS supports the case-sensitivity flag per directory.
///
/// <para>This exists because a whole class of this codebase's name-handling defects is invisible on
/// a case-insensitive filesystem. Five behaviours were documented as unpinnable on Windows and
/// shipped with ruling comments instead of tests; they are pinnable, and this is how.</para>
///
/// <para><b>Returns null rather than throwing when the flag cannot be obtained</b> (a non-NTFS
/// volume, a policy that blocks fsutil, a case-insensitive macOS volume). Callers must SKIP in that
/// case, never pass — a test that silently no-ops on the platform it was written for is worse than
/// no test, and is exactly the failure this class exists to correct.</para>
///
/// <para>The flag is inherited by subdirectories created after it is set, so a store rooted here is
/// case-sensitive all the way down.</para>
/// </summary>
public sealed class CaseSensitiveDirectory : IDisposable
{
    public string Path { get; }

    private CaseSensitiveDirectory(string path) => Path = path;

    /// <summary>Creates a case-sensitive temp directory, or returns null if this machine cannot
    /// provide one. Never throws for the "not supported here" case.</summary>
    public static CaseSensitiveDirectory? TryCreate(string prefix)
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);

        if (OperatingSystem.IsWindows() && !TryEnableOnWindows(path))
        {
            TryDelete(path);
            return null;
        }

        // The probe is the authority, not the platform or fsutil's exit code. macOS is
        // case-INsensitive by default, and a filesystem can ignore the flag silently.
        if (!IsCaseSensitive(path))
        {
            TryDelete(path);
            return null;
        }

        return new CaseSensitiveDirectory(path);
    }

    private static bool TryEnableOnWindows(string path)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("fsutil.exe")
            {
                ArgumentList = { "file", "setCaseSensitiveInfo", path, "enable" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return false;
            proc.WaitForExit(30_000);
            if (!proc.HasExited) { try { proc.Kill(entireProcessTree: true); } catch { } }
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch
        {
            return false; // fsutil absent or blocked — the caller skips
        }
    }

    private static bool IsCaseSensitive(string path)
    {
        string upper = System.IO.Path.Combine(path, "CaseProbe.tmp");
        string lower = System.IO.Path.Combine(path, "caseprobe.tmp");
        try
        {
            File.WriteAllText(upper, "u");
            bool sensitive = !File.Exists(lower);
            File.Delete(upper);
            return sensitive;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    public void Dispose() => TryDelete(Path);
}
