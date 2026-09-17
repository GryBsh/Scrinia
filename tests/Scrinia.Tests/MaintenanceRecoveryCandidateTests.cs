using FluentAssertions;
using Scrinia.Core;
using System.Security.Cryptography;

namespace Scrinia.Tests;

public sealed class MaintenanceRecoveryCandidateTests
{
    [Fact]
    public void B4MaintenanceProjectionRetainsQuarantineAndRefusesCleanup()
    {
        var state = new B4ParticipantRecoveryState(
            "transaction", "operation", "local", "/notes/item", "participant", 1, "participant-1", "kind",
            ["rollback"], new Dictionary<string, string>(), new Dictionary<string, string>(),
            TransactionalParticipantLifecycleState.Ambiguous, "handle", "v1", 0, 0, true, true,
            TransactionalFailureCode.UnknownTransient, "evidence", "journal", "v1",
            TransactionalQuarantineState.Quarantined, "recovery-mismatch");

        var projection = new B4ParticipantMaintenanceAdapter().Project(state);
        RecoveryCleanupDecision decision = new B4ParticipantMaintenanceAdapter().EvaluateCleanup(projection,
            new HashSet<string>(StringComparer.Ordinal));

        projection.IsTerminalAndVerified.Should().BeFalse();
        decision.Eligible.Should().BeFalse();
        decision.Protection.Should().Be(MaintenanceProtectionReason.UnresolvedRecovery);
    }

    [Fact]
    public void B4MissingEvidenceReferenceCannotAuthorizeCleanup()
    {
        var state = new B4ParticipantRecoveryState(
            "transaction", "operation", "local", "/notes/item", "participant", 1, "participant-1", "kind",
            ["rollback"], new Dictionary<string, string>(), new Dictionary<string, string>(),
            TransactionalParticipantLifecycleState.RolledBack, "handle", "v1", 0, 0, false, false,
            null, null, null, null, TransactionalQuarantineState.None, null);

        RecoveryCleanupDecision decision = new B4ParticipantMaintenanceAdapter().EvaluateCleanup(
            new B4ParticipantMaintenanceAdapter().Project(state), new HashSet<string>(StringComparer.Ordinal));

        decision.Eligible.Should().BeFalse();
        decision.Protection.Should().Be(MaintenanceProtectionReason.MissingExpectedMember);
    }

    [Theory]
    [InlineData("Malformed", "Malformed")]
    [InlineData("UnsupportedVersion", "UnsupportedVersion")]
    [InlineData("Ambiguous", "Ambiguous")]
    [InlineData("Inconsistent", "Unverifiable")]
    [InlineData("Unsafe", "Unsafe")]
    public void BlockedRecoveryEvidenceKeepsItsTypedProtectionReason(
        string reasonName, string expectedName)
    {
        RecoveryBlockReason reason = Enum.Parse<RecoveryBlockReason>(reasonName);
        MaintenanceProtectionReason expected = Enum.Parse<MaintenanceProtectionReason>(expectedName);
        var block = new RecoveryBlock("local", "evidence", RecoveryOperation.Restore, "operation", reason, "detail");
        var projection = new B2RecoveryMaintenanceAdapter()
            .Project(new RecoveryDiscoveryResult([], block), "local").Single();

        RecoveryCleanupDecision decision = new B2RecoveryMaintenanceAdapter().EvaluateCleanup(
            projection, new HashSet<string>(StringComparer.Ordinal));

        decision.Protection.Should().Be(expected);
        decision.Eligible.Should().BeFalse();
    }
}
