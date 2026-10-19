using System.Globalization;

namespace Scrinia.Core.Encoding;

/// <summary>The mandatory metadata on the first logical line of an NMP/2 artifact.</summary>
internal readonly record struct Nmp2Header(int OriginalBytes, uint Crc32, int ChunkCount, bool HasChunkMarker)
{
    internal static Nmp2Header Parse(string artifact)
    {
        int newline = artifact.IndexOf('\n');
        int end = newline < 0 ? artifact.Length : newline;
        if (newline >= 0 && end > 0 && artifact[end - 1] == '\r')
            end--;

        string[] tokens = artifact[..end].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is not (4 or 5) ||
            !tokens[0].Equals("NMP/2", StringComparison.Ordinal) ||
            !tokens[1].EndsWith('B') ||
            !int.TryParse(tokens[1].AsSpan(0, tokens[1].Length - 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out int originalBytes) ||
            !tokens[2].StartsWith("CRC32:", StringComparison.Ordinal) ||
            !tokens[3].Equals("BR+B64", StringComparison.Ordinal))
            throw new InvalidDataException("NMP/2 header requires length, CRC32 and BR+B64 metadata in order.");

        ReadOnlySpan<char> crcText = tokens[2].AsSpan(6);
        if (crcText.Length != 8)
            throw new InvalidDataException("NMP/2 CRC32 must contain exactly eight ASCII hexadecimal digits.");
        foreach (char digit in crcText)
        {
            if (digit is not (>= '0' and <= '9') and not (>= 'A' and <= 'F') and not (>= 'a' and <= 'f'))
                throw new InvalidDataException("NMP/2 CRC32 must contain exactly eight ASCII hexadecimal digits.");
        }
        uint crc = uint.Parse(crcText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

        bool hasChunkMarker = tokens.Length == 5;
        int chunkCount = 1;
        if (hasChunkMarker &&
            (!tokens[4].StartsWith("C:", StringComparison.Ordinal) ||
             !int.TryParse(tokens[4].AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out chunkCount) ||
             chunkCount < 1 || chunkCount > Nmp2Strategy.MaxChunkCount))
            throw new InvalidDataException($"NMP/2 header chunks must be 1..{Nmp2Strategy.MaxChunkCount}.");

        return new Nmp2Header(originalBytes, crc, chunkCount, hasChunkMarker);
    }
}
