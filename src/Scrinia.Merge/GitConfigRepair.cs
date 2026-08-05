using System.Diagnostics;

namespace Scrinia.Merge;

/// <summary>
/// Repairs a driver registration made before %P was passed. Scrinia.Merge is a standalone
/// AOT executable with no Scrinia.Core dependency, so it shells out to git directly.
/// </summary>
public static class GitConfigRepair
{
    /// <summary>
    /// Appends %P to merge.scrinia-{kind}.driver in LOCAL repository config. Never writes
    /// global config: a merge driver must not alter a user's global git state.
    /// </summary>
    public static bool TryAppendPathname(string kind, out string message)
    {
        string key = $"merge.scrinia-{kind}.driver";

        if (!TryGit($"config --local --get {key}", out string current) ||
            string.IsNullOrWhiteSpace(current))
        {
            message = $"could not read {key} from local git config";
            return false;
        }

        current = current.Trim();
        if (current.Contains("%P", StringComparison.Ordinal))
        {
            message = $"{key} already passes %P";
            return true;
        }

        string repaired = current + " %P";
        if (!TryGit($"config --local {key} \"{repaired}\"", out _))
        {
            message = $"could not write {key} to local git config";
            return false;
        }

        message = $"updated {key} to pass %P";
        return true;
    }

    private static bool TryGit(string arguments, out string stdout)
    {
        stdout = "";
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
