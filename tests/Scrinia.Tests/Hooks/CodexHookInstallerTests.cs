using System.Text.Json.Nodes;
using FluentAssertions;
using Scrinia.Commands.Hooks;
using Scrinia.Core.Process;

namespace Scrinia.Tests.Hooks;

/// <summary>
/// Mirror of <see cref="ClaudeCodeHookInstallerTests"/> for the Codex adapter.
/// Same JSON shape, different config directory. Tests cover install / idempotency /
/// user-content preservation / uninstall / drift detection.
/// </summary>
public sealed class CodexHookInstallerTests : IDisposable
{
    private readonly string _workspace;

    public CodexHookInstallerTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"scrinia_codex_hooks_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    private string ConfigPath() => Path.Combine(_workspace, ".codex", "hooks.json");

    private static readonly IReadOnlyList<HookSpec> Specs =
    [
        new HookSpec("SessionStart", "scri restore"),
        new HookSpec("UserPromptSubmit", "scri hint"),
    ];

    private static CodexHookInstaller NewInstaller(bool cliInstalled = true) =>
        new(new FakeRunner(cliInstalled));

    [Fact]
    public void IsCliInstalled_True_WhenCodexOnPath()
    {
        NewInstaller(cliInstalled: true).IsCliInstalled().Should().BeTrue();
        NewInstaller(cliInstalled: false).IsCliInstalled().Should().BeFalse();
    }

    [Fact]
    public void Install_WritesHooksJsonAtCodexPath_WithBothEvents()
    {
        var installer = NewInstaller();
        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();

        File.Exists(ConfigPath()).Should().BeTrue();
        var root = ParseConfig();
        root["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["command"]!.GetValue<string>()
            .Should().Be("scri restore");
        root["hooks"]!["UserPromptSubmit"]![0]!["hooks"]![0]!["command"]!.GetValue<string>()
            .Should().Be("scri hint");
    }

    [Fact]
    public void SupportsEvent_SessionEnd_ReturnsFalse()
    {
        // Codex (as of CLI ~0.124+) has only per-turn Stop — no once-per-session terminator.
        // The orchestrator uses this signal to skip the canonical SessionEnd hook on Codex
        // rather than mis-wire it to per-turn Stop.
        NewInstaller().SupportsEvent("SessionEnd").Should().BeFalse();
    }

    [Fact]
    public void SupportsEvent_OtherCanonicalEvents_ReturnTrue()
    {
        var installer = NewInstaller();
        installer.SupportsEvent("SessionStart").Should().BeTrue();
        installer.SupportsEvent("UserPromptSubmit").Should().BeTrue();
        installer.SupportsEvent("PreToolUse").Should().BeTrue();
        installer.SupportsEvent("PostToolUse").Should().BeTrue();
    }

    [Fact]
    public void Install_PreservesUserHooksJsonContent()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, ".codex"));
        File.WriteAllText(ConfigPath(), """
        {
          "global": { "timeout": 60 },
          "hooks": {
            "PreToolUse": [
              { "matcher": "git_*", "hooks": [{ "type": "command", "command": "echo user-pre" }] }
            ]
          }
        }
        """);

        var installer = NewInstaller();
        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();

        var root = ParseConfig();
        root["global"]!["timeout"]!.GetValue<int>().Should().Be(60);
        root["hooks"]!["PreToolUse"]!.AsArray().Should().HaveCount(1);
        root["hooks"]!["PreToolUse"]![0]!["matcher"]!.GetValue<string>().Should().Be("git_*");
    }

    [Fact]
    public void Install_IsIdempotent()
    {
        var installer = NewInstaller();
        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();
        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();

        ParseConfig()["hooks"]!["SessionStart"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public void Uninstall_RemovesOurBlock_KeepsUserHook()
    {
        var installer = NewInstaller();
        Directory.CreateDirectory(Path.Combine(_workspace, ".codex"));
        File.WriteAllText(ConfigPath(), """
        {
          "hooks": {
            "PreToolUse": [
              { "matcher": "user", "hooks": [{ "type": "command", "command": "echo u" }] }
            ]
          }
        }
        """);
        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();
        installer.UninstallHooks(HookScope.Project, _workspace).Should().BeTrue();

        var root = ParseConfig();
        root["hooks"]!["PreToolUse"]!.AsArray().Should().HaveCount(1);
        root["hooks"]!.AsObject().ContainsKey("SessionStart").Should().BeFalse();
    }

    [Fact]
    public void GetStatus_Installed_AfterFreshInstall()
    {
        var installer = NewInstaller();
        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();
        installer.GetStatus(HookScope.Project, _workspace, Specs).Should().Be(HookStatus.Installed);
    }

    [Fact]
    public void GetStatus_Drift_AfterUserEdit()
    {
        var installer = NewInstaller();
        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();
        var root = ParseConfig();
        root["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["command"] = "scri something-else";
        File.WriteAllText(ConfigPath(), root.ToJsonString());

        installer.GetStatus(HookScope.Project, _workspace, Specs).Should().Be(HookStatus.Drift);
    }

    private JsonObject ParseConfig() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(ConfigPath()))!;

    private sealed class FakeRunner(bool onPath) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string? s, CancellationToken c)
            => Task.FromResult(new ProcessResult(0, "", "", false));
        public bool IsExecutableOnPath(string executable) => onPath;
        public string? ResolveExecutable(string executable) => onPath ? $"/fake/{executable}" : null;
    }
}
