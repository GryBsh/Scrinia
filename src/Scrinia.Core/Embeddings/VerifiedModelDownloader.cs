using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using System.Net.Http;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// Thrown when a downloaded asset's content digest does not match the expected digest. The offending
/// temp file is already deleted before this propagates, so nothing partial is ever left behind.
/// </summary>
public sealed class ModelDownloadException : Exception
{
    public ModelDownloadException(string message) : base(message) { }
    public ModelDownloadException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Content-verified, atomically-promoted model download shared by every embedding-model manager.
///
/// For each asset the downloader streams bytes to a unique per-download temp while hashing them,
/// flushes and closes the temp before validating, rejects anything whose digest does not match (or
/// which is cancelled or fails to stream), and atomically moves ONLY a validated temp into place. A
/// concurrent winner of the promotion race wins; the loser discards its validated duplicate and
/// treats the already-valid destination as success, so retries stay idempotent.
/// </summary>
public static class VerifiedModelDownloader
{
    private const int StreamBufferSize = 81_920;
    private const string TempSubdirectory = ".downloads";

    // Serialises the check-then-move promotion so two verified downloads never corrupt one another.
    private static readonly object PromotionLock = new();

    /// <summary>Resolves one verified asset to its direct or digest-nested published path.</summary>
    public static string ResolveAssetPath(string modelDir, DownloadableAsset asset)
        => VerifiedAssetResolver.ResolvePath(modelDir, asset);

    /// <summary>Downloads every asset in sequence, skipping any that is already verified in place.</summary>
    public static async Task EnsureModelAsync(
        string modelDir, ILogger logger,
        IReadOnlyList<DownloadableAsset> assets, Func<HttpClient> httpClientFactory, CancellationToken ct = default)
        => await EnsureModelAsync(modelDir, logger, assets, httpClientFactory, afterHashCompleted: null, ct)
            .ConfigureAwait(false);

    /// <summary>Prepares a model root after proving that its complete existing ancestry is safe.</summary>
    public static void PrepareModelDirectory(string modelDir)
        => VerifiedAssetResolver.PrepareDirectoryForWrite(modelDir, modelDir);

    internal static async Task EnsureModelAsync(
        string modelDir, ILogger logger,
        IReadOnlyList<DownloadableAsset> assets, Func<HttpClient> httpClientFactory,
        Action? afterHashCompleted, CancellationToken ct)
        => await EnsureModelAsync(modelDir, logger, assets, httpClientFactory,
            afterHashCompleted, promote: null, ct).ConfigureAwait(false);

    internal static async Task EnsureModelAsync(
        string modelDir, ILogger logger,
        IReadOnlyList<DownloadableAsset> assets, Func<HttpClient> httpClientFactory,
        Action? afterHashCompleted, Action<string, string>? promote, CancellationToken ct)
        => await EnsureModelAsync(modelDir, logger, assets, httpClientFactory,
            afterHashCompleted, beforeDirectoryCreate: null, promote, ct).ConfigureAwait(false);

    internal static async Task EnsureModelAsync(
        string modelDir, ILogger logger,
        IReadOnlyList<DownloadableAsset> assets, Func<HttpClient> httpClientFactory,
        Action? afterHashCompleted, Action? beforeDirectoryCreate,
        Action<string, string>? promote, CancellationToken ct)
        => await EnsureModelAsync(modelDir, logger, assets, httpClientFactory,
            afterHashCompleted, beforeDirectoryCreate, promote, beforePublish: null, ct).ConfigureAwait(false);

