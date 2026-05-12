using FluentAssertions;
using Scrinia.Core.Encoding;

namespace Scrinia.Tests;

/// <summary>
/// Unit tests for Nmp2Strategy — the nmp/2 encoder/decoder.
/// NMP/2 always Brotli-compresses and encodes as plain URL-safe Base64 lines (no row-index prefix).
/// Covers format structure, roundtrip fidelity, edge cases, header parsing,
/// and the CanDecode sentinel check.
/// </summary>
public sealed class Nmp2StrategyTests
{
    private static readonly Nmp2Strategy Strategy = new();

    private static readonly EncodingOptions DefaultOptions = new();

    // ── CanDecode ─────────────────────────────────────────────────────────────

    [Fact]
    public void CanDecode_ValidArtifact_ReturnsTrue()
    {
        var result = Strategy.Encode(TestHelpers.Utf8("hello"), DefaultOptions);
        Strategy.CanDecode(result.Artifact).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an artifact")]
    [InlineData("NMP/2 without end sentinel")]
    [InlineData("NMP/END without header")]
    [InlineData("NMP/1 12x1 4B CRC32:00000000\n00\u250200000000\n##\u2502PAD:0\nNMP/END")]
    public void CanDecode_InvalidInput_ReturnsFalse(string input)
    {
        Strategy.CanDecode(input).Should().BeFalse();
    }

    // ── Header format ─────────────────────────────────────────────────────────

    [Fact]
    public void Encode_HeaderLine_HasCorrectFormat()
    {
        var result = Strategy.Encode(TestHelpers.Utf8("Hello, NMP!"), DefaultOptions);
        var header = result.Artifact.Split('\n')[0];

        header.Should().StartWith("NMP/2 ");
        header.Should().Contain("B ");     // byte count
        header.Should().Contain("CRC32:"); // checksum
        header.Should().Contain("BR+B64"); // codec tag
    }

    [Fact]
    public void Encode_Header_HasNoWxHDimensions()
    {
        var result = Strategy.Encode(TestHelpers.Utf8("test"), DefaultOptions);
        var header = result.Artifact.Split('\n')[0];

        header.Should().NotMatchRegex(@"\d+x\d+");
    }

    [Fact]
    public void Encode_Header_HasNoCompressionTag()
    {
        var result = Strategy.Encode(TestHelpers.Utf8("abc"), DefaultOptions);
        var header = result.Artifact.Split('\n')[0];

        header.Should().NotContain(" GZ");
        // BR appears only as part of the BR+B64 codec tag, never as a standalone NMP/1-style compression tag
    }

    [Fact]
    public void Encode_Header_EmbedsByteCount()
    {
        var input = TestHelpers.Utf8(TestHelpers.Facts.Fact1);
        var result = Strategy.Encode(input, DefaultOptions);
        var meta = Strategy.ParseHeader(result.Artifact);

        meta.OriginalBytes.Should().Be(input.Length);
    }

    // ── Data line format ──────────────────────────────────────────────────────

    [Fact]
    public void Encode_DataLines_HaveNoRowIndexPrefix()
    {
        // Data lines must not start with digits followed by │ (no NMP/1-style row indices)
        var result = Strategy.Encode(TestHelpers.Utf8("Hello, World!"), DefaultOptions);
        var lines = result.Artifact.Split('\n');

        // All data lines (between header and footer) should be pure Base64
        foreach (var line in lines.Skip(1).TakeWhile(l => !l.StartsWith("##") && l != "NMP/END"))
        {
            line.Should().NotContain("\u2502");
            line.Should().NotMatchRegex(@"^\d{2}\|");
        }
    }

    [Fact]
    public void Encode_DataLines_CharsAreUrlSafeBase64()
    {
        var result = Strategy.Encode(TestHelpers.Utf8("Hello"), DefaultOptions);
        var lines = result.Artifact.Split('\n');

        // First data line is lines[1]
        var chars = lines[1];

        // URL-safe Base64: A-Z, a-z, 0-9, -, _ only (no +, /, =)
        chars.All(c => c is
            (>= 'A' and <= 'Z') or
            (>= 'a' and <= 'z') or
            (>= '0' and <= '9') or
            '-' or '_').Should().BeTrue();
    }

    [Fact]
    public void Encode_DataLines_NoEqualsSignPadding()
    {
        // No '=' padding characters anywhere in the artifact
        var result = Strategy.Encode(TestHelpers.Utf8("test data that needs padding"), DefaultOptions);
        result.Artifact.Should().NotContain("=");
    }

    [Fact]
    public void Encode_CustomCharsPerLine_ProducesCorrectLineLength()
    {
        var options = new EncodingOptions(CharsPerLine: 40);
        // Use data that compresses to multiple lines at width 40
        var input = TestHelpers.LoadFactsBytes(200);
        var result = Strategy.Encode(input, options);
        var lines = result.Artifact.Split('\n');

        // First data line should be at most 40 chars
        lines[1].Length.Should().BeLessOrEqualTo(40);
    }

    // ── Footer format ─────────────────────────────────────────────────────────

    [Fact]
    public void Encode_Footer_EndsWithNmpEnd()
    {
        var result = Strategy.Encode(TestHelpers.Utf8("end test"), DefaultOptions);
        result.Artifact.Should().EndWith("NMP/END");
    }

    [Fact]
    public void Encode_Footer_HasPadLine()
    {
        var result = Strategy.Encode(TestHelpers.Utf8("end test"), DefaultOptions);
        result.Artifact.Should().Contain("##PAD:");
    }

    [Fact]
    public void Encode_Footer_PadLine_HasNoPipe()
    {
        var result = Strategy.Encode(TestHelpers.Utf8("end test"), DefaultOptions);
        var padLine = result.Artifact.Split('\n').First(l => l.StartsWith("##"));
        padLine.Should().NotContain("\u2502");
    }

    [Fact]
    public void Encode_PadCount_IsInValidRange()
    {
        // PAD must be 0, 1, or 2 (3-byte Base64 alignment)
        foreach (int len in new[] { 1, 2, 3, 4, 5, 10, 20, 64 })
        {
            var result = Strategy.Encode(new byte[len], DefaultOptions);
            var padLine = result.Artifact.Split('\n').First(l => l.StartsWith("##PAD:"));
            var padCount = int.Parse(padLine[6..]);
            padCount.Should().BeInRange(0, 2);
        }
    }

    // ── Roundtrip ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("Hello, World!")]
    [InlineData("The quick brown fox jumps over the lazy dog.")]
    public void RoundTrip_ShortStrings(string text)
    {
        var input = TestHelpers.Utf8(text);
        var result = Strategy.Encode(input, DefaultOptions);
        var decoded = Strategy.Decode(result.Artifact);

        decoded.Should().Equal(input);
    }

    [Fact]
    public void RoundTrip_SingleFact_ExactBytes()
    {
        var input = TestHelpers.Utf8(TestHelpers.Facts.Fact13);
        var result = Strategy.Encode(input, DefaultOptions);
        var decoded = Strategy.Decode(result.Artifact);

        decoded.Should().Equal(input);
        TestHelpers.FromUtf8(decoded).Should().Be(TestHelpers.Facts.Fact13);
    }

    [Fact]
    public void RoundTrip_MultiLineFacts_Excerpt()
    {
        var input = TestHelpers.Utf8(TestHelpers.Facts.Excerpt);
        var result = Strategy.Encode(input, DefaultOptions);
        var decoded = Strategy.Decode(result.Artifact);

        decoded.Should().Equal(input);
    }

    [Fact]
    public void RoundTrip_AllZeroBytes()
    {
        var input = new byte[64];
        var result = Strategy.Encode(input, DefaultOptions);
        var decoded = Strategy.Decode(result.Artifact);

        decoded.Should().Equal(input);
    }

    [Fact]
    public void RoundTrip_AllMaxBytes()
    {
        var input = Enumerable.Repeat((byte)0xFF, 64).ToArray();
        var result = Strategy.Encode(input, DefaultOptions);
        var decoded = Strategy.Decode(result.Artifact);

        decoded.Should().Equal(input);
    }

    [Fact]
    public void RoundTrip_BinaryPattern_AllByteValues()
    {
        var input = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var result = Strategy.Encode(input, DefaultOptions);
        var decoded = Strategy.Decode(result.Artifact);

        decoded.Should().Equal(input);
    }

    [Fact]
    public void RoundTrip_LargeInput_1KB()
    {
        var input = TestHelpers.LoadFactsBytes(1024);
        var result = Strategy.Encode(input, DefaultOptions);
        var decoded = Strategy.Decode(result.Artifact);

        decoded.Should().Equal(input);
    }

    [Fact]
    public void RoundTrip_LargeInput_10KB()
    {
        var input = TestHelpers.LoadFactsBytes(10 * 1024);
        var result = Strategy.Encode(input, DefaultOptions);
        var decoded = Strategy.Decode(result.Artifact);

        decoded.Should().Equal(input);
    }

    [Fact]
    public void RoundTrip_FullFactsFile()
    {
        var input = TestHelpers.LoadFactsBytes();
        var result = Strategy.Encode(input, DefaultOptions);
        var decoded = Strategy.Decode(result.Artifact);

        decoded.Should().Equal(input);
    }

    // ── ParseHeader ───────────────────────────────────────────────────────────

    [Fact]
    public void ParseHeader_ExtractsCrc32()
    {
        var input = TestHelpers.Utf8(TestHelpers.Facts.Fact5);
        var result = Strategy.Encode(input, DefaultOptions);
        var meta = Strategy.ParseHeader(result.Artifact);

        meta.Crc32.Should().NotBeNull();
        meta.Crc32.Should().NotBe(0u);
    }

    [Fact]
    public void ParseHeader_StrategyId_IsNmp2()
    {
        var result = Strategy.Encode(TestHelpers.Utf8("x"), DefaultOptions);
        var meta = Strategy.ParseHeader(result.Artifact);

        meta.StrategyId.Should().Be("nmp/2");
    }

    [Fact]
    public void ParseHeader_ExtractsOriginalByteCount()
    {
        var input = TestHelpers.Utf8(TestHelpers.Facts.Fact31);
        var result = Strategy.Encode(input, DefaultOptions);
        var meta = Strategy.ParseHeader(result.Artifact);

        meta.OriginalBytes.Should().Be(input.Length);
    }

    // ── EncodingResult metadata ───────────────────────────────────────────────

    [Fact]
    public void Encode_Result_StrategyId_IsNmp2()
    {
        var result = Strategy.Encode(TestHelpers.Utf8("test"), DefaultOptions);
        result.StrategyId.Should().Be("nmp/2");
    }

    [Fact]
    public void Encode_Result_OriginalBytes_MatchesInput()
    {
        var input = TestHelpers.Utf8(TestHelpers.Facts.Fact31);
        var result = Strategy.Encode(input, DefaultOptions);
        result.OriginalBytes.Should().Be(input.Length);
    }

    [Fact]
    public void Encode_Result_ArtifactChars_MatchesArtifactLength()
    {
        var result = Strategy.Encode(TestHelpers.Utf8("hello world"), DefaultOptions);
        result.ArtifactChars.Should().Be(result.Artifact.Length);
    }

    [Fact]
    public void Encode_EmptyInput_ProducesValidArtifact()
    {
        var result = Strategy.Encode([], DefaultOptions);
        result.Artifact.Should().StartWith("NMP/2 ");
        result.Artifact.Should().EndWith("NMP/END");
        Strategy.CanDecode(result.Artifact).Should().BeTrue();
    }

    [Fact]
    public void Encode_EmptyInput_RoundTrips()
    {
        var result = Strategy.Encode([], DefaultOptions);
        var decoded = Strategy.Decode(result.Artifact);
        decoded.Should().BeEmpty();
    }

    // ── MaxDecodedBytes / decode-size DoS guard ──────────────────────────────
    //
    // These tests use the internal *Bounded helpers + per-call maxBytes overload so
    // they are parallel-safe (no global static mutation). Coverage of the public
    // Strategy.Decode static-cap path lives in Nmp2DecodeCapTests (serial collection).

    [Fact]
    public void BrotliDecompressBounded_OutputExceedsCap_Throws()
    {
        byte[] compressed = Nmp2Strategy.BrotliCompress(TestHelpers.Utf8(new string('B', 16 * 1024)));

        FluentActions.Invoking(() => Nmp2Strategy.BrotliDecompressBounded(compressed, maxBytes: 1024))
            .Should().Throw<InvalidDataException>(
                because: "BrotliDecompressBounded must refuse to expand past maxBytes")
            .WithMessage("*MaxDecodedBytes*");
    }

    [Fact]
    public void BrotliDecompressBounded_OutputWithinCap_RoundTrips()
    {
        string original = new('D', 4096);
        byte[] compressed = Nmp2Strategy.BrotliCompress(TestHelpers.Utf8(original));

        byte[] decoded = Nmp2Strategy.BrotliDecompressBounded(compressed, maxBytes: 8 * 1024);
        System.Text.Encoding.UTF8.GetString(decoded).Should().Be(original);
    }

    [Fact]
    public void DecodeChunkSection_PerChunkCapExceeded_Throws()
    {
        string payload = new('A', 4096);
        string artifact = Nmp2ChunkedEncoder.EncodeChunks([payload, payload]);

        FluentActions.Invoking(() =>
                Nmp2Strategy.DecodeChunkSection(artifact, chunkIndex: 1, maxBytes: 1024))
            .Should().Throw<InvalidDataException>(
                because: "per-chunk decompression must respect the maxBytes argument")
            .WithMessage("*MaxDecodedBytes*");
    }

    [Fact]
    public void Decode_MultiChunk_DeclaredCountExceedsMaxChunkCount_Throws()
    {
        // Forge a header that declares an absurd chunk count. The first line is the only
        // thing IsMultiChunk / ParseChunkCount inspect; the count check fires before any
        // chunk content would be required.
        string forged = $"NMP/2 1B CRC32:00000000 BR+B64 C:{Nmp2Strategy.MaxChunkCount + 1}\nNMP/END";

        FluentActions.Invoking(() => Strategy.Decode(forged))
            .Should().Throw<InvalidDataException>(
                because: "an artifact declaring more than MaxChunkCount chunks must be rejected")
            .WithMessage("*chunks*");
    }

    [Fact]
    public void Decode_MultiChunk_WithinCap_StillRoundTrips()
    {
        // Regression guard: the cap must not affect legitimate decodes.
        string payload = new('C', 2048);
        string artifact = Nmp2ChunkedEncoder.EncodeChunks([payload, payload]);

        byte[] decoded = Strategy.Decode(artifact);
        System.Text.Encoding.UTF8.GetString(decoded).Should().Be(payload + payload);
    }

    // ── Encode allocation budget — ArrayPool + Base64Url path ─────────────────

    [Fact]
    public void Encode_OneKbPayload_StaysUnderAllocationBudget()
    {
        // Warm caches so the first-run JIT/static allocations don't pollute the measurement.
        byte[] warmup = TestHelpers.Utf8(new string('w', 1024));
        _ = Strategy.Encode(warmup, DefaultOptions);

        byte[] input = TestHelpers.Utf8(new string('x', 1024));

        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = Strategy.Encode(input, DefaultOptions);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        // Round-trip correctness first.
        byte[] decoded = Strategy.Decode(result.Artifact);
        decoded.Should().Equal(input);

        // The pre-refactor encoder was observed allocating ~6 KB of intermediate trash
        // (compressed array + padded copy + 3 string-Replace results + StringBuilder).
        // The pooled-buffer + Base64Url path keeps it under ~3 KB.
        delta.Should().BeLessThan(3_500,
            because: $"Encode of 1 KB should stay under 3.5 KB of allocations; actual: {delta} bytes");
    }

}

/// <summary>
/// End-to-end coverage of <see cref="Nmp2Strategy.MaxDecodedBytes"/> via the public
/// <c>Decode</c> path. Mutates a process-wide static, so the tests live in a serial
/// collection shared with any other suite that decodes real payloads.
/// </summary>
[Collection(Nmp2DecodeCapCollection.Name)]
public sealed class Nmp2DecodeCapTests
{
    private static readonly Nmp2Strategy Strategy = new();
    private static readonly EncodingOptions DefaultOptions = new();

