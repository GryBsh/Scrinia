namespace Scrinia.Core;

/// <summary>
/// Content preview truncation. A pure string function — it was previously a member of
/// <see cref="IMemoryStore"/>, where both implementations carried byte-identical bodies
/// and neither touched any store state.
/// </summary>
public static class ContentPreview
{
    /// <summary>
    /// Truncates <paramref name="content"/> to <paramref name="maxLength"/> characters and
    /// flattens newlines to spaces, so the result is safe to place in a single-line column.
    /// </summary>
    public static string Generate(string content, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(content)) return "";
        string preview = content[..Math.Min(maxLength, content.Length)];
        return preview.Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
