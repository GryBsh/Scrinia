using Scrinia.Core.Process;

namespace Scrinia.Tests.Hooks;

/// <summary>
/// Test <see cref="IProcessRunner"/> with per-executable PATH control — construct with the set of
/// executable names that should report as "on PATH". Used by the artifact-writer tests to simulate
/// which agent CLIs are present (e.g. only <c>opencode</c>, or only <c>claude</c>).
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly HashSet<string> _onPath;

    public FakeProcessRunner(params string[] onPath) =>
        _onPath = new HashSet<string>(onPath, StringComparer.OrdinalIgnoreCase);

    public bool IsExecutableOnPath(string executable) => _onPath.Contains(executable);

    public string? ResolveExecutable(string executable) =>
        _onPath.Contains(executable) ? $"/fake/{executable}" : null;

    public Task<ProcessResult> RunAsync(
        string executable, IReadOnlyList<string> args, string? stdin, CancellationToken ct) =>
        Task.FromResult(new ProcessResult(0, "", "", false));
}
