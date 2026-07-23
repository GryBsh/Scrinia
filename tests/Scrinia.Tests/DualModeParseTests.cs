using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

/// <summary>
/// Integration tests for path-based name parsing (the legacy "topic:subject"
/// colon form is now rejected), FormatQualifiedName roundtrips, and legacy
/// on-disk fallback resolution.
/// </summary>
public sealed class DualModeParseTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public DualModeParseTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia-dual-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Dual-mode parsing tests ─────────────────────────────────────────────

    [Fact]
    public void ParseQualifiedName_ColonName_IsRejected()
    {
        // The v1 "topic:subject" colon syntax was removed — names are paths now.
        // Rejecting (rather than silently sanitizing the colon away) surfaces the
        // path to use.
        var act = () => _store.ParseQualifiedName("api:auth-flow");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*/api/auth-flow*");
    }

    [Fact]
    public void ParseQualifiedName_V2Path_ReturnsCorrectScope()
    {
        // v2 syntax: "/api/auth-flow" -> scope with "api", subject "auth-flow"
        var (scope, subject) = _store.ParseQualifiedName("/api/auth-flow");

        scope.Should().Be("local:api");
        subject.Should().Be("auth-flow");
    }

    [Fact]
    public void ParseQualifiedName_V2DeepPath_ReturnsHierarchicalScope()
    {
        // v2 deep path: "/goal/G-5/research/frontend"
        // PathParser sees "goal" as entity type -> EntityPair("goal","G-5"), then "research","frontend" as tags
        // All segments build scope: "local:goal/G-5/research", subject "frontend"
        var (scope, subject) = _store.ParseQualifiedName("/goal/G-5/research/frontend");

        scope.Should().Be("local:goal/G-5/research");
        subject.Should().Be("frontend");
    }

    [Fact]
    public void ParseQualifiedName_V2SingleSegment_ReturnsLocal()
    {
        // v2 single segment: "/frontend" -> ("local", "frontend")
        var (scope, subject) = _store.ParseQualifiedName("/frontend");

        scope.Should().Be("local");
        subject.Should().Be("frontend");
    }

    [Fact]
    public async Task StoreViaLegacyScope_RecallViaV2Deep_Works()
    {
        // Store using the legacy namespaced topic scope (the on-disk shape v1
        // colon names used to produce) -> .scrinia/topics/memory/research/
        string scopeV1 = MemoryNaming.BuildScopedTopicScope("research");
        string subjectV1 = _store.SanitizeName("frontend");
        await _store.WriteArtifactAsync(subjectV1, scopeV1, "v1 stored content");

        string v1Path = _store.ArtifactPath(subjectV1, scopeV1);
        File.Exists(v1Path).Should().BeTrue("v1 write should create the file");

        // Recall using v2 deep path "/goal/G-5/research/frontend"
        // v2 scope = "local:goal/G-5/research" -> .scrinia/memories/goal/G-5/research/
        // Legacy fallback finds .scrinia/topics/entity/research/ (namespaced) or
        // .scrinia/topics/research/ (flat). The ResolveV2LegacyDir uses the leaf "research".
        var (scopeV2, subjectV2) = _store.ParseQualifiedName("/goal/G-5/research/frontend");
        string content = await _store.ReadArtifactAsync(subjectV2, scopeV2);
        content.Should().Be("v1 stored content");
    }

    [Fact]
    public async Task StoreViaV2_RoundTrips()
    {
        // Store using v2 path syntax and read it straight back.
        var (scopeV2, subjectV2) = _store.ParseQualifiedName("/api/auth-flow");
        await _store.WriteArtifactAsync(subjectV2, scopeV2, "v2 stored content");

        string v2Path = _store.ArtifactPath(subjectV2, scopeV2);
        File.Exists(v2Path).Should().BeTrue("v2 write should create the file");

        string v2Content = await _store.ReadArtifactAsync(subjectV2, scopeV2);
        v2Content.Should().Be("v2 stored content");
    }

    // ── Legacy fallback tests ───────────────────────────────────────────────

    [Fact]
    public async Task ReadV2Path_FallsBackToV1Legacy()
    {
        // Create file directly in v1 flat location: .scrinia/topics/api/auth-flow.nmp2
        string legacyDir = Path.Combine(_root, ".scrinia", "topics", "api");
        Directory.CreateDirectory(legacyDir);
        string legacyFile = Path.Combine(legacyDir, "auth-flow.nmp2");
        await File.WriteAllTextAsync(legacyFile, "legacy v1 content");

        // Read via v2 path — should fall back to the v1 location
        var (scope, subject) = _store.ParseQualifiedName("/api/auth-flow");
        string content = await _store.ReadArtifactAsync(subject, scope);

        content.Should().Be("legacy v1 content");
    }

    [Fact]
    public async Task ReadV2DeepPath_FallsBackToNamespacedLegacy()
    {
        // skill is the only remaining entity topic; place legacy data under
        // .scrinia/topics/entity/skill/ and resolve via a v2 deep path that
        // ends in "skill".
        string nsDir = Path.Combine(_root, ".scrinia", "topics", "entity", "skill");
        Directory.CreateDirectory(nsDir);
        string nsFile = Path.Combine(nsDir, "frontend.nmp2");
        await File.WriteAllTextAsync(nsFile, "namespaced legacy content");

        var (scope, subject) = _store.ParseQualifiedName("/agent/profile/skill/frontend");
        string content = await _store.ReadArtifactAsync(subject, scope);

        content.Should().Be("namespaced legacy content");
    }

    [Fact]
    public async Task ReadV2DeepPath_PrefersPrimaryOverLegacy()
    {
        // Use a deep v2 path so the scope is a genuine v2 path scope
        var (scope, subject) = _store.ParseQualifiedName("/goal/G-5/research/frontend");

        // Write to v2 primary location (.scrinia/memories/goal/G-5/research/)
        await _store.WriteArtifactAsync(subject, scope, "v2 primary content");

        // Also create a legacy file in the fallback location
        // ResolveV2LegacyDir uses leaf "research" -> .scrinia/topics/research/
        string legacyDir = Path.Combine(_root, ".scrinia", "topics", "research");
        Directory.CreateDirectory(legacyDir);
        string legacyFile = Path.Combine(legacyDir, "frontend.nmp2");
        await File.WriteAllTextAsync(legacyFile, "v1 legacy content");

        // Reading should return v2 primary content, not legacy
        string content = await _store.ReadArtifactAsync(subject, scope);
        content.Should().Be("v2 primary content");
    }

    [Fact]
    public void IndexLoad_MergesV1EntriesIntoV2Scope()
    {
        // Create a v1 topic directory with sidecar metadata
        string legacyDir = Path.Combine(_root, ".scrinia", "topics", "api");
        Directory.CreateDirectory(legacyDir);

        // Write a sidecar .meta.json and an artifact .nmp2 in the v1 location
        var entry = new ArtifactEntry(
            "auth-flow", "file://auth-flow", 100, 1,
            DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(legacyDir, "auth-flow.nmp2"),
            "legacy artifact content");
        File.WriteAllText(
            Path.Combine(legacyDir, "auth-flow.meta.json"),
            System.Text.Json.JsonSerializer.Serialize(entry));

        // Use the legacy namespaced scope to load index — the legacy dir should be discovered
        string scopeV1 = MemoryNaming.BuildScopedTopicScope("api");
        var entries = _store.LoadIndex(scopeV1);

        entries.Should().Contain(e => e.Name == "auth-flow",
            because: "v1 sidecar entries in .scrinia/topics/api/ should be visible");
    }

    // ── FormatQualifiedName roundtrip tests ─────────────────────────────────

    [Fact]
    public void FormatQualifiedName_V2Scope_ReturnsPathSyntax()
    {
        // v2 scope from parsing "/goal/G-5/research/frontend"
        var (scope, subject) = _store.ParseQualifiedName("/goal/G-5/research/frontend");

        // FormatQualifiedName should reconstruct the v2 path syntax
        string formatted = _store.FormatQualifiedName(scope, subject);

        formatted.Should().Be("/goal/G-5/research/frontend");
    }

    [Fact]
    public void FormatQualifiedName_LegacyNamespacedScope_ReturnsPathSyntax()
    {
        // A legacy namespaced topic scope (local:memory/api) still formats
        // as a path for display.
        string scope = MemoryNaming.BuildScopedTopicScope("api");

        string formatted = _store.FormatQualifiedName(scope, "auth-flow");

        formatted.Should().Be("/api/auth-flow");
    }

    // ── Additional coverage ─────────────────────────────────────────────────

    [Fact]
    public void ParseQualifiedName_V2ThreeSegment_ReturnsCorrectScopeAndSubject()
    {
        // "/agent/profile" -> 2 segments, scope "local:agent", subject "profile"
        var (scope, subject) = _store.ParseQualifiedName("/agent/profile");

        scope.Should().Be("local:agent");
        subject.Should().Be("profile");
    }

    [Fact]
    public void FormatQualifiedName_LocalScope_ReturnsSubjectOnly()
    {
        // Local scope (no topic prefix) returns just the subject
        string formatted = _store.FormatQualifiedName("local", "my-notes");

        formatted.Should().Be("my-notes");
    }

    [Fact]
    public void ParseAndFormat_V2DeepPath_Roundtrips()
    {
        // Full roundtrip: parse v2 deep path -> format -> should get same path back
        // Only deep (3+ segment) paths produce v2 path scopes that roundtrip as paths.
        // 2-segment paths like "/api/auth-flow" produce a scope indistinguishable from
        // v1, so FormatQualifiedName returns v1 syntax "api:auth-flow" for those.
        string original = "/goal/G-5/research/frontend";
        var (scope, subject) = _store.ParseQualifiedName(original);
        string formatted = _store.FormatQualifiedName(scope, subject);

        formatted.Should().Be(original);
    }

    [Fact]
    public void ParseAndFormat_V2TwoSegmentPath_FormatsAsV1()
    {
        // 2-segment v2 paths produce a flat scope (no slash in topic part),
        // so FormatQualifiedName uses v1 topic:subject syntax.
        // This is intentional: 2-segment v2 paths are equivalent to v1 topics.
        var (scope, subject) = _store.ParseQualifiedName("/api/auth-flow");
        string formatted = _store.FormatQualifiedName(scope, subject);

        formatted.Should().Be("/api/auth-flow");
    }

    [Fact]
    public void ParseAndFormat_TwoSegmentPath_Roundtrips()
    {
        // A two-segment path roundtrips through parse -> format unchanged.
        string original = "/patterns/retry";
        var (scope, subject) = _store.ParseQualifiedName(original);
        string formatted = _store.FormatQualifiedName(scope, subject);

        formatted.Should().Be("/patterns/retry");
    }

    [Fact]
    public void ParseQualifiedName_V2EntityPath_ScopeIncludesEntitySegments()
    {
        // "/concern/SEC-054" -> entity pair concern/SEC-054
        // scope = "local:concern", subject = "SEC-054"
        var (scope, subject) = _store.ParseQualifiedName("/concern/SEC-054");

        scope.Should().Be("local:concern");
        subject.Should().Be("SEC-054");
    }

    [Fact]
    public async Task FindArtifactPath_V2WithLegacyFallback_FindsLegacyFile()
    {
        // Directly test FindArtifactPath with a v2 scope that has a legacy file
        string legacyDir = Path.Combine(_root, ".scrinia", "topics", "patterns");
        Directory.CreateDirectory(legacyDir);
        string legacyFile = Path.Combine(legacyDir, "retry.nmp2");
        await File.WriteAllTextAsync(legacyFile, "retry pattern content");

        // v2 scope for "/patterns/retry"
        var (scope, subject) = _store.ParseQualifiedName("/patterns/retry");

        // FindArtifactPath should fall back to legacy
        string found = _store.FindArtifactPath(subject, scope);
        File.Exists(found).Should().BeTrue("FindArtifactPath should resolve to the legacy file");

        string content = await File.ReadAllTextAsync(found);
        content.Should().Be("retry pattern content");
    }
}
