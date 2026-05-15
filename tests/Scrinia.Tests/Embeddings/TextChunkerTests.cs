using FluentAssertions;
using Scrinia.Core.Embeddings;
using Xunit;

namespace Scrinia.Tests.Embeddings;

public class TextChunkerTests
{
    [Fact]
    public void ShortText_ReturnsSingleChunk()
    {
        var chunks = TextChunker.SliceWindows("hello world", windowSize: 1200, overlap: 200);

        chunks.Should().HaveCount(1);
        chunks[0].Index.Should().Be(0);
        chunks[0].Text.Should().Be("hello world");
    }

    [Fact]
    public void Empty_ReturnsEmpty()
    {
        TextChunker.SliceWindows("", 1200, 200).Should().BeEmpty();
        TextChunker.SliceWindows("   \n\t  ", 1200, 200).Should().BeEmpty();
    }

    [Fact]
    public void LongerThanWindow_SlicesIntoOverlappingWindows()
    {
        // 250 chars: window=100, overlap=20 → step=80 → windows at [0..100], [80..180], [160..250].
        string text = new('x', 250);
        var chunks = TextChunker.SliceWindows(text, windowSize: 100, overlap: 20);

        chunks.Should().HaveCount(3);
        chunks[0].Index.Should().Be(0);
        chunks[1].Index.Should().Be(1);
        chunks[2].Index.Should().Be(2);
        chunks[0].Text.Length.Should().Be(100);
        chunks[1].Text.Length.Should().Be(100);
        // Final chunk: 250 - 160 = 90 chars.
        chunks[2].Text.Length.Should().Be(90);
    }

    [Fact]
    public void OverlapPreservesContentAcrossBoundary()
    {
        // Distinctive markers at boundaries should appear in both adjacent windows.
        // Build text: 80 'A's + "NEEDLE" + 80 'B's (total 166 chars). Window=100, overlap=30,
        // step=70. Windows: [0..100], [70..166].
        // NEEDLE lives at chars 80..86, which falls in both [0..100] and [70..166].
        string text = new string('A', 80) + "NEEDLE" + new string('B', 80);
        var chunks = TextChunker.SliceWindows(text, windowSize: 100, overlap: 30);

        chunks.Should().HaveCountGreaterThanOrEqualTo(2);
        chunks[0].Text.Should().Contain("NEEDLE");
        chunks[1].Text.Should().Contain("NEEDLE");
    }

    [Fact]
    public void WhitespaceAtBoundaries_IsTrimmed()
    {
        // Window boundaries cut into pure whitespace — slices should be trimmed.
        string text = "first chunk content     " + new string(' ', 100) + "second chunk content";
        var chunks = TextChunker.SliceWindows(text, windowSize: 30, overlap: 5);

        foreach (var c in chunks)
        {
            c.Text.Should().NotStartWith(" ");
            c.Text.Should().NotEndWith(" ");
        }
    }

    [Fact]
    public void OverlapGreaterOrEqualToWindowSize_Throws()
    {
        var act1 = () => TextChunker.SliceWindows("anything", windowSize: 100, overlap: 100);
        var act2 = () => TextChunker.SliceWindows("anything", windowSize: 100, overlap: 200);

        act1.Should().Throw<ArgumentOutOfRangeException>();
        act2.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void NegativeOrZeroParams_Throw()
    {
        var act1 = () => TextChunker.SliceWindows("anything", windowSize: 0, overlap: 0);
        var act2 = () => TextChunker.SliceWindows("anything", windowSize: 100, overlap: -1);

        act1.Should().Throw<ArgumentOutOfRangeException>();
        act2.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DefaultParams_HandleTypicalMemorySizes()
    {
        // ~3000-char memory → expect roughly 3 chunks at default 1200/200 (step=1000).
        string text = new('x', 3000);
        var chunks = TextChunker.SliceWindows(text);

        chunks.Count.Should().BeGreaterThanOrEqualTo(3);
        chunks.Count.Should().BeLessThanOrEqualTo(4);
        chunks[0].Text.Length.Should().Be(TextChunker.DefaultWindowSize);
    }
}
