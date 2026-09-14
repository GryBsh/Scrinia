using FluentAssertions;
using Scrinia.Core.Models;
using Scrinia.Plugin.Abstractions;

namespace Scrinia.Tests;

public sealed class TransactionalParticipantContractTests
{
    [Fact]
    public void Descriptor_canonicalizes_capabilities_and_rejects_invalid_identity()
    {
        var descriptor = new TransactionalParticipantDescriptor(
            "participant", 1, new[] { " zeta ", "alpha" }, new TransactionalRetryPolicy(), "participant-key");

        descriptor.Capabilities.Should().Equal("alpha", "zeta");
        var action = () => new TransactionalParticipantDescriptor(
            " ", 1, Array.Empty<string>(), new TransactionalRetryPolicy(), "participant-key");
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Retry_policy_applies_defaults_and_bounds()
    {
        var policy = new TransactionalRetryPolicy();

        policy.MaxAttempts.Should().Be(3);
        policy.MaxElapsed.Should().Be(TimeSpan.FromSeconds(30));
        policy.InitialDelay.Should().Be(TimeSpan.FromMilliseconds(100));
        policy.MaximumDelay.Should().Be(TimeSpan.FromSeconds(2));
        Action invalid = () => new TransactionalRetryPolicy(9);
        invalid.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(TransactionalParticipantState.Committed)]
    [InlineData(TransactionalParticipantState.RolledBack)]
    public void Participant_success_states_require_no_failure_or_evidence(TransactionalParticipantState state)
    {
        var result = new TransactionalParticipantResult(state, null, null);
        result.State.Should().Be(state);
        Action invalid = () => new TransactionalParticipantResult(state, Failure(), null);
        invalid.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(TransactionalParticipantState.Changed)]
    [InlineData(TransactionalParticipantState.Ambiguous)]
    public void Participant_uncertain_states_require_failure_and_evidence(TransactionalParticipantState state)
    {
        var result = new TransactionalParticipantResult(state, Failure(), Evidence());
        result.State.Should().Be(state);
        Action invalid = () => new TransactionalParticipantResult(state, null, Evidence());
        invalid.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Prepare_and_commit_status_truth_tables_are_enforced()
    {
        var handle = new TransactionalPreparedHandle("prepared/1", "v1");
        var prepare = new TransactionalPrepareResult(handle, null, null);
        prepare.Handle.Should().Be(handle);
        new TransactionalPrepareResult(null, Failure(), Evidence()).Failure.Should().NotBeNull();
        new TransactionalCommitStatusResult(TransactionalCommitStatus.Prepared, null, null).State.Should().Be(TransactionalCommitStatus.Prepared);
        new TransactionalCommitStatusResult(TransactionalCommitStatus.Unknown, Evidence(), Failure()).State.Should().Be(TransactionalCommitStatus.Unknown);

        FluentActions.Invoking(() => new TransactionalPrepareResult(null, null, null)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new TransactionalPrepareResult(handle, Failure(), null)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new TransactionalCommitStatusResult(TransactionalCommitStatus.Unknown, null, null)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new TransactionalParticipantResult((TransactionalParticipantState)99, null, null)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Contexts_copy_manifest_and_validate_addresses_and_nested_values()
    {
        var entries = new List<TransactionalMemoryManifestEntry> { ManifestEntry() };
        var context = new TransactionalPrepareContext("tx", "scope", MemoryAddress.Parse("/memory/item"), "op", "write", entries);
        entries.Clear();
        context.Manifest.Should().ContainSingle();
        FluentActions.Invoking(() => new TransactionalPrepareContext("tx", "scope", default, "op", "write", new[] { ManifestEntry() }))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new TransactionalRecoveryEvidence("../evidence", "journal", "1"))
            .Should().Throw<ArgumentException>();
    }

    private static TransactionalMemoryManifestEntry ManifestEntry() =>
        new(MemoryAddress.Parse("/memory/item"), "op", "write");

    private static TransactionalParticipantFailure Failure() =>
        new(TransactionalFailureCode.Permanent, "failed", false);

    private static TransactionalRecoveryEvidence Evidence() =>
        new("evidence/1", "journal", "1");
}
