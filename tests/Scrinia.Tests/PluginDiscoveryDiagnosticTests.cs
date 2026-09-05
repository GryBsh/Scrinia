using System.Runtime.InteropServices;
using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

/// <summary>
/// Phase 5 Task 2 — mandatory version/RID plugin discovery under <c>SCRINIA_HOME</c>.
///
/// <para>The runtime plugin discovery must resolve the plugin executable only through the mandatory
/// <c>SCRINIA_HOME/plugins/&lt;plugin&gt;/&lt;version&gt;/&lt;rid&gt;/</c> hierarchy: it selects the
/// configured compatible version and the current RID deterministically, proves the resolved directory
/// stays inside <c>SCRINIA_HOME</c> via <see cref="PathContainment.IsWithin"/>, and never accepts a
/// flat <c>plugins/&lt;plugin&gt;&lt;ext&gt;</c> or a <c>plugins/&lt;plugin&gt;/&lt;plugin&gt;</c> layout.</para>
///
/// <para>These tests drive the internal <see cref="WorkspaceSetup.ResolvePluginDirectory"/> directly with
/// controlled plugin-name/version inputs and an isolated <c>SCRINIA_HOME</c>, so they assert exactly
/// which version and RID are selected, the missing-version/missing-RID fallbacks, the containment
/// rejections, and the diagnostic text — without starting a plugin process.</para>
///
/// <para><c>ResolvePluginDirectory</c> reads <c>SCRINIA_HOME</c> through Phase 2's
/// <see cref="UserHome.Resolve"/> (set via the <c>SCRINIA_HOME</c> environment variable here), so the
/// class lives in the global workspace-config collection that serializes such process-wide mutations.</para>
/// </summary>
[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class PluginDiscoveryDiagnosticTests : IDisposable
{
    private const string ScriniaHomeVar = "SCRINIA_HOME";
    private const string PluginName = "scri-plugin-embeddings";

    private readonly string _home;
    private string? _previousHome;

    public PluginDiscoveryDiagnosticTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"scrinia_plugin_home_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);

        // Phase 2 resolver honors an isolated, non-empty absolute SCRINIA_HOME exactly.
        _previousHome = Environment.GetEnvironmentVariable(ScriniaHomeVar);
        Environment.SetEnvironmentVariable(ScriniaHomeVar, _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ScriniaHomeVar, _previousHome);
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    private static string CurrentRid() => RuntimeInformation.RuntimeIdentifier;

    /// <summary>Creates the mandatory <c>plugins/&lt;plugin&gt;/&lt;version&gt;/&lt;rid&gt;/</c> tree with a
    /// single executable named after the plugin and returns the resolved directory.</summary>
    private string CreateMandatoryPlugin(string version, string rid)
    {
        string dir = Path.Combine(_home, "plugins", PluginName, version, rid);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{PluginName}.exe"), "plugin-exe-bytes");
        return dir;
    }

    private string PluginsRoot() => Path.Combine(_home, "plugins", PluginName);

    [Fact]
    public void ResolvesExactMandatoryHierarchy_WhenConfiguredVersionAndCurrentRidPresent()
    {
        string version = "1.2.3";
        string expected = Path.Combine(_home, "plugins", PluginName, version, CurrentRid());
        CreateMandatoryPlugin(version, CurrentRid());

        var result = WorkspaceSetup.ResolvePluginDirectory(PluginName, version);

        result.PluginDir.Should().Be(expected);
        result.Degraded.Should().BeFalse();
    }

    [Fact]
    public void SelectsConfiguredVersion_OverHigherInstalledVersion()
    {
        // Two versions installed; the configured (lower) one must win, proving version selection is
        // configured-driven rather than "highest wins".
        string configured = "1.2.3";
        string higher = "2.0.0";
        CreateMandatoryPlugin(configured, CurrentRid());
        CreateMandatoryPlugin(higher, CurrentRid());

        var result = WorkspaceSetup.ResolvePluginDirectory(PluginName, configured);

        result.PluginDir.Should().Be(Path.Combine(_home, "plugins", PluginName, configured, CurrentRid()));
    }

    [Fact]
    public void SelectsCurrentRid_AmoungMultipleInstalledRids()
    {
        string version = "1.2.3";
        CreateMandatoryPlugin(version, CurrentRid());
        string otherRid = CurrentRid() == "win-x64" ? "linux-x64" : "win-x64";
        string otherDir = Path.Combine(_home, "plugins", PluginName, version, otherRid);
        Directory.CreateDirectory(otherDir);
        File.WriteAllText(Path.Combine(otherDir, $"{PluginName}.exe"), "other-rid-exe");

        var result = WorkspaceSetup.ResolvePluginDirectory(PluginName, version);

        result.PluginDir.Should().Be(Path.Combine(_home, "plugins", PluginName, version, CurrentRid()));
        result.Degraded.Should().BeFalse();
    }

    [Fact]
    public void FallsBackToHighestVersion_WhenConfiguredVersionNotInstalled()
    {
        // Configured version 9.9.9 is absent; two compatible versions are installed — the highest wins.
        string missing = "9.9.9";
        string installedLower = "1.2.0";
        string installedHigher = "1.2.3";
        CreateMandatoryPlugin(installedLower, CurrentRid());
        CreateMandatoryPlugin(installedHigher, CurrentRid());

        var result = WorkspaceSetup.ResolvePluginDirectory(PluginName, missing);

        result.PluginDir.Should().Be(Path.Combine(_home, "plugins", PluginName, installedHigher, CurrentRid()));
    }

    [Fact]
    public void DegradesToAvailableRid_WhenCurrentRidMissing()
    {
        string version = "1.2.3";
        string otherRid = CurrentRid() == "win-x64" ? "linux-x64" : "win-x64";
        string otherDir = Path.Combine(_home, "plugins", PluginName, version, otherRid);
        Directory.CreateDirectory(otherDir);
        File.WriteAllText(Path.Combine(otherDir, $"{PluginName}.exe"), "other-rid-exe");

        var result = WorkspaceSetup.ResolvePluginDirectory(PluginName, version);

        result.PluginDir.Should().Be(otherDir);
        result.Degraded.Should().BeTrue("a missing current RID degrades to the single available RID");
    }

    [Fact]
    public void ReturnsNull_WhenPluginNotInstalled_NamingHierarchyAndHomeInDiagnostics()
    {
        var result = WorkspaceSetup.ResolvePluginDirectory(PluginName, null);

        result.PluginDir.Should().BeNull("no plugin hierarchy is installed under SCRINIA_HOME");
        result.Diagnostics.Should().Contain("SCRINIA_HOME");
        result.Diagnostics.Should().Contain(PluginName);
        result.Diagnostics.Should().Contain("<version>");
        result.Diagnostics.Should().Contain("<rid>");
        result.Diagnostics.Should().Contain(_home);
    }

    [Fact]
    public void RejectsFlatPluginLayout_PluginsUnderPlugin_NotSelected()
    {
        // Legacy flat layout: SCRINIA_HOME/plugins/<plugin>/<plugin> with no version/RID segments.
        string flatDir = Path.Combine(_home, "plugins", PluginName, PluginName);
        Directory.CreateDirectory(flatDir);
        File.WriteAllText(Path.Combine(flatDir, $"{PluginName}.exe"), "flat-exe");

        var result = WorkspaceSetup.ResolvePluginDirectory(PluginName, null);

        result.PluginDir.Should().BeNull("the flat plugins/<plugin>/<plugin> layout is not a valid version/RID path");
    }

    [Fact]
    public void RejectsAppContextBaseDirectoryPlugins_NeverScanned()
    {
        // A plugin sitting under the (immutable) executable directory must never be discovered.
        string appPlugins = Path.Combine(AppContext.BaseDirectory, "plugins", PluginName);
        string appVersionDir = Path.Combine(appPlugins, "1.2.3", CurrentRid());
        Directory.CreateDirectory(appVersionDir);
        File.WriteAllText(Path.Combine(appVersionDir, $"{PluginName}.exe"), "app-exe");

        var result = WorkspaceSetup.ResolvePluginDirectory(PluginName, "1.2.3");

        result.PluginDir.Should().BeNull("plugin discovery is rooted at SCRINIA_HOME, never AppContext.BaseDirectory");
    }

    [Fact]
    public void RejectsPathTraversalPluginName_NeverLeavesScriniaHome()
    {
        CreateMandatoryPlugin("1.2.3", CurrentRid());

        var result = WorkspaceSetup.ResolvePluginDirectory("../../../../etc", "1.2.3");

        result.PluginDir.Should().BeNull("a plugin name that escapes SCRINIA_HOME is rejected");
    }
}
