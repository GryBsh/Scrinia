using System.IO.Compression;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Regression coverage proving the bundle-import allowlist skips an unknown
/// <c>fileEntities</c> category key (e.g. "../outside") before any path
/// resolution. This proves allowlist-skip of an unknown key, not real-traversal
/// containment (the harder real-traversal case lives in BundleImportCategoryAllowlistTests).
/// </summary>
public sealed class BundleZipSlipTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspaceRoot;
    private readonly FileMemoryStore _store;

    public BundleZipSlipTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-zipslip-" + Guid.NewGuid().ToString("N")[..8]);
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
    public void ImportBundle_WithUnknownCategoryKey_IsRejected_ByAllowlist()
    {
        // The real gate is BundleFormatService.CategoryToKind, an allowlist of known
        // category keys ("skills", "agent") checked in ImportFileEntities before any
        // path resolution happens. "../outside" is not a key, so it is skipped there —
        // Path.Combine is never reached on the production side. The test itself still
        // computes sentinelDir from _workspaceRoot below and asserts against it — that base
        // directory is this test's own fixture setup, not something the gate depends on.
        string sentinelDir = Path.Combine(_workspaceRoot, "outside");
        Directory.CreateDirectory(sentinelDir);

        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                // Manifest with a traversal category key.
                WriteZipText(zip, "manifest.json",
                    """{"version":2,"topics":[],"totalEntries":0,"fileEntities":{"../outside":["pwned.md"]}}""");
                // Matching crafted zip entry the traversal category would resolve to.
                WriteZipText(zip, "files/../outside/pwned.md", "owned");
            }
            zipBytes = ms.ToArray();
        }

        // ACT — import via the real public bundle-import entry point.
        using (var readMs = new MemoryStream(zipBytes))
        using (var zipRead = new ZipArchive(readMs, ZipArchiveMode.Read))
        {
            Action import = () => BundleFormatService.ImportTopicsFromZip(zipRead, _store, filterTopics: null, overwrite: true);
            import.Should().Throw<BundleValidationException>().WithMessage("*unknown file-entity category*");
        }

        File.Exists(Path.Combine(sentinelDir, "pwned.md")).Should().BeFalse(
            "zip-slip: file written outside the store base dir");
    }

    private static void WriteZipText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }
}
