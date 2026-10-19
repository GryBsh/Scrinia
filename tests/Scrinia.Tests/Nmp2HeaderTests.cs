using Scrinia.Core.Encoding;

namespace Scrinia.Tests;

public sealed class Nmp2HeaderTests
{
    public static TheoryData<string> InvalidHeaders => new()
    {
        "", "NMP/2", "NMP/2 3B", "NMP/2 3B CRC32:352441C2",
        "NMP/1 3B CRC32:352441C2 BR+B64",
        "nmp/2 3B CRC32:352441C2 BR+B64",
        "\uFEFFNMP/2 3B CRC32:352441C2 BR+B64",
        "NMP/2 +3B CRC32:352441C2 BR+B64",
        "NMP/2 -3B CRC32:352441C2 BR+B64",
        "NMP/2 B CRC32:352441C2 BR+B64",
        "NMP/2 3b CRC32:352441C2 BR+B64",
        "NMP/2 3.0B CRC32:352441C2 BR+B64",
        "NMP/2 2147483648B CRC32:352441C2 BR+B64",
        "NMP/2 \u0663B CRC32:352441C2 BR+B64",
        "NMP/2 3B BR+B64",
        "NMP/2 3B crc32:352441C2 BR+B64",
        "NMP/2 3B CRC32: BR+B64",
        "NMP/2 3B CRC32:352441C BR+B64",
        "NMP/2 3B CRC32:0352441C2 BR+B64",
        "NMP/2 3B CRC32:NOTHEX!! BR+B64",
        "NMP/2 3B CRC32:+352441C BR+B64",
        "NMP/2 3B CRC32:\uFF13\uFF15\uFF12\uFF14\uFF14\uFF11C2 BR+B64",
        "NMP/2 3B CRC32:352441C2 GZ+B64",
        "NMP/2 3B CRC32:352441C2 br+b64",
        "NMP/2 CRC32:352441C2 3B BR+B64",
        "NMP/2 3B BR+B64 CRC32:352441C2",
        "NMP/2 3B CRC32:352441C2 C:2 BR+B64",
        "NMP/2 3B CRC32:352441C2 BR+B64 extra",
        "NMP/2 3B CRC32:352441C2 BR+B64 C:2 C:2",
        "NMP/2 3B CRC32:352441C2 BR+B64 C:0",
        "NMP/2 3B CRC32:352441C2 BR+B64 C:-1",
        "NMP/2 3B CRC32:352441C2 BR+B64 C:+1",
        "NMP/2 3B CRC32:352441C2 BR+B64 C:100001",
        "NMP/2 3B CRC32:352441C2 BR+B64 C:2147483648",
        "NMP/2 3B CRC32:352441C2 BR+B64 C:",
        "NMP/2 3B CRC32:352441C2 BR+B64 c:2",
        "NMP/2\t3B CRC32:352441C2 BR+B64",
        "NMP/2 3B\tCRC32:352441C2 BR+B64",
        "NMP/2 3B CRC32:352441C2\tBR+B64",
        "NMP/2 3B CRC32:352441C2 BR+B64\tC:2",
        "NMP/2\u00A03B CRC32:352441C2 BR+B64",
        "NMP/2 3B CRC32:352441C2 BR+B64\r\r"
    };

    [Theory]
    [MemberData(nameof(InvalidHeaders))]
    public void MalformedHeadersAreRejectedBeforePayloadDecoding(string header)
    {
        string artifact = Nmp2ChunkedEncoder.Encode("abc");
        string changed = header + artifact[artifact.IndexOf('\n')..];
        Assert.Throws<InvalidDataException>(() => Nmp2Strategy.Instance.Decode(changed));
    }

    [Theory]
    [MemberData(nameof(InvalidHeaders))]
    public void MetadataOnlyCallersRejectMalformedHeaders(string header)
    {
        Assert.Throws<InvalidDataException>(() => Nmp2Strategy.Instance.ParseHeader(header));
        Assert.Throws<InvalidDataException>(() => Nmp2Strategy.IsMultiChunk(header));
        Assert.Throws<InvalidDataException>(() => Nmp2Strategy.ParseChunkCount(header));
        Assert.Throws<InvalidDataException>(() => Nmp2ChunkedEncoder.GetChunkCount(header));
    }

