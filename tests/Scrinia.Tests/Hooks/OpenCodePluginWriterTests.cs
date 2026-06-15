using FluentAssertions;
using Scrinia.Commands.Hooks;

namespace Scrinia.Tests.Hooks;

/// <summary>
/// Tests for <see cref="OpenCodePluginWriter"/> — the generated <c>.opencode/plugin/scrinia.ts</c>
/// that delivers the per-turn hint on OpenCode (which has no shell-hook channel). Owns a whole file;
/// install overwrites, uninstall deletes only our marked file.
/// </summary>
public sealed class OpenCodePluginWriterTests : IDisposable
{
    private readonly string _workspace;

    public OpenCodePluginWriterTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"scrinia_ocplugin_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    private string PluginPath() => Path.Combine(_workspace, ".opencode", "plugin", "scrinia.ts");
    private static OpenCodePluginWriter Writer(params string[] clisOnPath) => new(new FakeProcessRunner(clisOnPath));

    [Fact]
    public void Applies_OnlyWhenOpenCodePresent()
    {
        Writer("opencode").Applies(_workspace).Should().BeTrue();
        Writer("claude", "codex").Applies(_workspace).Should().BeFalse();
    }

    [Fact]
    public void Install_WritesPlugin_InvokingHintWithClientFlag()
    {
        Writer("opencode").Install(_workspace).Should().BeTrue();

        File.Exists(PluginPath()).Should().BeTrue();
        string ts = File.ReadAllText(PluginPath());
        ts.Should().Contain("experimental.chat.messages.transform",
            "the prompt-aware injection point is the only one that sees the user's message");
        ts.Should().Contain("\"hint\", \"--client\", \"opencode\"",
            "the plugin shells out to the client-tailored hint");
        ts.Should().Contain("try").And.Contain("catch",
            "the experimental API must be wrapped so drift degrades to no-hint, not a crash");
        // The resolved scri path is baked in (no reliance on PATH at runtime).
        ts.Should().Contain(AgentHookSetup.ResolveScriExecutablePath().Trim('"'));
    }

    [Fact]
    public void Uninstall_DeletesOurFile_ButNotAUserAuthoredOne()
    {
        var writer = Writer("opencode");
        writer.Install(_workspace).Should().BeTrue();
        writer.Uninstall(_workspace).Should().BeTrue();
        File.Exists(PluginPath()).Should().BeFalse();

        // A user-authored scrinia.ts (no marker) must be left alone.
        Directory.CreateDirectory(Path.GetDirectoryName(PluginPath())!);
        File.WriteAllText(PluginPath(), "// my own plugin\nexport const X = 1\n");
        writer.Uninstall(_workspace).Should().BeTrue();
        File.Exists(PluginPath()).Should().BeTrue("uninstall must not delete a file it didn't generate");
    }

    [Fact]
    public void GetStatus_NotInstalled_ThenInstalled()
    {
        var writer = Writer("opencode");
        writer.GetStatus(_workspace).Should().Be(HookStatus.NotInstalled);
        writer.Install(_workspace).Should().BeTrue();
        writer.GetStatus(_workspace).Should().Be(HookStatus.Installed);
    }
}
