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

    /// <summary>
    /// Task 10, T10-E. <c>EveryDeclaredTool_HasAnExplicitPermissionDecision</c> above only proves
    /// every declared tool has SOME recorded decision — membership in
    /// <c>McpPermissionFilter.UngatedTools</c> satisfies it exactly as well as a mapped permission
    /// does, so it cannot tell a deliberate exemption from an accidental or malicious one. Since
    /// Task 4 replaced the theory's own hard-coded copy of the ungated list with one derived from
    /// the production array directly, adding a name to <c>UngatedTools</c> now requires no test
    /// change at all — the exact escape-hatch-with-no-pin shape Task 3 closed for
    /// <c>PermissionCheckedElsewhere</c> with a source-scan pin over its call sites. A single
    /// <c>string[]</c> field has no call sites to scan; reflecting its value directly is the
    /// equivalent pin for this shape. Falsify by adding a third name to <c>UngatedTools</c> and
    /// re-running: this test is the one that fails.
    /// </summary>
    [Fact]
    public void UngatedTools_IsExactlyGuideAndRestore()
    {
        var field = typeof(McpPermissionFilter).GetField("UngatedTools", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "McpPermissionFilter.UngatedTools not found by reflection — has it been renamed or removed?");
        var ungated = (string[])field.GetValue(null)!;

        ungated.Should().BeEquivalentTo(["guide", "restore"],
            "a tool named here is reachable with ANY permission set, including none — a new entry " +
            "must be a deliberate, reviewed decision pinned by name, not a silent addition that " +
            "still passes every other coverage test");
    }
}