    [Theory]
    [InlineData("NMP/2 0B CRC32:00000000 BR+B64")]
    [InlineData("NMP/2 3B CRC32:NOTHEX!! BR+B64")]
    [InlineData("NMP/2 +3B CRC32:00000000 BR+B64")]
    [InlineData("NMP/2 3B BR+B64")]
    public void InvalidMetadataNeverDisablesVerification(string header)
    {
        string artifact = Nmp2ChunkedEncoder.Encode("abc");
        string changed = header + artifact[artifact.IndexOf('\n')..];
        Assert.Throws<InvalidDataException>(() => Nmp2Strategy.Instance.Decode(changed));
    }

    [Fact]
    public void DeclaredZeroCannotBypassLengthVerification()
    {
        string artifact = Nmp2ChunkedEncoder.Encode("abc");
        string changed = artifact.Replace("3B", "0B", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => Nmp2Strategy.Instance.Decode(changed));
    }

    [Fact]
    public void ValidEmptyArtifactStillDecodes()
    {
        string artifact = Nmp2ChunkedEncoder.Encode("");
        Assert.StartsWith("NMP/2 0B CRC32:00000000 BR+B64\n", artifact);
        Assert.Empty(Nmp2Strategy.Instance.Decode(artifact));
    }

    [Theory]
    [InlineData("NMP/2 0B CRC32:00000000 BR+B64", 0, 0u, 1, false)]
    [InlineData("NMP/2 2147483647B CRC32:FFFFFFFF BR+B64 C:100000", int.MaxValue, uint.MaxValue, 100000, true)]
    [InlineData("NMP/2 3B CRC32:352441c2 BR+B64 C:1", 3, 0x352441C2u, 1, true)]
    [InlineData("  NMP/2   003B  CRC32:352441C2   BR+B64 C:00002  ", 3, 0x352441C2u, 2, true)]
    [InlineData("NMP/2 3B CRC32:352441C2 BR+B64\nignored header tokens", 3, 0x352441C2u, 1, false)]
    [InlineData("NMP/2 3B CRC32:352441C2 BR+B64\r\nignored header tokens", 3, 0x352441C2u, 1, false)]
    public void ValidGrammarParsesOnlyFirstLogicalLine(string artifact, int bytes, uint crc, int count, bool hasMarker)
    {
        Nmp2Header header = Nmp2Header.Parse(artifact);
        Assert.Equal(bytes, header.OriginalBytes);
        Assert.Equal(crc, header.Crc32);
        Assert.Equal(count, header.ChunkCount);
        Assert.Equal(hasMarker, header.HasChunkMarker);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void LowercaseHexAndMultipleSpacesStillDecode(string newline)
    {
        string artifact = Nmp2ChunkedEncoder.Encode("abc");
        string changed = "NMP/2   3B  CRC32:352441c2   BR+B64" + artifact[artifact.IndexOf('\n')..];
        changed = changed.Replace("\n", newline, StringComparison.Ordinal);
        Assert.Equal("abc", System.Text.Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(changed)));
    }

    [Theory]
    [InlineData("  NMP/2 3B CRC32:352441C2 BR+B64", "\n")]
    [InlineData("  NMP/2 3B CRC32:352441C2 BR+B64", "\r\n")]
    [InlineData("NMP/2   3B  CRC32:352441C2   BR+B64", "\n")]
    [InlineData("NMP/2   3B  CRC32:352441C2   BR+B64", "\r\n")]
    [InlineData("NMP/2 3B CRC32:352441C2 BR+B64  ", "\n")]
    [InlineData("NMP/2 3B CRC32:352441C2 BR+B64  ", "\r\n")]
    public void AcceptedAsciiSpaceHeadersRoundTripThroughFullDecode(string header, string newline)
    {
        string artifact = Nmp2ChunkedEncoder.Encode("abc");
        string changed = (header + artifact[artifact.IndexOf('\n')..]).Replace("\n", newline, StringComparison.Ordinal);
        Assert.Equal("abc", System.Text.Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(changed)));
    }

    [Fact]
    public void EmptyArtifactStillChecksItsCrc()
    {
        string artifact = Nmp2ChunkedEncoder.Encode("").Replace("CRC32:00000000", "CRC32:00000001", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => Nmp2Strategy.Instance.Decode(artifact));
    }

    [Fact]
    public void TrailingCrWithoutLfIsNotRemoved()
    {
        Assert.Throws<InvalidDataException>(() => Nmp2Header.Parse("NMP/2 3B CRC32:352441C2 BR+B64\r"));
    }
}
