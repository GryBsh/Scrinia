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
    public void ToFilesystemPath_AgentTopic_ReturnsMd()
    {
        var result = PathRouter.ToFilesystemPath(P("/agent/profile"), _root);

        result.Should().NotBeNull();
        result!.Should().EndWith(Path.Combine(".scrinia", "memories", "agent", "profile.md"));
    }

    [Fact]
    public void ToFilesystemPath_SkillTopic_ReturnsMd()
    {
        var result = PathRouter.ToFilesystemPath(P("/skill/qa"), _root);

        result.Should().NotBeNull();
        result!.Should().EndWith(Path.Combine(".scrinia", "memories", "skill", "qa.md"));
    }

    [Fact]
    public void ToFilesystemPath_WorkflowTopic_ReturnsJson()
    {
        var result = PathRouter.ToFilesystemPath(P("/workflow/goal-execution"), _root);

        result.Should().NotBeNull();
        result!.Should().EndWith(Path.Combine(".scrinia", "memories", "workflow", "goal-execution.json"));
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

    // ── ToLegacyPath ──────────────────────────────────────────────────────────

    [Fact]
    public void ToLegacyPath_WithExistingLegacyFile_ReturnsLegacyPath()
    {
        // Create a legacy file at .scrinia/topics/api/auth-flow.nmp2
        var legacyDir = Path.Combine(_root, ".scrinia", "topics", "api");
        Directory.CreateDirectory(legacyDir);
        var legacyFile = Path.Combine(legacyDir, "auth-flow.nmp2");
        File.WriteAllText(legacyFile, "legacy content");

        var result = PathRouter.ToLegacyPath(P("/api/auth-flow"), _root);

        result.Should().NotBeNull();
        result.Should().Be(legacyFile);
    }

    [Fact]
    public void ToLegacyPath_EntityChain_ReturnsNull()
    {
        // More than 2 segments — no v1 equivalent.
        var result = PathRouter.ToLegacyPath(P("/goal/G-5/phase/01/task/fix"), _root);

        result.Should().BeNull();
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
    public void ToLegacyPath_NoLegacyFile_ReturnsNull()
    {
        // No legacy file exists on disk.
        var result = PathRouter.ToLegacyPath(P("/api/auth-flow"), _root);

        result.Should().BeNull();
    }

    [Fact]
    public void ToFilesystemPath_DeepEntityPath_IncludesAllSegments()
    {
        var result = PathRouter.ToFilesystemPath(P("/goal/G-5/phase/01/task/fix"), _root);

        result.Should().NotBeNull();
        result!.Should().EndWith(Path.Combine(
            ".scrinia", "memories", "goal", "G-5", "phase", "01", "task", "fix.nmp2"));
    }
}
