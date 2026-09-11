using FluentAssertions;
using System.Text.Json.Nodes;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed class AppendRecoveryTests
{
    [Fact]
    public async Task NullAppendIsCorrelatedWithAnOperationId()
    {
        using var store = new FileMemoryStore(Path.Combine(Path.GetTempPath(), "scrinia-null-" + Guid.NewGuid().ToString("N")));

        var exception = await FluentActions.Invoking(() => store.AppendAsync(null!))
            .Should().ThrowAsync<AppendStoreException>();

        exception.Which.OperationId.Should().NotBeNullOrWhiteSpace();
        exception.Which.Code.Should().Be(AppendStoreErrorCode.Internal);
    }

    [Fact]
    public async Task AppendCapturesArtifactAndSidecarBytesBeforePublication()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-append-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var store = new FileMemoryStore(root);
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldArtifact = await File.ReadAllBytesAsync(store.ArtifactPath("target"));
        byte[] oldSidecar = await File.ReadAllBytesAsync(store.MetaPath("target"));
        try
        {
            AppendStoreResult result = await store.AppendAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

            result.Outcome.Should().Be(AppendStoreOutcome.Appended);
            store.LastCapturedOldArtifactBytesForTest.Should().Equal(oldArtifact);
            store.LastCapturedOldSidecarBytesForTest.Should().Equal(oldSidecar);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task WholeMemoryRecoveryEvidenceUsesTheCommonEnvelopeAtTheLegacyAppendLocation()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        FileMemoryStore.WholeMemoryFaultInjector = (point, _) => point switch
        {
            WholeMemoryFaultPoint.ArchiveArtifactWrite or WholeMemoryFaultPoint.RollbackWrite =>
                new IOException("Injected whole-memory recovery fault."),
            _ => null
        };

        try
        {
            await FluentActions.Invoking(() => workspace.Store.StoreAsync(
                    MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
                .Should().ThrowAsync<WholeMemoryTransactionException>();
        }
        finally
        {
            workspace.Store.ClearFaults();
        }

        string journalPath = Path.Combine(workspace.Store.GetStoreDirForScope("local"), "versions",
            AppendJournal.FileName);
        JsonObject journal = JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!.AsObject();

        journal["format"]!.GetValue<string>().Should().Be(RecoveryEnvelopeCodec.Format);
        journal["operation"]!.GetValue<string>().Should().Be("append");
        journal["state"]!.GetValue<string>().Should().Be("applying");
    }

    [Theory]
    [InlineData((int)AppendFaultPoint.RawArtifactRead, AppendStoreErrorCode.Internal)]
    [InlineData((int)AppendFaultPoint.RawSidecarRead, AppendStoreErrorCode.Internal)]
    [InlineData((int)AppendFaultPoint.JournalWrite, AppendStoreErrorCode.Internal)]
    [InlineData((int)AppendFaultPoint.JournalFlush, AppendStoreErrorCode.Internal)]
    [InlineData((int)AppendFaultPoint.ArchiveArtifactFlush, AppendStoreErrorCode.RecoveryRequired)]
    [InlineData((int)AppendFaultPoint.ArchiveSidecarFlush, AppendStoreErrorCode.RecoveryRequired)]
    [InlineData((int)AppendFaultPoint.LiveArtifactFlush, AppendStoreErrorCode.Internal)]
    [InlineData((int)AppendFaultPoint.LiveSidecarWrite, AppendStoreErrorCode.Internal)]
    [InlineData((int)AppendFaultPoint.LiveSidecarFlush, AppendStoreErrorCode.Internal)]
    [InlineData((int)AppendFaultPoint.VerificationRead, AppendStoreErrorCode.Internal)]
    public async Task InjectedAppendBoundaryFailurePreservesTheOldPairAndOperationAttribution(
        int pointValue, AppendStoreErrorCode expectedCode)
    {
        AppendFaultPoint point = (AppendFaultPoint)pointValue;
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(
            MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldArtifact = await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("target"));
        byte[] oldSidecar = await File.ReadAllBytesAsync(workspace.Store.MetaPath("target"));
        string? injectedOperationId = null;
        FileMemoryStore.AppendFaultInjector = (faultPoint, operationId, _) =>
        {
            if (faultPoint == point)
            {
                injectedOperationId = operationId;
                return new IOException($"Injected {point} failure.");
            }
            return null;
        };

        try
        {
            Exception exception = (await FluentActions.Invoking(() => workspace.Store.AppendAsync(
                    MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
                .Should().ThrowAsync<Exception>()).Which;

            string? operationId = exception switch
            {
                AppendStoreException append => append.OperationId,
                DurableWriteException durable => durable.AppendOperationId,
                _ => null
            };
            operationId.Should().NotBeNullOrWhiteSpace();
            operationId.Should().Be(injectedOperationId,
                because: "every deterministic append boundary must retain its operation correlation");
            if (exception is AppendStoreException appendException)
                appendException.Code.Should().Be(expectedCode);
            else
                expectedCode.Should().Be(AppendStoreErrorCode.Internal);

            (await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("target"))).Should().Equal(oldArtifact);
            (await File.ReadAllBytesAsync(workspace.Store.MetaPath("target"))).Should().Equal(oldSidecar);
        }
        finally
        {
            workspace.Store.ClearFaults();
        }
    }

    [Fact]
    public async Task CancellationDuringArchiveRollsBackAndPreservesOperationId()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-append-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var store = new FileMemoryStore(root);
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldArtifact = await File.ReadAllBytesAsync(store.ArtifactPath("target"));
        byte[] oldSidecar = await File.ReadAllBytesAsync(store.MetaPath("target"));
        using var cancellation = new CancellationTokenSource();
        FileMemoryStore.AppendFaultInjector = (point, _, _) =>
        {
            if (point != AppendFaultPoint.ArchiveArtifactWrite)
                return null;
            cancellation.Cancel();
            return new OperationCanceledException("cancelled");
        };

        try
        {
            Func<Task> append = () => store.AppendAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]), cancellation.Token);

            AppendOperationCanceledException exception = (await append.Should()
                .ThrowAsync<AppendOperationCanceledException>()).Which;
            exception.OperationId.Should().NotBeNullOrWhiteSpace();
            exception.OperationId.Should().Be(store.LastPreparedAppendTransactionForTest!.OperationId);
            exception.InnerException.Should().NotBeNull();
            (await File.ReadAllBytesAsync(store.ArtifactPath("target"))).Should().Equal(oldArtifact);
            (await File.ReadAllBytesAsync(store.MetaPath("target"))).Should().Equal(oldSidecar);
            Directory.GetFiles(Path.Combine(store.GetStoreDirForScope("local"), "versions"), "target_*")
                .Should().BeEmpty();
        }
        finally
        {
            store.ClearFaults();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task CancellationDuringLivePublicationRollsBackAndPreservesOperationId()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldArtifact = await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("target"));
        byte[] oldSidecar = await File.ReadAllBytesAsync(workspace.Store.MetaPath("target"));
        using var cancellation = new CancellationTokenSource();
        FileMemoryStore.AppendFaultInjector = (point, _, _) =>
        {
            if (point != AppendFaultPoint.LiveArtifactWrite)
                return null;
            cancellation.Cancel();
            return new OperationCanceledException("cancelled");
        };

        try
        {
            AppendOperationCanceledException exception = (await FluentActions.Invoking(() =>
                    workspace.Store.AppendAsync(
                        MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]),
                        cancellation.Token))
                .Should().ThrowAsync<AppendOperationCanceledException>()).Which;

            exception.OperationId.Should().NotBeNullOrWhiteSpace();
            exception.OperationId.Should().Be(workspace.Store.LastPreparedAppendTransactionForTest!.OperationId);
            exception.InnerException.Should().NotBeNull();
            (await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("target"))).Should().Equal(oldArtifact);
            (await File.ReadAllBytesAsync(workspace.Store.MetaPath("target"))).Should().Equal(oldSidecar);
        }
        finally
        {
            workspace.Store.ClearFaults();
        }
    }

    [Theory]
    [InlineData((int)AppendFaultPoint.ArchiveArtifactWrite, AppendStoreErrorCode.RecoveryRequired)]
    [InlineData((int)AppendFaultPoint.LiveArtifactWrite, AppendStoreErrorCode.Internal)]
    [InlineData((int)AppendFaultPoint.VerificationRead, AppendStoreErrorCode.Internal)]
    public async Task InjectedOperationCanceledExceptionUsesFaultClassification(
        int pointValue, AppendStoreErrorCode expectedCode)
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        byte[] oldArtifact = await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("target"));
        byte[] oldSidecar = await File.ReadAllBytesAsync(workspace.Store.MetaPath("target"));
        AppendFaultPoint point = (AppendFaultPoint)pointValue;
        FileMemoryStore.AppendFaultInjector = (faultPoint, _, _) =>
            faultPoint == point ? new OperationCanceledException("injected") : null;

        try
        {
            AppendStoreException exception = (await FluentActions.Invoking(() =>
                    workspace.Store.AppendAsync(
                        MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
                .Should().ThrowAsync<AppendStoreException>()).Which;

            exception.Code.Should().Be(expectedCode);
            exception.OperationId.Should().NotBeNullOrWhiteSpace();
            exception.OperationId.Should().Be(workspace.Store.LastPreparedAppendTransactionForTest!.OperationId);
            (exception.InnerException is OperationCanceledException ||
             exception.InnerException?.InnerException is OperationCanceledException).Should().BeTrue();
            (await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("target"))).Should().Equal(oldArtifact);
            (await File.ReadAllBytesAsync(workspace.Store.MetaPath("target"))).Should().Equal(oldSidecar);
        }
        finally
        {
            workspace.Store.ClearFaults();
        }
    }

    [Fact]
    public async Task RecoveryCompletesRecordedArchiveMembersAndIsIdempotent()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        workspace.Store.FailOnceAt(AppendFaultPoint.ArchiveSidecarWrite);

        await FluentActions.Invoking(() => workspace.Store.AppendAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
            .Should().ThrowAsync<AppendStoreException>()
            .Where(exception => exception.Code == AppendStoreErrorCode.RecoveryRequired);

        workspace.Store.ClearFaults();
        await workspace.Store.RecoverAppendAsync("local");
        await workspace.Store.RecoverAppendAsync("local");

        File.Exists(Path.Combine(workspace.Store.GetStoreDirForScope("local"), "versions",
            AppendJournal.FileName)).Should().BeFalse();
        string artifact = await workspace.Store.ReadArtifactAsync(MemoryAddress.Parse("/target"));
        Nmp2ChunkedEncoder.GetChunkCount(artifact).Should().Be(1);
    }

    [Fact]
    public async Task MissingTargetRecoversRetainedCreateJournalBeforeCreating()
    {
        using var workspace = new TempWorkspace();
        FileMemoryStore.AppendFaultInjector = (point, _, _) => point switch
        {
            AppendFaultPoint.LiveSidecarWrite => new IOException("Retain failed create journal."),
            AppendFaultPoint.RollbackDelete => new IOException("Retain failed create rollback."),
            _ => null
        };

        await FluentActions.Invoking(() => workspace.Store.AppendAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/new-target"), "local", ["created"])))
            .Should().ThrowAsync<AppendStoreException>()
            .Where(exception => exception.Code == AppendStoreErrorCode.RecoveryRequired);

        string journalPath = Path.Combine(workspace.Store.GetStoreDirForScope("local"), "versions",
            AppendJournal.FileName);
        File.Exists(journalPath).Should().BeTrue();
        File.Delete(workspace.Store.ArtifactPath("new-target"));
        File.Delete(workspace.Store.MetaPath("new-target"));
        workspace.Store.ClearFaults();

        AppendStoreResult result = await workspace.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/new-target"), "local", ["created"]));

        result.Outcome.Should().Be(AppendStoreOutcome.Created);
        File.Exists(journalPath).Should().BeFalse();
        File.Exists(workspace.Store.ArtifactPath("new-target")).Should().BeTrue();
    }

    [Theory]
    [InlineData("payload", null)]
    [InlineData("payload", "not-base64")]
    [InlineData("sha256", null)]
    [InlineData("sha256", "invalid-hash")]
    [InlineData("path", null)]
    [InlineData("address", null)]
    [InlineData("address", "not a logical address")]
    public async Task MalformedJournalFieldsBecomeTypedRecoveryRequired(
        string field, string? value)
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        workspace.Store.FailOnceAt(AppendFaultPoint.ArchiveCleanup);

        (await workspace.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
            .Outcome.Should().Be(AppendStoreOutcome.SuccessWithWarning);

        string journalPath = Path.Combine(workspace.Store.GetStoreDirForScope("local"), "versions",
            AppendJournal.FileName);
        JsonObject journal = JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!.AsObject();
        string operationId = journal["operationId"]!.GetValue<string>();
        JsonObject target = field is "address" ? journal : journal["body"]!["oldArtifact"]!.AsObject();
        target[field] = value is null ? null : JsonValue.Create(value);
        await File.WriteAllTextAsync(journalPath, journal.ToJsonString());

        var exception = await FluentActions.Invoking(() => workspace.Store.RecoverAppendAsync("local"))
            .Should().ThrowAsync<AppendStoreException>();

        exception.Which.Code.Should().Be(AppendStoreErrorCode.RecoveryRequired);
        exception.Which.OperationId.Should().Be(operationId);
    }

    [Fact]
    public async Task InvalidPruneCandidateBecomesTypedRecoveryRequiredWithJournalOperationId()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        workspace.Store.FailOnceAt(AppendFaultPoint.ArchiveCleanup);

        (await workspace.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
            .Outcome.Should().Be(AppendStoreOutcome.SuccessWithWarning);

        string journalPath = Path.Combine(workspace.Store.GetStoreDirForScope("local"), "versions",
            AppendJournal.FileName);
        JsonObject journal = JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!.AsObject();
        string operationId = journal["operationId"]!.GetValue<string>();
        journal["pruneCandidates"] = new JsonArray(new JsonNode?[] { null });
        await File.WriteAllTextAsync(journalPath, journal.ToJsonString());

        var exception = await FluentActions.Invoking(() => workspace.Store.RecoverAppendAsync("local"))
            .Should().ThrowAsync<AppendStoreException>();

        exception.Which.Code.Should().Be(AppendStoreErrorCode.RecoveryRequired);
        exception.Which.OperationId.Should().Be(operationId);
    }

    [Fact]
    public async Task RecoveryArchiveReplayFailureUsesTheJournalOperationId()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        workspace.Store.FailOnceAt(AppendFaultPoint.ArchiveArtifactWrite);

        await FluentActions.Invoking(() => workspace.Store.AppendAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
            .Should().ThrowAsync<AppendStoreException>();

        string journalPath = Path.Combine(workspace.Store.GetStoreDirForScope("local"), "versions",
            AppendJournal.FileName);
        string operationId = JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!["operationId"]!.GetValue<string>();
        workspace.Store.FailPersistentlyAt(AppendFaultPoint.ArchiveArtifactWrite);

        var exception = await FluentActions.Invoking(() => workspace.Store.RecoverAppendAsync("local"))
            .Should().ThrowAsync<AppendStoreException>();

        exception.Which.Code.Should().Be(AppendStoreErrorCode.RecoveryRequired);
        exception.Which.OperationId.Should().Be(operationId);
    }

    [Fact]
    public async Task RecoveryRollbackFailureUsesTheJournalOperationId()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        workspace.Store.FailOnceAt(AppendFaultPoint.ArchiveArtifactWrite);

        await FluentActions.Invoking(() => workspace.Store.AppendAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
            .Should().ThrowAsync<AppendStoreException>();

        string journalPath = Path.Combine(workspace.Store.GetStoreDirForScope("local"), "versions",
            AppendJournal.FileName);
        JsonObject journal = JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!.AsObject();
        string operationId = journal["operationId"]!.GetValue<string>();
        byte[] newArtifact = Convert.FromBase64String(journal["body"]!["newArtifact"]!["payload"]!.GetValue<string>());
        await File.WriteAllBytesAsync(workspace.Store.ArtifactPath("target"), newArtifact);
        workspace.Store.FailPersistentlyAt(AppendFaultPoint.RollbackWrite);

        var exception = await FluentActions.Invoking(() => workspace.Store.RecoverAppendAsync("local"))
            .Should().ThrowAsync<AppendStoreException>();

        exception.Which.Code.Should().Be(AppendStoreErrorCode.RecoveryRequired);
        exception.Which.OperationId.Should().Be(operationId);
    }

    [Fact]
    public async Task RecoveryFinalizationFailureUsesTheJournalOperationId()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        workspace.Store.FailOnceAt(AppendFaultPoint.ArchiveArtifactWrite);

        await FluentActions.Invoking(() => workspace.Store.AppendAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
            .Should().ThrowAsync<AppendStoreException>();

        string journalPath = Path.Combine(workspace.Store.GetStoreDirForScope("local"), "versions",
            AppendJournal.FileName);
        string operationId = JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!["operationId"]!.GetValue<string>();
        workspace.Store.FailPersistentlyAt(AppendFaultPoint.JournalDelete);

        var exception = await FluentActions.Invoking(() => workspace.Store.RecoverAppendAsync("local"))
            .Should().ThrowAsync<AppendStoreException>();

        exception.Which.Code.Should().Be(AppendStoreErrorCode.RecoveryRequired);
        exception.Which.OperationId.Should().Be(operationId);
    }

    [Fact]
    public async Task RecoveryDoesNotOverwriteAnUnrelatedCommittedLiveHash()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(
            MemoryAddress.Parse("/target"), "local", ["old"]));
        workspace.Store.FailOnceAt(AppendFaultPoint.ArchiveCleanup);

        AppendStoreResult result = await workspace.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));
        result.Outcome.Should().Be(AppendStoreOutcome.SuccessWithWarning);

        string journalPath = Path.Combine(workspace.Store.GetStoreDirForScope("local"), "versions",
            AppendJournal.FileName);
        byte[] unrelatedArtifact = [0x01, 0x02, 0x03];
        await File.WriteAllBytesAsync(workspace.Store.ArtifactPath("target"), unrelatedArtifact);

        var exception = await FluentActions.Invoking(() => workspace.Store.RecoverAppendAsync("local"))
            .Should().ThrowAsync<AppendStoreException>();

        exception.Which.Code.Should().Be(AppendStoreErrorCode.RecoveryRequired);
        File.Exists(journalPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(workspace.Store.ArtifactPath("target"))).Should().Equal(unrelatedArtifact);
    }

    [Fact]
    public async Task RecoveryMissingArchiveMemberRecreatesTheExactRecordedMember()
    {
        using var workspace = new TempWorkspace();
        await workspace.Store.StoreAsync(MemoryWrite.Create(
            MemoryAddress.Parse("/target"), "local", ["old"]));
        workspace.Store.FailOnceAt(AppendFaultPoint.ArchiveSidecarWrite);

        await FluentActions.Invoking(() => workspace.Store.AppendAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
            .Should().ThrowAsync<AppendStoreException>();

        AppendTransaction transaction = workspace.Store.LastPreparedAppendTransactionForTest!;
        byte[] oldArtifact = transaction.OldArtifact.Bytes.ToArray();
        File.Delete(transaction.Archive.ArtifactArchivePath!);
        workspace.Store.ClearFaults();

        await workspace.Store.RecoverAppendAsync("local");

        (await File.ReadAllBytesAsync(transaction.Archive.ArtifactArchivePath!)).Should().Equal(oldArtifact);
        File.Exists(Path.Combine(workspace.Store.GetStoreDirForScope("local"), "versions",
            AppendJournal.FileName)).Should().BeFalse();
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "scrinia-append-recovery-workspace-" + Guid.NewGuid().ToString("N"));
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
