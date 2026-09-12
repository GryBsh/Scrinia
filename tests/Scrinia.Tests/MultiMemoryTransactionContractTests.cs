using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed class MultiMemoryTransactionContractTests
{
    [Fact]
    public void Request_RejectsMissingParticipantsAndBlankParticipantIdentity()
    {
        FluentActions.Invoking(() => new MultiMemoryTransactionRequest("local-topic:notes", null!))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new MultiMemoryTransactionRequest("local-topic:notes", []))
            .Should().Throw<ArgumentException>();

        var address = MemoryAddress.Parse("/notes/a");
        FluentActions.Invoking(() => new MultiMemoryParticipantRequest(address, " ", "memory"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new MultiMemoryParticipantRequest(address, "op-1", " "))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Request_RejectsDuplicateCanonicalSubjects()
    {
        var participant = new MultiMemoryParticipantRequest(MemoryAddress.Parse("/notes/a"), "op-1", "memory");
        var duplicate = new MultiMemoryParticipantRequest(MemoryAddress.Parse("/notes/a"), "op-2", "memory");

        FluentActions.Invoking(() => new MultiMemoryTransactionRequest(
                "local-topic:notes", [participant, duplicate]))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ManifestAndParticipantOutcome_PreserveTypedIdentityFields()
    {
        var manifest = new MultiMemoryParticipantManifest("note-a", "op-1", "memory");
        var outcome = new MultiMemoryParticipantOutcome(
            manifest.Subject, manifest.OperationId, manifest.Kind, "notes/a/.append-journal.json", true, false, null);

        manifest.Subject.Should().Be(outcome.Subject);
        manifest.OperationId.Should().Be(outcome.OperationId);
        manifest.Kind.Should().Be(outcome.Kind);
        outcome.Committed.Should().BeTrue();
        outcome.RolledBack.Should().BeFalse();
    }

    [Fact]
    public void Aggregate_DefensivelyCopiesParticipantOutcomesAndPreservesTheirOrder()
    {
        var outcomes = new List<MultiMemoryParticipantOutcome>
        {
            new("note-a", "op-a", "memory", "notes/a", true, false, null),
            new("note-b", "op-b", "memory", "notes/b", false, true, "rolled back")
        };

        var result = new MultiMemoryAggregateResult(
            "tx-1", "local-topic:notes", MultiMemoryOutcome.RolledBack, outcomes, null, null, null);
        outcomes[0] = outcomes[1];

        result.Participants.Select(outcome => outcome.Subject)
            .Should().Equal("note-a", "note-b");
        FluentActions.Invoking(() => ((IList<MultiMemoryParticipantOutcome>)result.Participants)[0] = outcomes[1])
            .Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Outcome_DistinguishesCommittedCleanupPendingFromCommitted()
    {
        MultiMemoryOutcome.CommittedCleanupPending.Should().NotBe(MultiMemoryOutcome.Committed);
    }

    [Fact]
    public void RecoveryRequiredAggregate_RequiresSafeEvidencePath()
    {
        FluentActions.Invoking(() => new MultiMemoryAggregateResult(
                "tx-1", "local-topic:notes", MultiMemoryOutcome.RecoveryRequired, [],
                MultiMemoryFailure.Rollback, null, null))
            .Should().Throw<ArgumentException>();

        FluentActions.Invoking(() => new MultiMemoryAggregateResult(
                "tx-1", "local-topic:notes", MultiMemoryOutcome.RecoveryRequired, [],
                MultiMemoryFailure.Rollback, null, "C:\\foreign\\evidence"))
            .Should().Throw<ArgumentException>();

        var result = new MultiMemoryAggregateResult(
            "tx-1", "local-topic:notes", MultiMemoryOutcome.RecoveryRequired, [],
            MultiMemoryFailure.Rollback, null, "notes/.recovery");

        result.EvidencePath.Should().Be("notes/.recovery");
    }
}
