using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

public class MemoryNamingTests
{
    [Fact]
    public void StripEphemeralPrefix_RemovesTilde()
    {
        MemoryNaming.StripEphemeralPrefix("~scratch").Should().Be("scratch");
    }

    [Fact]
    public void StripEphemeralPrefix_NoTilde_ReturnsUnchanged()
    {
        MemoryNaming.StripEphemeralPrefix("notes").Should().Be("notes");
    }

    [Fact]
    public void StripEphemeralPrefix_EmptyString_ReturnsEmpty()
    {
        MemoryNaming.StripEphemeralPrefix("").Should().Be("");
    }

    [Fact]
    public void FormatScopeLabel_Local()
    {
        MemoryNaming.FormatScopeLabel("local").Should().Be("local");
    }

    [Fact]
    public void FormatScopeLabel_Ephemeral()
    {
        MemoryNaming.FormatScopeLabel("ephemeral").Should().Be("ephemeral");
    }

    [Fact]
    public void FormatScopeLabel_LocalTopic_ExtractsTopicName()
    {
        MemoryNaming.FormatScopeLabel("local-topic:api").Should().Be("api");
    }

    [Fact]
    public void FormatScopeLabel_UnknownScope_ReturnsAsIs()
    {
        MemoryNaming.FormatScopeLabel("custom-scope").Should().Be("custom-scope");
    }
}
