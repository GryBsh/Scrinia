using System.Globalization;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Core.Migration;

/// <summary>
/// Forward-fills the legacy memory carriers into the attributes model: tags→`tag`,
/// codeRefs paths→`file`, reviewAfter/reviewWhen→`review` (ISO date + condition text),
/// anchors→keywords. ADDITIVE — legacy fields are left populated so the legacy read/scoring
/// paths keep working; clearing them and removing the model fields is the cleanup plan.
/// Also folds recognized-key <c>key:value</c>-shaped legacy keywords (e.g. <c>person:maria</c>,
/// <c>ref:other-note</c>) into <c>Fields[key]</c> whenever <c>key</c> is one of
/// <see cref="KnownFields.RecommendedKeys"/> — this mirrors the scorer's dual-read
/// <c>key:value</c>-in-Keywords parse so that parse can eventually be retired without
/// regressing pre-unification entries. The keyword itself is left in <c>Keywords</c>
/// unchanged (additive, not a rewrite). Keywords whose key is NOT a recognized attribute
/// key (e.g. provenance markers like <c>basedOn:skill-x</c>) are intentionally left as
/// plain keywords — they are not searchable facets and must not pollute the attribute
/// namespace. Deterministic and idempotent: re-running produces no change once an entry
/// is forward-filled.
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

        if (entry.Keywords is { Length: > 0 })
        {
            Dictionary<string, List<string>>? kwFields = null;
            foreach (string kw in entry.Keywords)
            {
                int sep = kw.IndexOf(':');
                if (sep <= 0 || sep >= kw.Length - 1) continue;
                // Canonicalize the KEY to lowercase (mirrors FieldExtractor.RouteKeywords) so the
                // persisted Fields key survives a JSON round-trip that drops the OrdinalIgnoreCase
                // comparer; RecommendedKeys matching is case-insensitive so this is safe.
                string key = kw[..sep].ToLowerInvariant();
                if (!KnownFields.RecommendedKeys.Contains(key)) continue;
                kwFields ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                if (!kwFields.TryGetValue(key, out var list))
                    kwFields[key] = list = new List<string>();
                // value kept verbatim (no length/URL guard) to preserve scorer key:value parity; canonicalize the KEY only
                string value = kw[(sep + 1)..];
                if (!list.Contains(value, StringComparer.OrdinalIgnoreCase))
                    list.Add(value);
            }
            if (kwFields is not null)
                foreach (var (k, v) in kwFields)
                    incoming[k] = incoming.TryGetValue(k, out var existing)
                        ? existing.Union(v, StringComparer.OrdinalIgnoreCase).ToArray()
                        : v.ToArray();
        }

        // Anchors → Fields["anchor"]: a first-class searchable facet + the display carrier P6.2 reads.
        // Additive — anchors also stay folded into Keywords below (keyword channel unchanged; the
        // redundant fold is retired in P6.5). Cleaned identically to the fold (whitespace + dedupe).
        if (entry.RetrievalAnchors is { Length: > 0 })
        {
            var cleanAnchors = entry.RetrievalAnchors
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (cleanAnchors.Length > 0)
                incoming[KnownFields.AnchorKey] = cleanAnchors;
        }

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

    /// <summary>Summary of a <see cref="MigrateStore"/> run: how many entries were seen, forward-filled, and left untouched.</summary>
    public readonly record struct MigrateSummary(int Total, int Migrated, int Skipped);

    /// <summary>Forward-fills every persistent entry across all scopes. Idempotent: only entries
    /// whose Fields/Keywords actually change are written. <c>ListScoped("all")</c> reaches entity-classified
    /// scopes that the default selector excludes; ephemeral entries are in-memory-only, so a fresh
    /// migrate process sees none. Pass the <see cref="IMemoryStore"/> instance backing persistent storage
    /// (e.g. the workspace's <c>FileMemoryStore</c>) — the static facade's topic discovery is v1-only.</summary>
    public static MigrateSummary MigrateStore(IMemoryStore store, bool dryRun = false, Action<int, int>? onProgress = null)
    {
        var all = store.ListScoped("all");
        int total = all.Count, migrated = 0, done = 0;
        foreach (var item in all)
        {
            if (MigratesTo(item.Entry, out var next))
            {
                if (!dryRun) store.Upsert(next, item.Scope);
                migrated++;
            }
            onProgress?.Invoke(++done, total);
        }
        return new MigrateSummary(total, migrated, total - migrated);
    }
}
