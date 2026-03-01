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
        return new Nmp2Strategy().Encode(bytes, new EncodingOptions(CharsPerLine)).Artifact;
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
            return new Nmp2Strategy().Encode(bytes, new EncodingOptions(CharsPerLine)).Artifact;
        }

        return EncodeMultiChunkFromParts(chunks);
    }

    /// <summary>
    /// Appends a new chunk to an existing NMP/2 artifact without re-encoding existing chunks.
    /// <list type="bullet">
    /// <item>Single-chunk artifact → promotes to 2-chunk multi-chunk format.</item>
    /// <item>Multi-chunk artifact → appends a new chunk section, rewrites header.</item>
    /// </list>
    /// CRC32 is recomputed over all decoded original bytes + new chunk bytes.
    /// </summary>
    public static string AppendChunk(string existingArtifact, string newChunkText)
    {
        ArgumentException.ThrowIfNullOrEmpty(existingArtifact);

        if (!Nmp2Strategy.IsMultiChunk(existingArtifact))
        {
            // Single-chunk → decode existing, promote to 2-chunk
            byte[] existingBytes = new Nmp2Strategy().Decode(existingArtifact);
            string existingText = System.Text.Encoding.UTF8.GetString(existingBytes);
            return EncodeMultiChunkFromParts([existingText, newChunkText]);
        }

        // Multi-chunk → surgical append: copy existing chunk sections verbatim
        int oldCount = Nmp2Strategy.ParseChunkCount(existingArtifact);
        int newCount = oldCount + 1;

        // Extract existing chunk sections (the raw encoded lines between ##CHUNK:N and ##PAD:N inclusive)
        var existingChunkSections = ExtractRawChunkSections(existingArtifact, oldCount);

        // Decode all existing chunks to recompute CRC32 + total bytes
        var allDecodedBytes = new List<byte[]>(newCount);
        for (int i = 1; i <= oldCount; i++)
            allDecodedBytes.Add(Nmp2Strategy.DecodeChunkSection(existingArtifact, i));

        byte[] newChunkUtf8 = System.Text.Encoding.UTF8.GetBytes(newChunkText);
        allDecodedBytes.Add(newChunkUtf8);

        // Total original bytes and CRC32
        int totalBytes = allDecodedBytes.Sum(b => b.Length);
        byte[] allBytes = new byte[totalBytes];
        int offset = 0;
        foreach (var chunk in allDecodedBytes)
        {
            chunk.CopyTo(allBytes, offset);
            offset += chunk.Length;
        }
        uint crc = Crc32.HashToUInt32(allBytes);

        // Build new artifact: new header + existing chunk sections verbatim + new chunk compressed
        var sb = new StringBuilder();
        sb.Append("NMP/2 ");
        sb.Append(totalBytes);
        sb.Append("B CRC32:");
        sb.Append(crc.ToString("X8"));
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
        sb.Append(crc.ToString("X8"));
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

        byte[] compressed = Nmp2Strategy.BrotliCompress(chunkUtf8);

        int pad = (3 - (compressed.Length % 3)) % 3;
        byte[] padded = new byte[compressed.Length + pad];
        compressed.CopyTo(padded, 0);

        string b64 = Nmp2Strategy.Base64UrlEncode(padded);
        int lines = b64.Length == 0 ? 0 : (int)Math.Ceiling((double)b64.Length / CharsPerLine);

        for (int j = 0; j < lines; j++)
        {
            int start = j * CharsPerLine;
            int len = Math.Min(CharsPerLine, b64.Length - start);
            sb.Append(b64, start, len);
            sb.Append('\n');
        }

        sb.Append("##PAD:");
        sb.Append(pad);
        sb.Append('\n');
    }

    /// <summary>
    /// Extracts the raw text of each chunk section (##CHUNK:N through ##PAD:N\n inclusive)
    /// from a multi-chunk artifact, preserving exact formatting for verbatim copy.
    /// </summary>
    private static List<string> ExtractRawChunkSections(string artifact, int chunkCount)
    {
        var sections = new List<string>(chunkCount);
        for (int i = 1; i <= chunkCount; i++)
        {
            string marker = $"##CHUNK:{i}\n";
            int start = artifact.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                throw new FormatException($"Could not find ##CHUNK:{i} marker in artifact.");

            // Find the end of this chunk's PAD line
            string padPrefix = "##PAD:";
            int padStart = artifact.IndexOf(padPrefix, start + marker.Length, StringComparison.Ordinal);
            if (padStart < 0)
                throw new FormatException($"Could not find ##PAD: after ##CHUNK:{i}.");

            int padEnd = artifact.IndexOf('\n', padStart);
            if (padEnd < 0)
                padEnd = artifact.Length;
            else
                padEnd++; // include the newline

            sections.Add(artifact[start..padEnd]);
        }
        return sections;
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
            bytes = new Nmp2Strategy().Decode(artifact);
        }
        else
        {
            bytes = Nmp2Strategy.DecodeChunkSection(artifact, chunkIndex);
        }

        return System.Text.Encoding.UTF8.GetString(bytes);
    }

}
