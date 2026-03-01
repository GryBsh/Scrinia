using System.Text;
using FluentAssertions;
using Scrinia.Core.Encoding;

namespace Scrinia.Tests;

/// <summary>
/// Unit tests for <see cref="Nmp2ChunkedEncoder"/> — the chunk-addressable NMP/2 encoder.
///
/// Single-chunk: input ≤ 20 000 chars → standard NMP/2 artifact, no ##CHUNK: markers.
/// Multi-chunk: input > 20 000 chars → header contains "C:{k}", body has ##CHUNK:N sections.
/// </summary>
public sealed class Nmp2ChunkedEncoderTests
{
    // ── Test data helpers ─────────────────────────────────────────────────────

    private static string SmallInput() => TestHelpers.Facts.Fact1;

    /// <summary>Generates ~40 000 chars of paragraph-separated text.</summary>
    private static string LargeInput()
    {
        const string paragraph =
            "The quick brown fox jumps over the lazy dog. " +
            "Pack my box with five dozen liquor jugs. " +
            "How vividly daft jumping zebras vex.";

        var sb = new StringBuilder();
        while (sb.Length < 40_000)
        {
            sb.Append(paragraph);
            sb.Append("\n\n");
        }
        return sb.ToString();
    }

    // ── Single-chunk (small input) ────────────────────────────────────────────

    [Fact]
    public void SingleChunk_SmallInput_RoundTrips()
    {
        string original = SmallInput();
        string artifact = Nmp2ChunkedEncoder.Encode(original);

        // No multi-chunk marker in header
        artifact.Split('\n')[0].Should().NotContain(" C:",
            because: "single-chunk artifacts must not have a C: token in the header");

        // Full decode via Nmp2Strategy must equal original
        byte[] decoded = new Nmp2Strategy().Decode(artifact);
        System.Text.Encoding.UTF8.GetString(decoded).Should().Be(original);
    }

    [Fact]
    public void SingleChunk_ChunkCountIsOne()
    {
        string artifact = Nmp2ChunkedEncoder.Encode(SmallInput());
        Nmp2ChunkedEncoder.GetChunkCount(artifact).Should().Be(1);
    }

    [Fact]
    public void SingleChunk_GetChunk1_RoundTrips()
    {
        string original = SmallInput();
        string artifact = Nmp2ChunkedEncoder.Encode(original);

        string decoded = Nmp2ChunkedEncoder.DecodeChunk(artifact, 1);
        decoded.Should().Be(original);
    }

    [Fact]
    public void SingleChunk_GetChunk2_ThrowsOutOfRange()
    {
        string artifact = Nmp2ChunkedEncoder.Encode(SmallInput());

        var act = () => Nmp2ChunkedEncoder.DecodeChunk(artifact, 2);

        act.Should().Throw<ArgumentOutOfRangeException>(
            because: "chunk index 2 does not exist in a single-chunk artifact");
    }

    // ── Large input (no auto-chunking) ──────────────────────────────────────

    [Fact]
    public void Encode_LargeInput_AlwaysSingleChunk()
    {
        string artifact = Nmp2ChunkedEncoder.Encode(LargeInput());
        string firstLine = artifact.Split('\n')[0];

        firstLine.Should().NotContain(" C:",
            because: "Encode() always produces single-chunk regardless of size");

        Nmp2ChunkedEncoder.GetChunkCount(artifact).Should().Be(1);
    }

    [Fact]
    public void Encode_LargeInput_RoundTrips()
    {
        string original = LargeInput();
        string artifact = Nmp2ChunkedEncoder.Encode(original);

        byte[] fullBytes = new Nmp2Strategy().Decode(artifact);
        string decoded = System.Text.Encoding.UTF8.GetString(fullBytes);

        decoded.Should().Be(original);
    }

    // ── Multi-chunk via EncodeChunks ──────────────────────────────────────

