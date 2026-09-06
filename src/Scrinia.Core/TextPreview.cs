namespace Scrinia.Core;

/// <summary>
/// Pure string helper for content previews. Previously a member of <see cref="IMemoryStore"/>;
/// now a static helper so callers do not need a store to truncate text.
/// Alias to <see cref="ContentPreview"/> for N8 compatibility.
/// </summary>
public static class TextPreview
{
    /// <summary>
    /// Truncates <paramref name="content"/> to <paramref name="maxLength"/> characters.
    /// Pure function — no store state, no side effects.
    /// </summary>
    public static string GenerateContentPreview(string content, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(content)) return "";
        if (content.Length <= maxLength) return content;
        int cut = content.LastIndexOf(' ', maxLength - 3);
        if (cut <= maxLength / 2) cut = maxLength - 3;
        return content[..cut] + "...";
    }

    /// <summary>Alias for <see cref="ContentPreview.Generate"/>.</summary>
    public static string Generate(string content, int maxLength = 500) =>
        ContentPreview.Generate(content, maxLength);
}
