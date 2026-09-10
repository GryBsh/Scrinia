using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed class AppendCleanupTests
{
    [Fact]
    public async Task AppendAsync_CleanupFailureKeepsNewPairAndReturnsDistinctWarning()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        workspace.Store.FailOnceAt(AppendFaultPoint.ArchiveCleanup);

        AppendStoreResult result = await workspace.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

        result.Outcome.Should().Be(AppendStoreOutcome.SuccessWithWarning);
        result.Warning!.Code.Should().Be(AppendWarningCode.PostCommitCleanupFailed);
        (await workspace.Store.ReadAsync(MemoryAddress.Parse("/target")))!.ChunkCount.Should().Be(2);
    }

    [Fact]
    public async Task AppendAsync_ArchiveCleanupFailureDoesNotPrunePlannedEvidence()
    {
        using var workspace = new TempWorkspace();
        for (int version = 0; version < 12; version++)
            await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", [$"old-{version}"]));
        workspace.Store.FailOnceAt(AppendFaultPoint.ArchiveCleanup);

        await workspace.Store.AppendAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

        string versions = Path.Combine(workspace.Store.GetStoreDirForScope("local"), "versions");
        Directory.EnumerateFiles(versions, "target_*.nmp2").Should().HaveCount(11);
    }

    [Fact]
    public async Task RecoverAppendAsync_CleanupPendingRetriesOnlyPlannedPruning()
    {
        using var workspace = new TempWorkspace();
        for (int version = 0; version < 12; version++)
            await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", [$"old-{version}"]));
        string versions = Path.Combine(workspace.Store.GetStoreDirForScope("local"), "versions");
        string retainedArchive = Directory.EnumerateFiles(versions, "target_*.nmp2").First();
        File.Copy(retainedArchive, Path.Combine(versions, "target_manual.nmp2"));
        workspace.Store.FailOnceAt(AppendFaultPoint.PruningDelete);

        AppendStoreResult result = await workspace.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

        result.Outcome.Should().Be(AppendStoreOutcome.SuccessWithWarning);
        workspace.Store.ClearFaults();
        await workspace.Store.RecoverAppendAsync("local");

        Directory.EnumerateFiles(versions, "target_*.nmp2").Should().HaveCount(11);
        File.Exists(Path.Combine(versions, AppendJournal.FileName)).Should().BeFalse();
        (await workspace.Store.ReadAsync(MemoryAddress.Parse("/target")))!.ChunkCount.Should().Be(2);
    }

    [Fact]
    public async Task AppendAsync_SidecarArchiveFailureNamesTheArchiveMemberAndOperation()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        workspace.Store.FailOnceAt(AppendFaultPoint.ArchiveSidecarWrite);

        AppendStoreException exception = (await FluentActions.Invoking(() =>
                workspace.Store.AppendAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
            .Should().ThrowAsync<AppendStoreException>()).Which;

        exception.Code.Should().Be(AppendStoreErrorCode.RecoveryRequired);
        exception.OperationId.Should().NotBeNullOrWhiteSpace();
        exception.InnerException!.Message.Should().Contain("Archive sidecar");
    }

    [Fact]
    public async Task PruningStopsAfterUnsafeCandidateAndLeavesLaterSafeCandidateUntouched()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        await workspace.Store.AppendAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

        AppendTransaction transaction = workspace.Store.LastPreparedAppendTransactionForTest!;
        string versions = workspace.Store.GetStoreDirForScope("local") + Path.DirectorySeparatorChar + "versions";
        string unsafeCandidate = Path.Combine(versions, "..", "outside.nmp2");
        string safeCandidate = Path.Combine(versions, "later-safe.nmp2");
        Directory.CreateDirectory(versions);
        await File.WriteAllBytesAsync(Path.GetFullPath(unsafeCandidate), [1]);
        await File.WriteAllBytesAsync(safeCandidate, [2]);
        transaction = transaction with
        {
            Archive = new ArchivePlan(
                true,
                Path.Combine(versions, "current.nmp2"),
                Path.Combine(versions, "current.meta.json"),
                ArchiveMemberStatus.Planned,
                ArchiveMemberStatus.Planned,
                [unsafeCandidate, safeCandidate])
        };

        workspace.Store.PruneCommittedArchivesForTest(transaction).Should().ContainSingle();
        File.Exists(safeCandidate).Should().BeTrue();
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "scrinia-append-cleanup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Store = new FileMemoryStore(Root);
        }

        public string Root { get; }
        public FileMemoryStore Store { get; }

        public void Dispose()
        {
            Store.ClearFaults();
            Store.Dispose();
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
