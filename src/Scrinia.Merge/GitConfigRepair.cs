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

        if (!TryGit(out string current, "config", "--local", "--get", key) ||
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
        if (!TryGit(out _, "config", "--local", key, repaired))
        {
            message = $"could not write {key} to local git config";
            return false;
        }

        message = $"updated {key} to pass %P";
        return true;
    }

    private static bool TryGit(out string stdout, params string[] arguments)
    {
        stdout = "";
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string arg in arguments)
                psi.ArgumentList.Add(arg);
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
