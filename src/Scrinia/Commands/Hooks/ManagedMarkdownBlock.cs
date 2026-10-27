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
    private const string BeginPrefix = "<!-- scrinia:begin";
    private const string EndPrefix = "<!-- scrinia:end";

    /// <summary>
    /// Produces the full file content with the managed block inserted, replaced, or appended.
    /// Returns false for ambiguous or unsupported markers, leaving the content unchanged.
    /// </summary>
    public static bool TryUpsert(string? existing, string body, out string updated)
    {
        updated = existing ?? "";
        if (!TryFindBlock(updated, out int start, out int endMarker))
            return false;

        string block = $"{Begin}\n{body.Trim()}\n{End}";

        if (string.IsNullOrWhiteSpace(existing))
        {
            updated = block + "\n";
        }
        else if (start >= 0)
        {
            int endOfBlock = endMarker + End.Length;
            updated = existing[..start] + block + existing[endOfBlock..];
        }
        else
        {
            updated = existing.TrimEnd() + "\n\n" + block + "\n";
        }
        return true;
    }

    /// <summary>
    /// Produces the content with the managed block removed (surrounding blank lines tidied), or
    /// <c>null</c> when nothing but the block (and whitespace) remained — the caller deletes the
    /// file in that case. Content without a block is returned unchanged. Returns false for
    /// ambiguous or unsupported markers without modifying the content.
    /// </summary>
    public static bool TryRemove(string existing, out string? remaining)
    {
        remaining = existing;
        if (!TryFindBlock(existing, out int start, out int endMarker))
            return false;
        if (start < 0) return true;

        int endOfBlock = endMarker + End.Length;
        string remainder = (existing[..start] + existing[endOfBlock..]).Trim();
        remaining = remainder.Length == 0 ? null : remainder + "\n";
        return true;
    }

    /// <summary>True when content contains any managed marker, including malformed markers.</summary>
    public static bool Contains(string content) =>
        content.Contains(BeginPrefix, StringComparison.Ordinal)
        || content.Contains(EndPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Extracts the managed body (text between the markers, trimmed) for drift detection, or null
    /// when no well-formed block is present.
    /// </summary>
    public static string? ExtractBody(string content)
    {
        if (!TryFindBlock(content, out int start, out int endMarker) || start < 0)
            return null;
        int bodyStart = start + Begin.Length;
        return content[bodyStart..endMarker].Trim();
    }

    // Validate every marker before identifying any removable region. In particular, an orphaned
    // opening marker must never acquire a closing marker from a later installation.
    private static bool TryFindBlock(string content, out int start, out int endMarker)
    {
        start = content.IndexOf(BeginPrefix, StringComparison.Ordinal);
        endMarker = content.IndexOf(EndPrefix, StringComparison.Ordinal);
        if (start < 0 && endMarker < 0) return true;
        if (start < 0 || endMarker < start) return false;

        return content.AsSpan(start).StartsWith(Begin, StringComparison.Ordinal)
            && content.AsSpan(endMarker).StartsWith(End, StringComparison.Ordinal)
            && content.IndexOf(BeginPrefix, start + BeginPrefix.Length, StringComparison.Ordinal) < 0
            && content.IndexOf(EndPrefix, endMarker + EndPrefix.Length, StringComparison.Ordinal) < 0;
    }
}
