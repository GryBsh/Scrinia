using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core.Embeddings;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Scrinia.Tests.Embeddings;

public sealed class Model2VecAssetResolverTests : IDisposable
{
    private static readonly string ModelDigest = Digest("model.safetensors");
    private static readonly string VocabDigest = Digest("vocab.txt");

    private readonly string _modelDir = Path.Combine(Path.GetTempPath(), $"model2vec-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_modelDir, recursive: true); } catch { }
    }

    [Fact]
    public void Resolve_UsesCompleteDirectPair()
    {
        WriteDirectAssets(
            ("model.safetensors", "model.safetensors"),
            ("vocab.txt", "vocab.txt"));

        Model2VecAssetResolver.Resolve(_modelDir, Assets()).Should().BeEquivalentTo(
            new Model2VecAssetPaths(
                Path.Combine(_modelDir, "model.safetensors"),
                Path.Combine(_modelDir, "vocab.txt")));
    }

    [Fact]
    public void Resolve_UsesExpectedDigestNestedAssets()
    {
        WriteNestedAsset(ModelDigest, "model.safetensors", "model.safetensors");
        WriteNestedAsset(VocabDigest, "vocab.txt", "vocab.txt");

        Model2VecAssetResolver.Resolve(_modelDir, Assets()).Should().BeEquivalentTo(
            new Model2VecAssetPaths(
                Path.Combine(_modelDir, ModelDigest, "model.safetensors"),
                Path.Combine(_modelDir, VocabDigest, "vocab.txt")));
    }

    [Fact]
    public void Resolve_RejectsMissingAsset()
    {
        WriteDirectAssets(("model.safetensors", "model.safetensors"));

        Action resolve = () => Model2VecAssetResolver.Resolve(_modelDir, Assets());

        resolve.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Resolve_RejectsMixedDirectAndNestedLayouts()
    {
        WriteDirectAssets(("model.safetensors", "model.safetensors"));
        WriteNestedAsset(VocabDigest, "vocab.txt", "vocab.txt");

        Action resolve = () => Model2VecAssetResolver.Resolve(_modelDir, Assets());

        resolve.Should().Throw<InvalidDataException>().Which.Message.Should().Contain("mixed");
    }

    [Fact]
    public void Resolve_RejectsMalformedNestedCandidate()
    {
        WriteNestedAsset("not-a-sha256", "model.safetensors", "model.safetensors");
        WriteNestedAsset(VocabDigest, "vocab.txt", "vocab.txt");

        Action resolve = () => Model2VecAssetResolver.Resolve(_modelDir, Assets());

        resolve.Should().Throw<InvalidDataException>().Which.Message.Should().Contain("digest");
    }

    [Fact]
    public void Resolve_RejectsTamperedDirectAsset()
    {
        WriteDirectAssets(
            ("model.safetensors", "tampered"),
            ("vocab.txt", "vocab.txt"));

        Action resolve = () => Model2VecAssetResolver.Resolve(_modelDir, Assets());

        resolve.Should().Throw<ModelDownloadException>().Which.Message.Should().Contain("model.safetensors");
    }

    [Fact]
    public void Resolve_RejectsTamperedNestedAsset()
    {
        WriteNestedAsset(ModelDigest, "model.safetensors", "tampered");
        WriteNestedAsset(VocabDigest, "vocab.txt", "vocab.txt");

        Action resolve = () => Model2VecAssetResolver.Resolve(_modelDir, Assets());

        resolve.Should().Throw<ModelDownloadException>().Which.Message.Should().Contain("model.safetensors");
    }

    [Fact]
    public void Resolve_RejectsDuplicateAssetDefinitionDeterministically()
    {
        WriteDirectAssets(
            ("model.safetensors", "model.safetensors"),
            ("vocab.txt", "vocab.txt"));
        IReadOnlyList<DownloadableAsset> assets =
        [
            new("test", "https://example.test/model", "model.safetensors", ModelDigest),
            new("test", "https://example.test/model-duplicate", "model.safetensors", ModelDigest),
        ];

        Action resolve = () => Model2VecAssetResolver.Resolve(_modelDir, assets);

        resolve.Should().Throw<InvalidDataException>().Which.Message.Should().Contain("declared more than once");
    }

    [Fact]
    public void Provider_RejectsTamperedDirectAssetBeforeLoading()
    {
        WriteDirectAssets(
            ("model.safetensors", "tampered"),
            ("vocab.txt", "vocab.txt"));

        Action load = () => Model2VecProvider.Load(_modelDir, NullLogger.Instance);

        load.Should().Throw<ModelDownloadException>();
    }

    private IReadOnlyList<DownloadableAsset> Assets() =>
    [
        new("test", "https://example.test/model", "model.safetensors", ModelDigest),
        new("test", "https://example.test/vocab", "vocab.txt", VocabDigest),
    ];

    private static string Digest(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private void WriteDirectAssets(params (string File, string Content)[] files)
    {
        Directory.CreateDirectory(_modelDir);
        foreach ((string file, string content) in files)
            File.WriteAllText(Path.Combine(_modelDir, file), content);
    }

    private void WriteNestedAsset(string directory, string file, string content)
    {
        string path = Path.Combine(_modelDir, directory, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
