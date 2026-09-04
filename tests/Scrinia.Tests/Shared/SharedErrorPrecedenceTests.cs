using FluentAssertions;
using Scrinia.Mcp.SharedContracts;

namespace Scrinia.Tests.Shared;

public sealed class SharedErrorPrecedenceTests
{
    [Fact]
    public void Applies_validation_stages_in_the_fixed_contract_order()
    {
        SharedValidationStageOrder.All.Should().Equal(
            SharedValidationStage.Parameter,
            SharedValidationStage.BaseSyntax,
            SharedValidationStage.EnabledMembership,
            SharedValidationStage.Registry,
            SharedValidationStage.GitRoot,
            SharedValidationStage.LogicalPath,
            SharedValidationStage.TrackingBlock,
            SharedValidationStage.Availability,
            SharedValidationStage.Freshness,
            SharedValidationStage.Operation,
            SharedValidationStage.Storage);
    }

    [Fact]
    public void Rejects_disabled_base_without_echoing_private_identity()
    {
        SharedEnvelope envelope = SharedEnvelope.ForValidationFailure(SharedValidationStage.EnabledMembership, "private-name");

        envelope.Base.Should().BeNull();
        envelope.ToYaml().Should().NotContain("private-name");
        envelope.ToYaml().Should().Contain("errorCode: SHARED_BASE_NOT_ENABLED");
    }

    [Fact]
    public void Echoes_base_only_after_membership_and_uses_mutation_certainty()
    {
        SharedEnvelope envelope = SharedEnvelope.ForValidatedBase("team", "DURABLE_WRITE_FAILED", "The durable write outcome could not be verified.", MutationCertainty.Unknown);

        envelope.Base.Should().Be("team");
        envelope.ToYaml().Should().Contain("base: team").And.Contain("mutation: unknown");
    }
}
