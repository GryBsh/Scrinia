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

/// <summary>
/// Regression coverage for the PATHEXT-appending bug: on Windows, an already
/// extension-qualified name (e.g. "cmd.exe") used to only ever be probed as
/// "cmd.exe.EXE" etc., so it never resolved. These tests mutate the process-wide
/// PATH env var to point at a hermetic temp directory, so they run in their own
/// non-parallel collection (see <see cref="ProcessRunnerResolveExtensionCollection"/>)
/// to avoid racing other tests that resolve real PATH entries concurrently.
/// </summary>
[Collection(ProcessRunnerResolveExtensionCollection.Name)]
public class ProcessRunnerResolveExtensionTests : IDisposable
{
    private static readonly bool IsWindows = OperatingSystem.IsWindows();

    private readonly string _tempDir;
    private readonly string? _originalPath;

    public ProcessRunnerResolveExtensionTests()
    {
        _tempDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "scrinia-resolve-exe-" + Guid.NewGuid().ToString("N"))).FullName;

        string fakeExeName = IsWindows ? "tool.exe" : "tool";
        File.WriteAllText(Path.Combine(_tempDir, fakeExeName), "#!/bin/sh\necho fake\n");

        _originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", _tempDir + Path.PathSeparator + _originalPath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort cleanup */ }
    }

    [Fact]
    public void ResolveExecutable_FindsAlreadyExtensionQualifiedName()
    {
        var runner = new ProcessRunner();

        if (!IsWindows)
        {
            // Non-Windows: extensions aren't a concept; confirm the bare name still
            // resolves so this test remains meaningful cross-platform.
            runner.ResolveExecutable("tool").Should().Be(Path.Combine(_tempDir, "tool"));
            return;
        }

        // This is the regression: pre-fix, "tool.exe" only ever probed
        // "tool.exe.COM", "tool.exe.EXE", etc. and never found the file.
        runner.ResolveExecutable("tool.exe").Should().Be(Path.Combine(_tempDir, "tool.exe"));

        // Bare-name resolution must keep working (PATHEXT append path, unaffected by the
        // fix). Case-insensitive: the resolved extension casing comes from the PATHEXT
        // env var (e.g. ".EXE"), not from the literal file name on disk.
        runner.ResolveExecutable("tool").Should().BeEquivalentTo(Path.Combine(_tempDir, "tool.exe"));
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessRunnerResolveExtensionCollection
{
    public const string Name = "ProcessRunnerResolveExtension";
}
