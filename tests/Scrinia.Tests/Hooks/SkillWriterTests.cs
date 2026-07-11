using FluentAssertions;
using Scrinia.Commands.Hooks;
using Scrinia.Mcp;

namespace Scrinia.Tests.Hooks;

/// <summary>
/// Tests for <see cref="SkillWriter"/> — the cross-tool <c>scrinia-memory/SKILL.md</c> Agent Skill.
/// Exercises the workspace-rooted destinations (Claude, OpenCode); the user-home <c>~/.agents</c>
/// destination (Codex) is deliberately not faked so tests never touch the real home directory.
/// </summary>
public sealed class SkillWriterTests : IDisposable
{
    private readonly string _workspace;

    public SkillWriterTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"scrinia_skill_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    private string ClaudeSkill() => Path.Combine(_workspace, ".claude", "skills", "scrinia-memory", "SKILL.md");
    private string OpenCodeSkill() => Path.Combine(_workspace, ".opencode", "skills", "scrinia-memory", "SKILL.md");
    private static SkillWriter Writer(params string[] clisOnPath) => new(new FakeProcessRunner(clisOnPath));

    [Fact]
    public void EmbeddedSkill_IsPresent_WithFrontmatter()
    {
        // Guards the Scrinia.Mcp csproj resource glob: the skill must be embedded and loadable.
        string? content = EmbeddedPrompts.Load("prompts/scrinia-memory-skill.md");
        content.Should().NotBeNull();
        content.Should().Contain("name: scrinia-memory").And.Contain("description:");
    }

    [Fact]
    public void Applies_WhenAnySkillCapableCliPresent_NotOtherwise()
    {
        Writer("claude").Applies(_workspace).Should().BeTrue();
        Writer("opencode").Applies(_workspace).Should().BeTrue();
        Writer("copilot").Applies(_workspace).Should().BeFalse("Copilot gets AGENTS.md + MCP instructions, not a SKILL.md here");
    }

    [Fact]
    public void Install_WritesSkillToEachDetectedClisDir()
    {
        Writer("claude", "opencode").Install(_workspace).Should().BeTrue();

        File.Exists(ClaudeSkill()).Should().BeTrue();
        File.Exists(OpenCodeSkill()).Should().BeTrue();

        string content = File.ReadAllText(ClaudeSkill());
        content.Should().Contain("name: scrinia-memory");
        content.Should().Contain("description:");
        content.Should().Contain("search").And.Contain("remember");
        content.Should().NotContain("memory_search").And.NotContain("memory_remember");
    }

    [Fact]
    public void Install_OnlyWritesForDetectedClis()
    {
        Writer("claude").Install(_workspace).Should().BeTrue();

        File.Exists(ClaudeSkill()).Should().BeTrue();
        File.Exists(OpenCodeSkill()).Should().BeFalse("OpenCode wasn't on PATH — no skill written there");
    }

    [Fact]
    public void Install_IsIdempotent()
    {
        var writer = Writer("claude", "opencode");
        writer.Install(_workspace).Should().BeTrue();
        writer.Install(_workspace).Should().BeTrue();

        File.Exists(ClaudeSkill()).Should().BeTrue();
        File.Exists(OpenCodeSkill()).Should().BeTrue();
    }

    [Fact]
    public void Uninstall_DeletesSkillFiles_AndTidiesDirs()
    {
        var writer = Writer("claude", "opencode");
        writer.Install(_workspace).Should().BeTrue();
        writer.Uninstall(_workspace).Should().BeTrue();

        File.Exists(ClaudeSkill()).Should().BeFalse();
        File.Exists(OpenCodeSkill()).Should().BeFalse();
        Directory.Exists(Path.GetDirectoryName(ClaudeSkill())!).Should().BeFalse("the empty scrinia-memory dir is tidied");
    }

    [Fact]
    public void GetStatus_TransitionsAcrossInstall()
    {
        var writer = Writer("claude", "opencode");
        writer.GetStatus(_workspace).Should().Be(HookStatus.NotInstalled);
        writer.Install(_workspace).Should().BeTrue();
        writer.GetStatus(_workspace).Should().Be(HookStatus.Installed);
    }
}
