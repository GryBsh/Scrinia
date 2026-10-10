using System.Runtime.InteropServices;
using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Core.Configuration;
using Scrinia.TestDoubles;

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
/// <para>These tests drive the internal <see cref="EmbeddingsHost.ResolvePluginDirectory"/> directly with
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
    private readonly ProcessEnvironmentLock _environmentLock;
    private EnvironmentVariableScope? _homeScope;

    public PluginDiscoveryDiagnosticTests()
    {
        _environmentLock = ProcessEnvironmentLock.Acquire();
        _home = Path.Combine(Path.GetTempPath(), $"scrinia_plugin_home_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);

        // Phase 2 resolver honors an isolated, non-empty absolute SCRINIA_HOME exactly.
        _homeScope = new EnvironmentVariableScope(ScriniaHomeVar, _home);
    }

    public void Dispose()
    {
        _homeScope?.Dispose();
        try { Directory.Delete(_home, recursive: true); } catch { }
        _environmentLock.Dispose();
    }

    private static string CurrentRid() => EmbeddingsRuntimeIdentity.CurrentRid;

    [Fact]
    public void PortableBundleRidResolvesWithoutDegradation()
    {
        string portable = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsMacOS() ? "osx-arm64" : "linux-x64";
        string expected = CreateMandatoryPlugin("0.6.0", portable);
        var resolution = EmbeddingsHost.ResolvePluginDirectory(PluginName, "0.6.0");
        resolution.PluginDir.Should().Be(expected);
        resolution.Degraded.Should().BeFalse();
        resolution.Diagnostics.Should().NotContain("degraded");
    }

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

        var result = EmbeddingsHost.ResolvePluginDirectory(PluginName, version);

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

        var result = EmbeddingsHost.ResolvePluginDirectory(PluginName, configured);

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

        var result = EmbeddingsHost.ResolvePluginDirectory(PluginName, version);

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

        var result = EmbeddingsHost.ResolvePluginDirectory(PluginName, missing);

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

        var result = EmbeddingsHost.ResolvePluginDirectory(PluginName, version);

        result.PluginDir.Should().Be(otherDir);
        result.Degraded.Should().BeTrue("a missing current RID degrades to the single available RID");
    }

    [Fact]
    public void ReturnsNull_WhenPluginNotInstalled_NamingHierarchyAndHomeInDiagnostics()
    {
        var result = EmbeddingsHost.ResolvePluginDirectory(PluginName, null);

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

        var result = EmbeddingsHost.ResolvePluginDirectory(PluginName, null);

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

        var result = EmbeddingsHost.ResolvePluginDirectory(PluginName, "1.2.3");

        result.PluginDir.Should().BeNull("plugin discovery is rooted at SCRINIA_HOME, never AppContext.BaseDirectory");
    }

    [Fact]
    public void RejectsPathTraversalPluginName_NeverLeavesScriniaHome()
    {
        CreateMandatoryPlugin("1.2.3", CurrentRid());

        var result = EmbeddingsHost.ResolvePluginDirectory("../../../../etc", "1.2.3");

        result.PluginDir.Should().BeNull("a plugin name that escapes SCRINIA_HOME is rejected");
    }
}

/// <summary>
/// Phase 5 Task 7 — the exact living-document headings and source diagnostics.
///
/// <para>These tests pin the Phase 5-owned documentation contract: every required <c>##</c>/<c>###</c>
/// heading is present in its owned living doc, the layout is described in terms of
/// <c>SCRINIA_HOME/models</c> and the mandatory
/// <c>SCRINIA_HOME/plugins/&lt;plugin&gt;&lt;/version&gt;/&lt;rid&gt;/</c> hierarchy, Vulkan is a
/// separate install, and there is no flat plugin fallback. They also assert that no stale
/// executable-relative <c>{exeDir}/models</c> or <c>{exeDir}/plugins</c> reference survives in the
/// owned living docs, the package readme, the setup command, or <c>WorkspaceSetup</c>.</para>
///
/// <para>The scan scope is exactly the Phase 5-owned living docs and source. Historical artifacts —
/// <c>docs/superpowers/**</c>, <c>docs/reviews/**</c>, <c>docs/planning/**</c>, dated
/// planning/review snapshots elsewhere under <c>docs/</c>, and the released CHANGELOG history — are
/// deliberately excluded and retain their historical-doc exemption.</para>
/// </summary>
public sealed class Phase5DocumentationContractTests
{
    /// <summary>The exact Phase 5-owned living docs + source/help scope (mirrors the Task 7 rg scan).</summary>
    private static IReadOnlyList<string> OwnedDocFiles() =>
    [
        "README.md",
        "docs/getting-started.md",
        "docs/cli-reference.md",
        "docs/security.md",
        "docs/troubleshooting.md",
        "docs/plugin-authoring.md",
        "docs/installation.md",
        "docs/architecture/cli.md",
        "src/Scrinia/package-readme.md",
    ];

    /// <summary>The Phase 5-owned source files that carry path diagnostics / setup output.</summary>
    private static IReadOnlyList<string> OwnedSourceFiles() =>
    [
        "src/Scrinia/Commands/ScriniaCommands.Setup.cs",
        "src/Scrinia/Commands/WorkspaceSetup.cs",
    ];

    private static string Read(string relativePath) =>
        File.ReadAllText(RepoPaths.Combine(relativePath));

    private static string ReadAllOwnedDocs() => string.Join("\n", OwnedDocFiles().Select(Read));

    private static string ReadAllOwnedSource() => string.Join("\n", OwnedSourceFiles().Select(Read));

