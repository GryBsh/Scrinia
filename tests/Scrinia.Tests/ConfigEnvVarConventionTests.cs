using FluentAssertions;
using Scrinia.Commands;

namespace Scrinia.Tests;

/// <summary>
/// WorkspaceSetup.GetConfigValue performed two environment lookups whose expressions produce a
/// byte-identical string for every key in this repository — no key contains a '.' or a doubled
/// underscore — so the second could never find anything the first did not. Its own doc comment
/// claimed SCRINIA__EMBEDDINGS__PROVIDER resolved. It did not.
///
/// <para>This matters across surfaces, not just within the CLI: the server builds configuration
/// through WebApplication.CreateBuilder, whose environment provider maps the double-underscore
/// form natively. The same key took one form on the server and a different one here.</para>
/// </summary>
[Collection(EnvVarConventionCollection.Name)]
public sealed class ConfigEnvVarConventionTests : IDisposable
{
    private const string Key = "Scrinia:Embeddings:Provider";
    private const string SingleForm = "SCRINIA_EMBEDDINGS_PROVIDER";
    private const string DoubleForm = "SCRINIA__EMBEDDINGS__PROVIDER";

    private readonly string _root;

    /// <summary>An isolated empty workspace root. Without it, the pre-fix run of
    /// DoubleUnderscoreForm_Resolves falls past both env lookups to
    /// WorkspaceConfig.GetValue(Root, key), and WorkspaceSetup.Root THROWS when Configure has
    /// not been called — so the expected red would be an InvalidOperationException, or worse a
    /// silent read of whatever workspace another test class left behind.</summary>
    public ConfigEnvVarConventionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_envvar_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        WorkspaceSetup.OverrideRoot(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SingleForm, null);
        Environment.SetEnvironmentVariable(DoubleForm, null);
        WorkspaceSetup.OverrideRoot(null);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary><c>WorkspaceSetup</c> is `internal static` and `src/Scrinia/Scrinia.csproj:30`
    /// declares `InternalsVisibleTo Scrinia.Tests`, so this calls the method directly — no
    /// reflection, and a rename becomes a compile error rather than a silent skip.</summary>
    private static string? Resolve(string key) => WorkspaceSetup.GetConfigValue(key);

    [Fact]
    public void SingleUnderscoreForm_Resolves()
    {
        Environment.SetEnvironmentVariable(SingleForm, "from-single");
        Resolve(Key).Should().Be("from-single");
    }

    [Fact]
    public void DoubleUnderscoreForm_Resolves()
    {
        Environment.SetEnvironmentVariable(DoubleForm, "from-double");
        Resolve(Key).Should().Be("from-double",
            because: "the documented .NET-standard form must resolve — the server already accepts it");
    }

    [Fact]
    public void SingleUnderscore_WinsWhenBothAreSet()
    {
        Environment.SetEnvironmentVariable(SingleForm, "from-single");
        Environment.SetEnvironmentVariable(DoubleForm, "from-double");
        Resolve(Key).Should().Be("from-single",
            because: "single-underscore is the historically documented form and keeps priority, so " +
                     "adding the double form cannot change an existing installation's behaviour");
    }

    // ── GetPluginName delegates rather than repeating the lookup ─────────────

    private const string PluginKey = "plugins:embeddings";
    private const string PluginSingleForm = "PLUGINS_EMBEDDINGS";
    private const string PluginDoubleForm = "PLUGINS__EMBEDDINGS";

    /// <summary>
    /// GetPluginName used to carry its own copy of the environment-then-config lookup, so when the
    /// double-underscore form was repaired the fix had to be applied twice. It now delegates to
    /// GetConfigValue. These two tests are what would fail if someone re-inlined the lookup and got
    /// one of the two forms wrong — the exact way the broken form came to be documented.
    /// </summary>
    [Fact]
    public void GetPluginName_ResolvesBothEnvironmentForms()
    {
        try
        {
            Environment.SetEnvironmentVariable(PluginDoubleForm, "plugin-from-double");
            WorkspaceSetup.GetPluginName(PluginKey, "fallback").Should().Be("plugin-from-double");

            Environment.SetEnvironmentVariable(PluginSingleForm, "plugin-from-single");
            WorkspaceSetup.GetPluginName(PluginKey, "fallback").Should().Be("plugin-from-single",
                because: "single-underscore keeps priority here too, not only in GetConfigValue");
        }
        finally
        {
            Environment.SetEnvironmentVariable(PluginSingleForm, null);
            Environment.SetEnvironmentVariable(PluginDoubleForm, null);
        }
    }

    [Fact]
    public void GetPluginName_FallsBackToTheDefaultWhenNothingIsSet()
    {
        // The isolated empty root means the config file cannot answer either, so this exercises
        // the one behaviour GetPluginName adds over GetConfigValue.
        WorkspaceSetup.GetPluginName(PluginKey, "scri-plugin-embeddings")
            .Should().Be("scri-plugin-embeddings");
    }
}

/// <summary>An xUnit collection only serialises test CLASSES that are members of the SAME
/// collection — this one has exactly one member (<see cref="ConfigEnvVarConventionTests"/>
/// itself), so <c>DisableParallelization</c> here only stops that class's own tests from
/// interleaving with each other. It does NOT stop a class in a different collection (e.g.
/// <c>GlobalWorkspaceConfigCollection</c>, which runs in parallel with this one) from running
/// concurrently alongside it.
///
/// <para>That gap is real for half of what this class touches: the single/double-underscore env
/// vars it sets (<c>SCRINIA_EMBEDDINGS_PROVIDER</c>, <c>SCRINIA__EMBEDDINGS__PROVIDER</c>,
/// <c>PLUGINS_EMBEDDINGS</c>, <c>PLUGINS__EMBEDDINGS</c>) are process-wide — a concurrent test
/// elsewhere that reads Scrinia config could observe them mid-run. The other half genuinely is
/// safe: <c>WorkspaceSetup.OverrideRoot</c> is <c>AsyncLocal</c>, so a concurrent collection's
/// override cannot leak into this one's, or vice versa.</para></summary>
[CollectionDefinition(Name, DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "xUnit collection definition; the Collection suffix identifies the shared fixture contract.")]
public sealed class EnvVarConventionCollection
{
    public const string Name = "EnvVarConvention";
}
