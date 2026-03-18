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
    public void ListScoped_NoExclude_ReturnsAll()
    {
        StorePlanEntry("my-plan");
        StoreKnowledgeEntry("my-knowledge");

        var result = _store.ListScoped(null, null);

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
        bool anyPlan = result.Any(r => GetResultScope(r).StartsWith("local-topic:plan", StringComparison.Ordinal));
        anyPlan.Should().BeFalse("planning entries should be excluded when excludeTopics includes 'plan'");
    }

    [Fact]
    public void SearchAll_NoExclude_IncludesPlanning()
    {
        StorePlanEntry("searchable-plan");
        StoreKnowledgeEntry("searchable-knowledge");

        // Without excludeTopics, planning entries should appear in results
        var result = _store.SearchAll("searchable", null, 20, (string?)null);

        // Should contain at least one result from the plan topic
        result.Any(r => GetResultScope(r) == "local-topic:plan").Should().BeTrue();
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

        scopes.Should().NotContain("local-topic:plan");
        scopes.Should().Contain("local"); // local scope still present
    }

    [Fact]
    public void ResolveReadScopes_ExcludeMultiple_OmitsAll()
    {
        StorePlanEntry("p1");
        StoreTaskEntry("t1");

        var scopes = _store.ResolveReadScopes(null, "plan,task");

        scopes.Should().NotContain("local-topic:plan");
        scopes.Should().NotContain("local-topic:task");
    }

    [Fact]
    public void ResolveReadScopes_NoExclude_IncludesAll()
    {
        StorePlanEntry("p1");
        StoreKnowledgeEntry("k1");

        var scopes = _store.ResolveReadScopes(null, null);

        scopes.Should().Contain("local");
        scopes.Should().Contain("local-topic:plan");
    }

    [Fact]
    public void ResolveReadScopes_ExcludeTopics_IsCaseInsensitive()
    {
        StorePlanEntry("case-check");

        var scopes = _store.ResolveReadScopes(null, "Plan,TASK");

        scopes.Should().NotContain("local-topic:plan");
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
}
