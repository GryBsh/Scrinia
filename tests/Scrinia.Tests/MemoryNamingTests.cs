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

    // ── ClassifyTopic ────────────────────────────────────────────────────────

    [Fact]
    public void ClassifyTopic_Skill_ReturnsEntity()
    {
        MemoryNaming.ClassifyTopic("skill").Should().Be("entity",
            because: "'skill' is the only topic that still routes to the entity namespace (legacy NMP/2 layout compat)");
    }

    [Fact]
    public void ClassifyTopic_Agent_ReturnsAgent()
    {
        MemoryNaming.ClassifyTopic("agent").Should().Be("agent");
    }

    [Theory]
    [InlineData("api")]
    [InlineData("dotnet")]
    [InlineData("patterns")]
    [InlineData("sessions")]
    [InlineData("my-custom-topic")]
    [InlineData("project")]
    [InlineData("task")]
    [InlineData("concern")]
    [InlineData("goal")]
    [InlineData("workflow")]
    public void ClassifyTopic_UserTopics_ReturnsMemory(string topic)
    {
        MemoryNaming.ClassifyTopic(topic).Should().Be("memory",
            because: $"'{topic}' is a regular topic that belongs in the memory namespace");
    }

    [Fact]
    public void ClassifyTopic_IsCaseInsensitive()
    {
        MemoryNaming.ClassifyTopic("Skill").Should().Be("entity");
        MemoryNaming.ClassifyTopic("AGENT").Should().Be("agent");
        MemoryNaming.ClassifyTopic("Project").Should().Be("memory");
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

    // ── StripNamespacePrefix ─────────────────────────────────────────────────

    [Fact]
    public void StripNamespacePrefix_EntityPrefix_StripsToTopicName()
    {
        MemoryNaming.StripNamespacePrefix("entity/task").Should().Be("task");
        MemoryNaming.StripNamespacePrefix("entity/project").Should().Be("project");
    }

    [Fact]
    public void StripNamespacePrefix_MemoryPrefix_StripsToTopicName()
    {
        MemoryNaming.StripNamespacePrefix("memory/api").Should().Be("api");
        MemoryNaming.StripNamespacePrefix("memory/dotnet").Should().Be("dotnet");
    }

    [Fact]
    public void StripNamespacePrefix_AgentPrefix_StripsToTopicName()
    {
        MemoryNaming.StripNamespacePrefix("agent/profile").Should().Be("profile");
    }

    [Fact]
    public void StripNamespacePrefix_NoPrefix_PassesThrough()
    {
        MemoryNaming.StripNamespacePrefix("api").Should().Be("api");
        MemoryNaming.StripNamespacePrefix("custom-topic").Should().Be("custom-topic");
    }

    [Fact]
    public void StripNamespacePrefix_UnknownPrefix_PassesThrough()
    {
        MemoryNaming.StripNamespacePrefix("unknown/something").Should().Be("unknown/something");
    }

    [Fact]
    public void StripNamespacePrefix_Agent_NoSlash_PassesThrough()
    {
        MemoryNaming.StripNamespacePrefix("agent").Should().Be("agent");
    }

    // ── FormatScopeLabel with namespace-prefixed scopes ──────────────────────

    [Fact]
    public void FormatScopeLabel_EntityNamespacedScope_StripsToTopicName()
    {
        MemoryNaming.FormatScopeLabel("local:entity/task").Should().Be("task");
        MemoryNaming.FormatScopeLabel("local:entity/project").Should().Be("project");
    }

    [Fact]
    public void FormatScopeLabel_MemoryNamespacedScope_StripsToTopicName()
    {
        MemoryNaming.FormatScopeLabel("local:memory/api").Should().Be("api");
        MemoryNaming.FormatScopeLabel("local:memory/dotnet").Should().Be("dotnet");
    }

    [Fact]
    public void FormatScopeLabel_AgentScope_ReturnsAgent()
    {
        MemoryNaming.FormatScopeLabel("local:agent").Should().Be("agent");
    }

    // ── ReservedNamespaceDirs ────────────────────────────────────────────────

    [Theory]
    [InlineData("entity")]
    [InlineData("memory")]
    [InlineData("agent")]
    public void ReservedNamespaceDirs_ContainsExpected(string dir)
    {
        MemoryNaming.ReservedNamespaceDirs.Should().Contain(dir);
    }

    [Theory]
    [InlineData("api")]
    [InlineData("task")]
    [InlineData("project")]
    public void ReservedNamespaceDirs_DoesNotContainTopicNames(string dir)
    {
        MemoryNaming.ReservedNamespaceDirs.Should().NotContain(dir,
            because: "only namespace directory names (entity, memory, agent) should be reserved");
    }

    // ── FormatRoundTrip ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("local:memory/api", "auth", "/memory/api/auth")] // local: keeps the namespace segment
    [InlineData("local", "SEC-001", "SEC-001")]                  // bare local -> subject as-is
    [InlineData("ephemeral", "note", "note")]                    // non-local -> subject as-is
    public void FormatRoundTrip_MatchesLegacyDefault(string scope, string subject, string expected) =>
        MemoryNaming.FormatRoundTrip(scope, subject).Should().Be(expected);
}
