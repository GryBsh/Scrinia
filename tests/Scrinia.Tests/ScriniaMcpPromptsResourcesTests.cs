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
    public void BuiltInSkills_EmbeddedResourcesAndGuideHaveExactApprovedNames()
    {
        string[] expected =
        ["auditor", "chaos-engineer", "debugger", "evolutionary", "onboarder", "qa", "self-reflector"];
        const string prefix = "Scrinia.Mcp.skills.";
        string[] embedded = typeof(ScriniaMcpTools).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .Select(name => name[prefix.Length..^3]).ToArray();
        embedded.Should().BeEquivalentTo(expected);

        string guide = ScriniaMcpPrompts.Guide();
        string builtInsLine = guide.Split('\n').Single(line => line.StartsWith("Built-ins: ", StringComparison.Ordinal));
        string builtIns = builtInsLine[..builtInsLine.IndexOf(". ", StringComparison.Ordinal)];
        string[] guideNames = System.Text.RegularExpressions.Regex.Matches(builtIns, "`([^`]+)`")
            .Select(match => match.Groups[1].Value).ToArray();
        guideNames.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void GuidePrompt_ReturnsTheAgentGuide()
    {
        string prompt = ScriniaMcpPrompts.Guide();
        prompt.Should().NotBeNullOrWhiteSpace();
        prompt.Should().Be(EmbeddedPrompts.LoadGuide());
        // Check documented capabilities without tying them to editorial headings.
        prompt.Should().Contain("remember");
        prompt.Should().NotContain("memory_remember");
        prompt.Should().Contain("/agent/");
        prompt.Should().Contain("facts",
            because: "the facts tool must be documented in the agent guide");
    }

    [Fact]
    public async Task ReadMemoryResource_DecodesStoredMemoryContent()
    {
        using var scope = new TestHelpers.StoreScope();
        const string original = "OAuth token rotation and refresh policy.";
        await new ScriniaMcpTools().MemoryRemember(path: "auth-flow", content: [original], description: "Test memory.");

        string content = await ScriniaMcpResources.ReadMemory("auth-flow", CancellationToken.None);

        content.Should().Be(original,
            "the resource read path must decode the NMP/2 artifact back to the original text");
    }

    [Fact]
    public async Task ReadMemoryResource_NoStoreConfigured_Throws()
    {
        var prevCurrent = MemoryStoreContext.RawCurrent;
        var prevDefault = MemoryStoreContext.Default;
        MemoryStoreContext.RawCurrent = null;
        MemoryStoreContext.Default = null;
        try
        {
            var act = async () => await ScriniaMcpResources.ReadMemory("anything", CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            MemoryStoreContext.RawCurrent = prevCurrent;
            MemoryStoreContext.Default = prevDefault;
        }
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
