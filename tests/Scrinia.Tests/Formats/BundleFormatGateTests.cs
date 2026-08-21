using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Formats;
using Scrinia.Core.Models;

namespace Scrinia.Tests.Formats;

/// <summary>
/// Exercises the two Task 5 gate checks added to <see cref="BundleFormatService.ImportTopicsFromZip"/>:
/// the manifest-level version refusal and the per-entry SchemaVersion refusal. Both are
/// "refuse before any write" checks — see the comments beside each throw in
/// BundleFormatService.cs — so each test here asserts nothing was imported, not only that an
/// exception was thrown.
/// </summary>
public sealed class BundleFormatGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspaceRoot;
    private readonly FileMemoryStore _store;

    public BundleFormatGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-bundlegate-" + Guid.NewGuid().ToString("N")[..8]);
        _workspaceRoot = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(_workspaceRoot);
        _store = new FileMemoryStore(_workspaceRoot);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static void WriteZipText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }

    [Fact]
    public void Import_RefusesManifestVersionNewerThanCurrent()
    {
        // A manifest one past DurableFormats.BundleManifest.Current claims a capability this
        // build does not understand. Deleting the Step-4 check makes this pass (a manifest
        // with an unknown-newer version silently imports), which is exactly the regression
        // this test exists to catch.
        int newerThanCurrent = DurableFormats.BundleManifest.Current + 1;

        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var indexJson = JsonSerializer.Serialize(
                    new BundleIndex(new List<ArtifactEntry>
                    {
                        new("note", "", 5, 1, DateTimeOffset.UtcNow, "a note")
                    }),
                    BundleFormatService.DefaultJsonOptions);
                WriteZipText(zip, "topics/test/index.json", indexJson);
                WriteZipText(zip, "topics/test/note.nmp2", "hello");

                var manifest = new BundleManifest(
                    Version: newerThanCurrent,
                    Exported: DateTimeOffset.UtcNow.ToString("o"),
                    Topics: ["test"],
                    TotalEntries: 1);
                WriteZipText(zip, "manifest.json",
                    JsonSerializer.Serialize(manifest, BundleFormatService.DefaultJsonOptions));
            }
            zipBytes = ms.ToArray();
        }

        using var readMs = new MemoryStream(zipBytes);
        using var zipRead = new ZipArchive(readMs, ZipArchiveMode.Read);

        Action act = () => BundleFormatService.ImportTopicsFromZip(
            zipRead, _store, filterTopics: null, overwrite: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*version {newerThanCurrent}*");

        // Refused before any write: the topic must not have landed in the store.
        _store.LoadIndex(MemoryNaming.BuildScopedTopicScope("test")).Should().BeEmpty();
    }

    [Fact]
    public void Import_RefusesEntrySchemaVersionNewerThanArtifactSidecarCurrent()
    {
        // A per-entry "v" one past DurableFormats.ArtifactSidecar.Current means this entry was
        // written by a build newer than ours. Deleting the Step-5 check makes this pass (the
        // entry silently imports and this build then rewrites it under its own, older rules),
        // which is exactly the regression this test exists to catch.
        int newerThanCurrent = DurableFormats.ArtifactSidecar.Current + 1;

        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = new ArtifactEntry(
                    Name: "note", Uri: "", OriginalBytes: 5, ChunkCount: 1,
                    CreatedAt: DateTimeOffset.UtcNow, ContentPreview: "a note",
                    SchemaVersion: newerThanCurrent);
                var indexJson = JsonSerializer.Serialize(
                    new BundleIndex(new List<ArtifactEntry> { entry }),
                    BundleFormatService.DefaultJsonOptions);
                WriteZipText(zip, "topics/test/index.json", indexJson);
                WriteZipText(zip, "topics/test/note.nmp2", "hello");

                // Manifest version 1 — at-or-below Current, so only the entry-level check fires.
                var manifest = new BundleManifest(
                    Version: 1,
                    Exported: DateTimeOffset.UtcNow.ToString("o"),
                    Topics: ["test"],
                    TotalEntries: 1);
                WriteZipText(zip, "manifest.json",
                    JsonSerializer.Serialize(manifest, BundleFormatService.DefaultJsonOptions));
            }
            zipBytes = ms.ToArray();
        }

        using var readMs = new MemoryStream(zipBytes);
        using var zipRead = new ZipArchive(readMs, ZipArchiveMode.Read);

        Action act = () => BundleFormatService.ImportTopicsFromZip(
            zipRead, _store, filterTopics: null, overwrite: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*schema version {newerThanCurrent}*");

        _store.LoadIndex(MemoryNaming.BuildScopedTopicScope("test")).Should().BeEmpty();
    }

    [Fact]
    public void ExportedIndexJson_CarriesExplicitNullSchemaVersionWhenAbsent()
    {
        // Known fact, not a defect to fix here: BundleJsonContext has no
        // DefaultIgnoreCondition = WhenWritingNull (unlike SidecarJsonContext), so a freshly
        // written entry with no SchemaVersion serializes "v": null explicitly rather than
        // omitting the key. Pinned so a future change to that attribute is a deliberate,
        // visible decision rather than an accidental byte-size / round-trip change.
        var entry = new ArtifactEntry(
            Name: "note", Uri: "", OriginalBytes: 5, ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow, ContentPreview: "a note");
        string indexJson = JsonSerializer.Serialize(
            new BundleIndex(new List<ArtifactEntry> { entry }),
            BundleFormatService.DefaultJsonOptions);

        indexJson.Should().Contain("\"v\": null");
    }
}
