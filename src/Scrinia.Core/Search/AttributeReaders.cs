using System.Globalization;

namespace Scrinia.Core.Search;

/// <summary>
/// Read-side helpers that source display values from the attributes model (<c>Attributes</c> /
/// <c>ContentPreview</c>). Introduced by the P6.2 reader cutover.
/// </summary>
public static class AttributeReaders
{
    public readonly record struct ReviewStatus(bool IsStale, bool NeedsReview);

    /// <summary>
    /// Extracts the raw review date/condition from <c>Attributes["review"]</c> (positional: an
    /// element in round-trip <c>"o"</c> DateTimeOffset format is the review date; any other
    /// non-blank element is the condition). Returns <see langword="true"/> when either was found.
    /// </summary>
    public static bool TryReadReview(
        IReadOnlyDictionary<string, string[]>? attributes,
        out DateTimeOffset? date, out string? condition)
    {
        date = null;
        condition = null;
        if (attributes is not null
            && attributes.TryGetValue(KnownAttributes.ReviewKey, out var review)
            && review is not null)
        {
            foreach (string token in review)
            {
                if (DateTimeOffset.TryParseExact(token, "o", CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var parsed))
                    date ??= parsed;
                else if (!string.IsNullOrWhiteSpace(token))
                    condition ??= token;
            }
        }
        return date is not null || condition is not null;
    }

    /// <summary>
    /// Review status derived from <c>Attributes["review"]</c> via <see cref="TryReadReview"/>.
    /// Stale iff a review date is present and &lt;= <paramref name="now"/>; needs-review iff not
    /// stale and a condition is present.
    /// </summary>
    public static ReviewStatus GetReviewStatus(
        IReadOnlyDictionary<string, string[]>? attributes, DateTimeOffset now)
    {
        TryReadReview(attributes, out var date, out var condition);
        bool isStale = date.HasValue && date.Value <= now;
        bool needsReview = !isStale && condition is not null;
        return new ReviewStatus(isStale, needsReview);
    }

    /// <summary>Description column / preview text: <c>ContentPreview</c> when present, else empty.</summary>
    public static string PreviewText(string? contentPreview) => contentPreview ?? string.Empty;

    /// <summary>Tags for display/aggregation: <c>Attributes["tag"]</c> when that key exists. Never null.</summary>
    public static IReadOnlyList<string> DisplayTags(IReadOnlyDictionary<string, string[]>? attributes)
    {
        if (attributes is not null && attributes.TryGetValue(KnownAttributes.TagKey, out var tags) && tags is not null)
            return tags;
        return [];
    }

    /// <summary>
    /// Anchors for the <c>anchors:</c> display block: <c>Attributes["anchor"]</c> when that key
    /// exists. Never null (empty ⇒ caller omits the block).
    /// </summary>
    public static IReadOnlyList<string> DisplayAnchors(IReadOnlyDictionary<string, string[]>? attributes)
    {
        if (attributes is not null && attributes.TryGetValue(KnownAttributes.AnchorKey, out var anchors) && anchors is not null)
            return anchors;
        return [];
    }
}
