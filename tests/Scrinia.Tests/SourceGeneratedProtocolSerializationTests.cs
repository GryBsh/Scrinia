using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Embeddings;
using Scrinia.Services;
using Xunit;

namespace Scrinia.Tests;

public sealed class SourceGeneratedProtocolSerializationTests
{
    [Fact]
    public void PluginClientContext_ContainsEveryProtocolDto()
    {
        var status = new EmbeddingsStatusResponse(1, ["status", "index", "search", "remove"], true, 3, "sig");
        string json = JsonSerializer.Serialize(status, PluginClientJsonContext.Default.EmbeddingsStatusResponse);

        JsonSerializer.Deserialize(json, PluginClientJsonContext.Default.EmbeddingsStatusResponse)
            .Should().BeEquivalentTo(status);
    }
}
