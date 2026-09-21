using System.Security.Cryptography;
using Scrinia.Core;

namespace Scrinia.Core.Embeddings;

internal sealed record VerifiedAssetPath(DownloadableAsset Asset, string Path);

internal static class VerifiedAssetResolver
{
    public static string ResolvePath(string modelDir, DownloadableAsset asset)
        => Resolve(modelDir, [asset]).Single().Path;

    public static bool DetermineNestedLayout(string modelDir, IReadOnlyList<DownloadableAsset> assets)
    {
        bool? nestedLayout = null;
        foreach (DownloadableAsset asset in assets)
        {
            string directPath = Path.Combine(modelDir, asset.Filename);
            string nestedPath = Path.Combine(modelDir, asset.ExpectedSha256!.ToLowerInvariant(), asset.Filename);
            bool directExists = File.Exists(directPath);
            bool nestedExists = File.Exists(nestedPath);
            if (!directExists && !nestedExists)
                continue;

            bool isNested = nestedExists;
            if (nestedLayout is not null && nestedLayout.Value != isNested)
                throw new InvalidDataException("Assets use mixed direct and digest-nested layouts.");

            nestedLayout = isNested;
        }

        return nestedLayout ?? true;
    }

    private const int HashBufferSize = 81_920;

    public static IReadOnlyList<VerifiedAssetPath> Resolve(
        string modelDir, IReadOnlyList<DownloadableAsset> assets)
    {
        IReadOnlyList<VerifiedAssetPath> paths = ResolvePaths(modelDir, assets);
        foreach (VerifiedAssetPath path in paths)
            ValidateContent(path);
        return paths;
    }

    public static async Task<IReadOnlyList<VerifiedAssetPath>> ResolveAsync(
        string modelDir, IReadOnlyList<DownloadableAsset> assets, CancellationToken ct)
    {
        IReadOnlyList<VerifiedAssetPath> paths = ResolvePaths(modelDir, assets);
        foreach (VerifiedAssetPath path in paths)
            await ValidateContentAsync(path, ct).ConfigureAwait(false);
        return paths;
    }

