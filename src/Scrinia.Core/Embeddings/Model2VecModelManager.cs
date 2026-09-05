using Microsoft.Extensions.Logging;
using Scrinia.Core.Configuration;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// Downloads the m2v-MiniLM-L6-v2 Model2Vec model from HuggingFace. This is a 384-dim distillation of
/// all-MiniLM-L6-v2, vector-compatible with the Vulkan GGUF provider so both can share the same
/// VectorStore without reindexing. Files: model.safetensors (~22MB, F16) + vocab.txt (~220KB).
///
/// Downloads are content-verified and atomically promoted by <see cref="VerifiedModelDownloader"/>:
/// each asset is streamed to a unique temp, hashed while writing, validated, and moved into place only
/// once verified. Files publish under <c>Path.Combine(UserHome.Resolve(), "models", identifier, digest,
/// filename)</c>, so the same content is idempotent and tampered content is rejected before promotion.
/// </summary>
public static class Model2VecModelManager
{
    /// <summary>Logical identifier scoping this model under <c>SCRINIA_HOME/models</c>.</summary>
    public const string Identifier = "m2v-MiniLM-L6-v2";

    /// <summary>HuggingFace base URL for model files.</summary>
    public const string ModelBaseUrl = "https://huggingface.co/grybsh/m2v-MiniLM-L6-v2/resolve/main";

    private static readonly DownloadableAsset[] Assets =
    [
        new(Identifier, $"{ModelBaseUrl}/model.safetensors", "model.safetensors", ExpectedSha256: "0792db26b8391f89224e825fb3ed3f35498d8bda4936b92fa7658b4dda90cf28"),
        new(Identifier, $"{ModelBaseUrl}/vocab.txt", "vocab.txt", ExpectedSha256: "eef2f6b6ea4166090d7e6c1feec524b17a105c621847f3f99c9af20291d3cee1"),
    ];

    /// <summary>Whether every model file is published (directly or at its verified digest path). A
    /// <c>.tmp</c> or partial file never counts as available.</summary>
    public static bool IsModelAvailable(string modelDir)
        => Assets.All(a => VerifiedModelDownloader.IsAssetAvailable(modelDir, a));

    /// <summary>Downloads the model files if not already verified in place.</summary>
    public static Task EnsureModelAsync(string modelDir, ILogger logger, CancellationToken ct = default)
        => EnsureModelAsync(modelDir, logger, Assets, static () => new HttpClient(), ct);

    internal static Task EnsureModelAsync(
        string modelDir, ILogger logger,
        IReadOnlyList<DownloadableAsset> assets, Func<HttpClient> httpClientFactory, CancellationToken ct = default)
        => VerifiedModelDownloader.EnsureModelAsync(modelDir, logger, assets, httpClientFactory, ct);
}
