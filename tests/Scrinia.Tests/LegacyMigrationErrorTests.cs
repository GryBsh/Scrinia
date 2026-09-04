using FluentAssertions;
using Scrinia.Core.Configuration;
using Scrinia.Core.Migration;

namespace Scrinia.Tests;

public sealed class LegacyMigrationErrorTests
{
    [Theory]
    [InlineData(LegacyMigrationState.LegacyClean, TrackingPolicy.Local, ConfigurationErrorCode.LegacyMigrationRequired)]
    [InlineData(LegacyMigrationState.LegacyMixed, TrackingPolicy.ProjectGit, ConfigurationErrorCode.LegacyMigrationRequired)]
    [InlineData(LegacyMigrationState.LegacyManual, TrackingPolicy.Local, ConfigurationErrorCode.LegacyMigrationRequired)]
    [InlineData(LegacyMigrationState.LegacyUnsafe, TrackingPolicy.Local, ConfigurationErrorCode.ManagedFileUnsafe)]
    [InlineData(LegacyMigrationState.PolicyDrift, TrackingPolicy.Local, ConfigurationErrorCode.TrackingPolicyInvalid)]
    public void CommandPrecedenceIsDeclaredByState(LegacyMigrationState state, TrackingPolicy policy, ConfigurationErrorCode expected)
    {
        LegacyMigrationResult result = LegacyMigrationStateMachine.ApplyCommand(state, policy, isMigration: false);
        result.ErrorCode.Should().Be(expected);
        result.ExitClass.Should().Be(ConfigurationExitClass.PolicyFailure);
        result.Changed.Should().BeFalse();
    }

    [Fact]
    public void MigrationReturnsStaticUnsetArgvWithoutExecutingGit()
    {
        LegacyMigrationResult result = LegacyMigrationStateMachine.ApplyCommand(
            LegacyMigrationState.LegacyClean, TrackingPolicy.Local, isMigration: true);

        result.Succeeded.Should().BeTrue();
        result.GitIndexStatus.Should().Be("UNVERIFIED");
        result.DriverUnsetArgv.Should().HaveCount(14);
    }
}
