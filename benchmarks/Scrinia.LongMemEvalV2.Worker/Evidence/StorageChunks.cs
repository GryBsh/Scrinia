namespace Scrinia.LongMemEvalV2.Worker.Evidence;

/// <summary>V2 storage policy v1: bounded UTF-16 chunks, preserving the complete original string.</summary>
public static class StorageChunks
{
    // Public recall permits 7,992 content characters. Even two Int32.MaxValue
    // indices in "Chunk i/n\n\n" use only 29, leaving a conservative margin.
    public const int MaxCharacters = 7900;

    public static string[] Split(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0) return [content];
        var chunks = new List<string>();
        for (int offset = 0; offset < content.Length;)
        {
            int length = Math.Min(MaxCharacters, content.Length - offset);
            // Never divide a Unicode scalar between separately UTF-8-encoded chunks.
            if (offset + length < content.Length && char.IsHighSurrogate(content[offset + length - 1])
                && char.IsLowSurrogate(content[offset + length])) length--;
            chunks.Add(content.Substring(offset, length));
            offset += length;
        }
        return chunks.ToArray();
    }
}
