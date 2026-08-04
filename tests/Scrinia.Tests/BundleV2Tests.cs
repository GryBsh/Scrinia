using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Tests for bundle v2 format — disk-based file entity export/import,
/// v1 backward compatibility, and round-trip fidelity.
/// </summary>
public sealed class BundleV2Tests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspaceRoot;
    private readonly FileMemoryStore _store;

    public BundleV2Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-bundlev2-" + Guid.NewGuid().ToString("N")[..8]);
        _workspaceRoot = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(_workspaceRoot);
        _store = new FileMemoryStore(_workspaceRoot);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the store with a topic entry + artifact so the bundle has something to export.
    /// </summary>
    private void SeedTopic(string topic, string subject, string content)
    {
        string scope = MemoryNaming.BuildScopedTopicScope(_store.SanitizeName(topic));
        var entry = new ArtifactEntry(
            Name: subject,
            Uri: "",
            OriginalBytes: content.Length,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            ContentPreview: $"Test entry for {topic}:{subject}");
        _store.Upsert(entry, scope);
        _store.WriteArtifactAsync(subject, scope, content).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Seeds the store with an entry carrying modern <see cref="ArtifactEntry.ContentPreview"/> +
    /// <see cref="ArtifactEntry.Attributes"/> data (post-migration shape), for round-trip fidelity tests.
    /// </summary>
    private void SeedTopicWithAttributes(
        string topic, string subject, string content, string contentPreview, string[] tags)
    {
        string scope = MemoryNaming.BuildScopedTopicScope(_store.SanitizeName(topic));
        var entry = new ArtifactEntry(
            Name: subject,
            Uri: "",
            OriginalBytes: content.Length,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            ContentPreview: contentPreview,
            Attributes: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [KnownAttributes.TagKey] = tags
            });
        _store.Upsert(entry, scope);
        _store.WriteArtifactAsync(subject, scope, content).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Creates a disk file under a file-entity category directory with the given content.
    /// "agent" and "skills" resolve through <see cref="PathRouter.ToFileEntityDir"/> at the
    /// top level of <c>.scrinia/</c> — "workflows" has no file-entity kind, so files created
    /// under it exist on disk but are never picked up by bundle export (<c>CategoryDir</c>
    /// returns null for it).
    /// </summary>
    private void CreateDiskFile(string category, string fileName, string content)
    {
        string dir = category.Equals("agent", StringComparison.OrdinalIgnoreCase)
            ? PathRouter.ToFileEntityDir("agent", _workspaceRoot)
            : Path.Combine(_workspaceRoot, ".scrinia", category);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    /// <summary>
    /// Exports the store to an in-memory zip and returns the raw bytes.
    /// </summary>
    private byte[] ExportToBytes(string[] topics)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            BundleFormatService.ExportTopicsToZip(zip, _store, topics);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Imports a zip from raw bytes into the given store.
    /// </summary>
    private static (int TopicCount, int EntryCount, List<string> Names) ImportFromBytes(
        byte[] zipBytes, IMemoryStore store, bool overwrite = true)
    {
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        return BundleFormatService.ImportTopicsFromZip(zip, store, filterTopics: null, overwrite);
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void BundleV2Export_IncludesDiskFiles()
    {
        // Arrange — seed a topic so the bundle is non-trivial
        SeedTopic("test", "entry1", "chunk content");

        // Create disk files in all three legacy categories — "workflows" has no file-entity
        // kind and must be silently skipped rather than crash the export.
        CreateDiskFile("workflows", "deploy.json", """{"name":"deploy"}""");
        CreateDiskFile("skills", "researcher.md", "# Researcher\nDoes research.");
        CreateDiskFile("agent", "profile.md", "# Agent profile");

        // Act — export
        byte[] zipBytes = ExportToBytes(["test"]);

        // Assert — inspect the zip
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        // Verify file entries exist under files/{category}/ for kinds PathRouter knows about.
        // "workflows" has no file-entity kind, so CategoryDir returns null and it is skipped.
        zip.GetEntry("files/workflows/deploy.json").Should().BeNull(
            because: "workflows has no PathRouter file-entity kind");
        zip.GetEntry("files/skills/researcher.md").Should().NotBeNull();
        zip.GetEntry("files/agent/profile.md").Should().NotBeNull();

        // Verify manifest is v2 with fileEntities
        var manifestEntry = zip.GetEntry("manifest.json");
        manifestEntry.Should().NotBeNull();

        using var reader = new StreamReader(manifestEntry!.Open());
        string manifestJson = reader.ReadToEnd();
        using var doc = JsonDocument.Parse(manifestJson);
        var root = doc.RootElement;

        root.GetProperty("version").GetInt32().Should().Be(2);
        root.TryGetProperty("fileEntities", out var fe).Should().BeTrue();

        fe.TryGetProperty("workflows", out _).Should().BeFalse(
            because: "workflows is never scanned, so it never gets a manifest entry");
        fe.GetProperty("skills").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("researcher.md");
        fe.GetProperty("agent").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("profile.md");
    }

    [Fact]
    public void BundleV2Import_RestoresDiskFiles()
    {
        // Arrange — build a v2 zip with file entities manually
        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                // Minimal topic so manifest has at least one topic
                var indexJson = JsonSerializer.Serialize(
                    new BundleIndex(new List<ArtifactEntry>
                    {
                        new("note", "", 5, 1, DateTimeOffset.UtcNow, "a note")
                    }),
                    BundleFormatService.DefaultJsonOptions);
                WriteZipText(zip, "topics/test/index.json", indexJson);
                WriteZipText(zip, "topics/test/note.nmp2", "hello");

                // File entities
                WriteZipText(zip, "files/workflows/ci.json", """{"steps":["build","test"]}""");
                WriteZipText(zip, "files/skills/planner.md", "# Planner skill");
                WriteZipText(zip, "files/agent/norms.md", "# Norms\nBe concise.");

                // Manifest
                var manifest = new BundleManifest(
                    Version: 2,
                    Exported: DateTimeOffset.UtcNow.ToString("o"),
                    Topics: ["test"],
                    TotalEntries: 1,
                    FileEntities: new Dictionary<string, List<string>>
                    {
                        ["workflows"] = ["ci.json"],
                        ["skills"] = ["planner.md"],
                        ["agent"] = ["norms.md"]
                    });
                var manifestJson = JsonSerializer.Serialize(manifest, BundleFormatService.DefaultJsonOptions);
                WriteZipText(zip, "manifest.json", manifestJson);
            }
            zipBytes = ms.ToArray();
        }

        // Act — import into a fresh workspace
        string importRoot = Path.Combine(_tempDir, "import-target");
        Directory.CreateDirectory(importRoot);
        using var importStore = new FileMemoryStore(importRoot);

        ImportFromBytes(zipBytes, importStore);

        // Assert — verify files written to correct locations. "agent" and "skills" resolve
        // via PathRouter.ToFileEntityDir at the top level of .scrinia/. "workflows" has no
        // file-entity kind, so CategoryDir returns null and the manifest entry is skipped
        // entirely — nothing is written for it.
        string scriniaBase = Path.Combine(importRoot, ".scrinia");
        string agentDir = PathRouter.ToFileEntityDir("agent", importRoot);
        File.Exists(Path.Combine(scriniaBase, "workflows", "ci.json")).Should().BeFalse(
            because: "workflows has no PathRouter file-entity kind");
        File.Exists(Path.Combine(scriniaBase, "skills", "planner.md")).Should().BeTrue();
        File.Exists(Path.Combine(agentDir, "norms.md")).Should().BeTrue();

        // Verify content fidelity
        File.ReadAllText(Path.Combine(scriniaBase, "skills", "planner.md"))
            .Should().Be("# Planner skill");
        File.ReadAllText(Path.Combine(agentDir, "norms.md"))
            .Should().Be("# Norms\nBe concise.");
    }

    [Fact]
    public void BundleV1Import_NoFileEntities_StillWorks()
    {
        // Arrange — build a v1 bundle with NO fileEntities in manifest
        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var indexJson = JsonSerializer.Serialize(
                    new BundleIndex(new List<ArtifactEntry>
                    {
                        new("readme", "", 12, 1, DateTimeOffset.UtcNow, "a readme")
                    }),
                    BundleFormatService.DefaultJsonOptions);
                WriteZipText(zip, "topics/docs/index.json", indexJson);
                WriteZipText(zip, "topics/docs/readme.nmp2", "docs content");

                // v1 manifest — no FileEntities key at all
                var manifest = new BundleManifest(
                    Version: 1,
                    Exported: DateTimeOffset.UtcNow.ToString("o"),
                    Topics: ["docs"],
                    TotalEntries: 1);
                var manifestJson = JsonSerializer.Serialize(manifest, BundleFormatService.DefaultJsonOptions);
                WriteZipText(zip, "manifest.json", manifestJson);
            }
            zipBytes = ms.ToArray();
        }

        // Act — import into a fresh workspace
        string importRoot = Path.Combine(_tempDir, "v1-import");
        Directory.CreateDirectory(importRoot);
        using var importStore = new FileMemoryStore(importRoot);

        var (topicCount, entryCount, names) = ImportFromBytes(zipBytes, importStore);

        // Assert — NMP/2 topics imported successfully
        topicCount.Should().Be(1);
        entryCount.Should().Be(1);
        names.Should().Contain("docs");

        // Verify no file-entity directories were created
        string scriniaBase = Path.Combine(importRoot, ".scrinia");
        Directory.Exists(Path.Combine(scriniaBase, "workflows")).Should().BeFalse();
        Directory.Exists(Path.Combine(scriniaBase, "skills")).Should().BeFalse();
        // agent/ may exist from FileMemoryStore init, but should have no extra files
        var agentDir = Path.Combine(scriniaBase, "agent");
        if (Directory.Exists(agentDir))
        {
            Directory.GetFiles(agentDir, "*.md").Should().BeEmpty();
            Directory.GetFiles(agentDir, "*.json").Should().BeEmpty();
        }
    }

    [Fact]
    public async Task BundleV2RoundTrip_FilesMatch()
    {
        // Arrange — seed topic data and disk files
        SeedTopic("patterns", "retry", "## Retry pattern\nExponential backoff.");
        SeedTopic("patterns", "circuit-breaker", "## Circuit breaker\nFail fast.");

        CreateDiskFile("workflows", "release.json", """{"name":"release","version":2}""");
        CreateDiskFile("skills", "debugger.md", "# Debugger\nRoot cause analysis.");
        CreateDiskFile("agent", "execution-policy.md", "# Execution policy\nVerify before commit.");

        // Act — export → import into fresh workspace
        byte[] zipBytes = ExportToBytes(["patterns"]);

        string importRoot = Path.Combine(_tempDir, "roundtrip-target");
        Directory.CreateDirectory(importRoot);
        using var importStore = new FileMemoryStore(importRoot);

        var (topicCount, entryCount, _) = ImportFromBytes(zipBytes, importStore);

        // Assert — topic data round-tripped
        topicCount.Should().Be(1);
        entryCount.Should().Be(2);

        // Assert — disk files match originals. "agent" and "skills" resolve via
        // PathRouter.ToFileEntityDir at the top level of .scrinia/. "workflows" has no
        // file-entity kind, so it never round-trips through a bundle.
        string importBase = Path.Combine(importRoot, ".scrinia");
        string importAgentDir = PathRouter.ToFileEntityDir("agent", importRoot);

        File.Exists(Path.Combine(importBase, "workflows", "release.json")).Should().BeFalse(
            because: "workflows has no PathRouter file-entity kind");
        File.ReadAllText(Path.Combine(importBase, "skills", "debugger.md"))
            .Should().Be("# Debugger\nRoot cause analysis.");
        File.ReadAllText(Path.Combine(importAgentDir, "execution-policy.md"))
            .Should().Be("# Execution policy\nVerify before commit.");

        // Verify the imported topic artifact content matches
        string importScope = MemoryNaming.BuildScopedTopicScope(importStore.SanitizeName("patterns"));
        var retryContent = await importStore.ReadArtifactAsync("retry", importScope);
        retryContent.Should().Be("## Retry pattern\nExponential backoff.");

        var cbContent = await importStore.ReadArtifactAsync("circuit-breaker", importScope);
        cbContent.Should().Be("## Circuit breaker\nFail fast.");
    }

    [Fact]
    public void BundleRoundTrip_ContentPreviewAndTags_Survive()
    {
        // Arrange — seed an entry with modern ContentPreview + Attributes["tag"] data.
        SeedTopicWithAttributes(
            "recipes", "soup", "## Soup\nSimmer for an hour.",
            contentPreview: "A hearty soup recipe.",
            tags: ["cooking", "winter"]);

        // Act — export → import into a fresh workspace.
        byte[] zipBytes = ExportToBytes(["recipes"]);

        string importRoot = Path.Combine(_tempDir, "roundtrip-attrs-target");
        Directory.CreateDirectory(importRoot);
        using var importStore = new FileMemoryStore(importRoot);

        ImportFromBytes(zipBytes, importStore);

        // Assert — ContentPreview and Attributes["tag"] survived the round trip.
        string importScope = MemoryNaming.BuildScopedTopicScope(importStore.SanitizeName("recipes"));
        var imported = importStore.LoadIndex(importScope).Single(e => e.Name == "soup");

        imported.ContentPreview.Should().Be("A hearty soup recipe.");
        imported.Attributes.Should().NotBeNull();
        imported.Attributes!.Should().ContainKey(KnownAttributes.TagKey)
            .WhoseValue.Should().BeEquivalentTo(["cooking", "winter"]);
    }

    [Fact]
    public void BundleImport_OldBundleFormat_RoutesDescriptionAndTagsToContentPreviewAndAttributes()
    {
        // Arrange — build a bundle whose index.json mimics a pre-Merge-3 export: only
        // "description"/"tags" keys, no "contentPreview"/"attributes" keys at all.
        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                string indexJson = """
                {
                  "entries": [
                    {
                      "name": "legacy-note",
                      "originalBytes": 5,
                      "chunkCount": 1,
                      "createdAt": "2020-01-01T00:00:00+00:00",
                      "description": "An old-format description",
                      "tags": ["archived", "legacy"]
                    }
                  ]
                }
                """;
                WriteZipText(zip, "topics/old/index.json", indexJson);
                WriteZipText(zip, "topics/old/legacy-note.nmp2", "hello");

                var manifest = new BundleManifest(
                    Version: 1,
                    Exported: DateTimeOffset.UtcNow.ToString("o"),
                    Topics: ["old"],
                    TotalEntries: 1);
                string manifestJson = JsonSerializer.Serialize(manifest, BundleFormatService.DefaultJsonOptions);
                WriteZipText(zip, "manifest.json", manifestJson);
            }
            zipBytes = ms.ToArray();
        }

        // Act — import into a fresh workspace.
        string importRoot = Path.Combine(_tempDir, "old-bundle-import-target");
        Directory.CreateDirectory(importRoot);
        using var importStore = new FileMemoryStore(importRoot);

        ImportFromBytes(zipBytes, importStore);

        // Assert — old "description"/"tags" keys routed into ContentPreview/Attributes["tag"].
        string importScope = MemoryNaming.BuildScopedTopicScope(importStore.SanitizeName("old"));
        var imported = importStore.LoadIndex(importScope).Single(e => e.Name == "legacy-note");

        imported.ContentPreview.Should().Be("An old-format description");
        imported.Attributes.Should().NotBeNull();
        imported.Attributes!.Should().ContainKey(KnownAttributes.TagKey)
            .WhoseValue.Should().BeEquivalentTo(["archived", "legacy"]);
    }

    // ── Zip helper ───────────────────────────────────────────────────────────

    private static void WriteZipText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }
}