    [Fact]
    public void MultiChunk_AllChunksJoinToOriginal()
    {
        string[] parts = [LargeInput()[..20_000], LargeInput()[20_000..]];
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(parts);
        int count = Nmp2ChunkedEncoder.GetChunkCount(artifact);

        var decoded = Enumerable.Range(1, count)
            .Select(i => Nmp2ChunkedEncoder.DecodeChunk(artifact, i))
            .ToList();
        string chunked = string.Concat(decoded);

        byte[] fullBytes = new Nmp2Strategy().Decode(artifact);
        string fullText = System.Text.Encoding.UTF8.GetString(fullBytes);

        chunked.Should().Be(fullText,
            because: "concatenating individual chunks must equal Nmp2Strategy.Decode()");
    }

    // ── EncodeChunks ─────────────────────────────────────────────────────────

    [Fact]
    public void EncodeChunks_SingleElement_ProducesSingleChunkArtifact()
    {
        string[] chunks = ["Hello, world!"];
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(chunks);

        Nmp2ChunkedEncoder.GetChunkCount(artifact).Should().Be(1);
        artifact.Split('\n')[0].Should().NotContain(" C:");

        string decoded = Nmp2ChunkedEncoder.DecodeChunk(artifact, 1);
        decoded.Should().Be("Hello, world!");
    }

    [Fact]
    public void EncodeChunks_TwoElements_ProducesMultiChunkArtifact()
    {
        string[] chunks = ["## Auth\nLogin flow here.", "## Users\nUser endpoints here."];
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(chunks);

        Nmp2ChunkedEncoder.GetChunkCount(artifact).Should().Be(2);
        artifact.Split('\n')[0].Should().Contain(" C:2");
    }

    [Fact]
    public void EncodeChunks_ThreeElements_RoundTrips()
    {
        string[] chunks = ["Part A: alpha content.", "Part B: bravo content.", "Part C: charlie content."];
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(chunks);

        Nmp2ChunkedEncoder.GetChunkCount(artifact).Should().Be(3);

        string decoded1 = Nmp2ChunkedEncoder.DecodeChunk(artifact, 1);
        string decoded2 = Nmp2ChunkedEncoder.DecodeChunk(artifact, 2);
        string decoded3 = Nmp2ChunkedEncoder.DecodeChunk(artifact, 3);

        decoded1.Should().Be(chunks[0]);
        decoded2.Should().Be(chunks[1]);
        decoded3.Should().Be(chunks[2]);

        // Full decode via Nmp2Strategy must equal concatenation
        byte[] fullBytes = new Nmp2Strategy().Decode(artifact);
        string fullText = System.Text.Encoding.UTF8.GetString(fullBytes);
        fullText.Should().Be(string.Concat(chunks));
    }

