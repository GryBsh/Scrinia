namespace Scrinia.Commands.Hooks;

/// <summary>
/// Idempotent insert/update/remove of a scrinia-owned region inside a (possibly user-authored)
/// markdown file — the markdown analogue of the <c>_scriniaManaged</c> JSON sentinel the hook
/// installers use. The region is delimited by HTML-comment markers so it's invisible when the
/// markdown renders and unambiguous to find:
/// <code>
/// &lt;!-- scrinia:begin v1 --&gt;
/// ...managed body...
/// &lt;!-- scrinia:end --&gt;
/// </code>
/// User content outside the markers is always preserved.
/// </summary>
internal static class ManagedMarkdownBlock
{
    public const string Begin = "<!-- scrinia:begin v1 -->";
    public const string End = "<!-- scrinia:end -->";

    /// <summary>
    /// Returns the full file content with the managed block inserted (new/empty file), replaced
    /// in place (block already present), or appended (file has user content but no block).
    /// </summary>
    public static string Upsert(string? existing, string body)
    {
        string block = $"{Begin}\n{body.Trim()}\n{End}";

        if (string.IsNullOrWhiteSpace(existing))
            return block + "\n";

        int start = existing.IndexOf(Begin, StringComparison.Ordinal);
        if (start >= 0)
        {
            int endMarker = existing.IndexOf(End, start, StringComparison.Ordinal);
            if (endMarker >= 0)
            {
                int endOfBlock = endMarker + End.Length;
                return existing[..start] + block + existing[endOfBlock..];
            }
            // Begin without End — malformed; fall through and append a fresh block rather than
            // risk eating the rest of the file.
        }

        return existing.TrimEnd() + "\n\n" + block + "\n";
    }

    /// <summary>
    /// Returns the content with the managed block removed (surrounding blank lines tidied), or
    /// <c>null</c> when nothing but the block (and whitespace) remained — the caller deletes the
    /// file in that case. Content without a block is returned unchanged.
    /// </summary>
    public static string? Remove(string existing)
    {
        int start = existing.IndexOf(Begin, StringComparison.Ordinal);
        if (start < 0) return existing;

        int endMarker = existing.IndexOf(End, start, StringComparison.Ordinal);
        if (endMarker < 0) return existing;

        int endOfBlock = endMarker + End.Length;
        string remainder = (existing[..start] + existing[endOfBlock..]).Trim();
        return remainder.Length == 0 ? null : remainder + "\n";
    }

    /// <summary>True when <paramref name="content"/> contains a scrinia-managed block.</summary>
    public static bool Contains(string content) =>
        content.Contains(Begin, StringComparison.Ordinal);

    /// <summary>
    /// Extracts the managed body (text between the markers, trimmed) for drift detection, or null
    /// when no well-formed block is present.
    /// </summary>
    public static string? ExtractBody(string content)
    {
        int start = content.IndexOf(Begin, StringComparison.Ordinal);
        if (start < 0) return null;
        int bodyStart = start + Begin.Length;
        int endMarker = content.IndexOf(End, bodyStart, StringComparison.Ordinal);
        if (endMarker < 0) return null;
        return content[bodyStart..endMarker].Trim();
    }
}
