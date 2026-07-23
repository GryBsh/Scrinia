using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Agent;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Coverage for <see cref="AgentPrompts.ParseIngest"/> — the parser for the single
/// combined agent ingest op that yields a typed <c>attributes</c> map plus freeform
/// <c>keywords</c>, mirroring the production <c>remember</c> tool's two arguments.
/// </summary>
public class AgentPromptsTests
{
    [Fact]
    public void ParseIngest_ParsesAttributesAndKeywords()
    {
        var result = AgentPrompts.ParseIngest(
            "{\"attributes\":{\"person\":[\"Maria\"],\"place\":[\"Lisbon\"]},\"keywords\":[\"trip\",\"vacation\"]}");

        result.Should().NotBeNull();
        result!.Attributes.Should().ContainKey("person");
        result.Attributes["person"].Should().Equal("maria");   // lowercased
        result.Attributes["place"].Should().Equal("lisbon");
        result.Keywords.Should().Equal("trip", "vacation");
    }

    [Fact]
    public void ParseIngest_ToleratesCodeFence()
    {
        var result = AgentPrompts.ParseIngest(
            "```json\n{\"attributes\":{\"org\":[\"acme\"]},\"keywords\":[\"contract\"]}\n```");

        result.Should().NotBeNull();
        result!.Attributes["org"].Should().Equal("acme");
        result.Keywords.Should().Equal("contract");
    }

    [Fact]
    public void ParseIngest_AcceptsScalarAttributeValue_AsSingleElementArray()
    {
        // Small models often emit { "person": "maria" } instead of { "person": ["maria"] }.
        var result = AgentPrompts.ParseIngest("{\"attributes\":{\"person\":\"maria\"}}");

        result.Should().NotBeNull();
        result!.Attributes["person"].Should().Equal("maria");
    }

    [Fact]
    public void ParseIngest_DedupsAndLowercasesValues()
    {
        var result = AgentPrompts.ParseIngest(
            "{\"attributes\":{\"topic\":[\"Renovation\",\"renovation\"]},\"keywords\":[\"Remodel\",\"remodel\"]}");

        result.Should().NotBeNull();
        result!.Attributes["topic"].Should().Equal("renovation");
        result.Keywords.Should().Equal("remodel");
    }

    [Fact]
    public void ParseIngest_DropsTooShortTerms()
    {
        // "a" is below MinTermChars and dropped; the attribute whose only value was too short
        // yields no surviving values, so the key is omitted.
        var result = AgentPrompts.ParseIngest(
            "{\"attributes\":{\"person\":[\"a\"]},\"keywords\":[\"ok\",\"a\"]}");

        result.Should().NotBeNull();
        result!.Attributes.Should().NotContainKey("person");
        result.Keywords.Should().Equal("ok");
    }

    [Fact]
    public void ParseIngest_CapsAttributeKeys()
    {
        var keys = string.Join(",",
            Enumerable.Range(0, AgentPrompts.MaxAttributeKeysPerDoc + 5)
                .Select(i => $"\"key{i}\":[\"value{i}\"]"));
        var result = AgentPrompts.ParseIngest("{\"attributes\":{" + keys + "}}");

        result.Should().NotBeNull();
        result!.Attributes.Should().HaveCount(AgentPrompts.MaxAttributeKeysPerDoc);
    }

    [Fact]
    public void ParseIngest_CapsValuesPerKey()
    {
        var vals = string.Join(",",
            Enumerable.Range(0, AgentPrompts.MaxValuesPerKey + 4).Select(i => $"\"value{i}\""));
        var result = AgentPrompts.ParseIngest("{\"attributes\":{\"topic\":[" + vals + "]}}");

        result.Should().NotBeNull();
        result!.Attributes["topic"].Should().HaveCount(AgentPrompts.MaxValuesPerKey);
    }

    [Fact]
    public void ParseIngest_CapsKeywords()
    {
        var kws = string.Join(",",
            Enumerable.Range(0, AgentPrompts.MaxKeywordsPerDoc + 6).Select(i => $"\"kw{i}\""));
        var result = AgentPrompts.ParseIngest("{\"keywords\":[" + kws + "]}");

        result.Should().NotBeNull();
        result!.Keywords.Should().HaveCount(AgentPrompts.MaxKeywordsPerDoc);
    }

    [Fact]
    public void ParseIngest_KeywordsOnly_IsValid()
    {
        var result = AgentPrompts.ParseIngest("{\"keywords\":[\"solo\"]}");

        result.Should().NotBeNull();
        result!.Attributes.Should().BeEmpty();
        result.Keywords.Should().Equal("solo");
    }

    [Fact]
    public void ParseIngest_AttributesOnly_IsValid()
    {
        var result = AgentPrompts.ParseIngest("{\"attributes\":{\"thing\":[\"camry\"]}}");

        result.Should().NotBeNull();
        result!.Keywords.Should().BeEmpty();
        result.Attributes["thing"].Should().Equal("camry");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[\"array\",\"not\",\"object\"]")]        // valid JSON, wrong shape
    [InlineData("{}")]                                    // no attributes, no keywords
    [InlineData("{\"attributes\":{},\"keywords\":[]}")]   // both empty
    public void ParseIngest_ReturnsNull_ForUnusableInput(string? raw)
    {
        AgentPrompts.ParseIngest(raw).Should().BeNull();
    }
}
