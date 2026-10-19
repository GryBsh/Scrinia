using System.Text;
using Scrinia.Core.Encoding;
using Xunit;
using Xunit.Abstractions;

namespace Scrinia.Tests;

public sealed class Nmp2BodyScannerTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(2)]
    [InlineData(1000)]
    public void CrLfScanHasBoundedWorkAndPreservesSlices(int count)
    {
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(Enumerable.Repeat("x", count).ToArray())
            .Replace("\n", "\r\n", StringComparison.Ordinal);
        Nmp2Scan scan = Nmp2BodyScanner.Scan(artifact, Nmp2Header.Parse(artifact));
        output.WriteLine($"chunks={count}; artifactCharacters={artifact.Length}; visitedCharacters={scan.VisitedCharacters}");
        Assert.Equal(count, scan.Sections.Count);
        Assert.InRange(scan.VisitedCharacters, 1, 3L * artifact.Length);
        foreach (Nmp2Section section in scan.Sections)
            Assert.EndsWith("\r\n", artifact.Substring(section.Start, section.Length));
        Assert.Equal(new string('x', count), Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(artifact)));
    }

    public static IEnumerable<object[]> MalformedBodies()
    {
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(["first", "second"]);
        yield return [artifact.Replace("##CHUNK:1", "##CHUNK:2", StringComparison.Ordinal)];
        yield return [artifact.Replace("##CHUNK:2\n", "", StringComparison.Ordinal)];
        yield return [artifact.Replace("##CHUNK:2", "##CHUNK:1", StringComparison.Ordinal)];
        yield return [artifact.Replace("C:2", "C:1", StringComparison.Ordinal)];
        yield return [artifact.Replace("C:2", "C:3", StringComparison.Ordinal)];
        yield return [artifact.Replace("##PAD:0", "##PAD:-1", StringComparison.Ordinal)];
        yield return [artifact.Replace("##PAD:0", "##PAD:3", StringComparison.Ordinal)];
        yield return [artifact.Replace("##PAD:0\n", "", StringComparison.Ordinal)];
        yield return [artifact.Replace("##PAD:0", "##PAD:0\n##PAD:0", StringComparison.Ordinal)];
        yield return [artifact + "\ncontent"];
        yield return [artifact.Replace("NMP/END", "", StringComparison.Ordinal)];
        yield return [artifact.Replace("##CHUNK:2", "literal##CHUNK:2", StringComparison.Ordinal)];
        yield return [artifact.Replace("##PAD:0", "literal##PAD:0", StringComparison.Ordinal)];
        yield return [artifact.Replace("##PAD:0", "##PA\rD:0", StringComparison.Ordinal)];
        yield return [artifact.Replace("NMP/END", "NMP/ENDjunk", StringComparison.Ordinal)];
    }

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public void InvalidStructureIsRejected(string artifact)
    {
        Assert.Throws<InvalidDataException>(() => Nmp2BodyScanner.Scan(artifact, Nmp2Header.Parse(artifact)));
        Assert.Throws<InvalidDataException>(() => Nmp2Strategy.Instance.Decode(artifact));
        Assert.Throws<InvalidDataException>(() => Nmp2ChunkedEncoder.AppendChunk(artifact, "new"));
    }

    [Fact]
    public void MixedNewlinesPreserveExactSectionsAndAllowTrailingWhitespace()
    {
        string original = Nmp2ChunkedEncoder.EncodeChunks(["first", "second"]);
        string[] lines = original.Split('\n');
        string artifact = string.Concat(lines.Select((line, i) => line + (i == lines.Length - 1 ? "" : i % 2 == 0 ? "\r\n" : "\n")));
        Nmp2Scan scan = Nmp2BodyScanner.Scan(artifact + "\n \t\r\n", Nmp2Header.Parse(artifact));
        List<string> raw = Nmp2Strategy.ExtractRawChunkSections(artifact, 2);
        Assert.Equal(lines[1] + "\n" + lines[2] + "\r\n" + lines[3] + "\n", raw[0]);
        Assert.Equal(lines[4] + "\r\n" + lines[5] + "\n" + lines[6] + "\r\n", raw[1]);
        Assert.Equal(raw, scan.Sections.Select(s => artifact.Substring(s.Start, s.Length)));
        Assert.Equal("firstsecond", Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(artifact)));
        string appended = Nmp2ChunkedEncoder.AppendChunk(artifact, "third");
        Assert.Equal(raw, Nmp2Strategy.ExtractRawChunkSections(appended, 3).Take(2));
    }

    [Theory]
    [InlineData("")]
    [InlineData("single")]
    public void SingleChunkSectionStartsAfterHeaderAndEndsAfterPad(string text)
    {
        string artifact = Nmp2ChunkedEncoder.Encode(text);
        Nmp2Scan scan = Nmp2BodyScanner.Scan(artifact, Nmp2Header.Parse(artifact));
        Nmp2Section section = Assert.Single(scan.Sections);
        Assert.Equal(artifact.IndexOf('\n') + 1, section.Start);
        Assert.Equal(artifact[section.Start..artifact.IndexOf("NMP/END", StringComparison.Ordinal)], artifact.Substring(section.Start, section.Length));
        Assert.Equal(text, Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(artifact)));
    }

    [Fact]
    public void SingleChunkPromotionPreservesCrLfRawPayloadAndPad()
    {
        string artifact = Nmp2ChunkedEncoder.Encode("first").Replace("\n", "\r\n", StringComparison.Ordinal);
        string body = artifact[(artifact.IndexOf('\n') + 1)..artifact.IndexOf("NMP/END", StringComparison.Ordinal)];
        string appended = Nmp2ChunkedEncoder.AppendChunk(artifact, "second");
        Assert.Equal("##CHUNK:1\n" + body, Nmp2Strategy.ExtractRawChunkSections(appended, 2)[0]);
        Assert.Equal("firstsecond", Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(appended)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LegacyPadValuesRemainDecodable(int pad)
    {
        byte[] compressed = Nmp2Strategy.BrotliCompress(Encoding.UTF8.GetBytes("legacy"));
        string artifact = Nmp2ChunkedEncoder.Encode("legacy");
        string changed = artifact[..(artifact.IndexOf('\n') + 1)] +
            Nmp2Strategy.Base64UrlEncode([.. compressed, .. new byte[pad]]) + $"\n##PAD:{pad}\nNMP/END";
        Assert.Equal("legacy", Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(changed)));
    }
}
