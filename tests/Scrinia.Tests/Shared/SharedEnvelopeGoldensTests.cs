using FluentAssertions;
using Scrinia.Mcp.SharedContracts;

namespace Scrinia.Tests.Shared;

public sealed class SharedEnvelopeGoldensTests
{
    [Fact]
    public void Serializes_recall_mutation_refresh_partial_and_zero_success_error_exactly()
    {
        var recall = new RecallResult("team", "/patterns/review", ["Review public contracts."], new Dictionary<string, string[]>(), ["contract review"], "2026-08-31T10:00:00.0000000Z", null);
        SharedEnvelope.Success("team", recall).ToYaml().Should().Be("""
status: success
base: team
result:
  base: team
  path: /patterns/review
  chunks:
    - Review public contracts.
  attributes: {}
  keywords:
    - contract review
  created: 2026-08-31T10:00:00.0000000Z
  updated: null
warnings: []
instruction: null
followUp: []
mutation: none
""");

        var mutation = new MutationResult("team", "/patterns/review", "/findings/api", MutationKind.Applied,
            new string('0', 64), [".scrinia/a.nmp2"]);
        SharedEnvelope.CreateMutation("team", mutation, "git guidance").ToYaml().Should().Contain("mutation: applied");
        SharedEnvelope.CreateMutation("team", mutation, "git guidance").ToYaml().Should().NotContain("results:");

        var refresh = new RefreshResult("team", new string('0', 64), new string('f', 64), SharedAvailability.Available, []);
        SharedEnvelope.Success("team", refresh).ToYaml().Should().Contain("previousGeneration: " + new string('0', 64));

        var warning = new SharedWarning("team", "SHARED_BASE_UNAVAILABLE", "The enabled base could not be opened.");
        SharedEnvelope.Partial(new object[] { new SharedBaseSummary("team", null, SharedAvailability.Unavailable, null) }, [warning]).ToYaml()
            .Should().Contain("status: partial").And.Contain("warnings:");

        SharedEnvelope.CreateError("SHARED_BASE_UNAVAILABLE", "No selected shared base is available.", [warning]).ToYaml()
            .Should().NotContain("results:").And.NotContain("result:").And.Contain("mutation: none");
    }

    [Fact]
    public void Preserves_nullable_timestamps_and_digests()
    {
        var item = new ListItem("team", "/skill/qa", SharedListKind.Persistent, null, null, null, new Dictionary<string, string[]>(), [], SharedDrift.Changed,
            [new FileDriftRef("src/a.cs", null, new string('a', 64))]);

        string yaml = SharedEnvelope.Success("team", item).ToYaml();
        yaml.Should().Contain("created: null").And.Contain("updated: null");
        yaml.Should().Contain("expectedSha256: null").And.Contain("actualSha256: " + new string('a', 64));
    }

    [Fact]
    public void Omits_rejected_base_and_mutation_path()
    {
        string yaml = SharedEnvelope.CreateError("SHARED_BASE_NOT_ENABLED", "The selected base is not enabled for this workspace.").ToYaml();

        yaml.Should().NotContain("base:").And.NotContain("path:").And.NotContain("result:").And.NotContain("results:");
        yaml.Should().Contain("errorCode: SHARED_BASE_NOT_ENABLED").And.Contain("mutation: none");
    }
}
