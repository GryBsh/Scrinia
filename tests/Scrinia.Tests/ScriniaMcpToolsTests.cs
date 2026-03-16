using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Comprehensive unit tests for the 11 NMP/2 MCP tools:
///   Encode(content)                          → inline NMP/2 artifact
///   ChunkCount(artifactOrName)               → int
///   GetChunk(artifactOrName, chunkIndex)     → string chunk text
///   Show(artifactOrName)                     → decoded string or error message
///   Store(content, name, description, tags)  → confirmation string
///   List(scopes?)                            → index as formatted text
///   Search(query, scopes?, limit)            → scored results table
///   Copy(name, destination, overwrite)       → confirmation string
///   Forget(name)                             → confirmation string
///   Export(topics, filename?)                → confirmation string
///   Import(bundlePath, topics?, overwrite)   → confirmation string
///
/// All tests are offline — no LLM or external service required.
/// Store/List/Forget tests use TestHelpers.StoreScope to isolate
/// from the real user store.
/// </summary>
public sealed class ScriniaMcpToolsTests
{
    private static ScriniaMcpTools Tools() => new();

    // Large content with paragraph breaks (≥ 25 000 chars, \n\n separated → multi-chunk)
    private static string LargeContent(int approxChars = 40_000)
    {
        const string para = "The quick brown fox jumped over the lazy dog. Pack my box with five dozen liquor jugs.\n\n";
        int reps = approxChars / para.Length + 2;
        string raw = string.Concat(Enumerable.Repeat(para, reps));
        return raw[..Math.Min(approxChars, raw.Length)];
    }

    // Single-chunk content: under 20 000 chars threshold (no \n\n needed)
    private static string MediumContent(int approxChars = 18_000)
    {
        const string line = "Alpha bravo charlie delta echo foxtrot golf hotel india juliet.\n";
        int reps = approxChars / line.Length + 2;
        string raw = string.Concat(Enumerable.Repeat(line, reps));
        return raw[..Math.Min(approxChars, raw.Length)];
    }

    // ── Encode (7 tests) ─────────────────────────────────────────────────────

    [Fact]
    public async Task Encode_ShortProse_ReturnsInlineArtifact()
    {
        string result = await Tools().Encode([TestHelpers.Facts.Fact1]);

        result.Should().StartWith("NMP/2 ",
            because: "small artifacts must be returned inline as an NMP/2 artifact");
    }

    [Fact]
    public async Task Encode_EmptyString_ReturnsNmp2Artifact()
    {
        string result = await Tools().Encode([string.Empty]);

        result.Should().StartWith("NMP/2 ",
            because: "even an empty string must produce a valid NMP/2 artifact");
    }

    [Fact]
    public async Task Encode_UnicodeContent_StartsWithNmp2()
    {
        string content = "Hello 🌍 世界 مرحبا — Unicode roundtrip test.\nLine 2: emoji 🚀 CJK 日本語 RTL عربي.";
        string result = await Tools().Encode([content]);

        result.Should().StartWith("NMP/2 ",
            because: "Unicode content must encode to a valid NMP/2 artifact");
    }

    [Fact]
    public async Task Encode_SourceCodeWithSpecialChars_RoundTrips()
    {
        string content = """
            public class Foo {
                private readonly Dictionary<string, List<int>> _map = new();
                // backticks `here`, braces {}, angle <T>, quotes "hello"
                public Task<bool> Run(CancellationToken ct = default) => Task.FromResult(true);
            }
            """;
        string artifact = await Tools().Encode([content]);
        string restored = await Tools().Show(artifact);

        restored.Should().Be(content,
            because: "source code with special characters must roundtrip exactly");
    }

    [Fact]
    public async Task Encode_AlwaysInline_NeverReturnsFileUri()
    {
        string content = TestHelpers.LoadFactsText()[..5_000];
        string result = await Tools().Encode([content]);

        result.Should().NotStartWith("file://",
            because: "Encode must always return inline, never a file:// URI");
        result.Should().StartWith("NMP/2 ");
    }

    [Fact]
    public async Task Encode_LargeInput_ValidNmp2Artifact()
    {
        string content = MediumContent(); // 18 000 chars → single chunk
        string result = await Tools().Encode([content]);

        result.Should().StartWith("NMP/2 ",
            because: "large single-chunk inputs must still produce valid NMP/2 artifacts");
        result.Should().Contain("NMP/END",
            because: "every NMP/2 artifact ends with NMP/END");
    }

    [Fact]
    public async Task Encode_VeryLargeInput_SingleElement_ProducesSingleChunk()
    {
        string content = LargeContent(40_000);
        string result = await Tools().Encode([content]);

        result.Should().NotContain(" C:",
            because: "single-element Encode always produces single-chunk regardless of size");
    }

