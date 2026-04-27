using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Comprehensive unit tests for the NMP/2 MCP tools:
///   Show(artifactOrName, chunk?)             → decoded string or error message (chunk param for individual chunk retrieval)
///   Store(content, name, description, tags)  → confirmation string
///   List(scopes?, mode?)                     → index as formatted text (mode='drift' for code reference drift)
///   Search(query, scopes?, limit)            → scored results table
///   Copy(name, destination, overwrite)       → confirmation string
///   Forget(name)                             → confirmation string
///   Export(topics, filename?)                → confirmation string
///   Import(bundlePath, topics?, overwrite)   → confirmation string
///   Append(content, name)                    → confirmation string
///
/// All tests are offline — no LLM or external service required.
/// Store/List/Forget tests use TestHelpers.StoreScope to isolate
/// from the real user store.
/// </summary>
public sealed class ScriniaMcpToolsTests
{
    private static ScriniaMcpTools Tools() => new();

    // Single-chunk content: under 20 000 chars threshold (no \n\n needed)
    private static string MediumContent(int approxChars = 18_000)
    {
        const string line = "Alpha bravo charlie delta echo foxtrot golf hotel india juliet.\n";
        int reps = approxChars / line.Length + 2;
        string raw = string.Concat(Enumerable.Repeat(line, reps));
        return raw[..Math.Min(approxChars, raw.Length)];
    }

    // ── Show (10 tests) ─────────────────────────────────────────────────────

    [Fact]
    public async Task Show_SmallNmp2Inline_ExactOriginal()
    {
        string original = TestHelpers.Facts.Fact1;
        string artifact = Nmp2ChunkedEncoder.Encode(original);

        string result = await Tools().Show(artifact);
        var parsed = ResponseParser.Parse(result);

        parsed.Status.Should().Be("success");
        parsed.Content.Should().Be(original,
            because: "Show must restore the exact original text from a small inline artifact");
    }

    [Fact]
    public async Task Show_MediumNmp2Inline_SingleChunk_ExactOriginal()
    {
        string original = MediumContent(); // ~18 000 chars — single chunk
        string artifact = Nmp2ChunkedEncoder.Encode(original);

        Nmp2Strategy.IsMultiChunk(artifact).Should().BeFalse(
            because: "Encode() always produces single-chunk artifacts");

        string result = await Tools().Show(artifact);
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success");
        // Content may be truncated by the 8KB YAML response cap — verify it starts correctly
        parsed.Content.Should().StartWith(original[..500],
            because: "single-chunk decode must contain the original text (may be truncated by YAML response cap)");
    }

    [Fact]
    public async Task Show_MultiChunkNmp2Inline_ExactOriginal()
    {
        string[] parts = ["Part A: alpha.", "Part B: bravo."];
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(parts);

        Nmp2Strategy.IsMultiChunk(artifact).Should().BeTrue(
            because: "two-element EncodeChunks produces multi-chunk artifact");

        string result = await Tools().Show(artifact);
        var parsed = ResponseParser.Parse(result);
        // Multi-chunk Show prepends "(N chunks)\n\n" header
        string expected = string.Concat(parts);
        parsed.Content.Should().Contain(expected,
            because: "multi-chunk Show must reassemble and contain the exact original text");
        parsed.Content.Should().Contain("chunks)",
            because: "multi-chunk Show should include a chunk count header");
    }

    [Fact]
    public async Task Show_FileUri_SingleChunk_ExactOriginal()
    {
        using var scope = new TestHelpers.StoreScope();
        string original = TestHelpers.Facts.Fact13;
        string artifact = Nmp2ChunkedEncoder.Encode(original);
        string path = Path.Combine(scope.TempDir, $"fileuri_test_{Guid.NewGuid():N}.nmp2");
        await File.WriteAllTextAsync(path, artifact);

        string result = await Tools().Show($"file://{path}");
        ResponseParser.Parse(result).Content.Should().Be(original,
            because: "Show must read and decode a file:// URI within workspace .scrinia/ directory");
    }

    [Fact]
    public async Task Show_FileUri_MultiChunk_ExactOriginal()
    {
        using var scope = new TestHelpers.StoreScope();
        string[] parts = ["Part A: alpha.", "Part B: bravo."];
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(parts);
        string path = Path.Combine(scope.TempDir, $"fileuri_test_{Guid.NewGuid():N}.nmp2");
        await File.WriteAllTextAsync(path, artifact);

        string result = await Tools().Show($"file://{path}");
        // Multi-chunk Show prepends "(N chunks)\n\n" header
        string expected = string.Concat(parts);
        ResponseParser.Parse(result).Content.Should().Contain(expected,
            because: "Show must read and decode a file:// URI within workspace .scrinia/ directory");
    }

    [Fact]
    public async Task Show_UnicodeRoundtrip_EmojisAndCjkAndRtl()
    {
        string original = "Emoji: 🎉🚀🌍\nCJK: 日本語 中文 한국어\nRTL: مرحبا بالعالم\nMath: ∑∫√π\n";
        string artifact = Nmp2ChunkedEncoder.Encode(original);

        string result = await Tools().Show(artifact);

        // YAML block scalars may add a trailing newline; trim to compare
        ResponseParser.Parse(result).Content!.TrimEnd().Should().Be(original.TrimEnd(),
            because: "Unicode characters (emoji, CJK, RTL, math) must roundtrip exactly through NMP/2");
    }

    [Fact]
    public async Task Show_SourceCodeRoundtrip_ExactMatch()
    {
        string original = TestHelpers.LoadHumanEvalText()[..2_000];
        string artifact = Nmp2ChunkedEncoder.Encode(original);

        string result = await Tools().Show(artifact);
        var parsed = ResponseParser.Parse(result);

        parsed.Status.Should().Be("success");
        // YAML block scalars collapse consecutive blank lines;
        // verify all non-empty lines are present and in order
        var expectedLines = original.TrimEnd().Split('\n')
            .Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        var actualLines = parsed.Content!.TrimEnd().Split('\n')
            .Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        actualLines.Should().BeEquivalentTo(expectedLines, opts => opts.WithStrictOrdering(),
            because: "source code with backticks, braces, and special characters must roundtrip through NMP/2");
    }

    [Fact]
    public async Task Show_NonNmp2Artifact_ReturnsErrorString()
    {
        // A TAMIS/2 artifact header — not an NMP/2 artifact
        string fakeArtifact = "TAMIS/2 42B CRC32:DEADBEEF K:3\nsome body\nTAMIS/END";

        string result = await Tools().Show(fakeArtifact);

        ResponseParser.Parse(result).Status.Should().Be("error",
            because: "non-NMP/2 artifacts must return an error status");
    }

    [Fact]
    public async Task Show_Mnde1Artifact_ReturnsErrorString()
    {
        // MNDE/1 artifacts are no longer supported by Show
        string fakeArtifact = "MNDE/1 42B CRC32:DEADBEEF V:1\nsome body\nMNDE/END";

        string result = await Tools().Show(fakeArtifact);

        ResponseParser.Parse(result).Status.Should().Be("error",
            because: "MNDE/1 artifacts are not supported by the trimmed Show tool");
    }

    [Fact]
    public async Task Show_NonExistentFileUri_ReturnsError()
    {
        using var scope = new TestHelpers.StoreScope();
        string badUri = $"file://{Path.GetTempPath()}scrinia_nonexistent_{Guid.NewGuid():N}.nmp2";

        // file:// URI outside workspace is blocked by sandbox — returns error or throws
        try
        {
            string result = await Tools().Show(badUri);
            ResponseParser.Parse(result).Status.Should().Be("error",
                because: "file:// URI outside workspace must return an error");
        }
        catch (UnauthorizedAccessException)
        {
            // Also acceptable — sandbox threw before Show could catch
        }
    }

