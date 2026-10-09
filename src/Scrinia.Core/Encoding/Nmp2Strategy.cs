using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace Scrinia.Core.Encoding;

/// <summary>
/// NMP/2 (Named Memory Protocol v2) encoding strategy. Always Brotli-compresses, then encodes
/// as URL-safe Base64. Achieves ~60–90 bits/token on compressible content.
///
/// Format:
///   NMP/2 {N}B CRC32:{hex} BR+B64
///   {up to 76 url-safe base64 chars per line}
///   ...
///   ##PAD:{n}
///   NMP/END
///
/// No row-index prefix — Brotli destroys byte positions so indices carry no meaning.
/// Brotli is unconditional and declared by the BR+B64 codec token in the header.
/// CRC32 is computed over original (pre-compression) bytes.
/// PAD is 0–2 zero bytes appended to Brotli output for 3-byte Base64 alignment.
/// </summary>
public sealed class Nmp2Strategy : IEncodingStrategy
{
    public static readonly Nmp2Strategy Instance = new();

    /// <summary>
    /// Upper bound on bytes Decode() will produce. Guards against memory-pressure DoS from
    /// large or malicious multi-chunk artifacts and high-ratio Brotli streams. Set per-process
    /// at startup; default 64 MB.
    /// </summary>
    public static int MaxDecodedBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    /// Upper bound on declared chunk count in a multi-chunk artifact header. Prevents
    /// trivial DoS via tiny artifacts that declare millions of chunks.
    /// </summary>
    public const int MaxChunkCount = 100_000;

    public string StrategyId => "nmp/2";
    public string Description => "nmp/2 brotli+base64 — maximum LLM density, ~60-90 bits/token on code";

    public EncodingResult Encode(ReadOnlySpan<byte> input, EncodingOptions options)
    {
        int originalLen = input.Length;
        uint crc = Crc32.HashToUInt32(input);
        int charsPerLine = options.CharsPerLine;

        // Brotli + Base64 typically lands at ~1.4x the compressed size in chars. Most real
        // payloads compress 3–10x, so size the builder to the expected compressed-base64
        // chars plus header/footer (~70 chars) with a 128-char floor for tiny inputs.
        int approxCap = 70 + Math.Max(128, (input.Length * 14) / 10);
        var sb = new StringBuilder(approxCap);

        // Header
        sb.Append("NMP/2 ");
        sb.Append(originalLen);
        sb.Append("B CRC32:");
        sb.Append(crc.ToString("X8", CultureInfo.InvariantCulture));
        sb.Append(" BR+B64");
        sb.Append('\n');

        // Compress + Base64Url + append data lines via the pooled helper.
        // Returns the pad value to record in the footer; new artifacts always emit 0
        // because Base64Url encodes without padding bytes — older artifacts may carry pad 1 or 2
        // and remain decodable.
        int pad = CompressAndAppendBase64(sb, input, charsPerLine);

        // Footer
        sb.Append("##PAD:");
        sb.Append(pad);
        sb.Append('\n');
        sb.Append("NMP/END");

        string artifact = sb.ToString();
        return new EncodingResult(
            Artifact: artifact,
            OriginalBytes: originalLen,
            ArtifactChars: artifact.Length,
            EstimatedTokens: 0,  // filled in by EncoderService
            BitsPerToken: 0,     // filled in by EncoderService
            StrategyId: StrategyId);
    }

