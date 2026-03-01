using System.IO.Compression;
using System.Text.Json;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Server.Services;

/// <summary>
/// Export/import .scrinia-bundle zip archives for topic sharing.
/// </summary>
public static class BundleService
{
    private sealed record BundleIndex(List<ArtifactEntry> Entries);
    private sealed record BundleManifest(int Version, string Exported, List<string> Topics, int TotalEntries);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static MemoryStream ExportToStream(IMemoryStore store, string[] topics)
    {
        var ms = new MemoryStream();
        int totalEntries = 0;
        var exportedTopics = new List<string>();

        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string topic in topics)
            {
                string topicScope = $"local-topic:{store.SanitizeName(topic.Trim())}";
                var artifacts = store.ListTopicArtifacts(topicScope);
                var entries = store.LoadIndex(topicScope);

                if (entries.Count == 0)
                    continue;

                string sanitizedTopic = store.SanitizeName(topic.Trim());
                exportedTopics.Add(sanitizedTopic);

                string indexJson = JsonSerializer.Serialize(new BundleIndex(entries), _jsonOptions);
                var indexEntry = zip.CreateEntry($"topics/{sanitizedTopic}/index.json");
                using (var writer = new StreamWriter(indexEntry.Open()))
                    writer.Write(indexJson);

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

            var manifest = new BundleManifest(1, DateTimeOffset.UtcNow.ToString("o"), exportedTopics, totalEntries);
            string manifestJson = JsonSerializer.Serialize(manifest, _jsonOptions);
            var manifestEntry = zip.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifestEntry.Open()))
                writer.Write(manifestJson);
        }

        ms.Position = 0;
        return ms;
    }

    public static (int TopicCount, int EntryCount, List<string> Names) ImportFromStream(
        IMemoryStore store, Stream bundle, string[]? topics, bool overwrite)
    {
        int importedTopics = 0;
        int importedEntries = 0;
        var importedTopicNames = new List<string>();

        using var zip = new ZipArchive(bundle, ZipArchiveMode.Read);

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

        var topicsToImport = topics is { Length: > 0 }
            ? availableTopics.Where(t => topics.Any(f => f.Trim().Equals(t, StringComparison.OrdinalIgnoreCase))).ToList()
            : availableTopics;

        foreach (string topic in topicsToImport)
        {
            string topicScope = $"local-topic:{store.SanitizeName(topic)}";

            var indexZipEntry = zip.GetEntry($"topics/{topic}/index.json");
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
                    ? DateTimeOffset.Parse(ca.GetString()!)
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

                string artifactEntryName = $"topics/{topic}/{store.SanitizeName(name)}.nmp2";
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

        return (importedTopics, importedEntries, importedTopicNames);
    }
}
