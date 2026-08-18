using System.Reflection;
using FluentAssertions;
using Scrinia.Mcp;

namespace Scrinia.Tests.Mcp;

/// <summary>
/// Task 9 (contradiction 6/6 of the spec's B2 table): <c>search</c> took <c>attributeFilter</c> as
/// a real map but <c>scopes</c>/<c>excludeTopics</c> as comma-separated strings, so the schema
/// could not express a scope name containing a comma. <c>list</c> and <c>facts</c> carried the
/// same shape and convert alongside it — three tools, not one.
/// </summary>
public class SearchParameterShapeTests
{
    private static ScriniaMcpTools Tools() => new();

    /// <summary>Finds the <c>[McpServerTool(Name = toolName)]</c> method by name rather than by
    /// C# method name, matching the tool's actual published identity — string-typed reflection
    /// over the attribute, not a direct reference, so this also verifies the attribute itself is
    /// still present and correctly named.</summary>
    private static MethodInfo FindTool(string toolName) =>
        typeof(ScriniaMcpTools).GetMethods()
            .Single(m => m.GetCustomAttributes()
                .Any(a => a.GetType().Name == "McpServerToolAttribute"
                          && (string?)a.GetType().GetProperty("Name")?.GetValue(a) == toolName));

    [Theory]
    [InlineData("search")]
    [InlineData("list")]
    [InlineData("facts")]
    public void ScopesAndExcludeTopics_AreArraysOnTheMcpSchema_NotCommaStrings(string toolName)
    {
        var tool = FindTool(toolName);

        tool.GetParameters().Single(p => p.Name == "scopes")
            .ParameterType.Should().Be(typeof(string[]),
                because: $"{toolName}'s schema must be able to express a scope name containing a comma");
        tool.GetParameters().Single(p => p.Name == "excludeTopics")
            .ParameterType.Should().Be(typeof(string[]),
                because: $"{toolName}'s schema must be able to express a topic name containing a comma");
    }

    /// <summary>The concrete capability the comma-string encoding could not express. Replaces the
    /// plan's original second test, which asserted only <c>Status.Should().NotBe("error")</c> — an
    /// unresolvable scope yields zero results and a SUCCESS status, not an error, so that
    /// assertion passed trivially the moment the code compiled. This stores into a comma-named
    /// scope and asserts the memory actually comes back, which is the capability this task
    /// delivers.</summary>
    [Fact]
    public async Task AScopeNameContainingAComma_IsAddressable()
    {
        using var scope = new TestHelpers.StoreScope();
        // Store into a comma-named topic, then select it by name. Under the old comma-string
        // encoding the filter split this into two scopes ("odd" and "name") that match nothing.
        await Tools().MemoryRemember(path: "/odd,name/probe", content: ["findable"]);

        var response = ResponseParser.Parse(
            await Tools().MemorySearch(query: "findable", scopes: ["odd,name"]));

        response.Status.Should().NotBe("error");
        response.Content.Should().Contain("probe",
            "a scope whose name contains a comma is unreachable through a comma-separated filter");
    }

    /// <summary>Task 9, D-4, pinned at the facade: an empty <c>scopes</c>/<c>excludeTopics</c>
    /// array over JSON-RPC must behave identically to omitting the field, not narrow the search.
    /// This is the same trap Task 6's fix round hit with an explicit null/empty <c>mode</c>.</summary>
    [Fact]
    public async Task Search_EmptyScopesAndExcludeTopics_BehaveIdenticallyToNull()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().MemoryRemember(path: "/findings/alpha", content: ["alpha probe content"]);

        var withEmpty = ResponseParser.Parse(
            await Tools().MemorySearch(query: "alpha", scopes: [], excludeTopics: []));
        var withNull = ResponseParser.Parse(
            await Tools().MemorySearch(query: "alpha", scopes: null, excludeTopics: null));

        withEmpty.Status.Should().NotBe("error");
        withEmpty.Content.Should().Contain("alpha",
            "an empty scopes/excludeTopics array must not narrow the search away from the match");
        withEmpty.Content.Should().Be(withNull.Content);
    }

    /// <summary>Task 9, D-4, the <c>list</c> sibling of the test above.</summary>
    [Fact]
    public async Task List_EmptyScopesAndExcludeTopics_BehaveIdenticallyToNull()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().MemoryRemember(path: "/findings/beta", content: ["beta probe content"]);

        var withEmpty = ResponseParser.Parse(
            await Tools().MemoryList(scopes: [], mode: "full", excludeTopics: []));
        var withNull = ResponseParser.Parse(
            await Tools().MemoryList(scopes: null, mode: "full", excludeTopics: null));

        withEmpty.Status.Should().NotBe("error");
        withEmpty.Content.Should().Contain("beta",
            "an empty scopes/excludeTopics array must not narrow the listing away from the entry");
        withEmpty.Content.Should().Be(withNull.Content);
    }
}
