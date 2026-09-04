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

    [Theory]
    [InlineData("/")]
    [InlineData("C:\\")]
    public void NormalizeRoot_PreservesVolumeRoots(string root)
    {
        if (root.StartsWith("C:", StringComparison.Ordinal) && !OperatingSystem.IsWindows()) return;

        SharedBaseRegistry.NormalizeRoot(root).Should().Be(Path.GetPathRoot(Path.GetFullPath(root)));
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
    public void Add_RejectsAncestorGitMarkerWhenTheSuppliedRootHasNoMarker()
    {
        string parent = Path.Combine(_home, "parent");
        string root = Path.Combine(parent, "child");
        Directory.CreateDirectory(Path.Combine(parent, ".git"));
        Directory.CreateDirectory(root);
        var registry = new SharedBaseRegistry(_home, _workspace);

        registry.Add("child", root).ErrorCode.Should().Be(ConfigurationErrorCode.SharedBaseNotGitRoot);
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
    public void EnableAndDisable_PreserveOrderAndRemoveTheLastScalar()
    {
        string first = CreateGitRoot("first-repo");
        string second = CreateGitRoot("second-repo");
        var registry = new SharedBaseRegistry(_home, _workspace);
        registry.Add("first", first).Succeeded.Should().BeTrue();
        registry.Add("second", second).Succeeded.Should().BeTrue();

        registry.Enable("second").Succeeded.Should().BeTrue();
        registry.Enable("first").Succeeded.Should().BeTrue();
        WorkspaceConfig.Load(_workspace).GetEnabledBases().Should().Equal("second", "first");
        registry.Disable("second").Succeeded.Should().BeTrue();
        registry.Disable("first").Succeeded.Should().BeTrue();
        WorkspaceConfig.Load(_workspace).GetValue(WorkspaceConfig.EnabledKey).Should().BeNull();
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
    public void Add_RejectsEmptyWorktreeGitMarkerTarget()
    {
        string root = Path.Combine(_home, "empty-marker");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".git"), $"gitdir:   {Environment.NewLine}");
        var registry = new SharedBaseRegistry(_home, _workspace);

        registry.Add("empty-marker", root).ErrorCode.Should().Be(ConfigurationErrorCode.SharedBaseNotGitRoot);
    }

    [Fact]
    public void Add_AllowsTraversalThatResolvesOutsideWorkspace()
    {
        string parent = Path.GetDirectoryName(_workspace)!;
        string root = Path.Combine(parent, "repo");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var registry = new SharedBaseRegistry(_home, _workspace);

        registry.Add("outside", Path.Combine(_workspace, "..", "repo")).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Enable_ConcurrentCallsRetainBothBases()
    {
        string first = CreateGitRoot("concurrent-first");
        string second = CreateGitRoot("concurrent-second");
        var firstRegistry = new SharedBaseRegistry(_home, _workspace);
        var secondRegistry = new SharedBaseRegistry(_home, _workspace);
        firstRegistry.Add("first", first).Succeeded.Should().BeTrue();
        firstRegistry.Add("second", second).Succeeded.Should().BeTrue();
        using var start = new Barrier(2);

        Task firstEnable = Task.Run(() => { start.SignalAndWait(); firstRegistry.Enable("first"); });
        Task secondEnable = Task.Run(() => { start.SignalAndWait(); secondRegistry.Enable("second"); });
        await Task.WhenAll(firstEnable, secondEnable);

        WorkspaceConfig.Load(_workspace).GetEnabledBases().Should().BeEquivalentTo(new[] { "first", "second" });
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
