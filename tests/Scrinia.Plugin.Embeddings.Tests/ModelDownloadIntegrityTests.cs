using System.Security.Cryptography;
using System.Net;
using System.Collections.Concurrent;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Plugin.Embeddings;
using Scrinia.TestDoubles;
using Xunit;

namespace Scrinia.Plugin.Embeddings.Tests;

/// <summary>
/// Isolated integrity tests for the content-verified, atomically-promoted model download shared by
/// <see cref="Model2VecModelManager"/> and <see cref="VulkanModelManager"/>. The tests drive each
/// manager's <c>EnsureModelAsync</c> entry point through an injected <see cref="HttpClient"/> factory
/// backed by a fake <see cref="HttpMessageHandler"/>, and an isolated <c>SCRINIA_HOME/models</c> tree.
///
/// The <c>SCRINIA_HOME</c> environment variable is process-wide, so this class runs in a dedicated
/// sequential collection and never in parallel with tests that read the same resolver.
/// </summary>
[Collection("ModelDownloadIntegrity")]
public sealed class ModelDownloadIntegrityTests : IDisposable
{
    private readonly string _home;
    private readonly ProcessEnvironmentLock _environmentLock;
    private readonly EnvironmentVariableScope _homeScope;
    private readonly ILogger _logger = NullLogger.Instance;

    public ModelDownloadIntegrityTests()
    {
        _environmentLock = ProcessEnvironmentLock.Acquire();
        _home = Path.Combine(Path.GetTempPath(), $"scrinia_home_{Guid.NewGuid():N}");
        _homeScope = new EnvironmentVariableScope("SCRINIA_HOME", _home);
    }

    public void Dispose()
    {
        _homeScope.Dispose();
        try { Directory.Delete(_home, recursive: true); } catch { /* best-effort */ }
        _environmentLock.Dispose();
    }

    private string ModelsRoot => Path.Combine(_home, "models");

    // ── Download dispatch: one per manager, exercising the retained public entry point's internals ──

    private static Task DownloadM2V(
        IReadOnlyList<DownloadableAsset> assets, string modelDir, Func<HttpClient> factory, CancellationToken ct)
        => Model2VecModelManager.EnsureModelAsync(modelDir, NullLogger.Instance, assets, factory, ct);

    private static Task DownloadVulkan(
        IReadOnlyList<DownloadableAsset> assets, string modelDir, Func<HttpClient> factory, CancellationToken ct)
        => VulkanModelManager.EnsureModelAsync(modelDir, NullLogger.Instance, assets, factory, ct);

    private delegate Task DownloadDelegate(
        IReadOnlyList<DownloadableAsset> assets, string modelDir, Func<HttpClient> factory, CancellationToken ct);

    // ── (a) key/path contains identifier + lowercase SHA-256 digest ────────────────────────────────

    [Theory]
    [InlineData("m2v-test")]
    [InlineData("all-MiniLM-L6-v2")]
    public async Task AssetKey_ContainsIdentifierAndLowercaseDigest(string identifier)
    {
        byte[] body = Encoding.UTF8.GetBytes($"{identifier} payload");
        string digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

        var asset = Asset(identifier, body);
        string modelDir = Path.Combine(ModelsRoot, identifier);
        using var handler = new FakeHttpHandler(body);
        var factory = () => new HttpClient(handler);

        await DownloadM2V([asset], modelDir, factory, CancellationToken.None);

        string promoted = Path.Combine(modelDir, digest, asset.Filename);
        File.Exists(promoted).Should().BeTrue($"the asset must be published at identifier + lowercase digest");
        promoted.Should().Contain(identifier);
        promoted.Should().Contain(digest);
        File.ReadAllText(promoted).Should().Be(Encoding.UTF8.GetString(body));
    }

    // ── (b) non-matching bytes throw before promotion, no final file left ─────────────────────────

    [Theory]
    [InlineData("m2v-test")]
    public async Task DigestMismatch_IsRejectedBeforePromotion(string identifier)
    {
        byte[] body = Encoding.UTF8.GetBytes("tampered payload");
        string wrongDigest = "0000000000000000000000000000000000000000000000000000000000000000";

        var asset = new DownloadableAsset(identifier, $"https://example.test/{identifier}/model", "model.safetensors", wrongDigest);
        string modelDir = Path.Combine(ModelsRoot, identifier);
        using var handler = new FakeHttpHandler(body);

        await ThrowAsync<ModelDownloadException>(() =>
            DownloadM2V([asset], modelDir, () => new HttpClient(handler), CancellationToken.None));

        string actualDigest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        File.Exists(Path.Combine(modelDir, actualDigest, "model.safetensors")).Should().BeFalse("a mismatched download must never be promoted");
        NoTempFiles(modelDir).Should().BeTrue("the rejected temp must be deleted");
    }