    internal static void ValidateDestinationPath(string modelDir, string destinationPath)
    {
        string modelRoot = Path.GetFullPath(modelDir);
        string destination = Path.GetFullPath(destinationPath);

        if (!PathContainment.IsWithin(modelRoot, destination))
            throw new InvalidDataException($"Asset destination '{destination}' is outside model directory '{modelRoot}'.");

        string current = destination;
        while (true)
        {
            RejectReparsePointIfPresent(current);
            string? parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparisonForPaths()))
                break;
            current = parent;
        }
    }

    internal static void PrepareDirectoryForWrite(
        string modelDir, string directoryPath, Action? beforeCreate = null)
    {
        ValidateDestinationPath(modelDir, directoryPath);
        if (Directory.Exists(directoryPath))
        {
            ValidateDestinationPath(modelDir, directoryPath);
            return;
        }

        beforeCreate?.Invoke();
        ValidateDestinationPath(modelDir, directoryPath);
        Directory.CreateDirectory(directoryPath);
        ValidateDestinationPath(modelDir, directoryPath);
    }

    internal static List<VerifiedAssetPath> ResolvePaths(
        string modelDir, IReadOnlyList<DownloadableAsset> assets)
    {
        if (assets.Count == 0)
            throw new ArgumentException("At least one asset is required.", nameof(assets));

        var seenFilenames = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new List<VerifiedAssetPath>(assets.Count);
        var missing = new List<DownloadableAsset>();
        bool? nestedLayout = null;

        foreach (DownloadableAsset asset in assets)
        {
            ValidateAssetDefinition(asset, seenFilenames);

            string directPath = Path.Combine(modelDir, asset.Filename);
            string? nestedPath = IsSha256(asset.ExpectedSha256)
                ? Path.Combine(modelDir, asset.ExpectedSha256!.ToLowerInvariant(), asset.Filename)
                : null;
            ValidateDestinationPath(modelDir, directPath);
            if (nestedPath is not null)
                ValidateDestinationPath(modelDir, nestedPath);
            bool directExists = File.Exists(directPath);
            bool nestedExists = nestedPath is not null && File.Exists(nestedPath);

            if (directExists && nestedExists)
                throw new InvalidDataException(
                    $"Asset {asset.Filename} exists in both direct and digest-nested layouts.");

            RejectMalformedNestedCandidates(modelDir, asset);

            if (!directExists && !nestedExists)
            {
                missing.Add(asset);
                continue;
            }

            bool isNested = nestedExists;
            if (nestedLayout is not null && nestedLayout.Value != isNested)
                throw new InvalidDataException("Assets use mixed direct and digest-nested layouts.");

            nestedLayout = isNested;
            resolved.Add(new VerifiedAssetPath(asset, isNested ? nestedPath! : directPath));
        }

        if (missing.Count > 0)
            throw new FileNotFoundException(
                $"Asset {missing[0].Filename} not found in direct or digest-nested layout.",
                Path.Combine(modelDir, missing[0].Filename));

        return resolved;
    }

    private static void ValidateAssetDefinition(
        DownloadableAsset asset, HashSet<string> seenFilenames)
    {
        if (!seenFilenames.Add(asset.Filename))
            throw new InvalidDataException($"Asset {asset.Filename} is declared more than once.");

        if (Path.IsPathRooted(asset.Filename) ||
            !string.Equals(Path.GetFileName(asset.Filename), asset.Filename, StringComparison.Ordinal))
            throw new ArgumentException($"Asset filename '{asset.Filename}' must be a file name.", nameof(asset));

        if (string.IsNullOrWhiteSpace(asset.ExpectedSha256) || !IsSha256(asset.ExpectedSha256))
            throw new ArgumentException(
                $"Asset {asset.Filename} must declare a valid SHA-256 digest.", nameof(asset));
    }

    private static void RejectMalformedNestedCandidates(string modelDir, DownloadableAsset asset)
    {
        if (!Directory.Exists(modelDir))
            return;

        foreach (string directory in Directory.EnumerateDirectories(modelDir))
        {
            string? directoryName = Path.GetFileName(directory);
            string candidatePath = Path.Combine(directory, asset.Filename);
            if (!File.Exists(candidatePath))
                continue;

            if (!IsSha256(directoryName))
                throw new InvalidDataException($"Nested asset directory '{directoryName}' is not a SHA-256 digest.");
            if (!string.Equals(directoryName, asset.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Nested asset {asset.Filename} is under digest '{directoryName}', expected '{asset.ExpectedSha256}'.");
        }
    }

    /// <summary>
    /// Cheap availability pre-check for one asset. Reads only the SafeTensors <b>header</b> — never the
    /// tensor body — and confirms the manifest is well-formed and declares a file length that matches on
    /// disk. A truncated or corrupt manifest is caught here without reading the ~22MB body; the full-file
    /// SHA-256 that proves on-disk content matches the pinned digest stays in <see cref="ValidateContent"/>,
    /// the gate <c>Load</c> runs before the model embeds.
    /// </summary>
    internal static void VerifyAssetDigest(VerifiedAssetPath path)
    {
        if (!path.Asset.Filename.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
            return; // non-tensor asset (e.g. vocab): existence is the signal; Load's full hash gates content.

        long declaredLength;
        using var fs = new FileStream(
            path.Path, FileMode.Open, FileAccess.Read, FileShare.Read, HashBufferSize, FileOptions.SequentialScan);
        declaredLength = SafeTensorsReader.VerifyManifestLength(fs);

        long actualLength = new FileInfo(path.Path).Length;
        if (actualLength != declaredLength)
            throw new ModelDownloadException(
                $"{path.Asset.Filename} manifest declares {declaredLength} bytes but the file is {actualLength}.");
    }

    private static void ValidateContent(VerifiedAssetPath path)
    {
        // Recorded up front: any full-file hash here — success or a digest mismatch that rejects it —
        // is the expensive ~22MB read the eager/availability split exists to avoid doubling.
        RecordFullContentValidation(path.Asset.Filename);
        using var stream = new FileStream(
            path.Path, FileMode.Open, FileAccess.Read, FileShare.Read, HashBufferSize, FileOptions.SequentialScan);
        string actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        ThrowIfDigestMismatch(path.Asset, actual);
    }

    /// <summary>
    /// Test seam: records the filenames whose <b>full file</b> content was SHA-256 validated, on the
    /// current execution context, so the eager <c>Load</c> vs. cheap availability pre-check contract
    /// can be asserted. Production never installs the list, so <see cref="RecordFullContentValidation"/>
    /// is a no-op outside the tests that read <see cref="FullContentValidatedAssets"/>.
    /// </summary>
    private static readonly AsyncLocal<List<string>?> FullContentValidated = new();

    internal static IReadOnlyList<string> FullContentValidatedAssets =>
        FullContentValidated.Value is { } list ? list : Array.Empty<string>();

    internal static void ResetFullContentValidationSpy() => FullContentValidated.Value = new List<string>();

    internal static void RecordFullContentValidation(string filename)
    {
        if (FullContentValidated.Value is { } list)
            list.Add(filename);
    }

    private static async Task ValidateContentAsync(VerifiedAssetPath path, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(
            path.Path, FileMode.Open, FileAccess.Read, FileShare.Read, HashBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[HashBufferSize];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            hash.AppendData(buffer, 0, read);

        string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        ThrowIfDigestMismatch(path.Asset, actual);
    }

    private static void ThrowIfDigestMismatch(DownloadableAsset asset, string actual)
    {
        if (!string.Equals(actual, asset.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new ModelDownloadException(
                $"Existing {asset.Filename} digest {actual} does not match expected {asset.ExpectedSha256}.");
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void RejectReparsePointIfPresent(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Asset destination contains a reparse point: '{path}'.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static StringComparison StringComparisonForPaths() =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
