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

    /// <summary>Downloads every asset in sequence, skipping any that is already verified in place.</summary>
    public static async Task EnsureModelAsync(
        string modelDir, ILogger logger,
        IReadOnlyList<DownloadableAsset> assets, Func<HttpClient> httpClientFactory, CancellationToken ct = default)
    {
        foreach (DownloadableAsset asset in assets)
            await DownloadAsync(asset, modelDir, logger, httpClientFactory, ct).ConfigureAwait(false);
    }

    /// <summary>Downloads and verifies a single asset, publishing it only when its digest checks out.</summary>
    public static async Task DownloadAsync(
        DownloadableAsset asset, string modelDir, ILogger logger, Func<HttpClient> httpClientFactory, CancellationToken ct)
    {
        if (IsAssetAvailable(modelDir, asset))
        {
            logger.LogInformation("Model already present: {Path}", Path.Combine(modelDir, asset.Filename));
            return;
        }

        string tmpPath = ScratchTemp(modelDir, asset);
        Directory.CreateDirectory(ScratchDirectory(modelDir));

        using var http = httpClientFactory();
        http.Timeout = TimeSpan.FromMinutes(10);
        logger.LogInformation("Downloading {File} from {Url}...", asset.Filename, asset.Url);

        byte[] digest;
        try
        {
            using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            digest = await WriteAndHashAsync(body, tmpPath, ct);
        }
        catch
        {
            await DeleteSilentlyAsync(tmpPath);
            throw;
        }

        string digestKey = Convert.ToHexString(digest).ToLowerInvariant();
        string promotedPath = Path.Combine(modelDir, digestKey, asset.Filename);

        string? expected = asset.ExpectedSha256;
        if (!string.IsNullOrEmpty(expected)
            && !string.Equals(digestKey, expected!, StringComparison.OrdinalIgnoreCase))
        {
            await DeleteSilentlyAsync(tmpPath);
            throw new ModelDownloadException(
                $"Downloaded {asset.Filename} digest {digestKey} does not match expected {expected}.");
        }

        // A concurrent download may have already published the same verified content.
        if (File.Exists(promotedPath))
        {
            await DeleteSilentlyAsync(tmpPath);
            logger.LogInformation("Model already present: {Path}", promotedPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(promotedPath)!);
        lock (PromotionLock)
        {
            if (File.Exists(promotedPath))
            {
                // Lost the race: another verified temp won. Discard this validated duplicate.
                File.Delete(tmpPath);
                logger.LogInformation("Model already present: {Path}", promotedPath);
                return;
            }

            File.Move(tmpPath, promotedPath); // atomic same-volume rename of the verified temp
        }

        logger.LogInformation("Downloaded {File} ({Size:F1} MB)", asset.Filename,
            new FileInfo(promotedPath).Length / (1024.0 * 1024));
    }

    /// <summary>Whether the asset is published — as a direct file (legacy/setup layout) or a verified
    /// content-addressed file. A <c>.tmp</c> or partial file never counts as available.</summary>
    public static bool IsAssetAvailable(string modelDir, DownloadableAsset asset)
    {
        if (File.Exists(Path.Combine(modelDir, asset.Filename)))
            return true;

        if (!string.IsNullOrEmpty(asset.ExpectedSha256))
        {
            string digestKey = asset.ExpectedSha256!.ToLowerInvariant();
            if (File.Exists(Path.Combine(modelDir, digestKey, asset.Filename)))
                return true;
        }

        return false;
    }

    private static string ScratchDirectory(string modelDir) => Path.Combine(modelDir, TempSubdirectory);

    private static string ScratchTemp(string modelDir, DownloadableAsset asset)
        => Path.Combine(ScratchDirectory(modelDir), $".{asset.Filename}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");

    /// <summary>Streams <paramref name="body"/> into <paramref name="tmpPath"/>, hashing as it writes.
    /// The file is flushed and closed before the digest is returned so validation runs on final data.</summary>
    private static async Task<byte[]> WriteAndHashAsync(Stream body, string tmpPath, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var crypto = new CryptoStream(fs, sha, CryptoStreamMode.Write);

        await body.CopyToAsync(crypto, ct).ConfigureAwait(false);
        // Disposing CryptoStream flushes the final block to the file and finalizes the SHA-256, so the
        // temp is flushed/closed before the digest is returned and validated downstream.
        crypto.Dispose();
        return sha.Hash!;
    }

    private static async Task DeleteSilentlyAsync(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup of a partial temp */ }
    }
}
