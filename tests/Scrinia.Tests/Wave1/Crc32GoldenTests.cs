using System.IO.Hashing;
using System.Text;
using Scrinia.Core.Encoding;
using Xunit;

namespace Scrinia.Tests.Wave1;

/// <summary>
/// C4: Crc32Combine GF(2) matrix operator — verified against ground truth Crc32.HashToUInt32.
/// Covers 200 random splits, empty-second edge, AppendChunk incremental CRC, 5 MiB boundary,
/// and corrupt-header rejection via DecodeAllChunksVerified.
/// </summary>
public sealed class Crc32GoldenTests
{
    private static readonly string[] ExpectedHelloWorld = new[] { "hello", "world" };

    [Fact]
    public void Crc32Combine_MatchesGroundTruth_ForRandomSplits()
    {
        var rnd = new Random(42);
        for (int t = 0; t < 200; t++)
        {
            int len1 = rnd.Next(0, 4096);
            int len2 = rnd.Next(0, 4096);
            byte[] a = new byte[len1];
            rnd.NextBytes(a);
            byte[] b = new byte[len2];
            rnd.NextBytes(b);
            byte[] concat = new byte[len1 + len2];
            Buffer.BlockCopy(a, 0, concat, 0, len1);
            Buffer.BlockCopy(b, 0, concat, len1, len2);
            uint crc1 = Crc32.HashToUInt32(a);
            uint crc2 = Crc32.HashToUInt32(b);
            uint expected = Crc32.HashToUInt32(concat);
            uint got = Nmp2ChunkedEncoder.Crc32Combine(crc1, crc2, len2);
            Assert.Equal(expected, got);
        }
    }

    [Fact]
    public void Crc32Combine_EmptySecond_ReturnsFirst()
    {
        uint crc1 = Crc32.HashToUInt32(Encoding.UTF8.GetBytes("hello"));
        Assert.Equal(crc1, Nmp2ChunkedEncoder.Crc32Combine(crc1, 0, 0));
    }

    [Fact]
    public void AppendChunk_IncrementalCrc_EqualsConcatGroundTruth()
    {
        string p1 = new string('a', 1000);
        string p2 = new string('b', 2000);
        string p3 = "hello world";
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(new[] { p1, p2 });
        artifact = Nmp2ChunkedEncoder.AppendChunk(artifact, p3);
        byte[] concat = Encoding.UTF8.GetBytes(p1 + p2 + p3);
        uint ground = Crc32.HashToUInt32(concat);
        var meta = Nmp2Strategy.Instance.ParseHeader(artifact);
        Assert.Equal(ground, meta.Crc32);
        var (chunks, total) = Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact);
        Assert.Equal(3, chunks.Length);
        Assert.Equal(p1, chunks[0]);
        Assert.Equal(p2, chunks[1]);
        Assert.Equal(p3, chunks[2]);
        Assert.Equal(concat.Length, total);
    }

    [Fact]
    public void Crc32Combine_5MiB_Boundary_100kChunkGuard()
    {
        string big = new string('x', 5 * 1024 * 1024);
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(new[] { big, "tail" });
        var (chunks, total) = Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact);
        Assert.Equal(2, chunks.Length);
        Assert.Equal(big, chunks[0]);
        Assert.Equal("tail", chunks[1]);
    }

    [Fact]
    public void DecodeAllChunksVerified_RejectsCorruptHeaderCrc()
    {
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(ExpectedHelloWorld);
        // Corrupt header CRC — replace the 8 hex digits after CRC32: without duplicating them.
        // Plan's naive Replace("CRC32:", "CRC32:00000000") would produce "CRC32:00000000<old>"
        // which TryParse may reject (null CRC => no integrity check). Replace the whole token instead.
        string corrupt = CorruptCrcHeader(artifact);

        // If original already happened to equal the corrupt value, flip to the other corrupt constant
        var originalMeta = Nmp2Strategy.Instance.ParseHeader(artifact);
        var corruptMeta = Nmp2Strategy.Instance.ParseHeader(corrupt);
        if (originalMeta.Crc32 == corruptMeta.Crc32)
        {
            corrupt = CorruptCrcHeaderWithValue(artifact, "FFFFFFFF");
            corruptMeta = Nmp2Strategy.Instance.ParseHeader(corrupt);
            if (originalMeta.Crc32 == corruptMeta.Crc32)
                corrupt = CorruptCrcHeaderWithValue(artifact, "12345678");
        }

        Assert.NotEqual(artifact, corrupt);
        Assert.Throws<InvalidDataException>(() => Nmp2ChunkedEncoder.DecodeAllChunksVerified(corrupt));
    }

    private static string CorruptCrcHeader(string artifact) => CorruptCrcHeaderWithValue(artifact, "00000000");

    private static string CorruptCrcHeaderWithValue(string artifact, string newHex)
    {
        const string marker = "CRC32:";
        int idx = artifact.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return artifact;
        int hexStart = idx + marker.Length;
        int hexEnd = hexStart;
        while (hexEnd < artifact.Length && IsHexChar(artifact[hexEnd]))
            hexEnd++;
        // hexEnd - hexStart should be 8, but handle generically
        return artifact[..hexStart] + newHex + artifact[hexEnd..];
    }

    private static bool IsHexChar(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
