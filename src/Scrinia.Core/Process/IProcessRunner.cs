namespace Scrinia.Core.Process;

/// <summary>
/// Thin testable abstraction over <see cref="System.Diagnostics.Process"/>. Models a
/// one-shot "run a command, capture stdout/stderr, get the exit code" interaction —
/// no long-lived sessions, no streaming, no stdin piping after launch. Production
/// impl is <see cref="ProcessRunner"/>; tests inject a fake that returns canned
/// <see cref="ProcessResult"/> values.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Launch <paramref name="executable"/> with <paramref name="arguments"/>, optionally
    /// writing <paramref name="stdin"/> to its standard input, then wait for exit while
    /// <paramref name="ct"/> remains active. Cancellation kills the process. Returns the
    /// captured outputs and exit code; throws only for "couldn't even start the process"
    /// failures (missing executable, OS-level deny, etc.).
    /// </summary>
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? stdin,
        CancellationToken ct);

    /// <summary>
    /// Cheap probe: does <paramref name="executable"/> resolve to an actual file on PATH?
    /// Used by callers that want to decide between providers before starting a process.
    /// </summary>
    bool IsExecutableOnPath(string executable);

    /// <summary>
    /// Resolve <paramref name="executable"/> to its full path by scanning PATH (and
    /// PATHEXT on Windows). Returns null when not found. Necessary on Windows because
    /// <c>Process.Start</c> with <c>UseShellExecute=false</c> doesn't apply PATHEXT, so
    /// shimmed CLIs (<c>claude.cmd</c>, <c>codex.cmd</c>) won't launch by bare name.
    /// </summary>
    string? ResolveExecutable(string executable);
}

/// <summary>Captured outputs of a one-shot process invocation.</summary>
/// <param name="ExitCode">Process exit code; non-zero typically indicates failure.</param>
/// <param name="Stdout">Captured standard output (UTF-8 decoded).</param>
/// <param name="Stderr">Captured standard error (UTF-8 decoded).</param>
/// <param name="TimedOut">True if the run was terminated by the supplied <c>CancellationToken</c>.</param>
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);
