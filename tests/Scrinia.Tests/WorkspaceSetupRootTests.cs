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

    /// <summary>
    /// Proves the AsyncLocal override actually isolates: while <see cref="WorkspaceSetup.Root"/>
    /// would otherwise return whatever the plain static <c>_root</c> field holds (set by
    /// <see cref="WorkspaceSetup.Configure"/>), an active <see cref="WorkspaceSetup.OverrideRoot"/>
    /// must win — this is what keeps two xUnit test classes that both call
    /// <c>Configure</c> under parallel execution from reading each other's root. Clearing
    /// the override must fall back to the <c>Configure</c>-cached value, not throw.
    /// </summary>
    [Fact]
    public void Root_OverrideRoot_TakesPrecedenceThenFallsBackWhenCleared()
    {
        string configuredDir = Path.Combine(Path.GetTempPath(), $"scrinia_root_configured_{Guid.NewGuid():N}");
        Directory.CreateDirectory(configuredDir);
        Directory.CreateDirectory(Path.Combine(configuredDir, ".git"));
        string overridePath = Path.Combine(Path.GetTempPath(), $"scrinia_root_override_{Guid.NewGuid():N}");
        try
        {
            WorkspaceSetup.Configure(configuredDir);
            string expectedConfigured = Path.GetFullPath(configuredDir);
            WorkspaceSetup.Root.Should().Be(expectedConfigured);

            WorkspaceSetup.OverrideRoot(overridePath);
            try
            {
                // _root (set by Configure above) still holds expectedConfigured here —
                // the override must be read first, proving the two don't collide.
                WorkspaceSetup.Root.Should().Be(overridePath);
                WorkspaceSetup.Root.Should().NotBe(expectedConfigured);
            }
            finally
            {
                WorkspaceSetup.OverrideRoot(null);
            }

            WorkspaceSetup.Root.Should().Be(expectedConfigured);
        }
        finally
        {
            try { Directory.Delete(configuredDir, recursive: true); } catch { }
        }
    }
}
