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
}

/// <summary>Environment variables are process-wide, so these tests must not run beside anything
/// else that reads Scrinia config. Same shape as Nmp2DecodeCapCollection and the three other
/// serialised collections in this project.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvVarConventionCollection
{
    public const string Name = "EnvVarConvention";
}
