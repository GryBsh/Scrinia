using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class PlanningTopicsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public PlanningTopicsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-planning-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new FileMemoryStore(_tempDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task StorePlanTopic_CreatesFileInTopicsDir()
    {
        // Arrange
        var (scope, subject) = _store.ParseQualifiedName("plan:phase-1");
        string artifact = Nmp2ChunkedEncoder.Encode("test content");

        // Act
        await _store.WriteArtifactAsync(subject, scope, artifact);

        // Assert
        string artifactPath = _store.ArtifactPath(subject, scope);
        File.Exists(artifactPath).Should().BeTrue("artifact file should exist at the resolved path");
        scope.Should().StartWith("local-topic:plan", "plan: prefix should resolve to local-topic:plan scope");
    }

    [Fact]
    public void AllPlanningTopics_ResolveCorrectly()
    {
        // All planning topic prefixes should resolve to local-topic:{prefix} scope
        string[] prefixes = ["plan", "task", "project", "learn"];

        foreach (string prefix in prefixes)
        {
            var (scope, subject) = _store.ParseQualifiedName($"{prefix}:test-memory");
            scope.Should().Be($"local-topic:{prefix}",
                $"prefix '{prefix}:' should resolve to scope 'local-topic:{prefix}'");
            subject.Should().Be("test-memory",
                $"subject should be 'test-memory' after stripping '{prefix}:' prefix");
        }
    }

    [Fact]
    public async Task ListScoped_WithPlanScope_ReturnsPlanEntries()
    {
        // Arrange — store an entry in the plan topic
        var (scope, subject) = _store.ParseQualifiedName("plan:phase-1");
        string artifact = Nmp2ChunkedEncoder.Encode("plan content");
        await _store.WriteArtifactAsync(subject, scope, artifact);

        var entry = new ArtifactEntry(
            Name: $"plan:{subject}",
            Uri: _store.ArtifactUri(subject, scope),
            OriginalBytes: 12,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Description: "Test plan entry");
        _store.Upsert(entry, scope);

        // Act
        var listed = _store.ListScoped("plan");

        // Assert
        listed.Should().HaveCountGreaterThanOrEqualTo(1, "ListScoped('plan') should return plan topic entries");
        listed.Should().Contain(x => x.Entry.Name.Contains("phase-1") || x.Entry.Name == $"plan:{subject}",
            "the stored plan entry should appear in ListScoped('plan') results");
    }

    [Fact]
    public async Task ListScoped_Null_IncludesPlanningTopics()
    {
        // Arrange — store entries in both plan topic and local scope
        var (planScope, planSubject) = _store.ParseQualifiedName("plan:my-plan");
        string planArtifact = Nmp2ChunkedEncoder.Encode("plan data");
        await _store.WriteArtifactAsync(planSubject, planScope, planArtifact);

        var planEntry = new ArtifactEntry(
            Name: $"plan:{planSubject}",
            Uri: _store.ArtifactUri(planSubject, planScope),
            OriginalBytes: 9,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Description: "Plan entry");
        _store.Upsert(planEntry, planScope);

        string localArtifact = Nmp2ChunkedEncoder.Encode("local data");
        await _store.WriteArtifactAsync("local-note", "local", localArtifact);

        var localEntry = new ArtifactEntry(
            Name: "local-note",
            Uri: _store.ArtifactUri("local-note", "local"),
            OriginalBytes: 10,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Description: "Local entry");
        _store.Upsert(localEntry, "local");

        // Act — null scopes means "all scopes"
        var allEntries = _store.ListScoped(null);

        // Assert — should include entries from both scopes
        allEntries.Should().Contain(x => x.Entry.Name == "local-note",
            "local entries should appear in ListScoped(null)");
        allEntries.Should().Contain(x => x.Entry.Name == $"plan:{planSubject}",
            "plan topic entries should appear in ListScoped(null)");
    }
}
