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
}
