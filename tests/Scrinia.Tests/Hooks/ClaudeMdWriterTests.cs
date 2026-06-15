using FluentAssertions;
using Scrinia.Commands.Hooks;

namespace Scrinia.Tests.Hooks;

/// <summary>
/// Tests for <see cref="ClaudeMdWriter"/> — CLAUDE.md as a managed <c>@AGENTS.md</c> import (DRY:
/// the canonical baseline lives in AGENTS.md), preserving any user CLAUDE.md content.
/// </summary>
public sealed class ClaudeMdWriterTests : IDisposable
{
    private readonly string _workspace;

    public ClaudeMdWriterTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"scrinia_claudemd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    private string ClaudeMdPath() => Path.Combine(_workspace, "CLAUDE.md");
    private static ClaudeMdWriter Writer(params string[] clisOnPath) => new(new FakeProcessRunner(clisOnPath));

    [Fact]
    public void Applies_OnlyWhenClaudePresent()
    {
        Writer("claude").Applies(_workspace).Should().BeTrue();
        Writer("codex", "opencode").Applies(_workspace).Should().BeFalse();
    }

    [Fact]
    public void Install_WritesImportOfAgentsMd_InManagedBlock()
    {
        Writer("claude").Install(_workspace).Should().BeTrue();

        string content = File.ReadAllText(ClaudeMdPath());
        content.Should().Contain(ManagedMarkdownBlock.Begin).And.Contain(ManagedMarkdownBlock.End);
        ManagedMarkdownBlock.ExtractBody(content).Should().Be("@AGENTS.md",
            "CLAUDE.md imports the canonical AGENTS.md baseline rather than duplicating it");
    }

    [Fact]
    public void Install_IsIdempotent_AndPreservesUserContent()
    {
        File.WriteAllText(ClaudeMdPath(), "# Project rules\n\nUse 4 spaces.\n");
        var writer = Writer("claude");
        writer.Install(_workspace).Should().BeTrue();
        writer.Install(_workspace).Should().BeTrue();

        string content = File.ReadAllText(ClaudeMdPath());
        content.Should().Contain("# Project rules").And.Contain("Use 4 spaces.");
        CountOccurrences(content, ManagedMarkdownBlock.Begin).Should().Be(1);
    }

    [Fact]
    public void Uninstall_RemovesManagedBlock_PreservingUserContent()
    {
        File.WriteAllText(ClaudeMdPath(), "# Keep\n");
        var writer = Writer("claude");
        writer.Install(_workspace).Should().BeTrue();
        writer.Uninstall(_workspace).Should().BeTrue();

        string content = File.ReadAllText(ClaudeMdPath());
        content.Should().Contain("# Keep").And.NotContain("@AGENTS.md");
    }

    [Fact]
    public void GetStatus_InstalledThenDrift()
    {
        var writer = Writer("claude");
        writer.GetStatus(_workspace).Should().Be(HookStatus.NotInstalled);
        writer.Install(_workspace).Should().BeTrue();
        writer.GetStatus(_workspace).Should().Be(HookStatus.Installed);

        // User changes the import target inside our block → drift.
        File.WriteAllText(ClaudeMdPath(),
            File.ReadAllText(ClaudeMdPath()).Replace("@AGENTS.md", "@OTHER.md"));
        writer.GetStatus(_workspace).Should().Be(HookStatus.Drift);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}
