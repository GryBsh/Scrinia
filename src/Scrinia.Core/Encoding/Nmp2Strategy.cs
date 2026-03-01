using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace Scrinia.Core.Encoding;

/// <summary>
/// NMP/2 (Named Memory Protocol v2) encoding strategy. Always Brotli-compresses, then encodes
/// as URL-safe Base64. Achieves ~60–90 bits/token on compressible content.
///
/// Format:
///   NMP/2 {N}B CRC32:{hex}
///   {up to 76 url-safe base64 chars per line}
///   ...
///   ##PAD:{n}
///   NMP/END
///
/// No row-index prefix — Brotli destroys byte positions so indices carry no meaning.
/// Brotli is unconditional and implied by the NMP/2 sentinel — no compression tag in header.
/// CRC32 is computed over original (pre-compression) bytes.
/// PAD is 0–2 zero bytes appended to Brotli output for 3-byte Base64 alignment.
/// </summary>
public sealed class Nmp2Strategy : IEncodingStrategy
{
    public string StrategyId => "nmp/2";
    public string Description => "nmp/2 brotli+base64 — maximum LLM density, ~60-90 bits/token on code";

    public EncodingResult Encode(ReadOnlySpan<byte> input, EncodingOptions options)
    {
        int originalLen = input.Length;
        uint crc = Crc32.HashToUInt32(input);

        // Always Brotli-compress
        byte[] compressed = BrotliCompress(input);

        // Pad to 3-byte boundary for clean URL-safe Base64 (no trailing '=')
        int pad = (3 - (compressed.Length % 3)) % 3;
        byte[] padded = new byte[compressed.Length + pad];
        compressed.CopyTo(padded, 0);

        int charsPerLine = options.CharsPerLine;
        string b64 = Base64UrlEncode(padded);
        int lines = b64.Length == 0 ? 0 : (int)Math.Ceiling((double)b64.Length / charsPerLine);

        // Estimate capacity
        int approxCap = 50 + lines * (charsPerLine + 1) + 15;
        var sb = new StringBuilder(approxCap);

        // Header
        sb.Append("NMP/2 ");
        sb.Append(originalLen);
        sb.Append("B CRC32:");
        sb.Append(crc.ToString("X8"));
        sb.Append(" BR+B64");
        sb.Append('\n');

        // Data lines — plain Base64, no row-index prefix
        for (int i = 0; i < lines; i++)
        {
            int start = i * charsPerLine;
            int len = Math.Min(charsPerLine, b64.Length - start);
            sb.Append(b64, start, len);
            sb.Append('\n');
        }

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

    public byte[] Decode(string artifact)
    {
        if (IsMultiChunk(artifact))
        {
            int count = ParseChunkCount(artifact);
            var chunks = new List<byte[]>(count);
            for (int i = 1; i <= count; i++)
                chunks.Add(DecodeChunkSection(artifact, i));

            int total = chunks.Sum(c => c.Length);
            byte[] result = new byte[total];
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
        foreach (var line in EnumerateLines(artifact))
        {
            if (line.StartsWith("NMP/2 ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("##", StringComparison.Ordinal)) break;
            if (line.Equals("NMP/END", StringComparison.Ordinal)) break;

            sb64.Append(line);
        }

        int pad = ParsePad(artifact);
        byte[] padded = Base64UrlDecode(sb64.ToString());

        int compressedLen = padded.Length - pad;
        if (compressedLen <= 0)
            return [];

        byte[] compressed = padded[..compressedLen];
        return BrotliDecompress(compressed);
    }

    public bool CanDecode(string artifact) =>
        artifact.StartsWith("NMP/2 ", StringComparison.Ordinal) &&
        artifact.Contains("NMP/END", StringComparison.Ordinal);

    public ArtifactMetadata ParseHeader(string artifact)
    {
        // Header: "NMP/2 {N}B CRC32:{hex}"
        int newlineIdx = artifact.IndexOf('\n');
        string headerLine = newlineIdx >= 0 ? artifact[..newlineIdx] : artifact;

        var parts = headerLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // parts[0] = "NMP/2"
        // parts[1] = "{N}B"
        // parts[2] = "CRC32:{hex}"

        int originalBytes = 0;
        if (parts.Length >= 2)
        {
            var bytesPart = parts[1];
            if (bytesPart.EndsWith('B'))
                int.TryParse(bytesPart[..^1], out originalBytes);
        }

        uint? crc = null;
        if (parts.Length >= 3 && parts[2].StartsWith("CRC32:", StringComparison.Ordinal))
        {
            if (uint.TryParse(parts[2][6..], System.Globalization.NumberStyles.HexNumber, null, out uint parsedCrc))
                crc = parsedCrc;
        }

        return new ArtifactMetadata(
            StrategyId: StrategyId,
            OriginalBytes: originalBytes,
            Crc32: crc);
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

    /// <summary>Returns true if the first line of the artifact contains a " C:" token.</summary>
    internal static bool IsMultiChunk(string artifact)
    {
        int newlineIdx = artifact.IndexOf('\n');
        string firstLine = newlineIdx >= 0 ? artifact[..newlineIdx] : artifact;
        return firstLine.Contains(" C:", StringComparison.Ordinal);
    }

    /// <summary>Parses the C:{k} value from the header; returns 1 for single-chunk artifacts.</summary>
    internal static int ParseChunkCount(string artifact)
    {
        if (!IsMultiChunk(artifact)) return 1;

        int newlineIdx = artifact.IndexOf('\n');
        string firstLine = newlineIdx >= 0 ? artifact[..newlineIdx] : artifact;
        int ci = firstLine.IndexOf(" C:", StringComparison.Ordinal);
        if (ci < 0) return 1;

        string rest = firstLine[(ci + 3)..];
        int spaceIdx = rest.IndexOf(' ');
        string countStr = spaceIdx >= 0 ? rest[..spaceIdx] : rest;
        return int.TryParse(countStr, out int count) ? count : 1;
    }

    /// <summary>
    /// Decodes a single ##CHUNK:{chunkIndex} section from a multi-chunk artifact.
    /// Returns the decompressed bytes for that chunk.
    /// </summary>
    internal static byte[] DecodeChunkSection(string artifact, int chunkIndex)
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
        int compressedLen = padded.Length - pad;
        if (compressedLen <= 0) return [];

        byte[] compressed = padded[..compressedLen];
        return BrotliDecompress(compressed);
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

    internal static byte[] BrotliDecompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }
}