    // ── (c) concurrent downloads use distinct temps, publish one valid asset, expose no partial ────

    [Theory]
    [InlineData("m2v-test")]
    public async Task ConcurrentDownloads_UseUniqueTempsAndPublishOneValidAsset(string identifier)
    {
        // A large, chunked body with a per-read delay keeps both downloads mid-stream long enough to
        // overlap; if they shared a temp file the second FileStream (FileShare.None) would fail.
        byte[] body = Enumerable.Range(0, 32_768).Select(i => (byte)('A' + (i % 26))).ToArray();
        var asset = Asset(identifier, body);
        string modelDir = Path.Combine(ModelsRoot, identifier);

        // Each download gets its own handler (a FakeHttpHandler is not safe to share across concurrent
        // requests), but both share the body and the concurrency counter so the overlap is observable.
        var concurrency = new ConcurrentCounter();
        var factory = () => new HttpClient(new FakeHttpHandler(body, delayMs: 3, concurrency: concurrency));
        var t1 = Task.Run(() => DownloadM2V([asset], modelDir, factory, CancellationToken.None));
        var t2 = Task.Run(() => DownloadM2V([asset], modelDir, factory, CancellationToken.None));

        await Task.WhenAll(t1, t2);

        concurrency.Max.Should().BeGreaterThanOrEqualTo(2, "both downloads must stream simultaneously, which is only possible with distinct temp files");

        string digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        string promoted = Path.Combine(modelDir, digest, asset.Filename);
        File.Exists(promoted).Should().BeTrue();
        Directory.GetFiles(modelDir, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty("no partial temp may be exposed after promotion");
    }

    // ── (d) cancellation / stream failure / pre-seeded partial leaves no visible model; retry clean ─

    [Theory]
    [InlineData("m2v-test")]
    public async Task PreSeededPartialTemp_IsNotVisibleByIsModelAvailable(string identifier)
    {
        string modelDir = Path.Combine(ModelsRoot, identifier);
        string partial = Path.Combine(modelDir, "deadbeef", "model.safetensors.partial.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        File.WriteAllText(partial, "partial-bytes");

        Model2VecModelManager.IsModelAvailable(modelDir).Should().BeFalse(
            "a .tmp/partial file must never be considered an available model");
    }

    [Theory]
    [InlineData("m2v-test")]
    public async Task CancelledDownload_LeavesNoVisibleModel_AndRetrySucceeds(string identifier)
    {
        byte[] body = Encoding.UTF8.GetBytes("cancel me");
        var asset = Asset(identifier, body);
        string modelDir = Path.Combine(ModelsRoot, identifier);

        using var cancel = new CancellationTokenSource();
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        using var handler = new BlockingFakeHttpHandler(body, started, release);

        _ = Task.Run(async () => { await Task.Delay(40); cancel.Cancel(); });

        await ThrowAsync<OperationCanceledException>(() =>
            DownloadM2V([asset], modelDir, () => new HttpClient(handler), cancel.Token));

        release.Set(); // let the in-flight stream unwind
        string digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        File.Exists(Path.Combine(modelDir, digest, asset.Filename)).Should().BeFalse("no model is visible while cancelled");
        TempFiles(modelDir).Should().BeEmpty("the cancelled temp must be deleted");

        // retry starts clean
        using var okHandler = new FakeHttpHandler(body);
        await DownloadM2V([asset], modelDir, () => new HttpClient(okHandler), CancellationToken.None);
        File.Exists(Path.Combine(modelDir, digest, asset.Filename)).Should().BeTrue("retry must publish the verified asset");
        VerifiedModelDownloader.IsAssetAvailable(modelDir, asset).Should().BeTrue("the promoted asset is visible after retry");
    }

    [Fact]
    public async Task CancellationAfterHashCompletion_LeavesNoVisibleModelOrTemp()
    {
        const string identifier = "m2v-test";
        byte[] body = Encoding.UTF8.GetBytes("cancel after hash");
        var asset = Asset(identifier, body);
        string modelDir = Path.Combine(ModelsRoot, identifier);
        string digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        using var cancel = new CancellationTokenSource();
        using var handler = new FakeHttpHandler(body);

        Action cancelAfterHash = cancel.Cancel;
        Func<Task> download = () => VerifiedModelDownloader.EnsureModelAsync(
            modelDir, _logger, [asset], () => new HttpClient(handler), cancelAfterHash, cancel.Token);

        await download.Should().ThrowAsync<OperationCanceledException>();

        File.Exists(Path.Combine(modelDir, digest, asset.Filename)).Should().BeFalse(
            "cancellation between hashing and promotion must not publish the model");
        TempFiles(modelDir).Should().BeEmpty("cancellation must clean the validated temp");
    }

    [Fact]
    public async Task PromotionRace_WithValidWinner_IsSuccessfulAndCleansLoserTemp()
    {
        const string identifier = "m2v-test";
        byte[] body = Encoding.UTF8.GetBytes("valid cross-process winner");
        var asset = Asset(identifier, body);
        string modelDir = Path.Combine(ModelsRoot, identifier);
        string digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        string destination = Path.Combine(modelDir, digest, asset.Filename);

        void PublishWinnerThenLose(string source, string target)
        {
            File.Copy(source, target);
            throw new IOException("simulated promotion race loss");
        }

        using var handler = new FakeHttpHandler(body);

        await VerifiedModelDownloader.EnsureModelAsync(
            modelDir, _logger, [asset], () => new HttpClient(handler),
            afterHashCompleted: null, PublishWinnerThenLose, CancellationToken.None);

        File.ReadAllBytes(destination).Should().BeEquivalentTo(body);
        TempFiles(modelDir).Should().BeEmpty();
    }

    [Fact]
    public async Task PromotionRace_WithInvalidWinner_FailsAndCleansLoserTemp()
    {
        const string identifier = "m2v-test";
        byte[] body = Encoding.UTF8.GetBytes("expected payload");
        var asset = Asset(identifier, body);
        string modelDir = Path.Combine(ModelsRoot, identifier);
        string digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        string destination = Path.Combine(modelDir, digest, asset.Filename);

        void PublishInvalidWinnerThenLose(string source, string target)
        {
            File.WriteAllText(target, "invalid winner");
            throw new IOException("simulated promotion race loss");
        }

        using var handler = new FakeHttpHandler(body);

        Func<Task> download = () => VerifiedModelDownloader.EnsureModelAsync(
            modelDir, _logger, [asset], () => new HttpClient(handler),
            afterHashCompleted: null, PublishInvalidWinnerThenLose, CancellationToken.None);

        await download.Should().ThrowAsync<ModelDownloadException>();

        VerifiedModelDownloader.IsAssetAvailable(modelDir, asset).Should().BeFalse();
        TempFiles(modelDir).Should().BeEmpty();
    }

    [Fact]
    public async Task ExistingInvalidDestination_IsReplacedByVerifiedContent()
    {
        const string identifier = "m2v-test";
        byte[] body = Encoding.UTF8.GetBytes("verified replacement");
        var asset = Asset(identifier, body);
        string modelDir = Path.Combine(ModelsRoot, identifier);
        string destination = Path.Combine(modelDir, asset.ExpectedSha256!, asset.Filename);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "tampered destination");

        using var handler = new FakeHttpHandler(body);
        await DownloadM2V([asset], modelDir, () => new HttpClient(handler), CancellationToken.None);

        File.ReadAllBytes(destination).Should().BeEquivalentTo(body);
        VerifiedModelDownloader.IsAssetAvailable(modelDir, asset).Should().BeTrue();
        TempFiles(modelDir).Should().BeEmpty();
    }

    [Fact]
    public async Task ReparseNestedDestination_IsRejectedWithoutExternalPublication()
    {
        byte[] body = Encoding.UTF8.GetBytes("reparse nested");
        var asset = Asset("m2v-test", body);
        string modelDir = Path.Combine(ModelsRoot, asset.Identifier);
        string outsideDir = Path.Combine(_home, "outside-nested");
        string outsidePath = Path.Combine(outsideDir, asset.Filename);
        Directory.CreateDirectory(outsideDir);
        File.WriteAllBytes(outsidePath, body);

        string nestedLink = Path.Combine(modelDir, asset.ExpectedSha256!);
        if (!TryCreateDirSymlink(nestedLink, outsideDir)) return;

        using var handler = new FakeHttpHandler(body);
        Func<Task> download = () => DownloadM2V(
            [asset], modelDir, () => new HttpClient(handler), CancellationToken.None);

        await download.Should().ThrowAsync<InvalidDataException>();
        File.ReadAllBytes(outsidePath).Should().BeEquivalentTo(body);
        Directory.GetFiles(outsideDir, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task ReparseDirectDestination_IsRejectedWithoutExternalPublication()
    {
        byte[] body = Encoding.UTF8.GetBytes("reparse direct");
        var asset = Asset("m2v-test", body);
        string modelDir = Path.Combine(ModelsRoot, asset.Identifier);
        string outsidePath = Path.Combine(_home, "outside-direct.bin");
        Directory.CreateDirectory(modelDir);
        File.WriteAllBytes(outsidePath, body);

        string directLink = Path.Combine(modelDir, asset.Filename);
        if (!TryCreateFileSymlink(directLink, outsidePath)) return;

        using var handler = new FakeHttpHandler(body);
        Func<Task> download = () => DownloadM2V(
            [asset], modelDir, () => new HttpClient(handler), CancellationToken.None);

        await download.Should().ThrowAsync<InvalidDataException>();
        File.ReadAllBytes(outsidePath).Should().BeEquivalentTo(body);
        TempFiles(modelDir).Should().BeEmpty();
    }

    [Fact]
    public async Task ReparseDownloadParent_IsRejectedWithoutExternalPublication()
    {
        byte[] body = Encoding.UTF8.GetBytes("reparse temp parent");
        var asset = Asset("m2v-test", body);
        string modelDir = Path.Combine(ModelsRoot, asset.Identifier);
        string outsideDir = Path.Combine(_home, "outside-downloads");
        Directory.CreateDirectory(outsideDir);
        Directory.CreateDirectory(modelDir);

        string downloadsLink = Path.Combine(modelDir, ".downloads");
        if (!TryCreateDirSymlink(downloadsLink, outsideDir)) return;

        using var handler = new FakeHttpHandler(body);
        Func<Task> download = () => DownloadM2V(
            [asset], modelDir, () => new HttpClient(handler), CancellationToken.None);

        await download.Should().ThrowAsync<InvalidDataException>();
        Directory.GetFiles(outsideDir, "*", SearchOption.AllDirectories).Should().BeEmpty();
        VerifiedModelDownloader.IsAssetAvailable(modelDir, asset).Should().BeFalse();
    }

    [Fact]
    public async Task ReparseModelAncestor_IsRejectedBeforeAnyDirectoryCreation()
    {
        byte[] body = Encoding.UTF8.GetBytes("reparse model ancestor");
        var asset = Asset("m2v-test", body);
        string outsideDir = Path.Combine(_home, "outside-model-root");
        Directory.CreateDirectory(outsideDir);
        string modelParent = Path.Combine(_home, "models");
        if (!TryCreateDirSymlink(modelParent, outsideDir)) return;

        string modelDir = Path.Combine(modelParent, asset.Identifier);
        using var handler = new FakeHttpHandler(body);

        Func<Task> download = () => DownloadM2V(
            [asset], modelDir, () => new HttpClient(handler), CancellationToken.None);

        await download.Should().ThrowAsync<InvalidDataException>();
        Directory.GetFileSystemEntries(outsideDir).Should().BeEmpty(
            "a reparse-point ancestor must be rejected before model directories are created");
    }

    [Fact]
    public async Task ReplacedTempAfterHash_IsRejectedWithoutPublication()
    {
        byte[] body = Encoding.UTF8.GetBytes("replaced temp");
        var asset = Asset("m2v-test", body);
        string modelDir = Path.Combine(ModelsRoot, asset.Identifier);
        string outsidePath = Path.Combine(_home, "outside-temp.bin");
        Directory.CreateDirectory(_home);
        File.WriteAllBytes(outsidePath, body);

        void ReplaceTempWithLink()
        {
            string temp = Directory.GetFiles(
                Path.Combine(modelDir, ".downloads"), "*.tmp", SearchOption.TopDirectoryOnly).Single();
            File.Delete(temp);
            if (!TryCreateFileSymlink(temp, outsidePath))
                throw new Xunit.Sdk.XunitException("File symlinks are required for this regression test.");
        }

        using var handler = new FakeHttpHandler(body);
        Func<Task> download = () => VerifiedModelDownloader.EnsureModelAsync(
            modelDir, _logger, [asset], () => new HttpClient(handler),
            ReplaceTempWithLink, promote: null, CancellationToken.None);

        await download.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(modelDir, asset.ExpectedSha256!, asset.Filename)).Should().BeFalse();
        File.ReadAllBytes(outsidePath).Should().BeEquivalentTo(body);
        TempFiles(modelDir).Should().BeEmpty();
    }

    [Fact]
    public async Task ReplacedDestinationAncestorBeforeCreate_IsRejectedWithoutExternalPublication()
    {
        byte[] body = Encoding.UTF8.GetBytes("replaced destination ancestor");
        var asset = Asset("m2v-test", body);
        string modelDir = Path.Combine(ModelsRoot, asset.Identifier);
        string outsideDir = Path.Combine(_home, "outside-destination");
        Directory.CreateDirectory(outsideDir);
        bool replaced = false;

        void ReplaceDestinationAncestor()
        {
            if (replaced) return;
            replaced = true;
            string digestDir = Path.Combine(modelDir, asset.ExpectedSha256!);
            if (!TryCreateDirSymlink(digestDir, outsideDir))
                throw new Xunit.Sdk.XunitException("Directory symlinks are required for this regression test.");
        }

        using var handler = new FakeHttpHandler(body);
        Func<Task> download = () => VerifiedModelDownloader.EnsureModelAsync(
            modelDir, _logger, [asset], () => new HttpClient(handler),
            afterHashCompleted: null, beforeDirectoryCreate: ReplaceDestinationAncestor,
            promote: null, CancellationToken.None);

        await download.Should().ThrowAsync<InvalidDataException>();
        Directory.GetFileSystemEntries(outsideDir).Should().BeEmpty();
        TempFiles(modelDir).Should().BeEmpty();
    }

    [Fact]
    public async Task TempReplacedWithIdenticalBytes_IsRejectedOnIdentityMismatch()
    {
        byte[] body = Encoding.UTF8.GetBytes("identical replacement");
        var asset = Asset("m2v-test", body);
        string modelDir = Path.Combine(ModelsRoot, asset.Identifier);

        void ReplaceTempWithIdenticalBytes()
        {
            string temp = Directory.GetFiles(
                Path.Combine(modelDir, ".downloads"), "*.tmp", SearchOption.TopDirectoryOnly).Single();
            File.Delete(temp);
            File.WriteAllBytes(temp, body);
            // Guarantee identity drift even on filesystems with coarse timestamp granularity.
            File.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddHours(1));
        }

        using var handler = new FakeHttpHandler(body);
        Func<Task> download = () => VerifiedModelDownloader.EnsureModelAsync(
            modelDir, _logger, [asset], () => new HttpClient(handler),
            ReplaceTempWithIdenticalBytes, beforeDirectoryCreate: null,
            promote: null, beforePublish: null, CancellationToken.None);

        await download.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(modelDir, asset.ExpectedSha256!, asset.Filename)).Should().BeFalse(
            "a replaced temp must never publish even when its bytes still verify");
        TempFiles(modelDir).Should().BeEmpty();
    }

