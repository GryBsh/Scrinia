namespace Scrinia.Core.Resilience;

/// <summary>
/// The one place an HTTP error body is shortened for display. Four call sites carried this
/// rule verbatim — three chat providers and the embedding provider — and they had already
/// drifted: the embedding provider omitted the ellipsis. Bodies can be megabytes of HTML from
/// a proxy, so the cap protects logs and error envelopes alike.
/// </summary>
public static class HttpErrorBody
{
    /// <summary>Maximum characters of a provider error body worth surfacing.</summary>
    public const int MaxChars = 500;

    /// <summary>Returns <paramref name="body"/> unchanged, or its first
    /// <see cref="MaxChars"/> characters followed by an ellipsis.</summary>
    public static string Truncate(string body) =>
        body.Length > MaxChars ? body[..MaxChars] + "..." : body;
}
