using Microsoft.Extensions.Logging;
using Scrinia.Core.Embeddings;

namespace Scrinia.Plugin.Embeddings;

/// <summary>
/// Downloads a GGUF embedding model from HuggingFace for use with VulkanEmbeddingProvider.
/// Default model: all-MiniLM-L6-v2 in GGUF format.
///
/// Downloads are content-verified and atomically promoted by <see cref="VerifiedModelDownloader"/>:
/// the file is streamed to a unique temp, hashed while writing, validated, and moved into place only
/// once verified, publishing under <c>SCRINIA_HOME/models</c> keyed by the verified digest.
/// </summary>
public static class VulkanModelManager
{
    /// <summary>Logical identifier scoping this model under <c>SCRINIA_HOME/models</c>.</summary>
    public const string Identifier = "all-MiniLM-L6-v2";

    /// <summary>HuggingFace URL for the GGUF model file.</summary>
    public const string DefaultModelUrl =
        "https://huggingface.co/second-state/All-MiniLM-L6-v2-Embedding-GGUF/resolve/main/all-MiniLM-L6-v2-Q8_0.gguf";

    /// <summary>GGUF model filename.</summary>
    public const string DefaultModelFile = "all-MiniLM-L6-v2-Q8_0.gguf";

    private static readonly DownloadableAsset[] Assets =
    [
        new(Identifier, DefaultModelUrl, DefaultModelFile, ExpectedSha256: "263215c3cadd6e16740741a7624ab4cbb6c8e777688bd5331ecfbf5681c2f8ed"),
    ];

    /// <summary>Whether the GGUF model file is published (directly or at its verified digest path). A
    /// <c>.tmp</c> or partial file never counts as available.</summary>
    public static bool IsModelAvailable(string modelDir)
        => Assets.All(a => VerifiedModelDownloader.IsAssetAvailable(modelDir, a));

    /// <summary>Gets the path to the default GGUF model.</summary>
    public static string GetModelPath(string modelDir)
        => Path.Combine(modelDir, DefaultModelFile);

    /// <summary>Downloads the GGUF model if not already verified in place.</summary>
    public static Task EnsureModelAsync(string modelDir, ILogger logger, CancellationToken ct = default)
        => EnsureModelAsync(modelDir, logger, Assets, static () => new HttpClient(), ct);

    internal static Task EnsureModelAsync(
        string modelDir, ILogger logger,
        IReadOnlyList<DownloadableAsset> assets, Func<HttpClient> httpClientFactory, CancellationToken ct = default)
        => VerifiedModelDownloader.EnsureModelAsync(modelDir, logger, assets, httpClientFactory, ct);
}
