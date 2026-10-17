using System.Reflection;
using ModelContextProtocol.Server;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests.Wave1;

public class McpToolSurfaceTests
{
    private static readonly string[] ExpectedToolNames =
    [
        "compact", "facts", "forget", "guide", "link",
        "list", "recall", "remember", "restore", "search"
    ];

    [Fact]
    public void ScriniaMcpTools_ExposesExactly10Tools()
    {
        var tools = typeof(ScriniaMcpTools).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
            .ToArray();
        Assert.Equal(10, tools.Length);
    }

    [Fact]
    public void ScriniaMcpTools_NoPublicHelperOutsideToolSet()
    {
        var publicMethods = typeof(ScriniaMcpTools).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToArray();
        var toolMethods = publicMethods.Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null).ToArray();
        var helpers = publicMethods.Except(toolMethods).ToArray();
        Assert.Empty(helpers);
    }

    [Fact]
    public void ScriniaMcpTools_MatchesRefAttribute_SpellingIsSingleB()
    {
        var method = typeof(ScriniaMcpTools).GetMethod("MatchesRefAttribute",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
        Assert.NotNull(method);
        var typo = typeof(ScriniaMcpTools).GetMethod("MatchesRefAttributeb",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
        Assert.Null(typo);
    }

    [Fact]
    public void WithToolsRegistersExactly10()
    {
        var toolNames = typeof(ScriniaMcpTools).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name ?? m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        var expected = ExpectedToolNames.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, toolNames);
    }

    [Fact]
    public void ScriniaMcpTools_ReconcileIsNotATool()
    {
        var toolNames = typeof(ScriniaMcpTools).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name ?? m.Name)
            .ToArray();
        Assert.DoesNotContain("reconcile", toolNames, StringComparer.OrdinalIgnoreCase);
    }
}
