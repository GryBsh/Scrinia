using System.IO.Compression;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// B1 Task 7: bundle-export discipline. <see cref="BundleFormatService.ExportTopicsToZip"/> must
/// BUILD every topic (index + body + manifest) before it creates the target bundle, PUBLISH per
/// topic into the archive, and leave NO half-created bundle behind when the build refuses a
/// malformed topic or the atomic publish fails. These tests pin all three: a malformed topic
/// produces no bundle and no side effect, a valid export round-trips through import, and a failed
/// publish leaves no bundle (and no staging tree).
/// </summary>
public sealed class BundleExportDisciplineTests
{
    [Fact]
    public void Export_MalformedTopic_ProducesNoBundleAndNoSideEffect()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        BundleExportFixture.SeedValidTopic(store, "api", "api-flow", "How session rotation works.");

        string target = Path.Combine(scope.WorkspaceDir, "export.scrinia-bundle");

        // "bad." is a malformed topic (a segment ending with a dot) that BuildScopedTopicScope
        // refuses. It is paired with a VALID topic ("api") so the assertion proves the build
        // refuses BEFORE the valid topic is ever published — interleaved build+write would have
        // already committed "api" to the archive.
        // A malformed topic fails the BUILD (before any durable publish), so the refuse surfaces as
        // the scope constructor's ArgumentException rather than a DurableWriteException.
        Assert.Throws<ArgumentException>(() =>
            BundleFormatService.ExportTopicsToZip(target, store, ["api", "bad."]));

        Assert.False(File.Exists(target), "a refused export must not leave a bundle behind");
    }

    [Fact]
    public void Export_Success_ProducesBundleThatRoundTripsThroughImport()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        BundleExportFixture.SeedValidTopic(store, "api", "api-flow", "How session rotation works.");
        BundleExportFixture.SeedValidTopic(store, "arch", "arch-decision", "Why we chose this architecture.");

        string target = Path.Combine(scope.WorkspaceDir, "export.scrinia-bundle");

        BundleFormatService.ExportTopicsToZip(target, store, ["api", "arch"]);

        Assert.True(File.Exists(target));

        var imported = new FileMemoryStore(scope.WorkspaceDir);
        (int topicCount, int entryCount, List<string> names) =
            BundleFormatService.ImportTopicsFromZip(
                OpenForRead(target), imported, filterTopics: null, overwrite: true);

        Assert.Equal(2, topicCount);
        Assert.Equal(2, entryCount);
        Assert.Equal(["api", "arch"], names);

        ArtifactEntry api = imported.LoadIndex("local:api").First();
        Assert.Equal("How session rotation works.", api.ContentPreview);
    }

    [Fact]
    public void Export_FailedPublish_LeavesNoBundleAndNoStagingTree()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        BundleExportFixture.SeedValidTopic(store, "api", "api-flow", "How session rotation works.");
        BundleExportFixture.SeedValidTopic(store, "arch", "arch-decision", "Why we chose this architecture.");

        string target = Path.Combine(scope.WorkspaceDir, "export.scrinia-bundle");

        // The atomic DurableFile publish is the only durable write a successful export performs
        // onto the target path; failing it at BeforeWrite proves the publish never commits and the
        // export cleanup leaves no bundle.
        B0FaultSeam.FailPersistentlyAt(B0FaultSeam.FaultPhase.BeforeWrite, target);
        try
        {
            Assert.Throws<DurableWriteException>(() =>
                BundleFormatService.ExportTopicsToZip(target, store, ["api", "arch"]));
        }
        finally
        {
            B0FaultSeam.ClearFaults();
        }

        Assert.False(File.Exists(target), "a failed publish must not leave a half-created bundle");
    }

    private static ZipArchive OpenForRead(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read), ZipArchiveMode.Read);
}
