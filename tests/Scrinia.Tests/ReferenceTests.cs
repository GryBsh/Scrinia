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
        string input = "See https://api.openai.com/v1/embeddings and also src/Real.cs";

        var refs = ReferenceExtractor.ExtractFileRefs(input);

        refs.Should().NotContain(r => r.Contains("openai") || r.Contains("api"),
            "URL paths should not be extracted as file references");
        refs.Should().Contain("src/Real.cs",
            "a real path elsewhere in the line must still extract — a NotContain-only assertion " +
            "would pass even if extraction returned nothing at all");
    }

    [Fact]
    public void FileRef_AfterAColon_IsNotSilentlyTruncated()
    {
        // The lookbehind (?<!["'`=:]) does not suppress the match, it SHIFTS it: the engine fails
        // at 's' (preceded by ':'), advances one char, and succeeds at 'r' — yielding "rc/new.cs".
        // That corrupt path is indexed, seeded into term frequencies, and reports a clean drift
        // baseline because HashWorkspaceFile returns null for a path that does not exist.
        //
        // A pure negative here would pass with extraction disabled entirely (bare ReferenceExtractor
        // no longer produces ANYTHING for a path after a colon — that's correct, matching D-a's
        // fix). The positive half pins that the clean path still surfaces, via AttributeExtractor's
        // inline prose routing (D-b) — so this fails if either half regresses.
        ReferenceExtractor.ExtractFileRefs("see file:src/new.cs")
            .Should().NotContain("rc/new.cs");
        AttributeExtractor.Extract("see file:src/new.cs")!["file"]
            .Should().Contain("src/new.cs");
    }

    [Fact]
    public void ExtractFileRefs_RelativeParentPath_StillMatches()
    {
        // Widening FilePathPattern's lookbehind to exclude '.' (D-a's fix) means the engine can no
        // longer coast in from a later restart position the way the old narrower lookbehind
        // coincidentally allowed for "../..." — the prefix group must consume the whole ".." itself
        // or this regresses to matching nothing (a silent drift-baseline removal, same harm class
        // as D-a in the opposite direction).
        ReferenceExtractor.ExtractFileRefs("See ../src/a/b.cs for context")
            .Should().Contain("src/a/b.cs");
    }

    [Fact]
    public void ExtractFileRefs_WindowsRelativePath_StillMatches()
    {
        ReferenceExtractor.ExtractFileRefs(@"See .\src\a\b.cs for context")
            .Should().Contain(@"src\a\b.cs");
    }

    [Fact]
    public void ExtractFileRefs_TwoLevelsUpRelativePath_StillMatches()
    {
        // The prefix group is a Kleene star, not "?": a single optional repetition only ever
        // restores ONE "../" level. This is the third round this exact shape has appeared on this
        // task (each earlier fix restored one level and missed the next) — pin two levels deep so
        // the class of bug, not just one instance, is covered.
        ReferenceExtractor.ExtractFileRefs("See ../../src/a.cs for context")
            .Should().Contain("src/a.cs");
    }

    [Fact]
    public void ExtractFileRefs_TwoLevelsUpWindowsRelativePath_StillMatches()
    {
        ReferenceExtractor.ExtractFileRefs(@"See ..\..\src\a.cs for context")
            .Should().Contain(@"src\a.cs");
    }

    [Fact]
    public void ExtractFileRefs_UrlWithKnownExtension_StillIgnored()
    {
        // The prefix group's widened repetition must not turn into a URL-path recovery mechanism —
        // a colon-and-slashes URL still has no valid start position for FilePathPattern regardless
        // of how many times the prefix group can repeat once a start position exists.
        var refs = ReferenceExtractor.ExtractFileRefs("See https://ex.com/a/b.cs and src/Real.cs for the schema");

        refs.Should().NotContain(r => r.Contains("ex.com") || r.Contains("b.cs"));
        refs.Should().Contain("src/Real.cs",
            "a real path elsewhere in the line must still extract — a NotContain-only assertion " +
            "would pass even if extraction returned nothing at all");
    }

    [Theory]
    // Context, then input, then the expected extracted (post-TrimStart) path.
    [InlineData("start of line", "src/foo.cs is the file", "src/foo.cs")]
    [InlineData("after whitespace", "see src/foo.cs", "src/foo.cs")]
    [InlineData("after tab", "\tsrc/foo.cs", "src/foo.cs")]
    [InlineData("after '>'", ">src/foo.cs", "src/foo.cs")]
    [InlineData("after '('", "(src/foo.cs)", "src/foo.cs")]
    [InlineData("after ','", ",src/foo.cs", "src/foo.cs")]
    [InlineData("after ';'", ";src/foo.cs", "src/foo.cs")]
    [InlineData("after em dash", "note—src/foo.cs", "src/foo.cs")]
    [InlineData("markdown list item", "- src/foo.cs", "src/foo.cs")]
    [InlineData("numbered list item", "1. src/foo.cs", "src/foo.cs")]
    [InlineData("markdown link target", "[label](src/foo.cs)", "src/foo.cs")]
    [InlineData("bold-wrapped", "**src/foo.cs**", "src/foo.cs")]
    [InlineData("end of sentence", "see src/foo.cs.", "src/foo.cs")]
    [InlineData("'file: ' with space", "file: src/foo.cs", "src/foo.cs")]
    [InlineData("'/'-prefixed", "/src/foo.cs", "src/foo.cs")]
    [InlineData("'./'-prefixed", "./src/foo.cs", "src/foo.cs")]
    [InlineData("'\\'-prefixed", @"\src\foo.cs", @"src\foo.cs")]
    [InlineData("bare filename, no path", "Program.cs", "Program.cs")]
    [InlineData("'../../'-prefixed (two levels up)", "../../src/foo.cs", "src/foo.cs")]
    [InlineData("'..\\..\\'-prefixed (two levels up, Windows)", @"..\..\src\foo.cs", @"src\foo.cs")]
    public void ExtractFileRefs_ContextTable(string context, string input, string expected)
    {
        // Equal, not Contain: Contain cannot catch a spurious EXTRA extraction alongside the
        // expected one, which is exactly the failure class a regex change can introduce.
        ReferenceExtractor.ExtractFileRefs(input)
            .Should().Equal([expected], because: $"context: {context}");
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
        string input = "Scrinia.Core.Resilience namespace, see [[/api/real]]";

        var refs = ReferenceExtractor.ExtractMemoryRefs(input);

        refs.Should().NotContain(r => r.Contains("Core") || r.Contains("Resilience"),
            "C# namespaces (capital letters, dot-separated) should not be extracted as memory references");
        refs.Should().Contain("/api/real",
            "a real wiki-link elsewhere in the line must still extract — a NotContain-only " +
            "assertion would pass even if extraction returned nothing at all");
    }

    [Fact]
    public void PreFenceAwareness_PinsCurrentExtractionBehaviour()
    {
        // Pins ExtractFileRefs/ExtractMemoryRefs output as produced BEFORE this branch routes both
        // extractors through ProseScanner. Recorded by running against pre-change code (see
        // task-6-report.md). None of these lines sits inside a REAL fence — the indented ``` below
        // is deliberately not one, since ProseScanner inherits that gap unfixed — so this output is
        // the contract: byte-identical after the change.
        string content =
            "See src/Alpha.cs and src/Beta.cs for details.\n" +
            "Link: [[/api/gamma]].\n" +
            "Stored as ~delta.\n" +
            "Ref file:src/Epsilon.cs here.\n" +
            "Two levels up: ../../src/Zeta.cs done.\n" +
            "Backtick path (never extracts): `src/Iota.cs` here.\n" +
            "Backtick wiki link (extracts): `[[/api/eta]]` and ephemeral `~theta` here.\n" +
            "Links to [[\n/api/kappa\n]] here.\n" +
            "  ```\n" +
            "  src/Lambda.cs\n" +
            "  ```\n";

        ReferenceExtractor.ExtractFileRefs(content).Should().Equal(
            "src/Alpha.cs", "src/Beta.cs", "src/Zeta.cs", "src/Lambda.cs");
        ReferenceExtractor.ExtractMemoryRefs(content).Should().Equal(
            "/api/gamma", "/api/eta", "/api/kappa", "~delta", "~theta");
    }

    [Fact]
    public void ExtractFileRefs_IgnoresPathsInsideAFencedBlock()
    {
        string content = "See src/Real.cs.\n\n```\n  at Foo() in src/Fenced.cs:line 12\n  src/Other.cs\n```\n";
        ReferenceExtractor.ExtractFileRefs(content).Should().Equal("src/Real.cs");
    }

    [Fact]
    public void ExtractMemoryRefs_IgnoresLinksInsideAFencedBlock()
    {
        string content = "Links to [[/api/real]].\n```\n[[/api/fenced]]\n~fenced-scratch\n```\n";
        ReferenceExtractor.ExtractMemoryRefs(content).Should().Equal("/api/real");
    }

    /// <summary>An unterminated fence (opening ``` with no closer) runs to end of content per
    /// <see cref="ProseScanner"/> — not just the ``` line itself. Everything after it, including
    /// text that never visually looks "inside a code block", is suppressed. Pinned here rather than
    /// fixed: the semantic is inherited from <see cref="ProseScanner"/>, which
    /// <see cref="AttributeExtractor"/> also consumes, so changing it is a decision above this
    /// extractor.</summary>
    [Fact]
    public void ExtractFileRefs_UnterminatedFence_SuppressesToEndOfContent()
    {
        string content =
            "See src/Real.cs.\n```\nsrc/NeverClosed.cs\nMore prose mentioning src/AlsoLost.cs here.\n";
        ReferenceExtractor.ExtractFileRefs(content).Should().Equal("src/Real.cs");
    }

    /// <summary>Memory-ref counterpart of <see cref="ExtractFileRefs_UnterminatedFence_SuppressesToEndOfContent"/>.</summary>
    [Fact]
    public void ExtractMemoryRefs_UnterminatedFence_SuppressesToEndOfContent()
    {
        string content =
            "Links to [[/api/real]].\n```\n[[/api/never-closed]]\nMore prose with ~also-lost here.\n";
        ReferenceExtractor.ExtractMemoryRefs(content).Should().Equal("/api/real");
    }

    /// <summary>Frontmatter stays prose — a file: line there is deliberate metadata.</summary>
    [Fact]
    public void ExtractFileRefs_StillReadsFrontmatter()
    {
        ReferenceExtractor.ExtractFileRefs("---\nfile: src/Front.cs\n---\nbody")
            .Should().Equal("src/Front.cs");
    }

    /// <summary>Inline code stays prose for MEMORY refs, which is where the claim is true.
    /// A backtick-quoted PATH is not asserted here because it has never extracted — see the
    /// lookbehind at ReferenceExtractor.cs:35 and the comment block at :10-16.</summary>
    [Fact]
    public void ExtractMemoryRefs_StillReadsInlineCode()
    {
        ReferenceExtractor.ExtractMemoryRefs("See `[[/api/auth]]` and `~scratch` here")
            .Should().BeEquivalentTo(["/api/auth", "~scratch"]);
    }

    /// <summary>A wiki link wrapped across lines still extracts: MemoryNamePattern's \s* matches \n,
    /// so this pattern runs over rejoined prose rather than line by line.</summary>
    [Fact]
    public void ExtractMemoryRefs_StillReadsALinkWrappedAcrossLines()
    {
        ReferenceExtractor.ExtractMemoryRefs("Links to [[\n/api/auth\n]] here.")
            .Should().Equal("/api/auth");
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
    public async Task Link_WithAnEphemeralSide_RefusesBeforeWritingEither()
    {
        // link ran both UpdateMeta calls unconditionally. UpdateMeta refuses an ephemeral name, so
        // the ~scratch side failed — and then the OTHER side was written anyway, persisting
        // ref:~scratch on a memory that was never linked back and reporting "Partial link failure".
        // A dangling one-sided ref is worse than a refusal: it is a link the graph believes in.
        await _tools.Remember(["Persistent side"], "/patterns/link-target");
        await _tools.Remember(["Ephemeral side"], "~scratch");

        string result = (await _tools.Link("~scratch", "/patterns/link-target")).ToYaml();

        result.Should().Contain("ephemeral",
            because: "the tool description has always claimed link refuses an ephemeral side");

        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("/patterns/link-target");
        var entry = store.LoadIndex(scope)
            .First(e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        (entry.Attributes is not null && entry.Attributes.ContainsKey("ref")).Should().BeFalse(
            because: "a refused link must write NOTHING — the surviving half of a partial write is " +
                     "a ref pointing at a memory that carries no ref back");
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