    internal static async Task EnsureModelAsync(
        string modelDir, ILogger logger,
        IReadOnlyList<DownloadableAsset> assets, Func<HttpClient> httpClientFactory,
        Action? afterHashCompleted, Action? beforeDirectoryCreate,
        Action<string, string>? promote, Action? beforePublish, CancellationToken ct)
    {
        PrepareModelDirectory(modelDir);
        try
        {
            await VerifiedAssetResolver.ResolveAsync(modelDir, assets, ct).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is FileNotFoundException or ModelDownloadException)
        {
            // At least one asset is missing; download only the missing assets below.
        }

        bool nestedLayout = VerifiedAssetResolver.DetermineNestedLayout(modelDir, assets);
        foreach (DownloadableAsset asset in assets)
            await DownloadAsync(asset, modelDir, logger, httpClientFactory, assets, nestedLayout,
                afterHashCompleted, beforeDirectoryCreate, promote, beforePublish, ct)
                .ConfigureAwait(false);
    }

    /// <summary>Downloads and verifies a single asset, publishing it only when its digest checks out.</summary>
    public static async Task DownloadAsync(
        DownloadableAsset asset, string modelDir, ILogger logger, Func<HttpClient> httpClientFactory,
        CancellationToken ct)
        => await DownloadAsync(asset, modelDir, logger, httpClientFactory, [asset], nestedLayout: true,
            afterHashCompleted: null, beforeDirectoryCreate: null, promote: null, beforePublish: null, ct)
            .ConfigureAwait(false);

    private static async Task DownloadAsync(
        DownloadableAsset asset, string modelDir, ILogger logger, Func<HttpClient> httpClientFactory,
        IReadOnlyList<DownloadableAsset> allAssets, bool nestedLayout,
        Action? afterHashCompleted, Action? beforeDirectoryCreate,
        Action<string, string>? promote, Action? beforePublish, CancellationToken ct)
    {
        try
        {
            await VerifiedAssetResolver.ResolveAsync(modelDir, [asset], ct).ConfigureAwait(false);
            logger.LogInformation("Model already present: {Path}", Path.Combine(modelDir, asset.Filename));
            return;
        }
        catch (Exception ex) when (ex is FileNotFoundException or ModelDownloadException)
        {
            // The asset is absent; proceed with the verified download.
        }

        string tmpPath = ScratchTemp(modelDir, asset);
        VerifiedAssetResolver.ValidateDestinationPath(modelDir, tmpPath);
        VerifiedAssetResolver.PrepareDirectoryForWrite(modelDir, ScratchDirectory(modelDir));
        VerifiedAssetResolver.ValidateDestinationPath(modelDir, tmpPath);

        using var http = httpClientFactory();
        http.Timeout = TimeSpan.FromMinutes(10);
        logger.LogInformation("Downloading {File} from {Url}...", asset.Filename, asset.Url);

        byte[] digest;
        TempWriteIdentity writtenIdentity;
        try
        {
            using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            (digest, writtenIdentity) = await WriteAndHashAsync(body, tmpPath, ct);

            ct.ThrowIfCancellationRequested();
            afterHashCompleted?.Invoke();
            ct.ThrowIfCancellationRequested();

            string digestKey = Convert.ToHexString(digest).ToLowerInvariant();
            string promotedPath = nestedLayout
                ? Path.Combine(modelDir, digestKey, asset.Filename)
                : Path.Combine(modelDir, asset.Filename);

            string? expected = asset.ExpectedSha256;
            if (!string.IsNullOrEmpty(expected)
                && !string.Equals(digestKey, expected!, StringComparison.OrdinalIgnoreCase))
            {
                throw new ModelDownloadException(
                    $"Downloaded {asset.Filename} digest {digestKey} does not match expected {expected}.");
            }

            ValidateTempForPromotion(modelDir, tmpPath, digestKey, writtenIdentity);
            bool lostPromotionRace = false;
            lock (PromotionLock)
            {
                // Revalidate the observed destination instead of trusting existence. This covers
                // stale/tampered files and a winner that changed between the async download and
                // this promotion boundary.
                if (IsValidPublishedAsset(modelDir, asset))
                {
                    File.Delete(tmpPath);
                    logger.LogInformation("Model already present: {Path}", promotedPath);
                    return;
                }

                ct.ThrowIfCancellationRequested();
                ValidateTempForPromotion(modelDir, tmpPath, digestKey, writtenIdentity);
                VerifiedAssetResolver.PrepareDirectoryForWrite(
                    modelDir, Path.GetDirectoryName(promotedPath)!, beforeDirectoryCreate);
                VerifiedAssetResolver.ValidateDestinationPath(modelDir, promotedPath);
                // Deterministic race seam: runs after the final pre-move validation but before
                // the move, so tests can swap the destination ancestry without relying on an
                // OS-level race. Production passes null.
                beforePublish?.Invoke();
                // Conservative revalidation during publication: the ancestry may have been
                // swapped between validation and create/move, so re-prove both ends
                // immediately before the move with no-follow semantics.
                ValidateTempForPromotion(modelDir, tmpPath, digestKey, writtenIdentity);
                VerifiedAssetResolver.ValidateDestinationPath(modelDir, promotedPath);
                try
                {
                    if (promote is not null)
                        promote(tmpPath, promotedPath);
                    else
                        File.Move(tmpPath, promotedPath, overwrite: true);
                }
                catch (IOException)
                {
                    // Another process may have won the promotion race. Validate that winner
                    // outside the synchronous lock before treating this operation as successful.
                    lostPromotionRace = true;
                }

                if (!lostPromotionRace)
                    ValidatePublishedAfterMove(modelDir, promotedPath, digestKey);
            }

            if (lostPromotionRace)
            {
                // Never trust the destination's existence alone: re-resolve and hash-validate the
                // winner, then confirm the model's currently published assets use one layout.
                await ValidatePromotionWinnerAsync(modelDir, asset, allAssets, ct)
                    .ConfigureAwait(false);
                await DeleteSilentlyAsync(tmpPath).ConfigureAwait(false);
                logger.LogInformation("Model already present: {Path}", promotedPath);
                return;
            }

            logger.LogInformation("Downloaded {File} ({Size:F1} MB)", asset.Filename,
                new FileInfo(promotedPath).Length / (1024.0 * 1024));
        }
        catch
        {
            await DeleteSilentlyAsync(tmpPath);
            throw;
        }
    }

