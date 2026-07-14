using System.Globalization;

namespace Scrinia.Core.Search;

/// <summary>
/// Read-side helpers that source display values from the attributes model (<c>Attributes</c> /
/// <c>ContentPreview</c>), falling back to the legacy per-memory fields for entries not yet
/// forward-filled (e.g. in-memory ephemeral entries the load-seam migration never sees).
/// Introduced by the P6.2 reader cutover; the legacy fallbacks are removed with the legacy
/// fields in P6.6.
/// </summary>
public static class AttributeReaders
{
    public readonly record struct ReviewStatus(bool IsStale, bool NeedsReview);

    /// <summary>
    /// Review status derived from <c>Attributes["review"]</c> (positional: an element in round-trip
    /// <c>"o"</c> DateTimeOffset format is the review date; any other non-blank element is the
    /// condition) when that key exists, else the legacy <paramref name="legacyReviewAfter"/> /
    /// <paramref name="legacyReviewWhen"/>. Mirrors the legacy predicate exactly:
    /// stale iff a review date is present and &lt;= <paramref name="now"/>; needs-review iff not
    /// stale and a condition is present.
    /// </summary>
    public static ReviewStatus GetReviewStatus(
        IReadOnlyDictionary<string, string[]>? attributes,
        DateTimeOffset? legacyReviewAfter,
        string? legacyReviewWhen,
        DateTimeOffset now)
    {
        DateTimeOffset? date;
        string? condition;

        if (attributes is not null
            && attributes.TryGetValue(KnownFields.ReviewKey, out var review)
            && review is not null)
        {
            date = null;
            condition = null;
            foreach (string token in review)
            {
                if (DateTimeOffset.TryParseExact(token, "o", CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var parsed))
                    date ??= parsed;
                else if (!string.IsNullOrWhiteSpace(token))
                    condition ??= token;
            }
        }
        else
        {
            date = legacyReviewAfter;
            condition = string.IsNullOrWhiteSpace(legacyReviewWhen) ? null : legacyReviewWhen;
        }

        bool isStale = date.HasValue && date.Value <= now;
        bool needsReview = !isStale && condition is not null;
        return new ReviewStatus(isStale, needsReview);
    }

    /// <summary>Description column / preview text: <c>ContentPreview</c> when present, else <c>Description</c>.</summary>
    public static string PreviewText(string? contentPreview, string description)
        => contentPreview ?? description;

    /// <summary>
    /// Tags for display/aggregation: <c>Attributes["tag"]</c> when that key exists, else the legacy
    /// <paramref name="legacyTags"/>. Never null.
    /// </summary>
    public static IReadOnlyList<string> DisplayTags(
        IReadOnlyDictionary<string, string[]>? attributes, string[]? legacyTags)
    {
        if (attributes is not null && attributes.TryGetValue(KnownFields.TagKey, out var tags) && tags is not null)
            return tags;
        return legacyTags ?? [];
    }

    /// <summary>
    /// Anchors for the <c>anchors:</c> display block: <c>Attributes["anchor"]</c> when that key exists,
    /// else the legacy <paramref name="legacyAnchors"/>. Never null (empty ⇒ caller omits the block).
    /// </summary>
    public static IReadOnlyList<string> DisplayAnchors(
        IReadOnlyDictionary<string, string[]>? attributes, string[]? legacyAnchors)
    {
        if (attributes is not null && attributes.TryGetValue(KnownFields.AnchorKey, out var anchors) && anchors is not null)
            return anchors;
        return legacyAnchors ?? [];
    }
}