    /// <summary>
    /// Brotli-compresses <paramref name="data"/> into a pooled buffer, Base64Url-encodes the
    /// result, and writes the data as newline-terminated chunks of <paramref name="charsPerLine"/>
    /// characters into <paramref name="sb"/>. Returns the pad value to record in the footer
    /// (always 0 with Base64Url encoding; preserved for back-compat with the older padded format).
    /// </summary>
    internal static int CompressAndAppendBase64(StringBuilder sb, ReadOnlySpan<byte> data, int charsPerLine)
    {
        int maxCompressed = BrotliEncoder.GetMaxCompressedLength(data.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(maxCompressed);
        try
        {
            if (!BrotliEncoder.TryCompress(data, rented.AsSpan(0, maxCompressed),
                    out int compressedLen, quality: 11, window: 22))
                throw new InvalidOperationException(
                    $"NMP/2: Brotli compression failed for {data.Length}-byte input.");

            string b64 = Base64Url.EncodeToString(rented.AsSpan(0, compressedLen));
            int lines = b64.Length == 0 ? 0 : (int)Math.Ceiling((double)b64.Length / charsPerLine);

            for (int i = 0; i < lines; i++)
            {
                int start = i * charsPerLine;
                int len = Math.Min(charsPerLine, b64.Length - start);
                sb.Append(b64, start, len);
                sb.Append('\n');
            }
            return 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public byte[] Decode(string artifact)
    {
        byte[] decoded = DecodeCore(artifact);
        VerifyIntegrity(artifact, decoded);
        return decoded;
    }

    private byte[] DecodeCore(string artifact)
    {
        int maxBytes = MaxDecodedBytes;

        if (IsMultiChunk(artifact))
        {
            int count = ParseChunkCount(artifact);
            if (count < 1 || count > MaxChunkCount)
                throw new InvalidDataException(
                    $"NMP/2 artifact declares {count} chunks; must be 1..{MaxChunkCount}.");

            var sections = ExtractRawChunkSections(artifact, count);
            var chunks = new List<byte[]>(Math.Min(count, 256));
            long runningTotal = 0;
            for (int i = 0; i < sections.Count; i++)
            {
                byte[] chunkBytes = DecodeChunkSectionFrom(sections[i], maxBytes - runningTotal);
                runningTotal += chunkBytes.Length;
                if (runningTotal > maxBytes)
                    throw new InvalidDataException(
                        $"NMP/2 decoded size exceeds MaxDecodedBytes={maxBytes} bytes (after chunk {i + 1}).");
                chunks.Add(chunkBytes);
            }

            byte[] result = new byte[runningTotal];
            int offset = 0;
            foreach (var chunk in chunks)
            {
                chunk.CopyTo(result, offset);
                offset += chunk.Length;
            }
            return result;
        }

        // Single-chunk: collect Base64 chars from data lines (between header and footer)
        var sb64 = new StringBuilder();
        foreach (var line in EnumerateLines(artifact).Skip(1))
        {
            if (line.StartsWith("##", StringComparison.Ordinal)) break;
            if (line.Equals("NMP/END", StringComparison.Ordinal)) break;

            sb64.Append(line);
        }

        int pad = ParsePad(artifact);
        byte[] padded = Base64UrlDecode(sb64.ToString());

        return DecompressPadded(padded, pad, maxBytes);
    }

    /// <summary>Verifies decoded output against the header's declared length and CRC32.
    /// Both length (including zero) and CRC32 are mandatory and always checked.
    /// Internal (not private) so <see cref="Nmp2ChunkedEncoder.DecodeAllChunksVerified"/> can run
    /// the same check against a per-chunk-assembled buffer without a second whole-artifact
    /// decompression pass.</summary>
    internal static void VerifyIntegrity(string artifact, byte[] decoded)
    {
        Nmp2Header header = Nmp2Header.Parse(artifact);
        if (decoded.Length != header.OriginalBytes)
            throw new InvalidDataException("NMP/2 decoded length does not match its header.");
        if (Crc32.HashToUInt32(decoded) != header.Crc32)
            throw new InvalidDataException("NMP/2 CRC32 does not match its header.");
    }

    /// <summary>Checks family and end sentinels for routing; does not verify header grammar or integrity.</summary>
    public bool CanDecode(string artifact) =>
        artifact.StartsWith("NMP/2 ", StringComparison.Ordinal) &&
        artifact.Contains("NMP/END", StringComparison.Ordinal);

    /// <summary>
    /// Preliminary family gate for classification/routing: true when the input begins with the
    /// NMP/2 prefix. This is the single authority for "does this look like an NMP/2 artifact"
    /// across the CLI, MCP, plugin, and shared-base surfaces. It recognises the family only — full
    /// validity (header grammar, body structure, CRC) is <see cref="Decode"/>'s job and runs after a
    /// positive result here. Keeping the bare prefix (no trailing space) on purpose: a bare or
    /// incomplete "NMP/2…" input is still recognised as the family and then rejected by decoding,
    /// so it is never misrouted to name/URI handling.
    /// </summary>
    public bool RecognizesNmp2Prefix(string candidate) =>
        candidate.StartsWith("NMP/2", StringComparison.Ordinal);

    public ArtifactMetadata ParseHeader(string artifact)
    {
        Nmp2Header header = Nmp2Header.Parse(artifact);
        return new ArtifactMetadata(
            StrategyId: StrategyId,
            OriginalBytes: header.OriginalBytes,
            Crc32: header.Crc32);
    }

    private static int ParsePad(string artifact)
    {
        foreach (var line in EnumerateLines(artifact))
        {
            if (!line.StartsWith("##", StringComparison.Ordinal)) continue;
            if (line.StartsWith("##PAD:", StringComparison.Ordinal) &&
                int.TryParse(line[6..], out int pad))
                return pad;
            break;
        }
        return 0;
    }

    /// <summary>Returns whether the validated header has an explicit chunk marker.</summary>
    internal static bool IsMultiChunk(string artifact) => Nmp2Header.Parse(artifact).HasChunkMarker;

    /// <summary>Parses the C:{k} value from the header; returns 1 for single-chunk artifacts.</summary>
    internal static int ParseChunkCount(string artifact) => Nmp2Header.Parse(artifact).ChunkCount;

    /// <summary>
    /// Decodes a single ##CHUNK:{chunkIndex} section from a multi-chunk artifact.
    /// Returns the decompressed bytes for that chunk. Bounded by <see cref="MaxDecodedBytes"/>.
    /// </summary>
    internal static byte[] DecodeChunkSection(string artifact, int chunkIndex) =>
        DecodeChunkSection(artifact, chunkIndex, MaxDecodedBytes);

    /// <summary>
    /// Decodes a single ##CHUNK:{chunkIndex} section, throwing
    /// <see cref="InvalidDataException"/> if the decompressed output exceeds
    /// <paramref name="maxBytes"/>.
    /// </summary>
    internal static byte[] DecodeChunkSection(string artifact, int chunkIndex, long maxBytes)
    {
        string chunkMarker = $"##CHUNK:{chunkIndex}";
        bool inChunk = false;
        var sb64 = new StringBuilder();
        int pad = 0;

        foreach (var line in EnumerateLines(artifact))
        {
            if (!inChunk)
            {
                if (line.Equals(chunkMarker, StringComparison.Ordinal))
                    inChunk = true;
                continue;
            }

            if (line.StartsWith("##PAD:", StringComparison.Ordinal))
            {
                if (int.TryParse(line[6..], out int p))
                    pad = p;
                break;
            }
            if (line.StartsWith("##", StringComparison.Ordinal)) break;
            if (line.Equals("NMP/END", StringComparison.Ordinal)) break;

            sb64.Append(line);
        }

        byte[] padded = Base64UrlDecode(sb64.ToString());
        return DecompressPadded(padded, pad, maxBytes);
    }

    /// <summary>
    /// Extracts the raw text of each <c>##CHUNK:N</c> .. <c>##PAD:N</c> section from a
    /// multi-chunk artifact in a single left-to-right pass, preserving exact formatting for
    /// verbatim copy. Each <c>IndexOf</c> starts where the previous section ended rather than
    /// re-scanning from index 0, so the whole extraction is O(n) instead of O(k·n).
    /// </summary>
    internal static List<string> ExtractRawChunkSections(string artifact, int chunkCount)
    {
        var sections = new List<string>(chunkCount);
        int from = 0;
        for (int i = 1; i <= chunkCount; i++)
        {
            string marker = $"##CHUNK:{i}";
            int start = artifact.IndexOf(marker + "\n", from, StringComparison.Ordinal);
            int markerLength = marker.Length + 1;
            if (start < 0)
            {
                start = artifact.IndexOf(marker + "\r\n", from, StringComparison.Ordinal);
                markerLength++;
            }
            if (start < 0)
                throw new FormatException($"Could not find ##CHUNK:{i} marker in artifact.");
            // find ##PAD: after the marker
            string padPrefix = "##PAD:";
            int padStart = artifact.IndexOf(padPrefix, start + markerLength, StringComparison.Ordinal);
            if (padStart < 0)
                throw new FormatException($"Could not find ##PAD: after ##CHUNK:{i}.");
            int padEnd = artifact.IndexOf('\n', padStart);
            if (padEnd < 0) padEnd = artifact.Length; else padEnd++; // include newline
            sections.Add(artifact[start..padEnd]);
            from = padEnd; // next section starts here — makes the whole extraction O(n)
        }
        return sections;
    }

    /// <summary>
    /// Decodes a single chunk section (already extracted between its <c>##CHUNK:N</c> and
    /// <c>##PAD:N</c> markers) into decompressed bytes, bounded by <paramref name="maxBytes"/>.
    /// Operates on the pre-extracted slice rather than re-walking the whole artifact.
    /// </summary>
    internal static byte[] DecodeChunkSectionFrom(string section, long maxBytes)
    {
        var sb64 = new StringBuilder();
        int pad = 0;
        foreach (var line in EnumerateLines(section))
        {
            // The section's first line is the ##CHUNK:N marker copied verbatim from the artifact;
            // skip it, exactly as DecodeChunkSection skipped the marker before collecting.
            if (line.StartsWith("##CHUNK:", StringComparison.Ordinal)) continue;
            if (line.StartsWith("##PAD:", StringComparison.Ordinal))
            {
                if (int.TryParse(line[6..], out int p)) pad = p;
                break;
            }
            if (line.StartsWith("##", StringComparison.Ordinal)) break;
            if (line.Equals("NMP/END", StringComparison.Ordinal)) break;
            sb64.Append(line);
        }

        byte[] padded = Base64UrlDecode(sb64.ToString());
        return DecompressPadded(padded, pad, maxBytes);
    }

    /// <summary>
    /// Shared pad-footer handling for the single-chunk and per-chunk decode paths. Validates the
    /// <c>##PAD</c> footer against the decoded length BEFORE slicing so a negative or over-large pad
    /// (e.g. a tampered <c>##PAD:-999999</c>) fails closed with a catchable
    /// <see cref="InvalidDataException"/> instead of slicing past the buffer and throwing
    /// <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    private static byte[] DecompressPadded(byte[] padded, int pad, long maxBytes)
    {
        if (pad < 0 || pad > padded.Length)
            throw new InvalidDataException("NMP/2 pad footer is out of range and cannot be trusted.");
        int compressedLen = padded.Length - pad;
        if (compressedLen == 0) return [];
        byte[] compressed = padded[..compressedLen];
        return BrotliDecompressBounded(compressed, maxBytes);
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        int start = 0;
        while (start < text.Length)
        {
            int end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            yield return text[start..end].TrimEnd('\r');
            start = end + 1;
        }
    }

    internal static string Base64UrlEncode(byte[] data)
    {
        if (data.Length == 0) return string.Empty;
        return Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    internal static byte[] Base64UrlDecode(string s)
    {
        if (string.IsNullOrEmpty(s)) return [];
        s = s.Replace('-', '+').Replace('_', '/');
        int rem = s.Length % 4;
        if (rem != 0) s += new string('=', 4 - rem);
        return Convert.FromBase64String(s);
    }

    internal static byte[] BrotliCompress(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var brotli = new BrotliStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            brotli.Write(data);
        return ms.ToArray();
    }

    internal static byte[] BrotliDecompress(byte[] data) =>
        BrotliDecompressBounded(data, MaxDecodedBytes);

    /// <summary>
    /// Decompresses Brotli data, throwing <see cref="InvalidDataException"/> if the
    /// decompressed output would exceed <paramref name="maxBytes"/>. Bounds high-ratio
    /// compression so a small artifact cannot expand without limit.
    /// </summary>
    internal static byte[] BrotliDecompressBounded(byte[] data, long maxBytes)
    {
        if (maxBytes < 0)
            throw new InvalidDataException(
                $"NMP/2 remaining decode budget exhausted (maxBytes={maxBytes}).");

        using var input = new MemoryStream(data);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            long total = 0;
            int read;
            while ((read = brotli.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new InvalidDataException(
                        $"NMP/2 decompressed output exceeds MaxDecodedBytes={maxBytes} bytes.");
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return output.ToArray();
    }
}