    private static async Task ValidatePromotionWinnerAsync(
        string modelDir, DownloadableAsset asset, IReadOnlyList<DownloadableAsset> allAssets,
        CancellationToken ct)
    {
        await VerifiedAssetResolver.ResolveAsync(modelDir, [asset], ct).ConfigureAwait(false);
        _ = VerifiedAssetResolver.DetermineNestedLayout(modelDir, allAssets);
    }

    private static bool IsValidPublishedAsset(string modelDir, DownloadableAsset asset)
    {
        try
        {
            _ = VerifiedAssetResolver.Resolve(modelDir, [asset]);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or ModelDownloadException)
        {
            return false;
        }
    }

    /// <summary>Whether the asset is published — as a direct file (legacy/setup layout) or a verified
    /// content-addressed file. A <c>.tmp</c> or partial file never counts as available.</summary>
    public static bool IsAssetAvailable(string modelDir, DownloadableAsset asset)
    {
        try
        {
            VerifiedAssetResolver.Resolve(modelDir, [asset]);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (InvalidDataException) { return false; }
        catch (ModelDownloadException) { return false; }
    }

    private static string ScratchDirectory(string modelDir) => Path.Combine(modelDir, TempSubdirectory);

    private static string ScratchTemp(string modelDir, DownloadableAsset asset)
        => Path.Combine(ScratchDirectory(modelDir), $".{asset.Filename}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");

    /// <summary>Streams <paramref name="body"/> into <paramref name="tmpPath"/>, hashing as it writes.
    /// The file is flushed and closed before the digest is returned so validation runs on final data.
    /// Captures the written file identity so promotion can reject a replaced temp even when the
    /// replacement carries identical bytes.</summary>
    private static async Task<(byte[] Digest, TempWriteIdentity Identity)> WriteAndHashAsync(Stream body, string tmpPath, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var crypto = new CryptoStream(fs, sha, CryptoStreamMode.Write);

        await body.CopyToAsync(crypto, ct).ConfigureAwait(false);
        // Disposing CryptoStream flushes the final block to the file and finalizes the SHA-256, so the
        // temp is flushed/closed before the digest is returned and validated downstream.
        crypto.Dispose();
        byte[] digest = sha.Hash!;
        return (digest, CaptureTempIdentity(tmpPath));
    }

    internal sealed record TempWriteIdentity(long Length, DateTime LastWriteUtc, DateTime CreationUtc);

    private static TempWriteIdentity CaptureTempIdentity(string tempPath)
    {
        var info = new FileInfo(tempPath);
        return new TempWriteIdentity(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
    }

    private static async Task DeleteSilentlyAsync(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup of a partial temp */ }
    }

    private static void ValidateTempForPromotion(
        string modelDir, string tempPath, string expectedDigest, TempWriteIdentity expectedIdentity)
    {
        if (string.IsNullOrEmpty(tempPath))
            throw new InvalidDataException("Verified model temp path is missing before promotion.");

        // Lexical + reparse-safe containment first: the temp must still live under the model root.
        VerifiedAssetResolver.ValidateDestinationPath(modelDir, tempPath);

        if (!File.Exists(tempPath))
            throw new InvalidDataException($"Verified model temp '{tempPath}' disappeared before promotion.");

        RejectTempLink(tempPath);

        var current = new FileInfo(tempPath);
        if (current.LinkTarget is not null)
            throw new InvalidDataException($"Verified model temp '{tempPath}' is not a regular file.");

        if (current.Length != expectedIdentity.Length
            || current.LastWriteTimeUtc != expectedIdentity.LastWriteUtc
            || current.CreationTimeUtc != expectedIdentity.CreationUtc)
            throw new InvalidDataException(
                $"Verified model temp '{tempPath}' was replaced before promotion.");

        using var stream = new FileStream(
            tempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            StreamBufferSize, FileOptions.SequentialScan);
        // No-follow revalidation after open: a temp swapped to a link between the attribute
        // check and the open must not be hashed through the link.
        RejectTempLink(tempPath);
        var reopened = new FileInfo(tempPath);
        if (reopened.LinkTarget is not null)
            throw new InvalidDataException($"Verified model temp '{tempPath}' is not a regular file.");
        if (reopened.Length != expectedIdentity.Length)
            throw new InvalidDataException(
                $"Verified model temp '{tempPath}' was replaced before promotion.");

        string actualDigest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actualDigest, expectedDigest, StringComparison.OrdinalIgnoreCase))
            throw new ModelDownloadException(
                $"Verified model temp '{tempPath}' changed before promotion; digest is {actualDigest}.");
    }

