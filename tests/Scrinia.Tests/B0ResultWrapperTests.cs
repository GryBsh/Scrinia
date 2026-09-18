using Scrinia.Core;
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
