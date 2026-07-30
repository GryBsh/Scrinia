using System.Text;
using Scrinia.Core.Encoding;
using FluentAssertions;
using Xunit;

namespace Scrinia.Tests;

public sealed class Nmp2IntegrityTests
{
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
