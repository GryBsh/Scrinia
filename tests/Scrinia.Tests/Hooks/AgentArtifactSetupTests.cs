using FluentAssertions;
using Scrinia.Commands.Hooks;

namespace Scrinia.Tests.Hooks;

/// <summary>
/// Tests for the <see cref="AgentArtifactSetup"/> orchestrator — chiefly the ordering contract that
/// AGENTS.md is written before the CLAUDE.md that imports it (so the <c>@AGENTS.md</c> target exists),
/// and that install/uninstall drive the installer set without throwing.
/// </summary>
public sealed class AgentArtifactSetupTests : IDisposable
{
    private readonly string _workspace;

    public AgentArtifactSetupTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"scrinia_artifactsetup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void BuiltInInstallers_WriteAgentsMd_BeforeClaudeMdImporter()
    {
        var types = AgentArtifactSetup.BuiltInInstallers.Select(i => i.GetType()).ToList();
        int agentsIdx = types.IndexOf(typeof(AgentsMdWriter));
        int claudeIdx = types.IndexOf(typeof(ClaudeMdWriter));

        agentsIdx.Should().BeGreaterThanOrEqualTo(0);
        claudeIdx.Should().BeGreaterThan(agentsIdx,
            "CLAUDE.md imports @AGENTS.md, so AGENTS.md must be written first");
    }

    [Fact]
    public async Task InstallAsync_RunsApplicableInstallers_InOrder()
    {
        var a = new RecordingArtifact("A", applies: true);
        var b = new RecordingArtifact("B", applies: false);
        var c = new RecordingArtifact("C", applies: true);
        var order = new List<string>();
        a.OnInstall = () => order.Add("A");
        b.OnInstall = () => order.Add("B");
        c.OnInstall = () => order.Add("C");

        int written = await AgentArtifactSetup.InstallAsync(
            _workspace, nonInteractive: true, installers: [a, b, c]);

        written.Should().Be(2, "only applicable installers run");
        order.Should().Equal("A", "C");
    }

    private sealed class RecordingArtifact : IAgentArtifactInstaller
    {
        private readonly bool _applies;
        public RecordingArtifact(string name, bool applies) { Name = name; _applies = applies; }
        public string Name { get; }
        public Action? OnInstall { get; set; }
        public bool Applies(string workspaceRoot) => _applies;
        public bool Install(string workspaceRoot) { OnInstall?.Invoke(); return true; }
        public bool Uninstall(string workspaceRoot) => true;
        public HookStatus GetStatus(string workspaceRoot) => HookStatus.NotInstalled;
    }
}
