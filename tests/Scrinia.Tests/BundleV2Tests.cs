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
        // Task 13: the constructor sanitizes each segment itself. Wrapping it in SanitizeName
        // would flatten '/' to '_' and seed a DIFFERENT scope than the one export resolves.
        string scope = MemoryNaming.BuildScopedTopicScope(topic);
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
        // Task 13: the constructor sanitizes each segment itself. Wrapping it in SanitizeName
        // would flatten '/' to '_' and seed a DIFFERENT scope than the one export resolves.
        string scope = MemoryNaming.BuildScopedTopicScope(topic);
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
    /// top level of <c>.scrinia/</c>.
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
    private static (int TopicCount, int EntryCount, List<string> Names, List<string> SkippedCategories) ImportFromBytes(
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

        // Create disk files in the two file-entity categories.
        CreateDiskFile("skills", "researcher.md", "# Researcher\nDoes research.");
        CreateDiskFile("agent", "profile.md", "# Agent profile");

        // Act — export
        byte[] zipBytes = ExportToBytes(["test"]);

        // Assert — inspect the zip
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        // Verify file entries exist under files/{category}/ for kinds PathRouter knows about.
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
        // via PathRouter.ToFileEntityDir at the top level of .scrinia/.
        string scriniaBase = Path.Combine(importRoot, ".scrinia");
        string agentDir = PathRouter.ToFileEntityDir("agent", importRoot);
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

        var (topicCount, entryCount, names, skippedCategories) = ImportFromBytes(zipBytes, importStore);

        // Assert — NMP/2 topics imported successfully
        topicCount.Should().Be(1);
        entryCount.Should().Be(1);
        names.Should().Contain("docs");
        skippedCategories.Should().BeEmpty();

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
    public void BundleV2Import_PreservesAcceptedSidecarUnknownFieldsAndRawBytes()
    {
        byte[] expected = [0, 255, 10, 13];
        byte[] zipBytes;
        using (var stream = new MemoryStream())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteZipBytes(zip, "files/skills/planner.md", expected);
                string meta = "{\"v\":1,\"futureField\":{\"kept\":true}}";
                WriteZipText(zip, "files/skills/planner.meta.json", meta);
                WriteZipText(zip, "manifest.json", """
                    {"version":2,"topics":[],"totalEntries":0,
                    "fileEntities":{"skills":["planner.md","planner.meta.json"]}}
                    """);
            }
            zipBytes = stream.ToArray();
        }

        string importRoot = Path.Combine(_tempDir, "byte-fidelity-import");
        Directory.CreateDirectory(importRoot);
        using (var importing = new FileMemoryStore(importRoot))
            ImportFromBytes(zipBytes, importing);

        // A fresh process-equivalent store sees the exact bytes selected from the archive.
        using var cold = new FileMemoryStore(importRoot);
        string skillsDir = Path.Combine(importRoot, ".scrinia", "skills");
        File.ReadAllBytes(Path.Combine(skillsDir, "planner.md")).Should().Equal(expected);
        File.ReadAllBytes(Path.Combine(skillsDir, "planner.meta.json"))
            .Should().Equal(ReadZipMemberBytes(zipBytes, "files/skills/planner.meta.json"));
    }

    [Fact]
    public void BundleV2Import_WhenFileEntityDirectorySetupFails_WrapsFailureAndPreservesExistingBytes()
    {
        string importRoot = Path.Combine(_tempDir, "directory-setup-failure-import");
        Directory.CreateDirectory(importRoot);
        using var importing = new FileMemoryStore(importRoot);
        string skillsDirectory = PathRouter.ToFileEntityDir("skill", importRoot);
        byte[] originalBytes = [0, 255, 7];
        Directory.CreateDirectory(Path.GetDirectoryName(skillsDirectory)!);
        File.WriteAllBytes(skillsDirectory, originalBytes);

        byte[] zipBytes;
        using (var stream = new MemoryStream())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteZipBytes(zip, "files/skills/planner.md", [1, 2, 3]);
                WriteZipText(zip, "manifest.json", """
                    {"version":2,"topics":[],"totalEntries":0,
                    "fileEntities":{"skills":["planner.md"]}}
                    """);
            }
            zipBytes = stream.ToArray();
        }

        Action import = () => ImportFromBytes(zipBytes, importing);

        var failure = import.Should().Throw<DurableWriteException>().Which;

        failure.Operation.Should().Be("create directory");
        failure.Destination.Should().Be(skillsDirectory);
        failure.InnerException.Should().BeOfType<IOException>();
        importing.Dispose();
        using var cold = new FileMemoryStore(importRoot);
        File.ReadAllBytes(skillsDirectory).Should().Equal(originalBytes);
    }

    [Fact]
    public void BundleV2Import_MalformedAgentMetadata_RefusesBeforeMutatingExistingTopicOrFiles()
    {
        string importRoot = Path.Combine(_tempDir, "malformed-agent-import");
        Directory.CreateDirectory(importRoot);
        SentinelBundleState sentinels;
        using (var target = new FileMemoryStore(importRoot))
        {
            sentinels = SeedSentinels(target, "agent");
            Action import = () => ImportFromBytes(CreateFileEntityBundle(
                "agent", "{ not json", includeDeclaredBody: true), target);

            import.Should().Throw<InvalidOperationException>().WithMessage("*agent*planner.meta.json*malformed*");
        }

        AssertSentinelsSurvived(importRoot, sentinels);
    }

    [Fact]
    public void BundleV2Import_ManifestDeclaredMissingSkillMember_RefusesBeforeMutatingExistingTopicOrFiles()
    {
        string importRoot = Path.Combine(_tempDir, "missing-member-import");
        Directory.CreateDirectory(importRoot);
        SentinelBundleState sentinels;
        using (var target = new FileMemoryStore(importRoot))
        {
            sentinels = SeedSentinels(target, "skill");
            Action import = () => ImportFromBytes(CreateFileEntityBundle(
                "skills", "{\"v\":1}", includeDeclaredBody: false), target);

            import.Should().Throw<InvalidOperationException>().WithMessage("*skills*planner.md*missing*");
        }

        AssertSentinelsSurvived(importRoot, sentinels);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"not-an-object\"")]
    public void BundleImport_PresentNonObjectFileEntities_RefusesBeforeMutatingTopicsOrFiles(
        string fileEntitiesJson)
    {
        string importRoot = Path.Combine(_tempDir, "invalid-file-entities-import");
        Directory.CreateDirectory(importRoot);
        SentinelBundleState sentinels;
        using (var target = new FileMemoryStore(importRoot))
        {
            sentinels = SeedSentinels(target, "skill");
            Action import = () => ImportFromBytes(
                CreateBundleWithRawFileEntities(fileEntitiesJson), target);

            import.Should().Throw<InvalidOperationException>()
                .WithMessage("*fileEntities*JSON object*");
        }

        AssertSentinelsSurvived(importRoot, sentinels);
    }

    [Fact]
    public async Task BundleV2RoundTrip_FilesMatch()
    {
        // Arrange — seed topic data and disk files
        SeedTopic("patterns", "retry", "## Retry pattern\nExponential backoff.");
        SeedTopic("patterns", "circuit-breaker", "## Circuit breaker\nFail fast.");

        CreateDiskFile("skills", "debugger.md", "# Debugger\nRoot cause analysis.");
        CreateDiskFile("agent", "execution-policy.md", "# Execution policy\nVerify before commit.");

        // Act — export → import into fresh workspace
        byte[] zipBytes = ExportToBytes(["patterns"]);

        string importRoot = Path.Combine(_tempDir, "roundtrip-target");
        Directory.CreateDirectory(importRoot);
        using var importStore = new FileMemoryStore(importRoot);

        var (topicCount, entryCount, _, _) = ImportFromBytes(zipBytes, importStore);

        // Assert — topic data round-tripped
        topicCount.Should().Be(1);
        entryCount.Should().Be(2);

        // Assert — disk files match originals. "agent" and "skills" resolve via
        // PathRouter.ToFileEntityDir at the top level of .scrinia/.
        string importBase = Path.Combine(importRoot, ".scrinia");
        string importAgentDir = PathRouter.ToFileEntityDir("agent", importRoot);

        File.ReadAllText(Path.Combine(importBase, "skills", "debugger.md"))
            .Should().Be("# Debugger\nRoot cause analysis.");
        File.ReadAllText(Path.Combine(importAgentDir, "execution-policy.md"))
            .Should().Be("# Execution policy\nVerify before commit.");

        // Verify the imported topic artifact content matches
        // Task 13: the constructor sanitizes each segment itself — see SeedTopic. Wrapping it in
        // SanitizeName would flatten '/' to '_' and resolve a DIFFERENT scope than import wrote to.
        string importScope = MemoryNaming.BuildScopedTopicScope("patterns");
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
        // See SeedTopic for why the SanitizeName wrapper is wrong here.
        string importScope = MemoryNaming.BuildScopedTopicScope("recipes");
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
        // See SeedTopic for why the SanitizeName wrapper is wrong here.
        string importScope = MemoryNaming.BuildScopedTopicScope("old");
        var imported = importStore.LoadIndex(importScope).Single(e => e.Name == "legacy-note");

        imported.ContentPreview.Should().Be("An old-format description");
        imported.Attributes.Should().NotBeNull();
        imported.Attributes!.Should().ContainKey(KnownAttributes.TagKey)
            .WhoseValue.Should().BeEquivalentTo(["archived", "legacy"]);
    }

    // ── Task 13: the bundle key is DERIVED from the scope ────────────────────

    /// <summary>
    /// The scope and the zip/manifest key are two transforms of one topic — the scope treats '/'
    /// as a SEPARATOR, <c>SanitizeName</c> replaces it with '_'. Computing them independently
    /// made them disagree for any hierarchical topic, so a bundle exported from
    /// <c>local:goal/G-5</c> re-imported into <c>local:goal_G-5</c>: the memories silently
    /// changed scope. This is the round trip that must be exact.
    /// </summary>
    [Fact]
    public void BundleRoundTrip_HierarchicalTopic_LandsInTheScopeItLeft()
    {
        SeedTopic("goal/G-5", "hierarchical-note", "nested topic content");

        byte[] zipBytes = ExportToBytes(["goal/G-5"]);

        string importRoot = Path.Combine(_tempDir, "hierarchical-import-target");
        Directory.CreateDirectory(importRoot);
        using var importStore = new FileMemoryStore(importRoot);
        var (_, _, names, _) = ImportFromBytes(zipBytes, importStore);

        names.Should().BeEquivalentTo(["goal/G-5"],
            because: "the manifest carries the topic in its scope-derived form, hierarchy intact");

        importStore.LoadIndex("local:goal/G-5").Select(e => e.Name)
            .Should().Contain("hierarchical-note",
                because: "the memory must land in the scope it was exported from");
        importStore.LoadIndex("local:goal_G-5").Should().BeEmpty(
            because: "flattening the separator would silently relocate it to a different scope");
    }

    /// <summary>
    /// Back-compat for bundles written BEFORE the key became scope-derived: their manifest carries
    /// an already-flattened "goal_G-5". That constructs scope <c>local:goal_G-5</c> and formats
    /// back to the same "goal_G-5" zip key they were written with, so they still import exactly as
    /// they used to. Hand-built rather than exported, because the current exporter can no longer
    /// produce this shape.
    /// </summary>
    [Fact]
    public void BundleImport_LegacyFlattenedManifestTopic_StillImportsAsBefore()
    {
        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteZipText(zip, "manifest.json",
                    """{"version":1,"topics":["goal_G-5"],"entryCount":1}""");
                WriteZipText(zip, "topics/goal_G-5/index.json",
                    """{"entries":[{"name":"legacy-note","originalBytes":5,"chunkCount":1}]}""");
                WriteZipText(zip, "topics/goal_G-5/legacy-note.nmp2", "body");
            }
            zipBytes = ms.ToArray();
        }

        string importRoot = Path.Combine(_tempDir, "legacy-flat-import-target");
        Directory.CreateDirectory(importRoot);
        using var importStore = new FileMemoryStore(importRoot);

        ImportFromBytes(zipBytes, importStore);

        importStore.LoadIndex("local:goal_G-5").Select(e => e.Name)
            .Should().Contain("legacy-note",
                because: "a pre-existing flattened bundle must import exactly where it always did");
    }

    /// <summary>
    /// A manifest topic the one scope constructor cannot build is refused BEFORE anything is
    /// written. Validating inside the write loop would abort part-way, leaving the store
    /// half-imported and surfacing a raw ArgumentException that no caller turns into a
    /// caller-facing error.
    /// </summary>
    [Fact]
    public void BundleImport_UnconstructableManifestTopic_IsRefusedBeforeAnythingIsWritten()
    {
        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                // "good" sorts first and would already be written by the time "bad." threw.
                WriteZipText(zip, "manifest.json",
                    """{"version":1,"topics":["good","bad."],"entryCount":2}""");
                WriteZipText(zip, "topics/good/index.json",
                    """{"entries":[{"name":"good-note","originalBytes":5,"chunkCount":1}]}""");
                WriteZipText(zip, "topics/good/good-note.nmp2", "body");
            }
            zipBytes = ms.ToArray();
        }

        string importRoot = Path.Combine(_tempDir, "bad-topic-import-target");
        Directory.CreateDirectory(importRoot);
        using var importStore = new FileMemoryStore(importRoot);

        var act = () => ImportFromBytes(zipBytes, importStore);

        act.Should().Throw<InvalidOperationException>(
            because: "a malformed manifest is the failure this method already documents")
           .WithMessage("*bad.*");

        importStore.LoadIndex("local:good").Should().BeEmpty(
            because: "the refusal must land before ANY topic is written, not part-way through");
    }

    /// <summary>Export refuses an unconstructable topic outright, so our own exporter can never
    /// write a manifest that import would then have to refuse.</summary>
    [Fact]
    public void BundleExport_UnconstructableTopic_IsRefused()
    {
        var act = () => ExportToBytes(["topic."]);

        act.Should().Throw<ArgumentException>().WithMessage("*must not end with*");
    }

    // ── Zip helper ───────────────────────────────────────────────────────────

    private static void WriteZipText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }

    private static void WriteZipBytes(ZipArchive zip, string entryName, byte[] bytes)
    {
        var entry = zip.CreateEntry(entryName);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static byte[] ReadZipMemberBytes(byte[] zipBytes, string entryName)
    {
        using var stream = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var member = zip.GetEntry(entryName)!.Open();
        using var bytes = new MemoryStream();
        member.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static byte[] CreateFileEntityBundle(string category, string sidecarJson, bool includeDeclaredBody)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipText(zip, "topics/imported/index.json", """
                {"entries":[{"name":"imported-note","originalBytes":5,"chunkCount":1}]}
                """);
            WriteZipText(zip, "topics/imported/imported-note.nmp2", "body");
            if (includeDeclaredBody)
                WriteZipBytes(zip, $"files/{category}/planner.md", [1, 2, 3]);
            WriteZipText(zip, $"files/{category}/planner.meta.json", sidecarJson);
            var manifest = new BundleManifest(
                Version: 2,
                Exported: DateTimeOffset.UtcNow.ToString("o"),
                Topics: ["imported"],
                TotalEntries: 1,
                FileEntities: new Dictionary<string, List<string>>
                {
                    [category] = ["planner.md", "planner.meta.json"]
                });
            WriteZipText(zip, "manifest.json",
                JsonSerializer.Serialize(manifest, BundleFormatService.DefaultJsonOptions));
        }
        return stream.ToArray();
    }

    private static byte[] CreateBundleWithRawFileEntities(string fileEntitiesJson)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipText(zip, "topics/imported/index.json", """
                {"entries":[{"name":"imported-note","originalBytes":5,"chunkCount":1}]}
                """);
            WriteZipText(zip, "topics/imported/imported-note.nmp2", "body");
            WriteZipText(zip, "manifest.json", $$"""
                {"version":2,"topics":["imported"],"totalEntries":1,
                "fileEntities":{{fileEntitiesJson}}}
                """);
        }

        return stream.ToArray();
    }

    private static SentinelBundleState SeedSentinels(FileMemoryStore store, string fileEntityKind)
    {
        const string topic = "sentinel-topic";
        const string name = "sentinel-note";
        string scope = MemoryNaming.BuildScopedTopicScope(topic);
        var entry = new ArtifactEntry(name, "", 13, 1, DateTimeOffset.UtcNow, "sentinel topic");
        store.Upsert(entry, scope);
        store.WriteArtifactAsync(name, scope, "topic sentinel").GetAwaiter().GetResult();

        string topicDirectory = store.GetStoreDirForScope(scope);
        string topicBodyPath = Path.Combine(topicDirectory, name + ".nmp2");
        string topicSidecarPath = Path.Combine(topicDirectory, name + ".meta.json");
        string fileDirectory = PathRouter.ToFileEntityDir(fileEntityKind, store.WorkspaceRoot);
        Directory.CreateDirectory(fileDirectory);
        string fileBodyPath = Path.Combine(fileDirectory, "planner.md");
        string fileSidecarPath = Path.Combine(fileDirectory, "planner.meta.json");
        File.WriteAllBytes(fileBodyPath, [0, 255, 7]);
        File.WriteAllBytes(fileSidecarPath, "{\"v\":1,\"sentinel\":true}"u8.ToArray());

        return new SentinelBundleState(
            scope,
            topicBodyPath,
            File.ReadAllBytes(topicBodyPath),
            topicSidecarPath,
            File.ReadAllBytes(topicSidecarPath),
            fileBodyPath,
            File.ReadAllBytes(fileBodyPath),
            fileSidecarPath,
            File.ReadAllBytes(fileSidecarPath));
    }

    private static void AssertSentinelsSurvived(string importRoot, SentinelBundleState sentinels)
    {
        using var cold = new FileMemoryStore(importRoot);
        cold.LoadIndex(sentinels.TopicScope).Select(entry => entry.Name).Should().Contain("sentinel-note");
        cold.LoadIndex(MemoryNaming.BuildScopedTopicScope("imported")).Should().BeEmpty();
        File.ReadAllBytes(sentinels.TopicBodyPath).Should().Equal(sentinels.TopicBody);
        File.ReadAllBytes(sentinels.TopicSidecarPath).Should().Equal(sentinels.TopicSidecar);
        File.ReadAllBytes(sentinels.FileBodyPath).Should().Equal(sentinels.FileBody);
        File.ReadAllBytes(sentinels.FileSidecarPath).Should().Equal(sentinels.FileSidecar);
    }

    private sealed record SentinelBundleState(
        string TopicScope,
        string TopicBodyPath,
        byte[] TopicBody,
        string TopicSidecarPath,
        byte[] TopicSidecar,
        string FileBodyPath,
        byte[] FileBody,
        string FileSidecarPath,
        byte[] FileSidecar);
}
