using Scrinia.Core;
using Scrinia.Core.Migration;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public class B0ResultWrapperTests
{
    [Fact]
    public void FromAppend_MapsOutcomeWarningAndCorrelation()
    {
        ArtifactEntry entry = new(
            MemoryAddress.Parse("/notes/candidate"),
            "artifacts/notes/candidate.nmp2", 3, 1, DateTimeOffset.UtcNow);
        AppendStoreResult append = new(AppendStoreOutcome.Appended, entry, "op-append");
        B0Result b0 = B0ResultWrappers.FromAppend(append);

        Assert.Equal(B0Outcome.Appended, b0.Outcome);
        Assert.Equal("op-append", b0.OperationId);
        Assert.Equal("op-append", b0.CorrelationId);
        Assert.Same(entry, b0.Artifact);
        Assert.Null(b0.Failure);
        Assert.Null(b0.Recovery);
    }

    [Fact]
    public void FromWhole_MapsRecoveryIntoSharedRecoveryInfo()
    {
        WholeMemoryResult whole = new(
            WholeMemoryOutcome.RecoveryRequired, null, "op-whole",
            "sidecar newer", new RecoveryInfo("/evid", "recoveryRequired", "resolve", "blocked"));
        B0Result b0 = B0ResultWrappers.FromWhole(whole);

        Assert.Equal(B0Outcome.RecoveryRequired, b0.Outcome);
        Assert.Equal("op-whole", b0.CorrelationId);
        Assert.Equal("sidecar newer", b0.Warning);
        Assert.Equal("/evid", b0.Recovery!.EvidencePath);
    }

    [Fact]
    public void FromMulti_RecordsFailureTaxonomyAndCollapsesTransactionId()
    {
        MultiMemoryAggregateResult multi = new(
            "tx-multi", "notes", MultiMemoryOutcome.Committed,
            Array.Empty<MultiMemoryParticipantOutcome>(), null, null, null);
        B0Result b0 = B0ResultWrappers.FromMulti(multi);

        Assert.Equal(B0Outcome.Committed, b0.Outcome);
        Assert.Equal("tx-multi", b0.OperationId);
        Assert.Equal("tx-multi", b0.CorrelationId);
    }

    [Fact]
    public void FromRecovery_MapsBlockReasonIntoB0Failure()
    {
        RecoveryBlock block = new("notes", "/evid", RecoveryOperation.Append, "op-r",
            RecoveryBlockReason.WrongScope, "scope mismatch");
        RecoveryOutcome outcome = new(true, false, false, "notes", "/evid", "op-r", block, null);
        B0Result b0 = B0ResultWrappers.FromRecovery(outcome);

        Assert.Equal(B0Outcome.RecoveryRequired, b0.Outcome);
        Assert.NotNull(b0.Failure);
        Assert.Equal(B0FailureTaxonomy.RecoveryBlock, b0.Failure!.Taxonomy);
        Assert.Equal("WrongScope", b0.Failure.Code);
    }

    [Fact]
    public void B0Exception_WrapsExistingExceptionTypeUnchanged()
    {
        AppendStoreException appendEx = new(AppendStoreErrorCode.Internal, "op", "nope");
        B0Exception b0ex = B0Exception.Of(appendEx);
        Assert.Same(appendEx, b0ex.Source);
        Assert.Equal("nope", b0ex.Source.Message);
    }
}

public class ImportMemoryResultWrapperTests
{
    [Fact]
    public void ImportMemoryResult_UsesWholeMemoryOutcomeAndCarriesRecovery()
    {
        var recovery = new RecoveryInfo("/scope/.restore-journal.json", "recoveryRequired",
            "resolve retained restore journal", "completed-journal cleanup deferred");
        ImportMemoryResult import = new(
            WholeMemoryOutcome.Replaced,
            new ArtifactEntry(MemoryAddress.Parse("/api/flow"), "artifacts/api/flow.nmp2", 1, 12, DateTimeOffset.UtcNow),
            "47bf1102-0000-0000-0000-000000000000",
            "Import committed; completed-journal cleanup deferred.",
            recovery);

        Assert.Equal(WholeMemoryOutcome.Replaced, import.Outcome);
        Assert.Equal("47bf1102-0000-0000-0000-000000000000", import.OperationId);
        Assert.Same(recovery, import.Recovery);
    }

