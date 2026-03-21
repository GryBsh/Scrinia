using System.Text;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Unit tests for three new MCP tools:
///   update_meta  — merge keywords and update description without re-encoding
///   plan_tasks   — file-conflict detection across same-wave tasks
///   backlog_promote — promote a backlog entry to a new goal
/// </summary>
public sealed class NewToolTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaMcpTools _memTools;
    private readonly ScriniaProjectTools _projTools;

    public NewToolTests()
    {
        _scope = new TestHelpers.StoreScope();
        _memTools = new ScriniaMcpTools();
        _projTools = new ScriniaProjectTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── update_meta tests ───────────────────────────────────────────────────

    [Fact]
    public async Task UpdateMeta_MergesKeywords_UnionNotReplace()
    {
        // Arrange — store a memory with keywords ["a", "b"]
        await _memTools.Store(
            ["Some test content for keyword merge."],
            "meta-kw-test",
            keywords: ["a", "b"]);

        // Act — call update_meta with keywords ["b", "c"]
        string result = await _memTools.UpdateMeta("meta-kw-test", keywords: ["b", "c"]);

        // Assert — entry should now have ["a", "b", "c"] (union)
        result.Should().Contain("keyword", "response should mention keywords changed");

        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("meta-kw-test");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        // The entry keywords include auto-extracted terms, so check that a, b, c are all present
        entry.Keywords.Should().Contain("a", "keyword 'a' from original set must be preserved");
        entry.Keywords.Should().Contain("b", "keyword 'b' (shared) must be present");
        entry.Keywords.Should().Contain("c", "keyword 'c' from update must be added");
    }

    [Fact]
    public async Task UpdateMeta_MergesKeywords_ArtifactUnchanged()
    {
        // Arrange — store a memory and capture its .nmp2 artifact bytes
        string originalContent = "Artifact content must not change during metadata update.";
        await _memTools.Store([originalContent], "meta-art-test", keywords: ["a", "b"]);

        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("meta-art-test");
        string artifactBefore = await store.ResolveArtifactAsync("meta-art-test");

        // Act — update keywords
        await _memTools.UpdateMeta("meta-art-test", keywords: ["b", "c"]);

        // Assert — .nmp2 artifact text must be identical (no re-encoding)
        string artifactAfter = await store.ResolveArtifactAsync("meta-art-test");
        artifactAfter.Should().Be(artifactBefore,
            "update_meta must not re-encode the .nmp2 artifact");

        // Double-check content roundtrips correctly
        byte[] decoded = new Nmp2Strategy().Decode(artifactAfter);
        string restored = System.Text.Encoding.UTF8.GetString(decoded);
        restored.Should().Be(originalContent,
            "artifact content must remain unchanged after metadata update");
    }

    [Fact]
    public async Task UpdateMeta_UpdatesDescription()
    {
        // Arrange — store a memory with default description
        await _memTools.Store(["Description update test content."], "meta-desc-test");

        // Act — update description
        string result = await _memTools.UpdateMeta("meta-desc-test", description: "new desc");

        // Assert
        result.Should().Contain("description updated",
            "response should confirm description was updated");

        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("meta-desc-test");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
        entry.Description.Should().Be("new desc",
            "entry description should be replaced with the new value");
    }

    [Fact]
    public async Task UpdateMeta_MissingMemory_ReturnsError()
    {
        // Act — call update_meta on a nonexistent memory
        string result = await _memTools.UpdateMeta("nonexistent", keywords: ["x"]);

        // Assert
        result.Should().Contain("not found",
            "update_meta on a missing memory must return an error containing 'not found'");
        result.Should().StartWith("Error:",
            "error responses should start with 'Error:'");
    }

    // ── plan_tasks file-conflict detection tests ────────────────────────────

    private async Task SetupProjectAndRoadmap()
    {
        await _projTools.ProjectInit("Goals: test file conflicts", cancellationToken: CancellationToken.None);
        await _projTools.GoalUpdate("add", "Test file conflict detection", cancellationToken: CancellationToken.None);
        await _projTools.PlanRequirements(
            "- REQ-01: task decomposition\n- REQ-02: conflict detection",
            cancellationToken: CancellationToken.None);
        await _projTools.PlanRoadmap(
            "### Phase 1\nREQ-01, REQ-02 tasks",
            cancellationToken: CancellationToken.None);
    }

    [Fact]
    public async Task PlanTasks_FileConflict_DetectedForSameWaveTasks()
    {
        // Arrange
        await SetupProjectAndRoadmap();

        string tasksWithConflict = """
            ## Task 01
            Depends on: none
            Files: src/Foo.cs, src/Bar.cs
            Action: modify Foo and Bar

            ## Task 02
            Depends on: none
            Files: src/Bar.cs, src/Baz.cs
            Action: modify Bar and Baz
            """;

        // Act
        string result = await _projTools.PlanTasks("01", tasksWithConflict,
            cancellationToken: CancellationToken.None);

        // Assert — response must warn about the file conflict on Bar.cs
        result.Should().ContainEquivalentOf("conflict",
            "plan_tasks should detect and warn about file conflicts in same-wave tasks");
        result.Should().ContainEquivalentOf("Bar.cs",
            "file conflict warning should mention the conflicting file Bar.cs");
    }

    [Fact]
    public async Task PlanTasks_NoFilesField_NoConflictRegression()
    {
        // Arrange
        await SetupProjectAndRoadmap();

        string tasksWithoutFiles = """
            ## Task 01
            Depends on: none
            Action: Implement authentication
            Acceptance criteria:
            - Users can log in

            ## Task 02
            Depends on: none
            Action: Implement user profile
            Acceptance criteria:
            - Profile data is stored
            """;

        // Act
        string result = await _projTools.PlanTasks("01", tasksWithoutFiles,
            cancellationToken: CancellationToken.None);

        // Assert — no conflict warning when no Files: lines present
        result.Should().NotContainEquivalentOf("conflict",
            "plan_tasks without Files: lines should not produce any conflict warnings");
        result.Should().Contain("Created 2 task(s)",
            "tasks should be created normally when no Files: lines are present");
    }

    // ── backlog_promote tests ───────────────────────────────────────────────

    [Fact]
    public async Task BacklogPromote_PromotesBacklogEntryToGoal()
    {
        // Arrange — set up project context (required for goal_update)
        await _projTools.ProjectInit("Goals: test backlog promotion", cancellationToken: CancellationToken.None);

        // Store a backlog entry
        await _memTools.Store(
            ["Deferred resilience work"],
            "backlog:test-promote",
            description: "Test backlog item");

        // Act
        string result = await _projTools.BacklogPromote("backlog:test-promote",
            cancellationToken: CancellationToken.None);

        // Assert
        result.Should().Contain("Promoted",
            "backlog_promote should return a response containing 'Promoted'");
        result.Should().MatchRegex(@"G-\d+",
            "backlog_promote response should contain a goal ID like G-1");
    }

    [Fact]
    public async Task BacklogPromote_MissingEntry_ReturnsError()
    {
        // Arrange — set up project context
        await _projTools.ProjectInit("Goals: test backlog error", cancellationToken: CancellationToken.None);

        // Act
        string result = await _projTools.BacklogPromote("backlog:nonexistent",
            cancellationToken: CancellationToken.None);

        // Assert
        result.Should().Contain("not found",
            "backlog_promote on a missing entry must return an error containing 'not found'");
        result.Should().StartWith("Error:",
            "error responses should start with 'Error:'");
    }

    // ── compact() tests ──────────────────────────────────────────────────────

    [Fact]
    public async Task Compact_MergesAllChunks()
    {
        // Arrange — store a memory, then append twice to create 3 chunks
        await _memTools.Store(["Chunk one content."], "compact-merge-test");
        await _memTools.Append("Chunk two content.", "compact-merge-test");
        await _memTools.Append("Chunk three content.", "compact-merge-test");

        // Act
        string result = await _memTools.Compact("compact-merge-test");

        // Assert — response mentions compaction
        result.Should().Contain("Compacted",
            "compact response should confirm the memory was compacted");

        // Verify chunk count is now 1
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("compact-merge-test");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
        entry.ChunkCount.Should().Be(1,
            "after compact with no keepRecent, all chunks should merge into 1");

        // Verify all original content is still accessible
        string artifact = await store.ReadArtifactAsync(subject, scope);
        byte[] decoded = new Nmp2Strategy().Decode(artifact);
        string fullText = Encoding.UTF8.GetString(decoded);
        fullText.Should().Contain("Chunk one content.",
            "merged artifact must contain text from the first chunk");
        fullText.Should().Contain("Chunk two content.",
            "merged artifact must contain text from the second chunk");
        fullText.Should().Contain("Chunk three content.",
            "merged artifact must contain text from the third chunk");
    }

    [Fact]
    public async Task Compact_KeepRecent()
    {
        // Arrange — store + append 4 times = 5 chunks total
        await _memTools.Store(["Part one."], "compact-keep-test");
        await _memTools.Append("Part two.", "compact-keep-test");
        await _memTools.Append("Part three.", "compact-keep-test");
        await _memTools.Append("Part four.", "compact-keep-test");
        await _memTools.Append("Part five.", "compact-keep-test");

        // Act — keep only the 2 most recent chunks
        string result = await _memTools.Compact("compact-keep-test", keepRecent: 2);

        // Assert
        result.Should().Contain("Compacted",
            "compact response should confirm compaction occurred");

        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("compact-keep-test");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
        entry.ChunkCount.Should().Be(2,
            "after compact with keepRecent=2, chunk count should be 2");
    }

    [Fact]
    public async Task Compact_ArchivesOriginal()
    {
        // Arrange — store + append once = 2 chunks
        await _memTools.Store(["Original content."], "compact-archive-test");
        await _memTools.Append("Appended content.", "compact-archive-test");

        // Act
        string result = await _memTools.Compact("compact-archive-test");

        // Assert — a version file should exist in the versions/ directory
        result.Should().Contain("archived",
            "compact response should mention the original was archived");

        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("compact-archive-test");
        string storeDir = store.GetStoreDirForScope(scope);
        string versionsDir = Path.Combine(storeDir, "versions");
        Directory.Exists(versionsDir).Should().BeTrue(
            "versions/ directory should exist after compact archives the original");
        Directory.GetFiles(versionsDir, "*.nmp2").Should().NotBeEmpty(
            "versions/ directory should contain at least one .nmp2 archive file");
    }

    [Fact]
    public async Task Compact_SingleChunk_NoOp()
    {
        // Arrange — store a single-chunk memory (no appends)
        await _memTools.Store(["Single chunk only."], "compact-noop-test");

        // Act
        string result = await _memTools.Compact("compact-noop-test");

        // Assert — should indicate nothing to compact
        result.Should().Contain("single chunk",
            "compact on a single-chunk memory should indicate nothing to compact");
    }

    // ── suggest_patterns() tests ─────────────────────────────────────────────

    [Fact]
    public async Task SuggestPatterns_FindsOverlap()
    {
        // Arrange — create 3+ concern:* entries sharing the "retry" keyword
        await _memTools.Store(
            ["Concern A: retry failures in network layer"],
            "concern:issue-alpha",
            keywords: ["retry", "network"]);
        await _memTools.Store(
            ["Concern B: retry timeout handling"],
            "concern:issue-beta",
            keywords: ["retry", "timeout"]);
        await _memTools.Store(
            ["Concern C: retry logic in provider"],
            "concern:issue-gamma",
            keywords: ["retry", "provider"]);

        // Act
        string result = await _memTools.SuggestPatterns();

        // Assert — should detect "retry" appearing in 3+ entries
        result.Should().Contain("retry",
            "suggest_patterns should identify 'retry' as a recurring keyword");
        result.Should().Contain("3",
            "suggest_patterns should report that 3 entries share the keyword");
    }

    [Fact]
    public async Task SuggestPatterns_NoOverlap()
    {
        // Arrange — create 2 concerns with unique keywords (no 3+ overlap)
        await _memTools.Store(
            ["Concern X: unique issue one"],
            "concern:unique-one",
            keywords: ["alpha-unique"]);
        await _memTools.Store(
            ["Concern Y: unique issue two"],
            "concern:unique-two",
            keywords: ["beta-unique"]);

        // Act
        string result = await _memTools.SuggestPatterns();

        // Assert — no patterns should be detected
        result.Should().Contain("No recurring patterns",
            "suggest_patterns should report no patterns when fewer than 3 entries share a keyword");
    }
}
