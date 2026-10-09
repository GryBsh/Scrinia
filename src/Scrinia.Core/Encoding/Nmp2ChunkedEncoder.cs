using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace Scrinia.Core.Encoding;

/// <summary>
/// Chunk-addressable NMP/2 encoder/decoder. Extends <see cref="Nmp2Strategy"/> with support
/// for independently-decodable multi-chunk artifacts.
///
/// Single-element content always produces a single chunk via <see cref="Nmp2Strategy"/>.
/// Multi-chunk artifacts are only created when the caller explicitly passes multiple elements
/// to <see cref="EncodeChunks"/> or uses <see cref="AppendChunk"/>.
///
/// Multi-chunk format:
///   NMP/2 {N}B CRC32:{hex} BR+B64 C:{k}
///   ##CHUNK:1
///   {base64 lines — independently brotli-compressed}
///   ##PAD:{n}
///   ##CHUNK:2
///   ...
///   NMP/END
///
/// CRC32 is over the full original UTF-8 bytes (pre-split).
/// Each chunk is independently brotli-compressed and base64-encoded.
/// </summary>
public static class Nmp2ChunkedEncoder
{
    private const int CharsPerLine = 76;

    /// <summary>
    /// Encodes text into a single-chunk NMP/2 artifact, regardless of size.
    /// Multi-chunk artifacts are only created via <see cref="EncodeChunks"/> (multiple elements)
    /// or <see cref="AppendChunk"/>.
    /// </summary>
    public static string Encode(string text)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return Nmp2Strategy.Instance.Encode(bytes, new EncodingOptions(CharsPerLine)).Artifact;
    }

    /// <summary>
    /// Encodes an array of pre-split text chunks into a multi-chunk NMP/2 artifact.
    /// Single element → delegates to <see cref="Nmp2Strategy"/> (single-chunk format).
    /// Two or more elements → multi-chunk format with <c>C:{k}</c> header token.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="chunks"/> is null or empty.</exception>
    public static string EncodeChunks(string[] chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Length == 0)
            throw new ArgumentException("At least one chunk is required.", nameof(chunks));

        if (chunks.Length == 1)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(chunks[0]);
            return Nmp2Strategy.Instance.Encode(bytes, new EncodingOptions(CharsPerLine)).Artifact;
        }

        return EncodeMultiChunkFromParts(chunks);
    }

    /// <summary>
    /// Appends a new chunk to an existing NMP/2 artifact without re-encoding existing chunks.
    /// <list type="bullet">
    /// <item>Single-chunk artifact → promotes to 2-chunk multi-chunk format.</item>
    /// <item>Multi-chunk artifact → appends a new chunk section, rewrites header.
    ///   CRC32 is computed incrementally via <see cref="Crc32Combine"/> — existing chunks
    ///   are NOT decoded, making this O(new chunk size) instead of O(total artifact size).</item>
    /// </list>
    /// </summary>
    public static string AppendChunk(string existingArtifact, string newChunkText)
    {
        ArgumentException.ThrowIfNullOrEmpty(existingArtifact);

        if (!Nmp2Strategy.IsMultiChunk(existingArtifact))
        {
            // Single-chunk → decode existing, promote to 2-chunk
            byte[] decoded = Nmp2Strategy.Instance.Decode(existingArtifact);
            string existingText = System.Text.Encoding.UTF8.GetString(decoded);
            return EncodeMultiChunkFromParts([existingText, newChunkText]);
        }

        // Multi-chunk → surgical append: copy existing chunk sections verbatim
        int oldCount = Nmp2Strategy.ParseChunkCount(existingArtifact);
        int newCount = oldCount + 1;

        // Extract existing chunk sections (the raw encoded lines between ##CHUNK:N and ##PAD:N inclusive)
        var existingChunkSections = Nmp2Strategy.ExtractRawChunkSections(existingArtifact, oldCount);

        // Parse existing header for CRC and byte count — no need to decode existing chunks
        Nmp2Header metadata = Nmp2Header.Parse(existingArtifact);
        uint existingCrc = metadata.Crc32;
        int existingBytes = metadata.OriginalBytes;

        byte[] newChunkUtf8 = System.Text.Encoding.UTF8.GetBytes(newChunkText);
        uint newChunkCrc = Crc32.HashToUInt32(newChunkUtf8);

        // Incremental CRC: combine existing CRC with new chunk CRC
        int totalBytes = existingBytes + newChunkUtf8.Length;
        uint crc = Crc32Combine(existingCrc, newChunkCrc, newChunkUtf8.Length);

        // Build new artifact: new header + existing chunk sections verbatim + new chunk compressed
        var sb = new StringBuilder();
        sb.Append("NMP/2 ");
        sb.Append(totalBytes);
        sb.Append("B CRC32:");
        sb.Append(crc.ToString("X8", CultureInfo.InvariantCulture));
        sb.Append(" BR+B64 C:");
        sb.Append(newCount);
        sb.Append('\n');

        // Copy existing chunk sections verbatim
        foreach (string section in existingChunkSections)
        {
            sb.Append(section);
        }

        // Compress and append the new chunk
        AppendCompressedChunk(sb, newChunkUtf8, newCount);

        sb.Append("NMP/END");
        return sb.ToString();
    }

    /// <summary>
    /// Combines two CRC-32 checksums for concatenated data without re-reading the original data.
    /// Given crc1 = CRC(data1) and crc2 = CRC(data2), computes CRC(data1 + data2).
    /// Uses the GF(2) matrix method from zlib (crc32_combine).
    /// Polynomial: 0xEDB88320 (standard CRC-32, IEEE 802.3).
    /// </summary>
    internal static uint Crc32Combine(uint crc1, uint crc2, long len2)
    {
        if (len2 <= 0) return crc1;

        uint[] even = new uint[32];
        uint[] odd = new uint[32];

        // Operator for one zero bit: shifts right by 1 with polynomial feedback
        odd[0] = 0xEDB88320u;
        uint row = 1;
        for (int n = 1; n < 32; n++)
        {
            odd[n] = row;
            row <<= 1;
        }

        // Operator for two zero bits
        Gf2MatrixSquare(even, odd);
        // Operator for four zero bits
        Gf2MatrixSquare(odd, even);

        // Apply len2 bytes (8*len2 bits) of zeros to crc1
        do
        {
            // Each iteration: square doubles the bit count of the operator
            // First iteration: even = 8 bits (1 byte)
            Gf2MatrixSquare(even, odd);
            if ((len2 & 1) != 0)
                crc1 = Gf2MatrixTimes(even, crc1);
            len2 >>= 1;
            if (len2 == 0) break;

            Gf2MatrixSquare(odd, even);
            if ((len2 & 1) != 0)
                crc1 = Gf2MatrixTimes(odd, crc1);
            len2 >>= 1;
        } while (len2 != 0);

        return crc1 ^ crc2;
    }

    private static uint Gf2MatrixTimes(uint[] mat, uint vec)
    {
        uint sum = 0;
        int i = 0;
        while (vec != 0)
        {
            if ((vec & 1) != 0)
                sum ^= mat[i];
            vec >>= 1;
            i++;
        }
        return sum;
    }

    private static void Gf2MatrixSquare(uint[] square, uint[] mat)
    {
        for (int n = 0; n < 32; n++)
            square[n] = Gf2MatrixTimes(mat, mat[n]);
    }

    /// <summary>
    /// Shared encoding logic for multi-chunk artifacts from pre-split text parts.
    /// Computes CRC32 over the full concatenated UTF-8 bytes.
    /// </summary>
    private static string EncodeMultiChunkFromParts(string[] chunks)
    {
        // Compute total bytes and CRC32 over the full concatenation
        var chunkBytes = new byte[chunks.Length][];
        int totalBytes = 0;
        for (int i = 0; i < chunks.Length; i++)
        {
            chunkBytes[i] = System.Text.Encoding.UTF8.GetBytes(chunks[i]);
            totalBytes += chunkBytes[i].Length;
        }

        byte[] allBytes = new byte[totalBytes];
        int offset = 0;
        for (int i = 0; i < chunkBytes.Length; i++)
        {
            chunkBytes[i].CopyTo(allBytes, offset);
            offset += chunkBytes[i].Length;
        }
        uint crc = Crc32.HashToUInt32(allBytes);

        int k = chunks.Length;
        var sb = new StringBuilder();
        sb.Append("NMP/2 ");
        sb.Append(totalBytes);
        sb.Append("B CRC32:");
        sb.Append(crc.ToString("X8", CultureInfo.InvariantCulture));
        sb.Append(" BR+B64 C:");
        sb.Append(k);
        sb.Append('\n');

        for (int i = 0; i < k; i++)
        {
            AppendCompressedChunk(sb, chunkBytes[i], i + 1);
        }

        sb.Append("NMP/END");
        return sb.ToString();
    }

    /// <summary>Appends a single compressed chunk section (##CHUNK:N, base64 lines, ##PAD:N) to the builder.</summary>
    private static void AppendCompressedChunk(StringBuilder sb, byte[] chunkUtf8, int chunkNumber)
    {
        sb.Append("##CHUNK:");
        sb.Append(chunkNumber);
        sb.Append('\n');

        int pad = Nmp2Strategy.CompressAndAppendBase64(sb, chunkUtf8, CharsPerLine);

        sb.Append("##PAD:");
        sb.Append(pad);
        sb.Append('\n');
    }

    /// <summary>Returns the number of independently decodable chunks. Always ≥ 1.</summary>
    public static int GetChunkCount(string artifact) =>
        Nmp2Strategy.ParseChunkCount(artifact);

    /// <summary>
    /// Decodes and returns the text of one chunk (1-based).
    /// Throws <see cref="ArgumentOutOfRangeException"/> if <paramref name="chunkIndex"/>
    /// is outside [1, ChunkCount].
    /// </summary>
    public static string DecodeChunk(string artifact, int chunkIndex)
    {
        int count = Nmp2Strategy.ParseChunkCount(artifact);
        if (chunkIndex < 1 || chunkIndex > count)
            throw new ArgumentOutOfRangeException(nameof(chunkIndex),
                $"chunkIndex {chunkIndex} is out of range [1, {count}].");

        byte[] bytes;
        if (!Nmp2Strategy.IsMultiChunk(artifact))
        {
            // Single-chunk: decode the entire artifact (chunkIndex must be 1, already validated)
            bytes = Nmp2Strategy.Instance.Decode(artifact);
        }
        else
        {
            bytes = Nmp2Strategy.DecodeChunkSection(artifact, chunkIndex);
        }

        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Decodes every chunk of an artifact in a single pass and verifies the header's declared
    /// length and CRC32 against the concatenated result — the same check
    /// <see cref="Nmp2Strategy.Decode"/> performs for the whole artifact.
    ///
    /// <para>Exists because calling <see cref="Nmp2Strategy.Decode"/> for the verified byte count
    /// and then <see cref="DecodeChunk"/> once per index for the per-chunk text decompresses every
    /// chunk twice: <c>Decode</c>'s multi-chunk path already walks and decompresses each
    /// <c>##CHUNK:</c> section internally but only returns the concatenated bytes, discarding the
    /// per-chunk boundaries a caller displaying a memory needs. This method
    /// does that same per-chunk decompression exactly once, keeps the boundaries, and runs the
    /// identical integrity check against the assembled buffer — nothing is decompressed twice and
    /// nothing about the check is weakened.</para>
    /// </summary>
    /// <exception cref="InvalidDataException">The header's declared length or CRC32 does not
    /// match the concatenated decoded bytes.</exception>
    internal static (string[] Chunks, long TotalBytes) DecodeAllChunksVerified(string artifact)
    {
        if (!Nmp2Strategy.IsMultiChunk(artifact))
        {
            // Single-chunk: there is no ##CHUNK: marker to decode a section against, so
            // Nmp2Strategy.Decode is already the one-and-only decompression pass, integrity
            // check included.
            byte[] decoded = Nmp2Strategy.Instance.Decode(artifact);
            return ([System.Text.Encoding.UTF8.GetString(decoded)], decoded.LongLength);
        }

        int count = Nmp2Strategy.ParseChunkCount(artifact);
        // Same guard Nmp2Strategy.DecodeCore runs before any chunk work — prevents trivial DoS
        // via a tiny artifact that declares millions of chunks. Missing here, this method would
        // do genuine unbounded O(count) work (a `new byte[count][]` allocation and a decode loop)
        // before the MaxDecodedBytes budget below ever gets a chance to fire, on an artifact
        // Decode() rejects in O(1).
        if (count < 1 || count > Nmp2Strategy.MaxChunkCount)
            throw new InvalidDataException(
                $"NMP/2 artifact declares {count} chunks; must be 1..{Nmp2Strategy.MaxChunkCount}.");
        long maxBytes = Nmp2Strategy.MaxDecodedBytes;
        var sections = Nmp2Strategy.ExtractRawChunkSections(artifact, count);
        var chunkBytes = new byte[count][];
        long total = 0;
        for (int i = 0; i < sections.Count; i++)
        {
            chunkBytes[i] = Nmp2Strategy.DecodeChunkSectionFrom(sections[i], maxBytes - total);
            total += chunkBytes[i].Length;
            if (total > maxBytes)
                throw new InvalidDataException(
                    $"NMP/2 decoded size exceeds MaxDecodedBytes={maxBytes} bytes (after chunk {i + 1}).");
        }

        byte[] all = new byte[total];
        int offset = 0;
        foreach (var cb in chunkBytes)
        {
            cb.CopyTo(all, offset);
            offset += cb.Length;
        }
        // Same check Decode() runs on its own concatenated result — reused rather than
        // duplicated so this can never drift from what a whole-artifact decode verifies.
        Nmp2Strategy.VerifyIntegrity(artifact, all);

        var chunks = new string[count];
        for (int i = 0; i < count; i++)
            chunks[i] = System.Text.Encoding.UTF8.GetString(chunkBytes[i]);

        return (chunks, total);
    }
}
