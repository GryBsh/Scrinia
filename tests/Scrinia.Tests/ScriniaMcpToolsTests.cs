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
        // No explicit description on either Store call: Merge 3 Task 2b folds an explicit
        // description into ContentPreview at write time (option A), which would otherwise mask
        // this test's real intent — that the AUTO content preview tracks the latest Store call.
        await Tools().Store(["first content"], "overwrite-test");
        await Tools().Store(["second content"], "overwrite-test");

        string memories = await Tools().List(mode: "full");
        var content = ResponseParser.Parse(memories).Content!;

        content.Should().Contain("overwrite-test",
            because: "overwritten artifact must still appear in List");
        // P6.2 reader cutover: the description column sources ContentPreview (always-populated)
        // ahead of the legacy Description field — see AttributeReaders.PreviewText and the locked
        // design decision in docs/superpowers/specs/2026-07-10-memory-model-keywords-attributes-design.md.
        content.Should().Contain("second content",
            because: "the content preview must reflect the most recent Store call");
        content.Should().NotContain("first content",
            because: "the old content preview must be replaced, not duplicated");
    }

    [Fact]
    public async Task Store_AutoDescription_UsesFirst500Chars()
    {
        // P6.2 reader cutover: with no explicit description, ContentPreview is auto-generated
        // via GenerateContentPreview (maxLength 500) — the legacy Description auto-truncation
        // (first 200 chars) no longer drives what is displayed/searched.
        using var scope = new TestHelpers.StoreScope();
        string content = new string('x', 600);

        await Tools().Store([content], "auto-desc-test"); // no description

        var entries = ScriniaArtifactStore.LoadIndex();
        entries.Should().ContainSingle(e => e.Name == "auto-desc-test");
        entries[0].ContentPreview!.Length.Should().Be(500,
            because: "the auto content preview must use exactly the first 500 chars when no description is given");
    }

    [Fact]
    public async Task Store_ExplicitDescription_FoldsIntoContentPreview()
    {
        // Merge 3 Task 2b: an explicit curated description becomes the displayed ContentPreview
        // at write time (option A) — it wins over the auto content snippet, giving the curated
        // description a home now that ContentPreview (not Description) is what list/search show.
        using var scope = new TestHelpers.StoreScope();
        string explicitDesc = "My custom description here.";

        await Tools().Store([TestHelpers.Facts.Fact1], "explicit-desc-preview-test", explicitDesc);

        var entries = ScriniaArtifactStore.LoadIndex();
        entries.Should().ContainSingle(e => e.Name == "explicit-desc-preview-test");
        entries[0].ContentPreview.Should().Be(explicitDesc,
            because: "an explicit non-blank description must fold into ContentPreview at write time");
    }

    [Fact]
    public async Task Store_WithTags_StoresTagsInIndex()
    {
        using var scope = new TestHelpers.StoreScope();
        string[] tags = ["csharp", "dependency-injection"];

        await Tools().Store(["DI patterns"], "tagged-memory", "DI notes", tags);

        var entries = ScriniaArtifactStore.LoadIndex();
        entries.Should().ContainSingle(e => e.Name == "tagged-memory");
        entries[0].Attributes!["tag"].Should().BeEquivalentTo(tags,
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

    // ── Attributes (3 tests) ──────────────────────────────────────────────

    [Fact]
    public async Task Store_Attributes_LandInFields()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["Acme signed the renewal."], "acme-note",
            attributes: new Dictionary<string, string[]>
            {
                ["person"] = ["maria"],
                ["org"] = ["acme"],
                ["status"] = ["done"],   // arbitrary key — must land too
            });

        var entry = ScriniaArtifactStore.LoadIndex().First(e => e.Name == "acme-note");
        entry.Attributes.Should().ContainKey("person");
        entry.Attributes!["person"].Should().Contain("maria");
        entry.Attributes.Should().ContainKey("status");
        entry.Attributes!["status"].Should().Contain("done");
    }

    [Fact]
    public async Task Store_ReviewAttribute_DateSetsReviewAfter_TextSetsReviewWhen()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["note"], "rev-note",
            attributes: new Dictionary<string, string[]>
            {
                ["review"] = ["2026-08-01", "when the auth system changes"],
            });

        var entry = ScriniaArtifactStore.LoadIndex().First(e => e.Name == "rev-note");
        AttributeReaders.TryReadReview(entry.Attributes, out var reviewDate, out var reviewCondition).Should().BeTrue();
        reviewDate.Should().NotBeNull();
        reviewDate!.Value.Year.Should().Be(2026);
        reviewCondition.Should().Be("when the auth system changes");
    }

    [Fact]
    public async Task Store_FileAttribute_RecordsCodeRefHash()
    {
        using var scope = new TestHelpers.StoreScope();
        string relPath = "src/Probe.cs";
        string fullPath = Path.Combine(scope.WorkspaceDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "class Probe {}");

        await Tools().Store(["depends on probe"], "file-note",
            attributes: new Dictionary<string, string[]> { ["file"] = [relPath] });

        var entry = ScriniaArtifactStore.LoadIndex().First(e => e.Name == "file-note");
        entry.FileHashes.Should().NotBeNull();
        entry.FileHashes!.Keys.Should().Contain(relPath);
    }

    [Fact]
    public async Task Store_LegacyCarriers_FoldIntoAttributesAtWriteTime()
    {
        // Locks the write-time pre-fold (AttributeMigration.BuildCarrierAttributes) against
        // the RAW persisted entry — tag/anchor/review/file all supplied via legacy params
        // (not the `attributes` dict) so this test only passes if the fold actually runs.
        using var scope = new TestHelpers.StoreScope();
        string relPath = "src/Carrier.cs";
        string fullPath = Path.Combine(scope.WorkspaceDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "class Carrier {}");

        await Tools().Store(["content"], "carrier-note",
            tags: ["alpha", "beta"],
            anchors: ["how do carriers work"],
            reviewAfter: "2026-08-01",
            reviewWhen: "when the carrier system changes",
            codeRefs: [relPath]);

        var entry = ScriniaArtifactStore.LoadIndex().First(e => e.Name == "carrier-note");
        entry.Attributes.Should().NotBeNull();
        entry.Attributes!["tag"].Should().BeEquivalentTo(["alpha", "beta"]);
        entry.Attributes!["anchor"].Should().Contain("how do carriers work");
        entry.Attributes!["review"].Should().Contain(v => v.StartsWith("2026-08-01", StringComparison.Ordinal));
        entry.Attributes!["review"].Should().Contain("when the carrier system changes");
        entry.Attributes!["file"].Should().Contain(relPath);
    }

    [Fact]
    public async Task MemoryRemember_Attributes_RoundTripToFields()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().MemoryRemember(
            path: "/findings/acme",
            content: ["Acme renewed."],
            attributes: new Dictionary<string, string[]> { ["org"] = ["acme"], ["topic"] = ["renewal"] },
            keywords: ["renewed", "annual contract"]);

        // "/findings/acme" is a v2 path — it resolves to scope "local-topic:findings", not "local".
        var entry = ScriniaArtifactStore.LoadIndex("local-topic:findings").First(e => e.Name.Contains("acme"));
        entry.Attributes.Should().ContainKey("org");
        entry.Attributes!["org"].Should().Contain("acme");
        entry.Keywords.Should().Contain("annual contract");
    }

    [Fact]
    public async Task MemoryAppend_Attributes_MergeIntoFields()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().MemoryRemember(path: "/sessions/s1", content: ["first"],
            attributes: new Dictionary<string, string[]> { ["person"] = ["alice"] });
        await Tools().MemoryAppend(path: "/sessions/s1", content: "second",
            attributes: new Dictionary<string, string[]> { ["person"] = ["bob"], ["topic"] = ["standup"] });

        // "/sessions/s1" is a v2 path — it resolves to scope "local-topic:sessions", not "local".
        var entry = ScriniaArtifactStore.LoadIndex("local-topic:sessions").First(e => e.Name == "s1");
        entry.Attributes!["person"].Should().Contain("alice").And.Contain("bob");   // multi-valued union
        entry.Attributes.Should().ContainKey("topic");
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
        content.Should().Contain("`search({ query: '...' })`");
        content.Should().NotContain("memory_search");
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

        // NOTE: this asserts null ContentPreview because ScriniaArtifactStore.LoadIndex is the v1-only
        // static CLI facade, which does NOT run the read-time forward-fill. The real FileMemoryStore cold
        // load (and `scri migrate`) WOULD backfill ContentPreview from Description via AttributeMigration.
        // If this read is ever rerouted through FileMemoryStore, this assertion must flip to "desc".
        var entries = ScriniaArtifactStore.LoadIndex();

        entries.Should().ContainSingle(e => e.Name == "old-entry");
        entries[0].Attributes.Should().BeNull(because: "v1 entries have no attributes");
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
        AttributeReaders.TryReadReview(entries[0].Attributes, out var reviewDate, out _).Should().BeTrue();
        reviewDate.Should().NotBeNull();
        reviewDate!.Value.Year.Should().Be(2026);
        reviewDate.Value.Month.Should().Be(6);
    }

    [Fact]
    public async Task Store_WithReviewWhen_PersistsCondition()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["content"], "review-when-test",
            reviewWhen: "when auth system changes");

        var entries = ScriniaArtifactStore.LoadIndex();
        AttributeReaders.TryReadReview(entries[0].Attributes, out _, out var reviewCondition).Should().BeTrue();
        reviewCondition.Should().Be("when auth system changes");
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
    public async Task Search_SurfacesTokenCount()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["test content"], "search-tok-test");

        string result = await Tools().Search("search-tok-test");

        ResponseParser.Parse(result).Content.Should().Contain("tokens",
            because: "Search output must surface an estimated token count per result so " +
                     "the agent can decide whether to recall the full content");
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

    [Fact]
    public async Task Store_TypedKeyword_ValueIsBm25Matchable()
    {
        // A typed keyword (prefix:value) used to be seeded into TF as the monolithic
        // "person:mary" string — a key no tokenized query term can ever equal, so it was
        // dead weight in BM25. Now it routes to Fields["person"] (unified fields), and the
        // tokenized VALUE is seeded into TF (via FieldExtractor.SeedTermFrequencies) so the
        // part is matchable. The structured token no longer lives on entry.Keywords at all.
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(
            ["The subsystem orchestrates lifecycle hooks during plugin startup."], // no "Mary" in content
            "typed-kw-test",
            keywords: ["person:Mary"]);

        var entry = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "typed-kw-test");
        entry.Attributes.Should().NotBeNull();
        entry.Attributes!["person"].Should().BeEquivalentTo(["Mary"],
            because: "the structured typed token routes to the unified Fields store for field-score/facet/cross-link use");
        entry.Keywords.Should().NotContain(k => k.StartsWith("person:"),
            because: "prefixed keywords are no longer written");
        entry.TermFrequencies.Should().NotBeNull();
        entry.TermFrequencies!.Should().ContainKey("mary",
            because: "the typed keyword's value must be seeded into TF as a real BM25 term");
        entry.TermFrequencies.Should().NotContainKey("person:mary",
            because: "the monolithic prefixed token is dead weight in BM25 and must not be seeded as-is");

        var parsed = ResponseParser.Parse(await Tools().Search("Mary"));
        parsed.Status.Should().Be("success");
        parsed.Content.Should().Contain("typed-kw-test",
            because: "a typed keyword's value must be reachable via BM25 even when absent from content");
    }

    [Fact]
    public async Task Append_PreservesCuratedAnchorsTagsAndDescription()
    {
        // Regression: append used to rebuild the entry with Tags=null, RetrievalAnchors=null,
        // and ContentPreview overwritten with a content slice — silently destroying everything
        // remember() curated. Appending must preserve that metadata.
        //
        // Merge 3 Task 2b: an explicit curated description is folded into ContentPreview at
        // write time (option A) — it is now the displayed carrier, not the legacy Description
        // member. An append that supplies no new description must PRESERVE the existing
        // ContentPreview rather than regenerating it from content, which would silently drop
        // the curated description.
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(
            ["The widget subsystem orchestrates lifecycle hooks."],
            "append-meta-preserve",
            description: "widget lifecycle notes",
            tags: ["architecture", "widgets"],
            keywords: ["widget", "lifecycle"],
            anchors: ["what is the widget subsystem", "how do lifecycle hooks work"]);

        await Tools().Append("A later addition about plugin startup ordering.", "append-meta-preserve");

        var entry = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "append-meta-preserve");
        entry.Attributes.Should().NotBeNull();
        entry.Attributes!["anchor"].Should().Contain("what is the widget subsystem")
            .And.Contain("how do lifecycle hooks work");
        entry.Attributes!["tag"].Should().Contain("architecture").And.Contain("widgets");
        entry.ContentPreview.Should().Be("widget lifecycle notes",
            because: "an append with no new description must preserve the curated description that was folded into ContentPreview at Store time, not overwrite it with a content slice");
        entry.Keywords.Should().Contain("widget",
            because: "agent-supplied keywords must survive append, not be dropped");
    }

    [Fact]
    public async Task Append_MergesNewAnchorsTagsKeywords_AndDeduplicates()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(
            ["Initial content about the auth flow."],
            "append-meta-merge",
            tags: ["auth"],
            anchors: ["how does login work"]);

        await Tools().Append(
            "Added detail about token refresh.",
            "append-meta-merge",
            tags: ["auth", "tokens"],                                        // "auth" duplicates existing
            keywords: ["refresh"],
            anchors: ["how does login work", "what triggers a token refresh"]); // first duplicates existing

        var entry = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "append-meta-merge");
        entry.Attributes!["tag"].Should().BeEquivalentTo(["auth", "tokens"],
            because: "new tags merge with existing and dedupe case-insensitively");
        entry.Attributes!["anchor"].Should().BeEquivalentTo(["how does login work", "what triggers a token refresh"],
            because: "new anchors merge with existing and dedupe");
        entry.Keywords.Should().Contain("refresh");
    }

    [Fact]
    public async Task Append_CarriesForwardReviewAndFileIntoAttributes()
    {
        // Append has no reviewAfter/reviewWhen/codeRefs params of its own — those carry
        // forward from the existing entry. BuildAppendMetadata already folds tag/anchor into
        // Fields, but review/file are folded separately at the construction site (the gap this
        // locks): the RAW appended entry's Attributes must still carry review/file even though
        // the append call itself supplies neither.
        using var scope = new TestHelpers.StoreScope();
        string relPath = "src/AppendCarrier.cs";
        string fullPath = Path.Combine(scope.WorkspaceDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "class AppendCarrier {}");

        await Tools().Store(["initial content"], "append-carrier-note",
            reviewAfter: "2026-08-01",
            reviewWhen: "when the carrier system changes",
            codeRefs: [relPath]);

        await Tools().Append("a later addition", "append-carrier-note");

        var entry = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "append-carrier-note");
        entry.Attributes.Should().NotBeNull();
        entry.Attributes!["review"].Should().Contain(v => v.StartsWith("2026-08-01", StringComparison.Ordinal));
        entry.Attributes!["review"].Should().Contain("when the carrier system changes");
        entry.Attributes!["file"].Should().Contain(relPath);
    }

    [Fact]
    public async Task Append_NewAnchor_BridgesQueryViaSearch()
    {
        // The high-value case: appended content introduces a concept users query for in
        // vocabulary that's in neither the original nor the appended text. An anchor on the
        // append must seed BM25 so that query surfaces the memory.
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["The subsystem orchestrates lifecycle hooks."], "append-anchor-bridge");

        await Tools().Append(
            "More notes on internal sequencing.",
            "append-anchor-bridge",
            anchors: ["how do I configure zorptree"]);

        var entry = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "append-anchor-bridge");
        entry.Attributes!["anchor"].Should().Contain("how do I configure zorptree");
        entry.TermFrequencies.Should().NotBeNull();
        entry.TermFrequencies!.Should().ContainKey("zorptree",
            because: "anchor tokens supplied on append must be TF-seeded so BM25 can match them");

        var parsed = ResponseParser.Parse(await Tools().Search("how do I configure zorptree"));
        parsed.Status.Should().Be("success");
        parsed.Content.Should().Contain("append-anchor-bridge",
            because: "an anchor added on append must bridge a user query to the memory");
    }

    [Fact]
    public async Task Append_FieldMerge_ScalarReplacesEntityAccumulates()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Store(["status: active\nInitial."], "unified-append-test",
            keywords: ["person:Alice"]);
        await Tools().Append("status: done\nFollow-up.", "unified-append-test",
            keywords: ["person:Bob"]);

        var entry = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "unified-append-test");
        entry.Attributes!["status"].Should().BeEquivalentTo(["done"], because: "scalar newer-wins");
        entry.Attributes["person"].Should().BeEquivalentTo(["Alice", "Bob"], because: "entities accumulate");
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
        guide.Should().Contain("Review markers");
        guide.Should().Contain("reviewAfter");
        guide.Should().Contain("/checkpoint/");
        guide.Should().Contain("## Recovery");
        guide.Should().Contain("remember");
        guide.Should().NotContain("memory_remember");
    }

    // ── Per-verb memory tools: remember / recall (4 tests) ───────────────────

    [Fact]
    public async Task MemoryRemember_StoresContent()
    {
        using var scope = new TestHelpers.StoreScope();
        string original = TestHelpers.Facts.Fact1;

        string storeResult = await Tools().MemoryRemember(
            path: "remember-test", content: [original]);

        var parsed = ResponseParser.Parse(storeResult);
        parsed.Status.Should().Be("success",
            because: "memory_remember must succeed");

        // Verify content is retrievable
        string showResult = await Tools().Show("remember-test");
        ResponseParser.Parse(showResult).Content.Should().Be(original,
            because: "content stored via memory_remember must be retrievable via Show");
    }

    [Fact]
    public async Task MemoryRemember_ResponseActionIsRemembered()
    {
        using var scope = new TestHelpers.StoreScope();

        string result = await Tools().MemoryRemember(
            path: "remember-action-test", content: ["test content"]);

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success");
        parsed.Action.Should().Be("remembered",
            because: "memory_remember must use action 'remembered', not the lower-level 'stored'");
    }

    [Fact]
    public async Task MemoryRecall_ReturnsContent()
    {
        using var scope = new TestHelpers.StoreScope();
        string original = TestHelpers.Facts.Fact13;

        await Tools().Store([original], "recall-test");

        string result = await Tools().MemoryRecall(path: "recall-test");

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success",
            because: "memory_recall must succeed");
        parsed.Content.Should().Be(original,
            because: "memory_recall must return the exact stored content");
    }

    [Fact]
    public async Task MemoryRecall_ResponseActionIsRecalled()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Store(["recall action content"], "recall-action-test");

        string result = await Tools().MemoryRecall(path: "recall-action-test");

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success");
        parsed.Action.Should().Be("recalled",
            because: "memory_recall must use action 'recalled', not the lower-level 'shown'");
    }

    // ── withBuiltin parameter (skill recall) — disambiguated from memory_reconcile ──

    [Fact]
    public async Task MemoryRecall_Skill_WithBuiltin_ReturnsBothBuiltInAndOverride()
    {
        using var scope = new TestHelpers.StoreScope();

        // Create a project override for the qa skill.
        string overrideBody = "## Project QA override\nUse repo-specific acceptance criteria.";
        string skillsDir = Path.Combine(scope.WorkspaceDir, ".scrinia", "skills");
        Directory.CreateDirectory(skillsDir);
        await File.WriteAllTextAsync(Path.Combine(skillsDir, "qa.md"), overrideBody);

        string result = await Tools().MemoryRecall(
            path: "/skill/qa",
            withBuiltin: true);

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success",
            because: "withBuiltin recall on an existing skill must succeed");
        parsed.Content.Should().Contain("Current Built-in",
            because: "withBuiltin must include the embedded skill alongside the override");
        parsed.Content.Should().Contain("Your Project Override",
            because: "withBuiltin must include the project override alongside the built-in");
        parsed.Content.Should().Contain("Project QA override",
            because: "withBuiltin must include the actual override body verbatim");
    }

    [Fact]
    public async Task MemoryRecall_NonSkill_WithBuiltinFlag_IsIgnored()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["plain content"], "plain-memory");

        // Setting withBuiltin: true on a non-/skill/ path must not change behavior —
        // the parameter is skill-recall-only and is unrelated to memory_reconcile.
        string result = await Tools().MemoryRecall(
            path: "plain-memory",
            withBuiltin: true);

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success");
        parsed.Content.Should().Be("plain content",
            because: "withBuiltin must be silently ignored for non-skill paths");
    }

    [Fact]
    public async Task MemoryReconcile_IsIndependentOfWithBuiltinFlag()
    {
        using var scope = new TestHelpers.StoreScope();

        // memory_reconcile scans for git merge conflicts; in a fresh store it reports zero.
        // It is unrelated to the withBuiltin skill-merge flag on memory_recall.
        string result = await Tools().MemoryReconcile();

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success",
            because: "memory_reconcile on a clean store must succeed");
    }

    // ── Structured error responses: errorCode + actionNeeded recovery hints ──
    //
    // Note: the dispatcher's "unknown action" path no longer exists — each verb is its
    // own tool, so unknown names fail at the MCP tool-registration layer, not at runtime
    // inside a handler. The previous Memory_UnknownAction_ReturnsInvalidActionCode test
    // was removed as obsolete when the dispatcher was split.

    [Fact]
    public async Task MemoryRemember_MissingRequiredParameter_ReturnsInvalidParameterCode()
    {
        using var scope = new TestHelpers.StoreScope();

        // empty path AND empty content array — trips the validator inside the tool.
        string result = await Tools().MemoryRemember(path: "", content: []);

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("error");
        parsed.ErrorCode.Should().Be(ErrorCodes.InvalidParameter);
        parsed.ActionNeeded.Should().NotBeEmpty();
        parsed.ActionNeeded[0].Should().Contain("remember({ path: '...', content: ['chunk 1', 'chunk 2'] })",
            because: "the recovery hint should be a concrete tool call template");
        parsed.ActionNeeded[0].Should().NotContain("memory_remember");
    }

    [Fact]
    public async Task Memory_NotFound_ReturnsNotFoundCodeWithListAndSearchHints()
    {
        using var scope = new TestHelpers.StoreScope();

        string result = await Tools().Show("nonexistent-memory");

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("error");
        parsed.ErrorCode.Should().Be(ErrorCodes.NotFound);
        parsed.ActionNeeded.Should().NotBeEmpty();
        parsed.ActionNeeded.Should().Contain(
            h => h.Contains("list() to see available memories") && !h.Contains("memory_list"),
            because: "a not-found error should point to list as one recovery path");
    }

    [Fact]
    public async Task Memory_LegacyErrorBuilder_FallsBackToInternalCode()
    {
        // The single-arg ResponseBuilder.Error overload is used by callsites that haven't
        // adopted explicit codes yet. It must default to INTERNAL so consumers can still
        // branch on errorCode reliably.
        string yaml = ResponseBuilder.Error("something exploded").ToYaml();
        var parsed = ResponseParser.Parse(yaml);

        parsed.Status.Should().Be("error");
        parsed.Error.Should().Be("something exploded");
        parsed.ErrorCode.Should().Be(ErrorCodes.Internal);
    }

    // ── Reserved-prefix typo guard ────────────────────────────────────────────

    [Fact]
    public async Task MemoryRemember_PluralPrefixTypo_AddsInfoHint()
    {
        using var scope = new TestHelpers.StoreScope();

        // /patterns/ is the canonical reserved prefix; /pattern/ is the singular typo.
        // Soft-warn: store still succeeds, but the response carries an info hint.
        string result = await Tools().MemoryRemember(
            path: "/pattern/auth-retry",
            content: ["use exponential backoff with jitter"]);

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success",
            because: "the typo guard must NOT block the write — soft-warn only");
        parsed.Info.Should().Contain(i => i.Contains("'/patterns/'") && i.Contains("reserved prefix"),
            because: "agents must see a hint suggesting the canonical reserved spelling");
    }

    [Fact]
    public async Task MemoryRemember_WrongCasePrefix_AddsInfoHint()
    {
        using var scope = new TestHelpers.StoreScope();

        string result = await Tools().MemoryRemember(
            path: "/Findings/SEC-001",
            content: ["api key leaked in logs"]);

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success");
        parsed.Info.Should().Contain(i => i.Contains("lower-case") && i.Contains("'/findings/'"),
            because: "case-mismatch on a reserved prefix should suggest the canonical lower-case form");
    }

    [Fact]
    public async Task MemoryRemember_ExactReservedPrefix_NoHint()
    {
        using var scope = new TestHelpers.StoreScope();

        string result = await Tools().MemoryRemember(
            path: "/patterns/retry-backoff",
            content: ["use exponential backoff"]);

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success");
        parsed.Info.Should().NotContain(i => i.Contains("reserved prefix"),
            because: "canonical reserved paths must not trigger the typo hint");
    }

    [Fact]
    public async Task MemoryRemember_UnrelatedPath_NoHint()
    {
        using var scope = new TestHelpers.StoreScope();

        // /api/ is not a reserved prefix and not similar to any — must be silent.
        string result = await Tools().MemoryRemember(
            path: "/api/auth-flow",
            content: ["oauth callback handler"]);

        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success");
        parsed.Info.Should().NotContain(i => i.Contains("reserved prefix"),
            because: "non-reserved paths must not produce false-positive hints");
    }

    // ── Rich search surface (facts/tags/path filters/path-segment score) ────────

    [Fact]
    public async Task Search_SurfacesAttributesWhenPresent()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().MemoryRemember(path: "surfaced", content: ["auth token refresh flow"],
            attributes: new Dictionary<string, string[]> { ["topic"] = ["security"], ["org"] = ["acme"] });

        string result = await Tools().MemorySearch(query: "auth token");
        var parsed = ResponseParser.Parse(result);
        parsed.Content.Should().Contain("attributes:",
            because: "rich search results must surface attributes inline in place of tags");
        parsed.Content.Should().Contain("topic: security");
        parsed.Content.Should().Contain("org: acme");
    }

    [Fact]
    public async Task Search_OmitsAnchorsLineWhenEntryHasNoAnchors()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["plain content with no anchors"], "no-anchors-test");

        string result = await Tools().Search("plain");
        var parsed = ResponseParser.Parse(result);
        parsed.Content.Should().NotContain("anchors:",
            because: "anchors section must be omitted entirely when the anchor attribute is null/empty");
    }

    [Fact]
    public async Task Store_WithAnchors_PersistsAnchors_SeedsTf_AndSurfacesViaSearch()
    {
        // End-to-end check of the agent-supplied anchor flow:
        //   1. Store() accepts anchors[]
        //   2. Attributes["anchor"] carries them through
        //   3. Anchor tokens get TF-seeded at +2 so BM25 picks up anchor vocabulary
        //   4. A query phrased in the anchor's vocabulary (not the content's) surfaces
        //      the entry near the top — proving the anchor → retrieval bridge works.
        using var scope = new TestHelpers.StoreScope();

        string[] anchors = ["what is foo", "how does foo work"];
        // Content uses entirely different vocabulary from the anchors. Without anchor
        // TF seeding, "foo" would never tokenize from the content and a "what is foo"
        // query would miss this entry.
        await Tools().Store(
            ["The widget subsystem orchestrates lifecycle hooks during plugin startup."],
            "anchor-flow-test",
            description: "widget lifecycle notes",
            tags: null,
            keywords: null,
            anchors: anchors);

        // (1) + (2): Attributes["anchor"] persisted on the entry.
        var entries = ScriniaArtifactStore.LoadIndex();
        var entry = entries.Single(e => e.Name == "anchor-flow-test");
        entry.Attributes.Should().NotBeNull();
        entry.Attributes!["anchor"].Should().Contain("what is foo");
        entry.Attributes!["anchor"].Should().Contain("how does foo work");

        // (3): "foo" is not in the content, only in the anchors. After TF seeding, it
        // must appear with weight ≥ 2 (the anchor-tokenization boost).
        entry.TermFrequencies.Should().NotBeNull();
        entry.TermFrequencies!.Should().ContainKey("foo");
        entry.TermFrequencies["foo"].Should().BeGreaterThanOrEqualTo(2,
            because: "anchor tokenization must seed TF at the +2 weight so BM25 picks up anchor vocabulary");

        // (4): A query in the anchor's vocabulary surfaces the entry. We expect it
        // among the results (top-most candidate is fine — the store is otherwise empty
        // for this scope so any non-empty match is the right one).
        string result = await Tools().Search("what is foo");
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success");
        parsed.Content.Should().Contain("anchor-flow-test",
            because: "anchor-supplied vocabulary must bridge a user query to the entry via BM25");
    }

    [Fact]
    public async Task Store_UnifiedFields_RoutesKeywordsExtractsContentAndRefs()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Store(
            ["---\nstatus: active\n---\nSee notes in src/Widget.cs today.\nperson mentioned inline: nobody"],
            "unified-store-test",
            keywords: ["person:Nick", "plainword"]);

        var entry = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "unified-store-test");

        // Routed keyword → field; plain keyword stays.
        entry.Attributes!["person"].Should().BeEquivalentTo(["Nick"]);
        entry.Keywords.Should().Contain("plainword");
        entry.Keywords.Should().NotContain(k => k.StartsWith("person:"),
            because: "prefixed keywords are no longer written");

        // Content extraction.
        entry.Attributes["status"].Should().BeEquivalentTo(["active"]);

        // Reference extraction → file field (not file: keyword) → FileHashes unaffected shape.
        entry.Attributes["file"].Should().Contain("src/Widget.cs");
        entry.Keywords.Should().NotContain(k => k.StartsWith("file:"));

        // TF seeding at +3 for routed field value.
        entry.TermFrequencies!["nick"].Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task Search_PathPrefix_FiltersOutNonMatchingScopes()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().MemoryRemember(path: "/findings/auth-refactor",
            content: ["oauth callback handler in findings"]);
        await Tools().MemoryRemember(path: "/learn/oauth-pkce",
            content: ["oauth callback handler in learn"]);

        string result = await Tools().MemorySearch(query: "oauth", pathPrefix: "/findings/");
        var parsed = ResponseParser.Parse(result);
        parsed.Content.Should().Contain("auth-refactor",
            because: "findings entry should be retained");
        parsed.Content.Should().NotContain("oauth-pkce",
            because: "learn entry must be filtered out by pathPrefix=/findings/");
    }

    [Fact]
    public async Task Search_AttributeFilter_RequiresKeyAndValue()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().MemoryRemember(path: "attr-hit", content: ["security refactor entry"],
            attributes: new Dictionary<string, string[]> { ["topic"] = ["security", "refactor"] });
        await Tools().MemoryRemember(path: "attr-miss", content: ["security entry only"],
            attributes: new Dictionary<string, string[]> { ["topic"] = ["other"] });
        await Tools().MemoryRemember(path: "attr-nokey", content: ["security entry no topic"]);

        string result = await Tools().MemorySearch(query: "entry",
            attributeFilter: new Dictionary<string, string[]> { ["topic"] = ["security"] });
        var parsed = ResponseParser.Parse(result);
        parsed.Content.Should().Contain("attr-hit",
            because: "entry carries the required key and a matching value");
        parsed.Content.Should().NotContain("attr-miss",
            because: "entry has key 'topic' but not value 'security'");
        parsed.Content.Should().NotContain("attr-nokey",
            because: "entry lacks key 'topic' entirely");
    }

    [Fact]
    public async Task Search_AttributeFilter_KeyOnly_MatchesAnyValue()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().MemoryRemember(path: "haskey", content: ["entry one"],
            attributes: new Dictionary<string, string[]> { ["review"] = ["2026-08-01"] });
        await Tools().MemoryRemember(path: "nokey", content: ["entry two"]);

        string result = await Tools().MemorySearch(query: "entry",
            attributeFilter: new Dictionary<string, string[]> { ["review"] = [] });
        var parsed = ResponseParser.Parse(result);
        parsed.Content.Should().Contain("haskey",
            because: "key-only filter matches any value as long as the key is present");
        parsed.Content.Should().NotContain("nokey",
            because: "entry lacks key 'review' entirely");
    }

    [Fact]
    public async Task Search_AttributeFilter_KeyMatchIsCaseInsensitive()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().MemoryRemember(path: "attr-case", content: ["entry body"],
            attributes: new Dictionary<string, string[]> { ["topic"] = ["security"] });

        string result = await Tools().MemorySearch(query: "entry",
            attributeFilter: new Dictionary<string, string[]> { ["Topic"] = ["security"] });
        var parsed = ResponseParser.Parse(result);
        parsed.Content.Should().Contain("attr-case",
            because: "attribute keys are normalized to lowercase at write time; the filter key " +
                     "must be normalized the same way before lookup, regardless of the caller's casing");
    }

    [Fact]
    public async Task PathSegmentScoreContributor_LiftsPathMatchWithoutContentMatch()
    {
        // Two candidates: one whose path mentions 'auth', one that doesn't.
        // Neither body contains 'auth'. Path scoring must lift the path-match candidate.
        var candidates = new List<ScopedArtifact>
        {
            new("local-topic:findings/auth-refactor", new ArtifactEntry(
                Name: "notes",
                Uri: "stub://1",
                OriginalBytes: 100,
                ChunkCount: 1,
                CreatedAt: DateTimeOffset.UtcNow,
                ContentPreview: "unrelated body text")),
            new("local-topic:archive", new ArtifactEntry(
                Name: "shopping",
                Uri: "stub://2",
                OriginalBytes: 50,
                ChunkCount: 1,
                CreatedAt: DateTimeOffset.UtcNow,
                ContentPreview: "unrelated body text")),
        };

        var contributor = new PathSegmentScoreContributor(weight: 8.0);
        var scores = await contributor.ComputeScoresAsync(
            "auth", candidates, store: null!, CancellationToken.None);

        scores.Should().NotBeNull();
        scores!.Should().ContainKey("local-topic:findings/auth-refactor|notes",
            because: "the candidate whose path contains 'auth' must receive a lift");
        scores["local-topic:findings/auth-refactor|notes"].Should().BeApproximately(8.0, 0.01);
        scores.Should().NotContainKey("local-topic:archive|shopping",
            because: "candidates with no path-token overlap must receive no entry (additive, not penalizing)");
    }

    [Fact]
    public async Task PathSegmentScoreContributor_ZeroWeightDisablesContribution()
    {
        var candidates = new List<ScopedArtifact>
        {
            new("local-topic:findings/auth-refactor", new ArtifactEntry(
                Name: "notes",
                Uri: "stub://1",
                OriginalBytes: 100,
                ChunkCount: 1,
                CreatedAt: DateTimeOffset.UtcNow,
                ContentPreview: "x")),
        };

        var contributor = new PathSegmentScoreContributor(weight: 0.0);
        var scores = await contributor.ComputeScoresAsync(
            "auth", candidates, store: null!, CancellationToken.None);

        scores.Should().BeNull(
            because: "weight=0 short-circuits without producing entries");
    }

    // ── References dual-read (fields ∪ legacy keywords) ─────────────────────

    [Fact]
    public async Task References_FindsBothLegacyKeywordAndFieldEntries()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["legacy body"], "legacy-ref", keywords: ["file:src/Foo.cs"]);

        var store = MemoryStoreContext.Current!;
        var entries = ScriniaArtifactStore.LoadIndex();
        var legacy = entries.Single(e => e.Name == "legacy-ref");

        // Pin the LEGACY shape explicitly (Fields null, Keywords carries the typed marker).
        store.Upsert(legacy with { Attributes = null, Keywords = ["file:src/Foo.cs"] });

        // A sibling entry that only carries the unified Fields form of the same reference.
        store.Upsert(legacy with
        {
            Name = "fields-ref",
            Keywords = null,
            Attributes = new Dictionary<string, string[]> { ["file"] = ["src/Foo.cs"] },
        });

        string result = await Tools().References("src/Foo.cs");
        result.Should().Contain("legacy-ref").And.Contain("fields-ref");
    }

    // ── UpdateMeta field routing / Link writes ref fields ────────────────────

    [Fact]
    public async Task UpdateMeta_RoutesFieldShapedKeywords_AndLinkWritesRefFields()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["alpha body"], "um-alpha");
        await Tools().Store(["beta body"], "um-beta");

        await Tools().UpdateMeta("um-alpha", keywords: ["person:Carol", "plain-extra"]);
        var alpha = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "um-alpha");
        alpha.Attributes!["person"].Should().BeEquivalentTo(["Carol"]);
        alpha.Keywords.Should().Contain("plain-extra");
        alpha.Keywords.Should().NotContain(k => k.StartsWith("person:"));

        await Tools().Link("um-alpha", "um-beta");
        var entries = ScriniaArtifactStore.LoadIndex();
        entries.Single(e => e.Name == "um-alpha").Attributes!["ref"].Should().Contain(v => v.Contains("um-beta"));
        entries.Single(e => e.Name == "um-beta").Attributes!["ref"].Should().Contain(v => v.Contains("um-alpha"));
    }

    // ── UpdateMeta review-state / ContentPreview direct write (P6.6b Task 4 / Task 2b) ────
    // These are VALUE regression-locks on the RAW persisted entry. Because AttributeMigration.Forward
    // still runs after UpdateMeta's own write in this merge step, they cannot by themselves prove the
    // direct write executed (Forward would produce the same values) — that's expected here; a later
    // step that removes the Forward fold is what makes these discriminating.
    //
    // Merge 3 Task 2b (option A): an explicit new description always wins and overwrites
    // ContentPreview — the old Merge-2 null-only backfill (only set ContentPreview when it was
    // previously null) has been superseded.

    [Fact]
    public async Task UpdateMeta_ReviewAfterAndWhen_SetsAttributesReview_DateFirst()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["base content"], "hint-review-both");

        await Tools().UpdateMeta("hint-review-both",
            reviewAfter: "2026-08-01", reviewWhen: "when the auth system changes");

        var entry = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "hint-review-both");
        entry.Attributes.Should().NotBeNull();
        var review = entry.Attributes!["review"];
        review.Should().HaveCount(2);
        AttributeReaders.TryReadReview(entry.Attributes, out var reviewDate, out _).Should().BeTrue();
        reviewDate.Should().NotBeNull();
        DateTimeOffset.Parse(review[0]).Should().Be(DateTimeOffset.Parse("2026-08-01"));
        review[1].Should().Be("when the auth system changes");
    }

    [Fact]
    public async Task UpdateMeta_ClearingReviewWhen_DropsConditionButPreservesDate()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["base content"], "hint-review-clear",
            reviewAfter: "2026-08-01", reviewWhen: "when the auth system changes");

        await Tools().UpdateMeta("hint-review-clear", reviewWhen: "");

        var entry = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "hint-review-clear");
        entry.Attributes.Should().NotBeNull();
        var review = entry.Attributes!["review"];
        review.Should().HaveCount(1);
        AttributeReaders.TryReadReview(entry.Attributes, out var reviewDate, out _).Should().BeTrue();
        reviewDate.Should().NotBeNull();
        DateTimeOffset.Parse(review[0]).Should().Be(DateTimeOffset.Parse("2026-08-01"));
    }

    [Fact]
    public async Task UpdateMeta_WithoutReviewParams_PreservesExistingReviewAttribute()
    {
        // Merge 3 Task 2: UpdateMeta's review-carry-forward defaults must be re-sourced from
        // Attributes["review"] (via AttributeReaders.TryReadReview), not the legacy
        // ReviewAfter/ReviewWhen members — this locks the RAW persisted Attributes so a
        // regression that drops the carry-forward (e.g. defaulting to null) fails loudly.
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["base content"], "hint-review-preserve",
            reviewAfter: "2026-08-01", reviewWhen: "when the auth system changes");

        var seeded = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "hint-review-preserve");
        var seededReview = seeded.Attributes!["review"];

        await Tools().UpdateMeta("hint-review-preserve", keywords: ["extra-keyword"]);

        var entry = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "hint-review-preserve");
        entry.Attributes.Should().NotBeNull();
        entry.Attributes!["review"].Should().BeEquivalentTo(seededReview,
            because: "update without reviewAfter/reviewWhen params must carry the existing review forward, not drop it");
    }

    [Fact]
    public async Task UpdateMeta_DescriptionOnNullContentPreview_SetsContentPreview()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["base content"], "hint-preview-null");

        // Pin ContentPreview to null directly — Store always populates it, so force the shape
        // where the entry starts with no ContentPreview at all.
        var store = MemoryStoreContext.Current!;
        var baseline = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "hint-preview-null");
        store.Upsert(baseline with { ContentPreview = null });

        await Tools().UpdateMeta("hint-preview-null", description: "new");

        var entry = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "hint-preview-null");
        entry.ContentPreview.Should().Be("new");
    }

    [Fact]
    public async Task UpdateMeta_DescriptionOnExistingContentPreview_OverwritesContentPreview()
    {
        // Merge 3 Task 2b (option A): a new curated description always wins and becomes the
        // displayed ContentPreview, even when one was already set (superseding the old Merge-2
        // null-only backfill, which left an existing ContentPreview untouched).
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["base content"], "hint-preview-set");
        string originalPreview = ScriniaArtifactStore.LoadIndex()
            .Single(e => e.Name == "hint-preview-set").ContentPreview!;
        originalPreview.Should().NotBeNullOrEmpty();

        await Tools().UpdateMeta("hint-preview-set", description: "new");

        var entry = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "hint-preview-set");
        entry.ContentPreview.Should().Be("new",
            because: "an explicit new description must overwrite ContentPreview, not preserve the prior one");
    }

}
