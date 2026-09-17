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
