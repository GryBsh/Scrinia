using Scrinia.TestDoubles;

namespace Scrinia.Tests.Shared;

public sealed class SharedSpecialFileTests
{
    [UnixTheory]
    [InlineData("fifo", "cold")]
    [InlineData("fifo", "cached")]
    [InlineData("socket", "cold")]
    [InlineData("socket", "cached")]
    public async Task Special_carriers_are_refused_without_blocking_healthy_bases(string carrier, string mode)
    {
        string root = Path.Combine(Path.GetTempPath(), "scri-carrier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var child = await TestProcessRunner.StartAsync(["shared-carrier", root, carrier, mode], CancellationToken.None);
            await child.ReadEventAsync("carrier-ready", CancellationToken.None);
            // The harness owns and terminates this child after 30 seconds even when FIFO open hangs.
            await child.ReadEventAsync("verified", CancellationToken.None);
            Assert.Equal(0, await child.WaitForExitAsync(CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class UnixTheoryAttribute : TheoryAttribute
    {
        public UnixTheoryAttribute()
        { if (OperatingSystem.IsWindows()) Skip = "Unix FIFO/socket classification requires a native Unix host."; }
    }
}
