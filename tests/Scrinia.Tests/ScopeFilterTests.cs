using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Tests for excludeTopics filtering on IMemoryStore ListScoped, SearchAll, and ResolveReadScopes.
/// Verifies that planning namespaces (plan:*, task:*, project:*, learn:*) can be excluded from
/// knowledge queries without breaking backward compatibility.
/// </summary>
public sealed class ScopeFilterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public ScopeFilterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-scopefilter-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new FileMemoryStore(_tempDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void StorePlanEntry(string name)
    {
        var (scope, subject) = _store.ParseQualifiedName($"plan:{name}");
        string artifact = Nmp2ChunkedEncoder.Encode($"Planning content for {name}");
        _store.WriteArtifactAsync(subject, scope, artifact).GetAwaiter().GetResult();
        _store.Upsert(new ArtifactEntry(
            Name: subject,
            Uri: _store.ArtifactUri(subject, scope),
            OriginalBytes: artifact.Length,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Description: $"Plan: {name}",
            Keywords: new[] { "planning", name }), scope);
    }

    private void StoreTaskEntry(string name)
    {
        var (scope, subject) = _store.ParseQualifiedName($"task:{name}");
        string artifact = Nmp2ChunkedEncoder.Encode($"Task content for {name}");
        _store.WriteArtifactAsync(subject, scope, artifact).GetAwaiter().GetResult();
        _store.Upsert(new ArtifactEntry(
            Name: subject,
            Uri: _store.ArtifactUri(subject, scope),
            OriginalBytes: artifact.Length,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Description: $"Task: {name}",
            Keywords: new[] { "task", name }), scope);
    }

    private void StoreKnowledgeEntry(string name)
    {
        string artifact = Nmp2ChunkedEncoder.Encode($"Knowledge content for {name}");
        _store.WriteArtifactAsync(name, "local", artifact).GetAwaiter().GetResult();
        _store.Upsert(new ArtifactEntry(
            Name: name,
            Uri: _store.ArtifactUri(name, "local"),
            OriginalBytes: artifact.Length,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Description: $"Knowledge: {name}",
            Keywords: new[] { "knowledge", name }), "local");
    }

    // ── ListScoped ───────────────────────────────────────────────────────────

    [Fact]
    public void ListScoped_ExcludePlanningTopics_ReturnsOnlyKnowledge()
    {
        StorePlanEntry("my-plan");
        StoreKnowledgeEntry("my-knowledge");

        var result = _store.ListScoped(null, "plan,task,project,learn");

        result.Should().HaveCount(1);
        result[0].Entry.Name.Should().Be("my-knowledge");
        result[0].Scope.Should().Be("local");
    }

    [Fact]
    public void ListScoped_NoExclude_ExcludesEntityScopes()
    {
        StorePlanEntry("my-plan");
        StoreKnowledgeEntry("my-knowledge");

        var result = _store.ListScoped(null, null);

        // Default excludes entity scopes — plan is entity-classified
        result.Should().HaveCount(1);
        result[0].Entry.Name.Should().Be("my-knowledge");
    }

    [Fact]
    public void ListScoped_ScopesAll_ReturnsAll()
    {
        StorePlanEntry("my-plan");
        StoreKnowledgeEntry("my-knowledge");

        var result = _store.ListScoped("all", null);

        result.Should().HaveCount(2);
    }

    [Fact]
    public void ListScoped_ExcludeMultipleTopics_ExcludesAllSpecified()
    {
        StorePlanEntry("p1");
        StoreTaskEntry("t1");
        StoreKnowledgeEntry("k1");

        var result = _store.ListScoped(null, "plan,task,project,learn");

        result.Should().HaveCount(1);
        result[0].Entry.Name.Should().Be("k1");
    }

    [Fact]
    public void ListScoped_ExcludeTopics_IsCaseInsensitive()
    {
        StorePlanEntry("case-test");
        StoreKnowledgeEntry("keep-this");

        // "Plan" (mixed case) should still exclude plan topic entries
        var result = _store.ListScoped(null, "Plan");

        result.Should().HaveCount(1);
        result[0].Entry.Name.Should().Be("keep-this");
    }

    // ── SearchAll ────────────────────────────────────────────────────────────

    [Fact]
    public void SearchAll_ExcludePlanningTopics_ExcludesPlanning()
    {
        StorePlanEntry("content-item");
        StoreKnowledgeEntry("content-item-knowledge");

        var result = _store.SearchAll("content", null, 20, "plan,task,project,learn");

        result.Should().NotBeEmpty();
        // No result should come from the plan topic scope
        bool anyPlan = result.Any(r => GetResultScope(r).StartsWith("local-topic:entity/plan", StringComparison.Ordinal));
        anyPlan.Should().BeFalse("planning entries should be excluded when excludeTopics includes 'plan'");
    }

    [Fact]
    public void SearchAll_NoExclude_ExcludesEntityScopes()
    {
        StorePlanEntry("searchable-plan");
        StoreKnowledgeEntry("searchable-knowledge");

        // Default search excludes entity scopes
        var result = _store.SearchAll("searchable", null, 20, (string?)null);

        result.Any(r => GetResultScope(r) == "local-topic:entity/plan").Should().BeFalse(
            "default search should exclude entity-classified scopes");
        result.Should().NotBeEmpty("knowledge entries should still appear");
    }

    [Fact]
    public void SearchAll_ScopesAll_IncludesEntityScopes()
    {
        StorePlanEntry("searchable-plan");
        StoreKnowledgeEntry("searchable-knowledge");

        // scopes="all" includes entity scopes
        var result = _store.SearchAll("searchable", "all", 20, (string?)null);

        result.Any(r => GetResultScope(r) == "local-topic:entity/plan").Should().BeTrue(
            "scopes='all' should include entity-classified scopes");
    }

    /// <summary>
    /// Extracts the scope from a SearchResult (handles EntryResult, ChunkEntryResult, TopicResult).
    /// </summary>
    private static string GetResultScope(SearchResult result) => result switch
    {
        EntryResult er => er.Item.Scope,
        ChunkEntryResult cr => cr.ParentItem.Scope,
        TopicResult tr => tr.Scope,
        _ => string.Empty
    };

    // ── ResolveReadScopes ────────────────────────────────────────────────────

    [Fact]
    public void ResolveReadScopes_ExcludePlan_OmitsPlanScope()
    {
        // Create a plan topic so it appears in discovered topics
        StorePlanEntry("scope-test");

        var scopes = _store.ResolveReadScopes(null, "plan");

        scopes.Should().NotContain("local-topic:entity/plan");
        scopes.Should().Contain("local"); // local scope still present
    }

    [Fact]
    public void ResolveReadScopes_ExcludeMultiple_OmitsAll()
    {
        StorePlanEntry("p1");
        StoreTaskEntry("t1");

        var scopes = _store.ResolveReadScopes(null, "plan,task");

        scopes.Should().NotContain("local-topic:entity/plan");
        scopes.Should().NotContain("local-topic:entity/task");
    }

    [Fact]
    public void ResolveReadScopes_NoExclude_ExcludesEntityScopes()
    {
        StorePlanEntry("p1");
        StoreKnowledgeEntry("k1");

        var scopes = _store.ResolveReadScopes(null, null);

        scopes.Should().Contain("local");
        scopes.Should().NotContain("local-topic:entity/plan",
            "default ResolveReadScopes should exclude entity-classified scopes");
    }

    [Fact]
    public void ResolveReadScopes_ScopesAll_IncludesEntityScopes()
    {
        StorePlanEntry("p1");
        StoreKnowledgeEntry("k1");

        var scopes = _store.ResolveReadScopes("all", null);

        scopes.Should().Contain("local");
        scopes.Should().Contain("local-topic:entity/plan");
    }

    [Fact]
    public void ResolveReadScopes_ExcludeTopics_IsCaseInsensitive()
    {
        StorePlanEntry("case-check");

        var scopes = _store.ResolveReadScopes(null, "Plan,TASK");

        scopes.Should().NotContain("local-topic:entity/plan");
    }

    // ── Ka() simulation ──────────────────────────────────────────────────────

    [Fact]
    public void KaResults_SameWithOrWithoutPlanningMemories()
    {
        // Store a knowledge entry first
        StoreKnowledgeEntry("arch-decisions");

        // Get a baseline count without planning data (simulating ka() with excludeTopics)
        var baselineWithExclude = _store.ListScoped(null, "plan,task,project,learn")
            .Where(e => e.Scope != "ephemeral")
            .ToList();
        int baselineCount = baselineWithExclude.Count;

        // Now store a planning entry
        StorePlanEntry("feature-001");

        // With excludeTopics, count should remain the same (planning entry excluded)
        var withPlanningExcluded = _store.ListScoped(null, "plan,task,project,learn")
            .Where(e => e.Scope != "ephemeral")
            .ToList();

        withPlanningExcluded.Should().HaveCount(baselineCount,
            "planning entries should not appear when excludeTopics='plan,task,project,learn'");
    }

    // ── REQ-35: Search scope defaults ────────────────────────────────────────

    private void StoreEntityEntry(string topic, string name)
    {
        var (scope, subject) = _store.ParseQualifiedName($"{topic}:{name}");
        string artifact = Nmp2ChunkedEncoder.Encode($"{topic} content for {name}");
        _store.WriteArtifactAsync(subject, scope, artifact).GetAwaiter().GetResult();
        _store.Upsert(new ArtifactEntry(
            Name: subject,
            Uri: _store.ArtifactUri(subject, scope),
            OriginalBytes: artifact.Length,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Description: $"{topic}: {name}",
            Keywords: new[] { topic, name }), scope);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("task")]
    [InlineData("concern")]
    [InlineData("plan")]
    [InlineData("goal")]
    [InlineData("skill")]
    [InlineData("research")]
    [InlineData("backlog")]
    [InlineData("requirement")]
    public void ResolveReadScopes_Default_ExcludesAllEntityTopics(string entityTopic)
    {
        // Store an entry in each entity topic so DiscoverTopics finds it
        StoreEntityEntry(entityTopic, "test-item");

        var scopes = _store.ResolveReadScopes(null);

        string expectedEntityScope = MemoryNaming.BuildScopedTopicScope(entityTopic);
        scopes.Should().NotContain(expectedEntityScope,
            $"default ResolveReadScopes should exclude entity topic '{entityTopic}' (scope '{expectedEntityScope}')");
    }

    [Theory]
    [InlineData("project")]
    [InlineData("task")]
    [InlineData("concern")]
    [InlineData("plan")]
    [InlineData("goal")]
    [InlineData("skill")]
    [InlineData("research")]
    [InlineData("backlog")]
    [InlineData("requirement")]
    public void ResolveReadScopes_All_IncludesAllEntityTopics(string entityTopic)
    {
        StoreEntityEntry(entityTopic, "test-item");

        var scopes = _store.ResolveReadScopes("all");

        string expectedEntityScope = MemoryNaming.BuildScopedTopicScope(entityTopic);
        scopes.Should().Contain(expectedEntityScope,
            $"scopes='all' should include entity topic '{entityTopic}' (scope '{expectedEntityScope}')");
    }

    [Theory]
    [InlineData("task")]
    [InlineData("project")]
    [InlineData("concern")]
    public void ResolveReadScopes_ExplicitScope_RoutesToEntityScope(string entityTopic)
    {
        StoreEntityEntry(entityTopic, "explicit-item");

        // Explicit scope string should resolve via BuildScopedTopicScope
        var scopes = _store.ResolveReadScopes(entityTopic);

        string expectedScope = MemoryNaming.BuildScopedTopicScope(entityTopic);
        scopes.Should().Contain(expectedScope,
            $"explicit scope '{entityTopic}' should resolve to '{expectedScope}'");
    }

    [Theory]
    [InlineData("task")]
    [InlineData("plan")]
    [InlineData("project")]
    public void ListScoped_ExplicitScope_ReturnsEntityEntries(string entityTopic)
    {
        StoreEntityEntry(entityTopic, "findable");

        // Explicit scope should return entity entries even though default excludes them
        var result = _store.ListScoped(entityTopic);

        result.Should().NotBeEmpty($"ListScoped('{entityTopic}') should return entries from that entity scope");
        result.Should().Contain(x => x.Entry.Name == "findable",
            $"the stored entry should be found via explicit scope '{entityTopic}'");
    }

    [Fact]
    public void ListScoped_Default_ExcludesEntityEntries_IncludesKnowledge()
    {
        // Store entries across multiple entity topics and one knowledge entry
        StoreEntityEntry("task", "my-task");
        StoreEntityEntry("project", "my-project");
        StoreEntityEntry("concern", "my-concern");
        StoreKnowledgeEntry("my-knowledge");

        var result = _store.ListScoped(null, null);

        result.Should().Contain(x => x.Entry.Name == "my-knowledge",
            "knowledge entries should appear in default list");
        result.Should().NotContain(x => x.Entry.Name == "my-task",
            "task entries should be excluded from default list");
        result.Should().NotContain(x => x.Entry.Name == "my-project",
            "project entries should be excluded from default list");
        result.Should().NotContain(x => x.Entry.Name == "my-concern",
            "concern entries should be excluded from default list");
    }

    [Fact]
    public void SearchAll_Default_ExcludesEntityEntries_IncludesKnowledge()
    {
        StoreEntityEntry("task", "search-target");
        StoreKnowledgeEntry("search-target-knowledge");

        // Default search (null scopes, null excludeTopics) should exclude entity scopes
        var result = _store.SearchAll("search-target", null, 20, (string?)null);

        result.Should().NotBeEmpty("knowledge entries should appear in default search");
        bool anyEntity = result.Any(r => GetResultScope(r).Contains("entity/"));
        anyEntity.Should().BeFalse("entity-scoped entries should be excluded from default search");
    }

    [Fact]
    public void SearchAll_ScopesAll_IncludesEntityEntries()
    {
        StoreEntityEntry("task", "findable-task");
        StoreKnowledgeEntry("findable-knowledge");

        var result = _store.SearchAll("findable", "all", 20, (string?)null);

        bool anyEntity = result.Any(r => GetResultScope(r).Contains("entity/"));
        anyEntity.Should().BeTrue("scopes='all' search should include entity-scoped entries");
    }

    [Fact]
    public void ExcludeTopics_BareTopicName_ExcludesNamespacedScope()
    {
        StoreEntityEntry("task", "excluded-item");
        StoreKnowledgeEntry("included-item");

        // Bare name "task" should exclude entity/task
        var result = _store.ListScoped("all", "task");

        result.Should().NotContain(x => x.Scope == "local-topic:entity/task",
            "excludeTopics='task' should exclude the entity/task scope");
        result.Should().Contain(x => x.Entry.Name == "included-item",
            "knowledge entries should still be included");
    }

    [Fact]
    public void ExcludeTopics_NamespacedName_ExcludesCorrectScope()
    {
        StoreEntityEntry("task", "excluded-namespaced");
        StoreEntityEntry("plan", "kept-plan");
        StoreKnowledgeEntry("kept-knowledge");

        // Using the full namespaced path "entity/task" should exclude only that scope
        var result = _store.ListScoped("all", "entity/task");

        result.Should().NotContain(x => x.Scope == "local-topic:entity/task",
            "excludeTopics='entity/task' should exclude the entity/task scope");
        result.Should().Contain(x => x.Entry.Name == "kept-plan" || x.Entry.Name == "kept-knowledge",
            "other scopes should not be affected");
    }

    [Fact]
    public void ResolveReadScopes_Default_ExcludesLegacyFlatEntityScope()
    {
        // Create a legacy flat "task" directory (not under entity/ namespace)
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "task");
        Directory.CreateDirectory(legacyDir);
        // Write a sidecar so it has entries
        string metaJson = """
        {
          "name": "legacy-task",
          "uri": "file:///tmp/legacy-task.nmp2",
          "originalBytes": 50,
          "chunkCount": 1,
          "createdAt": "2026-01-01T00:00:00+00:00",
          "description": "A legacy task entry"
        }
        """;
        File.WriteAllText(Path.Combine(legacyDir, "legacy-task.meta.json"), metaJson);

        var scopes = _store.ResolveReadScopes(null);

        // "task" is in EntityTopics, so "local-topic:task" (legacy flat) should be excluded
        scopes.Should().NotContain("local-topic:task",
            "legacy flat entity scopes like 'local-topic:task' should be excluded from default read scopes");
    }

    [Fact]
    public void ResolveReadScopes_Default_IncludesNonEntityFlatTopic()
    {
        // Create a legacy flat topic that is NOT an entity topic
        string customDir = Path.Combine(_tempDir, ".scrinia", "topics", "custom-notes");
        Directory.CreateDirectory(customDir);

        var scopes = _store.ResolveReadScopes(null);

        // "custom-notes" is not in EntityTopics, so it should be included in defaults
        scopes.Should().Contain("local-topic:custom-notes",
            "non-entity flat topics should still appear in default read scopes");
    }

    [Fact]
    public void ResolveReadScopes_Default_IncludesMemoryNamespacedTopics()
    {
        // Create a memory-namespaced topic (user knowledge)
        string memDir = Path.Combine(_tempDir, ".scrinia", "topics", "memory", "dotnet");
        Directory.CreateDirectory(memDir);

        var scopes = _store.ResolveReadScopes(null);

        scopes.Should().Contain("local-topic:memory/dotnet",
            "memory-namespaced topics should appear in default read scopes");
    }

    [Fact]
    public void ResolveReadScopes_Default_IncludesAgentScope()
    {
        // Create the agent scope
        string agentDir = Path.Combine(_tempDir, ".scrinia", "topics", "agent");
        Directory.CreateDirectory(agentDir);

        var scopes = _store.ResolveReadScopes(null);

        scopes.Should().Contain("local-topic:agent",
            "agent scope should appear in default read scopes (agent is not entity-classified)");
    }
}