    [Fact]
    public async Task TempContentMutatedAfterHash_IsRejectedWithoutPublication()
    {
        byte[] body = Encoding.UTF8.GetBytes("original verified bytes");
        var asset = Asset("m2v-test", body);
        string modelDir = Path.Combine(ModelsRoot, asset.Identifier);

        void MutateTempContent()
        {
            string temp = Directory.GetFiles(
                Path.Combine(modelDir, ".downloads"), "*.tmp", SearchOption.TopDirectoryOnly).Single();
            File.WriteAllBytes(temp, Encoding.UTF8.GetBytes("mutated after hash completed!!"));
        }

        using var handler = new FakeHttpHandler(body);
        Func<Task> download = () => VerifiedModelDownloader.EnsureModelAsync(
            modelDir, _logger, [asset], () => new HttpClient(handler),
            MutateTempContent, beforeDirectoryCreate: null,
            promote: null, beforePublish: null, CancellationToken.None);

        await download.Should().ThrowAsync<Exception>();
        File.Exists(Path.Combine(modelDir, asset.ExpectedSha256!, asset.Filename)).Should().BeFalse();
        TempFiles(modelDir).Should().BeEmpty();
    }

    [Fact]
    public async Task MaliciousPromoteWritingOutside_IsRejectedWithoutTrustingMove()
    {
        byte[] body = Encoding.UTF8.GetBytes("malicious promote");
        var asset = Asset("m2v-test", body);
        string modelDir = Path.Combine(ModelsRoot, asset.Identifier);
        string outsideDir = Path.Combine(_home, "outside-promote");
        Directory.CreateDirectory(outsideDir);
        string outsidePath = Path.Combine(outsideDir, "evil.bin");

        void MaliciousPromote(string source, string target)
        {
            File.Copy(source, outsidePath, overwrite: true);
        }

        using var handler = new FakeHttpHandler(body);
        Func<Task> download = () => VerifiedModelDownloader.EnsureModelAsync(
            modelDir, _logger, [asset], () => new HttpClient(handler),
            afterHashCompleted: null, beforeDirectoryCreate: null,
            MaliciousPromote, beforePublish: null, CancellationToken.None);

        await download.Should().ThrowAsync<Exception>();
        File.Exists(Path.Combine(modelDir, asset.ExpectedSha256!, asset.Filename)).Should().BeFalse(
            "a move that does not publish the verified destination must be rejected");
        TempFiles(modelDir).Should().BeEmpty();
    }

