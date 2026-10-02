using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Embeddings;
using Xunit;

namespace Scrinia.Plugin.Embeddings.Tests;

public sealed class EmbeddingsProtocolTests
{
    [Fact]
    public void Status_RequiresProtocolV1AndEveryRequiredCapability()
    {
        var valid = new EmbeddingsStatusResponse(
            1, ["status", "index", "search", "remove"], true, 384, "vulkan:test");

        EmbeddingsProtocol.ValidateStatus(valid).Should().BeNull();
        EmbeddingsProtocol.ValidateStatus(valid with { ProtocolVersion = 2 })
            .Should().BeOfType<EmbeddingsProtocolUnsupportedException>();
        EmbeddingsProtocol.ValidateStatus(valid with { Capabilities = ["search", "index"] })
            .Should().BeOfType<EmbeddingsProtocolInvalidException>();
        EmbeddingsProtocol.ValidateStatus(valid with { Capabilities = ["search", "index", "remove", "remove"] })
            .Should().BeOfType<EmbeddingsProtocolInvalidException>();
        EmbeddingsProtocol.ValidateStatus(valid with { Capabilities = ["status", "index", "search", "remove", "future"] })
            .Should().BeOfType<EmbeddingsProtocolInvalidException>();
        EmbeddingsProtocol.ValidateStatus(valid with { ProviderAvailable = false })
            .Should().BeOfType<EmbeddingsProtocolInvalidException>();
    }

    [Fact]
    public void DeprecatedUpsert_IsNotPartOfTheV1Contract()
    {
        EmbeddingsProtocol.RequiredCapabilities.Should().Equal("status", "index", "search", "remove");
        EmbeddingsProtocol.IsSupportedCapability("upsert").Should().BeFalse();
    }

    [Fact]
    public void ProtocolDtos_RoundTripThroughTheProductionJsonMetadata()
    {
        var values = new object[]
        {
            new EmbeddingsStatusResponse(1, ["status", "index", "search", "remove"], true, 384, "sig"),
            new EmbeddingsIndexRequest("local", "note", ["one", "two"]),
            new EmbeddingsSearchRequest("query", new Dictionary<string, string[]> { ["local"] = ["note"] }),
            new EmbeddingsRemoveRequest("local", "note"),
            new EmbeddingsOperationResponse(true, null),
            new EmbeddingsSearchResponse(new Dictionary<string, double> { ["local/note"] = 1.5 }, null),
        };

        foreach (object value in values)
        {
            string json = EmbeddingsProtocolJson.Serialize(value);
            json.Should().NotBeNullOrWhiteSpace();
            EmbeddingsProtocolJson.Deserialize(value.GetType(), json).Should().BeEquivalentTo(value);
        }
    }
}