    [Fact]
    public void Decode_MultiChunk_CumulativeSizeExceedsCap_Throws()
    {
        string payload = new('A', 4096);
        string artifact = Nmp2ChunkedEncoder.EncodeChunks([payload, payload, payload, payload]);

        int originalCap = Nmp2Strategy.MaxDecodedBytes;
        try
        {
            Nmp2Strategy.MaxDecodedBytes = 8 * 1024;

            FluentActions.Invoking(() => Strategy.Decode(artifact))
                .Should().Throw<InvalidDataException>()
                .WithMessage("*MaxDecodedBytes*");
        }
        finally
        {
            Nmp2Strategy.MaxDecodedBytes = originalCap;
        }
    }

    [Fact]
    public void Decode_SingleChunk_BrotliOutputExceedsCap_Throws()
    {
        var result = Strategy.Encode(TestHelpers.Utf8(new string('B', 16 * 1024)), DefaultOptions);

        int originalCap = Nmp2Strategy.MaxDecodedBytes;
        try
        {
            Nmp2Strategy.MaxDecodedBytes = 1024;

            FluentActions.Invoking(() => Strategy.Decode(result.Artifact))
                .Should().Throw<InvalidDataException>()
                .WithMessage("*MaxDecodedBytes*");
        }
        finally
        {
            Nmp2Strategy.MaxDecodedBytes = originalCap;
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Nmp2DecodeCapCollection
{
    public const string Name = "Nmp2DecodeCap";
}
