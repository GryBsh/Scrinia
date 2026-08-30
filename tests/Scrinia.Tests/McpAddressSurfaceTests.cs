using FluentAssertions;
using ModelContextProtocol;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class McpAddressSurfaceTests
{
    public static TheoryData<string> UnsupportedRecallInputs => new()
    {
        "file:///tmp/memory.nmp2",
        "https://example.test/memory",
        "//host/memory",
        "/memory?chunk=1",
        "/memory#chunk",
        "/memory/%2e%2e/secret",
        "NMP/2",
        "TAMIS/2 payload",
        "UNKNOWN/9 payload",
        "{\"artifact\":true}\n",
    };

    [Theory]
    [MemberData(nameof(UnsupportedRecallInputs))]
    public async Task Recall_RejectsArtifactAndUriFormsBeforeStoreLookup(string input)
    {
        MemoryStoreContext.Current = null;
        string yaml = await new ScriniaMcpTools().MemoryRecall(input);

        var response = ResponseParser.Parse(yaml);
        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be("UNSUPPORTED_INPUT_FORM");
    }

    [Fact]
    public async Task Recall_DecodesValidInlineNmp2WithoutStore()
    {
        string original = "inline artifact content";
        string yaml = await new ScriniaMcpTools().MemoryRecall(Nmp2ChunkedEncoder.Encode(original));

        var response = ResponseParser.Parse(yaml);
        response.Status.Should().Be("success");
        response.Content.Should().Be(original);
    }

    [Fact]
    public async Task Recall_TrimsOnlyInlineArtifactForm()
    {
        string artifact = $"  {Nmp2ChunkedEncoder.Encode("trimmed artifact")}  ";

        var response = ResponseParser.Parse(await new ScriniaMcpTools().MemoryRecall(artifact));

        response.Status.Should().Be("success");
        response.Content.Should().Be("trimmed artifact");
    }

    [Fact]
    public async Task Recall_UsesMemoryAddressParserRulesForLogicalAddressInput()
    {
        var response = ResponseParser.Parse(await new ScriniaMcpTools().MemoryRecall(" /ordinary-name "));

        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Resource_RejectsUriBeforeRequiringStore()
    {
        Func<Task> action = () => ScriniaMcpResources.ReadMemory("file:///tmp/memory.nmp2");

        var exception = await action.Should().ThrowAsync<ModelContextProtocol.McpException>();
        exception.Which.Message.Should().Contain("UNSUPPORTED_INPUT_FORM");
    }

    [Fact]
    public void ActiveMcpSurface_IsExplicitlyEnumerated()
    {
        typeof(ScriniaMcpTools).GetMethods()
            .Where(method => method.GetCustomAttributes(false)
                .Any(attribute => attribute.GetType().Name == "McpServerToolAttribute"))
            .Select(method => method.Name)
            .Should().BeEquivalentTo(
                "Guide", "MemoryRemember", "MemoryRecall", "MemorySearch", "MemoryList",
                "MemoryAppend", "MemoryForget", "MemoryCompact", "MemoryLink", "MemoryRestore",
                "MemoryReconcile", "Facts");

        typeof(ScriniaMcpResources).GetMethod(nameof(ScriniaMcpResources.ReadMemory))
            .Should().NotBeNull();
    }
}