    [Fact]
    public async Task Encode_MultiElement_ProducesMultiChunkArtifact()
    {
        string result = await Tools().Encode(["Part A content.", "Part B content."]);

        result.Should().Contain(" C:2",
            because: "two-element Encode must produce a multi-chunk artifact with C:2 header");
    }

    // ── ChunkCount (3 tests) ──────────────────────────────────────────────────

    [Fact]
    public async Task ChunkCount_SmallInlineArtifact_ReturnsOne()
    {
        string artifact = await Tools().Encode([TestHelpers.Facts.Fact1]);
        int count = await Tools().ChunkCount(artifact);

        count.Should().Be(1,
            because: "a single-chunk artifact must report chunk count = 1");
    }

    [Fact]
    public async Task ChunkCount_MultiElementArtifact_ReturnsCorrectCount()
    {
        string artifact = await Tools().Encode(["Part A.", "Part B.", "Part C."]);
        int count = await Tools().ChunkCount(artifact);

        count.Should().Be(3,
            because: "a three-element encode must produce three chunks");
    }

    [Fact]
    public async Task ChunkCount_LargeSingleElement_ReturnsOne()
    {
        string content = LargeContent(40_000);
        string artifact = await Tools().Encode([content]);
        int count = await Tools().ChunkCount(artifact);

        count.Should().Be(1,
            because: "single-element Encode always produces single chunk");
    }

    // ── ChunkCount via memory name (2 tests) ─────────────────────────────────

    [Fact]
    public async Task ChunkCount_ByMemoryName_ResolvesAndReturnsCount()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store([TestHelpers.Facts.Fact1], "chunk-count-test");

        int count = await Tools().ChunkCount("chunk-count-test");

