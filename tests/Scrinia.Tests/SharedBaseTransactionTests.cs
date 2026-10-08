using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

public sealed class SharedBaseTransactionTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "scrinia-task7-home", Guid.NewGuid().ToString("N"));
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "scrinia-task7-workspace", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Add_RollsBackIgnoreAndNewDirectoryWhenRegistryCommitFails()
    {
        string root = CreateGitRoot("repo");
        string ignorePath = Path.Combine(root, ".scrinia", ".gitignore");
        Directory.CreateDirectory(Path.GetDirectoryName(ignorePath)!);
        File.WriteAllText(ignorePath, "# user line\n");
        var registry = new SharedBaseRegistry(_home, _workspace, new FailingHomeConfigStore());
        var operations = new SharedBaseOperations(registry, _home, _workspace);

        SharedBaseResult result = operations.Add("team", root);

        result.ErrorCode.Should().Be(ConfigurationErrorCode.ConfigWriteFailed);
        File.ReadAllText(ignorePath).Should().Be("# user line\n");
        registry.List(includeDisabled: true).Should().BeEmpty();
    }

    [Fact]
    public void Add_CreatesManagedBlockBeforeRegistryCommit()
    {
        string root = Path.Combine(_home, "repo");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var store = new RecordingHomeConfigStore();
        var registry = new SharedBaseRegistry(_home, _workspace, store);
        var operations = new SharedBaseOperations(registry, _home, _workspace);

        SharedBaseResult result = operations.Add("team", root);

        result.Succeeded.Should().BeTrue();
        File.ReadAllText(Path.Combine(root, ".scrinia", ".gitignore")).Should().Be(ManagedIgnoreBlock.ProjectGit);
        store.SaveObservedIgnore.Should().Be(ManagedIgnoreBlock.ProjectGit);
    }

    [Fact]
    public void Add_RetainsOnlyWorkspaceLockWhenNewRegistrationCommitFails()
    {
        string root = CreateGitRoot("empty-repo");
        string directory = Path.Combine(root, ".scrinia");
        var registry = new SharedBaseRegistry(_home, _workspace, new FailingHomeConfigStore());

        SharedBaseResult result = new SharedBaseOperations(registry, _home, _workspace).Add("team", root);

        result.ErrorCode.Should().Be(ConfigurationErrorCode.ConfigWriteFailed);
        Directory.GetFileSystemEntries(directory).Select(Path.GetFileName)
            .Should().Equal(".bundle-restore.lock");
        File.Exists(Path.Combine(directory, ".gitignore")).Should().BeFalse();
        registry.List(includeDisabled: true).Should().BeEmpty();
        using var released = new WorkspaceAdmission(root, null).EnterTracking(TimeSpan.FromSeconds(1), default);
    }

    [Fact]
    public void Add_ReportsRecoveryRequiredWhenCompensationFails()
    {
        string root = CreateGitRoot("compensation-repo");
        string ignorePath = Path.Combine(root, ".scrinia", ".gitignore");
        Directory.CreateDirectory(Path.GetDirectoryName(ignorePath)!);
        File.WriteAllText(ignorePath, "# user line\n");
        int writes = 0;
        DurableFile.FailureInjector = (_, _) => ++writes > 1 ? new IOException("rollback unavailable") : null;
        try
        {
            SharedBaseResult result = new SharedBaseOperations(
                new SharedBaseRegistry(_home, _workspace, new FailingHomeConfigStore()), _home, _workspace).Add("team", root);

            result.ErrorCode.Should().Be(ConfigurationErrorCode.ConfigRecoveryRequired);
            result.ExitClass.Should().Be(ConfigurationExitClass.WriteFailure);
        }
        finally
        {
            DurableFile.FailureInjector = null;
        }
    }

    [Fact]
    public async Task Add_SerializesDifferentNamesSharingOneRoot()
    {
        string root = CreateGitRoot("concurrent-repo");
        SharedBaseResult[] results = await Task.WhenAll(
            Task.Run(() => new SharedBaseOperations(new SharedBaseRegistry(_home, _workspace), _home, _workspace).Add("alpha", root)),
            Task.Run(() => new SharedBaseOperations(new SharedBaseRegistry(_home, _workspace), _home, _workspace).Add("beta", root)));

        results.Count(result => result.Succeeded).Should().Be(1);
        results.Count(result => result.ErrorCode == ConfigurationErrorCode.SharedBaseRootCollision).Should().Be(1);
        new SharedBaseRegistry(_home, _workspace).List(includeDisabled: true)
            .Select(entry => entry.Name).Should().ContainSingle();
        File.ReadAllText(Path.Combine(root, ".scrinia", ".gitignore"))
            .Should().Be(ManagedIgnoreBlock.ProjectGit);
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

    private sealed class FailingHomeConfigStore : IHomeConfigStore
    {
        public HomeConfig Load(string homeDirectory) => new() { SchemaVersion = 1, SharedBases = new SharedBasesMap() };
        public void Save(string homeDirectory, HomeConfig config) => throw new IOException("injected registry failure");
    }

    private sealed class RecordingHomeConfigStore : IHomeConfigStore
    {
        public string? SaveObservedIgnore { get; private set; }
        public HomeConfig Load(string homeDirectory) => new() { SchemaVersion = 1, SharedBases = new SharedBasesMap() };
        public void Save(string homeDirectory, HomeConfig config)
        {
            SaveObservedIgnore = File.ReadAllText(Path.Combine(config.SharedBases["team"].Root, ".scrinia", ".gitignore"));
        }
    }
}
