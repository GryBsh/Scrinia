using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Regression coverage for bundle-import category allowlisting: a manifest whose
/// <c>fileEntities</c> key is NOT one of the allowlisted categories
/// (<c>workflows</c>/<c>skills</c>/<c>agent</c>) must not be imported — in particular
/// an empty ("") category key, which resolves <c>categoryDir</c> to the store's
/// <c>.scrinia</c> base dir itself (passing the existing containment check) and would
/// otherwise let a crafted bundle write arbitrary files (e.g. <c>merge.config</c>)
/// directly under <c>.scrinia</c>.
/// </summary>
public sealed class BundleImportCategoryAllowlistTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspaceRoot;
    private readonly FileMemoryStore _store;

    public BundleImportCategoryAllowlistTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-catallow-" + Guid.NewGuid().ToString("N")[..8]);
        _workspaceRoot = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(_workspaceRoot);
        _store = new FileMemoryStore(_workspaceRoot);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void ImportBundle_WithNonAllowlistedCategory_DoesNotWriteFile()
    {
        // Arrange — a v2 bundle whose fileEntities manifest declares an empty ("")
        // category holding merge.config. "" is not in the FileEntityCategories
        // allowlist, but categoryDir == baseDir passes the existing IsWithin check.
        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteZipText(zip, "files//merge.config", "resolver = evil");

                var manifest = new BundleManifest(
                    Version: 2,
                    Exported: DateTimeOffset.UtcNow.ToString("o"),
                    Topics: [],
                    TotalEntries: 0,
                    FileEntities: new Dictionary<string, List<string>>
                    {
                        [""] = ["merge.config"]
                    });
                string manifestJson = JsonSerializer.Serialize(manifest, BundleFormatService.DefaultJsonOptions);
                WriteZipText(zip, "manifest.json", manifestJson);
            }
            zipBytes = ms.ToArray();
        }

        // Act — import via the real public bundle-import entry point.
        using (var readMs = new MemoryStream(zipBytes))
        using (var zipRead = new ZipArchive(readMs, ZipArchiveMode.Read))
        {
            BundleFormatService.ImportTopicsFromZip(zipRead, _store, filterTopics: null, overwrite: true);
        }

        // Assert — the malicious file was NOT written under .scrinia.
        string scriniaBase = Path.Combine(_workspaceRoot, ".scrinia");
        File.Exists(Path.Combine(scriniaBase, "merge.config")).Should().BeFalse(
            "the empty category key is not in the FileEntityCategories allowlist");
    }

    [Fact]
    public void ImportingABundleWithWorkflows_SucceedsAndReportsTheSkip()
    {
        // Workflows are deleted: nothing ever wrote one, so the category could only be
        // populated by importing a bundle. Old bundles must still import cleanly, and the
        // user must be told what was dropped rather than silently losing it.
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        string bundlePath = Path.Combine(scope.WorkspaceDir, "legacy.scrinia-bundle");
        using (var zip = ZipFile.Open(bundlePath, ZipArchiveMode.Create))
        {
            WriteZipText(zip, "manifest.json", """
                {"version":2,"topics":[],"fileEntities":{"workflows":["ci.json"]}}
                """);
            WriteZipText(zip, "files/workflows/ci.json", """{"steps":["build"]}""");
        }

        using var readStream = new FileStream(bundlePath, FileMode.Open, FileAccess.Read);
        using var zipRead = new ZipArchive(readStream, ZipArchiveMode.Read);
        var result = BundleFormatService.ImportTopicsFromZip(zipRead, store, filterTopics: null, overwrite: false);

        result.SkippedCategories.Should().Contain("workflows");
        Directory.Exists(Path.Combine(store.WorkspaceRoot, ".scrinia", "workflows"))
            .Should().BeFalse(because: "the category is deleted, not merely unwritten");
    }

    private static void WriteZipText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }
}
