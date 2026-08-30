using System.Reflection;
using ConsoleAppFramework;
using FluentAssertions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Commands;
using Scrinia.Mcp;

namespace Scrinia.Tests.Naming;

/// <summary>
/// Pins the ephemeral-alias policy at every surface that accepts an address as text.
/// Two of the three canonicalize and proceed; one deliberately refuses. Both answers
/// are correct for their surface — what this fixture catches is a surface that does
/// NEITHER, which is how three sites were missed on the ephemeral-addressing branch.
///
/// <para>The three surfaces and where each is pinned:</para>
/// <list type="table">
///   <item><term>MCP facade</term><description><c>ScriniaMcpTools</c> — canonicalize and
///     proceed. Pinned here (<see cref="McpFacade_TreatsTempPathAsTheEphemeralMemory"/>) and,
///     per verb, by <c>EphemeralAliasRoundTripTests</c>.</description></item>
///   <item><term>MCP resource</term><description><c>ScriniaMcpResources.ReadMemory</c> —
///     canonicalize and proceed. Pinned here, and nowhere else: this surface reaches
///     the typed artifact-read path directly, bypassing the tool methods.</description></item>
///   <item><term>CLI</term><description><c>ScriniaCommands.RefuseEphemeral</c> —
///     canonicalize then REFUSE, because the process exits before an ephemeral memory could
///     be read back. Pinned by <c>CliEphemeralRefusalTests</c>; not duplicated here.</description></item>
/// </list>
///
/// <para>Deliberately NOT one shared helper across all three: the two policies are considered
/// answers to different process lifetimes, not an inconsistency to unify.</para>
/// </summary>
public class AddressSurfaceCoverageTests
{
    private static ScriniaMcpTools Tools() => new();

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

    [Theory]
    [MemberData(nameof(ReservedMcpRememberAddresses))]
    public async Task McpRemember_ReservedAndOrdinaryAddresses_ReturnSuccessEnvelope(string address)
    {
        using var scope = new TestHelpers.StoreScope();

        var response = ResponseParser.Parse(await Tools().MemoryRemember(address, ["body"]));

        response.Status.Should().Be("success", because: address);
    }

    public static TheoryData<string> ReservedMcpRememberAddresses => new()
    {
        "/temp/probe", "~probe", "/skill/name", "/agent/name", "/findings/probe"
    };

    [Theory]
    [MemberData(nameof(ResourceAddressCases))]
    public async Task McpResource_AddressCases_UseNamedPolicy(string address, bool isSkillException)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = Tools();
        if (address == "/agent/name")
        {
            string agentScope = MemoryNaming.BuildScopedTopicScope("agent");
            var entry = new ArtifactEntry("name", "", 4, 1, DateTimeOffset.UtcNow);
            TestHelpers.Store.Upsert(entry, agentScope);
            await TestHelpers.Store.WriteArtifactAsync(
                "name", agentScope, Nmp2ChunkedEncoder.Encode("body"));
        }
        else
        {
            await tools.MemoryRemember(address == "/temp/probe" ? "~probe" : address, ["body"]);
        }

        if (isSkillException)
        {
            // Resources resolve store artifacts directly; unlike the MCP facade they do not route
            // /skill/name through the skill-file surface. This is an intentional named exception.
            await Assert.ThrowsAsync<McpException>(() => ScriniaMcpResources.ReadMemory(address));
            return;
        }

