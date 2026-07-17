using Scrinia.Core.Process;
using Xunit;

namespace Scrinia.Tests;

public class ProcessRunnerCancellationTests
{
    [Fact]
    public async Task Cancellation_ReturnsTimedOut_NotThrow()
    {
        var runner = new ProcessRunner();
        // Note: ResolveExecutable's PATHEXT search appends extensions onto the given
        // name (e.g. "cmd.exe" + ".EXE" -> "cmd.exe.EXE"), so a name that already
        // carries its extension never resolves on Windows. Use the extension-less
        // "cmd" so the PATH search actually finds C:\Windows\System32\cmd.exe.
        (string exe, IReadOnlyList<string> args) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", "ping -n 20 127.0.0.1 >NUL" })
            : ("sh", new[] { "-c", "sleep 20" });
        if (runner.ResolveExecutable(exe) is null) return; // skip if unavailable

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var ex = await Record.ExceptionAsync(async () =>
        {
            var result = await runner.RunAsync(exe, args, stdin: null, cts.Token);
            Assert.True(result.TimedOut);
        });
        Assert.Null(ex); // BUG: currently throws OperationCanceledException from the read tasks
    }
}
