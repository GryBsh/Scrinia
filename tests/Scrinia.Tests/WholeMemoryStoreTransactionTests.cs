using FluentAssertions;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public sealed class WholeMemoryStoreTransactionTests
{
    [Theory]
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
            .Should().ThrowAsync<WholeMemoryTransactionException>();

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
