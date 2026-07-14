using FluentAssertions;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

public sealed class McpToolSerializerOptionsTests
{
    // The MCP tool surface (memory_remember/search/append) exposes Dictionary<string,string[]> params
    // (attributes / attributeFilter). The trimmed single-file `scri serve` publish has NO reflection
    // metadata, so the MCP server marshals tool arguments via source generation only. If that type isn't
    // source-gen resolvable, tool construction throws NotSupportedException at server startup — the
    // reported `scri serve` crash. These guard the fix (register the type + compose the tool options).
    [Fact]
    public void ScriniaMcpJsonContext_Resolves_AttributesDictionary()
    {
        ScriniaMcpJsonContext.Default.GetTypeInfo(typeof(Dictionary<string, string[]>))
            .Should().NotBeNull("the attribute dictionary needs source-gen metadata for trimmed serve");
    }

    [Fact]
    public void ToolSerializerOptions_WireInScriniaContext()
    {
        // Structural (trim-faithful): the composed options must put our source-gen context in the resolver
        // chain. A GetTypeInfo check would false-green in JIT via McpJsonUtilities' reflection fallback.
        ScriniaMcpTools.CreateToolSerializerOptions().TypeInfoResolverChain
            .Should().Contain(ScriniaMcpJsonContext.Default, "serve/Server pass these options to WithTools");
    }
}
