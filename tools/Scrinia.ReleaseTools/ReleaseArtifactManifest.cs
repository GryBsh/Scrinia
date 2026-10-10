using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Scrinia.ReleaseTools;

internal sealed record ReleaseArtifact(string Name, long Length, string Sha256, string Kind, string? Rid);
internal sealed record ReleaseArtifactManifest(int Version, string Commit, string PackageVersion,
    string RunId, string SdkVersion, ReleaseArtifact[] Artifacts);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ReleaseArtifactManifest))]
[JsonSerializable(typeof(ReleaseArtifact[]))]
[JsonSerializable(typeof(PublicationProgress))]
[JsonSerializable(typeof(VerifiedNugetPackage))]
internal sealed partial class ReleaseToolsJsonContext : JsonSerializerContext;

internal static partial class ReleaseArtifactVerifier
{
    internal static readonly string[] Rids = ["win-x64", "linux-x64", "osx-arm64"];

    internal static void Verify(string manifestPath, string root, string sha, string version)
    {
        if (!CommitPattern().IsMatch(sha)) throw new ArgumentException("Revision must be a 40-character lower-case SHA.");
        if (new FileInfo(manifestPath).Length > 64 * 1024) throw new InvalidDataException("Release manifest exceeds 64 KiB.");
        using var input = File.OpenRead(manifestPath);
        var manifest = JsonSerializer.Deserialize(input, ReleaseToolsJsonContext.Default.ReleaseArtifactManifest)
            ?? throw new InvalidDataException("Missing release manifest.");
        if (manifest.Version != 1 || manifest.Commit != sha || manifest.PackageVersion != version ||
            string.IsNullOrWhiteSpace(manifest.RunId) || manifest.SdkVersion != "10.0.203")
            throw new InvalidDataException("Release manifest identity or SDK differs from candidate.");
        var artifacts = manifest.Artifacts ?? throw new InvalidDataException("Missing artifact records.");
        if (artifacts.Length != 7 || artifacts.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 7)
            throw new InvalidDataException("Expected seven uniquely named artifacts.");
        if (artifacts.Count(a => a.Kind == "nuget" && a.Rid is null) != 1 ||
            Rids.Any(rid => artifacts.Count(a => a.Kind == "plain-archive" && a.Rid == rid) != 1 ||
                            artifacts.Count(a => a.Kind == "embeddings-archive" && a.Rid == rid) != 1))
            throw new InvalidDataException("Expected one NuGet package and two archives for each supported RID.");
        root = Path.GetFullPath(root);
        foreach (ReleaseArtifact artifact in artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.Name) || artifact.Name is "." or ".." ||
                artifact.Name.IndexOfAny(['/', '\\', ':']) >= 0 || artifact.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                artifact.Length < 0 || !HashPattern().IsMatch(artifact.Sha256) ||
                (artifact.Kind == "nuget" ? !artifact.Name.EndsWith(".nupkg", StringComparison.Ordinal) :
                    !(artifact.Name.EndsWith(".zip", StringComparison.Ordinal) || artifact.Name.EndsWith(".tar.gz", StringComparison.Ordinal))))
                throw new InvalidDataException("Unsafe or invalid artifact record.");
            string path = Path.Combine(root, artifact.Name);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked artifact refused.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != artifact.Length || Convert.ToHexStringLower(SHA256.HashData(stream)) != artifact.Sha256)
                throw new InvalidDataException($"Artifact size or SHA256 mismatch: {artifact.Name}");
        }
        string[] actual = Directory.EnumerateFileSystemEntries(root)
            .Where(path => Path.GetFullPath(path) != Path.GetFullPath(manifestPath)).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(artifacts.Select(a => a.Name).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Artifact directory contains missing or unexpected members.");
        Console.WriteLine($"Manifest identity, artifact set, sizes and SHA-256 hashes match for seven artifacts: commit {sha}, package version {version}.");
    }

    [GeneratedRegex("\\A[0-9a-f]{40}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();
    [GeneratedRegex("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
}
