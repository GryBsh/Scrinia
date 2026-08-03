using FluentAssertions;
using Scrinia.Commands;

namespace Scrinia.Tests;

public sealed class WorkspaceSetupRootTests
{
    [Fact]
    public void Root_AfterConfigure_ReturnsResolvedRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"scrinia_root_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        try
        {
            WorkspaceSetup.Configure(dir);
            WorkspaceSetup.Root.Should().Be(Path.GetFullPath(dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
