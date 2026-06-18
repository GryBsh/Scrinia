using FluentAssertions;
using Scrinia.Server.Auth;
using Xunit;

namespace Scrinia.Server.Tests;

/// <summary>
/// Unit tests for the pure permission-mapping policy behind the MCP per-operation auth filter.
/// The map is the single source of truth for which MCP request needs which permission; these
/// assertions pin the policy independently of the JSON-RPC pipeline.
/// </summary>
public sealed class McpPermissionFilterTests
{
    [Theory]
    [InlineData("memory_search", "search")]
    [InlineData("memory_list", "search")]
    [InlineData("memory_recall", "read")]
    [InlineData("memory_remember", "store")]
    [InlineData("memory_compact", "store")]
    [InlineData("memory_link", "store")]
    [InlineData("memory_reconcile", "store")]
    [InlineData("memory_append", "append")]
    [InlineData("memory_forget", "forget")]
    public void ToolsCall_MapsEachTool_ToItsPermission(string tool, string expected)
    {
        McpPermissionFilter.RequiredPermission("tools/call", tool).Should().Be(expected);
    }

    [Theory]
    [InlineData("guide")]
    [InlineData("memory_restore")]
    [InlineData("some_unknown_tool")]
    [InlineData(null)]
    public void ToolsCall_BootstrapRecoveryOrUnknown_IsUngated(string? tool)
    {
        McpPermissionFilter.RequiredPermission("tools/call", tool).Should().BeNull();
    }

    [Fact]
    public void ResourcesRead_RequiresRead()
    {
        McpPermissionFilter.RequiredPermission("resources/read", null).Should().Be("read");
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("tools/list")]
    [InlineData("prompts/list")]
    [InlineData("prompts/get")]
    [InlineData("resources/list")]
    [InlineData("resources/templates/list")]
    [InlineData("notifications/initialized")]
    public void HandshakeAndDiscovery_AreUngated(string method)
    {
        McpPermissionFilter.RequiredPermission(method, null).Should().BeNull();
    }
}
