using FluentAssertions;
using Scrinia.Core.Process;

namespace Scrinia.Tests.Process;

/// <summary>
/// Smoke tests for the real <see cref="ProcessRunner"/>. Targets a tiny system binary
/// (`cmd /c echo` on Windows, `/bin/echo` on Unix) so the suite doesn't depend on a
/// long-running or networked CLI.
/// </summary>
public class ProcessRunnerTests
{
    private static readonly bool IsWindows = OperatingSystem.IsWindows();

    [Fact]
    public async Task RunAsync_CapturesStdout_FromSimpleEcho()
    {
        var runner = new ProcessRunner();

        ProcessResult result;
        if (IsWindows)
            result = await runner.RunAsync("cmd.exe", ["/c", "echo", "hello"], null, CancellationToken.None);
        else
            result = await runner.RunAsync("/bin/echo", ["hello"], null, CancellationToken.None);

        result.ExitCode.Should().Be(0);
        result.Stdout.Trim().Should().Be("hello");
        result.TimedOut.Should().BeFalse();
    }

    [Fact]
    public void ResolveExecutable_FindsSystemBinary()
    {
        var runner = new ProcessRunner();
        string sentinel = IsWindows ? "cmd" : "sh";

        string? resolved = runner.ResolveExecutable(sentinel);

        resolved.Should().NotBeNullOrEmpty();
        File.Exists(resolved).Should().BeTrue();
    }

    [Fact]
    public void ResolveExecutable_ReturnsNullForMissing()
    {
        var runner = new ProcessRunner();

        runner.ResolveExecutable("this-binary-definitely-does-not-exist-2026").Should().BeNull();
    }

    [Fact]
    public void IsExecutableOnPath_AgreesWithResolve()
    {
        var runner = new ProcessRunner();
        string sentinel = IsWindows ? "cmd" : "sh";

        runner.IsExecutableOnPath(sentinel).Should().BeTrue();
        runner.IsExecutableOnPath("missing-binary-2026").Should().BeFalse();
    }
}
