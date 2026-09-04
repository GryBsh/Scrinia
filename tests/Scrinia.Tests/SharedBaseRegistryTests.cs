using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

public sealed class SharedBaseRegistryTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "scrinia-registry-tests", Guid.NewGuid().ToString("N"));
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "scrinia-registry-workspace", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("team", true)]
    [InlineData("a", true)]
    [InlineData("base-01", true)]
    [InlineData("Team", false)]
    [InlineData("1team", false)]
    [InlineData("team_name", false)]
    [InlineData("", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz1234567", false)]
    public void CanonicalNameValidation_FollowsContract(string name, bool expected)
    {
        SharedBaseRegistry.IsCanonicalName(name).Should().Be(expected);
    }

    [Fact]
    public void Add_RequiresTheRootItselfToContainGitEvidence()
    {
        string root = Path.Combine(_home, "not-a-repo");
        Directory.CreateDirectory(root);
        var registry = new SharedBaseRegistry(_home, _workspace);

        SharedBaseResult result = registry.Add("team", root);

        result.ErrorCode.Should().Be(ConfigurationErrorCode.SharedBaseNotGitRoot);
        registry.Lookup("team").Should().BeNull();
    }

    [Fact]
    public void Add_AcceptsDirectoryGitEvidenceAndRejectsWorkspaceContainment()
    {
        string root = Path.Combine(_workspace, "repo");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var registry = new SharedBaseRegistry(_home, _workspace);

        SharedBaseResult result = registry.Add("team", root);

        result.ErrorCode.Should().Be(ConfigurationErrorCode.SharedBaseRootCollision);
    }

    [Fact]
    public void Add_StoresFixedProjectGitBaseAndListHidesDisabledEntries()
    {
        string root = CreateGitRoot("repo");
        var registry = new SharedBaseRegistry(_home, Path.Combine(Path.GetTempPath(), "outside-workspace", Guid.NewGuid().ToString("N")));

        registry.Add("team", root).Succeeded.Should().BeTrue();
        registry.List(includeDisabled: false).Should().BeEmpty();
        registry.Enable("team").Succeeded.Should().BeTrue();

        SharedBaseEntry entry = registry.Lookup("team")!;
        entry.Root.Should().Be(Path.GetFullPath(root));
        entry.Tracking.Should().Be(TrackingPolicy.ProjectGit);
        registry.List(includeDisabled: false).Should().ContainSingle(x => x.Name == "team");
    }

    [Fact]
    public void Add_RejectsEquivalentAndNestedRegisteredRoots()
    {
        string root = CreateGitRoot("repo");
        string nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(Path.Combine(nested, ".git"));
        var registry = new SharedBaseRegistry(_home, Path.Combine(Path.GetTempPath(), "outside-workspace", Guid.NewGuid().ToString("N")));

        registry.Add("team", root).Succeeded.Should().BeTrue();
        registry.Add("other", root).ErrorCode.Should().Be(ConfigurationErrorCode.SharedBaseRootCollision);
        registry.Add("nested", nested).ErrorCode.Should().Be(ConfigurationErrorCode.SharedBaseRootCollision);
    }

    [Fact]
    public void Add_ExistingNameWithSameRootIsIdempotent()
    {
        string root = CreateGitRoot("repo");
        var registry = new SharedBaseRegistry(_home, Path.Combine(Path.GetTempPath(), "outside-workspace", Guid.NewGuid().ToString("N")));

        registry.Add("team", root).Succeeded.Should().BeTrue();
        registry.Add("team", root).Succeeded.Should().BeTrue();
        registry.List(includeDisabled: true).Should().ContainSingle();
    }

    [Fact]
    public void Add_AcceptsAValidWorktreeGitMarker()
    {
        string root = CreateGitRoot("worktree");
        Directory.Delete(Path.Combine(root, ".git"));
        string admin = Path.Combine(_home, "admin");
        Directory.CreateDirectory(admin);
        File.WriteAllText(Path.Combine(root, ".git"), $"gitdir: {admin}{Environment.NewLine}");
        var registry = new SharedBaseRegistry(_home, _workspace);

        registry.Add("worktree", root).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void DisableUnknownNameIsAnIdempotentSuccess()
    {
        var registry = new SharedBaseRegistry(_home, _workspace);

        registry.Disable("missing").Succeeded.Should().BeTrue();
    }

    private string CreateGitRoot(string name)
    {
        string root = Path.Combine(_home, name);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        return root;
    }

    public void Dispose()
    {
        TryDelete(_home);
        TryDelete(_workspace);
    }

    private static void TryDelete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