    [Fact]
    public async Task DestinationAncestrySwappedBeforePublish_IsRejectedWithoutExternalPublication()
    {
        byte[] body = Encoding.UTF8.GetBytes("swapped before publish");
        var asset = Asset("m2v-test", body);
        string modelDir = Path.Combine(ModelsRoot, asset.Identifier);
        string outsideDir = Path.Combine(_home, "outside-swap");
        Directory.CreateDirectory(outsideDir);
        bool swapped = false;

        void SwapAncestry()
        {
            if (swapped) return;
            swapped = true;
            string digestDir = Path.Combine(modelDir, asset.ExpectedSha256!);
            try { Directory.Delete(digestDir, recursive: true); } catch { /* may not exist yet */ }
            if (!TryCreateDirSymlink(digestDir, outsideDir))
                throw new Xunit.Sdk.XunitException("Directory symlinks are required for this regression test.");
        }

        using var handler = new FakeHttpHandler(body);
        Func<Task> download = () => VerifiedModelDownloader.EnsureModelAsync(
            modelDir, _logger, [asset], () => new HttpClient(handler),
            afterHashCompleted: null, beforeDirectoryCreate: null,
            promote: null, SwapAncestry, CancellationToken.None);

        await download.Should().ThrowAsync<InvalidDataException>();
        Directory.GetFileSystemEntries(outsideDir).Should().BeEmpty();
        TempFiles(modelDir).Should().BeEmpty();
    }

