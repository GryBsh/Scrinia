using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scrinia.ReleaseTools;

/// <summary>Build-only publication of the explicitly selected package payload.</summary>
public static partial class EmbeddingsBundleStager
{
    private const string Plugin = "scri-plugin-embeddings";
    private const int ManifestLimit = 64 * 1024;
    private const int FileCountLimit = 32;
    private const long FileLimit = 512L * 1024 * 1024;
    private const long TotalLimit = 1024L * 1024 * 1024;

    /// <summary>Stages and verifies the exact policy payload, publishing the manifest last.</summary>
    public static void Stage(string raw, string root, string rid, string version, string nugetRoot, string policyPath)
    {
        raw = ExistingDirectory(raw);
        root = ExistingDirectory(root);
        nugetRoot = ExistingDirectory(nugetRoot);
        string manifestPath = Path.Combine(root, "embeddings-bundle.json");
        RejectLinks(manifestPath);
        // A failed attempt must never leave a root manifest claiming this attempt completed.
        File.Delete(manifestPath);
        if (rid is not ("win-x64" or "linux-x64" or "osx-arm64"))
            throw new ArgumentException($"Unsupported RID: {rid}");
        if (!VersionPattern().IsMatch(version)) throw new ArgumentException("Version must be a single semantic version.");
        version = version.Split('+')[0];
        RejectLinks(policyPath);
        using var policy = JsonDocument.Parse(File.ReadAllBytes(policyPath));
        JsonElement entry = policy.RootElement.GetProperty(rid);
        string executable = FileName(entry.GetProperty("executable").GetString());
        string expectedExecutable = Plugin + (rid == "win-x64" ? ".exe" : string.Empty);
        if (executable != expectedExecutable) throw new InvalidOperationException("Policy executable does not match plugin identity.");
        var sources = new List<Source> { new(executable, Path.Combine(raw, executable)) };
        foreach (JsonElement native in entry.GetProperty("nativeFiles").EnumerateArray())
        {
            string name = FileName(native.GetProperty("name").GetString());
            string package = FileName(native.GetProperty("package").GetString());
            string packageVersion = FileName(native.GetProperty("packageVersion").GetString());
            string relative = native.GetProperty("packageRelativePath").GetString()!;
            if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || Path.IsPathRooted(relative) ||
                relative.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidOperationException($"Unsafe package-relative path: {relative}");
            if (Path.GetFileName(relative) != name) throw new InvalidOperationException("Package path must end in its declared filename.");
            sources.Add(new Source(name, Path.Combine(nugetRoot, package, packageVersion, relative)));
        }
        if (sources.Count > FileCountLimit || sources.Select(source => source.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Count)
            throw new InvalidOperationException("Policy exceeds file count limit or contains duplicate names.");
        sources.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        long total = 0;
        foreach (Source source in sources)
        {
            RejectLinks(source.Path);
            var info = new FileInfo(source.Path);
            if (!info.Exists) throw new FileNotFoundException($"Approved payload source is missing: {source.Path}", source.Path);
            if (info.Length > FileLimit) throw new InvalidOperationException($"Payload file exceeds 512 MiB: {source.Name}");
            total = checked(total + info.Length);
            if (total > TotalLimit) throw new InvalidOperationException("Payload exceeds 1 GiB.");
        }
        string payload = Path.Combine(root, "plugins", Plugin, version, rid);
        RejectLinks(payload);
        string stage = Path.Combine(root, $".embeddings-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        try
        {
            var files = new List<PayloadFile>();
            long streamedTotal = 0;
            foreach (Source source in sources)
            {
                string destination = Path.Combine(stage, source.Name);
                PayloadFile file = CopyAndHash(source, destination);
                streamedTotal = checked(streamedTotal + file.Length);
                if (streamedTotal > TotalLimit) throw new InvalidOperationException("Payload exceeds 1 GiB.");
                VerifyFile(destination, file);
                files.Add(file);
            }
            VerifyInventory(stage, files);
            byte[] manifest = WriteManifest(version, rid, files);
            if (manifest.Length > ManifestLimit) throw new InvalidOperationException("Manifest exceeds 64 KiB.");
            if (Directory.Exists(payload))
            {
                VerifyInventory(payload, files);
                foreach (PayloadFile file in files) VerifyFile(Path.Combine(payload, file.Name), file);
                // The redundant copy must be gone before the completeness manifest is published.
                Directory.Delete(stage, recursive: true);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
                RejectLinks(payload);
                Directory.Move(stage, payload);
            }
            VerifyInventory(payload, files);
            foreach (PayloadFile file in files) VerifyFile(Path.Combine(payload, file.Name), file);
            string pendingManifest = Path.Combine(root, $".embeddings-manifest-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(pendingManifest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(manifest);
                    stream.Flush(flushToDisk: true);
                }
                RejectLinks(manifestPath);
                File.Move(pendingManifest, manifestPath);
            }
            finally { File.Delete(pendingManifest); }
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    private static PayloadFile CopyAndHash(Source source, string destination)
    {
        using var input = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long length = 0;
        int read;
        while ((read = input.Read(buffer)) != 0)
        {
            length = checked(length + read);
            if (length > FileLimit) throw new InvalidOperationException($"Payload file exceeds 512 MiB: {source.Name}");
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }
        output.Flush(flushToDisk: true);
        UnixFileMode? mode = null;
        if (!OperatingSystem.IsWindows())
        {
            mode = File.GetUnixFileMode(source.Path);
            File.SetUnixFileMode(destination, mode.Value);
        }
        return new PayloadFile(source.Name, length, Convert.ToHexStringLower(hash.GetHashAndReset()), mode);
    }

    private static void VerifyFile(string path, PayloadFile expected)
    {
        RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != expected.Length || Convert.ToHexStringLower(SHA256.HashData(stream)) != expected.Sha256)
            throw new InvalidOperationException($"Payload conflict or failed hash verification: {path}");
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != expected.Mode)
            throw new InvalidOperationException($"Payload Unix mode conflict: {path}");
    }

    private static void VerifyInventory(string directory, List<PayloadFile> files)
    {
        RejectLinks(directory);
        string[] entries = Directory.GetFileSystemEntries(directory);
        foreach (string entry in entries)
        {
            RejectLinks(entry);
            if (Directory.Exists(entry)) throw new InvalidOperationException($"Unexpected payload directory: {entry}");
        }
        if (!entries.Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(files.Select(file => file.Name), StringComparer.Ordinal))
            throw new InvalidOperationException($"Payload inventory conflict: {directory}");
    }

    private static byte[] WriteManifest(string version, string rid, List<PayloadFile> files)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("plugin", Plugin);
            writer.WriteString("pluginVersion", version);
            writer.WriteString("rid", rid);
            writer.WriteStartArray("files");
            foreach (PayloadFile file in files)
            {
                writer.WriteStartObject();
                writer.WriteString("name", file.Name);
                writer.WriteNumber("length", file.Length);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static string ExistingDirectory(string path)
    {
        path = Path.GetFullPath(path);
        RejectLinks(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        return path;
    }

    private static string FileName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0 ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException($"Unsafe policy filename: {name}");
        return name;
    }

    private static void RejectLinks(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Unexpected symbolic link or reparse point: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    private sealed record Source(string Name, string Path);
    private sealed record PayloadFile(string Name, long Length, string Sha256, UnixFileMode? Mode);
}
