using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

public sealed class SharedEnableTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "scrinia-task7-enable-home", Guid.NewGuid().ToString("N"));
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "scrinia-task7-enable-workspace", Guid.NewGuid().ToString("N"));

    [Fact]
    public void EnableAndDisable_AreIdempotentAndPreserveOrder()
    {
        var registry = new SharedBaseRegistry(_home, _workspace);
        var operations = new SharedBaseOperations(registry, _home, _workspace);
        string first = CreateGitRoot("first");
        string second = CreateGitRoot("second");
        operations.Add("first", first).Succeeded.Should().BeTrue();
        operations.Add("second", second).Succeeded.Should().BeTrue();

        operations.Enable("second").Succeeded.Should().BeTrue();
        operations.Enable("first").Succeeded.Should().BeTrue();
        operations.Enable("first").Succeeded.Should().BeTrue();
        WorkspaceConfig.Load(_workspace).GetEnabledBases().Should().Equal("second", "first");
        operations.Disable("second").Succeeded.Should().BeTrue();
        operations.Disable("second").Succeeded.Should().BeTrue();
        operations.Disable("first").Succeeded.Should().BeTrue();
        WorkspaceConfig.Load(_workspace).GetValue(WorkspaceConfig.EnabledKey).Should().BeNull();
    }

    private string CreateGitRoot(string name)
    {
        string root = Path.Combine(_home, name);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        return root;
    }

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }
}
