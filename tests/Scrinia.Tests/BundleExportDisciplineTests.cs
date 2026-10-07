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

    [Fact]
    public void ExportFailurePreservesExistingDestination()
    {
        using var scope = new TestHelpers.StoreScope();
        BundleExportFixture.SeedValidTopic(TestHelpers.Store, "api", "note", "content");
        string target = Path.Combine(scope.WorkspaceDir, "backup.scrinia-bundle");
        byte[] previous = "existing backup"u8.ToArray();
        File.WriteAllBytes(target, previous);
        B0FaultSeam.FailPersistentlyAt(B0FaultSeam.FaultPhase.BeforeMove, target);
        try
        {
            Assert.Throws<DurableWriteException>(() =>
                BundleFormatService.ExportTopicsToZip(target, TestHelpers.Store, ["api"]));
        }
        finally { B0FaultSeam.ClearFaults(); }
        Assert.Equal(previous, File.ReadAllBytes(target));
    }

    [Fact]
    public void ExportDirectorySyncFailureRestoresPriorBackup()
    {
        using var scope = new TestHelpers.StoreScope();
        BundleExportFixture.SeedValidTopic(TestHelpers.Store, "api", "note", "content");
        string destination = Path.Combine(scope.WorkspaceDir, "backup.scrinia-bundle");
        byte[] previous = "existing backup"u8.ToArray();
        File.WriteAllBytes(destination, previous);
        bool moved = false;
        bool fired = false;
        DurableFile.FailureInjector = (phase, path) =>
        {
            if (phase == DurableWritePhase.BeforeMove && path == destination) moved = true;
            if (moved && !fired && phase == DurableWritePhase.BeforeDirSync &&
                path == Path.GetDirectoryName(destination))
            {
                fired = true;
                return new IOException("one-shot export directory-sync failure");
            }
            return null;
        };
        try
        {
            Assert.Throws<DurableWriteException>(() =>
                BundleFormatService.ExportTopicsToZip(destination, TestHelpers.Store, ["api"]));
        }
        finally { DurableFile.FailureInjector = null; }
        Assert.True(fired);
        Assert.Equal(previous, File.ReadAllBytes(destination));
    }
    [Fact]
    public void ExportPostMoveFailureWithoutPriorRemovesOnlyAttemptOutput()
    {
        using var scope = new TestHelpers.StoreScope();
        BundleExportFixture.SeedValidTopic(TestHelpers.Store, "api", "note", "content");
        string destination = Path.Combine(scope.WorkspaceDir, "new.scrinia-bundle");
        bool moved = false;
        bool fired = false;
        DurableFile.FailureInjector = (phase, path) =>
        {
            if (phase == DurableWritePhase.BeforeMove && path == destination) moved = true;
            if (moved && !fired && phase == DurableWritePhase.BeforeDirSync && path == scope.WorkspaceDir)
            {
                fired = true;
                return new IOException("after replacement");
            }
            return null;
        };
        try
        {
            Assert.Throws<DurableWriteException>(() =>
                BundleFormatService.ExportTopicsToZip(destination, TestHelpers.Store, ["api"]));
        }
        finally { DurableFile.FailureInjector = null; }
        Assert.True(fired);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(scope.WorkspaceDir, ".new.scrinia-bundle.*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExportUnprovenCompensationRetainsPriorAndPreparedEvidence(bool concurrentReplacement)
    {
        using var scope = new TestHelpers.StoreScope();
        BundleExportFixture.SeedValidTopic(TestHelpers.Store, "api", "note", "content");
        string destination = Path.Combine(scope.WorkspaceDir, "backup.scrinia-bundle");
        byte[] previous = "prior backup"u8.ToArray();
        byte[] concurrent = "a different publisher's output"u8.ToArray();
        byte[]? output = null;
        File.WriteAllBytes(destination, previous);
        int moves = 0;
        bool fired = false;
        DurableFile.FailureInjector = (phase, path) =>
        {
            if (phase == DurableWritePhase.BeforeMove && path == destination)
            {
                moves++;
                if (moves > 1) return new UnauthorizedAccessException("compensation blocked");
            }
            if (moves == 1 && !fired && phase == DurableWritePhase.BeforeDirSync && path == scope.WorkspaceDir)
            {
                fired = true;
                output = File.ReadAllBytes(destination);
                if (concurrentReplacement) File.WriteAllBytes(destination, concurrent);
                return new IOException("after replacement");
            }
            return null;
        };
        DurableWriteException failure;
        try
        {
            failure = Assert.Throws<DurableWriteException>(() =>
                BundleFormatService.ExportTopicsToZip(destination, TestHelpers.Store, ["api"]));
        }
        finally { DurableFile.FailureInjector = null; }
        Assert.True(fired);
        Assert.Equal(concurrentReplacement ? concurrent : output, File.ReadAllBytes(destination));
        string prior = Assert.Single(Directory.GetFiles(scope.WorkspaceDir, ".backup.scrinia-bundle.*.prior"));
        string prepared = Assert.Single(Directory.GetFiles(scope.WorkspaceDir, ".backup.scrinia-bundle.*.tmp"));
        Assert.Equal(previous, File.ReadAllBytes(prior));
        Assert.Equal(output, File.ReadAllBytes(prepared));
        Assert.Contains(prior, failure.Message, StringComparison.Ordinal);
        Assert.Contains(prepared, failure.Message, StringComparison.Ordinal);
        Assert.Equal(concurrentReplacement ? 1 : 2, moves);
    }
    [Fact]
    public void ExportCleanupFailureDoesNotReportPublishedBundleAsRolledBack()
    {
        using var scope = new TestHelpers.StoreScope();
        BundleExportFixture.SeedValidTopic(TestHelpers.Store, "api", "note", "content");
        string destination = Path.Combine(scope.WorkspaceDir, "backup.scrinia-bundle");
        byte[] previous = "prior backup"u8.ToArray();
        File.WriteAllBytes(destination, previous);
        bool fired = false;
        DurableFile.DeleteFailureInjector = (phase, path) =>
        {
            if (phase == DurableDeletePhase.BeforeDelete && path.EndsWith(".prior", StringComparison.Ordinal))
            {
                fired = true;
                return new UnauthorizedAccessException("prior cleanup blocked");
            }
            return null;
        };
        try
        {
            var (topics, entries) = BundleFormatService.ExportTopicsToZip(destination, TestHelpers.Store, ["api"]);
            Assert.Equal(["api"], topics);
            Assert.Equal(1, entries);
        }
        finally { DurableFile.DeleteFailureInjector = null; }
        Assert.True(fired);
        using var archive = ZipFile.OpenRead(destination);
        Assert.NotNull(archive.GetEntry("topics/api/note.nmp2"));
        string prior = Assert.Single(Directory.GetFiles(scope.WorkspaceDir, ".backup.scrinia-bundle.*.prior"));
        Assert.Equal(previous, File.ReadAllBytes(prior));
        Assert.Empty(Directory.GetFiles(scope.WorkspaceDir, ".backup.scrinia-bundle.*.tmp"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExportLateDestinationChangeRefusesCompensation(bool hasPrior)
    {
        using var scope = new TestHelpers.StoreScope();
        BundleExportFixture.SeedValidTopic(TestHelpers.Store, "api", "note", "content");
        string destination = Path.Combine(scope.WorkspaceDir, "backup.scrinia-bundle");
        byte[] prior = "prior backup"u8.ToArray();
        byte[] concurrent = "another publisher won during compensation"u8.ToArray();
        byte[]? output = null;
        if (hasPrior) File.WriteAllBytes(destination, prior);
        int moves = 0;
        bool syncFailed = false;
        bool changed = false;
        DurableFile.FailureInjector = (phase, path) =>
        {
            if (phase == DurableWritePhase.BeforeMove && path == destination)
            {
                moves++;
                if (moves > 1)
                {
                    changed = true;
                    File.WriteAllBytes(destination, concurrent);
                }
            }
            if (moves == 1 && !syncFailed && phase == DurableWritePhase.BeforeDirSync && path == scope.WorkspaceDir)
            {
                syncFailed = true;
                output = File.ReadAllBytes(destination);
                return new IOException("after replacement");
            }
            return null;
        };
        DurableFile.DeleteFailureInjector = (phase, path) =>
        {
            if (!hasPrior && phase == DurableDeletePhase.BeforeDelete && path == destination)
            {
                changed = true;
                File.WriteAllBytes(destination, concurrent);
            }
            return null;
        };
        DurableWriteException failure;
        try
        {
            failure = Assert.Throws<DurableWriteException>(() =>
                BundleFormatService.ExportTopicsToZip(destination, TestHelpers.Store, ["api"]));
        }
        finally
        {
            DurableFile.FailureInjector = null;
            DurableFile.DeleteFailureInjector = null;
        }
        Assert.True(syncFailed);
        Assert.True(changed);
        Assert.Equal(concurrent, File.ReadAllBytes(destination));
        string prepared = Assert.Single(Directory.GetFiles(scope.WorkspaceDir, ".backup.scrinia-bundle.*.tmp"));
        Assert.Equal(output, File.ReadAllBytes(prepared));
        Assert.Contains(prepared, failure.Message, StringComparison.Ordinal);
        if (hasPrior)
        {
            string backup = Assert.Single(Directory.GetFiles(scope.WorkspaceDir, ".backup.scrinia-bundle.*.prior"));
            Assert.Equal(prior, File.ReadAllBytes(backup));
            Assert.Contains(backup, failure.Message, StringComparison.Ordinal);
        }
    }
    private static ZipArchive OpenForRead(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read), ZipArchiveMode.Read);
}
