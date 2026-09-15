using FluentAssertions;
using Scrinia.Core;

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

        B4ParticipantMaintenanceAdapter adapter = new();
        B4ParticipantMaintenanceProjection projection = adapter.Project(state);
        RecoveryCleanupDecision decision = adapter.EvaluateCleanup(projection,
            new HashSet<string>(StringComparer.Ordinal));

        projection.IsTerminalAndVerified.Should().BeFalse();
        decision.Eligible.Should().BeFalse();
        decision.Protected.Should().BeTrue();
        decision.Protection.Should().Be(MaintenanceProtectionReason.UnresolvedRecovery);
    }

    [Fact]
    public void MaintenanceCursorRejectsUnsafeRelativePaths()
    {
        FluentActions.Invoking(() => new MaintenanceDiscoveryCursor("../foreign"))
            .Should().Throw<ArgumentException>();
    }
}
