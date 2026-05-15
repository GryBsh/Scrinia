using System.Diagnostics;

namespace Scrinia.Core.Process;

/// <summary>
/// Default <see cref="IProcessRunner"/> backed by <see cref="System.Diagnostics.Process"/>.
/// Captures stdout + stderr into in-memory strings (callers know their CLIs produce
/// bounded output — we're not running tail -f). Cancellation kills the process; the
/// resulting <see cref="ProcessResult"/> has <c>TimedOut = true</c> so callers can
/// distinguish cancellation from genuine non-zero exit.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? stdin,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {executable}");

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        // Read both streams concurrently to avoid deadlock when a child fills one buffer.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { /* race with natural exit */ }
        }

        // Don't propagate cancellation from the stream reads — if the process is dead,
        // ReadToEnd resolves to whatever was already written. CancellationToken.None for
        // the await guards against the test scenario where the caller's token is
        // already cancelled.
        string stdout = await stdoutTask.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        string stderr = await stderrTask.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        return new ProcessResult(process.ExitCode, stdout, stderr, timedOut);
    }

    public bool IsExecutableOnPath(string executable) => ResolveExecutable(executable) is not null;

    public string? ResolveExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;

        // Absolute or relative path with directory component → trust it as-is.
        if (executable.Contains(Path.DirectorySeparatorChar) ||
            executable.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(executable) ? executable : null;
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null) return null;

        string[] extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string ext in extensions)
            {
                string candidate = Path.Combine(dir, executable + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
