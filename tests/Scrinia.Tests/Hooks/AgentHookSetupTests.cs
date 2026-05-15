using FluentAssertions;
using Scrinia.Commands.Hooks;

namespace Scrinia.Tests.Hooks;

/// <summary>
/// Tests for <see cref="AgentHookSetup"/> orchestrator. Focuses on the per-installer event
/// filtering — the universal <see cref="AgentHookSetup.DefaultHookSpecs"/> set gets passed
/// to each installer with unsupported events stripped out, so e.g. Codex never receives a
/// SessionEnd hook spec (it has no such event).
/// </summary>
public sealed class AgentHookSetupTests
{
    [Fact]
    public async Task InstallAsync_FiltersUnsupportedEvents_BeforeReachingInstaller()
    {
        var installer = new RecordingInstaller(
            supportsPredicate: e => e != "SessionEnd",
            isCliInstalled: true);

        IReadOnlyList<HookSpec> specs =
        [
            new HookSpec("SessionStart", "scri restore"),
            new HookSpec("SessionEnd", "scri consolidate --auto"),
            new HookSpec("UserPromptSubmit", "scri hint"),
        ];

        int configured = await AgentHookSetup.InstallAsync(
            HookScope.User, workspaceRoot: null, nonInteractive: true,
            installers: [installer], specs: specs);

        configured.Should().Be(1);
        installer.ReceivedSpecs.Should().NotBeNull();
        installer.ReceivedSpecs!.Select(s => s.EventName)
            .Should().BeEquivalentTo(["SessionStart", "UserPromptSubmit"],
                "SessionEnd is unsupported on this fake CLI and must be filtered out");
    }

    [Fact]
    public async Task InstallAsync_SkipsInstaller_WhenAllSpecsUnsupported()
    {
        var installer = new RecordingInstaller(
            supportsPredicate: _ => false,
            isCliInstalled: true);

        IReadOnlyList<HookSpec> specs = [new HookSpec("SessionStart", "scri restore")];

        int configured = await AgentHookSetup.InstallAsync(
            HookScope.User, workspaceRoot: null, nonInteractive: true,
            installers: [installer], specs: specs);

        configured.Should().Be(0);
        installer.ReceivedSpecs.Should().BeNull("installer never gets called when no specs are supported");
    }

    [Fact]
    public async Task InstallAsync_SkipsCliNotOnPath()
    {
        var installer = new RecordingInstaller(
            supportsPredicate: _ => true,
            isCliInstalled: false);

        int configured = await AgentHookSetup.InstallAsync(
            HookScope.User, workspaceRoot: null, nonInteractive: true,
            installers: [installer], specs: AgentHookSetup.DefaultHookSpecs);

        configured.Should().Be(0);
        installer.ReceivedSpecs.Should().BeNull();
    }

    private sealed class RecordingInstaller : IAgentHookInstaller
    {
        private readonly Func<string, bool> _supports;
        private readonly bool _cliInstalled;

        public RecordingInstaller(Func<string, bool> supportsPredicate, bool isCliInstalled)
        {
            _supports = supportsPredicate;
            _cliInstalled = isCliInstalled;
        }

        public string CliName => "fake-cli";

        public bool IsCliInstalled() => _cliInstalled;

        public bool SupportsEvent(string canonicalEvent) => _supports(canonicalEvent);

        public IReadOnlyList<HookSpec>? ReceivedSpecs { get; private set; }

        public bool InstallHooks(HookScope scope, string? workspaceRoot, IReadOnlyList<HookSpec> specs)
        {
            ReceivedSpecs = [.. specs];
            return true;
        }

        public bool UninstallHooks(HookScope scope, string? workspaceRoot) => true;

        public HookStatus GetStatus(HookScope scope, string? workspaceRoot, IReadOnlyList<HookSpec> specs)
            => HookStatus.NotInstalled;
    }
}