    [Theory]
    [InlineData((int)WholeMemoryOutcome.Created, (int)B0Outcome.Created)]
    [InlineData((int)WholeMemoryOutcome.Replaced, (int)B0Outcome.Replaced)]
    [InlineData((int)WholeMemoryOutcome.SuccessWithWarning, (int)B0Outcome.SuccessWithWarning)]
    [InlineData((int)WholeMemoryOutcome.RecoveryRequired, (int)B0Outcome.RecoveryRequired)]
    public void FromImport_MapsThroughMapWholeOutcomeExactlyLikeFromWhole(
        int outcomeValue, int expectedValue)
    {
        var outcome = (WholeMemoryOutcome)outcomeValue;
        var expected = (B0Outcome)expectedValue;
        ImportMemoryResult import = new(outcome, null, "op-import", null, null);
        B0Result b0 = B0ResultWrappers.FromImport(import);

        Assert.Equal(expected, b0.Outcome);
        Assert.Null(b0.Failure);            // FromImport mirrors FromWhole: Failure stays null
        Assert.Equal("op-import", b0.OperationId);
        Assert.Equal("op-import", b0.CorrelationId);
    }

    [Fact]
    public void FromImport_CarriesWarningAndRecoveryUnchanged()
    {
        var recovery = new RecoveryInfo("/scope/.restore-journal.json", "cleanupPending",
            "resolve retained evidence", "deferred");
        ImportMemoryResult import = new(
            WholeMemoryOutcome.SuccessWithWarning, null, "op-warn", "cleanup deferred", recovery);
        B0Result b0 = B0ResultWrappers.FromImport(import);

        Assert.Equal(B0Outcome.SuccessWithWarning, b0.Outcome);
        Assert.Equal("cleanup deferred", b0.Warning);
        Assert.Equal("/scope/.restore-journal.json", b0.Recovery!.EvidencePath);
    }

    [Fact]
    public void MapWholeMemoryCode_ClassifiesEveryExceptionCodeAsRecoveryRequired()
    {
        Assert.Equal(B0Outcome.RecoveryRequired, B0ResultWrappers.MapWholeMemoryCode(WholeMemoryErrorCode.RecoveryRequired));
        Assert.Equal(B0Outcome.RecoveryRequired, B0ResultWrappers.MapWholeMemoryCode(WholeMemoryErrorCode.Internal));
    }
}

public class FromMigrationWrapperTests
{
    private const string Quarantine = ".scrinia-conflicts-20260918-000000";

    [Fact]
    public void FromMigration_RanFalse_IsCommittedWithNoFailure()
    {
        StoreLayoutMigration.Result result = new(0, 0, Ran: false, BackupPath: null);
        B0Result b0 = B0ResultWrappers.FromMigration(result, null);

        Assert.Equal(B0Outcome.Committed, b0.Outcome);
        Assert.Null(b0.Failure);
        Assert.Null(b0.Recovery);
        Assert.Null(b0.Warning);
    }

    [Fact]
    public void FromMigration_RanTrueNoConflicts_IsCommitted()
    {
        StoreLayoutMigration.Result result = new(3, 0, Ran: true, BackupPath: ".scrinia-backup-20260918-000000");
        B0Result b0 = B0ResultWrappers.FromMigration(result, null);

        Assert.Equal(B0Outcome.Committed, b0.Outcome);
        Assert.Null(b0.Failure);
        Assert.NotNull(b0.Warning);
    }

    [Fact]
    public void FromMigration_RanTrueWithConflicts_IsSuccessWithWarningAndQuarantineRecovery()
    {
        StoreLayoutMigration.Result result = new(2, 1, Ran: true, BackupPath: null);
        B0Result b0 = B0ResultWrappers.FromMigration(result, Quarantine);

        Assert.Equal(B0Outcome.SuccessWithWarning, b0.Outcome);
        Assert.NotNull(b0.Failure);
        Assert.Equal(B0FailureTaxonomy.WholeMemory, b0.Failure!.Taxonomy); // borrowed: B1 adds no Migration taxonomy
        Assert.Equal("Conflict", b0.Failure.Code);
        Assert.Equal(Quarantine, b0.Failure.Detail);
        Assert.Equal(Quarantine, b0.Recovery!.EvidencePath);
        Assert.Equal("resolve conflict losers in .scrinia-conflicts-*/", b0.Recovery.Action);
    }
}
