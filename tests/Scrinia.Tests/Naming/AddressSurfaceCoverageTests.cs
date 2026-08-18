using System.Reflection;
using FluentAssertions;
using Scrinia.Mcp;

namespace Scrinia.Tests.Naming;

/// <summary>
/// Pins the ephemeral-alias policy at every surface that accepts an address as text.
/// Three of the five canonicalize and proceed; two deliberately refuse. Both answers
/// are correct for their surface — what this fixture catches is a surface that does
/// NEITHER, which is how three sites were missed on the ephemeral-addressing branch.
///
/// <para>The five surfaces and where each is pinned:</para>
/// <list type="table">
///   <item><term>MCP facade</term><description><c>ScriniaMcpTools</c> — canonicalize and
///     proceed. Pinned here (<see cref="McpFacade_TreatsTempPathAsTheEphemeralMemory"/>) and,
///     per verb, by <c>EphemeralAliasRoundTripTests</c>.</description></item>
///   <item><term>MCP resource</term><description><c>ScriniaMcpResources.ReadMemory</c> —
///     canonicalize and proceed. Pinned here, and nowhere else: this surface reaches
///     <c>IMemoryStore.ResolveArtifactAsync</c> directly, bypassing the tool methods.</description></item>
///   <item><term>Agent loop</term><description><c>AgentLoop.ExecuteRecallAsync</c> —
///     canonicalize and proceed. Lives in Scrinia.Server, which this project does not
///     reference; pinned by <c>Scrinia.Server.Tests.AgentLoopEphemeralAliasTests</c>.</description></item>
///   <item><term>CLI</term><description><c>ScriniaCommands.RefuseEphemeral</c> —
///     canonicalize then REFUSE, because the process exits before an ephemeral memory could
///     be read back. Pinned by <c>CliEphemeralRefusalTests</c>; not duplicated here.</description></item>
///   <item><term>REST</term><description><c>MemoryEndpoints.NameRefusal</c> — REFUSE, because
///     the alias would otherwise resolve to the persistent scope <c>local:temp</c>. Pinned by
///     <c>Scrinia.Server.Tests.EphemeralAliasRefusalTests</c>; not duplicated here.</description></item>
/// </list>
///
/// <para>Deliberately NOT one shared helper across all five: the three policies are considered
/// answers to three different process lifetimes, not an inconsistency to unify.</para>
/// </summary>
public class AddressSurfaceCoverageTests
{
    private static ScriniaMcpTools Tools() => new();

    [Fact]
    public async Task McpFacade_TreatsTempPathAsTheEphemeralMemory()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().MemoryRemember("/temp/probe", ["body"]);

        // Addressable under the canonical spelling, which is the whole rule.
        var recalled = ResponseParser.Parse(await Tools().MemoryRecall("~probe"));
        recalled.Status.Should().NotBe("error");
        recalled.Content.Should().Contain("body");
    }

    [Fact]
    public async Task McpResource_TreatsTempPathAsTheEphemeralMemory()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().MemoryRemember("~probe", ["body"]);

        // Reached through the resource method the UriTemplate binds to, which bypasses the tool
        // methods entirely — this is one of the two surfaces a type on the internal boundary
        // could never have covered. No test-only seam is needed: the resource method is already
        // public static and reads the ambient MemoryStoreContext, exactly as the MCP host calls it.
        string content = await ScriniaMcpResources.ReadMemory("/temp/probe");

        content.Should().Contain("body");
    }

    [Fact]
    public void EveryMcpToolTakingAnAddress_IsCoveredByThisFixture()
    {
        // Enumerates from the [McpServerTool] attribute set so a tool added later
        // that takes an address forces someone to extend this fixture.
        var addressTools = typeof(ScriniaMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttributes().Any(a => a.GetType().Name == "McpServerToolAttribute"))
            .Where(m => m.GetParameters().Any(p =>
                p.ParameterType == typeof(string) &&
                p.Name is "path" or "artifactOrName" or "nameOrUri" or "destination" or "source"))
            .Select(m => m.Name)
            .ToList();

        addressTools.Should().NotBeEmpty("the reflection query must actually match something");
        addressTools.Should().BeEquivalentTo(KnownAddressTools,
            "a tool taking an address must be added to this fixture's coverage");
    }

    // Update deliberately when a tool gains or loses an address parameter.
    private static readonly string[] KnownAddressTools =
    [
        "MemoryRemember", "MemoryRecall", "MemoryAppend",
        "MemoryForget", "MemoryCompact", "MemoryLink",
    ];
}
