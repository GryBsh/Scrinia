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
/// (<c>skills</c>/<c>agent</c> — the sole entries in <c>CategoryToKind</c>, the single
/// allowlist) must not be imported — in particular an empty ("") category key, which
/// resolves <c>categoryDir</c> to the store's <c>.scrinia</c> base dir itself (passing
/// the existing containment check) and would otherwise let a crafted bundle write
/// arbitrary files (e.g. <c>merge.config</c>) directly under <c>.scrinia</c>. Deleted
/// categories (the former <c>workflows</c>) fall under the same gate: no
/// <c>CategoryToKind</c> entry, so nothing is written, and the caller is told via
/// <c>SkippedCategories</c> — see <see cref="ImportingABundleWithWorkflows_SucceedsAndReportsTheSkip"/>.
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
        // category holding merge.config. "" has no CategoryToKind entry (the single
        // allowlist), but categoryDir == baseDir passes the existing IsWithin check.
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
            "the empty category key has no CategoryToKind entry, the single allowlist");
    }

    [Fact]
    public void ImportingABundleWithWorkflows_SucceedsAndReportsTheSkip()
    {
        // Workflows are deleted: nothing ever wrote one, so the category could only be
        // populated by importing a bundle. Old bundles must still import cleanly, and the
        // user must be told what was dropped rather than silently losing it.
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

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

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../planner.md")]
    [InlineData("nested/planner.md")]
    [InlineData("C:\\planner.md")]
    [InlineData("planner.json")]
    public void ImportBundle_WithUnsafeKnownCategoryFilename_RefusesBeforeWriting(string fileName)
    {
        var sentinels = SeedSentinels();
        byte[] zipBytes;
        using (var stream = new MemoryStream())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var manifest = new BundleManifest(
                    Version: 2,
                    Exported: DateTimeOffset.UtcNow.ToString("o"),
                    Topics: [],
                    TotalEntries: 0,
                    FileEntities: new Dictionary<string, List<string>> { ["skills"] = [fileName] });
                WriteZipText(zip, "manifest.json",
                    JsonSerializer.Serialize(manifest, BundleFormatService.DefaultJsonOptions));
            }
            zipBytes = stream.ToArray();
        }

        using var readStream = new MemoryStream(zipBytes);
        using var zipRead = new ZipArchive(readStream, ZipArchiveMode.Read);
        Action act = () => BundleFormatService.ImportTopicsFromZip(
            zipRead, _store, filterTopics: null, overwrite: true);

        act.Should().Throw<InvalidOperationException>().WithMessage("*skills*unsafe or unsupported*");
        AssertSentinelsSurvived(sentinels);
    }

    [Fact]
    public void ImportBundle_WhenExistingDestinationResolvesOutsideCategory_RefusesBeforeWriting()
    {
        var sentinels = SeedSentinels();
        string outside = Path.Combine(_tempDir, "outside");
        Directory.CreateDirectory(outside);
        File.Delete(sentinels.FileBodyPath);
        string outsideBody = Path.Combine(outside, "planner.md");
        File.WriteAllBytes(outsideBody, sentinels.FileBody);
        Skip.If(
            !TryCreateFileSymlink(sentinels.FileBodyPath, outsideBody),
            "This host cannot create file symbolic links because platform support or privileges are unavailable.");

        byte[] zipBytes;
        using (var stream = new MemoryStream())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteZipText(zip, "files/skills/planner.md", "replacement");
                WriteZipText(zip, "manifest.json", """
                    {"version":2,"topics":[],"totalEntries":0,"fileEntities":{"skills":["planner.md"]}}
                    """);
            }
            zipBytes = stream.ToArray();
        }

        using var readStream = new MemoryStream(zipBytes);
        using var zipRead = new ZipArchive(readStream, ZipArchiveMode.Read);
        Action act = () => BundleFormatService.ImportTopicsFromZip(
            zipRead, _store, filterTopics: null, overwrite: true);

        act.Should().Throw<InvalidOperationException>().WithMessage("*skills*planner.md*outside*");
        AssertSentinelsSurvived(sentinels);
    }

    private SentinelBundleState SeedSentinels()
    {
        const string name = "sentinel-note";
        string scope = MemoryNaming.BuildScopedTopicScope("sentinel-topic");
        _store.Upsert(new Scrinia.Core.Models.ArtifactEntry(name, "", 13, 1, DateTimeOffset.UtcNow), scope);
        _store.WriteArtifactAsync(name, scope, "topic sentinel").GetAwaiter().GetResult();

        string topicDirectory = _store.GetStoreDirForScope(scope);
        string skillsDirectory = Path.Combine(_workspaceRoot, ".scrinia", "skills");
        Directory.CreateDirectory(skillsDirectory);
        string bodyPath = Path.Combine(skillsDirectory, "planner.md");
        string sidecarPath = Path.Combine(skillsDirectory, "planner.meta.json");
        File.WriteAllBytes(bodyPath, [0, 255, 7]);
        File.WriteAllBytes(sidecarPath, "{\"v\":1,\"sentinel\":true}"u8.ToArray());

        return new SentinelBundleState(
            scope,
            Path.Combine(topicDirectory, name + ".nmp2"),
            Path.Combine(topicDirectory, name + ".meta.json"),
            bodyPath,
            sidecarPath,
            File.ReadAllBytes(Path.Combine(topicDirectory, name + ".nmp2")),
            File.ReadAllBytes(Path.Combine(topicDirectory, name + ".meta.json")),
            File.ReadAllBytes(bodyPath),
            File.ReadAllBytes(sidecarPath));
    }

    private void AssertSentinelsSurvived(SentinelBundleState sentinels)
    {
        using var cold = new FileMemoryStore(_workspaceRoot);
        cold.LoadIndex(sentinels.TopicScope).Select(entry => entry.Name).Should().Contain("sentinel-note");
        File.ReadAllBytes(sentinels.TopicBodyPath).Should().Equal(sentinels.TopicBody);
        File.ReadAllBytes(sentinels.TopicSidecarPath).Should().Equal(sentinels.TopicSidecar);
        File.ReadAllBytes(sentinels.FileBodyPath).Should().Equal(sentinels.FileBody);
        File.ReadAllBytes(sentinels.FileSidecarPath).Should().Equal(sentinels.FileSidecar);
    }

    private static bool TryCreateFileSymlink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private sealed record SentinelBundleState(
        string TopicScope,
        string TopicBodyPath,
        string TopicSidecarPath,
        string FileBodyPath,
        string FileSidecarPath,
        byte[] TopicBody,
        byte[] TopicSidecar,
        byte[] FileBody,
        byte[] FileSidecar);

    private static void WriteZipText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }
}
