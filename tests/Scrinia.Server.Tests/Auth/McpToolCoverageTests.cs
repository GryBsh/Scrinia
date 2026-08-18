using System.Reflection;
using FluentAssertions;
using Scrinia.Mcp;
using Scrinia.Server.Auth;

namespace Scrinia.Server.Tests.Auth;

public class McpToolCoverageTests
{
    // Enumerates from the [McpServerTool] attribute set — the authoritative list —
    // so a tool added later cannot land ungated without failing here.
    public static TheoryData<string> DeclaredToolNames()
    {
        var data = new TheoryData<string>();
        foreach (var method in typeof(ScriniaMcpTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = method.GetCustomAttributes()
                .FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute");
            if (attr is null) continue;
            string? name = attr.GetType().GetProperty("Name")?.GetValue(attr) as string;
            if (!string.IsNullOrWhiteSpace(name)) data.Add(name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(DeclaredToolNames))]
    public void EveryDeclaredTool_HasAnExplicitPermissionDecision(string toolName)
    {
        McpPermissionFilter.HasExplicitDecision("tools/call", toolName)
            .Should().BeTrue($"tool '{toolName}' must declare a permission or be explicitly ungated");
    }

    [Fact]
    public void AnUnknownToolName_HasNoExplicitDecision()
    {
        McpPermissionFilter.HasExplicitDecision("tools/call", "nonexistent_tool")
            .Should().BeFalse("an unrecognised tool must fail closed, not fall through ungated");
    }

    [Fact]
    public void AnUngatedTool_IsDistinguishableFromAnUnmappedOne()
    {
        // Both yield a null permission; only the explicitness query separates them.
        // Without this test the two cases could quietly collapse back into one.
        McpPermissionFilter.RequiredPermission("tools/call", "guide").Should().BeNull();
        McpPermissionFilter.RequiredPermission("tools/call", "nonexistent_tool").Should().BeNull();

        McpPermissionFilter.HasExplicitDecision("tools/call", "guide").Should().BeTrue();
        McpPermissionFilter.HasExplicitDecision("tools/call", "nonexistent_tool").Should().BeFalse();
    }

    [Fact]
    public void DeclaredToolNames_IsNotEmpty()
    {
        // Without this, a reflection query that silently matched nothing would make
        // the theory above vacuously green.
        DeclaredToolNames().Should().NotBeEmpty();
    }

    [Fact]
    public void ToolsCallWithNoName_HasNoExplicitDecision()
    {
        // A tools/call with a missing name used to fall through RequiredPermission's default
        // arm ungated, same as any other unmapped name. It now fails closed like every other
        // unmapped name — strictly safer than before, and worth pinning so it can't regress
        // back to "no name means no gate" unnoticed.
        McpPermissionFilter.HasExplicitDecision("tools/call", null)
            .Should().BeFalse("a tools/call with no tool name must not be treated as an explicit ungating decision");
    }
}
