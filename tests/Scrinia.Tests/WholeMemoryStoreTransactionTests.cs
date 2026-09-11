using FluentAssertions;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public sealed class WholeMemoryStoreTransactionTests
{
    [Theory]
    [InlineData((int)WholeMemoryFaultPoint.RawArtifactRead)]
    [InlineData((int)WholeMemoryFaultPoint.RawSidecarRead)]
    [InlineData((int)WholeMemoryFaultPoint.EvidenceWrite)]
    [InlineData((int)WholeMemoryFaultPoint.ArchiveArtifactWrite)]
    [InlineData((int)WholeMemoryFaultPoint.ArchiveSidecarWrite)]
    [InlineData((int)WholeMemoryFaultPoint.LiveArtifactWrite)]
    [InlineData((int)WholeMemoryFaultPoint.LiveSidecarWrite)]
    [InlineData((int)WholeMemoryFaultPoint.VerificationRead)]
    public async Task StoreAsync_PreCommitFailureRestoresTheOriginalPair(int faultPointValue)
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldBody = File.ReadAllBytes(store.ArtifactPath("target"));
        byte[] oldMeta = File.ReadAllBytes(store.MetaPath("target"));
        store.FailOnceAt((WholeMemoryFaultPoint)faultPointValue);

        await FluentActions.Invoking(() => store.StoreAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
            .Should().ThrowAsync<IOException>();

        File.ReadAllBytes(store.ArtifactPath("target")).Should().Equal(oldBody);
        File.ReadAllBytes(store.MetaPath("target")).Should().Equal(oldMeta);
    }

    [Fact]
    public async Task StoreAsync_LiveSidecarFailureRestoresBothMembersByteForByte()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldBody = File.ReadAllBytes(store.ArtifactPath("target"));
        byte[] oldMeta = File.ReadAllBytes(store.MetaPath("target"));

        store.FailOnceAt(WholeMemoryFaultPoint.LiveSidecarWrite);

        await FluentActions.Invoking(() => store.StoreAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
            .Should().ThrowAsync<IOException>();

        File.ReadAllBytes(store.ArtifactPath("target")).Should().Equal(oldBody);
        File.ReadAllBytes(store.MetaPath("target")).Should().Equal(oldMeta);
    }

    [Fact]
    public async Task StoreAsync_PersistentWriteCommitsArtifactAndSidecarTogether()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;

        ArtifactEntry entry = await store.StoreAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

        entry.Name.Should().Be("target");
        File.Exists(store.ArtifactPath("target")).Should().BeTrue();
        File.Exists(store.MetaPath("target")).Should().BeTrue();
    }

    [Fact]
    public async Task StoreAsync_CleanupStateFailureKeepsCommittedPairAndEvidence()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldBody = File.ReadAllBytes(store.ArtifactPath("target"));
        int cleanupFaults = 0;
        FileMemoryStore.WholeMemoryFaultInjector = (point, _) =>
            (point is WholeMemoryFaultPoint.EvidenceCleanup or WholeMemoryFaultPoint.CleanupStateWrite)
                && Interlocked.Increment(ref cleanupFaults) <= 2
                ? new IOException($"Injected cleanup fault at {point}.")
                : null;

        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

        File.ReadAllBytes(store.ArtifactPath("target")).Should().NotEqual(oldBody);
        File.Exists(Path.Combine(store.GetStoreDirForScope("local"), "versions", AppendJournal.FileName))
            .Should().BeTrue();
    }

    [Fact]
    public async Task StoreAsync_CommittedEvidenceIsRecoveredByAFreshStore()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        store.FailOnceAt(WholeMemoryFaultPoint.EvidenceCleanup);

        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

        string journalPath = Path.Combine(store.GetStoreDirForScope("local"), "versions", AppendJournal.FileName);
        File.Exists(journalPath).Should().BeTrue();
        using var freshStore = new FileMemoryStore(workspace.WorkspaceDir);
        await freshStore.RecoverAppendAsync("local");

        File.Exists(journalPath).Should().BeFalse();
        ArtifactEntry recovered = freshStore.LoadIndex("local")
            .Should().ContainSingle(entry => entry.Name == "target").Subject;
        recovered.Name.Should().Be("target");
    }

    [Fact]
    public async Task StoreAsync_ArchiveCleanupFailureRetainsCommittedEvidenceWithoutFailingWrite()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        FileMemoryStore.WholeMemoryFaultInjector = (point, _) =>
            point == WholeMemoryFaultPoint.ArchiveCleanup
                ? new IOException("Injected archive cleanup failure.")
                : null;

        ArtifactEntry entry = await store.StoreAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

        entry.Name.Should().Be("target");
        File.Exists(store.ArtifactPath("target")).Should().BeTrue();
        File.Exists(Path.Combine(store.GetStoreDirForScope("local"), "versions", AppendJournal.FileName))
            .Should().BeTrue();
    }

    [Fact]
    public void PruneWholeArchives_UsesOnlyTheFrozenCandidateList()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string versions = Path.Combine(store.GetStoreDirForScope("local"), "versions");
        Directory.CreateDirectory(versions);
        string frozenArtifact = Path.Combine(versions, "target_frozen.nmp2");
        string frozenSidecar = Path.Combine(versions, "target_frozen.meta.json");
        string laterArtifact = Path.Combine(versions, "target_later.nmp2");
        File.WriteAllBytes(frozenArtifact, [1]);
        File.WriteAllBytes(frozenSidecar, [2]);
        File.WriteAllBytes(laterArtifact, [3]);

        string[] failures = store.PruneWholeArchives([frozenArtifact, frozenSidecar], "test-operation");

        failures.Should().BeEmpty();
        File.Exists(frozenArtifact).Should().BeFalse();
        File.Exists(frozenSidecar).Should().BeFalse();
        File.Exists(laterArtifact).Should().BeTrue();
    }

    [Fact]
    public async Task StoreAsync_RetainsOnlyTheConfiguredArchiveWindow()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        for (int version = 0; version < 12; version++)
            await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", [$"old-{version}"]));

        Directory.EnumerateFiles(Path.Combine(store.GetStoreDirForScope("local"), "versions"), "target_*.nmp2")
            .Should().HaveCount(10);
    }

    [Fact]
    public async Task StoreAsync_ArchiveSidecarFailureRemovesUnpairedArchive()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        store.FailOnceAt(WholeMemoryFaultPoint.ArchiveSidecarWrite);

        await FluentActions.Invoking(() => store.StoreAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
            .Should().ThrowAsync<WholeMemoryTransactionException>();

        string versions = Path.Combine(store.GetStoreDirForScope("local"), "versions");
        if (Directory.Exists(versions))
            Directory.GetFiles(versions, "*.nmp2").Should().BeEmpty();
        File.Exists(store.ArtifactPath("target")).Should().BeTrue();
    }

    [Fact]
    public async Task StoreAsync_RollbackFailureRetainsRecoveryEvidence()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        FileMemoryStore.WholeMemoryFaultInjector = (point, _) => point switch
        {
            WholeMemoryFaultPoint.LiveArtifactWrite or WholeMemoryFaultPoint.RollbackWrite =>
                new IOException($"Injected whole-memory fault at {point}."),
            _ => null
        };

        WholeMemoryTransactionException exception = (await FluentActions.Invoking(() => store.StoreAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
            .Should().ThrowAsync<WholeMemoryTransactionException>()).Which;

        exception.Code.Should().Be(WholeMemoryErrorCode.RecoveryRequired);
        File.Exists(Path.Combine(store.GetStoreDirForScope("local"), "versions", AppendJournal.FileName))
            .Should().BeTrue();
    }

    [Fact]
    public async Task StoreAsync_CancellationBeforeCommitRestoresTheOriginalPair()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldBody = File.ReadAllBytes(store.ArtifactPath("target"));
        byte[] oldMeta = File.ReadAllBytes(store.MetaPath("target"));
        using var cancellation = new CancellationTokenSource();
        FileMemoryStore.WholeMemoryFaultInjector = (point, _) =>
            point == WholeMemoryFaultPoint.LiveArtifactWrite
                ? CancelAndReturn(cancellation)
                : null;

        await FluentActions.Invoking(() => store.StoreAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]), cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        File.ReadAllBytes(store.ArtifactPath("target")).Should().Equal(oldBody);
        File.ReadAllBytes(store.MetaPath("target")).Should().Equal(oldMeta);
    }

    [Fact]
    public async Task StoreAsync_CancellationAfterCommitStillReturnsCommittedPair()
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        using var cancellation = new CancellationTokenSource();
        FileMemoryStore.WholeMemoryFaultInjector = (point, _) =>
            point == WholeMemoryFaultPoint.VerificationRead
                ? CancelAndReturn(cancellation)
                : null;

        ArtifactEntry entry = await store.StoreAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]), cancellation.Token);

        entry.Name.Should().Be("target");
        File.Exists(store.ArtifactPath("target")).Should().BeTrue();
        File.Exists(store.MetaPath("target")).Should().BeTrue();
    }

    private static Exception? CancelAndReturn(CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        return null;
    }
}
