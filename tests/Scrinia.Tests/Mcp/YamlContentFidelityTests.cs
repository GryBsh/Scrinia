using FluentAssertions;
using Scrinia.Mcp;
using YamlDotNet.Serialization;

namespace Scrinia.Tests.Mcp;

public sealed class YamlContentFidelityTests
{
    [Theory]
    [InlineData("")]
    [InlineData("first\nsecond")]
    [InlineData("first\r\nsecond")]
    [InlineData("first\rsecond")]
    [InlineData("  leading\n\nlast  ")]
    [InlineData("trailing\n")]
    [InlineData("trailing\n\n")]
    [InlineData("\n\n")]
    [InlineData(" \n  ")]
    public void ParsedContentPreservesEveryCharacter(string content)
    {
        string yaml = ResponseBuilder.Success(content).ToYaml();
        var parsed = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(yaml);

        parsed["content"].Should().Be(content);
        yaml.Should().NotContain("\r\r\n");
    }

    [Fact]
    public async Task GuidePreservesBlankLinesAndLineEndings()
    {
        using var scope = new TestHelpers.StoreScope();
        string yaml = await new ScriniaMcpTools().Guide();
        var parsed = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(yaml);

        parsed["content"].Should().Be(EmbeddedPrompts.LoadGuide());
    }
}
