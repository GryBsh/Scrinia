using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Scrinia.Commands.Hooks;
using Scrinia.Core.Process;

namespace Scrinia.Tests.Hooks;

/// <summary>
/// Tests for <see cref="ClaudeCodeHookInstaller"/>. Each test uses a temp workspace
/// directory in lieu of <c>~/.claude</c> (project scope semantics — same code path,
/// no env-var mangling required).
/// </summary>
public sealed class ClaudeCodeHookInstallerTests : IDisposable
{
    private readonly string _workspace;

    public ClaudeCodeHookInstallerTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"scrinia_hooks_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    private string ConfigPath() => Path.Combine(_workspace, ".claude", "settings.json");

    private static readonly IReadOnlyList<HookSpec> Specs =
    [
        new HookSpec("SessionStart", "scri restore"),
        new HookSpec("SessionEnd", "scri consolidate --auto"),
    ];

    private static ClaudeCodeHookInstaller NewInstaller(bool cliInstalled = true)
    {
        var runner = new FakeRunner(cliInstalled);
        return new ClaudeCodeHookInstaller(runner);
    }

    [Fact]
    public void IsCliInstalled_True_WhenClaudeOnPath()
    {
        NewInstaller(cliInstalled: true).IsCliInstalled().Should().BeTrue();
        NewInstaller(cliInstalled: false).IsCliInstalled().Should().BeFalse();
    }

    [Fact]
    public void Install_CreatesSettingsFile_WithBothHookEvents()
    {
        var installer = NewInstaller();

        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();

        File.Exists(ConfigPath()).Should().BeTrue();
        var root = ParseConfig();
        var hooks = root["hooks"]!.AsObject();
        hooks["SessionStart"]!.AsArray().Should().HaveCount(1);
        hooks["SessionEnd"]!.AsArray().Should().HaveCount(1);

        // Each managed block has our sentinel + the expected command.
        string startCmd = hooks["SessionStart"]![0]!["hooks"]![0]!["command"]!.GetValue<string>();
        startCmd.Should().Be("scri restore");
        string stopCmd = hooks["SessionEnd"]![0]!["hooks"]![0]!["command"]!.GetValue<string>();
        stopCmd.Should().Be("scri consolidate --auto");

        hooks["SessionStart"]![0]![ClaudeCodeHookInstaller.ManagedMarker].Should().NotBeNull();
    }

    [Fact]
    public void Install_PreservesUserAuthoredHooks_InTheSameEvent()
    {
        // Pre-existing user hook + setting in the same file. Our install must not eat them.
        Directory.CreateDirectory(Path.Combine(_workspace, ".claude"));
        File.WriteAllText(ConfigPath(), """
        {
          "theme": "dark",
          "hooks": {
            "SessionStart": [
              { "matcher": "user-hook", "hooks": [{ "type": "command", "command": "echo user-pre" }] }
            ]
          }
        }
        """);

        var installer = NewInstaller();
        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();

        var root = ParseConfig();
        root["theme"]!.GetValue<string>().Should().Be("dark", "user-authored top-level settings must survive");

        var startArr = root["hooks"]!["SessionStart"]!.AsArray();
        startArr.Should().HaveCount(2, "user's hook + our managed block coexist");

        // User's hook is the one without our sentinel and matcher == "user-hook"
        var userHook = startArr.FirstOrDefault(n => n!["matcher"]!.GetValue<string>() == "user-hook");
        userHook.Should().NotBeNull();
        userHook!["hooks"]![0]!["command"]!.GetValue<string>().Should().Be("echo user-pre");
    }

