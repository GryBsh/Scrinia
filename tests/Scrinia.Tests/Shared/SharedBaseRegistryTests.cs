using System.Text;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests.Shared;

/// <summary>
/// Task 9 — the registry validates shared roots by filesystem evidence alone: a direct <c>.git</c>
/// directory or an exact <c>gitdir:</c> worktree marker, containment/collision of physical roots, a
/// fixed <c>project-git</c> tracking policy, and never by invoking Git or inspecting the index.
/// </summary>
public sealed class SharedBaseRegistryTests : IDisposable
{
    private readonly string _home;
    private readonly string _home2;
    private readonly string _home3;
    private readonly string _workspace;
    private readonly string _roots;

    public SharedBaseRegistryTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "scrinia-t9-home", Guid.NewGuid().ToString("N"));
        _home2 = Path.Combine(Path.GetTempPath(), "scrinia-t9-home2", Guid.NewGuid().ToString("N"));
        _home3 = Path.Combine(Path.GetTempPath(), "scrinia-t9-home3", Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(Path.GetTempPath(), "scrinia-t9-workspace", Guid.NewGuid().ToString("N"));
        // Shared roots live outside the workspace so containment never masks the Git-evidence assertions.
        _roots = Path.Combine(Path.GetTempPath(), "scrinia-t9-roots", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_roots);
        CreateGitMarker(_roots);
    }

    [Fact]
    public void Registry_accepts_git_directory_or_exact_utf8_worktree_marker_and_rejects_ancestor_only_nested_and_overlapping_roots()
    {
        // .git directory evidence with a fixed project-git policy.
        string gitDirRoot = NewGitRoot("dir-repo");
        SharedBaseResult directory = new SharedBaseRegistry(_home, _workspace).ValidateRoot(gitDirRoot);
        directory.Succeeded.Should().BeTrue();
        directory.Base!.Tracking.Should().Be(TrackingPolicy.ProjectGit);

        // Exact gitdir: worktree marker, LF line ending, absolute target.
        string worktreeAbsolute = NewWorktreeRoot(lineEnding: "\n", absolute: true);
        new SharedBaseRegistry(_home, _workspace).ValidateRoot(worktreeAbsolute).Succeeded.Should().BeTrue();

        // Exact gitdir: worktree marker, CRLF line ending, target relative to the root.
        string worktreeCrlf = NewWorktreeRoot(lineEnding: "\r\n", absolute: false);
        new SharedBaseRegistry(_home, _workspace).ValidateRoot(worktreeCrlf).Succeeded.Should().BeTrue();

        // Ancestry-only .git (the parent is a repository, the root itself is not) is insufficient.
        string ancestorRepo = NewGitRoot("ancestor");
        string nestedNonRepo = Path.Combine(ancestorRepo, "child");
        Directory.CreateDirectory(nestedNonRepo);
        SharedBaseResult nested = new SharedBaseRegistry(_home, _workspace).ValidateRoot(nestedNonRepo);
        nested.Succeeded.Should().BeFalse();
        nested.ErrorCode.Should().Be(ConfigurationErrorCode.SharedBaseNotGitRoot);

        // A registered root nested inside another registered root collides.
        string outer = NewGitRoot("outer");
        string inner = Path.Combine(outer, "inner");
        Directory.CreateDirectory(inner);
        CreateGitMarker(inner);
        var addRegistry = new SharedBaseRegistry(_home, _workspace);
        addRegistry.Add("outer", outer).Succeeded.Should().BeTrue();
        SharedBaseResult nestedAdd = addRegistry.Add("inner", inner);
        nestedAdd.Succeeded.Should().BeFalse();
        nestedAdd.ErrorCode.Should().Be(ConfigurationErrorCode.SharedBaseRootCollision);

        // A registered root whose physical parent is also registered overlaps and collides.
        var collisionRegistry = new SharedBaseRegistry(_home2, _workspace);
        collisionRegistry.Add("child", inner).Succeeded.Should().BeTrue();
        collisionRegistry.Add("parent", _roots).ErrorCode.Should().Be(ConfigurationErrorCode.SharedBaseRootCollision);
    }

    [Theory]
    [InlineData(true)]   // absolute gitdir target
    [InlineData(false)]  // relative gitdir target
    public void Worktree_marker_must_name_an_existing_target_directory(bool absolute)
    {
        string root = NewWorktreeRoot(lineEnding: "\n", absolute: absolute);
        new SharedBaseRegistry(_home, _workspace).ValidateRoot(root).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("gitdir:target\n", "the single required space after the colon")]
    [InlineData("GITDIR: target\n", "a case-sensitive gitdir: prefix")]
    [InlineData("gitdir:  target\n", "exactly one space after the colon")]
    [InlineData("gitdir: target\nextra\n", "exactly one line")]
    [InlineData("gitdir: \n", "a nonempty target")]
    [InlineData("\uFEFFgitdir: target\n", "UTF-8 without a BOM")]
    public void Malformed_worktree_markers_are_rejected(string content, string reason)
    {
        string root = NewWorktreeRoot(lineEnding: "", absolute: true);
        File.WriteAllText(Path.Combine(root, ".git"), content, new UTF8Encoding(false));

        SharedBaseResult result = new SharedBaseRegistry(_home, _workspace).ValidateRoot(root);
        result.Succeeded.Should().BeFalse($"({reason})");
        result.ErrorCode.Should().Be(ConfigurationErrorCode.SharedBaseNotGitRoot);
    }

    [Fact]
    public void Roots_inside_or_equal_to_the_workspace_and_scrinia_stores_are_rejected()
    {
        Directory.CreateDirectory(_workspace);
        SharedBaseRegistry registry = new(_home, _workspace);

        string insideWorkspace = Path.Combine(_workspace, "store");
        Directory.CreateDirectory(insideWorkspace);
        CreateGitMarker(insideWorkspace);
        registry.ValidateRoot(insideWorkspace).ErrorCode.Should().Be(ConfigurationErrorCode.SharedBaseRootCollision);

        string scriniaAsRoot = Path.Combine(_workspace, ".scrinia");
        Directory.CreateDirectory(scriniaAsRoot);
        CreateGitMarker(scriniaAsRoot);
        registry.ValidateRoot(scriniaAsRoot).ErrorCode.Should().Be(ConfigurationErrorCode.SharedBaseRootCollision);
    }

    [Fact]
    public void Add_reports_nested_physical_roots_as_collisions()
    {
        string outer = NewGitRoot("outer");
        string inner = Path.Combine(outer, "inner");
        Directory.CreateDirectory(inner);
        CreateGitMarker(inner);

        var registry = new SharedBaseRegistry(_home3, _workspace);
        registry.Add("outer", outer).Succeeded.Should().BeTrue();
        registry.Add("inner", inner).ErrorCode.Should().Be(ConfigurationErrorCode.SharedBaseRootCollision);
    }

    [Fact]
    public void Collisions_reports_overlapping_registered_roots()
    {
        var registry = new SharedBaseRegistry(_home, _workspace, new SeededHomeConfigStore(
            new HomeConfig
            {
                SchemaVersion = 1,
                SharedBases = new SharedBasesMap
                {
                    ["alpha"] = new SharedBaseEntry { Root = "/repo/alpha" },
                    ["beta"] = new SharedBaseEntry { Root = "/repo/alpha/beta" },
                },
            }));

        SharedBaseCollision collision = registry.Collisions().Should().ContainSingle().Which;
        (collision.Name, collision.OtherName).Should().BeOneOf(("alpha", "beta"), ("beta", "alpha"));
    }

    private string NewGitRoot(string name)
    {
        string root = Path.Combine(_roots, name);
        Directory.CreateDirectory(root);
        CreateGitMarker(root);
        return root;
    }

    private string NewWorktreeRoot(string lineEnding, bool absolute)
    {
        string root = Path.Combine(_roots, "worktree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string targetDir = absolute
            ? Path.Combine(_roots, "worktree-target-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(root, "target");
        Directory.CreateDirectory(targetDir);
        string target = absolute ? targetDir : "target";
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: " + target + lineEnding, new UTF8Encoding(false));
        return root;
    }

    private static void CreateGitMarker(string root) => Directory.CreateDirectory(Path.Combine(root, ".git"));

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        if (Directory.Exists(_home2)) Directory.Delete(_home2, recursive: true);
        if (Directory.Exists(_home3)) Directory.Delete(_home3, recursive: true);
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
        if (Directory.Exists(_roots)) Directory.Delete(_roots, recursive: true);
    }

    private sealed class SeededHomeConfigStore(HomeConfig config) : IHomeConfigStore
    {
        public HomeConfig Load(string homeDirectory) => config;
        public void Save(string homeDirectory, HomeConfig config) { }
    }
}
