using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Scrinia.Commands;

internal sealed record EmbeddingsBundleFile(string Name, long Length, string Sha256);
internal sealed record EmbeddingsBundleManifest(int Version, string Plugin,
    string PluginVersion, string Rid, EmbeddingsBundleFile[] Files);

// A bounded observation only. Installation must recheck identity and bytes using stable handles.
internal sealed record ValidatedEmbeddingsBundle(string Root, string PayloadDirectory,
    EmbeddingsBundleManifest Manifest);

internal enum EmbeddingsInstallStatus
{
    Success, InvalidBundle, IncompatibleIdentity, SelectionConflict,
    ExistingContentConflict, InstallationFailed, InstalledConfigurationFailed,
    InstalledModelPreparationFailed
}

internal sealed record EmbeddingsInstallResult(EmbeddingsInstallStatus Status,
    string? Destination, string Message, bool AlreadyInstalled = false);

internal sealed class EmbeddingsBundleException(EmbeddingsInstallStatus status, string message, Exception? inner = null)
    : Exception(message, inner)
{
    internal EmbeddingsInstallStatus Status { get; } = status;
}

internal static class EmbeddingsBundleValidator
{
    internal const int ManifestLimit = 64 * 1024;
    internal const int FileCountLimit = 32;
    internal const long FileLimit = 512L * 1024 * 1024;
    internal const long TotalLimit = 1024L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static ValidatedEmbeddingsBundle Validate(string root, string hostVersion, string rid) =>
        Validate(root, hostVersion, rid, File.GetAttributes,
            path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));

    internal static ValidatedEmbeddingsBundle Validate(string root, string hostVersion, string rid, CancellationToken ct) =>
        Validate(root, hostVersion, rid, File.GetAttributes,
            path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), ct);

    // Call-local seams make denial and nonseekable/growing streams reproducible without mutable global hooks.
    internal static ValidatedEmbeddingsBundle Validate(string root, string hostVersion, string rid,
        Func<string, FileAttributes> inspect, Func<string, Stream> openRead, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            string canonicalRoot = CanonicalRoot(root, inspect);
            string manifestPath = Path.Combine(canonicalRoot, "embeddings-bundle.json");
            InspectDescendant(canonicalRoot, manifestPath, directory: false, inspect);
            EmbeddingsBundleManifest manifest;
            ct.ThrowIfCancellationRequested();
            using (Stream input = openRead(manifestPath)) manifest = ReadManifest(input, ct);
            string version = hostVersion.Split('+')[0];
            if (manifest.Plugin != "scri-plugin-embeddings" || manifest.PluginVersion != version ||
                !StableVersion(version) || manifest.Rid != rid)
                throw new EmbeddingsBundleException(EmbeddingsInstallStatus.IncompatibleIdentity, "Bundle plugin, version or RID does not match this host.");
            IReadOnlyList<string> approved = EmbeddingsBundlePolicy.GetFileNames(rid);
            if (!manifest.Files.Select(file => file.Name).Order(StringComparer.Ordinal).SequenceEqual(approved.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw Invalid("Bundle records do not match the embedded payload policy.");
            string payload = Path.Combine(canonicalRoot, "plugins", manifest.Plugin, version, rid);
            InspectDescendant(canonicalRoot, payload, directory: true, inspect);
            var inventory = new HashSet<string>(StringComparer.Ordinal);
            foreach (string entry in Directory.EnumerateFileSystemEntries(payload))
            {
                ct.ThrowIfCancellationRequested();
                if (inventory.Count >= FileCountLimit) throw Invalid("Payload inventory exceeds 32 files.");
                InspectDescendant(canonicalRoot, entry, directory: false, inspect);
                inventory.Add(Path.GetFileName(entry));
            }
            if (!inventory.SetEquals(approved)) throw Invalid("Payload inventory has missing or extra runtime members.");
            long total = 0;
            foreach (EmbeddingsBundleFile file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                string path = Path.Combine(payload, file.Name);
                InspectDescendant(canonicalRoot, path, directory: false, inspect);
                using Stream input = openRead(path);
                if (input.CanSeek && input.Length != file.Length) throw Invalid("Payload length does not match its record.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[128 * 1024];
                long length = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int read = input.Read(buffer, 0, buffer.Length);
                    ct.ThrowIfCancellationRequested();
                    if (read == 0) break;
                    length = checked(length + read);
                    total = checked(total + read);
                    if (length > file.Length || length > FileLimit || total > TotalLimit)
                        throw Invalid("Streamed payload exceeds its declared bounds.");
                    hash.AppendData(buffer, 0, read);
                }
                if (length != file.Length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw Invalid("Payload length or SHA-256 does not match its record.");
            }
            // Hashes detect mismatches; they do not establish publisher authenticity.
            return new(canonicalRoot, payload, manifest);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
        {
            throw Invalid("Bundle source could not be safely inspected or read.", error);
        }
    }

    internal static EmbeddingsBundleManifest ReadManifest(Stream input, CancellationToken ct = default)
    {
        try
        {
            byte[] bytes = new byte[ManifestLimit + 1];
            int count = 0;
            while (count < bytes.Length)
            {
                ct.ThrowIfCancellationRequested();
                int read = input.Read(bytes, count, bytes.Length - count);
                ct.ThrowIfCancellationRequested();
                if (read == 0) break;
                count += read;
            }
            if (count > ManifestLimit) throw Invalid("Manifest exceeds 64 KiB.");
            // JsonDocument can otherwise replace malformed UTF-8 in string values.
            _ = StrictUtf8.GetCharCount(bytes.AsSpan(0, count));
            using JsonDocument document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            RequireProperties(root, ["version", "plugin", "pluginVersion", "rid", "files"]);
            if (root.GetProperty("version").GetInt32() != 1) throw Invalid("Unsupported manifest version.");
            RequireString(root.GetProperty("plugin"));
            RequireString(root.GetProperty("pluginVersion"));
            RequireString(root.GetProperty("rid"));
            JsonElement files = root.GetProperty("files");
            if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() is 0 or > FileCountLimit)
                throw Invalid("Manifest requires between 1 and 32 records.");
            long total = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement file in files.EnumerateArray())
            {
                RequireProperties(file, ["name", "length", "sha256"]);
                string name = RequireString(file.GetProperty("name"));
                if (!SafeLeaf(name) || !names.Add(name)) throw Invalid("Unsafe or case-colliding payload name.");
                long length = file.GetProperty("length").GetInt64();
                if (length < 0 || length > FileLimit) throw Invalid("Payload declaration exceeds 512 MiB or is negative.");
                total = checked(total + length);
                if (total > TotalLimit) throw Invalid("Payload declarations exceed 1 GiB.");
                string sha = RequireString(file.GetProperty("sha256"));
                if (sha.Length != 64 || sha.Any(character => !Uri.IsHexDigit(character))) throw Invalid("Malformed SHA-256.");
            }
            return JsonSerializer.Deserialize(bytes.AsSpan(0, count), CliJsonContext.Default.EmbeddingsBundleManifest)
                ?? throw Invalid("Manifest is missing.");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or DecoderFallbackException)
        {
            throw Invalid("Malformed embeddings manifest.", error);
        }
    }

    private static void RequireProperties(JsonElement element, string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid("Expected a manifest object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!required.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw Invalid("Unknown or duplicate manifest property.");
        if (seen.Count != required.Length) throw Invalid("Required manifest property is missing.");
    }

    private static string RequireString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(element.GetString())
            ? element.GetString()! : throw Invalid("Expected a nonempty manifest string.");

    private static bool SafeLeaf(string name) => name is not ("." or "..") &&
        !Path.IsPathRooted(name) && name.IndexOfAny(['/', '\\', ':']) < 0 &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Any(char.IsControl) &&
        !name.EndsWith('.') && !name.EndsWith(' ');

    private static bool StableVersion(string version)
    {
        string[] parts = version.Split('.');
        return parts.Length == 3 && parts.All(part => part.Length != 0 && (part.Length == 1 || part[0] != '0') && part.All(char.IsAsciiDigit));
    }

    private static string CanonicalRoot(string root, Func<string, FileAttributes> inspect)
    {
        if (string.IsNullOrWhiteSpace(root) || root.Contains("://", StringComparison.Ordinal)) throw Invalid("Source must be a directory path.");
        string full = Path.GetFullPath(root);
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes = inspect(current);
            if ((attributes & FileAttributes.Directory) == 0) throw Invalid("Source parent must be a directory.");
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                current = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw Invalid("Source root alias could not be resolved.");
        }
        if ((inspect(current) & FileAttributes.Directory) == 0) throw Invalid("Source must be a directory.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static void InspectDescendant(string root, string path, bool directory, Func<string, FileAttributes> inspect)
    {
        string relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw Invalid("Payload path escaped source root.");
        string current = root;
        string[] parts = relative.Split(Path.DirectorySeparatorChar);
        for (int index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            FileAttributes attributes = inspect(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw Invalid("Source descendant links are not supported.");
            bool expectedDirectory = index < parts.Length - 1 || directory;
            if (((attributes & FileAttributes.Directory) != 0) != expectedDirectory || (attributes & FileAttributes.Device) != 0)
                throw Invalid("Source component has an unexpected file identity.");
            if (!expectedDirectory) Scrinia.Core.RecoveryFileType.RequireRegularFile(current);
        }
    }

    private static EmbeddingsBundleException Invalid(string message, Exception? inner = null) =>
        new(EmbeddingsInstallStatus.InvalidBundle, message, inner);
}