    [Fact]
    public void VulkanManager_UsesCanonicalAssetDefinition()
    {
        VulkanModelManager.AssetDefinitions.Should().ContainSingle()
            .Which.Should().Be(VulkanModelAssets.DefaultAsset);
    }

    [Theory]
    [InlineData("m2v-test")]
    public async Task StreamFailure_LeavesNoVisibleModel_AndRetrySucceeds(string identifier)
    {
        byte[] body = Encoding.UTF8.GetBytes("drop mid-stream");
        var asset = Asset(identifier, body);
        string modelDir = Path.Combine(ModelsRoot, identifier);

        using var handler = new BlockingFakeHttpHandler(body, new ManualResetEventSlim(true), new ManualResetEventSlim(true), failAfter: 3);

        await ThrowAsync<IOException>(() =>
            DownloadM2V([asset], modelDir, () => new HttpClient(handler), CancellationToken.None));

        string digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        File.Exists(Path.Combine(modelDir, digest, asset.Filename)).Should().BeFalse("a truncated stream must not be promoted");
        TempFiles(modelDir).Should().BeEmpty("the failed temp must be deleted");

        // retry starts clean
        using var okHandler = new FakeHttpHandler(body);
        await DownloadM2V([asset], modelDir, () => new HttpClient(okHandler), CancellationToken.None);
        File.Exists(Path.Combine(modelDir, digest, asset.Filename)).Should().BeTrue();
    }

