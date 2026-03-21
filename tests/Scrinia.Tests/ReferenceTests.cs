using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Search;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Unit tests for ReferenceExtractor, the references() query tool, and the link() tool.
/// </summary>
public sealed class ReferenceTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaMcpTools _tools;

    public ReferenceTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaMcpTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── ReferenceExtractor unit tests ─────────────────────────────────────────

    [Fact]
    public void ExtractFileRefs_FindsKnownExtensions()
    {
        string input = "Modified src/Scrinia.Core/FileMemoryStore.cs and appsettings.json";

        var refs = ReferenceExtractor.ExtractFileRefs(input);

        refs.Should().Contain("src/Scrinia.Core/FileMemoryStore.cs");
        refs.Should().Contain("appsettings.json");
    }

    [Fact]
    public void ExtractFileRefs_IgnoresUrls()
    {
        string input = "See https://api.openai.com/v1/embeddings";

        var refs = ReferenceExtractor.ExtractFileRefs(input);

        refs.Should().NotContain(r => r.Contains("openai") || r.Contains("api"),
            "URL paths should not be extracted as file references");
    }

    [Fact]
    public void ExtractFileRefs_EmptyInput()
    {
        ReferenceExtractor.ExtractFileRefs(null).Should().BeEmpty("null input should return empty array");
        ReferenceExtractor.ExtractFileRefs("").Should().BeEmpty("empty string should return empty array");
        ReferenceExtractor.ExtractFileRefs("   ").Should().BeEmpty("whitespace-only input should return empty array");
    }

    [Fact]
    public void ExtractMemoryRefs_FindsTopicSubject()
    {
        string input = "See api:auth-flow and backlog:resilience for details";

        var refs = ReferenceExtractor.ExtractMemoryRefs(input);

        refs.Should().Contain("api:auth-flow");
        refs.Should().Contain("backlog:resilience");
    }

    [Fact]
    public void ExtractMemoryRefs_FindsEphemeral()
    {
        string input = "Stored in ~checkpoint and ~scratch";

        var refs = ReferenceExtractor.ExtractMemoryRefs(input);

        refs.Should().Contain("~checkpoint");
        refs.Should().Contain("~scratch");
    }

    [Fact]
    public void ExtractMemoryRefs_IgnoresNamespaces()
    {
        string input = "Scrinia.Core.Resilience namespace";

        var refs = ReferenceExtractor.ExtractMemoryRefs(input);

        refs.Should().NotContain(r => r.Contains("Core") || r.Contains("Resilience"),
            "C# namespaces (capital letters, dot-separated) should not be extracted as memory references");
    }

    // ── Integration tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task Store_ExtractsFileRefsAsKeywords()
    {
        // Arrange & Act — store a memory with content mentioning a file path
        await _tools.Store(
            ["This change modifies src/Foo.cs to add new functionality."],
            "ref-file-test");

        // Assert — verify the entry's keywords include the file ref
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("ref-file-test");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        entry.Keywords.Should().Contain("file:src/Foo.cs",
            "store should auto-extract file references as 'file:' prefixed keywords");
    }

    [Fact]
    public async Task References_FindsMemoryByFileRef()
    {
        // Arrange — store a memory that mentions a file path
        await _tools.Store(
            ["Updated src/Bar.cs with new validation logic."],
            "ref-bar-test");

        // Act — call references with the file name
        string result = await _tools.References("Bar.cs");

        // Assert — the memory should appear in the results
        result.Should().Contain("ref-bar-test",
            "references() should find a memory whose content mentions the target file");
    }

    [Fact]
    public async Task Link_CreatesBidirectionalKeywords()
    {
        // Arrange — create two memories
        await _tools.Store(["Memory A content"], "link-test-a");
        await _tools.Store(["Memory B content"], "link-test-b");

        // Act — link them
        string result = await _tools.Link("link-test-a", "link-test-b", "test reason");

        // Assert — response confirms the link
        result.Should().Contain("Linked");
        result.Should().Contain("test reason");

        // Verify bidirectional ref: keywords
        var store = MemoryStoreContext.Current!;

        var (scopeA, subjectA) = store.ParseQualifiedName("link-test-a");
        var entriesA = store.LoadIndex(scopeA);
        var entryA = entriesA.First(e => e.Name.Equals(subjectA, StringComparison.OrdinalIgnoreCase));
        entryA.Keywords.Should().Contain("ref:link-test-b",
            "linking should add ref:{target} keyword to the source entry");

        var (scopeB, subjectB) = store.ParseQualifiedName("link-test-b");
        var entriesB = store.LoadIndex(scopeB);
        var entryB = entriesB.First(e => e.Name.Equals(subjectB, StringComparison.OrdinalIgnoreCase));
        entryB.Keywords.Should().Contain("ref:link-test-a",
            "linking should add ref:{source} keyword to the target entry");
    }

    [Fact]
    public async Task References_FindsLinkedMemory()
    {
        // Arrange — create two memories and link them
        await _tools.Store(["Alpha content"], "linked-alpha");
        await _tools.Store(["Beta content"], "linked-beta");
        await _tools.Link("linked-alpha", "linked-beta");

        // Act — search references for alpha
        string result = await _tools.References("linked-alpha");

        // Assert — beta should appear because it has ref:linked-alpha keyword
        result.Should().Contain("linked-beta",
            "references() should find memories linked to the target via ref: keywords");
    }

    // ── CodeRefs & drift detection tests ────────────────────────────────────

    [Fact]
    public async Task Store_WithCodeRefs_RecordsHashes()
    {
        // Arrange — create a temp file relative to the workspace root
        string relPath = "src/example.cs";
        string fullPath = Path.Combine(_scope.WorkspaceDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "public class Example {}");

        // Act — store a memory with codeRefs pointing to the file
        await _tools.Store(
            ["Documents the Example class."],
            "coderef-hash-test",
            codeRefs: [relPath]);

        // Assert — verify the entry's CodeRefs contains the file with a non-empty hash
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("coderef-hash-test");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        entry.CodeRefs.Should().NotBeNull("store with codeRefs should populate CodeRefs dictionary");
        entry.CodeRefs.Should().ContainKey(relPath, "CodeRefs should include the referenced file path");
        entry.CodeRefs![relPath].Should().NotBeNullOrEmpty("hash value should be a non-empty SHA-256 hex string");
    }

    [Fact]
    public async Task Store_WithCodeRefs_MissingFile_Skipped()
    {
        // Arrange — reference a file that does not exist
        string relPath = "nonexistent/phantom.cs";

        // Act — store with codeRefs pointing to a missing file (should not throw)
        await _tools.Store(
            ["References a file that doesn't exist."],
            "coderef-missing-test",
            codeRefs: [relPath]);

        // Assert — CodeRefs should be empty or not contain the missing file
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("coderef-missing-test");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        if (entry.CodeRefs is not null)
        {
            entry.CodeRefs.Should().NotContainKey(relPath,
                "missing files should be skipped, not recorded in CodeRefs");
        }
    }

    [Fact]
    public async Task CheckDrift_NoDrift_ReportsOk()
    {
        // Arrange — create a temp file and store a memory with codeRefs
        string relPath = "src/stable.cs";
        string fullPath = Path.Combine(_scope.WorkspaceDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "// stable content");

        await _tools.Store(
            ["Tracks stable.cs for drift."],
            "drift-ok-test",
            codeRefs: [relPath]);

        // Act — check drift without modifying the file
        string result = await _tools.CheckDrift();

        // Assert — should report all references are current with no drift
        result.Should().Contain("current",
            "check_drift should report 'current' when no files have changed");
        result.Should().NotContain("DRIFT",
            "check_drift should not report DRIFT when files are unchanged");
    }

    [Fact]
    public async Task CheckDrift_FileChanged_DetectsDrift()
    {
        // Arrange — create a temp file and store a memory with codeRefs
        string relPath = "src/changing.cs";
        string fullPath = Path.Combine(_scope.WorkspaceDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "// original content");

        await _tools.Store(
            ["Tracks changing.cs for drift."],
            "drift-changed-test",
            codeRefs: [relPath]);

        // Modify the file after storing
        await File.WriteAllTextAsync(fullPath, "// modified content — different hash");

        // Act — check drift
        string result = await _tools.CheckDrift();

        // Assert — should detect drift
        result.Should().Contain("DRIFT",
            "check_drift should report DRIFT when a referenced file has been modified");
    }

    [Fact]
    public async Task CheckDrift_FileMissing_DetectsMissing()
    {
        // Arrange — create a temp file and store a memory with codeRefs
        string relPath = "src/ephemeral.cs";
        string fullPath = Path.Combine(_scope.WorkspaceDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "// will be deleted");

        await _tools.Store(
            ["Tracks ephemeral.cs for drift."],
            "drift-missing-test",
            codeRefs: [relPath]);

        // Delete the file after storing
        File.Delete(fullPath);

        // Act — check drift
        string result = await _tools.CheckDrift();

        // Assert — should detect the file as missing
        result.Should().Contain("MISSING",
            "check_drift should report MISSING when a referenced file has been deleted");
    }

    [Fact]
    public async Task List_Full_ShowsDriftMarker_WhenFileChanged()
    {
        // Arrange — create a temp file and store a memory with codeRefs
        string relPath = "src/drifted.cs";
        string fullPath = Path.Combine(_scope.WorkspaceDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "// original");

        await _tools.Store(
            ["Tracks drifted.cs."],
            "drift-list-test",
            codeRefs: [relPath]);

        // Modify the file after storing
        await File.WriteAllTextAsync(fullPath, "// changed");

        // Act — list in full mode
        string result = await _tools.List(mode: "full");

        // Assert — the listing should contain a [drift] marker
        result.Should().Contain("[drift]",
            "list(mode='full') should show [drift] marker for entries whose code references have changed");
    }
}
