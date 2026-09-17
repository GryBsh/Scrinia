using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Commands;
using Scrinia.Core.Embeddings;

namespace Scrinia.Tests;

public sealed class SetupModelDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scrinia-setup-models-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task SetupDownload_RepairsTamperedDirectAsset()
    {
        byte[] body = "verified model"u8.ToArray();
        DownloadableAsset asset = Asset("model.safetensors", body);
        string modelDir = Path.Combine(_root, "model");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, asset.Filename), "tampered");

        await ScriniaCommands.EnsureVerifiedModelAssetsAsync(
            modelDir, [asset], () => FixedClient(body), NullLogger.Instance, CancellationToken.None);

        File.ReadAllBytes(Path.Combine(modelDir, asset.ExpectedSha256!, asset.Filename))
            .Should().BeEquivalentTo(body);
        File.Exists(Path.Combine(modelDir, asset.Filename)).Should().BeFalse();
    }

    [Fact]
    public async Task FirstRunDownload_PublishesOnlyAfterVerification()
    {
        byte[] body = "first-run model"u8.ToArray();
        DownloadableAsset asset = Asset("model.safetensors", body);
        string modelDir = Path.Combine(_root, "model");

        await ScriniaCommands.EnsureVerifiedModelAssetsAsync(
            modelDir, [asset], () => FixedClient(body), NullLogger.Instance, CancellationToken.None);

        VerifiedModelDownloader.IsAssetAvailable(modelDir, asset).Should().BeTrue();
        Directory.GetFiles(modelDir, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task FirstRunDownload_CancellationLeavesNoPublicationOrTemp()
    {
        byte[] body = "cancelled model"u8.ToArray();
        DownloadableAsset asset = Asset("model.safetensors", body);
        string modelDir = Path.Combine(_root, "model");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ScriniaCommands.EnsureVerifiedModelAssetsAsync(
                modelDir, [asset], () => FixedClient(body), NullLogger.Instance, cancellation.Token));

        Directory.GetFiles(modelDir, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task SetupDownload_CompletesExistingDirectLayoutWithoutCreatingNestedLayout()
    {
        byte[] firstBody = "direct model"u8.ToArray();
        byte[] secondBody = "direct vocab"u8.ToArray();
        DownloadableAsset first = Asset("model.safetensors", firstBody);
        DownloadableAsset second = Asset("vocab.txt", secondBody);
        string modelDir = Path.Combine(_root, "model");
        Directory.CreateDirectory(modelDir);
        File.WriteAllBytes(Path.Combine(modelDir, first.Filename), firstBody);

        await ScriniaCommands.EnsureVerifiedModelAssetsAsync(
            modelDir, [first, second], () => new HttpClient(new MappedBodyHandler(
                new Dictionary<string, byte[]> { [first.Filename] = firstBody, [second.Filename] = secondBody })),
            NullLogger.Instance, CancellationToken.None);

        File.Exists(Path.Combine(modelDir, first.Filename)).Should().BeTrue();
        File.Exists(Path.Combine(modelDir, second.Filename)).Should().BeTrue();
        Directory.GetDirectories(modelDir)
            .Should().NotContain(Path.Combine(modelDir, first.ExpectedSha256!));
        Directory.GetDirectories(modelDir)
            .Should().NotContain(Path.Combine(modelDir, second.ExpectedSha256!));
    }

    private static DownloadableAsset Asset(string filename, byte[] body) =>
        new("test-model", $"https://example.test/{filename}", filename,
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());

    private static HttpClient FixedClient(byte[] body) =>
        new(new FixedBodyHandler(body));

    private sealed class MappedBodyHandler(IReadOnlyDictionary<string, byte[]> bodies) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bodies[Path.GetFileName(request.RequestUri!.AbsolutePath)]),
            });
    }

    private sealed class FixedBodyHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            });
    }
}
