using System.Security.Cryptography;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed class AppendTransactionTests
{
    [Fact]
    public void RawMemberSnapshot_CopiesBytesAndReportsStableHash()
    {
        byte[] source = [1, 2, 3];
        var snapshot = new RawMemberSnapshot("artifact", true, source, Convert.ToHexStringLower(SHA256.HashData(source)));

        source[0] = 9;
        byte[] exposed = snapshot.Bytes;
        exposed[1] = 8;

        snapshot.Bytes.Should().Equal([1, 2, 3]);
        snapshot.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData([1, 2, 3])));
    }

    [Fact]
    public void ArchivePlan_CopiesPruneCandidates()
    {
        var candidates = new List<string> { "one", "two" };
        var plan = new ArchivePlan(true, "artifact", "sidecar", ArchiveMemberStatus.Planned,
            ArchiveMemberStatus.Planned, candidates);

        candidates.Add("three");

        plan.PruneCandidates.Should().Equal("one", "two");
    }

    [Fact]
    public async Task AppendAsync_CapturesOldArtifactAndSidecarBytesBeforeAnyMutation()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldArtifact = await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("target"));
        byte[] oldSidecar = await File.ReadAllBytesAsync(workspace.Store.MetaPath("target"));

        AppendStoreResult result = await workspace.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

        result.Outcome.Should().Be(AppendStoreOutcome.Appended);
        workspace.Store.LastCapturedOldArtifactBytesForTest.Should().Equal(oldArtifact);
        workspace.Store.LastCapturedOldSidecarBytesForTest.Should().Equal(oldSidecar);
    }

    [Fact]
    public async Task AppendAsync_PreparationReadFailureLeavesBothLiveMembersUnchanged()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldArtifact = await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("target"));
        byte[] oldSidecar = await File.ReadAllBytesAsync(workspace.Store.MetaPath("target"));
        workspace.Store.FailOnceAt(AppendFaultPoint.RawSidecarRead);

        Func<Task> append = () => workspace.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

        await append.Should().ThrowAsync<AppendStoreException>()
            .Where(exception => exception.Code == AppendStoreErrorCode.Internal);
        (await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("target"))).Should().Equal(oldArtifact);
        (await File.ReadAllBytesAsync(workspace.Store.MetaPath("target"))).Should().Equal(oldSidecar);
        Directory.Exists(Path.Combine(workspace.Root, ".scrinia", "memories", "versions")).Should().BeFalse();
    }

    [Fact]
    public async Task AppendAsync_PublishesTheNewArtifactExactlyOnce()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        int artifactWrites = 0;
        FileMemoryStore.AppendFaultInjector = (point, _, path) =>
        {
            if (point == AppendFaultPoint.LiveArtifactWrite && path.EndsWith("target.nmp2", StringComparison.Ordinal))
                artifactWrites++;
            return null;
        };

        try
        {
            await workspace.Store.AppendAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));
        }
        finally
        {
            workspace.Store.ClearFaults();
        }

        artifactWrites.Should().Be(1);
    }

    [Fact]
    public async Task AppendAsync_ArchivePlanUsesExistingTimestampAndCollisionConventions()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        string versions = Path.Combine(workspace.Root, ".scrinia", "memories", "versions");
        Directory.CreateDirectory(versions);
        DateTimeOffset startedAt = new(2026, 9, 8, 12, 34, 56, 789, TimeSpan.Zero);
        string collisionBase = "target_" + startedAt.ToString("yyyyMMdd-HHmmss-fff");
        await File.WriteAllBytesAsync(Path.Combine(versions, collisionBase + ".nmp2"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(versions, collisionBase + "-2.nmp2"), [2]);

        var plan = workspace.Store.BuildArchivePlanForTest("target", "local", true, startedAt);

        plan.Required.Should().BeTrue();
        Path.GetFileName(plan.ArtifactArchivePath!).Should().Be(collisionBase + "-3.nmp2");
        plan.PruneCandidates.Should().BeEmpty();
    }

    [Fact]
    public async Task AppendAsync_ArchivesAtTheFrozenPlanDestination()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldArtifact = await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("target"));

        await workspace.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

        var transaction = workspace.Store.LastPreparedAppendTransactionForTest!;
        File.Exists(transaction.Archive.ArtifactArchivePath!).Should().BeTrue();
        File.Exists(transaction.Archive.SidecarArchivePath!).Should().BeTrue();
        (await File.ReadAllBytesAsync(transaction.Archive.ArtifactArchivePath!)).Should().Equal(
            oldArtifact);
        Directory.GetFiles(Path.GetDirectoryName(transaction.Archive.ArtifactArchivePath!)!, "target_*.nmp2")
            .Should().ContainSingle(transaction.Archive.ArtifactArchivePath!);
    }

    [Fact]
    public async Task AppendAsync_ArchiveCopyFailureLeavesLiveMembersUnchanged()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldArtifact = await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("target"));
        byte[] oldSidecar = await File.ReadAllBytesAsync(workspace.Store.MetaPath("target"));
        workspace.Store.FailOnceAt(AppendFaultPoint.ArchiveArtifactWrite);

        Func<Task> append = () => workspace.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

        await append.Should().ThrowAsync<AppendStoreException>()
            .Where(exception => exception.Code == AppendStoreErrorCode.RecoveryRequired);
        (await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("target"))).Should().Equal(oldArtifact);
        (await File.ReadAllBytesAsync(workspace.Store.MetaPath("target"))).Should().Equal(oldSidecar);
    }

    [Fact]
    public async Task AppendAsync_SidecarArchiveFailureLeavesArtifactArchiveButNotLiveChanges()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldArtifact = await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("target"));
        byte[] oldSidecar = await File.ReadAllBytesAsync(workspace.Store.MetaPath("target"));
        workspace.Store.FailOnceAt(AppendFaultPoint.ArchiveSidecarWrite);

        Func<Task> append = () => workspace.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

        await append.Should().ThrowAsync<AppendStoreException>()
            .Where(exception => exception.Code == AppendStoreErrorCode.RecoveryRequired);
        (await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("target"))).Should().Equal(oldArtifact);
        (await File.ReadAllBytesAsync(workspace.Store.MetaPath("target"))).Should().Equal(oldSidecar);
        var plan = workspace.Store.LastPreparedAppendTransactionForTest!.Archive;
        (await File.ReadAllBytesAsync(plan.ArtifactArchivePath!)).Should().Equal(oldArtifact);
        File.Exists(plan.SidecarArchivePath!).Should().BeFalse();
    }

    [Fact]
    public async Task AppendAsync_CancellationDuringSidecarArchiveRemovesCreatedArchiveAndJournal()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldArtifact = await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("target"));
        byte[] oldSidecar = await File.ReadAllBytesAsync(workspace.Store.MetaPath("target"));
        using var cancellation = new CancellationTokenSource();
        string? injectedOperationId = null;
        FileMemoryStore.AppendFaultInjector = (point, operationId, _) =>
        {
            if (point != AppendFaultPoint.ArchiveSidecarWrite)
                return null;

            injectedOperationId = operationId;
            cancellation.Cancel();
            return new OperationCanceledException(cancellation.Token);
        };

        try
        {
            var exception = await FluentActions.Invoking(() => workspace.Store.AppendAsync(
                    MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]), cancellation.Token))
                .Should().ThrowAsync<AppendOperationCanceledException>();

            exception.Which.OperationId.Should().Be(injectedOperationId);
            (await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("target"))).Should().Equal(oldArtifact);
            (await File.ReadAllBytesAsync(workspace.Store.MetaPath("target"))).Should().Equal(oldSidecar);
            var transaction = workspace.Store.LastPreparedAppendTransactionForTest!;
            File.Exists(transaction.Archive.ArtifactArchivePath!).Should().BeFalse();
            File.Exists(transaction.Archive.SidecarArchivePath!).Should().BeFalse();
            File.Exists(Path.Combine(Path.GetDirectoryName(transaction.Archive.ArtifactArchivePath!)!,
                    AppendJournal.FileName)).Should().BeFalse();
        }
        finally
        {
            workspace.Store.ClearFaults();
        }
    }

    [Fact]
    public async Task AppendAsync_PrunesOnlyAfterLiveCommitAndKeepsTenArchives()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        string versions = Path.Combine(workspace.Root, ".scrinia", "memories", "versions");
        Directory.CreateDirectory(versions);
        for (int i = 1; i <= 11; i++)
            await File.WriteAllBytesAsync(Path.Combine(versions, $"target_20200101-000000-{i:000}.nmp2"), [(byte)i]);

        byte[] oldArtifact = await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("target"));
        bool pruningSawCommittedLive = false;
        bool failedOnce = false;
        FileMemoryStore.AppendFaultInjector = (point, _, _) =>
        {
            if (point != AppendFaultPoint.PruningDelete)
                return null;
            pruningSawCommittedLive = !File.ReadAllBytes(workspace.Store.ArtifactPath("target")).SequenceEqual(oldArtifact);
            if (failedOnce)
                return null;
            failedOnce = true;
            return new IOException("best-effort pruning failure");
        };

        try
        {
            (await workspace.Store.AppendAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
                .Outcome.Should().Be(AppendStoreOutcome.SuccessWithWarning);
        }
        finally
        {
            workspace.Store.ClearFaults();
        }

        pruningSawCommittedLive.Should().BeTrue();
        // The one injected prune failure is retained; the newly selected archive is never a
        // candidate, so a successful cleanup would leave ten old archives plus the new one.
        Directory.GetFiles(versions, "target_*.nmp2").Should().HaveCount(12);
    }

    [Fact]
    public async Task AppendAsync_MatchingExistingArchiveMembersAreVerifiedAndReused()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldArtifact = await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("target"));
        byte[] oldSidecar = await File.ReadAllBytesAsync(workspace.Store.MetaPath("target"));
        FileMemoryStore.AppendFaultInjector = (point, _, path) =>
        {
            if (point == AppendFaultPoint.ArchiveArtifactWrite)
                File.WriteAllBytes(path, oldArtifact);
            if (point == AppendFaultPoint.ArchiveSidecarWrite)
                File.WriteAllBytes(path, oldSidecar);
            return null;
        };

        try
        {
            await workspace.Store.AppendAsync(MemoryWrite.Create(
                MemoryAddress.Parse("/target"), "local", ["new"]));
        }
        finally
        {
            workspace.Store.ClearFaults();
        }

        // The callback-created destinations are the planned paths; the publication must accept
        // both because their raw bytes match the captured old members.
        var transaction = workspace.Store.LastPreparedAppendTransactionForTest!;
        transaction.Archive.ArtifactStatus.Should().Be(ArchiveMemberStatus.AlreadyPresentVerified);
        transaction.Archive.SidecarStatus.Should().Be(ArchiveMemberStatus.AlreadyPresentVerified);
    }

    [Fact]
    public async Task AppendAsync_MismatchingExistingArchiveMemberRequiresRecovery()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        FileMemoryStore.AppendFaultInjector = (point, _, path) =>
        {
            if (point == AppendFaultPoint.ArchiveArtifactWrite)
                File.WriteAllBytes(path, [0xff]);
            return null;
        };

        try
        {
            Func<Task> append = () => workspace.Store.AppendAsync(MemoryWrite.Create(
                MemoryAddress.Parse("/target"), "local", ["new"]));

            var exception = await append.Should().ThrowAsync<AppendStoreException>();
            exception.Which.Code.Should().Be(AppendStoreErrorCode.RecoveryRequired);
        }
        finally
        {
            workspace.Store.ClearFaults();
        }
    }

    [Fact]
    public async Task AppendAsync_JournalParseFailurePreservesTheCurrentOperationId()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        workspace.Store.FailOnceAt(AppendFaultPoint.ArchiveArtifactWrite);
        Func<Task> firstAppend = () => workspace.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["first"]));
        await firstAppend.Should().ThrowAsync<AppendStoreException>()
            .Where(exception => exception.Code == AppendStoreErrorCode.RecoveryRequired);

        string? observedOperationId = null;
        FileMemoryStore.AppendFaultInjector = (point, operationId, _) =>
        {
            if (point != AppendFaultPoint.JournalParse)
                return null;
            observedOperationId = operationId;
            return new IOException("journal parse fault");
        };

        try
        {
            var exception = await FluentActions.Invoking(async () =>
                    await workspace.Store.AppendAsync(
                        MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["second"])))
                .Should().ThrowAsync<AppendStoreException>();

            exception.Which.Code.Should().Be(AppendStoreErrorCode.RecoveryRequired);
            exception.Which.OperationId.Should().Be(observedOperationId);
            exception.Which.OperationId.Should().NotBe("recovery");
        }
        finally
        {
            workspace.Store.ClearFaults();
        }
    }

    [Fact]
    public async Task AppendAsync_MissingMemoryUsesStoreCreateSemanticsWithJournalAdmission()
    {
        using var workspace = new TempWorkspace();

        AppendStoreResult result = await workspace.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/missing"), "local", ["body"]));

        result.Outcome.Should().Be(AppendStoreOutcome.Created);
        Directory.Exists(Path.Combine(workspace.Root, ".scrinia", "memories", "versions"))
            .Should().BeTrue();
        File.Exists(Path.Combine(workspace.Root, ".scrinia", "memories", "versions", ".append-journal.json"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task RecoverAppendAsync_RejectsACommittedPairWithTheWrongRecordedHash()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        workspace.Store.FailOnceAt(AppendFaultPoint.ArchiveCleanup);

        (await workspace.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
            .Outcome.Should().Be(AppendStoreOutcome.SuccessWithWarning);

        string journalPath = Path.Combine(workspace.Store.GetStoreDirForScope("local"), "versions",
            AppendJournal.FileName);
        string journal = await File.ReadAllTextAsync(journalPath);
        const string committedHashPrefix = "\"committedHash\":\"";
        int hashStart = journal.IndexOf(committedHashPrefix, StringComparison.Ordinal);
        hashStart.Should().BeGreaterOrEqualTo(0);
        hashStart += committedHashPrefix.Length;
        int hashEnd = journal.IndexOf('"', hashStart);
        journal = journal[..hashStart] + new string('0', 64) + journal[hashEnd..];
        await File.WriteAllTextAsync(journalPath, journal);

        Func<Task> recover = () => workspace.Store.RecoverAppendAsync("local");
        var exception = await recover.Should().ThrowAsync<AppendStoreException>();
        exception.Which.Code.Should().Be(AppendStoreErrorCode.RecoveryRequired);
        exception.Which.Message.Should().Contain("committed append hash");
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "scrinia-append-transaction-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Store = new FileMemoryStore(Root);
        }

        public string Root { get; }
        public FileMemoryStore Store { get; }

        public void Dispose()
        {
            Store.Dispose();
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
