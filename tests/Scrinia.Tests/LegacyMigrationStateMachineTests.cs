using FluentAssertions;
using Scrinia.Core.Configuration;
using Scrinia.Core.Migration;

namespace Scrinia.Tests;

public sealed class LegacyMigrationStateMachineTests
{
    [Theory]
    [InlineData(false, null, LegacyMigrationState.Fresh)]
    [InlineData(true, null, LegacyMigrationState.PolicyRequired)]
    [InlineData(true, TrackingPolicy.Local, LegacyMigrationState.Local)]
    [InlineData(true, TrackingPolicy.ProjectGit, LegacyMigrationState.ProjectGit)]
    public void ClassifiesCleanWorkspaceStates(bool authoritativeData, TrackingPolicy? policy, LegacyMigrationState expected)
    {
        LegacyMigrationStateMachine.Classify(new LegacyMigrationInput(authoritativeData, policy, null, null, null))
            .Should().Be(expected);
    }

    [Fact]
    public void RecognizesOnlyTheTwoLegacyAttributeLines()
    {
        var input = new LegacyMigrationInput(true, null,
            "user\n" + LegacyMergeRecognition.ScriniaMetaAttributeLine + "\n" + LegacyMergeRecognition.ScriniaNmp2AttributeLine + "\n",
            null, null);

        LegacyMigrationStateMachine.Classify(input).Should().Be(LegacyMigrationState.LegacyMixed);
    }

    [Fact]
    public void PlansConservativeLegacyCleanupAndPreservesUnrelatedLines()
    {
        string attributes = "user\n" + LegacyMergeRecognition.ScriniaMetaAttributeLine + "\n" +
            LegacyMergeRecognition.ScriniaNmp2AttributeLine + "\n";
        LegacyMigrationPlan plan = LegacyMigrationStateMachine.PlanMigration(
            new LegacyMigrationInput(true, null, attributes, null, null), TrackingPolicy.Local);

        plan.State.Should().Be(LegacyMigrationState.LegacyMixed);
        plan.GitAttributesContent.Should().Be("user\n");
        plan.DriverUnsetArgv.Should().HaveCount(14);
        plan.GitCommands.Should().BeEmpty();
    }

    [Fact]
    public void DeletesAttributesOnlyWhenRecognizedLinesAreTheOnlyBytes()
    {
        LegacyMigrationPlan plan = LegacyMigrationStateMachine.PlanMigration(
            new LegacyMigrationInput(true, null,
                LegacyMergeRecognition.ScriniaMetaAttributeLine + "\n" + LegacyMergeRecognition.ScriniaNmp2AttributeLine + "\n",
                null, null), TrackingPolicy.ProjectGit);

        plan.DeleteGitAttributes.Should().BeTrue();
        plan.GitAttributesContent.Should().BeNull();
    }

    [Fact]
    public void ExactMergeConfigIsRemovableButNonDefaultIsPreservedWithWarning()
    {
        LegacyMigrationStateMachine.PlanMigration(
            new LegacyMigrationInput(true, null, null,
                "{\r\n  \"jaccardThreshold\": 0.7,\r\n  \"conflictDir\": \"conflict\"\r\n}\n", null), TrackingPolicy.Local)
            .DeleteMergeConfig.Should().BeTrue();

        LegacyMigrationPlan nonDefault = LegacyMigrationStateMachine.PlanMigration(
            new LegacyMigrationInput(true, null, null, "user config", null), TrackingPolicy.Local);
        nonDefault.DeleteMergeConfig.Should().BeFalse();
        nonDefault.Warnings.Should().ContainSingle();
    }

    [Fact]
    public void UnsafeInputWinsOverRecognizedLegacyState()
    {
        LegacyMigrationStateMachine.Classify(new LegacyMigrationInput(true, null,
            LegacyMergeRecognition.ScriniaMetaAttributeLine + "\n", null, true))
            .Should().Be(LegacyMigrationState.LegacyUnsafe);
    }
}
