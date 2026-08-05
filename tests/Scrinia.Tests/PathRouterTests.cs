using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

public class PathRouterTests : IDisposable
{
    private static readonly HashSet<string> EntityTypes = new(
        ["goal", "phase", "task", "concern", "requirement", "project", "workflow", "skill"],
        StringComparer.OrdinalIgnoreCase);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"scrinia-test-{Guid.NewGuid():N}");

    public PathRouterTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private ParsedPath P(string raw) => PathParser.Parse(raw, EntityTypes);

    // ── ToFilesystemPath ──────────────────────────────────────────────────────

    [Fact]
    public void ToFilesystemPath_FreeformTopic_ReturnsNmp2()
    {
        var result = PathRouter.ToFilesystemPath(P("/api/auth-flow"), _root);

        result.Should().NotBeNull();
        result!.Should().EndWith(Path.Combine(".scrinia", "memories", "api", "auth-flow.nmp2"));
    }

    [Fact]
    public void ToFilesystemPath_EntityPrefixedPath_ReturnsNmp2()
    {
        var result = PathRouter.ToFilesystemPath(P("/goal/G-5/research/frontend"), _root);

        result.Should().NotBeNull();
        result!.Should().EndWith(Path.Combine(".scrinia", "memories", "goal", "G-5", "research", "frontend.nmp2"));
    }

    [Fact]
    public void ToFilesystemPath_EphemeralPath_ReturnsNull()
    {
        var result = PathRouter.ToFilesystemPath(P("/temp/scratch"), _root);

        result.Should().BeNull();
    }

    // ── ToVersionsDir ─────────────────────────────────────────────────────────

    [Fact]
    public void ToVersionsDir_FreeformTopic_ReturnsVersionsSibling()
    {
        var result = PathRouter.ToVersionsDir(P("/api/auth-flow"), _root);

        result.Should().EndWith(Path.Combine(".scrinia", "memories", "api", "versions"));
    }

    // ── ToMetadataPath ────────────────────────────────────────────────────────

    [Fact]
    public void ToMetadataPath_FreeformTopic_ReturnsMetaJson()
    {
        var result = PathRouter.ToMetadataPath(P("/api/auth-flow"), _root);

        result.Should().EndWith(Path.Combine(".scrinia", "memories", "api", "auth-flow.meta.json"));
    }

    // ── IsEphemeral ───────────────────────────────────────────────────────────

    [Fact]
    public void IsEphemeral_TempPath_ReturnsTrue()
    {
        PathRouter.IsEphemeral(P("/temp/scratch")).Should().BeTrue();
    }

    [Fact]
    public void IsEphemeral_RegularPath_ReturnsFalse()
    {
        PathRouter.IsEphemeral(P("/api/auth-flow")).Should().BeFalse();
    }

    // ── Additional coverage ───────────────────────────────────────────────────

    [Fact]
    public void ToVersionsDir_EphemeralPath_Throws()
    {
        var act = () => PathRouter.ToVersionsDir(P("/temp/scratch"), _root);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ToMetadataPath_EphemeralPath_Throws()
    {
        var act = () => PathRouter.ToMetadataPath(P("/temp/scratch"), _root);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ToFilesystemPath_DeepEntityPath_IncludesAllSegments()
    {
        var result = PathRouter.ToFilesystemPath(P("/goal/G-5/phase/01/task/fix"), _root);

        result.Should().NotBeNull();
        result!.Should().EndWith(Path.Combine(
            ".scrinia", "memories", "goal", "G-5", "phase", "01", "task", "fix.nmp2"));
    }

    // ── ToStoreDir ────────────────────────────────────────────────────────────

    [Fact]
    public void ToStoreDir_MatchesFileMemoryStoreForEveryScopeShape()
    {
        var store = new FileMemoryStore(_root);
        foreach (string scope in new[] { "local", "local:findings", "local:goal/G-5", "local:agent" })
            PathRouter.ToStoreDir(scope, _root).Should().Be(store.GetStoreDirForScope(scope));
    }

    [Fact]
    public void ToStoreDir_MapsEachScopeShapeToItsLiteralDirectory()
    {
        PathRouter.ToStoreDir("local", _root)
            .Should().Be(Path.Combine(_root, ".scrinia", "memories"));

        PathRouter.ToStoreDir("local:findings", _root)
            .Should().Be(Path.Combine(_root, ".scrinia", "memories", "findings"));

        PathRouter.ToStoreDir("local:goal/G-5", _root)
            .Should().Be(Path.Combine(_root, ".scrinia", "memories", "goal", "G-5"));

        PathRouter.ToStoreDir("local:agent", _root)
            .Should().Be(Path.Combine(_root, ".scrinia", "memories", "agent"));
    }

    [Fact]
    public void ToStoreDir_UnknownScope_Throws()
    {
        var act = () => PathRouter.ToStoreDir("remote:whatever", _root);
        act.Should().Throw<ArgumentException>().WithMessage("*Unknown scope*");
    }

    // ── File entities ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("skill", "skills")]
    [InlineData("agent", "agent")]
    public void ToFileEntityDir_MapsKindToItsLiteralDirectory(string kind, string dirName)
    {
        PathRouter.ToFileEntityDir(kind, _root)
            .Should().Be(Path.Combine(_root, ".scrinia", dirName),
                because: "the singular segment name and the on-disk directory name differ for " +
                         "'skill'; this table is the only place that mapping exists");
    }

    [Fact]
    public void ToFileEntityDir_IsCaseInsensitive()
    {
        PathRouter.ToFileEntityDir("SKILL", _root)
            .Should().Be(Path.Combine(_root, ".scrinia", "skills"));
    }

    [Fact]
    public void ToFileEntityDir_UnknownKind_Throws()
    {
        var act = () => PathRouter.ToFileEntityDir("workflow", _root);
        act.Should().Throw<ArgumentException>(
            because: "workflows are deleted; an unknown kind is a programming error, " +
                     "matching ToStoreDir's contract");
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("agent")]
    public void IsFileEntity_TrueForKnownKinds(string kind) =>
        PathRouter.IsFileEntity(kind).Should().BeTrue();

    [Theory]
    [InlineData("findings")]
    [InlineData("workflow")]
    [InlineData("")]
    public void IsFileEntity_FalseOtherwise(string segment) =>
        PathRouter.IsFileEntity(segment).Should().BeFalse();

    [Fact]
    public void TryToFileEntityPath_ValidName_ResolvesUnderTheKindDirectory()
    {
        PathRouter.TryToFileEntityPath("skill", "qa", _root, out string path)
            .Should().BeTrue();
        path.Should().Be(Path.Combine(_root, ".scrinia", "skills", "qa.md"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("sub/dir")]
    [InlineData("sub\\dir")]
    [InlineData("")]
    [InlineData("  ")]
    public void TryToFileEntityPath_RejectsUnsafeNames(string name)
    {
        PathRouter.TryToFileEntityPath("skill", name, _root, out string path)
            .Should().BeFalse(because: "the resolver enforces containment so no caller can forget it");
        path.Should().BeEmpty();
    }

    [Fact]
    public void ToFilesystemPath_SkillTopic_ResolvesToTopLevelSkillsDir()
    {
        var result = PathRouter.ToFilesystemPath(P("/skill/qa"), _root);
        result.Should().Be(Path.Combine(_root, ".scrinia", "skills", "qa.md"),
            because: "hand-authored file entities live at the top level of .scrinia/, " +
                     "never inside the memory store");
    }

    [Fact]
    public void ToFilesystemPath_AgentTopic_ResolvesToTopLevelAgentDir()
    {
        var result = PathRouter.ToFilesystemPath(P("/agent/profile"), _root);
        result.Should().Be(Path.Combine(_root, ".scrinia", "agent", "profile.md"));
    }

    [Fact]
    public void ToFilesystemPath_FileEntityWithExtraSegments_ReturnsNull()
    {
        PathRouter.ToFilesystemPath(P("/skill/a/b"), _root)
            .Should().BeNull(because: "file entities are flat; a nested path has no location");
    }
}
