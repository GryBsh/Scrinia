using System.Globalization;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Core.Migration;

/// <summary>
/// Forward-fills the legacy memory carriers into the attributes model: tags→`tag`,
/// FileHashes paths→`file`, reviewAfter/reviewWhen→`review` (ISO date + condition text),
/// anchors→`anchor`. ADDITIVE — legacy fields are left populated so the legacy read/scoring
/// paths keep working; clearing them and removing the model fields is the cleanup plan.
/// Also folds recognized-key <c>key:value</c>-shaped legacy keywords (e.g. <c>person:maria</c>,
/// <c>ref:other-note</c>) into <c>Fields[key]</c> whenever <c>key</c> is one of
/// <see cref="KnownFields.RecommendedKeys"/> — this mirrors the scorer's dual-read
/// <c>key:value</c>-in-Keywords parse so that parse can eventually be retired without
/// regressing pre-unification entries. The keyword itself is left in <c>Keywords</c>
/// unchanged (additive, not a rewrite). Keywords whose key is NOT a recognized attribute
/// key (e.g. provenance markers like <c>basedOn:skill-x</c>) are intentionally left as
/// plain keywords — they are not searchable facets and must not pollute the attribute
/// namespace. Also backfills <c>ContentPreview</c> from <c>Description</c> when <c>ContentPreview</c> is null,
/// so pre-<c>ContentPreview</c> sidecars keep their preview text once <c>Description</c> is removed.
/// Deterministic and idempotent: re-running produces no change once an entry
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
        if (entry.FileHashes is { Count: > 0 })
            incoming["file"] = entry.FileHashes.Keys.ToArray();
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
        // BM25 is TF-seeded from RetrievalAnchors at write time, so anchors are searchable without
        // also folding into Keywords (that redundant fold was retired in P6.5). Cleaned with
        // whitespace-drop + dedupe.
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

        // Description → ContentPreview: ContentPreview is the successor preview/display carrier
        // (Description is removed in P6.6b). New writes populate ContentPreview directly, but
        // sidecars created before ContentPreview existed carry only Description — backfill it here,
        // and ONLY when ContentPreview is null, so both the read-time forward-fill and `scri migrate`
        // rescue their preview text before Description is deleted. Idempotent: once set, `??`
        // short-circuits on re-run. A blank Description is not preview-worthy and stays null.
        string? mergedContentPreview = entry.ContentPreview
            ?? (string.IsNullOrWhiteSpace(entry.Description) ? null : entry.Description);

        return entry with
        {
            Fields = mergedFields,
            Keywords = mergedKeywords,
            ContentPreview = mergedContentPreview,
        };
    }

    /// <summary>True iff Forward(entry) changes the entry's Fields, Keywords, or ContentPreview (the write-or-skip
    /// decision). Set-equal comparison (order-insensitive) keeps re-runs write-free.</summary>
    public static bool MigratesTo(ArtifactEntry entry, out ArtifactEntry migrated)
    {
        migrated = Forward(entry);
        return !FieldsSetEqual(entry.Fields, migrated.Fields)
            || !SetEqual(entry.Keywords, migrated.Keywords)
            // Ordinal (case-sensitive) is intentional here: ContentPreview is verbatim display text, so a
            // case-only change is a real change — unlike the OrdinalIgnoreCase set comparisons above, which
            // treat Fields/Keywords as case-insensitive facet tokens.
            || !string.Equals(entry.ContentPreview, migrated.ContentPreview, StringComparison.Ordinal);
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

    /// <summary>Forward-fills every persistent entry across all scopes, reporting an accurate count.
    /// Reads each sidecar RAW (via <see cref="FileMemoryStore.EnumerateRawForMigration"/>) — bypassing the
    /// read-time forward-fill write-back — so the count reflects entries that actually needed migrating and
    /// <paramref name="dryRun"/> writes nothing. Legacy-flat-dir entries are persisted back to their own dir
    /// (no scope promotion). Idempotent: a re-run over upgraded sidecars reports 0.</summary>
    public static MigrateSummary MigrateStore(FileMemoryStore store, bool dryRun = false, Action<int, int>? onProgress = null)
    {
        var items = store.EnumerateRawForMigration().ToList();
        int total = items.Count, migrated = 0, done = 0;
        foreach (var (entry, scope, storeDir) in items)
        {
            if (MigratesTo(entry, out var next))
            {
                // PersistMigratedEntry locks on GetLockPath(scope), and FileLock.Acquire creates that
                // lock's parent dir (GetStoreDirForScope(scope)) as a side effect — so an empty namespaced
                // dir may be materialized here even for a flat-dir write. Harmless: EnumerateRawForMigration
                // only ever emits a scope whose namespaced dir already exists.
                if (!dryRun) store.PersistMigratedEntry(next, storeDir, scope);
                migrated++;
            }
            onProgress?.Invoke(++done, total);
        }
        return new MigrateSummary(total, migrated, total - migrated);
    }
}
