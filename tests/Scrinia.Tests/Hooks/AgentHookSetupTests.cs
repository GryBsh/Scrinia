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
    public void ResolveScriExecutablePath_ReturnsCurrentProcessPath()
    {
        // The hook commands embed the full path so they work regardless of the PATH
        // the agent CLI inherits when it spawns the hook process. Test that we use
        // Environment.ProcessPath (or a quoted form of it) as the source.
        string resolved = AgentHookSetup.ResolveScriExecutablePath();
        string? processPath = Environment.ProcessPath;

        resolved.Should().NotBeNullOrEmpty();
        if (string.IsNullOrEmpty(processPath))
        {
            // Defensive fallback path — extremely unlikely on .NET 6+.
            resolved.Should().Be("scri");
            return;
        }

        // Either the raw path (when there are no spaces) or the same path wrapped in
        // double quotes (when there are). Both indicate ProcessPath is the source.
        bool isQuoted = resolved.StartsWith('"') && resolved.EndsWith('"');
        string unquoted = isQuoted ? resolved.Trim('"') : resolved;

        // On Windows the resolved path has backslashes converted to forward slashes
        // (see ResolveScriExecutablePath docs) so compare against the converted form.
        string expectedPath = OperatingSystem.IsWindows()
            ? processPath.Replace('\\', '/')
            : processPath;
        unquoted.Should().Be(expectedPath);

        // If the path has whitespace, quoting MUST be applied (otherwise the agent
        // CLI's shell will tokenise mid-path and the hook silently fails).
        if (expectedPath.Contains(' '))
            isQuoted.Should().BeTrue("paths with spaces must be quoted in shell command strings");
    }

    [Fact]
    public void ResolveScriExecutablePath_OnWindows_UsesForwardSlashes()
    {
        // Claude Code and similar agent CLIs may spawn hooks via git bash on Windows
        // even when the user's interactive terminal is PowerShell. bash interprets `\`
        // as an escape, so the resolved path must use forward slashes to round-trip
        // safely through every shell.
        if (!OperatingSystem.IsWindows()) return;

        string resolved = AgentHookSetup.ResolveScriExecutablePath();
        resolved.Should().NotContain("\\",
            "on Windows, backslashes break in git bash; forward slashes work in bash, PowerShell, and cmd alike");
    }

    [Fact]
    public void DefaultHookSpecs_EmbedResolvedScriPath_NotBareName()
    {
        string scri = AgentHookSetup.ResolveScriExecutablePath();
        foreach (var spec in AgentHookSetup.DefaultHookSpecs)
        {
            spec.Command.Should().StartWith(scri,
                $"each canonical hook command must invoke the resolved scri path so the hook " +
                $"works even when PATH doesn't contain scrinia's install dir (event: {spec.EventName})");
        }
    }

    [Fact]
    public void DefaultHookSpecs_SessionStart_PassesHookFlagToRestore()
    {
        // SessionStart must invoke `scri restore --hook` so the YAML output gets wrapped
        // in the hookSpecificOutput JSON envelope each big-3 agent CLI understands —
        // bare `scri restore` would emit raw YAML and the model would see it as a status
        // dump rather than instructions framed as <scrinia-restored-memory>.
        var spec = AgentHookSetup.DefaultHookSpecs.Single(s => s.EventName == "SessionStart");
        spec.Command.Should().EndWith(" restore --hook");
    }

    [Fact]
    public void DefaultHookSpecs_UserPromptSubmit_InvokesHint()
    {
        // `scri hint` defaults to the hook JSON envelope already — no flag needed.
        var spec = AgentHookSetup.DefaultHookSpecs.Single(s => s.EventName == "UserPromptSubmit");
        spec.Command.Should().EndWith(" hint");
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
