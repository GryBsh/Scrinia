using FluentAssertions;
using ModelContextProtocol;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests for the MCP prompt (<see cref="ScriniaMcpPrompts"/>) and resource
/// (<see cref="ScriniaMcpResources"/>) surfaces added alongside the existing tools. The prompt
/// returns the agent guide (slash-command convenience); the resource decodes a stored memory by
/// name (the <c>@</c>-mention read path).
/// </summary>
public sealed class ScriniaMcpPromptsResourcesTests
{
    [Fact]
    public void GuidePrompt_ReturnsTheAgentGuide()
    {
        string prompt = ScriniaMcpPrompts.Guide();
        prompt.Should().NotBeNullOrWhiteSpace();
        // Same content the guide() tool serves — sanity-check a couple of stable sections.
        prompt.Should().Contain("remember");
        prompt.Should().NotContain("memory_remember");
        prompt.Should().Contain("Reserved paths");
        prompt.Should().Contain("facts",
            because: "the facts tool must be documented in the agent guide");
    }

    [Fact]
    public async Task ReadMemoryResource_DecodesStoredMemoryContent()
    {
        using var scope = new TestHelpers.StoreScope();
        const string original = "OAuth token rotation and refresh policy.";
        await new ScriniaMcpTools().MemoryRemember(path: "auth-flow", content: [original]);

        string content = await ScriniaMcpResources.ReadMemory("auth-flow", CancellationToken.None);

        content.Should().Be(original,
            "the resource read path must decode the NMP/2 artifact back to the original text");
    }

    [Fact]
    public async Task ReadMemoryResource_NoStoreConfigured_Throws()
    {
        MemoryStoreContext.Current = null;
        var act = async () => await ScriniaMcpResources.ReadMemory("anything", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ReadMemoryResource_MissingMemory_ThrowsCleanMcpError()
    {
        using var scope = new TestHelpers.StoreScope();
        var act = async () => await ScriniaMcpResources.ReadMemory("does-not-exist", CancellationToken.None);
        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("not found");
    }
}
