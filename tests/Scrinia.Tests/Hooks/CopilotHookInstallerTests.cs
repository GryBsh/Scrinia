using System.Text.Json.Nodes;
using FluentAssertions;
using Scrinia.Commands.Hooks;
using Scrinia.Core.Process;

namespace Scrinia.Tests.Hooks;

/// <summary>
/// Tests for <see cref="CopilotHookInstaller"/>. Copilot owns its own
/// <c>scrinia.json</c> file inside the hooks directory so install = overwrite, uninstall
/// = delete. Event names get translated from canonical PascalCase to Copilot's
/// camelCase (<c>SessionStart</c> → <c>sessionStart</c>, <c>Stop</c> →
/// <c>sessionEnd</c>).
/// </summary>
public sealed class CopilotHookInstallerTests : IDisposable
{
    private readonly string _workspace;

    public CopilotHookInstallerTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"scrinia_copilot_hooks_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    private string ConfigPath() => Path.Combine(_workspace, ".github", "hooks", "scrinia.json");

    private static readonly IReadOnlyList<HookSpec> Specs =
    [
        new HookSpec("SessionStart", "scri restore"),
        new HookSpec("SessionEnd", "scri consolidate --auto"),
    ];

    private static CopilotHookInstaller NewInstaller(bool cliInstalled = true) =>
        new(new FakeRunner(cliInstalled));

    [Fact]
    public void EventNameMapping_PascalCaseToCamelCase()
    {
        CopilotHookInstaller.ToCopilotEvent("SessionStart").Should().Be("sessionStart");
        CopilotHookInstaller.ToCopilotEvent("SessionEnd").Should().Be("sessionEnd");
        CopilotHookInstaller.ToCopilotEvent("UserPromptSubmit").Should().Be("userPromptSubmitted");
        CopilotHookInstaller.ToCopilotEvent("UnknownFutureEvent").Should().BeEmpty();
    }

    [Fact]
    public void Install_WritesScriniaJson_WithCamelCaseEventKeys()
    {
        var installer = NewInstaller();
        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();

        File.Exists(ConfigPath()).Should().BeTrue();
        var root = ParseConfig();
        var hooks = root["hooks"]!.AsObject();
        hooks.ContainsKey("sessionStart").Should().BeTrue();
        hooks.ContainsKey("sessionEnd").Should().BeTrue();
        hooks.ContainsKey("SessionStart").Should().BeFalse("Copilot uses camelCase");

        hooks["sessionStart"]![0]!["hooks"]![0]!["command"]!.GetValue<string>()
            .Should().Be("scri restore");
        hooks["sessionEnd"]![0]!["hooks"]![0]!["command"]!.GetValue<string>()
            .Should().Be("scri consolidate --auto");
    }

    [Fact]
    public void Install_IsIdempotent_OverwritesFileSameContent()
    {
        var installer = NewInstaller();
        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();
        string firstWrite = File.ReadAllText(ConfigPath());

        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();
        string secondWrite = File.ReadAllText(ConfigPath());

        secondWrite.Should().Be(firstWrite);
    }

    [Fact]
    public void Uninstall_DeletesScriniaJsonFile()
    {
        var installer = NewInstaller();
        installer.InstallHooks(HookScope.Project, _workspace, Specs).Should().BeTrue();
        File.Exists(ConfigPath()).Should().BeTrue();

        installer.UninstallHooks(HookScope.Project, _workspace).Should().BeTrue();
        File.Exists(ConfigPath()).Should().BeFalse();
    }

    [Fact]
    public void Uninstall_NoOp_WhenFileMissing()
    {
        NewInstaller().UninstallHooks(HookScope.Project, _workspace).Should().BeTrue();
    }

    [Fact]
    public void GetStatus_NotInstalled_WhenFileMissing()
    {
        NewInstaller().GetStatus(HookScope.Project, _workspace, Specs).Should().Be(HookStatus.NotInstalled);
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
        root["hooks"]!["sessionStart"]![0]!["hooks"]![0]!["command"] = "scri tampered";
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
