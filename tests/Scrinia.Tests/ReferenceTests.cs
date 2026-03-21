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
}
