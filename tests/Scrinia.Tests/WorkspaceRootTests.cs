using Scrinia.Core;

namespace Scrinia.Tests;

public class WorkspaceRootTests : IDisposable
{
    private readonly string _tempDir;

    public WorkspaceRootTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_wsroot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Mkdir(params string[] segments)
    {
        string path = Path.Combine([_tempDir, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void RepoRoot_ResolvesToItself_NotAnAncestor()
    {
        string repo = Mkdir("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        Assert.Equal(repo, WorkspaceRoot.Resolve(repo));
    }

    [Fact]
    public void Subdirectory_ResolvesToEnclosingRepoRoot()
    {
        string repo = Mkdir("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        string sub = Mkdir("repo", "src", "deep");

        Assert.Equal(repo, WorkspaceRoot.Resolve(sub));
    }

    [Fact]
    public void GitAsFile_IsTreatedAsRepoRoot()
    {
        // Worktrees and submodules write .git as a FILE containing a gitdir: pointer.
        string worktree = Mkdir("worktree");
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: /somewhere/.git/worktrees/wt\n");
        string sub = Mkdir("worktree", "src");

        Assert.Equal(worktree, WorkspaceRoot.Resolve(sub));
    }

    [Fact]
    public void NoGitAnywhere_ResolvesToStartDir_WithoutWalking()
    {
        string plain = Mkdir("container", "plain");

        Assert.Equal(plain, WorkspaceRoot.Resolve(plain));
    }

    [Fact]
    public void NestedRepos_EachResolveToThemselves()
    {
        string outer = Mkdir("outer");
        Directory.CreateDirectory(Path.Combine(outer, ".git"));
        string inner = Mkdir("outer", "inner");
        Directory.CreateDirectory(Path.Combine(inner, ".git"));

        Assert.Equal(outer, WorkspaceRoot.Resolve(outer));
        Assert.Equal(inner, WorkspaceRoot.Resolve(inner));
    }

    [Fact]
    public void AncestorStoreAboveRepoBoundary_IsNeverReached()
    {
        // A .scrinia far above must not influence resolution at all.
        Directory.CreateDirectory(Path.Combine(_tempDir, ".scrinia"));
        string repo = Mkdir("container", "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        Assert.Equal(repo, WorkspaceRoot.Resolve(repo));
    }

    [Fact]
    public void ExplicitRoot_BypassesResolutionEntirely()
    {
        string repo = Mkdir("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        string elsewhere = Mkdir("elsewhere");

        // Given an explicit root, the .git above the start directory is irrelevant.
        Assert.Equal(elsewhere, WorkspaceRoot.Resolve(elsewhere, Path.Combine(repo, "src")));
    }

    [Fact]
    public void NullOrBlankExplicitRoot_FallsBackToResolution()
    {
        string repo = Mkdir("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        Assert.Equal(repo, WorkspaceRoot.Resolve(null, repo));
        Assert.Equal(repo, WorkspaceRoot.Resolve("   ", repo));
    }

    [Fact]
    public void SiblingProjectsUnderSharedParentStore_ResolveToDifferentRoots()
    {
        // Regression for the reported bug: a .scrinia in a container directory
        // must not capture the projects beneath it.
        Directory.CreateDirectory(Path.Combine(_tempDir, ".scrinia"));
        string a = Mkdir("bludon");
        Directory.CreateDirectory(Path.Combine(a, ".git"));
        string b = Mkdir("wslc-docker-socket");
        Directory.CreateDirectory(Path.Combine(b, ".git"));

        string rootA = WorkspaceRoot.Resolve(a);
        string rootB = WorkspaceRoot.Resolve(b);

        Assert.Equal(a, rootA);
        Assert.Equal(b, rootB);
        Assert.NotEqual(rootA, rootB);
    }

    [Fact]
    public void FindOrphanStores_ReportsAncestorStoreAboveRoot()
    {
        string orphan = Path.Combine(_tempDir, ".scrinia");
        Directory.CreateDirectory(orphan);
        string repo = Mkdir("container", "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        var found = WorkspaceRoot.FindOrphanStores(repo, repo);

        Assert.Contains(orphan, found);
    }

    [Fact]
    public void FindOrphanStores_NeverReportsTheResolvedRootsOwnStore()
    {
        string repo = Mkdir("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(Path.Combine(repo, ".scrinia"));

        var found = WorkspaceRoot.FindOrphanStores(repo, repo);

        Assert.DoesNotContain(Path.Combine(repo, ".scrinia"), found);
    }

    [Fact]
    public void FindOrphanStores_IsEmptyForACleanNewProject()
    {
        string repo = Mkdir("container", "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        var clean = WorkspaceRoot.FindOrphanStores(repo, repo);

        // Stores outside the fixture (for example one in the user's home directory) are real
        // and correctly reported, so the assertion is scoped to the tree this test controls.
        Assert.DoesNotContain(clean, p => p.StartsWith(_tempDir, StringComparison.OrdinalIgnoreCase));

        // Planting a store inside the fixture makes it appear, so the assertion above is
        // falsifiable rather than true by construction.
        string planted = Path.Combine(_tempDir, "container", ".scrinia");
        Directory.CreateDirectory(planted);

        Assert.Contains(planted, WorkspaceRoot.FindOrphanStores(repo, repo));
    }

    [Fact]
    public void FindOrphanStores_ReportsCwdStoreWhenCwdIsBelowRoot()
    {
        string repo = Mkdir("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        string sub = Mkdir("repo", "src");
        string cwdStore = Path.Combine(sub, ".scrinia");
        Directory.CreateDirectory(cwdStore);

        var found = WorkspaceRoot.FindOrphanStores(repo, sub);

        Assert.Contains(cwdStore, found);
    }

    [Fact]
    public void OrphanWarning_NamesBothStoresAndAConcreteMigrationCommand()
    {
        string text = Scrinia.Commands.WorkspaceSetup.FormatOrphanWarning(
            orphan: @"C:\.scrinia",
            root: @"C:\repos\proj",
            topics: ["agent", "patterns"]);

        Assert.Contains(@"C:\.scrinia", text);
        Assert.Contains(@"C:\repos\proj", text);
        Assert.Contains("scri bundle export", text);
        Assert.Contains("scri bundle import", text);
        Assert.Contains("agent,patterns", text);
    }

    [Fact]
    public void OrphanWarning_ExportCommandTargetsTheOrphansParentNotTheStoreItself()
    {
        string text = Scrinia.Commands.WorkspaceSetup.FormatOrphanWarning(
            orphan: @"C:\containers\.scrinia",
            root: @"C:\containers\proj",
            topics: ["agent", "patterns"]);

        Assert.Contains(@"scri bundle export --workspace-root ""C:\containers"" --topics agent,patterns", text);
    }
}
