using System.Text;
using Scrinia.Core.Encoding;
using FluentAssertions;
using Xunit;

namespace Scrinia.Tests;

public sealed class Nmp2IntegrityTests
{
    [Theory]
    [InlineData("missing-pad")]
    [InlineData("duplicate-pad")]
    [InlineData("missing-footer")]
    [InlineData("trailing-content")]
    [InlineData("embedded-marker")]
    public void SingleArtifactFullReadRejectsMalformedStructure(string mutation)
    {
        string artifact = Encode("plain artifact");
        string malformed = mutation switch
        {
            "missing-pad" => artifact.Replace("##PAD:0\n", "", StringComparison.Ordinal),
            "duplicate-pad" => artifact.Replace("##PAD:0", "##PAD:0\n##PAD:0", StringComparison.Ordinal),
            "missing-footer" => artifact.Replace("NMP/END", "", StringComparison.Ordinal),
            "trailing-content" => artifact + "\ntrailing",
            "embedded-marker" => artifact.Replace("##PAD:0", "payload##PAD:0", StringComparison.Ordinal),
            _ => throw new ArgumentException("Unknown mutation.", nameof(mutation)),
        };
        Assert.Throws<InvalidDataException>(() => Nmp2Strategy.Instance.Decode(malformed));
        Assert.Throws<InvalidDataException>(() => Nmp2ChunkedEncoder.DecodeAllChunksVerified(malformed));
        Assert.Throws<InvalidDataException>(() => Nmp2ChunkedEncoder.AppendChunk(malformed, "new"));
    }

    private static string Encode(string text) =>
        Nmp2Strategy.Instance.Encode(Encoding.UTF8.GetBytes(text), new EncodingOptions()).Artifact;

    [Fact]
    public void RoundTrip_Succeeds()
    {
        string art = Encode("hello world, this is a compressible payload payload payload");
        Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(art)).Should().StartWith("hello world");
    }

    [Fact]
    public void TamperedCrc_Throws()
    {
        string art = Encode("integrity matters");
        // Corrupt the CRC hex in the header ("CRC32:XXXXXXXX").
        int i = art.IndexOf("CRC32:", StringComparison.Ordinal);
        string tampered = art[..(i + 6)] + "DEADBEEF" + art[(i + 14)..];
        Action act = () => Nmp2Strategy.Instance.Decode(tampered);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void TamperedLength_Throws()
    {
        string art = Encode("length matters too");
        // Replace the declared "{N}B" with a wrong, larger N.
        int b = art.IndexOf('B');           // end of the {N}B token on the header line
        int sp = art.IndexOf(' ') + 1;      // start of {N}
        string tampered = art[..sp] + "999999" + art[b..];
        Action act = () => Nmp2Strategy.Instance.Decode(tampered);
        act.Should().Throw<InvalidDataException>();
    }
}
