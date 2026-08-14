using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Tests for <see cref="ProseScanner.ProseLines"/> — the single fence-skipping rule shared by
/// every mechanical prose scan in <see cref="AttributeExtractor"/>.
/// </summary>
public class ProseScannerTests
{
    private static string[] Lines(string? content) => [.. ProseScanner.ProseLines(content)];

    [Fact]
    public void FencedBlock_AndItsDelimiters_Dropped()
    {
        string content = "before\n```\ninside one\ninside two\n```\nafter";
        Lines(content).Should().Equal(["before", "after"], because: "fence delimiters and their contents are not prose");
    }

    [Fact]
    public void Frontmatter_Kept()
    {
        string content = "---\nstatus: active\n---\nbody";
        Lines(content).Should().Equal(["---", "status: active", "---", "body"],
            because: "frontmatter is prose — it is deliberately not stripped here");
    }

    [Fact]
    public void InlineCodeSpans_Kept()
    {
        string content = "Set `MaxRetries` and call Bob.";
        Lines(content).Should().Equal([content],
            because: "inline code is prose — only fenced blocks are excluded");
    }

    [Fact]
    public void UnterminatedFence_RunsToEndOfContent()
    {
        string content = "before\n```\nnever closes\nstill inside";
        Lines(content).Should().Equal(["before"],
            because: "an unterminated fence excludes everything after it — the safe direction");
    }

    [Fact]
    public void Crlf_LeavesNoCarriageReturns()
    {
        string content = "line one\r\nline two\r\n";
        var lines = Lines(content);
        lines.Should().OnlyContain(l => !l.Contains('\r'));
        lines.Should().Equal(["line one", "line two", ""]);
    }

    [Fact]
    public void Null_YieldsNothing() =>
        Lines(null).Should().BeEmpty();

    [Fact]
    public void Empty_YieldsNothing() =>
        Lines("").Should().BeEmpty();

    [Fact]
    public void IndentedFence_IsNotRecognizedAsAFence()
    {
        // Known, deliberately-left gap: the fence test is StartsWith with no leading-whitespace
        // trim, so a fence indented inside a list item/blockquote is invisible — every line
        // "inside" it, including the fence markers themselves, is read as prose.
        string content = "- item\n  ```\n  code-shaped line\n  ```\nafter";
        Lines(content).Should().Equal(
            ["- item", "  ```", "  code-shaped line", "  ```", "after"],
            because: "an indented ``` does not StartsWith(\"```\") at column 0, so it is not treated as a fence");
    }
}
