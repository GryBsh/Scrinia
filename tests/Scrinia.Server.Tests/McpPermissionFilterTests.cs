using System.Reflection;
using FluentAssertions;
using FluentAssertions.Execution;
using ModelContextProtocol.Server;
using Scrinia.Mcp;
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
    [InlineData("search", "search")]
    [InlineData("list", "search")]
    [InlineData("recall", "read")]
    [InlineData("remember", "store")]
    [InlineData("compact", "store")]
    [InlineData("link", "store")]
    [InlineData("reconcile", "store")]
    [InlineData("append", "append")]
    [InlineData("forget", "forget")]
    public void ToolsCall_MapsEachTool_ToItsPermission(string tool, string expected)
    {
        McpPermissionFilter.RequiredPermission("tools/call", tool).Should().Be(expected);
    }

    [Theory]
    [InlineData("guide")]
    [InlineData("restore")]
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

    [Fact]
    public void Facts_RequiresSearchPermission()
    {
        McpPermissionFilter.RequiredPermission("tools/call", "facts").Should().Be("search");
    }

    [Fact]
    public void EveryRegisteredTool_HasAPermissionMapping_OrIsIntentionallyUngated()
    {
        // Guards the hand-maintained parallel copy: a tool advertised to agents that the
        // permission map doesn't know about would silently fall through to the ungated
        // default. Bootstrap/recovery tools are ungated on purpose (parity with REST).
        var intentionallyUngated = new HashSet<string>(StringComparer.Ordinal) { "guide", "restore" };

        var toolNames = typeof(ScriniaMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();

        toolNames.Should().NotBeEmpty("the tool type must expose [McpServerTool] methods");

        using var scope = new AssertionScope();
        foreach (var name in toolNames)
        {
            if (intentionallyUngated.Contains(name)) continue;
            McpPermissionFilter.RequiredPermission("tools/call", name)
                .Should().NotBeNull($"tool '{name}' must have an explicit permission mapping");
        }
    }
}
