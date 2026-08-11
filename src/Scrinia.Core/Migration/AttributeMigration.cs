using System.Globalization;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Core.Migration;

/// <summary>
/// Forward-fills the surviving member-sourced legacy carrier — FileHashes paths→`file` — into the
/// attributes model. ADDITIVE — legacy fields are left populated so the legacy read/scoring
/// paths keep working; clearing them and removing the model fields is the cleanup plan.
/// Also folds recognized-key <c>key:value</c>-shaped legacy keywords (e.g. <c>person:maria</c>,
/// <c>ref:other-note</c>) into <c>Attributes[key]</c> whenever <c>key</c> is one of
/// <see cref="KnownAttributes.RecommendedKeys"/> — this mirrors the scorer's dual-read
/// <c>key:value</c>-in-Keywords parse so that parse can eventually be retired without
/// regressing pre-unification entries. The keyword itself is left in <c>Keywords</c>
/// unchanged (additive, not a rewrite). Keywords whose key is NOT a recognized attribute
/// key (e.g. provenance markers like <c>basedOn:skill-x</c>) are intentionally left as
/// plain keywords — they are not searchable facets and must not pollute the attribute
/// namespace.
/// <see cref="Forward"/> also coalesces a legacy JSON <c>"fields"</c>-key value into
/// <c>Attributes</c> (via <see cref="ArtifactEntry.NormalizeAttributesKey"/>) as its first step,
/// so a not-yet-rewritten legacy sidecar is forward-filled from its coalesced Attributes and its
/// returned entry re-serializes under <c>"attributes"</c> only.
/// Deterministic and idempotent: re-running produces no change once an entry
/// is forward-filled.
/// </summary>
public static class AttributeMigration
{
    /// <summary>The attribute key legacy free-form tags migrate to.</summary>
    public const string TagKey = "tag";

    /// <summary>The single source of truth for folding the write-time carrier inputs
    /// (tags/file-hashes/review/anchors) into attribute keys. Both <see cref="Forward"/> and the
    /// attributes-native write paths call this so the write-time fold is byte-identical to the read-time
    /// fold. Returns only keys with non-empty input. Excludes the key:value-Keywords fold (Forward-only).</summary>
    internal static Dictionary<string, string[]> BuildCarrierAttributes(
        string[]? tags, IEnumerable<string>? fileKeys,
        DateTimeOffset? reviewAfter, string? reviewWhen, string[]? anchors)
    {
        var incoming = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (tags is { Length: > 0 })
            incoming[TagKey] = tags;
        if (fileKeys is not null)
        {
            var keys = fileKeys.ToArray();
            if (keys.Length > 0) incoming["file"] = keys;
        }
        var review = new List<string>(2);
        if (reviewAfter is { } ra)
            review.Add(ra.ToString("o", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(reviewWhen))
            review.Add(reviewWhen!);
        if (review.Count > 0)
            incoming[KnownAttributes.ReviewKey] = review.ToArray();
        if (anchors is { Length: > 0 })
        {
            var cleanAnchors = anchors
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (cleanAnchors.Length > 0) incoming[KnownAttributes.AnchorKey] = cleanAnchors;
        }
        return incoming;
    }

    public static ArtifactEntry Forward(ArtifactEntry entry)
    {
        entry = entry.NormalizeAttributesKey();

        var incoming = BuildCarrierAttributes(
            tags: null, entry.FileHashes?.Keys, reviewAfter: null, reviewWhen: null, anchors: null);

        if (entry.Keywords is { Length: > 0 })
        {
            Dictionary<string, List<string>>? kwAttributes = null;
            foreach (string kw in entry.Keywords)
            {
                int sep = kw.IndexOf(':');
                if (sep <= 0 || sep >= kw.Length - 1) continue;
                // Canonicalize the KEY to lowercase (mirrors AttributeExtractor.RouteKeywords) so the
                // persisted Attributes key survives a JSON round-trip that drops the OrdinalIgnoreCase
                // comparer; RecommendedKeys matching is case-insensitive so this is safe.
                string key = kw[..sep].ToLowerInvariant();
                if (!KnownAttributes.RecommendedKeys.Contains(key)) continue;
                kwAttributes ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                if (!kwAttributes.TryGetValue(key, out var list))
                    kwAttributes[key] = list = new List<string>();
                // value kept verbatim (no length/URL guard) to preserve scorer key:value parity; canonicalize the KEY only
                string value = kw[(sep + 1)..];
                if (!list.Contains(value, StringComparer.OrdinalIgnoreCase))
                    list.Add(value);
            }
            if (kwAttributes is not null)
                foreach (var (k, v) in kwAttributes)
                    incoming[k] = incoming.TryGetValue(k, out var existing)
                        ? existing.Union(v, StringComparer.OrdinalIgnoreCase).ToArray()
                        : v.ToArray();
        }

        var mergedAttributes = incoming.Count > 0 ? KnownAttributes.Merge(entry.Attributes, incoming) : entry.Attributes;

        string[]? mergedKeywords = entry.Keywords;

        return entry with
        {
            Attributes = mergedAttributes,
            Keywords = mergedKeywords,
        };
    }

    /// <summary>True iff Forward(entry) changes the entry's Attributes, Keywords, or ContentPreview (the write-or-skip
    /// decision). Set-equal comparison (order-insensitive) keeps re-runs write-free.</summary>
    public static bool MigratesTo(ArtifactEntry entry, out ArtifactEntry migrated)
    {
        migrated = Forward(entry);
        return !AttributesSetEqual(entry.Attributes, migrated.Attributes)
            || !SetEqual(entry.Keywords, migrated.Keywords)
            // Ordinal (case-sensitive) is intentional here: ContentPreview is verbatim display text, so a
            // case-only change is a real change — unlike the OrdinalIgnoreCase set comparisons above, which
            // treat Attributes/Keywords as case-insensitive facet tokens.
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

    private static bool AttributesSetEqual(
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
                // PersistMigratedEntry locks on GetLockPath(scope), and FileLock.AcquireExclusive creates
                // that lock's parent dir (GetStoreDirForScope(scope)) as a side effect — so an empty
                // namespaced dir may be materialized here even for a flat-dir write. Harmless:
                // EnumerateRawForMigration only ever emits a scope whose namespaced dir already exists.
                if (!dryRun) store.PersistMigratedEntry(next, storeDir, scope);
                migrated++;
            }
            onProgress?.Invoke(++done, total);
        }
        return new MigrateSummary(total, migrated, total - migrated);
    }
}
