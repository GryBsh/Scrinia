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
    public void ExtractMemoryRefs_FindsWikiLinkPaths()
    {
        string input = "See [[/api/auth-flow]] and [[/backlog/resilience]] for details";

        var refs = ReferenceExtractor.ExtractMemoryRefs(input);

        refs.Should().Contain("/api/auth-flow");
        refs.Should().Contain("/backlog/resilience");
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
    public async Task Store_ExtractsFileRefsAsFields()
    {
        // Arrange & Act — store a memory with content mentioning a file path
        await _tools.Remember(
            ["This change modifies src/Foo.cs to add new functionality."],
            "ref-file-test");

        // Assert — verify the entry's Fields["file"] includes the file ref, and that the
        // legacy 'file:' prefixed keyword is no longer written.
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("ref-file-test");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        entry.Attributes.Should().NotBeNull();
        entry.Attributes!["file"].Should().Contain("src/Foo.cs",
            "store should auto-extract file references into the unified 'file' FIELD");
        entry.Keywords.Should().NotContain(k => k.StartsWith("file:"),
            "prefixed 'file:' keywords are no longer written");
    }

    [Fact]
    public async Task References_FindsMemoryByFileRef()
    {
        // Arrange — store a memory that mentions a file path
        await _tools.Remember(
            ["Updated src/Bar.cs with new validation logic."],
            "ref-bar-test");

        // Act — call references with the file name
        string result = (await _tools.References("Bar.cs")).ToYaml();

        // Assert — the memory should appear in the results
        result.Should().Contain("ref-bar-test",
            "references() should find a memory whose content mentions the target file");
    }

    [Fact]
    public async Task Link_CreatesBidirectionalRefFields()
    {
        // Arrange — create two memories
        await _tools.Remember(["Memory A content"], "link-test-a");
        await _tools.Remember(["Memory B content"], "link-test-b");

        // Act — link them
        string result = (await _tools.Link("link-test-a", "link-test-b", "test reason")).ToYaml();

        // Assert — response confirms the link
        result.Should().Contain("Linked");
        result.Should().Contain("test reason");

        // Verify bidirectional ref FIELDS (UpdateMeta now routes ref:-shaped keywords into Fields).
        var store = MemoryStoreContext.Current!;

        var (scopeA, subjectA) = store.ParseQualifiedName("link-test-a");
        var entriesA = store.LoadIndex(scopeA);
        var entryA = entriesA.First(e => e.Name.Equals(subjectA, StringComparison.OrdinalIgnoreCase));
        entryA.Attributes.Should().NotBeNull();
        entryA.Attributes!["ref"].Should().Contain(v => v.Contains("link-test-b"),
            "linking should add the target to the ref FIELD on the source entry");
        entryA.Keywords.Should().NotContain(k => k.StartsWith("ref:"),
            "prefixed ref: keywords are no longer written");

        var (scopeB, subjectB) = store.ParseQualifiedName("link-test-b");
        var entriesB = store.LoadIndex(scopeB);
        var entryB = entriesB.First(e => e.Name.Equals(subjectB, StringComparison.OrdinalIgnoreCase));
        entryB.Attributes.Should().NotBeNull();
        entryB.Attributes!["ref"].Should().Contain(v => v.Contains("link-test-a"),
            "linking should add the source to the ref FIELD on the target entry");
        entryB.Keywords.Should().NotContain(k => k.StartsWith("ref:"),
            "prefixed ref: keywords are no longer written");
    }

    [Fact]
    public async Task References_FindsLinkedMemory()
    {
        // Arrange — create two memories and link them
        await _tools.Remember(["Alpha content"], "linked-alpha");
        await _tools.Remember(["Beta content"], "linked-beta");
        await _tools.Link("linked-alpha", "linked-beta");

        // Act — search references for alpha
        string result = (await _tools.References("linked-alpha")).ToYaml();

        // Assert — beta should appear because it has ref:linked-alpha keyword
        result.Should().Contain("linked-beta",
            "references() should find memories linked to the target via ref: keywords");
    }

    // ── FileHashes & drift detection tests ────────────────────────────────────

    [Fact]
    public async Task Store_WithCodeRefs_RecordsHashes()
    {
        // Arrange — create a temp file relative to the workspace root
        string relPath = "src/example.cs";
        string fullPath = Path.Combine(_scope.WorkspaceDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "public class Example {}");

        // Act — store a memory with codeRefs pointing to the file
        await _tools.Remember(
            ["Documents the Example class."],
            "coderef-hash-test",
            attributes: new Dictionary<string, string[]> { ["file"] = [relPath] });

        // Assert — verify the entry's FileHashes contains the file with a non-empty hash
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("coderef-hash-test");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        entry.FileHashes.Should().NotBeNull("store with codeRefs should populate FileHashes dictionary");
        entry.FileHashes.Should().ContainKey(relPath, "FileHashes should include the referenced file path");
        entry.FileHashes![relPath].Should().NotBeNullOrEmpty("hash value should be a non-empty SHA-256 hex string");
    }

    [Fact]
    public async Task Store_WithCodeRefs_MissingFile_Skipped()
    {
        // Arrange — reference a file that does not exist
        string relPath = "nonexistent/phantom.cs";

        // Act — store with codeRefs pointing to a missing file (should not throw)
        await _tools.Remember(
            ["References a file that doesn't exist."],
            "coderef-missing-test",
            attributes: new Dictionary<string, string[]> { ["file"] = [relPath] });

        // Assert — FileHashes should be empty or not contain the missing file
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("coderef-missing-test");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        if (entry.FileHashes is not null)
        {
            entry.FileHashes.Should().NotContainKey(relPath,
                "missing files should be skipped, not recorded in FileHashes");
        }
    }

    [Fact]
    public async Task ListDrift_NoDrift_ReportsOk()
    {
        // Arrange — create a temp file and store a memory with codeRefs
        string relPath = "src/stable.cs";
        string fullPath = Path.Combine(_scope.WorkspaceDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "// stable content");

        await _tools.Remember(
            ["Tracks stable.cs for drift."],
            "drift-ok-test",
            attributes: new Dictionary<string, string[]> { ["file"] = [relPath] });

        // Act — check drift via list(mode: "drift")
        string result = (await _tools.List(mode: "drift")).ToYaml();

        // Assert — should report all references are current with no drift
        result.Should().Contain("current",
            "list(mode:'drift') should report 'current' when no files have changed");
        result.Should().NotContain("DRIFT",
            "list(mode:'drift') should not report DRIFT when files are unchanged");
    }

    [Fact]
    public async Task ListDrift_FileChanged_DetectsDrift()
    {
        // Arrange — create a temp file and store a memory with codeRefs
        string relPath = "src/changing.cs";
        string fullPath = Path.Combine(_scope.WorkspaceDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "// original content");

        await _tools.Remember(
            ["Tracks changing.cs for drift."],
            "drift-changed-test",
            attributes: new Dictionary<string, string[]> { ["file"] = [relPath] });

        // Modify the file after storing
        await File.WriteAllTextAsync(fullPath, "// modified content — different hash");

        // Act — check drift via list(mode: "drift")
        string result = (await _tools.List(mode: "drift")).ToYaml();

        // Assert — should detect drift
        result.Should().Contain("DRIFT",
            "list(mode:'drift') should report DRIFT when a referenced file has been modified");
    }

    [Fact]
    public async Task ListDrift_FileMissing_DetectsMissing()
    {
        // Arrange — create a temp file and store a memory with codeRefs
        string relPath = "src/ephemeral.cs";
        string fullPath = Path.Combine(_scope.WorkspaceDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "// will be deleted");

        await _tools.Remember(
            ["Tracks ephemeral.cs for drift."],
            "drift-missing-test",
            attributes: new Dictionary<string, string[]> { ["file"] = [relPath] });

        // Delete the file after storing
        File.Delete(fullPath);

        // Act — check drift via list(mode: "drift")
        string result = (await _tools.List(mode: "drift")).ToYaml();

        // Assert — should detect the file as missing
        result.Should().Contain("MISSING",
            "list(mode:'drift') should report MISSING when a referenced file has been deleted");
    }

    [Fact]
    public async Task List_Full_ShowsDriftMarker_WhenFileChanged()
    {
        // Arrange — create a temp file and store a memory with codeRefs
        string relPath = "src/drifted.cs";
        string fullPath = Path.Combine(_scope.WorkspaceDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "// original");

        await _tools.Remember(
            ["Tracks drifted.cs."],
            "drift-list-test",
            attributes: new Dictionary<string, string[]> { ["file"] = [relPath] });

        // Modify the file after storing
        await File.WriteAllTextAsync(fullPath, "// changed");

        // Act — list in full mode
        string result = (await _tools.List(mode: "full")).ToYaml();

        // Assert — the listing should contain a [drift] marker
        result.Should().Contain("[drift]",
            "list(mode='full') should show [drift] marker for entries whose code references have changed");
    }
}