        count.Should().Be(1,
            because: "ChunkCount must resolve a memory name to its artifact");
    }

    [Fact]
    public async Task ChunkCount_ByMemoryName_MultiElement_ReturnsCorrectCount()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["Part A.", "Part B."], "multi-chunk-test");

        int count = await Tools().ChunkCount("multi-chunk-test");

        count.Should().Be(2,
            because: "a two-element stored artifact must report 2 chunks via name resolution");
    }

    // ── GetChunk (5 tests) ────────────────────────────────────────────────────

    [Fact]
    public async Task GetChunk_SingleChunk_Index1_EqualsShowResult()
    {
        string content = TestHelpers.Facts.Fact50;
        string artifact = await Tools().Encode([content]);

        string chunk1 = await Tools().GetChunk(artifact, 1);
        string unpacked = await Tools().Show(artifact);

        chunk1.Should().Be(unpacked,
            because: "for a single-chunk artifact, GetChunk(1) must equal the full Show result");
    }

    [Fact]
    public async Task GetChunk_MultiElement_AllChunksConcatenated_EqualsShowResult()
    {
        string artifact = await Tools().Encode(["Part A content.", "Part B content.", "Part C content."]);

        int count = await Tools().ChunkCount(artifact);
        count.Should().Be(3);
        var parts = new List<string>();
        for (int i = 1; i <= count; i++)
            parts.Add(await Tools().GetChunk(artifact, i));

        string reassembled = string.Concat(parts);
        string unpacked = await Tools().Show(artifact);

        reassembled.Should().Be(unpacked,
            because: "concatenating all chunks must equal the full Show result");
    }

    [Fact]
    public async Task GetChunk_MultiElement_FirstAndLastChunkDiffer()
    {
        string artifact = await Tools().Encode(["First chunk content.", "Second chunk content."]);

        int count = await Tools().ChunkCount(artifact);
        count.Should().Be(2);

        string first = await Tools().GetChunk(artifact, 1);
        string last  = await Tools().GetChunk(artifact, count);

        first.Should().NotBe(last,
            because: "different chunks must contain different text");
    }

    [Fact]
    public async Task GetChunk_IndexZero_ThrowsOrReturnsError()
    {
        string artifact = await Tools().Encode([TestHelpers.Facts.Fact1]);

        Func<Task<string>> act = () => Tools().GetChunk(artifact, 0);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>(
            because: "chunk index 0 is invalid (chunks are 1-based)");
    }

    [Fact]
    public async Task GetChunk_IndexBeyondCount_ThrowsOrReturnsError()
    {
        string artifact = await Tools().Encode([TestHelpers.Facts.Fact1]);
        int count = await Tools().ChunkCount(artifact); // = 1

        Func<Task<string>> act = () => Tools().GetChunk(artifact, count + 1);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>(
            because: "chunk index beyond count is invalid");
    }

    // ── Show (10 tests) ─────────────────────────────────────────────────────

    [Fact]
    public async Task Show_SmallNmp2Inline_ExactOriginal()
    {
        string original = TestHelpers.Facts.Fact1;
        string artifact = Nmp2ChunkedEncoder.Encode(original);

        string restored = await Tools().Show(artifact);

        restored.Should().Be(original,
            because: "Show must restore the exact original text from a small inline artifact");
    }

    [Fact]
    public async Task Show_MediumNmp2Inline_SingleChunk_ExactOriginal()
    {
        string original = MediumContent(); // ~18 000 chars — single chunk
        string artifact = Nmp2ChunkedEncoder.Encode(original);

        Nmp2Strategy.IsMultiChunk(artifact).Should().BeFalse(
            because: "Encode() always produces single-chunk artifacts");

        string restored = await Tools().Show(artifact);
        restored.Should().Be(original,
            because: "single-chunk decode must restore exact original text");
    }

    [Fact]
    public async Task Show_MultiChunkNmp2Inline_ExactOriginal()
    {
        string[] parts = ["Part A: alpha.", "Part B: bravo."];
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(parts);

        Nmp2Strategy.IsMultiChunk(artifact).Should().BeTrue(
            because: "two-element EncodeChunks produces multi-chunk artifact");

        string restored = await Tools().Show(artifact);
        restored.Should().Be(string.Concat(parts),
            because: "multi-chunk Show must reassemble and restore exact original text");
    }

    [Fact]
    public async Task Show_FileUri_SingleChunk_ExactOriginal()
    {
        string original = TestHelpers.Facts.Fact13;
        string artifact = Nmp2ChunkedEncoder.Encode(original);
        string path = Path.Combine(Path.GetTempPath(), $"scrinia_test_{Guid.NewGuid():N}.nmp2");
        await File.WriteAllTextAsync(path, artifact);

        try
        {
            string restored = await Tools().Show($"file://{path}");
            restored.Should().Be(original,
                because: "Show must read and decode a single-chunk artifact from a file:// URI");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Show_FileUri_MultiChunk_ExactOriginal()
    {
        string[] parts = ["Part A: alpha.", "Part B: bravo."];
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(parts);
        string path = Path.Combine(Path.GetTempPath(), $"scrinia_test_{Guid.NewGuid():N}.nmp2");
        await File.WriteAllTextAsync(path, artifact);

        try
        {
            string restored = await Tools().Show($"file://{path}");
            restored.Should().Be(string.Concat(parts),
                because: "Show must read and decode a multi-chunk artifact from a file:// URI");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Show_UnicodeRoundtrip_EmojisAndCjkAndRtl()
    {
        string original = "Emoji: 🎉🚀🌍\nCJK: 日本語 中文 한국어\nRTL: مرحبا بالعالم\nMath: ∑∫√π\n";
        string artifact = Nmp2ChunkedEncoder.Encode(original);

        string restored = await Tools().Show(artifact);

        restored.Should().Be(original,
            because: "Unicode characters (emoji, CJK, RTL, math) must roundtrip exactly through NMP/2");
    }

    [Fact]
    public async Task Show_SourceCodeRoundtrip_ExactMatch()
    {
        string original = TestHelpers.LoadHumanEvalText()[..2_000];
        string artifact = Nmp2ChunkedEncoder.Encode(original);

        string restored = await Tools().Show(artifact);

        restored.Should().Be(original,
            because: "source code with backticks, braces, and special characters must roundtrip exactly");
    }

    [Fact]
    public async Task Show_NonNmp2Artifact_ReturnsErrorString()
    {
        // A TAMIS/2 artifact header — not an NMP/2 artifact
        string fakeArtifact = "TAMIS/2 42B CRC32:DEADBEEF K:3\nsome body\nTAMIS/END";

        string result = await Tools().Show(fakeArtifact);

        result.Should().Contain("Error",
            because: "non-NMP/2 artifacts must return a descriptive error string");
    }

    [Fact]
    public async Task Show_Mnde1Artifact_ReturnsErrorString()
    {
        // MNDE/1 artifacts are no longer supported by Show
        string fakeArtifact = "MNDE/1 42B CRC32:DEADBEEF V:1\nsome body\nMNDE/END";

        string result = await Tools().Show(fakeArtifact);

        result.Should().Contain("Error",
            because: "MNDE/1 artifacts are not supported by the trimmed Show tool");
    }

    [Fact]
    public async Task Show_NonExistentFileUri_ThrowsOrErrors()
    {
        string badUri = $"file://{Path.GetTempPath()}scrinia_nonexistent_{Guid.NewGuid():N}.nmp2";

        Func<Task<string>> act = () => Tools().Show(badUri);

        // Should throw (FileNotFoundException) rather than silently returning empty
        await act.Should().ThrowAsync<Exception>(
            because: "a non-existent file:// URI must not silently return empty — it must throw");
    }

    // ── Show via memory name (2 tests) ─────────────────────────────────────

    [Fact]
    public async Task Show_ByMemoryName_ExactOriginal()
    {
        using var scope = new TestHelpers.StoreScope();
        string original = TestHelpers.Facts.Excerpt;
        await Tools().Store([original], "unpack-name-test");

        string restored = await Tools().Show("unpack-name-test");

        restored.Should().Be(original,
            because: "Show must resolve a memory name to its artifact and decode");
    }

    [Fact]
    public async Task Show_NonExistentName_ReturnsNotFoundError()
    {
        using var scope = new TestHelpers.StoreScope();

        string result = await Tools().Show("nonexistent-memory");

        result.Should().Contain("Error",
            because: "Show on a non-existent memory name must return an error");
        result.Should().Contain("not found",
            because: "the error must indicate the memory was not found");
        result.Should().Contain("nonexistent-memory",
            because: "the error must include the name that was not found");
    }

    // ── E2E workflows (3 tests) ──────────────────────────────────────────────

    [Fact]
    public async Task E2E_Encode_Show_Inline_ExactOriginal()
    {
        string original = TestHelpers.Facts.Excerpt;

        string artifact = await Tools().Encode([original]);
        string restored = await Tools().Show(artifact);

        restored.Should().Be(original,
            because: "Encode → Show inline pipeline must produce the exact original text");
    }

    [Fact]
    public async Task E2E_Encode_ChunkCount_GetChunk_Concat_EqualsShow()
    {
        string original = LargeContent(40_000);

        string artifact = await Tools().Encode([original]);
        int count = await Tools().ChunkCount(artifact);

        var parts = new List<string>();
        for (int i = 1; i <= count; i++)
            parts.Add(await Tools().GetChunk(artifact, i));

        string reassembled = string.Concat(parts);
        string unpacked = await Tools().Show(artifact);

        reassembled.Should().Be(unpacked,
            because: "concatenating GetChunk results must equal direct Show of the same artifact");
        reassembled.Should().Be(original,
            because: "the full pipeline must restore the exact original text");
    }

    [Fact]
    public async Task E2E_VeryLargeInput_Encode_Show_ExactOriginal()
    {
        // 50 000 chars — well above the multi-chunk threshold
        string original = LargeContent(50_000);

        string artifact = await Tools().Encode([original]);
        string restored = await Tools().Show(artifact);

        restored.Should().Be(original,
            because: "a 50 000-char input must roundtrip exactly through the full NMP/2 multi-chunk pipeline");
    }

    // ── Store (7 tests) ────────────────────────────────────────────────────

    [Fact]
    public async Task Store_SmallContent_ReturnsConfirmationString()
    {
        using var scope = new TestHelpers.StoreScope();
        string result = await Tools().Store([TestHelpers.Facts.Fact1], "test-memory");

        result.Should().StartWith("Remembered:",
            because: "Store must return a confirmation string");
        result.Should().Contain("test-memory",
            because: "confirmation must include the memory name");
        result.Should().Contain("1 chunk",
            because: "confirmation must include chunk count");
    }

    [Fact]
    public async Task Store_AppearsInMemoriesAfterCall()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store([TestHelpers.Facts.Fact1], "appear-test");

        string memories = await Tools().List(mode: "full");

        memories.Should().Contain("appear-test",
            because: "a stored artifact must be listed in List()");
    }

    [Fact]
    public async Task Store_OverwriteSameName_UpdatesIndex()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["first content"], "overwrite-test", "first desc");
        await Tools().Store(["second content"], "overwrite-test", "second desc");

        string memories = await Tools().List(mode: "full");

        memories.Should().Contain("overwrite-test",
            because: "overwritten artifact must still appear in List");
        memories.Should().Contain("second desc",
            because: "the description must reflect the most recent Store call");
        memories.Should().NotContain("first desc",
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

        result.Should().Be("No memories stored.",
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

        result.Should().Contain("Memory Summary");
        result.Should().Contain("3 memories");
        result.Should().Contain("2 topic");
        result.Should().Contain("topic:api");
        result.Should().Contain("topic:arch");
        result.Should().Contain("local");
        result.Should().Contain("Top keywords");
        result.Should().Contain("auth");
        result.Should().Contain("search(");
    }

    [Fact]
    public async Task List_Summary_EphemeralIncluded()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["persistent"], "some-note");
        await Tools().Store(["temp"], "~scratch");

        string result = await Tools().List();

        result.Should().Contain("2 memories");
        result.Should().Contain("1 ephemeral");
    }

    [Fact]
    public async Task List_Full_Pagination()
    {
        using var scope = new TestHelpers.StoreScope();
        for (int i = 0; i < 5; i++)
            await Tools().Store([$"content {i}"], $"page-test-{i}");

        string result = await Tools().List(mode: "full", offset: 2, limit: 2);

        result.Should().Contain("Showing 3-4 of 5");
    }

    [Fact]
    public async Task List_AfterRemember_ContainsName()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store([TestHelpers.Facts.Fact50], "marie-curie");

        string result = await Tools().List(mode: "full");

        result.Should().Contain("marie-curie",
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

        int alphaPos = result.IndexOf("alpha-entry", StringComparison.Ordinal);
        int betaPos  = result.IndexOf("beta-entry",  StringComparison.Ordinal);

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

        result.Should().Contain("Forgot",
            because: "Forget must return a confirmation string");
        result.Should().Contain("forget-me");
        File.Exists(filePath).Should().BeFalse(
            because: "the .nmp2 file must be deleted by Forget");
        ScriniaArtifactStore.LoadIndex().Should().BeEmpty(
            because: "the index entry must be removed by Forget");
    }

    [Fact]
    public async Task Forget_ByUri_RemovesFile()
    {
        using var scope = new TestHelpers.StoreScope();
        // Write a temp artifact that is NOT registered in the index (simulates legacy file)
        string tempPath = Path.Combine(scope.TempDir, "ephemeral.nmp2");
        File.WriteAllText(tempPath, Nmp2ChunkedEncoder.Encode("hello"));
        string uri = $"file://{tempPath}";

        string result = await Tools().Forget(uri);

        result.Should().Contain("Forgot",
            because: "Forget by URI must succeed for unregistered files too");
        File.Exists(tempPath).Should().BeFalse(
            because: "the file must be deleted even when not in the index");
    }

    [Fact]
    public async Task Forget_NonExistentName_ReturnsError()
    {
        using var scope = new TestHelpers.StoreScope();

        string result = await Tools().Forget("does-not-exist");

        result.Should().StartWith("Error",
            because: "Forget with an unknown name must return an error message, not throw");
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

        result.Should().Contain("Forgot",
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
        result.Should().StartWith("Remembered:");

        // List shows it
        string memoriesAfterRemember = await Tools().List(mode: "full");
        memoriesAfterRemember.Should().Contain("cycle-test");

        // Forget it
        string forgetResult = await Tools().Forget("cycle-test");
        forgetResult.Should().Contain("Forgot");

        // List no longer shows it
        string memoriesAfterForget = await Tools().List(mode: "full");
        memoriesAfterForget.Should().Be("No memories stored.",
            because: "after forgetting the only artifact the store must be empty");
    }

    [Fact]
    public async Task E2E_Store_GetChunk_RoundTrip()
    {
        using var scope = new TestHelpers.StoreScope();
        string[] originals = ["Section A: auth flow.", "Section B: user endpoints.", "Section C: billing."];

        await Tools().Store(originals, "chunked-memory");

        // Use memory name to resolve artifact
        int count = await Tools().ChunkCount("chunked-memory");
        count.Should().Be(3,
            because: "a three-element store must produce three chunks");

        var parts = new List<string>();
        for (int i = 1; i <= count; i++)
            parts.Add(await Tools().GetChunk("chunked-memory", i));

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
        string restored = await Tools().Show("unpack-memory");

        restored.Should().Be(original,
            because: "Show on a stored artifact's name must restore the exact original text");
    }

    // ── Search (2 tests) ───────────────────────────────────────────────

    [Fact]
    public async Task Search_WeightedScoring_ExactNameRanksHighest()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["DI patterns in .NET"], "di-patterns", "Dependency injection notes");
        await Tools().Store(["Various design info"], "design-info", "Including DI");

        string found = await Tools().Search("di-patterns");

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

        string found = await Tools().Search("di", scopes: "local");
        found.Should().Contain("dotnet-di",
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

        scope.Should().Be("local-topic:dotnet");
        subject.Should().Be("di-patterns");
    }

    [Fact]
    public void ParseQualifiedName_ColonSeparated_ReturnsTopic()
    {
        // "global:legacy-data" is now treated as topic="global", subject="legacy-data"
        var (scope, subject) = ScriniaArtifactStore.ParseQualifiedName("global:legacy-data");

        scope.Should().Be("local-topic:global");
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
        decoded.Should().Be(original);
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

        result.Should().StartWith("Remembered: ~scratch",
            because: "ephemeral Store must include the tilde-prefixed name");
        result.Should().Contain("[ephemeral]",
            because: "ephemeral Store must include an [ephemeral] suffix");
    }

    [Fact]
    public async Task Store_Ephemeral_AppearsInMemories()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["temp notes"], "~temp");

        string memories = await Tools().List(mode: "full");

        memories.Should().Contain("~temp",
            because: "ephemeral memories must appear in List(mode='full') output with ~ prefix");
    }

    [Fact]
    public async Task Show_Ephemeral_ExactOriginal()
    {
        using var scope = new TestHelpers.StoreScope();
        string original = "ephemeral content for unpack test";
        await Tools().Store([original], "~unpack-eph");

        string restored = await Tools().Show("~unpack-eph");

        restored.Should().Be(original,
            because: "Show on an ephemeral memory must restore exact original text");
    }

    [Fact]
    public async Task ChunkCount_Ephemeral_ResolvesCorrectly()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["ephemeral chunk test"], "~chunk-eph");

        int count = await Tools().ChunkCount("~chunk-eph");

        count.Should().Be(1,
            because: "a small ephemeral memory must have 1 chunk");
    }

    [Fact]
    public async Task Forget_Ephemeral_RemovesEntry()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["to be forgotten"], "~forget-me");

        string result = await Tools().Forget("~forget-me");

        result.Should().Contain("Forgot",
            because: "Forget must confirm ephemeral removal");
        result.Should().Contain("~forget-me");

        string memories = await Tools().List();
        memories.Should().NotContain("~forget-me",
            because: "a forgotten ephemeral memory must not appear in List()");
    }

    [Fact]
    public async Task Forget_Ephemeral_NonExistent_ReturnsError()
    {
        using var scope = new TestHelpers.StoreScope();

        string result = await Tools().Forget("~does-not-exist");

        result.Should().StartWith("Error",
            because: "Forget on a non-existent ephemeral name must return an error");
    }

    [Fact]
    public async Task Copy_EphemeralToPersistent_PromotesMemory()
    {
        using var scope = new TestHelpers.StoreScope();
        string original = "promote this content";
        await Tools().Store([original], "~promote-me");

        string result = await Tools().Copy("~promote-me", "promoted");

        result.Should().Contain("Copied",
            because: "Copy must confirm ephemeral → persistent promotion");

        // Verify the promoted memory exists and is correct
        string restored = await Tools().Show("promoted");
        restored.Should().Be(original,
            because: "promoted memory must contain the same content");

        // Original ephemeral still exists (non-destructive copy)
        string ephResult = await Tools().Show("~promote-me");
        ephResult.Should().Be(original,
            because: "Copy must be non-destructive — source still exists");
    }

    [Fact]
    public async Task Copy_PersistentToEphemeral_LoadsIntoMemory()
    {
        using var scope = new TestHelpers.StoreScope();
        string original = "load into ephemeral";
        await Tools().Store([original], "persistent-src");

        string result = await Tools().Copy("persistent-src", "~fast-ref");

        result.Should().Contain("Copied",
            because: "Copy must confirm persistent → ephemeral load");

        string restored = await Tools().Show("~fast-ref");
        restored.Should().Be(original,
            because: "ephemeral copy must contain the same content");
    }

    [Fact]
    public async Task E2E_Ephemeral_FullLifecycle()
    {
        using var scope = new TestHelpers.StoreScope();
        string original = "ephemeral lifecycle test content";

        // Store as ephemeral
        string remResult = await Tools().Store([original], "~lifecycle");
        remResult.Should().Contain("[ephemeral]");

        // Appears in List
        string memories = await Tools().List(mode: "full");
        memories.Should().Contain("~lifecycle");

        // Show works
        string restored = await Tools().Show("~lifecycle");
        restored.Should().Be(original);

        // Promote to persistent
        await Tools().Copy("~lifecycle", "lifecycle-saved");
        string persistedCheck = await Tools().Show("lifecycle-saved");
        persistedCheck.Should().Be(original);

        // Forget ephemeral
        await Tools().Forget("~lifecycle");
        string afterForget = await Tools().List(mode: "full");
        afterForget.Should().NotContain("~lifecycle");
        afterForget.Should().Contain("lifecycle-saved",
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
        string found = await Tools().Search("DI patterns");

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

        string found = await Tools().Search("exact-match");

        found.Should().Contain("exact-match",
            because: "single-term queries must still find exact name matches");
    }

    [Fact]
    public async Task Search_EphemeralMemories_IncludedInSearch()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["searchable ephemeral"], "~search-eph", "ephemeral search test");
        await Tools().Store(["persistent entry"], "persistent-entry", "not ephemeral");

        string found = await Tools().Search("search");

        found.Should().Contain("~search-eph",
            because: "ephemeral memories must be searchable via Search");
    }

    // ── Search output format (1 test) ──────────────────────────────────

    [Fact]
    public async Task Search_OutputContainsTypeColumn()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["test content"], "type-col-test", "testing type column");

        string found = await Tools().Search("type-col-test");

        found.Should().Contain("type",
            because: "Search output must include a 'type' column header");
        found.Should().Contain("entry",
            because: "entry results must be labeled with type 'entry'");
    }

    // ── Local topics (2 tests) ──────────────────────────────────────────────

    [Fact]
    public async Task Store_LocalTopic_WritesToWorkspaceTopics()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["auth flow documentation"], "api:auth");

        string expectedPath = Path.Combine(scope.WorkspaceDir, ".scrinia", "topics", "api", "auth.nmp2");
        File.Exists(expectedPath).Should().BeTrue(
            because: "topic:subject must write to .scrinia/topics/topic/subject.nmp2 in workspace");
    }

    [Fact]
    public async Task List_LocalTopic_ShowsTopicNameAsScope()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["auth notes"], "api:auth-flow");

        string memories = await Tools().List(mode: "full");

        memories.Should().Contain("api",
            because: "scope column for local-topic:api must display as 'api'");
        memories.Should().Contain("auth-flow",
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

        result.Should().Contain("Exported",
            because: "Export must return a confirmation");
        result.Should().Contain("2 topic(s)",
            because: "two topics were exported");
        result.Should().Contain("3 entries",
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

        // Extract bundle path from result
        string bundlePath = exportResult[(exportResult.LastIndexOf(") to ", StringComparison.Ordinal) + 5)..];

        // Delete the original topics
        await Tools().Forget("api:auth");
        await Tools().Forget("arch:database");

        // Import from bundle
        string importResult = await Tools().Import(bundlePath);

        importResult.Should().Contain("Imported",
            because: "Import must return a confirmation");
        importResult.Should().Contain("2 topic(s)",
            because: "two topics were imported");

        // Verify entries are restored
        string restored = await Tools().Show("api:auth");
        restored.Should().Be("auth flow doc",
            because: "imported entries must be decodable");
    }

    [Fact]
    public async Task Import_FilteredTopics_ImportsOnlyRequested()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["auth flow doc"], "api:auth");
        await Tools().Store(["db choice"], "arch:database");
        string exportResult = await Tools().Export(["api", "arch"]);
        string bundlePath = exportResult[(exportResult.LastIndexOf(") to ", StringComparison.Ordinal) + 5)..];

        // Delete originals
        await Tools().Forget("api:auth");
        await Tools().Forget("arch:database");

        // Import only "api" topic
        string importResult = await Tools().Import(bundlePath, ["api"]);

        importResult.Should().Contain("1 topic(s)",
            because: "only one topic was requested for import");
        importResult.Should().Contain("api");

        // api:auth should exist, arch:database should not
        string restored = await Tools().Show("api:auth");
        restored.Should().Be("auth flow doc");

        string archResult = await Tools().Show("arch:database");
        archResult.Should().Contain("not found",
            because: "arch topic was not imported");
    }

    [Fact]
    public async Task Export_EmptyTopic_ReturnsError()
    {
        using var scope = new TestHelpers.StoreScope();

        string result = await Tools().Export(["nonexistent-topic"]);

        result.Should().Contain("Error",
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

        result.Should().Contain("my-entry",
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

        result.Should().Contain("infra-notes",
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

        list.Should().Contain("[stale]",
            because: "entries past their review date should show [stale] marker");
    }

    [Fact]
    public async Task List_ReviewWhenEntry_ShowsReviewMarker()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["content"], "review-when-list",
            reviewWhen: "when auth system changes");

        string list = await Tools().List(mode: "full");

        list.Should().Contain("[review?]",
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

        list.Should().Contain("~tokens",
            because: "List output must include a ~tokens column header");
    }

    [Fact]
    public async Task Search_ContainsTokensColumn()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["test content"], "search-tok-test");

        string result = await Tools().Search("search-tok-test");

        result.Should().Contain("~tokens",
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
        result.Should().Contain("line one");
        result.Should().Contain("line two");

        int count = await Tools().ChunkCount("append-test");
        count.Should().Be(2);
    }

    [Fact]
    public async Task Append_NonexistentMemory_CreatesNew()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Append("brand new content", "append-new");

        string result = await Tools().Show("append-new");
        result.Should().Be("brand new content");
    }

    [Fact]
    public async Task Append_Ephemeral_Works()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["line A"], "~append-eph");

        await Tools().Append("line B", "~append-eph");

        string result = await Tools().Show("~append-eph");
        result.Should().Contain("line A");
        result.Should().Contain("line B");

        int count = await Tools().ChunkCount("~append-eph");
        count.Should().Be(2);
    }

    // ── Store with chunks (4 tests) ────────────────────────────────────────

    [Fact]
    public async Task Store_WithChunks_CreatesMultiChunkArtifact()
    {
        using var scope = new TestHelpers.StoreScope();
        string[] chunks = ["## Auth\nOAuth2 flow.", "## Users\nCRUD endpoints.", "## Billing\nStripe integration."];

        string result = await Tools().Store(chunks, "chunked-api");

        result.Should().Contain("3 chunks");

        int count = await Tools().ChunkCount("chunked-api");
        count.Should().Be(3);
    }

    [Fact]
    public async Task Store_WithChunks_IndividualChunksRoundTrip()
    {
        using var scope = new TestHelpers.StoreScope();
        string[] chunks = ["Section A content.", "Section B content."];

        await Tools().Store(chunks, "chunk-rt");

        string chunk1 = await Tools().GetChunk("chunk-rt", 1);
        string chunk2 = await Tools().GetChunk("chunk-rt", 2);
        chunk1.Should().Be("Section A content.");
        chunk2.Should().Be("Section B content.");
    }

    [Fact]
    public async Task Store_WithSingleChunk_CreatesSingleChunkArtifact()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Store(["Only one."], "single-chunk");

        int count = await Tools().ChunkCount("single-chunk");
        count.Should().Be(1);
        string decoded = await Tools().Show("single-chunk");
        decoded.Should().Be("Only one.");
    }

    [Fact]
    public async Task Store_WithChunks_Ephemeral_Works()
    {
        using var scope = new TestHelpers.StoreScope();
        string[] chunks = ["Eph chunk 1.", "Eph chunk 2."];

        string result = await Tools().Store(chunks, "~eph-chunked");

        result.Should().Contain("2 chunks");
        result.Should().Contain("[ephemeral]");

        string chunk1 = await Tools().GetChunk("~eph-chunked", 1);
        string chunk2 = await Tools().GetChunk("~eph-chunked", 2);
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

        result.Should().Contain("chunk 2");
        int count = await Tools().ChunkCount("nc-test");
        count.Should().Be(2);

        string chunk1 = await Tools().GetChunk("nc-test", 1);
        string chunk2 = await Tools().GetChunk("nc-test", 2);
        chunk1.Should().Be("Original content.");
        chunk2.Should().Be("New entry.");
    }

    [Fact]
    public async Task Append_NonexistentCreatesNew()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Append("First entry.", "nc-new");

        string result = await Tools().Show("nc-new");
        result.Should().Be("First entry.");
        int count = await Tools().ChunkCount("nc-new");
        count.Should().Be(1);
    }

    [Fact]
    public async Task Append_MultipleAppendsBuildJournal()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["Day 1: Started project."], "journal");

        await Tools().Append("Day 2: Added auth.", "journal");
        await Tools().Append("Day 3: Fixed bugs.", "journal");

        int count = await Tools().ChunkCount("journal");
        count.Should().Be(3);

        string day1 = await Tools().GetChunk("journal", 1);
        string day2 = await Tools().GetChunk("journal", 2);
        string day3 = await Tools().GetChunk("journal", 3);
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
        full.Should().Contain("Initial.");
        full.Should().Contain("More data here.");
    }

    [Fact]
    public async Task Append_Ephemeral_AddsChunk()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["Eph line 1."], "~nc-eph");

        string result = await Tools().Append("Eph line 2.", "~nc-eph");

        result.Should().Contain("chunk 2");

        string chunk1 = await Tools().GetChunk("~nc-eph", 1);
        string chunk2 = await Tools().GetChunk("~nc-eph", 2);
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

        // Full show should return all three chunks concatenated
        string full = await Tools().Show("nc-full");
        full.Should().Be("Alpha.Bravo.Charlie.");

        // Individual chunks should match
        string c1 = await Tools().GetChunk("nc-full", 1);
        string c2 = await Tools().GetChunk("nc-full", 2);
        string c3 = await Tools().GetChunk("nc-full", 3);
        (c1 + c2 + c3).Should().Be(full);
    }

    // ── Reflect tool (1 test) ────────────────────────────────────────────────

    [Fact]
    public async Task Reflect_ReturnsChecklist()
    {
        var result = await Tools().Reflect();

        result.Should().Contain("Session Reflection");
        result.Should().Contain("Decisions Made");
        result.Should().Contain("Patterns Discovered");
        result.Should().Contain("Problems Solved");
        result.Should().Contain("store()");
    }

    // ── Budget tool (3 tests) ────────────────────────────────────────────────

    [Fact]
    public async Task Budget_NoAccess_ReturnsEmpty()
    {
        using var scope = new TestHelpers.StoreScope();

        string result = await Tools().Budget();

        result.Should().Contain("No memories loaded");
    }

    [Fact]
    public async Task Budget_AfterShow_TracksAccess()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["some test content for budget tracking"], "budget-test");

        await Tools().Show("budget-test");
        string result = await Tools().Budget();

        result.Should().Contain("budget-test");
        result.Should().Contain("TOTAL");
    }

    [Fact]
    public async Task Budget_AfterGetChunk_TracksAccess()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["chunk budget test content"], "chunk-budget");

        await Tools().GetChunk("chunk-budget", 1);
        string result = await Tools().Budget();

        result.Should().Contain("chunk-budget");
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
    public async Task Guide_ContainsNewSections()
    {
        string guide = await Tools().Guide();

        guide.Should().Contain("Chunked retrieval");
        guide.Should().Contain("append");
        guide.Should().Contain("Version history");
        guide.Should().Contain("Review conditions");
        guide.Should().Contain("Budget tracking");
        guide.Should().Contain("reflect()");
        guide.Should().Contain("checkpoint");
        guide.Should().Contain("compaction");
    }

    // ── Ingest tool (1 test) ─────────────────────────────────────────────────

    [Fact]
    public async Task Ingest_ReturnsDetailedInstructions()
    {
        string result = await Tools().Ingest();

        result.Should().Contain("Phase 1");
        result.Should().Contain("Phase 2");
        result.Should().Contain("Phase 3");
        result.Should().Contain("Phase 4");
        result.Should().Contain("Phase 5");
        result.Should().Contain("list()");
        result.Should().Contain("show(");
        result.Should().Contain("store(");
        result.Should().Contain("forget(");
    }
}