    // ── Show via memory name (2 tests) ─────────────────────────────────────

    [Fact]
    public async Task Show_ByMemoryName_ExactOriginal()
    {
        using var scope = new TestHelpers.StoreScope();
        string original = TestHelpers.Facts.Excerpt;
        await Tools().Store([original], "unpack-name-test");

        string result = await Tools().Show("unpack-name-test");

        ResponseParser.Parse(result).Content.Should().Be(original,
            because: "Show must resolve a memory name to its artifact and decode");
    }

    [Fact]
    public async Task Show_NonExistentName_ReturnsNotFoundError()
    {
        using var scope = new TestHelpers.StoreScope();

        string result = await Tools().Show("nonexistent-memory");
        var parsed = ResponseParser.Parse(result);

        parsed.Status.Should().Be("error",
            because: "Show on a non-existent memory name must return an error");
        parsed.Error.Should().Contain("not found",
            because: "the error must indicate the memory was not found");
        parsed.Error.Should().Contain("nonexistent-memory",
            because: "the error must include the name that was not found");
    }

    // ── Store (7 tests) ────────────────────────────────────────────────────

    [Fact]
    public async Task Store_SmallContent_ReturnsConfirmationString()
    {
        using var scope = new TestHelpers.StoreScope();
        string result = await Tools().Store([TestHelpers.Facts.Fact1], "test-memory");
        var parsed = ResponseParser.Parse(result);

        parsed.Status.Should().Be("success",
            because: "Store must return a success status");
        parsed.Content.Should().Contain("Remembered:",
            because: "Store must return a confirmation string");
        parsed.Content.Should().Contain("test-memory",
            because: "confirmation must include the memory name");
        parsed.Content.Should().Contain("1 chunk",
            because: "confirmation must include chunk count");
    }

    [Fact]
    public async Task Store_AppearsInMemoriesAfterCall()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store([TestHelpers.Facts.Fact1], "appear-test");

        string memories = await Tools().List(mode: "full");

