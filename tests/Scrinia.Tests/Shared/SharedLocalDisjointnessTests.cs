using System.Linq;
using System.Reflection;
using FluentAssertions;
using ModelContextProtocol.Server;
using Scrinia.Mcp;
using Scrinia.Mcp.SharedContracts;

namespace Scrinia.Tests.Shared;

/// <summary>
/// Locks local/shared disjointness: the local <c>scri serve</c> surface (its <c>ScriniaMcpTools</c>
/// registration and its single local store path) is unchanged, and the shared surface registers only
/// its own prefixed tools with no overlap against the local surface.
/// </summary>
public sealed class SharedLocalDisjointnessTests
{
    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void Local_serve_registration_and_store_are_unchanged_and_shared_registration_is_disjoint()
    {
        string localServe = File.ReadAllText(Path.Combine(RepositoryRoot, "src/Scrinia/Commands/ScriniaCommands.Serve.cs"));
        localServe.Should().Contain("WithTools<ScriniaMcpTools>");
        localServe.Should().NotContain("SharedTools");
        localServe.Should().NotContain("Scrinia.Mcp.SharedContracts");
        localServe.Should().NotContain("SharedServer");

        string sharedServe = File.ReadAllText(Path.Combine(RepositoryRoot, "src/Scrinia/Commands/ScriniaCommands.SharedServe.cs"));
        sharedServe.Should().Contain("WithTools<SharedTools>");
        sharedServe.Should().NotContain("WithTools<ScriniaMcpTools>");

        // The two surfaces register disjoint tool sets: every shared tool is prefixed, and none of
        // them collides with a local tool name.
        HashSet<string> localTools = ToolNamesOf(typeof(ScriniaMcpTools));
        HashSet<string> sharedTools = ToolNamesOf(typeof(SharedTools));

        sharedTools.Should().AllSatisfy(name => name.StartsWith("shared_", StringComparison.Ordinal));
        localTools.Intersect(sharedTools).Should().BeEmpty("the local and shared tool surfaces must not share any tool name");
    }

    private static HashSet<string> ToolNamesOf(Type toolType) =>
        toolType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
}
