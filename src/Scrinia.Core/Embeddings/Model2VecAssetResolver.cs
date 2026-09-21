namespace Scrinia.Core.Embeddings;

internal sealed record Model2VecAssetPaths(string ModelPath, string VocabPath);

internal static class Model2VecAssetResolver
{
    public static Model2VecAssetPaths Resolve(string modelDir, IReadOnlyList<DownloadableAsset> assets)
    {
        if (assets.Count != 2)
            throw new ArgumentException("Model2Vec resolution requires exactly two assets.", nameof(assets));

        DownloadableAsset model = FindAsset(assets, "model.safetensors");
        DownloadableAsset vocab = FindAsset(assets, "vocab.txt");
        IReadOnlyList<VerifiedAssetPath> paths = VerifiedAssetResolver.Resolve(modelDir, assets);
        return new Model2VecAssetPaths(
            FindPath(paths, model),
            FindPath(paths, vocab));
    }

    /// <summary>
    /// Cheap availability pre-check: resolves the direct/digest-nested paths and validates each
    /// SafeTensors manifest header — <b>without hashing the tensor body</b>. Used by the startup
    /// <c>IsModelAvailable</c> probe so the ~22MB model is read once (in <c>Load</c>), not twice. The
    /// full-file content gate is preserved in <see cref="Resolve"/>.
    /// </summary>
    internal static Model2VecAssetPaths ResolveForAvailability(
        string modelDir, IReadOnlyList<DownloadableAsset> assets)
    {
        if (assets.Count != 2)
            throw new ArgumentException("Model2Vec resolution requires exactly two assets.", nameof(assets));

        DownloadableAsset model = FindAsset(assets, "model.safetensors");
        DownloadableAsset vocab = FindAsset(assets, "vocab.txt");
        IReadOnlyList<VerifiedAssetPath> paths = VerifiedAssetResolver.ResolvePaths(modelDir, assets);
        foreach (VerifiedAssetPath path in paths)
            VerifiedAssetResolver.VerifyAssetDigest(path);
        return new Model2VecAssetPaths(
            FindPath(paths, model),
            FindPath(paths, vocab));
    }

    private static DownloadableAsset FindAsset(IReadOnlyList<DownloadableAsset> assets, string filename)
    {
        DownloadableAsset[] matches = assets
            .Where(asset => string.Equals(asset.Filename, filename, StringComparison.Ordinal))
            .ToArray();
        return matches switch
        {
            [] => throw new ArgumentException($"Model2Vec assets must contain {filename}.", nameof(assets)),
            [DownloadableAsset match] => match,
            _ => throw new InvalidDataException($"Model2Vec asset {filename} is declared more than once."),
        };
    }

    private static string FindPath(IReadOnlyList<VerifiedAssetPath> paths, DownloadableAsset asset)
        => paths.Single(path => ReferenceEquals(path.Asset, asset)).Path;
}
