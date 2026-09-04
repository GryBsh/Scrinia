using FluentAssertions;
using Scrinia.Commands;
using CoreWorkspaceConfig = Scrinia.Core.Configuration.WorkspaceConfig;

namespace Scrinia.Tests;

[Collection(TrackingPrecedenceCollection.Name)]
public sealed class NoSharedEnabledEnvTests : IDisposable
{
    private const string Key = "Scrinia:Shared:Enabled";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"scrinia_enabled_{Guid.NewGuid():N}");

    public NoSharedEnabledEnvTests()
    {
        Directory.CreateDirectory(_root);
        WorkspaceSetup.OverrideRoot(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SCRINIA_SHARED_ENABLED", null);
        Environment.SetEnvironmentVariable("SCRINIA__SHARED__ENABLED", null);
        WorkspaceSetup.OverrideRoot(null);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void SharedEnabledEnvironmentVariablesCannotAlterEnabled()
    {
        WorkspaceConfig.SetValue(_root, Key, "team");
        Environment.SetEnvironmentVariable("SCRINIA_SHARED_ENABLED", "environment");
        Environment.SetEnvironmentVariable("SCRINIA__SHARED__ENABLED", "double-environment");

        CoreWorkspaceConfig.ParseEnabled(WorkspaceConfig.GetValue(_root, Key)).Should().Equal("team");
        WorkspaceSetup.GetConfigValue(Key).Should().Be("team",
            because: "Enabled is read directly from the flat workspace dictionary");
    }
}
