using FluentAssertions;
using Scrinia.Commands.Hooks;

namespace Scrinia.Tests.Hooks;

/// <summary>
/// Tests for <see cref="AgentsMdWriter"/> — the canonical workspace-root AGENTS.md baseline, owned
/// via a <see cref="ManagedMarkdownBlock"/> so user content is preserved across install/uninstall.
/// </summary>
public sealed class AgentsMdWriterTests : IDisposable
{
    private readonly string _workspace;

    public AgentsMdWriterTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"scrinia_agentsmd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    private string AgentsMdPath() => Path.Combine(_workspace, "AGENTS.md");
    private static AgentsMdWriter Writer(params string[] clisOnPath) => new(new FakeProcessRunner(clisOnPath));

    [Fact]
    public void Applies_WhenAnySupportedCliPresent_NotOtherwise()
    {
        Writer("codex").Applies(_workspace).Should().BeTrue();
        Writer("opencode").Applies(_workspace).Should().BeTrue();
        Writer("claude").Applies(_workspace).Should().BeTrue();
        Writer(/* none */).Applies(_workspace).Should().BeFalse();
    }

    [Fact]
    public void Install_CreatesAgentsMd_WithManagedPrimerBlock()
    {
        Writer("codex").Install(_workspace).Should().BeTrue();

        File.Exists(AgentsMdPath()).Should().BeTrue();
        string content = File.ReadAllText(AgentsMdPath());
        content.Should().Contain(ManagedMarkdownBlock.Begin).And.Contain(ManagedMarkdownBlock.End);
        content.Should().Contain("Scrinia memory").And.Contain("guide()").And.Contain("search(");
        content.Should().NotContain("memory_search");
    }

    [Fact]
    public void Install_IsIdempotent_SingleBlock()
    {
        var writer = Writer("codex");
        writer.Install(_workspace).Should().BeTrue();
        writer.Install(_workspace).Should().BeTrue();

        string content = File.ReadAllText(AgentsMdPath());
        CountOccurrences(content, ManagedMarkdownBlock.Begin).Should().Be(1, "second install updates, not duplicates");
    }

    [Fact]
    public void Install_PreservesUserContent()
    {
        File.WriteAllText(AgentsMdPath(), "# My project\n\nHand-written guidance.\n");
        Writer("codex").Install(_workspace).Should().BeTrue();

        string content = File.ReadAllText(AgentsMdPath());
        content.Should().Contain("# My project").And.Contain("Hand-written guidance.");
        content.Should().Contain(ManagedMarkdownBlock.Begin);
    }

    [Fact]
    public void Uninstall_RemovesOnlyManagedBlock_PreservingUserContent()
    {
        File.WriteAllText(AgentsMdPath(), "# Keep me\n");
        var writer = Writer("codex");
        writer.Install(_workspace).Should().BeTrue();
        writer.Uninstall(_workspace).Should().BeTrue();

        string content = File.ReadAllText(AgentsMdPath());
        content.Should().Contain("# Keep me");
        content.Should().NotContain(ManagedMarkdownBlock.Begin);
    }

    [Fact]
    public void Uninstall_DeletesFile_WhenOnlyManagedBlockRemained()
    {
        var writer = Writer("codex");
        writer.Install(_workspace).Should().BeTrue();
        writer.Uninstall(_workspace).Should().BeTrue();

        File.Exists(AgentsMdPath()).Should().BeFalse("a file that held only our block should be removed entirely");
    }

    [Fact]
    public void GetStatus_TransitionsAcrossInstallAndDrift()
    {
        var writer = Writer("codex");
        writer.GetStatus(_workspace).Should().Be(HookStatus.NotInstalled);

        writer.Install(_workspace).Should().BeTrue();
        writer.GetStatus(_workspace).Should().Be(HookStatus.Installed);

        // User edits inside the managed block → drift.
        string content = File.ReadAllText(AgentsMdPath())
            .Replace("Scrinia memory", "Scrinia memory (edited)");
        File.WriteAllText(AgentsMdPath(), content);
        writer.GetStatus(_workspace).Should().Be(HookStatus.Drift);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}
