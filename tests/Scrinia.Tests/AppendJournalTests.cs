using System.Security.Cryptography;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed class AppendJournalTests
{
    [Fact]
    public void PreparedJournal_RoundTripsRawTransactionBytesAndHashes()
    {
        var transaction = CreateTransaction();

        AppendJournal journal = AppendJournalCodec.CreatePrepared(transaction);
        AppendJournal parsed = AppendJournalCodec.ParseLegacyJournal(
            AppendJournalCodec.Serialize(journal), transaction.Scope, "journal.json");

        parsed.State.Should().Be(AppendJournalState.Prepared);
        parsed.OperationId.Should().Be(transaction.OperationId);
        parsed.OldArtifact.ToSnapshot("journal.json").Bytes.Should().Equal(transaction.OldArtifact.Bytes);
        parsed.NewSidecar.Sha256.Should().Be(transaction.NewSidecar.Sha256);
    }

    [Fact]
    public void AppendAdapter_RoundTripsCommonEnvelopeWithoutDroppingAppendState()
    {
        AppendJournal journal = AppendJournalCodec.CreatePrepared(CreateTransaction());
        var adapter = new AppendRecoveryAdapter();

        RecoveryEnvelopeDocument envelope = adapter.ToEnvelope(journal);
        AppendJournal restored = adapter.FromEnvelope(envelope, "versions/.append-journal.json");

        restored.Should().BeEquivalentTo(journal);
    }

    [Fact]
    public void AppendAdapter_NormalizesLegacyEvidenceWithoutRewritingItsLocation()
    {
        AppendJournal journal = AppendJournalCodec.CreatePrepared(CreateTransaction());
        var adapter = new AppendRecoveryAdapter();

        RecoveryEnvelopeDocument envelope = adapter.ConsumeLegacy(
            AppendJournalCodec.Serialize(journal), "local", "versions/.append-journal.json", ".");

        envelope.IsLegacy.Should().BeTrue();
        adapter.FromEnvelope(envelope, "versions/.append-journal.json").Should().BeEquivalentTo(journal);
    }

    [Fact]
    public void AppendAdapter_RejectsMemberPathsOutsideTheEvidenceStore()
    {
        string storeDirectory = Path.Combine(Path.GetTempPath(), "scrinia-append-adapter-" + Guid.NewGuid().ToString("N"));
        string evidencePath = Path.Combine(storeDirectory, "versions", AppendJournal.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        try
        {
            AppendTransaction transaction = CreateTransaction() with
            {
                OldArtifact = Snapshot(Path.Combine(storeDirectory, "..", "outside.nmp2"), [1, 2, 3])
            };
            AppendJournal journal = AppendJournalCodec.CreatePrepared(transaction);

            Action parse = () => new AppendRecoveryAdapter().FromEnvelope(
                AppendJournalCodec.ToEnvelope(journal), evidencePath);

            parse.Should().Throw<AppendJournalException>()
                .Which.Failure.Should().Be(AppendJournalFailure.Malformed);
        }
        finally
        {
            try { Directory.Delete(storeDirectory, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(AppendJournalState.Prepared)]
    [InlineData(AppendJournalState.ArchiveInProgress)]
    [InlineData(AppendJournalState.Archived)]
    [InlineData(AppendJournalState.NewPairInProgress)]
    [InlineData(AppendJournalState.Committed)]
    [InlineData(AppendJournalState.CleanupPending)]
    [InlineData(AppendJournalState.RecoveryRequired)]
    [InlineData(AppendJournalState.RolledBack)]
    public void AppendAdapter_PreservesEveryOperationState(AppendJournalState state)
    {
        AppendJournal journal = AppendJournalCodec.CreatePrepared(CreateTransaction()) with { State = state };

        AppendJournal restored = new AppendRecoveryAdapter().FromEnvelope(
            AppendJournalCodec.ToEnvelope(journal), "versions/.append-journal.json");

        restored.State.Should().Be(state);
    }

    [Fact]
    public void JournalWithChangedPayload_IsRejectedAsRecoveryRequired()
    {
        var transaction = CreateTransaction();
        AppendJournal journal = AppendJournalCodec.CreatePrepared(transaction);
        string document = System.Text.Encoding.UTF8.GetString(AppendJournalCodec.Serialize(journal));
        byte[] json = System.Text.Encoding.UTF8.GetBytes(document.Replace("AQID", "AQIE", StringComparison.Ordinal));

        Action parse = () => AppendJournalCodec.ParseLegacyJournal(json, transaction.Scope, "journal.json");

        parse.Should().Throw<AppendJournalException>()
            .Which.Failure.Should().Be(AppendJournalFailure.IntegrityFailed);
    }

    [Fact]
    public void JournalStatesAreExplicitAndMonotonic()
    {
        Enum.GetValues<AppendJournalState>().Should().Equal(
            AppendJournalState.Prepared,
            AppendJournalState.ArchiveInProgress,
            AppendJournalState.Archived,
            AppendJournalState.NewPairInProgress,
            AppendJournalState.Committed,
            AppendJournalState.CleanupPending,
            AppendJournalState.RecoveryRequired,
            AppendJournalState.RolledBack);
    }

    [Fact]
    public void WithState_RejectsSkippingTheDurableCommitOrder()
    {
        AppendJournal journal = AppendJournalCodec.CreatePrepared(CreateTransaction());

        Action transition = () => AppendJournalCodec.WithState(journal, AppendJournalState.Committed);

        transition.Should().Throw<AppendJournalException>();
    }

    [Theory]
    [InlineData(AppendJournalState.Committed, AppendJournalState.RolledBack)]
    [InlineData(AppendJournalState.CleanupPending, AppendJournalState.RolledBack)]
    [InlineData(AppendJournalState.RolledBack, AppendJournalState.Prepared)]
    [InlineData(AppendJournalState.RecoveryRequired, AppendJournalState.Prepared)]
    public void WithState_RejectsIllegalTerminalTransitions(
        AppendJournalState current, AppendJournalState next)
    {
        AppendJournal journal = AppendJournalCodec.CreatePrepared(CreateTransaction()) with { State = current };

        Action transition = () => AppendJournalCodec.WithState(journal, next);

        transition.Should().Throw<AppendJournalException>();
    }

    [Fact]
    public void ParseAndValidate_RejectsOversizedJournalBeforeJsonAllocation()
    {
        byte[] oversized = new byte[checked((int)RestoreJournalCodec.MaxJournalJsonBytes + 1)];

        Action parse = () => AppendJournalCodec.ParseLegacyJournal(oversized, "local", "journal.json");

        parse.Should().Throw<AppendJournalException>()
            .Which.Failure.Should().Be(AppendJournalFailure.Malformed);
    }

    [Fact]
    public void ParseAndValidate_RejectsMembersOutsideTheExpectedStoreLayout()
    {
        string storeDirectory = Path.Combine(Path.GetTempPath(), "scrinia-journal-layout");
        AppendTransaction transaction = CreateTransaction() with
        {
            OldArtifact = Snapshot(Path.Combine(storeDirectory, "other", "target.nmp2"), [1, 2, 3])
        };

        AppendJournal journal = AppendJournalCodec.CreatePrepared(transaction);
        Action parse = () => AppendJournalCodec.ParseLegacyJournal(
            AppendJournalCodec.Serialize(journal), "local", "journal.json", storeDirectory);

        parse.Should().Throw<AppendJournalException>()
            .Which.Failure.Should().Be(AppendJournalFailure.Malformed);
    }

    [Fact]
    public void ParseAndValidate_RejectsMalformedAddressAsRecoveryRequired()
    {
        AppendJournal journal = AppendJournalCodec.CreatePrepared(CreateTransaction()) with
        {
            Address = "not a logical address"
        };

        Action parse = () => AppendJournalCodec.ParseLegacyJournal(
            AppendJournalCodec.Serialize(journal), "local", "journal.json");

        parse.Should().Throw<AppendJournalException>()
            .Which.Failure.Should().Be(AppendJournalFailure.Malformed);
    }

    [Theory]
    [InlineData("\"payload\":\"AQID\"", "\"payload\":null")]
    [InlineData("\"path\":\"old.nmp2\"", "\"path\":null")]
    [InlineData("\"address\":\"/target\"", "\"address\":null")]
    public void ParseAndValidate_NormalizesNullMemberFieldsAndAddress(string original, string replacement)
    {
        AppendJournal journal = AppendJournalCodec.CreatePrepared(CreateTransaction());
        string document = System.Text.Encoding.UTF8.GetString(AppendJournalCodec.Serialize(journal));
        byte[] json = System.Text.Encoding.UTF8.GetBytes(document.Replace(original, replacement, StringComparison.Ordinal));

        Action parse = () => AppendJournalCodec.ParseLegacyJournal(json, "local", "journal.json");

        parse.Should().Throw<AppendJournalException>()
            .Which.Failure.Should().Be(AppendJournalFailure.Malformed);
    }

    [Fact]
    public void ParseAndValidate_RejectsMalformedPayloadWithValidHash()
    {
        AppendJournal journal = AppendJournalCodec.CreatePrepared(CreateTransaction());
        string document = System.Text.Encoding.UTF8.GetString(AppendJournalCodec.Serialize(journal));
        document = document.Replace("\"payload\":\"AQID\"", "\"payload\":\"not-base64\"", StringComparison.Ordinal);

        Action parse = () => AppendJournalCodec.ParseLegacyJournal(
            System.Text.Encoding.UTF8.GetBytes(document), "local", "journal.json");

        parse.Should().Throw<AppendJournalException>()
            .Which.Failure.Should().Be(AppendJournalFailure.IntegrityFailed);
    }

    [Fact]
    public void ParseAndValidate_RejectsInvalidHashBeforePayloadDecode()
    {
        AppendJournal journal = AppendJournalCodec.CreatePrepared(CreateTransaction());
        string document = System.Text.Encoding.UTF8.GetString(AppendJournalCodec.Serialize(journal))
            .Replace($"\"sha256\":\"{journal.OldArtifact.Sha256}\"", "\"sha256\":\"invalid-hash\"", StringComparison.Ordinal);

        Action parse = () => AppendJournalCodec.ParseLegacyJournal(
            System.Text.Encoding.UTF8.GetBytes(document), "local", "journal.json");

        parse.Should().Throw<AppendJournalException>()
            .Which.Failure.Should().Be(AppendJournalFailure.Malformed);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"not-an-archive-member\"")]
    public void ParseAndValidate_RejectsInvalidPruneCandidateBeforePathOperations(string candidate)
    {
        AppendJournal journal = AppendJournalCodec.CreatePrepared(CreateTransaction());
        string document = System.Text.Encoding.UTF8.GetString(AppendJournalCodec.Serialize(journal))
            .Replace("[\"stale.nmp2\"]", $"[{candidate}]", StringComparison.Ordinal);

        Action parse = () => AppendJournalCodec.ParseLegacyJournal(
            System.Text.Encoding.UTF8.GetBytes(document), "local", "journal.json", "store");

        parse.Should().Throw<AppendJournalException>()
            .Which.Failure.Should().Be(AppendJournalFailure.Malformed);
    }

    [Fact]
    public void ParseAndValidate_NormalizesNullHash()
    {
        AppendJournal journal = AppendJournalCodec.CreatePrepared(CreateTransaction());
        string document = System.Text.Encoding.UTF8.GetString(AppendJournalCodec.Serialize(journal))
            .Replace($"\"sha256\":\"{journal.OldArtifact.Sha256}\"", "\"sha256\":null", StringComparison.Ordinal);

        Action parse = () => AppendJournalCodec.ParseLegacyJournal(
            System.Text.Encoding.UTF8.GetBytes(document), "local", "journal.json");

        parse.Should().Throw<AppendJournalException>()
            .Which.Failure.Should().Be(AppendJournalFailure.Malformed);
    }

    [Fact]
    public async Task AppendAsync_RemovesTheJournalOnlyAfterTheCommitBoundary()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-append-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var store = new FileMemoryStore(root);
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));

        AppendStoreResult result = await store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

        result.Outcome.Should().Be(AppendStoreOutcome.Appended);
        string journal = Path.Combine(store.GetStoreDirForScope("local"), "versions",
            AppendJournal.FileName);
        File.Exists(journal).Should().BeFalse();
        try { Directory.Delete(root, true); } catch { }
    }

    private static AppendTransaction CreateTransaction()
    {
        byte[] oldArtifact = [1, 2, 3];
        byte[] oldSidecar = [4, 5];
        byte[] newArtifact = [6, 7, 8];
        byte[] newSidecar = [9, 10];
        return new AppendTransaction(
            "0123456789abcdef0123456789abcdef",
            MemoryAddress.Parse("/target"),
            "local",
            false,
            Snapshot("old.nmp2", oldArtifact),
            Snapshot("old.meta.json", oldSidecar),
            Snapshot("new.nmp2", newArtifact),
            Snapshot("new.meta.json", newSidecar),
            new ArchivePlan(true, "archive.nmp2", "archive.meta.json",
                ArchiveMemberStatus.Planned, ArchiveMemberStatus.Planned, ["stale.nmp2"]),
            DateTimeOffset.Parse("2026-09-08T00:00:00Z"));
    }

    private static RawMemberSnapshot Snapshot(string path, byte[] bytes) =>
        new(path, true, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
}
