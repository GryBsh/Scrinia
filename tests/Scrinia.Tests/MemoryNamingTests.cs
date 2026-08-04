using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

public class MemoryNamingTests
{
    [Fact]
    public void StripEphemeralPrefix_RemovesTilde()
    {
        MemoryNaming.StripEphemeralPrefix("~scratch").Should().Be("scratch");
    }

    [Fact]
    public void StripEphemeralPrefix_NoTilde_ReturnsUnchanged()
    {
        MemoryNaming.StripEphemeralPrefix("notes").Should().Be("notes");
    }

    [Fact]
    public void StripEphemeralPrefix_EmptyString_ReturnsEmpty()
    {
        MemoryNaming.StripEphemeralPrefix("").Should().Be("");
    }

    [Fact]
    public void FormatScopeLabel_Local()
    {
        MemoryNaming.FormatScopeLabel("local").Should().Be("local");
    }

    [Fact]
    public void FormatScopeLabel_Ephemeral()
    {
        MemoryNaming.FormatScopeLabel("ephemeral").Should().Be("ephemeral");
    }

    [Fact]
    public void FormatScopeLabel_LocalTopic_ExtractsTopicName()
    {
        MemoryNaming.FormatScopeLabel("local:api").Should().Be("api");
    }

    [Fact]
    public void FormatScopeLabel_UnknownScope_ReturnsAsIs()
    {
        MemoryNaming.FormatScopeLabel("custom-scope").Should().Be("custom-scope");
    }

    // ── BuildScopedTopicScope ────────────────────────────────────────────────

    [Fact]
    public void BuildScopedTopicScope_Skill_RouteToEntityNamespace()
    {
        // v3: flattened — skill no longer gets an "entity/" namespace segment. (This test
        // duplicates FileMemoryStoreTests.BuildScopedTopicScope_EntityTopic_RoutesToEntityNamespace;
        // the brief's Task 7 correction named only that one, but this one pins the identical
        // pre-flattening behaviour and would otherwise fail for the same reason.)
        MemoryNaming.BuildScopedTopicScope("skill").Should().Be("local:skill");
    }

    [Fact]
    public void BuildScopedTopicScope_Agent_ReturnsSingleScope()
    {
        MemoryNaming.BuildScopedTopicScope("agent").Should().Be("local:agent",
            because: "agent is a single scope without a child directory");
    }

    [Theory]
    [InlineData("api", "local:api")]
    [InlineData("dotnet", "local:dotnet")]
    [InlineData("patterns", "local:patterns")]
    [InlineData("my-notes", "local:my-notes")]
    [InlineData("goal", "local:goal")]
    [InlineData("task", "local:task")]
    public void BuildScopedTopicScope_UserTopics_RouteToFlatTopic(string topic, string expected)
    {
        MemoryNaming.BuildScopedTopicScope(topic).Should().Be(expected);
    }

    // ── FormatScopeLabel with legacy namespace-prefixed scope literals ───────
    // Task 8 removed MemoryNaming.StripNamespacePrefix: v3 never produces an "entity/"/"memory/"
    // segment (BuildScopedTopicScope is fully flat), and DiscoverTopics no longer scans the old
    // topics/ namespace directories, so FormatScopeLabel has nothing left to strip — it now
    // returns the scope's topic part verbatim, even for one of these legacy-shaped literals.

    [Fact]
    public void FormatScopeLabel_LegacyEntityPrefixedLiteral_NoLongerStripped()
    {
        MemoryNaming.FormatScopeLabel("local:entity/task").Should().Be("entity/task");
    }

    [Fact]
    public void FormatScopeLabel_LegacyMemoryPrefixedLiteral_NoLongerStripped()
    {
        MemoryNaming.FormatScopeLabel("local:memory/api").Should().Be("memory/api");
    }

    [Fact]
    public void FormatScopeLabel_AgentScope_ReturnsAgent()
    {
        MemoryNaming.FormatScopeLabel("local:agent").Should().Be("agent");
    }
}