    private static void RejectTempLink(string tempPath)
    {
        FileAttributes attributes = File.GetAttributes(tempPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException($"Verified model temp '{tempPath}' is not a regular file.");
    }

    private static void ValidatePublishedAfterMove(string modelDir, string publishedPath, string expectedDigest)
    {
        // The destination ancestry may have been swapped during the move itself. Re-prove
        // containment with no-follow semantics, reject a linked publication, and confirm the
        // published bytes still match the verified digest. On failure remove the publication
        // rather than leaving a suspect file behind.
        try
        {
            VerifiedAssetResolver.ValidateDestinationPath(modelDir, publishedPath);
            RejectTempLink(publishedPath);
            if (new FileInfo(publishedPath).LinkTarget is not null)
                throw new InvalidDataException(
                    $"Published model '{publishedPath}' is not a regular file.");

            using var stream = new FileStream(
                publishedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                StreamBufferSize, FileOptions.SequentialScan);
            RejectTempLink(publishedPath);
            string actualDigest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actualDigest, expectedDigest, StringComparison.OrdinalIgnoreCase))
                throw new ModelDownloadException(
                    $"Published model '{publishedPath}' digest {actualDigest} does not match verified {expectedDigest}.");
        }
        catch
        {
            try { if (File.Exists(publishedPath)) File.Delete(publishedPath); } catch { /* best-effort */ }
            throw;
        }
    }
}
