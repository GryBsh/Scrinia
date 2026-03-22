using System.Text;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Unit tests for MCP tools:
///   update_meta  — merge keywords and update description without re-encoding
///   plan_tasks   — file-conflict detection across same-wave tasks
///   compact      — merge chunks into fewer chunks
///   reconcile    — scan and resolve merge conflicts (including former resolve_conflict)
///   skill version reconciliation
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
        result.Should().Contain("Created 6 task(s)",
            "tasks should be created normally when no Files: lines are present " +
            "(2 user tasks + 4 auto-injected gate tasks for last phase)");
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

    // ── skill version reconciliation tests ───────────────────────────────────

    /// <summary>Sets up a project so skill_create prerequisite check passes.</summary>
    private async Task InitProject()
    {
        await _projTools.ProjectInit("Goals: test skill versioning", cancellationToken: CancellationToken.None);
    }

    [Fact]
    public async Task SkillCreate_OverridingBuiltIn_StoresBasedOnHash()
    {
        // Arrange — initialize project so skill_create prerequisite passes
        await InitProject();

        // Act — create a skill that overrides the built-in "planner" skill
        await _projTools.SkillCreate("planner", "custom", "test override", null, CancellationToken.None);

        // Assert — the stored index entry should have a basedOn: keyword
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("skill:planner");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e => e.Name.Equals("planner", StringComparison.OrdinalIgnoreCase));

        entry.Keywords.Should().NotBeNull("skill entry should have keywords");
        entry.Keywords.Should().Contain(k => k.StartsWith("basedOn:", StringComparison.Ordinal),
            "overriding a built-in skill must record the built-in's hash as a basedOn: keyword");
    }

    [Fact]
    public async Task SkillCreate_NoBuiltIn_NoBasedOnHash()
    {
        // Arrange
        await InitProject();

        // Act — create a skill that does NOT match any built-in name
        await _projTools.SkillCreate("my-custom-skill", "custom", "totally custom", null, CancellationToken.None);

        // Assert — the stored entry should NOT have a basedOn: keyword
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("skill:my-custom-skill");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e => e.Name.Equals("my-custom-skill", StringComparison.OrdinalIgnoreCase));

        entry.Keywords.Should().NotBeNull("skill entry should have keywords");
        entry.Keywords.Should().NotContain(k => k.StartsWith("basedOn:", StringComparison.Ordinal),
            "a skill that does not override a built-in should not have a basedOn: keyword");
    }

    [Fact]
    public async Task SkillLoad_FreshOverride_NoWarning()
    {
        // Arrange — create a fresh override of the built-in "planner" skill
        await InitProject();
        await _projTools.SkillCreate("planner", "custom", "fresh override", null, CancellationToken.None);

        // Act — load the skill immediately (hash should match current built-in)
        string result = await _projTools.SkillLoad("planner", cancellationToken: CancellationToken.None);

        // Assert — no stale warning because the basedOn hash matches the current built-in
        result.Should().NotContain("WARNING",
            "loading a freshly-created override should not produce a stale warning");
    }

    [Fact]
    public async Task SkillLoad_Reconcile_ShowsBothVersions()
    {
        // Arrange — create an override of a built-in skill
        await InitProject();
        await _projTools.SkillCreate("planner", "custom", "project-specific planner", null, CancellationToken.None);

        // Act — load with reconcile=true
        string result = await _projTools.SkillLoad("planner", reconcile: true, cancellationToken: CancellationToken.None);

        // Assert — response must contain both the built-in and override sections
        result.Should().Contain("Current Built-in",
            "reconcile mode must show the 'Current Built-in' section");
        result.Should().Contain("Your Project Override",
            "reconcile mode must show the 'Your Project Override' section");
    }

    // ── WriteSidecar sorting tests ──────────────────────────────────────────

    [Fact]
    public async Task WriteSidecar_SortsKeywordsAlphabetically()
    {
        // Arrange & Act — store a memory with deliberately unsorted keywords
        await _memTools.Store(
            ["test content for keyword sorting"],
            "sort-test",
            keywords: ["zebra", "apple", "mango"]);

        // Read the .meta.json file directly from disk
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        string metaPath = store.MetaPath("sort-test");
        string json = File.ReadAllText(metaPath);

        // Deserialize and verify keywords are sorted alphabetically
        var entry = JsonSerializer.Deserialize<ArtifactEntry>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        entry.Should().NotBeNull();
        entry!.Keywords.Should().NotBeNullOrEmpty("stored entry should have keywords");

        // Filter to just the user-supplied keywords to verify their relative order
        var userKeywords = entry.Keywords!
            .Where(k => k is "apple" or "mango" or "zebra")
            .ToList();

        userKeywords.Should().HaveCount(3, "all three user-supplied keywords should be present");
        userKeywords.Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase,
            "keywords in .meta.json must be sorted alphabetically (G-29 merge safety)");

        // Also verify via raw JSON that "apple" appears before "mango" appears before "zebra"
        int appleIdx = json.IndexOf("\"apple\"", StringComparison.Ordinal);
        int mangoIdx = json.IndexOf("\"mango\"", StringComparison.Ordinal);
        int zebraIdx = json.IndexOf("\"zebra\"", StringComparison.Ordinal);

        appleIdx.Should().BeLessThan(mangoIdx, "in raw JSON, 'apple' should appear before 'mango'");
        mangoIdx.Should().BeLessThan(zebraIdx, "in raw JSON, 'mango' should appear before 'zebra'");
    }

    [Fact]
    public async Task WriteSidecar_SortsTermFrequencyKeys()
    {
        // Arrange & Act — store content that will produce term frequencies
        // Use words that will appear in TF and whose keys we can verify ordering on
        await _memTools.Store(
            ["zebra apple mango zebra apple mango zebra apple mango"],
            "tf-sort-test");

        // Read the .meta.json file directly from disk
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        string metaPath = store.MetaPath("tf-sort-test");
        string json = File.ReadAllText(metaPath);

        // Parse the JSON and extract the termFrequencies object
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.TryGetProperty("termFrequencies", out var tfElement).Should().BeTrue(
            ".meta.json should contain a termFrequencies property");
        tfElement.ValueKind.Should().Be(JsonValueKind.Object,
            "termFrequencies should be a JSON object");

        // Extract all keys and verify they are sorted alphabetically
        var keys = tfElement.EnumerateObject()
            .Select(p => p.Name)
            .ToList();

        keys.Should().HaveCountGreaterThan(0, "termFrequencies should have at least one key");

        var sortedKeys = keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        keys.Should().Equal(sortedKeys,
            "termFrequency keys in .meta.json must be sorted alphabetically (G-29 merge safety)");
    }

    // ── reconcile() tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task Reconcile_NoConflicts_ReportsClean()
    {
        // Arrange — store a valid memory so .scrinia/ has a clean .meta.json
        await _memTools.Store(["Clean content, no conflicts."], "reconcile-clean-test");

        // Act
        string result = await _memTools.Reconcile();

        // Assert
        result.Should().Contain("No merge conflicts",
            "reconcile on a clean .scrinia/ directory should report no conflicts");
    }

    [Fact]
    public async Task Reconcile_MetaJsonConflict_AutoResolves()
    {
        // Arrange — write a .meta.json with realistic git conflict markers
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        string storeDir = store.GetStoreDirForScope("local");

        // Build the conflicted content line by line to avoid raw-string indentation issues
        var sb = new StringBuilder();
        sb.AppendLine("<<<<<<< HEAD");
        sb.AppendLine("{");
        sb.AppendLine("  \"name\": \"test\",");
        sb.AppendLine("  \"uri\": \"file://test.nmp2\",");
        sb.AppendLine("  \"originalBytes\": 100,");
        sb.AppendLine("  \"chunkCount\": 1,");
        sb.AppendLine("  \"createdAt\": \"2026-03-21T00:00:00Z\",");
        sb.AppendLine("  \"description\": \"ours desc\",");
        sb.AppendLine("  \"keywords\": [\"alpha\", \"shared\"],");
        sb.AppendLine("  \"updatedAt\": \"2026-03-21T20:00:00Z\"");
        sb.AppendLine("}");
        sb.AppendLine("=======");
        sb.AppendLine("{");
        sb.AppendLine("  \"name\": \"test\",");
        sb.AppendLine("  \"uri\": \"file://test.nmp2\",");
        sb.AppendLine("  \"originalBytes\": 100,");
        sb.AppendLine("  \"chunkCount\": 1,");
        sb.AppendLine("  \"createdAt\": \"2026-03-21T00:00:00Z\",");
        sb.AppendLine("  \"description\": \"theirs desc\",");
        sb.AppendLine("  \"keywords\": [\"beta\", \"shared\"],");
        sb.AppendLine("  \"updatedAt\": \"2026-03-21T21:00:00Z\"");
        sb.AppendLine("}");
        sb.AppendLine(">>>>>>> feature-branch");
        string conflictedMeta = sb.ToString();

        string metaPath = Path.Combine(storeDir, "test.meta.json");
        File.WriteAllText(metaPath, conflictedMeta);

        // Act
        string result = await _memTools.Reconcile();

        // Assert — response mentions auto-resolution
        result.Should().Contain("auto-resolved",
            "reconcile should report auto-resolved for .meta.json conflicts");

        // The file should no longer contain conflict markers
        string resolved = File.ReadAllText(metaPath);
        resolved.Should().NotContain("<<<<<<<",
            "resolved .meta.json must not contain git conflict markers");

        // Keywords should be the union of both sides: alpha, beta, shared
        using var doc = JsonDocument.Parse(resolved);
        var keywords = doc.RootElement.GetProperty("keywords")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();

        keywords.Should().Contain("alpha",
            "resolved keywords must include 'alpha' from ours");
        keywords.Should().Contain("beta",
            "resolved keywords must include 'beta' from theirs");
        keywords.Should().Contain("shared",
            "resolved keywords must include 'shared' from both sides");
    }

    [Fact]
    public async Task Reconcile_Nmp2Conflict_ReportsManual()
    {
        // Arrange — write a .nmp2 file with fake conflict markers
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        string storeDir = store.GetStoreDirForScope("local");

        string conflictedNmp2 = "<<<<<<< HEAD\nsome binary data ours\n=======\nsome binary data theirs\n>>>>>>> feature-branch\n";
        File.WriteAllText(Path.Combine(storeDir, "conflicted.nmp2"), conflictedNmp2);

        // Act
        string result = await _memTools.Reconcile();

        // Assert — .nmp2 conflicts require manual resolution
        result.Should().Contain("manual resolution",
            "reconcile should report that .nmp2 conflicts need manual resolution");
    }

    // ── structured reconciliation flow tests ─────────────────────────────────

    [Fact]
    public async Task Reconcile_AssignsConflictIds_ForNmp2()
    {
        // Arrange — write a .nmp2 file with conflict markers
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        string storeDir = store.GetStoreDirForScope("local");

        string conflictedNmp2 =
            "<<<<<<< HEAD\nours side content\n=======\ntheirs side content\n>>>>>>> feature-branch\n";
        File.WriteAllText(Path.Combine(storeDir, "id-test.nmp2"), conflictedNmp2);

        // Act
        string result = await _memTools.Reconcile();

        // Assert — response must contain a CONFLICT- ID and decoded content from both sides
        result.Should().Contain("CONFLICT-",
            "reconcile should assign a CONFLICT-N ID to the .nmp2 conflict");
        result.Should().Contain("ours side content",
            "reconcile should show decoded/raw content from the ours side");
        result.Should().Contain("theirs side content",
            "reconcile should show decoded/raw content from the theirs side");
    }

    [Fact]
    public async Task Reconcile_Ours_ResolvesAndRemoves()
    {
        // Arrange — write a .nmp2 file with conflict markers
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        string storeDir = store.GetStoreDirForScope("local");
        string filePath = Path.Combine(storeDir, "resolve-ours.nmp2");

        string conflictedNmp2 =
            "<<<<<<< HEAD\nours content here\n=======\ntheirs content here\n>>>>>>> feature-branch\n";
        File.WriteAllText(filePath, conflictedNmp2);

        // Act — reconcile to register the conflict, then extract the ID
        string reconcileResult = await _memTools.Reconcile();

        // Extract the CONFLICT-N ID from the reconcile response
        var match = System.Text.RegularExpressions.Regex.Match(reconcileResult, @"CONFLICT-\d+");
        match.Success.Should().BeTrue("reconcile should produce a CONFLICT-N ID");
        string conflictId = match.Value;

        // Resolve with "ours" via reconcile(conflictId, choice)
        string resolveResult = await _memTools.Reconcile(conflictId: conflictId, choice: "ours");

        // Assert — the file should no longer have conflict markers
        resolveResult.Should().Contain("Resolved",
            "reconcile(conflictId, choice:'ours') should confirm resolution");
        string fileContent = File.ReadAllText(filePath);
        fileContent.Should().NotContain("<<<<<<<",
            "resolved file must not contain git conflict markers");

        // Reconcile again — should report clean (0 remaining)
        string secondReconcile = await _memTools.Reconcile();
        secondReconcile.Should().Contain("No merge conflicts",
            "after resolving the only conflict, reconcile should report no conflicts");
    }

    [Fact]
    public async Task Reconcile_Merged_WritesCustomContent()
    {
        // Arrange — write a .nmp2 file with conflict markers
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        string storeDir = store.GetStoreDirForScope("local");
        string filePath = Path.Combine(storeDir, "resolve-merged.nmp2");

        string conflictedNmp2 =
            "<<<<<<< HEAD\nours for merge\n=======\ntheirs for merge\n>>>>>>> feature-branch\n";
        File.WriteAllText(filePath, conflictedNmp2);

        // Act — reconcile to register the conflict, then resolve with merged content
        string reconcileResult = await _memTools.Reconcile();
        var match = System.Text.RegularExpressions.Regex.Match(reconcileResult, @"CONFLICT-\d+");
        match.Success.Should().BeTrue("reconcile should produce a CONFLICT-N ID");
        string conflictId = match.Value;

        string mergedContent = "my merged content";
        string resolveResult = await _memTools.Reconcile(conflictId: conflictId, choice: "merged", content: mergedContent);

        // Assert — the file should contain the merged content re-encoded as NMP/2
        resolveResult.Should().Contain("Resolved",
            "reconcile(conflictId, choice:'merged', content) should confirm resolution");

        string fileContent = File.ReadAllText(filePath);
        fileContent.Should().NotContain("<<<<<<<",
            "resolved file must not contain git conflict markers");

        // Decode the NMP/2 artifact and verify it contains the merged content
        byte[] decoded = new Scrinia.Core.Encoding.Nmp2Strategy().Decode(fileContent);
        string decodedText = Encoding.UTF8.GetString(decoded);
        decodedText.Should().Be(mergedContent,
            "resolved NMP/2 artifact should decode to the custom merged content");
    }

    [Fact]
    public async Task Reconcile_InvalidId_ReturnsError()
    {
        // Act — call reconcile with a nonexistent conflict ID (no prior scan)
        string result = await _memTools.Reconcile(conflictId: "CONFLICT-999", choice: "ours");

        // Assert — should return an error indicating the ID was not found
        result.Should().ContainAny(["not found", "Error"],
            "reconcile with an invalid conflictId must return an error");
    }

    [Fact]
    public async Task Reconcile_ReportsRemainingCount()
    {
        // Arrange — write TWO .nmp2 files with conflict markers
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        string storeDir = store.GetStoreDirForScope("local");

        string conflict1 =
            "<<<<<<< HEAD\nfirst ours\n=======\nfirst theirs\n>>>>>>> feature-branch\n";
        string conflict2 =
            "<<<<<<< HEAD\nsecond ours\n=======\nsecond theirs\n>>>>>>> feature-branch\n";
        File.WriteAllText(Path.Combine(storeDir, "remaining-a.nmp2"), conflict1);
        File.WriteAllText(Path.Combine(storeDir, "remaining-b.nmp2"), conflict2);

        // Act — reconcile should find 2 conflicts
        string firstReconcile = await _memTools.Reconcile();
        firstReconcile.Should().Contain("2 conflict(s) remaining",
            "reconcile should report 2 conflicts when two .nmp2 files have markers");

        // Extract the first CONFLICT- ID and resolve it
        var match = System.Text.RegularExpressions.Regex.Match(firstReconcile, @"CONFLICT-\d+");
        match.Success.Should().BeTrue("reconcile should produce at least one CONFLICT-N ID");
        string firstConflictId = match.Value;

        await _memTools.Reconcile(conflictId: firstConflictId, choice: "ours");

        // Act — reconcile again (it re-scans from disk, so only the unresolved file remains)
        string secondReconcile = await _memTools.Reconcile();
        secondReconcile.Should().Contain("1 conflict(s) remaining",
            "after resolving one of two conflicts, reconcile should report 1 remaining");
    }

    // ── project_init merge infrastructure scaffolding tests ──────────────────

    [Fact]
    public async Task ProjectInit_ScaffoldsGitattributes()
    {
        // Arrange — use the StoreScope's workspace directory (already set up by constructor)
        // The StoreScope creates: {WorkspaceDir}/.scrinia/store/ and sets MemoryStoreContext.Current

        // Act — initialize the project
        await _projTools.ProjectInit("Goals: test gitattributes scaffolding",
            cancellationToken: CancellationToken.None);

        // Assert — .scrinia/.gitattributes should exist and contain the binary marker
        string gitattributesPath = Path.Combine(_scope.WorkspaceDir, ".scrinia", ".gitattributes");
        File.Exists(gitattributesPath).Should().BeTrue(
            "project_init should scaffold .scrinia/.gitattributes");
        string content = File.ReadAllText(gitattributesPath);
        content.Should().Contain("*.nmp2 binary",
            ".gitattributes should mark *.nmp2 files as binary for merge safety");
    }

}
