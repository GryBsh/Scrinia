using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core.Models;

namespace Scrinia.Core.Bundles;

/// <summary>Bundle index containing artifact entries for a single topic.</summary>
public sealed record BundleIndex(List<ArtifactEntry> Entries);

/// <summary>Bundle manifest describing the archive contents.</summary>
public sealed record BundleManifest(
    int Version,
    string Exported,
    List<string> Topics,
    int TotalEntries,
    Dictionary<string, List<string>>? FileEntities = null);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BundleIndex))]
[JsonSerializable(typeof(BundleManifest))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
public partial class BundleJsonContext : JsonSerializerContext;

/// <summary>
/// Shared bundle format I/O — zip creation/extraction, manifest/index serialization.
/// Used by both REST API (BundleService) and MCP layer (MemoryTools).
/// </summary>
public static class BundleFormatService
{
    /// <summary>Disk-based meta-entity categories to include in bundles.</summary>
    private static readonly string[] FileEntityCategories = ["workflows", "skills", "agent"];

    public static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = BundleJsonContext.Default,
    };

    /// <summary>
    /// Write topics to a zip archive. Caller manages the ZipArchive lifecycle.
    /// </summary>
    /// <returns>Tuple of (exported topic names, total entry count).</returns>
    public static (List<string> Topics, int EntryCount) ExportTopicsToZip(
        ZipArchive zip, IMemoryStore store, string[] topics)
    {
        int totalEntries = 0;
        var exportedTopics = new List<string>();

        foreach (string topic in topics)
        {
            string topicScope = MemoryNaming.BuildScopedTopicScope(store.SanitizeName(topic.Trim()));
            var artifacts = store.ListTopicArtifacts(topicScope);
            var entries = store.LoadIndex(topicScope);

            if (entries.Count == 0)
                continue;

            string sanitizedTopic = store.SanitizeName(topic.Trim());
            exportedTopics.Add(sanitizedTopic);

            // Write index.json for this topic
            string indexJson = JsonSerializer.Serialize(new BundleIndex(entries), DefaultJsonOptions);
            var indexEntry = zip.CreateEntry($"topics/{sanitizedTopic}/index.json");
            using (var writer = new StreamWriter(indexEntry.Open()))
                writer.Write(indexJson);

            // Write artifact files
            foreach (var (name, filePath) in artifacts)
            {
                string artifactContent = File.ReadAllText(filePath);
                string entryName = $"topics/{sanitizedTopic}/{store.SanitizeName(name)}.nmp2";
                var zipEntry = zip.CreateEntry(entryName);
                using var writer = new StreamWriter(zipEntry.Open());
                writer.Write(artifactContent);
                totalEntries++;
            }
        }

        // Export disk-based file entities (workflows, skills, agent)
        var fileEntities = ExportFileEntities(zip, store);

        // Write manifest — use v2 when file entities are present
        int version = fileEntities is { Count: > 0 } ? 2 : 1;
        var manifest = new BundleManifest(
            version,
            DateTimeOffset.UtcNow.ToString("o"),
            exportedTopics,
            totalEntries,
            fileEntities is { Count: > 0 } ? fileEntities : null);
        string manifestJson = JsonSerializer.Serialize(manifest, DefaultJsonOptions);
        var manifestEntry = zip.CreateEntry("manifest.json");
        using (var writer = new StreamWriter(manifestEntry.Open()))
            writer.Write(manifestJson);

        return (exportedTopics, totalEntries);
    }

    /// <summary>
    /// Read and import topics from a zip archive into the store.
    /// </summary>
    /// <returns>Tuple of (imported topic count, imported entry count, topic names).</returns>
    /// <exception cref="InvalidOperationException">If bundle is malformed.</exception>
    public static (int TopicCount, int EntryCount, List<string> Names) ImportTopicsFromZip(
        ZipArchive zip, IMemoryStore store, string[]? filterTopics, bool overwrite)
    {
        // Read manifest
        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("Invalid bundle — manifest.json not found.");

        string manifestJson;
        using (var reader = new StreamReader(manifestEntry.Open()))
            manifestJson = reader.ReadToEnd();

        using var manifestDoc = JsonDocument.Parse(manifestJson);
        var root = manifestDoc.RootElement;

        if (!root.TryGetProperty("topics", out var topicsElement))
            throw new InvalidOperationException("Invalid bundle — no topics in manifest.");

        var availableTopics = new List<string>();
        foreach (var t in topicsElement.EnumerateArray())
        {
            string? topicName = t.GetString();
            if (topicName is not null)
                availableTopics.Add(topicName);
        }

        // Filter topics
        var topicsToImport = filterTopics is { Length: > 0 }
            ? availableTopics.Where(t => filterTopics.Any(f =>
                f.Trim().Equals(t, StringComparison.OrdinalIgnoreCase))).ToList()
            : availableTopics;

        int importedTopics = 0;
        int importedEntries = 0;
        var importedTopicNames = new List<string>();

        foreach (string topic in topicsToImport)
        {
            string topicScope = MemoryNaming.BuildScopedTopicScope(store.SanitizeName(topic));
            string sanitizedTopic = store.SanitizeName(topic);

            var indexZipEntry = zip.GetEntry($"topics/{sanitizedTopic}/index.json");
            if (indexZipEntry is null)
                continue;

            string indexJson;
            using (var reader = new StreamReader(indexZipEntry.Open()))
                indexJson = reader.ReadToEnd();

            using var indexDoc = JsonDocument.Parse(indexJson);
            var indexRoot = indexDoc.RootElement;

            if (!indexRoot.TryGetProperty("entries", out var entriesElement))
                continue;

            var entries = new List<ArtifactEntry>();
            var artifactContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entryEl in entriesElement.EnumerateArray())
            {
                string? name = entryEl.GetProperty("name").GetString();
                if (name is null) continue;

                long originalBytes = entryEl.TryGetProperty("originalBytes", out var ob) ? ob.GetInt64() : 0;
                int chunkCount = entryEl.TryGetProperty("chunkCount", out var cc) ? cc.GetInt32() : 1;
                string description = entryEl.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                string? contentPreview = entryEl.TryGetProperty("contentPreview", out var cp) ? cp.GetString() : null;

                string[]? tags = null;
                if (entryEl.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                    tags = tagsEl.EnumerateArray().Select(t2 => t2.GetString() ?? "").Where(t2 => t2.Length > 0).ToArray();

                DateTimeOffset createdAt = entryEl.TryGetProperty("createdAt", out var ca)
                    && DateTimeOffset.TryParse(ca.GetString(), out var parsedDate)
                    ? parsedDate
                    : DateTimeOffset.UtcNow;

                ChunkEntry[]? chunkEntries = null;
                if (entryEl.TryGetProperty("chunkEntries", out var ceEl) && ceEl.ValueKind == JsonValueKind.Array)
                {
                    var ceList = new List<ChunkEntry>();
                    foreach (var ce in ceEl.EnumerateArray())
                    {
                        int ci = ce.TryGetProperty("chunkIndex", out var ciEl) ? ciEl.GetInt32() : 0;
                        string? cePrev = ce.TryGetProperty("contentPreview", out var cpEl) ? cpEl.GetString() : null;
                        string[]? ceKw = null;
                        if (ce.TryGetProperty("keywords", out var kwEl) && kwEl.ValueKind == JsonValueKind.Array)
                            ceKw = kwEl.EnumerateArray().Select(k => k.GetString() ?? "").Where(k => k.Length > 0).ToArray();
                        Dictionary<string, int>? ceTf = null;
                        if (ce.TryGetProperty("termFrequencies", out var tfEl) && tfEl.ValueKind == JsonValueKind.Object)
                        {
                            ceTf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                            foreach (var prop in tfEl.EnumerateObject())
                                if (prop.Value.TryGetInt32(out int v)) ceTf[prop.Name] = v;
                        }
                        ceList.Add(new ChunkEntry(ci, cePrev, ceKw, ceTf));
                    }
                    if (ceList.Count > 0) chunkEntries = ceList.ToArray();
                }

                entries.Add(new ArtifactEntry(
                    Name: name,
                    Uri: "",
                    OriginalBytes: originalBytes,
                    ChunkCount: chunkCount,
                    CreatedAt: createdAt,
                    Description: description,
                    Tags: tags,
                    ContentPreview: contentPreview,
                    ChunkEntries: chunkEntries));

                string artifactEntryName = $"topics/{sanitizedTopic}/{store.SanitizeName(name)}.nmp2";
                var artifactZipEntry = zip.GetEntry(artifactEntryName);
                if (artifactZipEntry is not null)
                {
                    using var reader = new StreamReader(artifactZipEntry.Open());
                    artifactContents[name] = reader.ReadToEnd();
                }
            }

            if (entries.Count > 0)
            {
                store.ImportTopicEntries(topicScope, entries, artifactContents, overwrite);
                importedTopics++;
                importedEntries += entries.Count;
                importedTopicNames.Add(topic);
            }
        }

        // Import disk-based file entities (v2 bundles; v1 bundles skip this)
        ImportFileEntities(zip, root, store);

        return (importedTopics, importedEntries, importedTopicNames);
    }

    // ── File entity helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Derives the .scrinia base directory from the store's scope directory.
    /// <c>store.GetStoreDirForScope("local")</c> returns <c>{workspace}/.scrinia/store</c>,
    /// so the parent is the .scrinia root.
    /// </summary>
    private static string GetScriniaBaseDir(IMemoryStore store) =>
        Path.GetDirectoryName(store.GetStoreDirForScope("local"))!;

    /// <summary>
    /// Scans .scrinia/{category}/ directories for exportable files and writes them
    /// into the zip under <c>files/{category}/{filename}</c>.
    /// Skips the <c>versions/</c> subdirectory within each category.
    /// </summary>
    private static Dictionary<string, List<string>> ExportFileEntities(
        ZipArchive zip, IMemoryStore store)
    {
        var fileEntities = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string baseDir = GetScriniaBaseDir(store);

        foreach (string category in FileEntityCategories)
        {
            string categoryDir = Path.Combine(baseDir, category);
            if (!Directory.Exists(categoryDir))
                continue;

            var files = Directory.EnumerateFiles(categoryDir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f =>
                {
                    string ext = Path.GetExtension(f);
                    return ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".md", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0)
                continue;

            var fileList = new List<string>();
            foreach (string filePath in files)
            {
                string fileName = Path.GetFileName(filePath);
                string zipEntryName = $"files/{category}/{fileName}";

                var zipEntry = zip.CreateEntry(zipEntryName);
                using var outStream = zipEntry.Open();
                using var inStream = File.OpenRead(filePath);
                inStream.CopyTo(outStream);

                fileList.Add(fileName);
            }

            if (fileList.Count > 0)
                fileEntities[category] = fileList;
        }

        return fileEntities;
    }

    /// <summary>
    /// Imports disk-based file entities from a v2 bundle.
    /// If the manifest has no <c>fileEntities</c> property (v1 bundles), this is a no-op.
    /// </summary>
    private static void ImportFileEntities(
        ZipArchive zip, JsonElement manifestRoot, IMemoryStore store)
    {
        if (!manifestRoot.TryGetProperty("fileEntities", out var fileEntitiesEl)
            || fileEntitiesEl.ValueKind != JsonValueKind.Object)
            return;

        string baseDir = GetScriniaBaseDir(store);

        foreach (var categoryProp in fileEntitiesEl.EnumerateObject())
        {
            string category = categoryProp.Name;
            if (categoryProp.Value.ValueKind != JsonValueKind.Array)
                continue;

            string categoryDir = Path.Combine(baseDir, category);
            Directory.CreateDirectory(categoryDir);

            foreach (var fileEl in categoryProp.Value.EnumerateArray())
            {
                string? fileName = fileEl.GetString();
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                // Guard against path traversal
                if (fileName.Contains("..") || Path.IsPathRooted(fileName))
                    continue;

                string zipEntryName = $"files/{category}/{fileName}";
                var zipEntry = zip.GetEntry(zipEntryName);
                if (zipEntry is null)
                    continue;

                string destPath = Path.Combine(categoryDir, fileName);
                string fullDest = Path.GetFullPath(destPath);
                string fullCategory = Path.GetFullPath(categoryDir);
                if (!fullDest.StartsWith(fullCategory, StringComparison.OrdinalIgnoreCase))
                    continue;

                using var inStream = zipEntry.Open();
                using var outStream = File.Create(destPath);
                inStream.CopyTo(outStream);
            }
        }
    }
}
