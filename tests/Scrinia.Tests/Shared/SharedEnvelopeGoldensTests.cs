using FluentAssertions;
using System.Text.Json;
using Scrinia.Mcp.SharedContracts;
using YamlDotNet.Serialization;

namespace Scrinia.Tests.Shared;

public sealed class SharedEnvelopeGoldensTests
{
    [Fact]
    public void Serializes_populated_follow_up_and_page_in_exact_order()
    {
        var recall = new RecallResult("team", "/patterns/review", ["Review public contracts."], new Dictionary<string, string[]> { ["topic"] = ["contracts"] }, ["contract review"], "2026-08-31T10:00:00.0000000Z", null);
        var envelope = SharedEnvelope.Success("team", recall) with
        {
            FollowUp = ["/patterns/next"],
            Page = new SharedPage(2, 10, 1, 11, true)
        };

        envelope.ToYaml().Should().Be("""
status: success
base: team
result:
  base: team
  path: /patterns/review
  chunks:
    - Review public contracts.
  attributes:
    topic:
      - contracts
  keywords:
    - contract review
  created: "2026-08-31T10:00:00.0000000Z"
  updated: null
warnings: []
instruction: null
followUp:
  - /patterns/next
mutation: none
page:
  offset: 2
  limit: 10
  returned: 1
  total: 11
  hasMore: true
""");
        ParseWithoutDuplicateKeys(envelope.ToYaml());
    }

