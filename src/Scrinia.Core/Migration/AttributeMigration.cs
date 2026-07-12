using System.Globalization;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Core.Migration;

/// <summary>
/// Forward-fills the legacy memory carriers into the attributes model: tags→`tag`,
/// codeRefs paths→`file`, reviewAfter/reviewWhen→`review` (ISO date + condition text),
/// anchors→keywords. ADDITIVE — legacy fields are left populated so the legacy read/scoring
/// paths keep working; clearing them and removing the model fields is the cleanup plan.
/// Deterministic and idempotent: re-running produces no change once an entry is forward-filled.
/// </summary>
public static class AttributeMigration
{
    /// <summary>The attribute key legacy free-form tags migrate to.</summary>
    public const string TagKey = "tag";

    public static ArtifactEntry Forward(ArtifactEntry entry)
    {
        var incoming = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (entry.Tags is { Length: > 0 })
            incoming[TagKey] = entry.Tags;
        if (entry.CodeRefs is { Count: > 0 })
            incoming["file"] = entry.CodeRefs.Keys.ToArray();
        var review = new List<string>(2);
        if (entry.ReviewAfter is { } ra)
            review.Add(ra.ToString("o", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(entry.ReviewWhen))
            review.Add(entry.ReviewWhen!);
        if (review.Count > 0)
            incoming[KnownFields.ReviewKey] = review.ToArray();

        var mergedFields = incoming.Count > 0 ? KnownFields.Merge(entry.Fields, incoming) : entry.Fields;

        string[]? mergedKeywords = entry.Keywords;
        if (entry.RetrievalAnchors is { Length: > 0 })
            mergedKeywords = (entry.Keywords ?? [])
                .Concat(entry.RetrievalAnchors)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        return entry with { Fields = mergedFields, Keywords = mergedKeywords };
    }

    /// <summary>True iff Forward(entry) changes the entry's Fields or Keywords (the write-or-skip
    /// decision). Set-equal comparison (order-insensitive) keeps re-runs write-free.</summary>
    public static bool MigratesTo(ArtifactEntry entry, out ArtifactEntry migrated)
    {
        migrated = Forward(entry);
        return !FieldsSetEqual(entry.Fields, migrated.Fields)
            || !SetEqual(entry.Keywords, migrated.Keywords);
    }

    private static bool SetEqual(string[]? a, string[]? b)
    {
        var sa = a is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                           : new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        var sb = b is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                           : new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);
        return sa.SetEquals(sb);
    }

    private static bool FieldsSetEqual(
        Dictionary<string, string[]>? a, Dictionary<string, string[]>? b)
    {
        int ca = a?.Count ?? 0, cb = b?.Count ?? 0;
        if (ca != cb) return false;
        if (a is null || b is null) return ca == 0 && cb == 0;
        foreach (var (k, va) in a)
        {
            if (!b.TryGetValue(k, out var vb)) return false;
            if (!SetEqual(va, vb)) return false;
        }
        return true;
    }
}
