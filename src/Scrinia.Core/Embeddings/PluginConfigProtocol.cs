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
        // The chunking keys must travel too. They are baked into the active embedding signature
        // (ChunkedSignature.Compose), so a host and a plugin that disagree about them disagree
        // about which vectors are valid — and without them the out-of-process plugin silently
        // ignored `scri config Scrinia:Embeddings:ChunkSize` end to end.
        "Scrinia:Embeddings:ChunkSize",
        "Scrinia:Embeddings:ChunkOverlap",
        "Scrinia:Embeddings:MaxChunksPerMemory",
    ];

    /// <summary>
    /// Rebuilds an <see cref="EmbeddingOptions"/> from the forwarded keys, plugin-side counterpart
    /// to the host's argument build. Only the keys in <see cref="Keys"/> are read — anything else
    /// never crossed the process boundary, so reading it here would silently produce settings the
    /// host does not share.
    ///
    /// <para>Applies the same two guardrails the CLI host applies: an overlap at or above the
    /// window size makes <c>TextChunker.SliceWindows</c> throw, and a chunk cap at or above
    /// <see cref="MemoryVectorIndexer.ChunkIndexStride"/> makes the indexer's constructor throw.
    /// In a plugin child process either would be a startup crash, so both clamp and report through
    /// <paramref name="warn"/>.</para>
    /// </summary>
    public static EmbeddingOptions BuildOptions(Func<string, string?> getConfig, Action<string>? warn = null)
    {
        var options = new EmbeddingOptions();

        if (getConfig("Scrinia:Embeddings:Provider") is { Length: > 0 } provider)
            options.Provider = provider;

        if (double.TryParse(getConfig("Scrinia:Embeddings:SemanticWeight"), out double semanticWeight))
            options.SemanticWeight = semanticWeight;

        if (getConfig("Scrinia:Embeddings:OllamaBaseUrl") is { Length: > 0 } ollamaUrl)
            options.OllamaBaseUrl = ollamaUrl;
        if (getConfig("Scrinia:Embeddings:OllamaModel") is { Length: > 0 } ollamaModel)
            options.OllamaModel = ollamaModel;

        if (getConfig("Scrinia:Embeddings:OpenAiApiKey") is { Length: > 0 } openAiKey)
            options.OpenAiApiKey = openAiKey;
        if (getConfig("Scrinia:Embeddings:OpenAiModel") is { Length: > 0 } openAiModel)
            options.OpenAiModel = openAiModel;
        if (getConfig("Scrinia:Embeddings:OpenAiBaseUrl") is { Length: > 0 } openAiUrl)
            options.OpenAiBaseUrl = openAiUrl;

        if (int.TryParse(getConfig("Scrinia:Embeddings:ChunkSize"), out int chunkSize) && chunkSize > 0)
            options.ChunkSize = chunkSize;
        if (int.TryParse(getConfig("Scrinia:Embeddings:ChunkOverlap"), out int chunkOverlap) && chunkOverlap >= 0)
            options.ChunkOverlap = chunkOverlap;
        if (int.TryParse(getConfig("Scrinia:Embeddings:MaxChunksPerMemory"), out int maxChunks) && maxChunks > 0)
            options.MaxChunksPerMemory = maxChunks;

        if (options.ChunkOverlap >= options.ChunkSize)
        {
            warn?.Invoke(
                $"ChunkOverlap ({options.ChunkOverlap}) >= ChunkSize ({options.ChunkSize}); "
                + $"reverting to defaults {TextChunker.DefaultWindowSize}/{TextChunker.DefaultOverlap}.");
            options.ChunkSize = TextChunker.DefaultWindowSize;
            options.ChunkOverlap = TextChunker.DefaultOverlap;
        }

        int requestedCap = options.MaxChunksPerMemory;
        if (options.ClampChunkCapBelowStride())
        {
            warn?.Invoke(
                $"MaxChunksPerMemory ({requestedCap}) must be below "
                + $"{MemoryVectorIndexer.ChunkIndexStride}; clamping to {options.MaxChunksPerMemory}.");
        }

        return options;
    }

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