    // ── (e) matching bytes flushed, validated, atomically promoted, visible ONLY after ────────────

    [Theory]
    [InlineData("m2v-test")]
    public async Task MatchingDigest_IsAtomicallyPromoted(string identifier)
    {
        byte[] body = Encoding.UTF8.GetBytes("verified payload");
        var asset = Asset(identifier, body);
        string modelDir = Path.Combine(ModelsRoot, identifier);

        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        using var handler = new BlockingFakeHttpHandler(body, started, release);

        var task = Task.Run(() => DownloadM2V([asset], modelDir, () => new HttpClient(handler), CancellationToken.None));
        await Task.Run(() => started.Wait(TimeSpan.FromSeconds(5)));

        // before promotion the asset is not visible, and no temp is published as a model
        string digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        File.Exists(Path.Combine(modelDir, digest, asset.Filename)).Should().BeFalse("not visible until promoted");
        Model2VecModelManager.IsModelAvailable(modelDir).Should().BeFalse("a streaming temp is not an available model");

        release.Set();
        await task;

        File.Exists(Path.Combine(modelDir, digest, asset.Filename)).Should().BeTrue("visible only after promotion");
        File.ReadAllBytes(Path.Combine(modelDir, digest, asset.Filename)).Should().BeEquivalentTo(body);
        TempFiles(modelDir).Should().BeEmpty();
    }

    // ── Both managers share the same behaviour; run the core promotion assertions against each ────

    [Theory]
    [InlineData("m2v-test")]
    public async Task SharedPromotion_ContractsHold_ForBothManagers(string identifier)
    {
        byte[] body = Encoding.UTF8.GetBytes($"{identifier}-payload");
        string digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

        DownloadDelegate[] downloads = [DownloadM2V, DownloadVulkan];
        foreach (DownloadDelegate download in downloads)
        {
            string modelDir = Path.Combine(ModelsRoot, identifier);
            var asset = Asset(identifier, body);
            using var handler = new FakeHttpHandler(body);

            await download([asset], modelDir, () => new HttpClient(handler), CancellationToken.None);

            File.Exists(Path.Combine(modelDir, digest, asset.Filename)).Should().BeTrue();
            TempFiles(modelDir).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Model2VecDownloader_RejectsMixedLayoutLikeProvider()
    {
        (DownloadableAsset model, DownloadableAsset vocab) = PairAssets();
        string modelDir = Path.Combine(ModelsRoot, model.Identifier);
        Directory.CreateDirectory(modelDir);
        File.WriteAllBytes(Path.Combine(modelDir, model.Filename), Encoding.UTF8.GetBytes("model"));
        Directory.CreateDirectory(Path.Combine(modelDir, vocab.ExpectedSha256!));
        File.WriteAllBytes(
            Path.Combine(modelDir, vocab.ExpectedSha256!, vocab.Filename), Encoding.UTF8.GetBytes("vocab"));

        Func<Task> download = () => DownloadM2V([model, vocab], modelDir,
            () => new HttpClient(new FakeHttpHandler([])), CancellationToken.None);

        await download.Should().ThrowAsync<InvalidDataException>().WithMessage("*mixed*");
    }

    [Fact]
    public async Task Model2VecDownloader_RejectsTamperedDirectAssetLikeProvider()
    {
        (DownloadableAsset model, DownloadableAsset vocab) = PairAssets();
        string modelDir = Path.Combine(ModelsRoot, model.Identifier);
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, model.Filename), "tampered");
        File.WriteAllText(Path.Combine(modelDir, vocab.Filename), "vocab");

        Func<Task> download = () => DownloadM2V([model, vocab], modelDir,
            () => new HttpClient(new FakeHttpHandler([])), CancellationToken.None);

        await download.Should().ThrowAsync<ModelDownloadException>().WithMessage("*model.safetensors*");
    }

