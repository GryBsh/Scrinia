using System.Reflection;
using FluentAssertions;
using ModelContextProtocol.Server;
using Scrinia.Mcp.SharedContracts;

namespace Scrinia.Tests.Shared;

/// <summary>
/// Task 11 — prompt isolation. The shared MCP schema carries no prompt, correlation, or session
/// field and the shared surface never branches on prompt text: every shared response is derived
/// from the operation's own inputs (query, base, path, manifest, …) only. Guidance is computed from
/// the operation manifest, so identical operation inputs yield identical guidance regardless of any
/// ambient initiating prompt.
/// </summary>
public sealed class SharedNoPromptCorrelationTests
{
    private static readonly string[] ForbiddenParameterNames =
    [
        "prompt", "correlation", "correlationid", "initiatingprompt", "ambientprompt",
        "session", "sessionid", "sessionidentity", "conversationid"
    ];

    [Fact]
    public void Shared_responses_are_independent_of_initiating_prompt_or_correlation()
    {
        // The ten shared tools exist and every public shared handler is a shared tool.
        var methods = typeof(SharedTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .ToArray();
        methods.Should().HaveCount(10);
        methods.All(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null).Should().BeTrue();

        // No shared input parameter transports a prompt, correlation, or session identifier.
        foreach (MethodInfo method in methods)
        {
            string[] forbiddenInMethod = method.GetParameters()
                .Select(p => p.Name?.ToLowerInvariant() ?? "")
                .Where(name => ForbiddenParameterNames.Contains(name, StringComparer.Ordinal))
                .ToArray();
            forbiddenInMethod.Should().BeEmpty(
                "{0} must not carry a prompt/correlation/session parameter (§7.3)", method.Name);
        }

        // The orchestration surface is likewise free of any prompt/correlation argument.
        foreach (MethodInfo method in typeof(SharedServer).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            string[] forbiddenInServer = method.GetParameters()
                .Select(p => p.Name?.ToLowerInvariant() ?? "")
                .Where(name => ForbiddenParameterNames.Contains(name, StringComparer.Ordinal))
                .ToArray();
            forbiddenInServer.Should().BeEmpty(
                "SharedServer.{0} must not carry a prompt/correlation/session parameter", method.Name);
        }

        // Operation-based guidance is a pure function of the operation inputs: the same manifest
        // yields the same guidance with no reference to any ambient initiating prompt.
        string[] manifest = [".scrinia/memories/patterns/review.nmp2"];
        string? guidanceA = SharedServer.GuidanceForMutation("/srv/team", manifest);
        string? guidanceB = SharedServer.GuidanceForMutation("/srv/team", manifest);
        guidanceA.Should().BeEquivalentTo(guidanceB);
        guidanceA.Should().NotContain("prompt");
    }
}