        ResponseParser.Parse(memories).Content.Should().Contain("appear-test",
            because: "a stored artifact must be listed in List()");
    }

    [Fact]
    public async Task Store_OverwriteSameName_UpdatesIndex()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["first content"], "overwrite-test", "first desc");
        await Tools().Store(["second content"], "overwrite-test", "second desc");

        string memories = await Tools().List(mode: "full");
        var content = ResponseParser.Parse(memories).Content!;

        content.Should().Contain("overwrite-test",
            because: "overwritten artifact must still appear in List");
        content.Should().Contain("second desc",
            because: "the description must reflect the most recent Store call");
        content.Should().NotContain("first desc",
            because: "the old description must be replaced, not duplicated");
    }

    [Fact]
    public async Task Store_AutoDescription_UsesFirst200Chars()
    {
        using var scope = new TestHelpers.StoreScope();
        string content = new string('x', 300);

        await Tools().Store([content], "auto-desc-test"); // no description

        var entries = ScriniaArtifactStore.LoadIndex();
        entries.Should().ContainSingle(e => e.Name == "auto-desc-test");
        entries[0].Description.Length.Should().Be(200,
            because: "auto-description must use exactly the first 200 chars when no description is given");
    }

    [Fact]
    public async Task Store_ExplicitDescription_UsedAsIs()
    {
        using var scope = new TestHelpers.StoreScope();
        string explicitDesc = "My custom description here.";

        await Tools().Store([TestHelpers.Facts.Fact1], "explicit-desc-test", explicitDesc);

        var entries = ScriniaArtifactStore.LoadIndex();
        entries.Should().ContainSingle(e => e.Name == "explicit-desc-test");
        entries[0].Description.Should().Be(explicitDesc,
            because: "when an explicit description is supplied it must be used verbatim");
    }

    [Fact]
    public async Task Store_WithTags_StoresTagsInIndex()
    {
        using var scope = new TestHelpers.StoreScope();
        string[] tags = ["csharp", "dependency-injection"];

        await Tools().Store(["DI patterns"], "tagged-memory", "DI notes", tags);

        var entries = ScriniaArtifactStore.LoadIndex();
        entries.Should().ContainSingle(e => e.Name == "tagged-memory");
        entries[0].Tags.Should().BeEquivalentTo(tags,
            because: "tags must be persisted in the index");
    }

    [Fact]
    public async Task Store_StoresContentPreview()
    {
        using var scope = new TestHelpers.StoreScope();
        string content = "This is a test content that should appear in the preview.";

        await Tools().Store([content], "preview-test");

        var entries = ScriniaArtifactStore.LoadIndex();
        entries.Should().ContainSingle(e => e.Name == "preview-test");
        entries[0].ContentPreview.Should().Contain("test content",
            because: "content preview must be stored in the index");
    }

    // ── List (3 tests) ────────────────────────────────────────────────────

    [Fact]
    public async Task List_NoArtifacts_ReturnsEmptyNotice()
    {
        using var scope = new TestHelpers.StoreScope();

        string result = await Tools().List();

        ResponseParser.Parse(result).Content.Should().Be("No memories stored.",
            because: "List() must return a specific message when the store is empty");
    }

    [Fact]
    public async Task List_Summary_ShowsTopicsAndStats()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["api notes"], "api:auth-flow", keywords: ["auth", "api"]);
        await Tools().Store(["arch notes"], "arch:decisions", keywords: ["architecture"]);
        await Tools().Store(["local note"], "quick-ref");

        string result = await Tools().List(); // default = summary
        var content = ResponseParser.Parse(result).Content!;

        content.Should().Contain("Memory Summary");
        content.Should().Contain("3 memories");
        content.Should().Contain("2 topic");
        content.Should().Contain("topic:api");
        content.Should().Contain("topic:arch");
        content.Should().Contain("local");
        content.Should().Contain("Top keywords");
        content.Should().Contain("auth");
        content.Should().Contain("memory('search'");
    }

    [Fact]
    public async Task List_Summary_EphemeralIncluded()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["persistent"], "some-note");
        await Tools().Store(["temp"], "~scratch");

        string result = await Tools().List();
        var content = ResponseParser.Parse(result).Content!;

        content.Should().Contain("2 memories");
        content.Should().Contain("1 ephemeral");
    }

    [Fact]
    public async Task List_Full_Pagination()
    {
        using var scope = new TestHelpers.StoreScope();
        for (int i = 0; i < 5; i++)
            await Tools().Store([$"content {i}"], $"page-test-{i}");

        string result = await Tools().List(mode: "full", offset: 2, limit: 2);

        ResponseParser.Parse(result).Content.Should().Contain("Showing 3-4 of 5");
    }

    [Fact]
    public async Task List_AfterRemember_ContainsName()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store([TestHelpers.Facts.Fact50], "marie-curie");

        string result = await Tools().List(mode: "full");

        ResponseParser.Parse(result).Content.Should().Contain("marie-curie",
            because: "the stored artifact name must appear in List(mode='full') output");
    }

    [Fact]
    public async Task List_MultipleEntries_SortedNewestFirst()
    {
        using var scope = new TestHelpers.StoreScope();
        // Store two artifacts with a small delay so timestamps differ
        await Tools().Store(["alpha content"], "alpha-entry");
        await Task.Delay(10);
        await Tools().Store(["beta content"], "beta-entry");

        string result = await Tools().List(mode: "full");
        var content = ResponseParser.Parse(result).Content!;

        int alphaPos = content.IndexOf("alpha-entry", StringComparison.Ordinal);
        int betaPos  = content.IndexOf("beta-entry",  StringComparison.Ordinal);

        alphaPos.Should().BeGreaterThan(0, because: "alpha-entry must appear in output");
        betaPos.Should().BeGreaterThan(0,  because: "beta-entry must appear in output");
        betaPos.Should().BeLessThan(alphaPos,
            because: "most-recently added entry (beta) must appear before older entry (alpha)");
    }

    // ── Forget (4 tests) ──────────────────────────────────────────────────────

    [Fact]
    public async Task Forget_ByName_RemovesFileAndIndex()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store([TestHelpers.Facts.Fact1], "forget-me");
        string filePath = ScriniaArtifactStore.ArtifactPath("forget-me");
        File.Exists(filePath).Should().BeTrue(because: "file must exist before Forget");

        string result = await Tools().Forget("forget-me");
        var parsed = ResponseParser.Parse(result);

        parsed.Content.Should().Contain("Forgot",
            because: "Forget must return a confirmation string");
        parsed.Content.Should().Contain("forget-me");
        File.Exists(filePath).Should().BeFalse(
            because: "the .nmp2 file must be deleted by Forget");
        ScriniaArtifactStore.LoadIndex().Should().BeEmpty(
            because: "the index entry must be removed by Forget");
    }

    [Fact]
    public async Task Forget_ByUri_RemovesIndexedMemory()
    {
        using var scope = new TestHelpers.StoreScope();
        // Store a memory, then forget it by its file:// URI
        string storeResult = await Tools().Store(["hello world"], "uri-test",
            description: "test", tags: null, keywords: null);
        ResponseParser.Parse(storeResult).Content.Should().Contain("uri-test");

        // Build the file:// URI for the stored artifact
        string artifactPath = Path.Combine(scope.TempDir, "uri-test.nmp2");
        string uri = $"file://{artifactPath}";

        string result = await Tools().Forget(uri);

        ResponseParser.Parse(result).Content.Should().Contain("Forgot",
            because: "Forget by URI must succeed for indexed memories");
    }

    [Fact]
    public async Task Forget_NonExistentName_ReturnsError()
    {
        using var scope = new TestHelpers.StoreScope();

        string result = await Tools().Forget("does-not-exist");

        ResponseParser.Parse(result).Status.Should().Be("error",
            because: "Forget with an unknown name must return an error status, not throw");
    }

    [Fact]
    public async Task Forget_AlreadyDeletedFile_NoException()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store([TestHelpers.Facts.Fact1], "already-deleted");
        string filePath = ScriniaArtifactStore.ArtifactPath("already-deleted");

        // Manually delete the file first
        File.Delete(filePath);

        // Forget should still work — removes the index entry even if file is gone
        string result = await Tools().Forget("already-deleted");

        ResponseParser.Parse(result).Content.Should().Contain("Forgot",
            because: "Forget must succeed even if the artifact file was already deleted");
    }

    // ── Store/List/Forget E2E (3 tests) ────────────────────────────────

    [Fact]
    public async Task E2E_Store_List_Forget_Cycle()
    {
        using var scope = new TestHelpers.StoreScope();
        string content = TestHelpers.Facts.Excerpt;

        // Store
        string result = await Tools().Store([content], "cycle-test", "Cycle E2E test");
        ResponseParser.Parse(result).Content.Should().Contain("Remembered:");

        // List shows it
        string memoriesAfterRemember = await Tools().List(mode: "full");
        ResponseParser.Parse(memoriesAfterRemember).Content.Should().Contain("cycle-test");

        // Forget it
        string forgetResult = await Tools().Forget("cycle-test");
        ResponseParser.Parse(forgetResult).Content.Should().Contain("Forgot");

        // List no longer shows it
        string memoriesAfterForget = await Tools().List(mode: "full");
        ResponseParser.Parse(memoriesAfterForget).Content.Should().Be("No memories stored.",
            because: "after forgetting the only artifact the store must be empty");
    }

    [Fact]
    public async Task E2E_Store_ShowChunk_RoundTrip()
    {
        using var scope = new TestHelpers.StoreScope();
        string[] originals = ["Section A: auth flow.", "Section B: user endpoints.", "Section C: billing."];

        await Tools().Store(originals, "chunked-memory");

        // Use Show(chunk: N) to retrieve individual chunks
        string chunk1Result = await Tools().Show("chunked-memory", chunk: 1);
        var chunk1Content = ResponseParser.Parse(chunk1Result).Content!;
        chunk1Content.Should().Contain("Chunk 1/3",
            because: "Show(chunk: 1) must report chunk 1 of 3 for a three-element artifact");

        var parts = new List<string>();
        for (int i = 1; i <= 3; i++)
        {
            string chunkResult = await Tools().Show("chunked-memory", chunk: i);
            var chunkText = ResponseParser.Parse(chunkResult).Content!;
            // Extract chunk content after the "Chunk N/M\n\n" header
            string chunkContent = chunkText[(chunkText.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
            parts.Add(chunkContent);
        }

        string reassembled = string.Concat(parts);
        reassembled.Should().Be(string.Concat(originals),
            because: "all chunks of a stored artifact must reassemble to the original text");
    }

    [Fact]
    public async Task E2E_Store_Show_RoundTrip()
    {
        using var scope = new TestHelpers.StoreScope();
        string original = TestHelpers.Facts.Excerpt;

        await Tools().Store([original], "unpack-memory");
        string result = await Tools().Show("unpack-memory");

        ResponseParser.Parse(result).Content.Should().Be(original,
            because: "Show on a stored artifact's name must restore the exact original text");
    }

    // ── Search (2 tests) ───────────────────────────────────────────────

    [Fact]
    public async Task Search_WeightedScoring_ExactNameRanksHighest()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["DI patterns in .NET"], "di-patterns", "Dependency injection notes");
        await Tools().Store(["Various design info"], "design-info", "Including DI");

        string result = await Tools().Search("di-patterns");
        var found = ResponseParser.Parse(result).Content!;

        // "di-patterns" should rank first due to exact name match (score 100)
        int diPatternsPos = found.IndexOf("di-patterns", StringComparison.Ordinal);
        int designInfoPos = found.IndexOf("design-info", StringComparison.Ordinal);

        diPatternsPos.Should().BeGreaterThan(0);
        if (designInfoPos > 0)
        {
            diPatternsPos.Should().BeLessThan(designInfoPos,
                because: "exact name match must rank higher than description match");
        }
    }

    [Fact]
    public async Task Search_LocalScope_ReturnsMatchingEntries()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["DI lifecycle notes"], "dotnet-di");
        await Tools().Store(["local build tips"], "build-notes");

        string result = await Tools().Search("di", scopes: "local");
        ResponseParser.Parse(result).Content.Should().Contain("dotnet-di",
            because: "Search should search across local scope");
    }

    // ── ParseQualifiedName (4 tests) ─────────────────────────────────────────

    [Fact]
    public void ParseQualifiedName_SimpleSubject_ReturnsLocal()
    {
        var (scope, subject) = ScriniaArtifactStore.ParseQualifiedName("session-notes");

        scope.Should().Be("local");
        subject.Should().Be("session-notes");
    }

    [Fact]
    public void ParseQualifiedName_TopicSubject_ReturnsLocalTopic()
    {
        var (scope, subject) = ScriniaArtifactStore.ParseQualifiedName("dotnet:di-patterns");

        scope.Should().Be("local-topic:memory/dotnet");
        subject.Should().Be("di-patterns");
    }

    [Fact]
    public void ParseQualifiedName_ColonSeparated_ReturnsTopic()
    {
        // "global:legacy-data" is now treated as topic="global", subject="legacy-data"
        var (scope, subject) = ScriniaArtifactStore.ParseQualifiedName("global:legacy-data");

        scope.Should().Be("local-topic:memory/global");
        subject.Should().Be("legacy-data");
    }

    [Fact]
    public void ParseQualifiedName_EmptyInput_Throws()
    {
        Action act = () => ScriniaArtifactStore.ParseQualifiedName("");

        act.Should().Throw<ArgumentException>(
            because: "empty name must be rejected");
    }

    [Fact]
    public void ParseQualifiedName_EmptySubject_Throws()
    {
        Action act = () => ScriniaArtifactStore.ParseQualifiedName("dotnet:");

        act.Should().Throw<ArgumentException>(
            because: "empty subject after colon must be rejected");
    }

    // ── ResolveArtifactAsync (3 tests) ───────────────────────────────────────

    [Fact]
    public async Task ResolveArtifactAsync_InlineArtifact_ReturnsAsIs()
    {
        string artifact = Nmp2ChunkedEncoder.Encode("hello");
        string result = await ScriniaArtifactStore.ResolveArtifactAsync(artifact);

        result.Should().Be(artifact,
            because: "inline NMP/2 artifacts must be returned unchanged");
    }

    [Fact]
    public async Task ResolveArtifactAsync_MemoryName_ResolvesCorrectly()
    {
        using var scope = new TestHelpers.StoreScope();
        string original = "test content for resolution";
        await Tools().Store([original], "resolve-test");

        string artifact = await ScriniaArtifactStore.ResolveArtifactAsync("resolve-test");

        artifact.Should().StartWith("NMP/2 ",
            because: "resolved artifact must be a valid NMP/2 artifact");

        string decoded = await Tools().Show(artifact);
        ResponseParser.Parse(decoded).Content.Should().Be(original);
    }

    [Fact]
    public async Task ResolveArtifactAsync_NonExistent_Throws()
    {
        using var scope = new TestHelpers.StoreScope();
        Func<Task<string>> act = () => ScriniaArtifactStore.ResolveArtifactAsync("nonexistent-memory");

        await act.Should().ThrowAsync<FileNotFoundException>(
            because: "a non-existent memory name must throw");
    }

    // ── FormatQualifiedName (2 tests) ────────────────────────────────────────

    [Fact]
    public void FormatQualifiedName_Local_ReturnsSubjectOnly()
    {
        string result = ScriniaArtifactStore.FormatQualifiedName("local", "notes");
        result.Should().Be("notes");
    }

    [Fact]
    public void FormatQualifiedName_LocalTopic_ReturnsTopicColon()
    {
        string result = ScriniaArtifactStore.FormatQualifiedName("local-topic:dotnet", "di");
        result.Should().Be("dotnet:di");
    }

    // ── FormatScopeLabel (3 tests) ───────────────────────────────────────────

    [Fact]
    public void FormatScopeLabel_Local_ReturnsLocal()
    {
        ScriniaArtifactStore.FormatScopeLabel("local").Should().Be("local");
    }

    [Fact]
    public void FormatScopeLabel_LocalTopic_ReturnsTopicName()
    {
        ScriniaArtifactStore.FormatScopeLabel("local-topic:api").Should().Be("api");
    }

    [Fact]
    public void FormatScopeLabel_Ephemeral_ReturnsEphemeral()
    {
        ScriniaArtifactStore.FormatScopeLabel("ephemeral").Should().Be("ephemeral");
    }

    // ── Index v2 backward compat (1 test) ────────────────────────────────────

    [Fact]
    public void LoadIndex_V1WithoutTagsAndPreview_LoadsWithNulls()
    {
        using var scope = new TestHelpers.StoreScope();
        // Simulate a v1 index with no Tags/ContentPreview
        string json = """
            {
              "v": 1,
              "entries": [
                {
                  "name": "old-entry",
                  "uri": "file:///tmp/old-entry.nmp2",
                  "originalBytes": 512,
                  "chunkCount": 1,
                  "createdAt": "2025-01-01T00:00:00+00:00",
                  "description": "old format entry"
                }
              ]
            }
            """;
        string indexPath = Path.Combine(scope.TempDir, "index.json");
        File.WriteAllText(indexPath, json);

        var entries = ScriniaArtifactStore.LoadIndex();

        entries.Should().ContainSingle(e => e.Name == "old-entry");
        entries[0].Tags.Should().BeNull(because: "v1 entries have no tags");
        entries[0].ContentPreview.Should().BeNull(because: "v1 entries have no content preview");
    }

    // ── Ephemeral memory (~name) ─────────────────────────────────────────────

    [Fact]
    public async Task Store_Ephemeral_ReturnsConfirmationWithTag()
    {
        using var scope = new TestHelpers.StoreScope();
        string result = await Tools().Store(["scratch data"], "~scratch");
        var parsed = ResponseParser.Parse(result);

        parsed.Content.Should().Contain("Remembered: ~scratch",
            because: "ephemeral Store must include the tilde-prefixed name");
        parsed.Content.Should().Contain("[ephemeral]",
            because: "ephemeral Store must include an [ephemeral] suffix");
    }

    [Fact]
    public async Task Store_Ephemeral_AppearsInMemories()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["temp notes"], "~temp");

        string memories = await Tools().List(mode: "full");

        ResponseParser.Parse(memories).Content.Should().Contain("~temp",
            because: "ephemeral memories must appear in List(mode='full') output with ~ prefix");
    }

    [Fact]
    public async Task Show_Ephemeral_ExactOriginal()
    {
        using var scope = new TestHelpers.StoreScope();
        string original = "ephemeral content for unpack test";
        await Tools().Store([original], "~unpack-eph");

        string result = await Tools().Show("~unpack-eph");

        ResponseParser.Parse(result).Content.Should().Be(original,
            because: "Show on an ephemeral memory must restore exact original text");
    }

    [Fact]
    public async Task ShowChunk_Ephemeral_ResolvesCorrectly()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["ephemeral chunk test"], "~chunk-eph");

        string result = await Tools().Show("~chunk-eph", chunk: 1);
        var content = ResponseParser.Parse(result).Content!;

        content.Should().Contain("Chunk 1/1",
            because: "a small ephemeral memory must have 1 chunk");
        content.Should().Contain("ephemeral chunk test",
            because: "Show(chunk: 1) must return the chunk content");
    }

    [Fact]
    public async Task Forget_Ephemeral_RemovesEntry()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["to be forgotten"], "~forget-me");

        string result = await Tools().Forget("~forget-me");
        var parsed = ResponseParser.Parse(result);

        parsed.Content.Should().Contain("Forgot",
            because: "Forget must confirm ephemeral removal");
        parsed.Content.Should().Contain("~forget-me");

        string memories = await Tools().List();
        ResponseParser.Parse(memories).Content.Should().NotContain("~forget-me",
            because: "a forgotten ephemeral memory must not appear in List()");
    }

    [Fact]
    public async Task Forget_Ephemeral_NonExistent_ReturnsError()
    {
        using var scope = new TestHelpers.StoreScope();

        string result = await Tools().Forget("~does-not-exist");

        ResponseParser.Parse(result).Status.Should().Be("error",
            because: "Forget on a non-existent ephemeral name must return an error");
    }

    [Fact]
    public async Task Copy_EphemeralToPersistent_PromotesMemory()
    {
        using var scope = new TestHelpers.StoreScope();
        string original = "promote this content";
        await Tools().Store([original], "~promote-me");

        string result = await Tools().Copy("~promote-me", "promoted");

        ResponseParser.Parse(result).Content.Should().Contain("Copied",
            because: "Copy must confirm ephemeral → persistent promotion");

        // Verify the promoted memory exists and is correct
        string restored = await Tools().Show("promoted");
        ResponseParser.Parse(restored).Content.Should().Be(original,
            because: "promoted memory must contain the same content");

        // Original ephemeral still exists (non-destructive copy)
        string ephResult = await Tools().Show("~promote-me");
        ResponseParser.Parse(ephResult).Content.Should().Be(original,
            because: "Copy must be non-destructive — source still exists");
    }

    [Fact]
    public async Task Copy_PersistentToEphemeral_LoadsIntoMemory()
    {
        using var scope = new TestHelpers.StoreScope();
        string original = "load into ephemeral";
        await Tools().Store([original], "persistent-src");

        string result = await Tools().Copy("persistent-src", "~fast-ref");

        ResponseParser.Parse(result).Content.Should().Contain("Copied",
            because: "Copy must confirm persistent → ephemeral load");

        string restored = await Tools().Show("~fast-ref");
        ResponseParser.Parse(restored).Content.Should().Be(original,
            because: "ephemeral copy must contain the same content");
    }

    [Fact]
    public async Task E2E_Ephemeral_FullLifecycle()
    {
        using var scope = new TestHelpers.StoreScope();
        string original = "ephemeral lifecycle test content";

        // Store as ephemeral
        string remResult = await Tools().Store([original], "~lifecycle");
        ResponseParser.Parse(remResult).Content.Should().Contain("[ephemeral]");

        // Appears in List
        string memories = await Tools().List(mode: "full");
        ResponseParser.Parse(memories).Content.Should().Contain("~lifecycle");

        // Show works
        string restored = await Tools().Show("~lifecycle");
        ResponseParser.Parse(restored).Content.Should().Be(original);

        // Promote to persistent
        await Tools().Copy("~lifecycle", "lifecycle-saved");
        string persistedCheck = await Tools().Show("lifecycle-saved");
        ResponseParser.Parse(persistedCheck).Content.Should().Be(original);

        // Forget ephemeral
        await Tools().Forget("~lifecycle");
        string afterForget = await Tools().List(mode: "full");
        var afterForgetContent = ResponseParser.Parse(afterForget).Content!;
        afterForgetContent.Should().NotContain("~lifecycle");
        afterForgetContent.Should().Contain("lifecycle-saved",
            because: "the promoted persistent copy must survive ephemeral Forget");
    }

    // ── Multi-term search (3 tests) ─────────────────────────────────────────

    [Fact]
    public async Task Search_MultiTerm_BothTermsRequiredForHighRank()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["DI patterns in .NET"], "di-patterns", "Dependency injection patterns");
        await Tools().Store(["Audio editing app"], "audio-editing", "Sound processing tool");
        await Tools().Store(["Design info"], "design-info", "Various design patterns including DI");

        // Multi-term query: "DI patterns" should rank "di-patterns" highest
        string result = await Tools().Search("DI patterns");
        var found = ResponseParser.Parse(result).Content!;

        int diPatternsPos = found.IndexOf("di-patterns", StringComparison.Ordinal);
        int audioPos = found.IndexOf("audio-editing", StringComparison.Ordinal);

        diPatternsPos.Should().BeGreaterThan(0,
            because: "di-patterns must appear in results");

        if (audioPos > 0)
        {
            diPatternsPos.Should().BeLessThan(audioPos,
                because: "di-patterns matching both terms must rank higher than audio-editing matching only 'di' as substring");
        }
    }

    [Fact]
    public async Task Search_MultiTerm_SingleTermStillWorks()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["test content"], "exact-match", "some description");

        string result = await Tools().Search("exact-match");

        ResponseParser.Parse(result).Content.Should().Contain("exact-match",
            because: "single-term queries must still find exact name matches");
    }

    [Fact]
    public async Task Search_EphemeralMemories_IncludedInSearch()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["searchable ephemeral"], "~search-eph", "ephemeral search test");
        await Tools().Store(["persistent entry"], "persistent-entry", "not ephemeral");

        string result = await Tools().Search("search");

        ResponseParser.Parse(result).Content.Should().Contain("~search-eph",
            because: "ephemeral memories must be searchable via Search");
    }

    // ── Search output format (1 test) ──────────────────────────────────

    [Fact]
    public async Task Search_OutputContainsTypeColumn()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["test content"], "type-col-test", "testing type column");

        string result = await Tools().Search("type-col-test");
        var content = ResponseParser.Parse(result).Content!;

        content.Should().Contain("type",
            because: "Search output must include a 'type' column header");
        content.Should().Contain("entry",
            because: "entry results must be labeled with type 'entry'");
    }

    // ── Local topics (2 tests) ──────────────────────────────────────────────

    [Fact]
    public async Task Store_LocalTopic_WritesToWorkspaceTopics()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["auth flow documentation"], "api:auth");

        string expectedPath = Path.Combine(scope.WorkspaceDir, ".scrinia", "topics", "memory", "api", "auth.nmp2");
        File.Exists(expectedPath).Should().BeTrue(
            because: "topic:subject must write to .scrinia/topics/topic/subject.nmp2 in workspace");
    }

    [Fact]
    public async Task List_LocalTopic_ShowsTopicNameAsScope()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["auth notes"], "api:auth-flow");

        string memories = await Tools().List(mode: "full");
        var content = ResponseParser.Parse(memories).Content!;

        content.Should().Contain("api",
            because: "scope column for local-topic:api must display as 'api'");
        content.Should().Contain("auth-flow",
            because: "the entry name must appear in the List output");
    }

    // ── Export / Import (4 tests) ────────────────────────────────

    [Fact]
    public async Task Export_MultipleTopics_CreatesBundleFile()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["auth flow doc"], "api:auth");
        await Tools().Store(["error handling"], "api:errors");
        await Tools().Store(["db choice"], "arch:database");

        string result = await Tools().Export(["api", "arch"]);
        var content = ResponseParser.Parse(result).Content!;

        content.Should().Contain("Exported",
            because: "Export must return a confirmation");
        content.Should().Contain("2 topic(s)",
            because: "two topics were exported");
        content.Should().Contain("3 entries",
            because: "three total entries across both topics");

        // Verify bundle file exists
        string exportsDir = Path.Combine(scope.WorkspaceDir, ".scrinia", "exports");
        Directory.Exists(exportsDir).Should().BeTrue();
        var bundleFiles = Directory.GetFiles(exportsDir, "*.scrinia-bundle");
        bundleFiles.Should().HaveCount(1);
    }

    [Fact]
    public async Task Import_FromBundle_RestoresEntries()
    {
        using var scope = new TestHelpers.StoreScope();
        // Create and export topics
        await Tools().Store(["auth flow doc"], "api:auth");
        await Tools().Store(["db choice"], "arch:database");
        string exportResult = await Tools().Export(["api", "arch"]);
        var exportContent = ResponseParser.Parse(exportResult).Content!;

        // Extract bundle path from result
        string bundlePath = exportContent[(exportContent.LastIndexOf(") to ", StringComparison.Ordinal) + 5)..];

        // Delete the original topics
        await Tools().Forget("api:auth");
        await Tools().Forget("arch:database");

        // Import from bundle
        string importResult = await Tools().Import(bundlePath);
        var importContent = ResponseParser.Parse(importResult).Content!;

        importContent.Should().Contain("Imported",
            because: "Import must return a confirmation");
        importContent.Should().Contain("2 topic(s)",
            because: "two topics were imported");

        // Verify entries are restored
        string restored = await Tools().Show("api:auth");
        ResponseParser.Parse(restored).Content.Should().Be("auth flow doc",
            because: "imported entries must be decodable");
    }

    [Fact]
    public async Task Import_FilteredTopics_ImportsOnlyRequested()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["auth flow doc"], "api:auth");
        await Tools().Store(["db choice"], "arch:database");
        string exportResult = await Tools().Export(["api", "arch"]);
        var exportContent = ResponseParser.Parse(exportResult).Content!;
        string bundlePath = exportContent[(exportContent.LastIndexOf(") to ", StringComparison.Ordinal) + 5)..];

        // Delete originals
        await Tools().Forget("api:auth");
        await Tools().Forget("arch:database");

        // Import only "api" topic
        string importResult = await Tools().Import(bundlePath, ["api"]);
        var importContent = ResponseParser.Parse(importResult).Content!;

        importContent.Should().Contain("1 topic(s)",
            because: "only one topic was requested for import");
        importContent.Should().Contain("api");

        // api:auth should exist, arch:database should not
        string restored = await Tools().Show("api:auth");
        ResponseParser.Parse(restored).Content.Should().Be("auth flow doc");

        string archResult = await Tools().Show("arch:database");
        ResponseParser.Parse(archResult).Error.Should().Contain("not found",
            because: "arch topic was not imported");
    }

    [Fact]
    public async Task Export_EmptyTopic_ReturnsError()
    {
        using var scope = new TestHelpers.StoreScope();

        string result = await Tools().Export(["nonexistent-topic"]);

        ResponseParser.Parse(result).Status.Should().Be("error",
            because: "exporting a topic with no entries must return an error");
    }

    // ── Keywords (4 tests) ───────────────────────────────────────────────────

    [Fact]
    public async Task Store_WithKeywords_PersistsKeywords()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["auth flow with JWT tokens"], "auth-notes",
            keywords: ["oauth", "jwt", "bearer"]);

        var entries = ScriniaArtifactStore.LoadIndex();
        entries.Should().ContainSingle();
        entries[0].Keywords.Should().Contain("oauth");
        entries[0].Keywords.Should().Contain("jwt");
        entries[0].Keywords.Should().Contain("bearer");
    }

    [Fact]
    public async Task Store_WithoutKeywords_AutoExtractsKeywords()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["authentication authentication authentication token token refresh"], "auto-kw-test");

        var entries = ScriniaArtifactStore.LoadIndex();
        entries.Should().ContainSingle();
        entries[0].Keywords.Should().NotBeNull();
        entries[0].Keywords.Should().Contain("authentication");
    }

    [Fact]
    public async Task Store_WithKeywords_MergesAgentAndAutoKeywords()
    {
        using var scope = new TestHelpers.StoreScope();
        string content = string.Join(" ", Enumerable.Repeat("database", 10))
            + " " + string.Join(" ", Enumerable.Repeat("query", 5));
        await Tools().Store([content], "merged-kw", keywords: ["custom-kw"]);

        var entries = ScriniaArtifactStore.LoadIndex();
        entries.Should().ContainSingle();
        // Agent keyword should be first
        entries[0].Keywords![0].Should().Be("custom-kw");
        // Auto keywords should also be present
        entries[0].Keywords.Should().Contain("database");
    }

    [Fact]
    public async Task Search_ByKeyword_FindsEntry()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["some generic content about services"], "my-entry",
            keywords: ["microservices", "kubernetes"]);

        string result = await Tools().Search("kubernetes");

        ResponseParser.Parse(result).Content.Should().Contain("my-entry",
            because: "keyword match should surface the entry in search results");
    }

    // ── Term Frequencies & BM25 (3 tests) ────────────────────────────────────

    [Fact]
    public async Task Store_ComputesTermFrequencies()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["authentication authentication token"], "tf-test");

        var entries = ScriniaArtifactStore.LoadIndex();
        entries.Should().ContainSingle();
        entries[0].TermFrequencies.Should().NotBeNull();
        entries[0].TermFrequencies!.Should().ContainKey("authentication");
    }

    [Fact]
    public async Task Store_BoostsKeywordsInTf()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["token token token"], "boost-test",
            keywords: ["token"]);

        var entries = ScriniaArtifactStore.LoadIndex();
        // token appears 3 times + 5 agent keyword boost = 8
        entries[0].TermFrequencies!["token"].Should().Be(8);
    }

    [Fact]
    public async Task Search_BM25_ContentTermsMatchEvenWithoutNameMatch()
    {
        using var scope = new TestHelpers.StoreScope();
        // Store with a name that doesn't match the query but content does
        string content = string.Join(" ", Enumerable.Repeat("kubernetes", 20))
            + " " + string.Join(" ", Enumerable.Repeat("deployment", 10))
            + " " + string.Join(" ", Enumerable.Repeat("scaling", 5));
        await Tools().Store([content], "infra-notes", "infrastructure documentation");

        string result = await Tools().Search("kubernetes deployment");

        ResponseParser.Parse(result).Content.Should().Contain("infra-notes",
            because: "BM25 should find entries via content terms even when name/description doesn't match");
    }

    // ── Review conditions (3 tests) ──────────────────────────────────────────

    [Fact]
    public async Task Store_WithReviewAfter_PersistsDate()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["content"], "review-test",
            reviewAfter: "2026-06-01");

        var entries = ScriniaArtifactStore.LoadIndex();
        entries[0].ReviewAfter.Should().NotBeNull();
        entries[0].ReviewAfter!.Value.Year.Should().Be(2026);
        entries[0].ReviewAfter!.Value.Month.Should().Be(6);
    }

    [Fact]
    public async Task Store_WithReviewWhen_PersistsCondition()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["content"], "review-when-test",
            reviewWhen: "when auth system changes");

        var entries = ScriniaArtifactStore.LoadIndex();
        entries[0].ReviewWhen.Should().Be("when auth system changes");
    }

    [Fact]
    public async Task List_StaleEntry_ShowsStaleMarker()
    {
        using var scope = new TestHelpers.StoreScope();
        // Store with a review date in the past
        await Tools().Store(["content"], "stale-test",
            reviewAfter: "2020-01-01");

        string list = await Tools().List(mode: "full");

        ResponseParser.Parse(list).Content.Should().Contain("[stale]",
            because: "entries past their review date should show [stale] marker");
    }

    [Fact]
    public async Task List_ReviewWhenEntry_ShowsReviewMarker()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["content"], "review-when-list",
            reviewWhen: "when auth system changes");

        string list = await Tools().List(mode: "full");

        ResponseParser.Parse(list).Content.Should().Contain("[review?]",
            because: "entries with reviewWhen should show [review?] marker");
    }

    // ── Versioning (2 tests) ─────────────────────────────────────────────────

    [Fact]
    public async Task Store_Overwrite_SetsUpdatedAt()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["v1 content"], "version-test");
        await Tools().Store(["v2 content"], "version-test");

        var entries = ScriniaArtifactStore.LoadIndex();
        entries.Should().ContainSingle();
        entries[0].UpdatedAt.Should().NotBeNull(
            because: "overwriting an existing entry must set UpdatedAt");
    }

    [Fact]
    public async Task Store_Overwrite_ArchivesPreviousVersion()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["v1 content"], "archive-ver-test");
        await Tools().Store(["v2 content"], "archive-ver-test");

        string versionsDir = Path.Combine(scope.TempDir, "versions");
        Directory.Exists(versionsDir).Should().BeTrue();
        Directory.GetFiles(versionsDir, "archive-ver-test_*.nmp2").Should().HaveCount(1);
    }

    [Fact]
    public async Task Store_Overwrite_PreservesCreatedAt()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["v1 content"], "created-test");
        var entries1 = ScriniaArtifactStore.LoadIndex();
        var originalCreatedAt = entries1[0].CreatedAt;

        await Task.Delay(50); // ensure different timestamp
        await Tools().Store(["v2 content"], "created-test");

        var entries2 = ScriniaArtifactStore.LoadIndex();
        entries2[0].CreatedAt.Should().Be(originalCreatedAt,
            because: "overwriting must preserve the original CreatedAt");
    }

    // ── List output (2 tests) ────────────────────────────────────────────────

    [Fact]
    public async Task List_ContainsTokensColumn()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["some content here"], "tokens-col-test");

        string list = await Tools().List(mode: "full");

        ResponseParser.Parse(list).Content.Should().Contain("~tokens",
            because: "List output must include a ~tokens column header");
    }

    [Fact]
    public async Task Search_ContainsTokensColumn()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["test content"], "search-tok-test");

        string result = await Tools().Search("search-tok-test");

        ResponseParser.Parse(result).Content.Should().Contain("~tokens",
            because: "Search output must include a ~tokens column header");
    }

    // ── Append tool (3 tests) ────────────────────────────────────────────────

    [Fact]
    public async Task Append_ExistingMemory_AddsNewChunk()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["line one"], "append-test");

        await Tools().Append("line two", "append-test");

        string result = await Tools().Show("append-test");
        var content = ResponseParser.Parse(result).Content!;
        content.Should().Contain("line one");
        content.Should().Contain("line two");

        string chunkResult = await Tools().Show("append-test", chunk: 1);
        ResponseParser.Parse(chunkResult).Content.Should().Contain("Chunk 1/2",
            because: "after append, memory should have 2 chunks");
    }

    [Fact]
    public async Task Append_NonexistentMemory_CreatesNew()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Append("brand new content", "append-new");

        string result = await Tools().Show("append-new");
        ResponseParser.Parse(result).Content.Should().Be("brand new content");
    }

    [Fact]
    public async Task Append_Ephemeral_Works()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["line A"], "~append-eph");

        await Tools().Append("line B", "~append-eph");

        string result = await Tools().Show("~append-eph");
        var content = ResponseParser.Parse(result).Content!;
        content.Should().Contain("line A");
        content.Should().Contain("line B");

        string chunkResult = await Tools().Show("~append-eph", chunk: 1);
        ResponseParser.Parse(chunkResult).Content.Should().Contain("Chunk 1/2",
            because: "after append, ephemeral memory should have 2 chunks");
    }

    // ── Store with chunks (4 tests) ────────────────────────────────────────

    [Fact]
    public async Task Store_WithChunks_CreatesMultiChunkArtifact()
    {
        using var scope = new TestHelpers.StoreScope();
        string[] chunks = ["## Auth\nOAuth2 flow.", "## Users\nCRUD endpoints.", "## Billing\nStripe integration."];

        string result = await Tools().Store(chunks, "chunked-api");

        ResponseParser.Parse(result).Content.Should().Contain("3 chunks");

        string chunkResult = await Tools().Show("chunked-api", chunk: 1);
        ResponseParser.Parse(chunkResult).Content.Should().Contain("Chunk 1/3",
            because: "a three-element store must produce three chunks");
    }

    [Fact]
    public async Task Store_WithChunks_IndividualChunksRoundTrip()
    {
        using var scope = new TestHelpers.StoreScope();
        string[] chunks = ["Section A content.", "Section B content."];

        await Tools().Store(chunks, "chunk-rt");

        var chunk1Text = ResponseParser.Parse(await Tools().Show("chunk-rt", chunk: 1)).Content!;
        var chunk2Text = ResponseParser.Parse(await Tools().Show("chunk-rt", chunk: 2)).Content!;
        // Extract chunk content after the "Chunk N/M\n\n" header
        string chunk1 = chunk1Text[(chunk1Text.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        string chunk2 = chunk2Text[(chunk2Text.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        chunk1.Should().Be("Section A content.");
        chunk2.Should().Be("Section B content.");
    }

    [Fact]
    public async Task Store_WithSingleChunk_CreatesSingleChunkArtifact()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Store(["Only one."], "single-chunk");

        string chunkResult = await Tools().Show("single-chunk", chunk: 1);
        ResponseParser.Parse(chunkResult).Content.Should().Contain("Chunk 1/1",
            because: "a single-element store must produce one chunk");
        string decoded = await Tools().Show("single-chunk");
        ResponseParser.Parse(decoded).Content.Should().Be("Only one.");
    }

    [Fact]
    public async Task Store_WithChunks_Ephemeral_Works()
    {
        using var scope = new TestHelpers.StoreScope();
        string[] chunks = ["Eph chunk 1.", "Eph chunk 2."];

        string result = await Tools().Store(chunks, "~eph-chunked");
        var parsed = ResponseParser.Parse(result);

        parsed.Content.Should().Contain("2 chunks");
        parsed.Content.Should().Contain("[ephemeral]");

        var chunk1Text = ResponseParser.Parse(await Tools().Show("~eph-chunked", chunk: 1)).Content!;
        var chunk2Text = ResponseParser.Parse(await Tools().Show("~eph-chunked", chunk: 2)).Content!;
        string chunk1 = chunk1Text[(chunk1Text.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        string chunk2 = chunk2Text[(chunk2Text.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        chunk1.Should().Be("Eph chunk 1.");
        chunk2.Should().Be("Eph chunk 2.");
    }

    // ── Append always adds new chunk (6 tests) ────────────────────────────

    [Fact]
    public async Task Append_AddsChunkToExisting()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["Original content."], "nc-test");

        string result = await Tools().Append("New entry.", "nc-test");

        ResponseParser.Parse(result).Content.Should().Contain("chunk 2");
        var chunkText = ResponseParser.Parse(await Tools().Show("nc-test", chunk: 1)).Content!;
        chunkText.Should().Contain("Chunk 1/2",
            because: "after append, memory should have 2 chunks");

        string chunk1 = chunkText[(chunkText.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        var chunk2Text = ResponseParser.Parse(await Tools().Show("nc-test", chunk: 2)).Content!;
        string chunk2 = chunk2Text[(chunk2Text.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        chunk1.Should().Be("Original content.");
        chunk2.Should().Be("New entry.");
    }

    [Fact]
    public async Task Append_NonexistentCreatesNew()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Append("First entry.", "nc-new");

        string result = await Tools().Show("nc-new");
        ResponseParser.Parse(result).Content.Should().Be("First entry.");
        string chunkResult = await Tools().Show("nc-new", chunk: 1);
        ResponseParser.Parse(chunkResult).Content.Should().Contain("Chunk 1/1",
            because: "a newly created memory from append should have 1 chunk");
    }

    [Fact]
    public async Task Append_MultipleAppendsBuildJournal()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["Day 1: Started project."], "journal");

        await Tools().Append("Day 2: Added auth.", "journal");
        await Tools().Append("Day 3: Fixed bugs.", "journal");

        var chunkText = ResponseParser.Parse(await Tools().Show("journal", chunk: 1)).Content!;
        chunkText.Should().Contain("Chunk 1/3",
            because: "after two appends, memory should have 3 chunks");

        string day1 = chunkText[(chunkText.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        var day2Text = ResponseParser.Parse(await Tools().Show("journal", chunk: 2)).Content!;
        string day2 = day2Text[(day2Text.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        var day3Text = ResponseParser.Parse(await Tools().Show("journal", chunk: 3)).Content!;
        string day3 = day3Text[(day3Text.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        day1.Should().Be("Day 1: Started project.");
        day2.Should().Be("Day 2: Added auth.");
        day3.Should().Be("Day 3: Fixed bugs.");
    }

    [Fact]
    public async Task Append_UpdatesMetadata()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["Initial."], "nc-meta");

        await Tools().Append("More data here.", "nc-meta");

        // Show returns the full decoded content (all chunks concatenated)
        string full = await Tools().Show("nc-meta");
        var content = ResponseParser.Parse(full).Content!;
        content.Should().Contain("Initial.");
        content.Should().Contain("More data here.");
    }

    [Fact]
    public async Task Append_Ephemeral_AddsChunk()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["Eph line 1."], "~nc-eph");

        string result = await Tools().Append("Eph line 2.", "~nc-eph");

        ResponseParser.Parse(result).Content.Should().Contain("chunk 2");

        var chunk1Text = ResponseParser.Parse(await Tools().Show("~nc-eph", chunk: 1)).Content!;
        var chunk2Text = ResponseParser.Parse(await Tools().Show("~nc-eph", chunk: 2)).Content!;
        string chunk1 = chunk1Text[(chunk1Text.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        string chunk2 = chunk2Text[(chunk2Text.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        chunk1.Should().Be("Eph line 1.");
        chunk2.Should().Be("Eph line 2.");
    }

    [Fact]
    public async Task Append_FullDecodeMatchesAllChunks()
    {
        using var scope = new TestHelpers.StoreScope();
        string[] originals = ["Alpha.", "Bravo."];
        await Tools().Store(originals, "nc-full");

        await Tools().Append("Charlie.", "nc-full");

        // Full show should return all three chunks concatenated (with header for multi-chunk)
        string full = ResponseParser.Parse(await Tools().Show("nc-full")).Content!;
        // Multi-chunk Show prepends "(3 chunks)\n\n" header
        string fullContent = full.Contains("chunks)\n\n")
            ? full[(full.IndexOf("chunks)\n\n", StringComparison.Ordinal) + "chunks)\n\n".Length)..]
            : full;
        fullContent.Should().Be("Alpha.Bravo.Charlie.");

        // Individual chunks should match
        var c1Text = ResponseParser.Parse(await Tools().Show("nc-full", chunk: 1)).Content!;
        var c2Text = ResponseParser.Parse(await Tools().Show("nc-full", chunk: 2)).Content!;
        var c3Text = ResponseParser.Parse(await Tools().Show("nc-full", chunk: 3)).Content!;
        string c1 = c1Text[(c1Text.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        string c2 = c2Text[(c2Text.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        string c3 = c3Text[(c3Text.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        (c1 + c2 + c3).Should().Be(fullContent);
    }

    // ── Show budget recording (1 test) ───────────────────────────────────────

    [Fact]
    public async Task Show_RecordsBudget()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["budget recording test"], "show-budget");

        await Tools().Show("show-budget");

        SessionBudget.TotalCharsLoaded.Should().BeGreaterThan(0,
            because: "Show must record chars loaded in SessionBudget");
    }

    // ── Ephemeral store v3 fields (2 tests) ──────────────────────────────────

    [Fact]
    public async Task Store_Ephemeral_ComputesKeywordsAndTf()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["authentication token refresh"], "~eph-v3-test",
            keywords: ["oauth"]);

        var entry = MemoryStoreContext.Current!.GetEphemeral("eph-v3-test");
        entry.Should().NotBeNull();
        entry!.Keywords.Should().Contain("oauth");
        entry.TermFrequencies.Should().NotBeNull();
        entry.TermFrequencies!.Should().ContainKey("authentication");
    }

    [Fact]
    public async Task Store_Ephemeral_Overwrite_SetsUpdatedAt()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["v1"], "~eph-update-test");
        await Tools().Store(["v2"], "~eph-update-test");

        var entry = MemoryStoreContext.Current!.GetEphemeral("eph-update-test");
        entry.Should().NotBeNull();
        entry!.UpdatedAt.Should().NotBeNull(
            because: "overwriting ephemeral entry must set UpdatedAt");
    }

    // ── Guide update (1 test) ────────────────────────────────────────────────

    [Fact]
    public async Task Guide_ContainsCoreSections()
    {
        string result = await Tools().Guide();
        var guide = ResponseParser.Parse(result).Content!;

        guide.Should().Contain("## Chunks");
        guide.Should().Contain("append");
        guide.Should().Contain("Review Dates");
        guide.Should().Contain("reviewAfter");
        guide.Should().Contain("/checkpoint/");
        guide.Should().Contain("## Recovery");
        guide.Should().Contain("memory('remember'");
    }

    // ── Memory aliases: remember / recall (4 tests) ──────────────────────────

    [Fact]
    public async Task Memory_Remember_StoresContent()
    {
        using var scope = new TestHelpers.StoreScope();
        string original = TestHelpers.Facts.Fact1;

        string storeResult = await Tools().Memory("remember",
            path: "remember-test", content: [original]);

        var parsed = ResponseParser.Parse(storeResult);
        parsed.Status.Should().Be("success",
            because: "memory('remember') must succeed like memory('store')");

        // Verify content is retrievable
        string showResult = await Tools().Show("remember-test");
        ResponseParser.Parse(showResult).Content.Should().Be(original,
            because: "content stored via 'remember' must be retrievable via Show");
    }

    [Fact]
    public async Task Memory_Remember_ResponseActionIsRemembered()
    {
        using var scope = new TestHelpers.StoreScope();

        string result = await Tools().Memory("remember",
            path: "remember-action-test", content: ["test content"]);

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success");
        parsed.Action.Should().Be("remembered",
            because: "memory('remember') must use action 'remembered', not 'stored'");
    }

    [Fact]
    public async Task Memory_Recall_ReturnsContent()
    {
        using var scope = new TestHelpers.StoreScope();
        string original = TestHelpers.Facts.Fact13;

        await Tools().Store([original], "recall-test");

        string result = await Tools().Memory("recall", path: "recall-test");

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success",
            because: "memory('recall') must succeed like memory('show')");
        parsed.Content.Should().Be(original,
            because: "memory('recall') must return the exact stored content");
    }

    [Fact]
    public async Task Memory_Recall_ResponseActionIsRecalled()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Store(["recall action content"], "recall-action-test");

        string result = await Tools().Memory("recall", path: "recall-action-test");

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success");
        parsed.Action.Should().Be("recalled",
            because: "memory('recall') must use action 'recalled', not 'shown'");
    }

    // ── Task alias: plan (2 tests) ──────────────────────────────────────────

    [Fact]
    public async Task Task_Plan_CreatesTasks()
    {
        using var scope = new TestHelpers.StoreScope();
        var projTools = new ScriniaProjectTools();

        // Initialize project so PlanTasks can update project:state
        await ScriniaProjectTools.ProjectInit("Goals: test alias", CancellationToken.None);
        await ScriniaProjectTools.PlanRequirements("## v1\n- REQ-01: Test plan alias",
            CancellationToken.None);

        string taskDef = """
            ## Task 01
            Depends on: none
            Action: Implement the plan alias test
            Acceptance criteria:
            - Test passes
            """;

        string result = await projTools.TaskDispatch("plan",
            phaseId: "01", tasks: taskDef, cancellationToken: CancellationToken.None);

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success",
            because: "task('plan') must create tasks successfully");
        parsed.Content.Should().Contain("task",
            because: "response must list the created tasks");
        parsed.Content.Should().Contain("01",
            because: "response must reference the phase ID");
    }

    [Fact]
    public async Task Task_Plan_ResponseActionIsPlanned()
    {
        using var scope = new TestHelpers.StoreScope();
        var projTools = new ScriniaProjectTools();

        await ScriniaProjectTools.ProjectInit("Goals: test plan action", CancellationToken.None);
        await ScriniaProjectTools.PlanRequirements("## v1\n- REQ-01: Plan action test",
            CancellationToken.None);

        string taskDef = """
            ## Task 01
            Depends on: none
            Action: Verify plan action response
            Acceptance criteria:
            - action field is 'created'
            """;

        string result = await projTools.TaskDispatch("plan",
            phaseId: "01", tasks: taskDef, cancellationToken: CancellationToken.None);

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success");
        parsed.Action.Should().Be("created",
            because: "task('plan') delegates to PlanTasks which uses action 'created'");
    }
}