    [Fact]
    public void VulkanModelPath_ResolvesDigestNestedAsset()
    {
        string modelDir = Path.Combine(ModelsRoot, VulkanModelManager.Identifier);
        byte[] body = Encoding.UTF8.GetBytes("vulkan");
        var asset = new DownloadableAsset(
            VulkanModelManager.Identifier, "https://example.test/model", VulkanModelManager.DefaultModelFile,
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());
        string digest = asset.ExpectedSha256!;
        string expectedPath = Path.Combine(modelDir, digest, VulkanModelManager.DefaultModelFile);
        Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
        File.WriteAllBytes(expectedPath, body);

        VulkanModelManager.GetModelPath(modelDir, asset).Should().Be(expectedPath);
    }

    [Fact]
    public async Task Model2VecDownloader_CompletesMissingAssetInExistingDirectLayout()
    {
        (DownloadableAsset model, DownloadableAsset vocab) = PairAssets();
        string modelDir = Path.Combine(ModelsRoot, model.Identifier);
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, model.Filename), "model");

        using var handler = new PathMappedFakeHttpHandler(
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [model.Filename] = Encoding.UTF8.GetBytes("model"),
                [vocab.Filename] = Encoding.UTF8.GetBytes("vocab"),
            });

        await DownloadM2V([model, vocab], modelDir,
            () => new HttpClient(handler), CancellationToken.None);

        File.Exists(Path.Combine(modelDir, model.Filename)).Should().BeTrue();
        File.Exists(Path.Combine(modelDir, vocab.Filename)).Should().BeTrue();
        Directory.Exists(Path.Combine(modelDir, vocab.ExpectedSha256!)).Should().BeFalse();
        VerifiedModelDownloader.IsAssetAvailable(modelDir, model).Should().BeTrue();
        VerifiedModelDownloader.IsAssetAvailable(modelDir, vocab).Should().BeTrue();
    }

    [Fact]
    public async Task Model2VecDownloader_CompletesMissingAssetInExistingNestedLayout()
    {
        (DownloadableAsset model, DownloadableAsset vocab) = PairAssets();
        string modelDir = Path.Combine(ModelsRoot, model.Identifier);
        string modelPath = Path.Combine(modelDir, model.ExpectedSha256!, model.Filename);
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        File.WriteAllText(modelPath, "model");

        using var handler = new PathMappedFakeHttpHandler(
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [model.Filename] = Encoding.UTF8.GetBytes("model"),
                [vocab.Filename] = Encoding.UTF8.GetBytes("vocab"),
            });

        await DownloadM2V([model, vocab], modelDir,
            () => new HttpClient(handler), CancellationToken.None);

        File.Exists(Path.Combine(modelDir, vocab.ExpectedSha256!, vocab.Filename)).Should().BeTrue();
        File.Exists(Path.Combine(modelDir, vocab.Filename)).Should().BeFalse();
        VerifiedModelDownloader.IsAssetAvailable(modelDir, model).Should().BeTrue();
        VerifiedModelDownloader.IsAssetAvailable(modelDir, vocab).Should().BeTrue();
    }

    private static (DownloadableAsset Model, DownloadableAsset Vocab) PairAssets()
    {
        static string Digest(string text) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        return (
            new DownloadableAsset("m2v-parity", "https://example.test/model/model.safetensors", "model.safetensors", Digest("model")),
            new DownloadableAsset("m2v-parity", "https://example.test/vocab/vocab.txt", "vocab.txt", Digest("vocab")));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static DownloadableAsset Asset(string identifier, byte[] body)
        => new(identifier, $"https://example.test/{identifier}/model", "model.safetensors",
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());

    private IEnumerable<string> TempFiles(string modelDir)
        => Directory.GetFiles(modelDir, "*.tmp", SearchOption.AllDirectories);

    private bool NoTempFiles(string modelDir) => TempFiles(modelDir).Any() == false;

    private static bool TryCreateDirSymlink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateFileSymlink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static async Task ThrowAsync<TException>(Func<Task> invocation)
        where TException : Exception => await invocation.Should().ThrowAsync<TException>();

    // ── Fake transport ────────────────────────────────────────────────────────────────────────────

    /// <summary>Serves a fixed body, optionally delaying reads to force concurrency.</summary>
    private sealed class FakeHttpHandler : DisposableHttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly int _delayMs;
        private readonly ConcurrentCounter? _concurrency;

        public FakeHttpHandler(byte[] body, int delayMs = 0, ConcurrentCounter? concurrency = null)
        {
            _body = body;
            _delayMs = delayMs;
            _concurrency = concurrency;
        }

        protected override HttpResponseMessage Build(HttpRequestMessage request)
            => Ok(new DelayingStream(_body, _delayMs, failAfter: 0, _concurrency));
    }

    private sealed class PathMappedFakeHttpHandler : DisposableHttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, byte[]> _bodies;

        public PathMappedFakeHttpHandler(IReadOnlyDictionary<string, byte[]> bodies) => _bodies = bodies;

        protected override HttpResponseMessage Build(HttpRequestMessage request)
        {
            string filename = Path.GetFileName(request.RequestUri!.AbsolutePath);
            return Ok(new MemoryStream(_bodies[filename], writable: false));
        }
    }

    /// <summary>Serves a body with a start gate, an optional release gate, a per-read delay, a
    /// concurrency gauge, and optional mid-stream failure.</summary>
    private sealed class BlockingFakeHttpHandler : DisposableHttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _gate;
        private readonly int _delayMs;
        private readonly int _failAfter;
        private readonly ConcurrentCounter? _concurrency;

        public BlockingFakeHttpHandler(
            byte[] body, ManualResetEventSlim started, ManualResetEventSlim gate,
            int delayMs = 0, int failAfter = 0, ConcurrentCounter? concurrency = null)
        {
            _body = body;
            _started = started;
            _gate = gate;
            _delayMs = delayMs;
            _failAfter = failAfter;
            _concurrency = concurrency;
        }

        protected override HttpResponseMessage Build(HttpRequestMessage request)
        {
            _started.Set();
            return Ok(new DelayingStream(_body, _delayMs, _failAfter, _concurrency, _gate));
        }
    }

    private abstract class DisposableHttpMessageHandler : HttpMessageHandler
    {
        protected abstract HttpResponseMessage Build(HttpRequestMessage request);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Build(request);

        protected static HttpResponseMessage Ok(Stream stream) =>
            new(HttpStatusCode.OK) { Content = new StreamContent(stream) };
    }

    /// <summary>
    /// A response body stream that can delay reads (to force concurrency), fail part-way through
    /// (to simulate a truncated download), and gate the first read (to observe pre-promotion state).
    /// Reads are chunked so a large body produces many reads over a wide time window.
    /// </summary>
    private sealed class DelayingStream : Stream
    {
        private const int DefaultChunkSize = 256;

        private readonly byte[] _data;
        private readonly int _delayMs;
        private readonly int _failAfter;
        private readonly ManualResetEventSlim? _gate;
        private readonly ConcurrentCounter? _concurrency;
        private int _pos;

        public DelayingStream(byte[] data, int delayMs, int failAfter, ConcurrentCounter? concurrency = null, ManualResetEventSlim? gate = null, int chunkSize = DefaultChunkSize)
        {
            _data = data;
            _delayMs = delayMs;
            _failAfter = failAfter;
            _concurrency = concurrency;
            _gate = gate;
            ChunkSize = chunkSize;
        }

        public int ChunkSize { get; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            int active = _concurrency?.Enter() ?? 0;
            try
            {
                if (_failAfter > 0 && _pos >= _failAfter)
                    throw new IOException("simulated partial stream failure");
                ct.ThrowIfCancellationRequested();
                if (_gate is not null) _gate.Wait(ct);
                ct.ThrowIfCancellationRequested(); // re-check after the gate releases (cancel or signal)
                if (_delayMs > 0) await Task.Delay(_delayMs, ct);

                int available = _data.Length - _pos;
                if (available == 0) return 0;
                int n = Math.Min(available, Math.Min(ChunkSize, buffer.Length));
                _data.AsSpan(_pos, n).CopyTo(buffer.Span);
                _pos += n;
                return n;
            }
            finally
            {
                _concurrency?.Leave();
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Thread-safe gauge measuring the maximum number of concurrently streaming reads.</summary>
    private sealed class ConcurrentCounter
    {
        private int _active;
        private int _max;
        private readonly object _gate = new();

        public int Enter()
        {
            int n = Interlocked.Increment(ref _active);
            lock (_gate) { if (n > _max) _max = n; }
            return n;
        }

        public void Leave() => Interlocked.Decrement(ref _active);

        public int Max => Volatile.Read(ref _max);
    }
}