    private static void AssertHeadingPresent(string file, string heading)
    {
        string content = Read(file);
        content.Should().Contain(heading, $"`{file}` is missing the exact Phase 5 heading \"{heading}\"");
    }

    [Fact]
    public void README_has_Install_and_Where_data_lives_headings()
    {
        AssertHeadingPresent("README.md", "## Install");
        AssertHeadingPresent("README.md", "## Where data lives");
    }

    [Fact]
    public void GettingStarted_has_Model2Vec_and_Install_the_global_tool_headings()
    {
        AssertHeadingPresent("docs/getting-started.md", "## Model2Vec");
        AssertHeadingPresent("docs/getting-started.md", "### Install the global tool");
    }

    [Fact]
    public void Security_has_Plugin_trust_and_Immutable_tool_package_headings()
    {
        AssertHeadingPresent("docs/security.md", "### Plugin trust");
        AssertHeadingPresent("docs/security.md", "## Immutable tool package");
    }

    [Fact]
    public void Architecture_cli_has_Embeddings_integration_and_Plugin_host_headings()
    {
        AssertHeadingPresent("docs/architecture/cli.md", "## Embeddings Integration");
        AssertHeadingPresent("docs/architecture/cli.md", "## Plugin Host");
    }

    [Fact]
    public void PluginAuthoring_has_Plugin_location_and_installation_heading()
    {
        AssertHeadingPresent("docs/plugin-authoring.md", "## Plugin location and installation");
    }

    [Fact]
    public void Troubleshooting_has_all_six_phase_5_headings()
    {
        AssertHeadingPresent("docs/troubleshooting.md", "### Model not found");
        AssertHeadingPresent("docs/troubleshooting.md", "### Semantic search not working");
        AssertHeadingPresent("docs/troubleshooting.md", "### Vulkan plugin not loading");
        AssertHeadingPresent("docs/troubleshooting.md", "### Plugin crashes / degraded to BM25-only");
        AssertHeadingPresent("docs/troubleshooting.md", "### Plugin not found despite being installed");
        AssertHeadingPresent("docs/troubleshooting.md", "### Access denied when publishing");
    }

    [Fact]
    public void Installation_doc_covers_tool_layout_and_plugin_hierarchy()
    {
        string installation = Read("docs/installation.md");
        installation.Should().NotBeEmpty("docs/installation.md must exist");

        // Immutable tool + SCRINIA_HOME models layout.
        installation.Should().Contain("Immutable tool", "the package is immutable once installed");
        installation.Should().Contain("SCRINIA_HOME/models", "built-in and plugin models live under SCRINIA_HOME/models");
        installation
            .Should()
            .Contain("SCRINIA_HOME/plugins/", "separately installed plugins live at the mandatory SCRINIA_HOME/plugins path");

        // Separate Vulkan install + mandatory version/RID hierarchy.
        installation.Should().Contain("Vulkan", "Vulkan is documented");
        installation.Should().Contain("<version>", "the mandatory plugin hierarchy names <version>");
        installation.Should().Contain("<rid>", "the mandatory plugin hierarchy names <rid>");
    }

    [Fact]
    public void Owned_docs_state_mandatory_plugin_path_and_separate_vulkan_and_no_flat_fallback()
    {
        string allDocs = ReadAllOwnedDocs();

        // Mandatory version/RID hierarchy is named explicitly somewhere in the owned scope.
        allDocs.Should().Contain("<plugin>", "the mandatory plugin hierarchy names <plugin>");
        allDocs.Should().Contain("<version>", "the mandatory plugin hierarchy names <version>");
        allDocs.Should().Contain("<rid>", "the mandatory plugin hierarchy names <rid>");

        // Separate Vulkan install is stated.
        allDocs
            .Should()
            .Contain("separately installed", "Vulkan / plugins are separately installed, not part of the immutable tool");

        // The absence of a flat plugin fallback is stated somewhere in the owned scope.
        bool statesNoFlatFallback = OwnedDocFiles()
            .Select(Read)
            .Any(c => c.Contains("no flat", StringComparison.OrdinalIgnoreCase) ||
                      c.Contains("never a flat", StringComparison.OrdinalIgnoreCase));
        statesNoFlatFallback.Should().BeTrue(
            "the owned docs state there is no flat plugin fallback");
    }

    [Fact]
    public void Owned_scope_has_no_stale_executable_relative_model_or_plugin_paths()
    {
        string docs = ReadAllOwnedDocs();
        string source = ReadAllOwnedSource();

        // Doc / help / setup-output form: the executable-relative placeholder paths.
        foreach (string stale in new[] { "{exeDir}/models", "{exeDir}/plugins" })
        {
            docs.Should().NotContain(stale, $"the stale executable-relative reference \"{stale}\" must not survive in owned living docs");
            source.Should().NotContain(stale, $"the stale executable-relative reference \"{stale}\" must not survive in owned source");
        }

        // Source form: the executable-relative models/plugins directory construction.
        foreach (string stale in new[] { "Path.Combine(exeDir, \"models\")", "Path.Combine(exeDir, \"plugins\")" })
        {
            source.Should().NotContain(stale, $"the stale executable-relative path consumer \"{stale}\" must not survive in owned source");
        }
    }

    [Fact]
    public void Setup_output_names_SCRINIA_HOME_models_and_mandatory_plugin_hierarchy()
    {
        string setup = Read("src/Scrinia/Commands/ScriniaCommands.Setup.cs");
        setup.Should().Contain("SCRINIA_HOME/models", "setup output paths the GGUF model under SCRINIA_HOME/models");
        setup
            .Should()
            .Contain("ResolveEmbeddingsPluginExe", "setup discovers the plugin through the mandatory SCRINIA_HOME hierarchy, not a flat scan");
    }
}