    [Fact]
    public void Install_IsIdempotent_DoesNotDuplicateOurBlock()
    {
        var installer = NewInstaller();
        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();
        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();

        var hooks = ParseConfig()["hooks"]!.AsObject();
        hooks["SessionStart"]!.AsArray().Should().HaveCount(1, "second install must update, not duplicate");
        hooks["SessionEnd"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public void Install_UpdatesCommand_WhenSpecChanged()
    {
        var installer = NewInstaller();
        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();

        IReadOnlyList<HookSpec> updated =
        [
            new HookSpec("SessionStart", "scri restore --new-flag"),
            new HookSpec("SessionEnd", "scri consolidate --auto"),
        ];
        installer.InstallHooks(HookScope.Project, _workspace, updated).Should().BeTrue();

        var hooks = ParseConfig()["hooks"]!.AsObject();
        hooks["SessionStart"]!.AsArray().Should().HaveCount(1);
        hooks["SessionStart"]![0]!["hooks"]![0]!["command"]!.GetValue<string>()
            .Should().Be("scri restore --new-flag");
    }

    [Fact]
    public void Uninstall_RemovesOnlyOurBlock_PreservingUserHooks()
    {
        var installer = NewInstaller();
        Directory.CreateDirectory(Path.Combine(_workspace, ".claude"));
        File.WriteAllText(ConfigPath(), """
        {
          "hooks": {
            "SessionStart": [
              { "matcher": "user", "hooks": [{ "type": "command", "command": "echo user" }] }
            ]
          }
        }
        """);

        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();
        installer.UninstallHooks(HookScope.Project, _workspace).Should().BeTrue();

        var hooks = ParseConfig()["hooks"]!.AsObject();
        var startArr = hooks["SessionStart"]!.AsArray();
        startArr.Should().HaveCount(1);
        startArr[0]!["matcher"]!.GetValue<string>().Should().Be("user");

        // Stop event had only our hook → key should be removed entirely.
        hooks.ContainsKey("SessionEnd").Should().BeFalse();
    }

    [Fact]
    public void Uninstall_RemovesHooksKey_WhenItBecomesEmpty()
    {
        var installer = NewInstaller();
        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();
        installer.UninstallHooks(HookScope.Project, _workspace).Should().BeTrue();

        var root = ParseConfig();
        root.ContainsKey("hooks").Should().BeFalse(
            "with no user hooks present, the hooks key should be removed entirely after uninstall");
    }

    [Fact]
    public void GetStatus_NotInstalled_WhenNoFile()
    {
        NewInstaller().GetStatus(HookScope.Project, _workspace, Specs)
            .Should().Be(HookStatus.NotInstalled);
    }

    [Fact]
    public void GetStatus_Installed_AfterFreshInstall()
    {
        var installer = NewInstaller();
        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();

        installer.GetStatus(HookScope.Project, _workspace, Specs).Should().Be(HookStatus.Installed);
    }

    [Fact]
    public void GetStatus_Drift_WhenUserEditsOurCommand()
    {
        var installer = NewInstaller();
        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();

        // User manually edits the command field in our managed block.
        var root = ParseConfig();
        root["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["command"] = "scri something-different";
        File.WriteAllText(ConfigPath(), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        installer.GetStatus(HookScope.Project, _workspace, Specs).Should().Be(HookStatus.Drift);
    }

    [Fact]
    public void GetStatus_Partial_WhenOnlySomeEventsInstalled()
    {
        var installer = NewInstaller();
        IReadOnlyList<HookSpec> singleSpec = [new HookSpec("SessionStart", "scri restore")];
        installer.InstallHooks(HookScope.Project, _workspace, singleSpec).Should().BeTrue();

        installer.GetStatus(HookScope.Project, _workspace, Specs).Should().Be(HookStatus.Partial);
    }

    private JsonObject ParseConfig()
    {
        string raw = File.ReadAllText(ConfigPath());
        return (JsonObject)JsonNode.Parse(raw)!;
    }

    private sealed class FakeRunner : IProcessRunner
    {
        private readonly bool _onPath;
        public FakeRunner(bool onPath) => _onPath = onPath;
        public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string? s, CancellationToken c) =>
            Task.FromResult(new ProcessResult(0, "", "", false));
        public bool IsExecutableOnPath(string executable) => _onPath;
        public string? ResolveExecutable(string executable) => _onPath ? $"/fake/{executable}" : null;
    }
}