    [Fact]
    public void Serializes_every_payload_family_and_one_files_sequence()
    {
        var search = new SearchHit("team", "/x", SharedHitKind.Chunk, 2, 4.25, "preview", new Dictionary<string, string[]> { ["topic"] = ["x"] });
        SharedEnvelope.Success(null, search).ToYaml().Should().Contain("score: 4.25").And.Contain("chunk: 2");

        var entities = new FactsEntitiesResult("team", FactsOperation.Entities, [new EntityCount("topic:x", 2)], false);
        SharedEnvelope.Success("team", entities).ToYaml().Should().Contain("op: entities").And.Contain("count: 2");
        var profile = new FactsProfileResult("team", FactsOperation.Profile, "topic:x", 2, null, null, [], false, ["/x"], false);
        SharedEnvelope.Success("team", profile).ToYaml().Should().Contain("timeline:").And.Contain("- /x");
        var intersect = new FactsIntersectResult("team", FactsOperation.Intersect, ["/x"], false);
        SharedEnvelope.Success("team", intersect).ToYaml().Should().Contain("paths:");

        var mutation = new MutationResult("team", "/patterns/review", "/findings/api", MutationKind.Applied,
            new string('0', 64), [".scrinia/a.nmp2"]);
        string mutationYaml = SharedEnvelope.CreateMutation("team", mutation, "git guidance").ToYaml();
        mutationYaml.Should().Be($"""
status: success
base: team
result:
  base: team
  path: /patterns/review
  destination: /findings/api
  mutation: applied
  generation: "{new string('0', 64)}"
  manifest:
    - ".scrinia/a.nmp2"
warnings: []
instruction: git guidance
followUp: []
mutation: applied
""");

        var refresh = new RefreshResult("team", new string('0', 64), new string('f', 64), SharedAvailability.Available, []);
        SharedEnvelope.Success("team", refresh).ToYaml().Should().Be($"""
status: success
base: team
result:
  base: team
  previousGeneration: "{new string('0', 64)}"
  newGeneration: {new string('f', 64)}
  availability: available
  warnings: []
warnings: []
instruction: null
followUp: []
mutation: none
""");

        var warning = new SharedWarning("team", "SHARED_BASE_UNAVAILABLE", "The enabled base could not be opened.");
        string partialYaml = SharedEnvelope.Partial(new object[] { new SharedBaseSummary("team", null, SharedAvailability.Unavailable, null) }, [warning]).ToYaml();
        partialYaml.Should().Be("""
status: partial
results:
  -
    base: team
    availability: unavailable
    generation: null
warnings:
  -
    base: team
    code: SHARED_BASE_UNAVAILABLE
    diagnostic: The enabled base could not be opened.
instruction: null
followUp: []
mutation: none
""");

        string errorYaml = SharedEnvelope.CreateError("SHARED_BASE_UNAVAILABLE", "No selected shared base is available.", [warning]).ToYaml();
        errorYaml.Should().Be("""
status: error
warnings:
  -
    base: team
    code: SHARED_BASE_UNAVAILABLE
    diagnostic: The enabled base could not be opened.
error: No selected shared base is available.
errorCode: SHARED_BASE_UNAVAILABLE
instruction: null
followUp: []
mutation: none
""");
        ParseWithoutDuplicateKeys(partialYaml);
        ParseWithoutDuplicateKeys(mutationYaml);
        ParseWithoutDuplicateKeys(errorYaml);
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
    public void Serializes_present_empty_files_as_an_empty_sequence()
    {
        var item = new ListItem("team", "/skill/qa", SharedListKind.Persistent, null, null, null, new Dictionary<string, string[]>(), [], Files: []);

        SharedEnvelope.Success("team", item).ToYaml().Should().Be("""
status: success
base: team
result:
  base: team
  path: /skill/qa
  kind: persistent
  created: null
  updated: null
  description: null
  attributes: {}
  keywords: []
  files: []
warnings: []
instruction: null
followUp: []
mutation: none
""");
    }

    [Fact]
    public void Omits_rejected_base_and_mutation_path()
    {
        string yaml = SharedEnvelope.CreateError("SHARED_BASE_NOT_ENABLED", "The selected base is not enabled for this workspace.").ToYaml();

        yaml.Should().NotContain("base:").And.NotContain("path:").And.NotContain("result:").And.NotContain("results:");
        yaml.Should().Contain("errorCode: SHARED_BASE_NOT_ENABLED").And.Contain("mutation: none");
    }

    [Fact]
    public void Quotes_adversarial_scalars_and_rejects_duplicate_yaml_keys()
    {
        var hit = new SearchHit("team", "/x", SharedHitKind.Entry, null, 1, "line 1\n: # - {} [] true null \\\"", new Dictionary<string, string[]> { ["key: #"] = ["true", "null", "-", "{x}"] });
        string yaml = (SharedEnvelope.Success("team", hit) with { Instruction = "line\nnext: # {}", FollowUp = ["null", "- item"] }).ToYaml();

        yaml.Should().Contain("preview: \"").And.Contain("\\n");
        ParseWithoutDuplicateKeys(yaml);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("&")]
    [InlineData("!")]
    [InlineData("|")]
    [InlineData(">")]
    [InlineData("@")]
    [InlineData("`")]
    public void Quotes_yaml_reserved_scalar_prefixes_and_parses_them_as_strings(string prefix)
    {
        var hit = new SearchHit("team", "/x", SharedHitKind.Entry, null, 1, prefix + "payload", new Dictionary<string, string[]>());

        string yaml = SharedEnvelope.Success("team", hit).ToYaml();

        yaml.Should().Contain("preview: \"" + prefix + "payload\"");
        ParseWithoutDuplicateKeys(yaml);
    }

    [Fact]
    public void Escapes_multiline_control_quote_and_backslash_scalars()
    {
        var hit = new SearchHit("team", "/x", SharedHitKind.Entry, null, 1, "line\n\r\t\b\f\u0001\\\"", new Dictionary<string, string[]>());

        string yaml = SharedEnvelope.Success("team", hit).ToYaml();

        yaml.Should().Contain("preview: \"line\\n\\r\\t\\u0008\\u000C\\u0001\\\\\\\"\"");
        ParseWithoutDuplicateKeys(yaml);
    }

    [Theory]
    [InlineData("%payload")]
    [InlineData("~")]
    [InlineData(".inf")]
    [InlineData("-.inf")]
    [InlineData("+.inf")]
    [InlineData(".nan")]
    [InlineData("*alias")]
    [InlineData("&anchor")]
    [InlineData("!tag")]
    [InlineData("|block")]
    [InlineData(">folded")]
    [InlineData("@directive")]
    [InlineData("`code")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[brackets]")]
    [InlineData("{mapping}")]
    [InlineData("key: value")]
    [InlineData("hash # value")]
    [InlineData("line\nnext")]
    [InlineData("control\u0001")]
    [InlineData("quote \"")]
    [InlineData("slash \\")]
    public void Dynamically_sourced_scalars_round_trip_as_exact_strings(string original)
    {
        var hit = new SearchHit("team", "/x", SharedHitKind.Entry, null, 1, original, new Dictionary<string, string[]>());

        object? parsed = ParseYamlRoot(SharedEnvelope.Success("team", hit).ToYaml())["result"] is Dictionary<object, object> result
            ? result["preview"]
            : null;

        parsed.Should().BeOfType<string>().Which.Should().Be(original);
    }

    [Fact]
    public void Shared_envelope_context_rejects_unknown_json_properties()
    {
        Action deserialize = () => JsonSerializer.Deserialize(
            "{\"status\":\"success\",\"extra\":true}", SharedDtoJsonContext.Default.SharedEnvelope);

        deserialize.Should().Throw<JsonException>();
    }

    private static void ParseWithoutDuplicateKeys(string yaml)
    {
        var serializer = new DeserializerBuilder().Build();
        serializer.Deserialize<object>(yaml).Should().NotBeNull();
    }

    private static Dictionary<object, object> ParseYamlRoot(string yaml) =>
        new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(yaml);
}
