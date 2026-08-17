using FluentAssertions;
using Scrinia.Core.Embeddings;

namespace Scrinia.Tests.Embeddings;

/// <summary>
/// Residual 2 of the 2026-08-16 embeddings-search-consolidation re-review.
/// <see cref="EmbeddingOptions.NormalizeGuardrails"/> is the one shared rule for every
/// out-of-range value that would otherwise reach <see cref="MemoryVectorIndexer"/>'s constructor
/// (a hard throw) or <c>TextChunker.SliceWindows</c> (also a throw) — previously the CLI applied
/// two of the three inline in <c>WorkspaceSetup.BuildEmbeddingOptions</c> and the server bound
/// <c>EmbeddingOptions</c> raw with none of them, so a bad <c>Scrinia:Embeddings:*</c> config 500'd
/// every request through the server's DI factory. These tests pin the corrected values directly,
/// independent of either host's wiring — <c>EmbeddingOptionsServerGuardrailTests</c> in
/// Scrinia.Server.Tests pins that the server actually calls this before constructing its
/// embeddings service.
/// </summary>
public sealed class EmbeddingOptionsGuardrailTests
{
    [Fact]
    public void NonPositiveMaxChunksPerMemory_RevertsToTheDefault()
    {
        var options = new EmbeddingOptions { MaxChunksPerMemory = 0 };

        var messages = options.NormalizeGuardrails();

        options.MaxChunksPerMemory.Should().Be(500,
            because: "a non-positive cap truncates IndexAsync's plan to Take(0) for every write, " +
                "silently wiping a memory's vectors, rather than throwing anywhere");
        messages.Should().ContainSingle().Which.Should().Contain("MaxChunksPerMemory");
    }

    [Fact]
    public void NegativeMaxChunksPerMemory_AlsoRevertsToTheDefault()
    {
        var options = new EmbeddingOptions { MaxChunksPerMemory = -5 };

        options.NormalizeGuardrails();

        options.MaxChunksPerMemory.Should().Be(500);
    }

    [Fact]
    public void MaxChunksPerMemoryAtOrAboveTheStride_ClampsBelowIt()
    {
        var options = new EmbeddingOptions { MaxChunksPerMemory = 1000 };

        var messages = options.NormalizeGuardrails();

        options.MaxChunksPerMemory.Should().Be(EmbeddingOptions.MaxChunksPerMemoryCeiling,
            because: "MemoryVectorIndexer's constructor throws ArgumentOutOfRangeException when " +
                "MaxChunksPerMemory >= ChunkIndexStride (1000) — thrown from a DI factory on the " +
                "server, this 500s every request");
        messages.Should().ContainSingle().Which.Should().Contain("MaxChunksPerMemory");
    }

    [Fact]
    public void ChunkOverlapAtOrAboveChunkSize_RevertsBothToDefaults()
    {
        var options = new EmbeddingOptions { ChunkSize = 100, ChunkOverlap = 100 };

        var messages = options.NormalizeGuardrails();

        options.ChunkOverlap.Should().BeLessThan(options.ChunkSize,
            because: "TextChunker.SliceWindows throws when overlap is not strictly less than the window size");
        messages.Should().ContainSingle().Which.Should().Contain("ChunkOverlap");
    }

    [Fact]
    public void InRangeOptions_AreLeftUntouched_AndReportNothing()
    {
        var options = new EmbeddingOptions { MaxChunksPerMemory = 250, ChunkSize = 1200, ChunkOverlap = 200 };

        var messages = options.NormalizeGuardrails();

        messages.Should().BeEmpty();
        options.MaxChunksPerMemory.Should().Be(250);
        options.ChunkSize.Should().Be(1200);
        options.ChunkOverlap.Should().Be(200);
    }

    [Fact]
    public void AllThreeGuardrailsCanFireTogether_OneMessageEach()
    {
        var options = new EmbeddingOptions { MaxChunksPerMemory = 0, ChunkSize = 100, ChunkOverlap = 100 };

        var messages = options.NormalizeGuardrails();

        // MaxChunksPerMemory reverts to 500 (the non-positive guard), which is already below the
        // stride ceiling, so the third guardrail (ClampChunkCapBelowStride) does not also fire —
        // two corrections, not three, for this particular combination.
        messages.Should().HaveCount(2);
        options.MaxChunksPerMemory.Should().Be(500);
        options.ChunkOverlap.Should().BeLessThan(options.ChunkSize);
    }
}