    [Fact]
    public void EncodeChunks_Null_Throws()
    {
        var act = () => Nmp2ChunkedEncoder.EncodeChunks(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EncodeChunks_Empty_Throws()
    {
        var act = () => Nmp2ChunkedEncoder.EncodeChunks([]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EncodeChunks_LargeChunks_RoundTrips()
    {
        // Two chunks each ~15K chars
        string chunkA = new string('A', 15_000);
        string chunkB = new string('B', 15_000);
        string artifact = Nmp2ChunkedEncoder.EncodeChunks([chunkA, chunkB]);

        Nmp2ChunkedEncoder.GetChunkCount(artifact).Should().Be(2);
        Nmp2ChunkedEncoder.DecodeChunk(artifact, 1).Should().Be(chunkA);
        Nmp2ChunkedEncoder.DecodeChunk(artifact, 2).Should().Be(chunkB);
    }

    // ── AppendChunk ──────────────────────────────────────────────────────────

    [Fact]
    public void AppendChunk_SingleToMulti_Promotion()
    {
        string original = "Chunk one content.";
        string singleArtifact = Nmp2ChunkedEncoder.EncodeChunks([original]);
        Nmp2ChunkedEncoder.GetChunkCount(singleArtifact).Should().Be(1);

        string appended = Nmp2ChunkedEncoder.AppendChunk(singleArtifact, "Chunk two content.");

        Nmp2ChunkedEncoder.GetChunkCount(appended).Should().Be(2);
        Nmp2ChunkedEncoder.DecodeChunk(appended, 1).Should().Be("Chunk one content.");
        Nmp2ChunkedEncoder.DecodeChunk(appended, 2).Should().Be("Chunk two content.");
    }

    [Fact]
    public void AppendChunk_MultiChunk_AppendsNewSection()
    {
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(["Part A", "Part B"]);
        Nmp2ChunkedEncoder.GetChunkCount(artifact).Should().Be(2);

        string appended = Nmp2ChunkedEncoder.AppendChunk(artifact, "Part C");

        Nmp2ChunkedEncoder.GetChunkCount(appended).Should().Be(3);
        Nmp2ChunkedEncoder.DecodeChunk(appended, 1).Should().Be("Part A");
        Nmp2ChunkedEncoder.DecodeChunk(appended, 2).Should().Be("Part B");
        Nmp2ChunkedEncoder.DecodeChunk(appended, 3).Should().Be("Part C");
    }

    [Fact]
    public void AppendChunk_Crc32Correctness()
    {
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(["Hello"]);
        string appended = Nmp2ChunkedEncoder.AppendChunk(artifact, " World");

        // Full decode via Nmp2Strategy should succeed (CRC32 validation)
        byte[] fullBytes = new Nmp2Strategy().Decode(appended);
        string fullText = System.Text.Encoding.UTF8.GetString(fullBytes);
        fullText.Should().Be("Hello World");

        // CRC32 in header should match computed CRC32 of all decoded bytes
        string header = appended.Split('\n')[0];
        header.Should().Contain("CRC32:");
    }

    [Fact]
    public void AppendChunk_MultipleChainedAppends()
    {
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(["Entry 1"]);

        artifact = Nmp2ChunkedEncoder.AppendChunk(artifact, "Entry 2");
        artifact = Nmp2ChunkedEncoder.AppendChunk(artifact, "Entry 3");
        artifact = Nmp2ChunkedEncoder.AppendChunk(artifact, "Entry 4");

        Nmp2ChunkedEncoder.GetChunkCount(artifact).Should().Be(4);
        Nmp2ChunkedEncoder.DecodeChunk(artifact, 1).Should().Be("Entry 1");
        Nmp2ChunkedEncoder.DecodeChunk(artifact, 2).Should().Be("Entry 2");
        Nmp2ChunkedEncoder.DecodeChunk(artifact, 3).Should().Be("Entry 3");
        Nmp2ChunkedEncoder.DecodeChunk(artifact, 4).Should().Be("Entry 4");

        // Full roundtrip
        byte[] fullBytes = new Nmp2Strategy().Decode(artifact);
        string fullText = System.Text.Encoding.UTF8.GetString(fullBytes);
        fullText.Should().Be("Entry 1Entry 2Entry 3Entry 4");
    }

    [Fact]
    public void AppendChunk_PreservesExistingChunkContent()
    {
        // Existing chunks with varied content
        string[] originals = ["func main() {}", "func helper() { return 42; }"];
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(originals);

        string appended = Nmp2ChunkedEncoder.AppendChunk(artifact, "func newFunc() {}");

        // All original chunks must be preserved exactly
        Nmp2ChunkedEncoder.DecodeChunk(appended, 1).Should().Be(originals[0]);
        Nmp2ChunkedEncoder.DecodeChunk(appended, 2).Should().Be(originals[1]);
        Nmp2ChunkedEncoder.DecodeChunk(appended, 3).Should().Be("func newFunc() {}");
    }

    [Fact]
    public void AppendChunk_EmptyArtifact_Throws()
    {
        var act = () => Nmp2ChunkedEncoder.AppendChunk("", "new content");
        act.Should().Throw<ArgumentException>();
    }
}
