using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Scrinia.ReleaseTools;

internal sealed record VerifiedPackageEntry(string Name, long Length, string Sha256);
internal sealed record VerifiedNugetPackage(string UploadedSha256, string RemoteSha256, VerifiedPackageEntry[] Entries);
internal sealed record PublicationProgress(int Version, string Commit, string PackageVersion, string ValidationRunId,
    string ManifestSha256, long? DraftReleaseId, string[] AssetsVerified, VerifiedNugetPackage? NugetVerified, bool ReleasePublished);

internal static partial class PublicationProgressVerifier
{
    internal static void VerifyValidationArchive(string archive, string manifestPath, string digest)
    {
        if (!HashPattern().IsMatch(digest) || new FileInfo(archive).Length > 2L * 1024 * 1024 * 1024 || Hash(archive) != digest)
            throw new InvalidDataException("Validation artifact archive digest differs from the selected run's immutable service artifact.");
        if (new FileInfo(manifestPath).Length > 64 * 1024) throw new InvalidDataException("Manifest limit exceeded.");
        var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath), ReleaseToolsJsonContext.Default.ReleaseArtifactManifest)
            ?? throw new InvalidDataException("Missing manifest.");
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 100_000 || zip.Entries.Sum(e => e.Length) > 8L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Validation artifact archive entry limits exceeded.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName.TrimEnd('/');
            if (string.IsNullOrEmpty(name) || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl) ||
                name.Split('/').Any(part => part is "" or "." or "..") || !names.Add(name) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || entry.Length > 1024L * 1024 * 1024)
                throw new InvalidDataException("Validation artifact has unsafe, aliased, duplicate or oversized entries.");
        }
        var serverManifest = zip.Entries.SingleOrDefault(e => e.FullName == "artifacts/release-artifacts.json")
            ?? throw new InvalidDataException("Selected run artifact has no unique authoritative manifest.");
        using (var stream = serverManifest.Open())
            if (serverManifest.Length > 64 * 1024 || BoundedHash(stream, serverManifest.Length) != Hash(manifestPath))
                throw new InvalidDataException("Local manifest differs from the selected run artifact's authoritative manifest.");
        string[] expected = manifest.Artifacts.Select(a => "artifacts/" + a.Name).Append("artifacts/release-artifacts.json").Order(StringComparer.Ordinal).ToArray();
        string[] actual = zip.Entries.Where(e => e.FullName.StartsWith("artifacts/", StringComparison.OrdinalIgnoreCase) && !e.FullName.EndsWith('/'))
            .Select(e => e.FullName).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal)) throw new InvalidDataException("Selected run archive's seven payload members differ.");
        foreach (var artifact in manifest.Artifacts)
        {
            var entry = zip.GetEntry("artifacts/" + artifact.Name)!;
            using var stream = entry.Open();
            if (entry.Length != artifact.Length || BoundedHash(stream, entry.Length) != artifact.Sha256)
                throw new InvalidDataException("Selected run artifact payload differs from its authoritative manifest.");
        }
        Console.WriteLine("Selected run's immutable artifact archive, authoritative manifest and seven payload digests verified without extraction.");
    }

    private static string BoundedHash(Stream stream, long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024]; long total = 0;
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            total += count;
            if (total > length) throw new InvalidDataException("Decompressed artifact exceeded its declared bounded length.");
            hash.AppendData(buffer, 0, count);
        }
        if (total != length) throw new InvalidDataException("Decompressed artifact length differs.");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static void Verify(string path, string manifestPath, string sha, string version, string run)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Publication progress exceeds 1 MiB.");
        var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath), ReleaseToolsJsonContext.Default.ReleaseArtifactManifest)
            ?? throw new InvalidDataException("Missing manifest.");
        var progress = JsonSerializer.Deserialize(File.ReadAllBytes(path), ReleaseToolsJsonContext.Default.PublicationProgress)
            ?? throw new InvalidDataException("Missing publication progress.");
        if (progress.Version != 1 || progress.Commit != sha || progress.PackageVersion != version || progress.ValidationRunId != run ||
            progress.ManifestSha256 != Hash(manifestPath) || progress.AssetsVerified is null ||
            progress.AssetsVerified.Distinct(StringComparer.Ordinal).Count() != progress.AssetsVerified.Length ||
            progress.AssetsVerified.Any(name => !manifest.Artifacts.Any(a => a.Name == name)) ||
            progress.DraftReleaseId is <= 0 ||
            (progress.AssetsVerified.Length != 0 && progress.DraftReleaseId is null) ||
            (progress.ReleasePublished && (progress.DraftReleaseId is null || progress.AssetsVerified.Length != 7 || progress.NugetVerified is null)))
            throw new InvalidDataException("Publication progress identity or completed state conflicts with candidate.");
        if (progress.NugetVerified is { } package &&
            (package.UploadedSha256 != manifest.Artifacts.Single(a => a.Kind == "nuget").Sha256 ||
             !HashPattern().IsMatch(package.RemoteSha256) || package.Entries is null || package.Entries.Length == 0 ||
             package.Entries.Any(e => string.IsNullOrEmpty(e.Name) || e.Length < 0 || !HashPattern().IsMatch(e.Sha256)) ||
             package.Entries.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() != package.Entries.Length))
            throw new InvalidDataException("Publication progress package proof is invalid.");
        Console.WriteLine("Publication progress identity verified; remote reconciliation remains required.");
    }

    internal static void ComparePackage(string original, string remote, string output)
    {
        var before = Entries(original, allowAddedSignature: false);
        var after = Entries(remote, allowAddedSignature: true);
        if (!before.SequenceEqual(after)) throw new InvalidDataException("CONFLICT: remote NuGet immutable package entries differ.");
        var proof = new VerifiedNugetPackage(Hash(original), Hash(remote), before);
        File.WriteAllText(output, JsonSerializer.Serialize(proof, ReleaseToolsJsonContext.Default.VerifiedNugetPackage));
    }

    internal static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }

    private static VerifiedPackageEntry[] Entries(string path, bool allowAddedSignature)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count is 0 or > 100_000 || zip.Entries.Sum(e => e.Length) > 1024L * 1024 * 1024)
            throw new InvalidDataException("NuGet package entry limits exceeded.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<VerifiedPackageEntry>();
        foreach (var entry in zip.Entries)
        {
            if (!names.Add(entry.FullName) || entry.FullName.Contains('\\') || entry.FullName.StartsWith('/') ||
                entry.FullName.Split('/').Any(part => part is "." or "..") || entry.Length > 512L * 1024 * 1024)
                throw new InvalidDataException("NuGet package has unsafe, duplicate or oversized entries.");
            if (entry.FullName == ".signature.p7s" && allowAddedSignature) continue;
            using var stream = entry.Open();
            result.Add(new(entry.FullName, entry.Length, Convert.ToHexStringLower(SHA256.HashData(stream))));
        }
        // The uploaded artifact is unsigned: only a newly added root service signature is normalized.
        if (!allowAddedSignature && names.Contains(".signature.p7s"))
            throw new InvalidDataException("Uploaded signed package needs an explicit signature identity policy.");
        var nuspecs = zip.Entries.Where(e => !e.FullName.Contains('/') && e.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).ToArray();
        if (nuspecs.Length != 1) throw new InvalidDataException("NuGet package must have one root nuspec.");
        using var metadata = nuspecs[0].Open();
        var document = XDocument.Load(metadata);
        string? id = document.Descendants().SingleOrDefault(e => e.Name.LocalName == "id")?.Value;
        string? version = document.Descendants().SingleOrDefault(e => e.Name.LocalName == "version")?.Value;
        if (id != "scrinia" || version != "0.6.0") throw new InvalidDataException("NuGet destination identity differs from scrinia 0.6.0.");
        return result.OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
    }

    [GeneratedRegex("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
}
