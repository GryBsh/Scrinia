using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

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
        ZipArchive zip, ILocalMemoryStore store, string[] topics)
    {
        int totalEntries = 0;
        var exportedTopics = new List<string>();

        foreach (string topic in topics)
        {
            // Task 13: the ONE scope constructor sanitizes each segment itself, so the separate
            // SanitizeName call this used to wrap is gone. That pairing sanitized without
            // de-aliasing — "topic." survived it intact and became a second scope string over the
            // "topic" directory.
            string topicScope = MemoryNaming.BuildScopedTopicScope(topic.Trim());
            var artifacts = store.ListTopicArtifacts(topicScope);
            var entries = store.LoadIndex(topicScope);

            if (entries.Count == 0)
                continue;

            string sanitizedTopic = store.SanitizeName(topic.Trim());
            exportedTopics.Add(sanitizedTopic);

            // Write index.json for this topic
            // ToList() because BundleIndex is a serialization DTO declared over List<T>; LoadIndex now
            // returns a live view of the index cache and must not be handed to a type that could retain it.
            string indexJson = JsonSerializer.Serialize(new BundleIndex(entries.ToList()), BundleJsonContext.Default.BundleIndex);
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

        // Export disk-based file entities (skills, agent)
        var fileEntities = ExportFileEntities(zip, store);

        // Write manifest — use v2 when file entities are present
        int version = fileEntities is { Count: > 0 } ? 2 : 1;
        var manifest = new BundleManifest(
            version,
            DateTimeOffset.UtcNow.ToString("o"),
            exportedTopics,
            totalEntries,
            fileEntities is { Count: > 0 } ? fileEntities : null);
        string manifestJson = JsonSerializer.Serialize(manifest, BundleJsonContext.Default.BundleManifest);
        var manifestEntry = zip.CreateEntry("manifest.json");
        using (var writer = new StreamWriter(manifestEntry.Open()))
            writer.Write(manifestJson);

        return (exportedTopics, totalEntries);
    }

    /// <summary>
    /// Read and import topics from a zip archive into the store.
    /// </summary>
    /// <returns>
    /// Tuple of (imported topic count, imported entry count, topic names, skipped file-entity
    /// categories — manifest categories with no <see cref="CategoryToKind"/> entry, reported
    /// rather than silently dropped).
    /// </returns>
    /// <exception cref="InvalidOperationException">If bundle is malformed.</exception>
    public static (int TopicCount, int EntryCount, List<string> Names, List<string> SkippedCategories) ImportTopicsFromZip(
        ZipArchive zip, ILocalMemoryStore store, string[]? filterTopics, bool overwrite)
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
            // Task 13: import is a WRITE path, so an unconstructable topic throws rather than
            // resolving to an aliased scope — see BundleFormatService's export counterpart.
            string topicScope = MemoryNaming.BuildScopedTopicScope(topic);
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
                // "description" is a back-compat read: pre-Merge-3 bundles carry it as the
                // display carrier. Modern bundles carry "contentPreview" instead — prefer that
                // when present, falling back to "description" for old bundles.
                string description = entryEl.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                string? contentPreview = entryEl.TryGetProperty("contentPreview", out var cp) ? cp.GetString() : null;
                contentPreview ??= description.Length > 0 ? description : null;

                // "tags" is a back-compat read: pre-Merge-3 bundles carry free-form tags as a
                // top-level array. Modern bundles carry "attributes" (a key → string[] dict,
                // including the "tag" key) instead. Read both; the modern "attributes" reading
                // wins if it already has a "tag" key, otherwise the legacy top-level "tags"
                // fills Attributes[KnownAttributes.TagKey] so neither source is lost.
                string[]? legacyTags = null;
                if (entryEl.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                    legacyTags = tagsEl.EnumerateArray().Select(t2 => t2.GetString() ?? "").Where(t2 => t2.Length > 0).ToArray();

                Dictionary<string, string[]>? attributes = null;
                if (entryEl.TryGetProperty("attributes", out var attrEl) && attrEl.ValueKind == JsonValueKind.Object)
                {
                    attributes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in attrEl.EnumerateObject())
                    {
                        if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                        string[] vals = prop.Value.EnumerateArray().Select(v => v.GetString() ?? "")
                            .Where(v => v.Length > 0).ToArray();
                        if (vals.Length > 0) attributes[prop.Name] = vals;
                    }
                }

                if (legacyTags is { Length: > 0 } && (attributes is null || !attributes.ContainsKey(KnownAttributes.TagKey)))
                {
                    attributes ??= new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                    attributes[KnownAttributes.TagKey] = legacyTags;
                }

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
                    ContentPreview: contentPreview,
                    ChunkEntries: chunkEntries,
                    Attributes: attributes));

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
        var skippedCategories = ImportFileEntities(zip, root, store);

        return (importedTopics, importedEntries, importedTopicNames, skippedCategories);
    }

    // ── File entity helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Bundle manifest category key -> PathRouter file-entity kind. The manifest has used the
    /// plural "skills" since v2 bundles shipped; changing it would break existing bundles.
    /// </summary>
    private static readonly Dictionary<string, string> CategoryToKind = new(StringComparer.OrdinalIgnoreCase)
    {
        ["skills"] = "skill",
        ["agent"] = "agent",
    };

    /// <summary>
    /// Resolves a file-entity category's directory, or null when the category has no
    /// file-entity kind. Every kind resolves the same way now — the agent special-case is
    /// gone because agent no longer lives inside the memory store.
    /// </summary>
    private static string? CategoryDir(ILocalMemoryStore store, string category) =>
        CategoryToKind.TryGetValue(category, out string? kind)
            ? PathRouter.ToFileEntityDir(kind, store.WorkspaceRoot)
            : null;

    /// <summary>
    /// Scans each file-entity category's directory for exportable files and writes them
    /// into the zip under <c>files/{category}/{filename}</c>.
    /// Skips the <c>versions/</c> subdirectory within each category.
    /// </summary>
    private static Dictionary<string, List<string>> ExportFileEntities(
        ZipArchive zip, ILocalMemoryStore store)
    {
        var fileEntities = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (string category in CategoryToKind.Keys)
        {
            string? categoryDir = CategoryDir(store, category);
            if (categoryDir is null || !Directory.Exists(categoryDir))
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
    /// <returns>
    /// Manifest category keys that were skipped because <see cref="CategoryToKind"/> has no
    /// entry for them (deleted categories like the former "workflows", or unrecognised/crafted
    /// keys) — reported so a bundle never silently loses content on import.
    /// </returns>
    private static List<string> ImportFileEntities(
        ZipArchive zip, JsonElement manifestRoot, ILocalMemoryStore store)
    {
        var skippedCategories = new List<string>();

        if (!manifestRoot.TryGetProperty("fileEntities", out var fileEntitiesEl)
            || fileEntitiesEl.ValueKind != JsonValueKind.Object)
            return skippedCategories;

        foreach (var categoryProp in fileEntitiesEl.EnumerateObject())
        {
            string category = categoryProp.Name;

            // CategoryToKind is the single allowlist: it doubles as both "is this category
            // recognised" and "where does it live on disk". A category with no kind (deleted,
            // like "workflows", or an unrecognised/crafted key) is reported, never written.
            if (!CategoryToKind.ContainsKey(category))
            {
                skippedCategories.Add(category);
                continue; // deleted or unknown category — reported, never written
            }

            if (categoryProp.Value.ValueKind != JsonValueKind.Array)
                continue;

            // CategoryToKind.ContainsKey(category) already returned true above, so CategoryDir
            // always resolves here — the "no kind" branch that used to guard this is unreachable.
            string categoryDir = CategoryDir(store, category)!;
            Directory.CreateDirectory(categoryDir);

            foreach (var fileEl in categoryProp.Value.EnumerateArray())
            {
                string? fileName = fileEl.GetString();
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                // Guard against path traversal and directory nesting: a crafted manifest entry
                // like "sub/x.md" would pass both the ".." and rooted-path checks, then
                // File.Create(destPath) would throw DirectoryNotFoundException for a
                // subdirectory that doesn't exist — uncaught, aborting the import after topics
                // were already written. Export only ever emits Path.GetFileName(filePath), so a
                // legitimate manifest entry never contains a separator.
                if (fileName.Contains("..") || Path.IsPathRooted(fileName)
                    || fileName.IndexOfAny(['/', '\\']) >= 0)
                    continue;

                string zipEntryName = $"files/{category}/{fileName}";
                var zipEntry = zip.GetEntry(zipEntryName);
                if (zipEntry is null)
                    continue;

                string destPath = Path.Combine(categoryDir, fileName);
                string fullDest = Path.GetFullPath(destPath);
                // Anchored to categoryDir (not the outer .scrinia base): stricter against
                // traversal, but permits a symlinked .scrinia/agent pointing outside .scrinia,
                // where an outer-anchored check would deny it. Deliberate — consistent with the
                // write path (SkillCreate/Remember's agent branch), which never had that outer
                // check either.
                if (!PathContainment.IsWithin(categoryDir, fullDest))
                    continue;

                using var inStream = zipEntry.Open();
                using var outStream = File.Create(destPath);
                inStream.CopyTo(outStream);
            }
        }

        return skippedCategories;
    }
}
