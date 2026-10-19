using FluentAssertions;
using Scrinia.Mcp;
using Scrinia.Mcp.SharedContracts;
using ModelContextProtocol.Server;
using System.Text.Json;
using Scrinia.TestDoubles;
using Xunit;

namespace Scrinia.Tests;

public sealed class McpToolSerializerOptionsTests
{
    // The MCP tool surface (remember/search) exposes Dictionary<string,string[]> params
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

    [Fact]
    public void SharedHost_UsesSourceGeneratedOptionsForEveryDictionaryToolSchema()
    {
        // This explicit resolver has no reflection fallback, matching the extracted trimmed host.
        var options = new JsonSerializerOptions { TypeInfoResolver = ScriniaMcpJsonContext.Default };
        string[] expected = ["Remember", "Search"];
        var methods = typeof(SharedTools).GetMethods().Where(method => method.GetParameters()
            .Any(parameter => parameter.ParameterType == typeof(Dictionary<string, string[]>))).ToArray();
        Assert.Equal(expected, methods.Select(method => method.Name).Order(StringComparer.Ordinal));
        foreach (var method in methods)
        {
            var tool = McpServerTool.Create(method, new SharedTools(), new() { SerializerOptions = options });
            Assert.Equal(JsonValueKind.Object, tool.ProtocolTool.InputSchema.ValueKind);
            foreach (var parameter in method.GetParameters().Where(parameter => parameter.ParameterType == typeof(Dictionary<string, string[]>)))
                Assert.True(tool.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty(parameter.Name!, out _));
        }
        // Wiring matters: schema metadata alone previously passed while the shared composition omitted it.
        string composition = File.ReadAllText(RepoPaths.Combine("src", "Scrinia", "Commands", "ScriniaCommands.SharedServe.cs"));
        Assert.Contains(".WithTools<SharedTools>(ScriniaMcpTools.CreateToolSerializerOptions())", composition);
    }
}
