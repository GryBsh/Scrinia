using System.IO.Compression;

namespace Scrinia.Core.Encoding;

internal readonly record struct Nmp2Section(int Start, int Length, int Pad);
internal sealed record Nmp2Scan(IReadOnlyList<Nmp2Section> Sections, long VisitedCharacters);

/// <summary>Validates body tokens with a monotonic cursor and retains exact source slices.</summary>
internal static class Nmp2BodyScanner
{
    internal static Nmp2Scan Scan(string artifact, Nmp2Header header)
    {
        var sections = new List<Nmp2Section>(Math.Min(header.ChunkCount, 256));
        int position = 0;
        long visited = 0;
        ReadLine(artifact, ref position, ref visited, out _, out _); // Validated header line.
        int sectionStart = position;
        bool needsMarker = header.HasChunkMarker;
        bool needsFooter = false;
        bool ended = false;
        long base64Characters = 0;
        long maxBase64Characters = 4L * ((BrotliEncoder.GetMaxCompressedLength(Nmp2Strategy.MaxDecodedBytes) + 2L) / 3);

        while (position < artifact.Length)
        {
            ReadOnlySpan<char> line = ReadLine(artifact, ref position, ref visited, out int start, out _);
            if (ended)
            {
                foreach (char ch in line)
                {
                    visited++;
                    if (!char.IsWhiteSpace(ch)) throw InvalidStructure();
                }
                continue;
            }

            if (needsFooter)
            {
                if (!Matches(line, "NMP/END", ref visited)) throw InvalidStructure();
                ended = true;
                continue;
            }

            if (needsMarker)
            {
                if (!MatchesPrefix(line, "##CHUNK:", ref visited)) throw InvalidStructure();
                int index = 0;
                ReadOnlySpan<char> digits = line[8..];
                if (digits.IsEmpty) throw InvalidStructure();
                foreach (char digit in digits)
                {
                    visited++;
                    if (digit is < '0' or > '9' || index > Nmp2Strategy.MaxChunkCount / 10) throw InvalidStructure();
                    index = index * 10 + digit - '0';
                }
                if (index != sections.Count + 1) throw InvalidStructure();
                sectionStart = start;
                needsMarker = false;
                base64Characters = 0;
                continue;
            }

            if (!line.IsEmpty) visited++; // Dispatch examines the first character.
            if (!line.IsEmpty && line[0] == '#')
            {
                if (line.Length != 7 || !MatchesPrefix(line, "##PAD:", ref visited)) throw InvalidStructure();
                visited++;
                int pad = line[6] - '0';
                if (pad is < 0 or > 2 || base64Characters == 0) throw InvalidStructure();
                sections.Add(new Nmp2Section(sectionStart, position - sectionStart, pad));
                needsFooter = sections.Count == header.ChunkCount;
                needsMarker = !needsFooter;
                continue;
            }

            if (line.IsEmpty) throw InvalidStructure();
            foreach (char ch in line)
            {
                visited++;
                if (ch is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-' and not '_')
                    throw InvalidStructure();
            }
            base64Characters += line.Length;
            if (base64Characters > maxBase64Characters) throw InvalidStructure();
        }
        if (!ended || sections.Count != header.ChunkCount) throw InvalidStructure();
        return new Nmp2Scan(sections.AsReadOnly(), visited);
    }

    // Every examined cursor/lexical character increments the metric at the point of examination.
    private static ReadOnlySpan<char> ReadLine(string artifact, ref int position, ref long visited, out int start, out int tokenEnd)
    {
        start = position;
        while (position < artifact.Length)
        {
            visited++;
            if (artifact[position++] == '\n')
            {
                tokenEnd = position - 1;
                if (tokenEnd > start)
                {
                    visited++;
                    if (artifact[tokenEnd - 1] == '\r') tokenEnd--;
                }
                return artifact.AsSpan(start, tokenEnd - start);
            }
        }
        tokenEnd = position;
        return artifact.AsSpan(start, tokenEnd - start);
    }

    private static bool Matches(ReadOnlySpan<char> line, string token, ref long visited) =>
        line.Length == token.Length && MatchesPrefix(line, token, ref visited);

    private static bool MatchesPrefix(ReadOnlySpan<char> line, string prefix, ref long visited)
    {
        if (line.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            visited++;
            if (line[i] != prefix[i]) return false;
        }
        return true;
    }

    private static InvalidDataException InvalidStructure() => new("NMP/2 body structure is invalid.");
}