        (await ScriniaMcpResources.ReadMemory(address)).Should().Contain("body", because: address);
    }

    public static TheoryData<string, bool> ResourceAddressCases => new()
    {
        { "/temp/probe", false },
        { "~probe", false },
        { "/agent/name", false },
        { "/findings/probe", false },
        { "/skill/name", true },
    };

    [Fact]
    public void EveryMcpToolTakingAnAddress_IsCoveredByThisFixture()
    {
        var toolMethods = typeof(ScriniaMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => m.Name)
            .ToList();

        toolMethods.Should().BeEquivalentTo(McpToolInventory.Select(tool => tool.MethodName),
            "every MCP tool must have explicit typed metadata; a new tool must not be silently omitted");

        foreach (var tool in McpToolInventory)
        {
            var method = typeof(ScriniaMcpTools).GetMethod(tool.MethodName,
                BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"MCP tool '{tool.MethodName}' is missing.");
            var parameters = method.GetParameters()
                .Select(parameter => parameter.Name!)
                .ToList();
            var classifiedParameters = tool.AddressParameters.Concat(tool.ExcludedParameters);
            tool.AddressParameters.Intersect(tool.ExcludedParameters).Should().BeEmpty(
                $"MCP metadata for {tool.MethodName} must not classify a parameter as both address and metadata");
            parameters.Should().BeEquivalentTo(classifiedParameters,
                $"MCP metadata for {tool.MethodName} must classify every parameter, including collections, custom types, and framework metadata");
        }
    }

    [Fact]
    public void EveryCliCommandTakingAnAddress_IsCoveredByThisFixture()
    {
        var positionalArguments = typeof(ScriniaCommands)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == typeof(Task<int>))
            .SelectMany(m => m.GetParameters()
                // ConsoleAppFramework generates ArgumentAttribute into the consuming assembly;
                // CustomAttributeData preserves the real attribute identity without the test
                // project's generated type shadowing the command assembly's type.
                .Where(p => p.GetCustomAttributesData().Any(attribute =>
                    attribute.AttributeType == typeof(ArgumentAttribute) ||
                    attribute.AttributeType.FullName == typeof(ArgumentAttribute).FullName))
                .Select(p => $"{m.Name}.{p.Name}"))
            .ToList();

        var expectedPositionalArguments = CliCommandInventory
            .SelectMany(command => command.AddressParameters.Concat(command.ExcludedParameters)
                .Select(parameter => $"{command.CommandName}.{parameter}"))
            .ToList();
        positionalArguments.Should().BeEquivalentTo(expectedPositionalArguments,
            "every positional CLI input must be classified explicitly as an address or a named non-address exception");

        var addressArguments = CliCommandInventory
            .SelectMany(command => command.AddressParameters.Select(parameter => $"{command.CommandName}.{parameter}"))
            .ToList();
        addressArguments.Should().BeEquivalentTo(CliAddressInventory.Select(address => address.Name),
            "the address inventory must exclude query, entity, config, and file inputs");

        foreach (var address in CliAddressInventory)
        {
            typeof(CliEphemeralRefusalTests).GetMethod(address.RefusalTest,
                BindingFlags.Public | BindingFlags.Instance)
                .Should().NotBeNull($"{address.Name} must have the named refusal coverage");
        }
    }

    [Fact]
    public void EveryMcpResourceTakingAnAddress_IsCoveredByThisFixture()
    {
        var addressResources = typeof(ScriniaMcpResources)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<McpServerResourceAttribute>() is not null)
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(string)))
            .Select(m => m.Name)
            .ToList();

        addressResources.Should().BeEquivalentTo(KnownAddressResources,
            "a resource taking an address must have a named path-policy case here");
    }

    [Fact]
    public void AddressPolicyCases_NameAllReservedAndOrdinaryForms()
    {
        KnownAddressForms.Should().BeEquivalentTo(
            ["/temp/probe", "~probe", "/skill/name", "/agent/name", "/findings/probe"],
            "the contract must keep the ephemeral alias, skill, agent, and ordinary path forms explicit");
    }

    private sealed record McpToolMetadata(
        string MethodName,
        string[] AddressParameters,
        string[] ExcludedParameters);

    private sealed record CliAddressMetadata(string Name, string RefusalTest);

    private sealed record CliCommandMetadata(
        string CommandName,
        string[] AddressParameters,
        string[] ExcludedParameters);

    // Explicitly enumerate every MCP tool. Address-bearing parameters are metadata rather than a
    // parameter-name heuristic, so a newly added tool fails this test until its policy is named.
    private static readonly McpToolMetadata[] McpToolInventory =
    [
        new("Guide", [], ["cancellationToken"]),
        new("MemoryRemember", ["path"], ["content", "attributes", "keywords", "cancellationToken"]),
        new("MemoryRecall", ["path"], ["chunk", "withBuiltin", "cancellationToken"]),
        new("MemorySearch", [], ["query", "context", "scopes", "limit", "excludeTopics", "pathPrefix", "attributeFilter", "cancellationToken"]),
        new("MemoryList", [], ["scopes", "mode", "offset", "limit", "excludeTopics", "cancellationToken"]),
        new("MemoryAppend", ["path"], ["content", "attributes", "keywords", "cancellationToken"]),
        new("MemoryForget", ["path"], ["cancellationToken"]),
        new("MemoryCompact", ["path"], ["keepRecent", "cancellationToken"]),
        new("MemoryLink", ["path", "destination"], ["reason", "cancellationToken"]),
        new("MemoryRestore", [], ["cancellationToken"]),
        new("MemoryReconcile", [], ["conflictId", "choice", "mergedContent", "cancellationToken"]),
        new("Facts", [], ["op", "entity", "entities", "key", "scopes", "excludeTopics", "limit", "cancellationToken"]),
    ];

    private static readonly CliAddressMetadata[] CliAddressInventory =
    [
        new("Store.name", "Store_RefusesAnEphemeralAddress"),
        new("Show.name", "Show_RefusesAnEphemeralAddress"),
        new("Forget.name", "Forget_RefusesAnEphemeralAddress"),
        new("Append.name", "Append_RefusesAnEphemeralAddress"),
        new("Compact.name", "Compact_RefusesAnEphemeralAddress"),
        new("Link.from", "Link_RefusesAnEphemeralAddressOnEitherEnd"),
        new("Link.to", "Link_RefusesAnEphemeralAddressOnEitherEnd"),
    ];

    private static readonly CliCommandMetadata[] CliCommandInventory =
    [
        new("Facts", [], ["op", "target"]),
        new("Search", [], ["query"]),
        new("Store", ["name"], ["file"]),
        new("Show", ["name"], []),
        new("Forget", ["name"], []),
        new("Append", ["name"], ["file"]),
        new("Compact", ["name"], []),
        new("Link", ["from", "to"], []),
        new("Config", [], ["key", "value"]),
    ];

    private static readonly string[] KnownAddressResources = ["ReadMemory"];

    private static readonly string[] KnownAddressForms =
    ["/temp/probe", "~probe", "/skill/name", "/agent/name", "/findings/probe"];
}
