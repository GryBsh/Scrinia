using System.Text;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// The host↔plugin configuration contract. Single source of truth for which config
/// keys the CLI host forwards to the embeddings plugin child process, which of them
/// are secrets, and how a secret key maps to an environment-variable name.
///
/// Secrets travel to the child via its environment, never via argv — a process command
/// line is world-readable on the local host (ps / Task Manager / /proc/&lt;pid&gt;/cmdline).
/// </summary>
public static class PluginConfigProtocol
{
    /// <summary>Config keys the host forwards to the plugin child process.</summary>
    public static readonly IReadOnlyList<string> Keys =
    [
        "Scrinia:Embeddings:Provider",
        "Scrinia:Embeddings:Hardware",
        "Scrinia:Embeddings:SemanticWeight",
        "Scrinia:Embeddings:OllamaBaseUrl",
        "Scrinia:Embeddings:OllamaModel",
        "Scrinia:Embeddings:OpenAiApiKey",
        "Scrinia:Embeddings:OpenAiModel",
        "Scrinia:Embeddings:OpenAiBaseUrl",
    ];

    /// <summary>A key is a secret when it names an API key. Covers current and future *ApiKey keys.</summary>
    public static bool IsSecret(string key) =>
        key.EndsWith("ApiKey", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deterministic, collision-safe environment-variable name for a config key:
    /// <c>SCRINIA_PLUGIN_CFG__</c> + the key uppercased with every character outside
    /// <c>[A-Z0-9]</c> replaced by <c>_</c>. Example:
    /// <c>Scrinia:Embeddings:OpenAiApiKey</c> → <c>SCRINIA_PLUGIN_CFG__SCRINIA_EMBEDDINGS_OPENAIAPIKEY</c>.
    /// </summary>
    public static string EnvVarName(string key)
    {
        var sb = new StringBuilder("SCRINIA_PLUGIN_CFG__");
        foreach (char c in key.ToUpperInvariant())
            sb.Append(c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') ? c : '_');
        return sb.ToString();
    }

    /// <summary>
    /// Reads secret config values from the environment into <paramref name="configValues"/>.
    /// For each secret key, if <paramref name="getEnv"/> returns a non-null value for its
    /// mapped env var, that value is written to <paramref name="configValues"/> under the
    /// canonical key. Plugin-side counterpart to the host's secret-environment build.
    /// </summary>
    public static void ApplySecretEnvironment(
        IDictionary<string, string> configValues, Func<string, string?> getEnv)
    {
        foreach (var key in Keys)
        {
            if (!IsSecret(key)) continue;
            var val = getEnv(EnvVarName(key));
            if (val is not null)
                configValues[key] = val;
        }
    }
}
