namespace Scrinia.Core.Search;

/// <summary>
/// The single schema point for unified fields — the ONLY place that knows which field keys
/// are multi-valued and what merge semantics apply. Replaces the three divergent typed-prefix
/// lists (EntityGraph.RealTypes stays as an alias view for graph node identity; the benchmark
/// and guide copies are retired). Entity keys feed the entity graph; reference keys feed
/// References/Link/orphan tracking and CodeRefs hashing; everything else is scalar state.
/// </summary>
public static class KnownFields
{
    public const int MaxFields = 32;

    /// <summary>Entity field keys — union-merge, graph-building. Same set as EntityGraph.RealTypes.</summary>
    public static readonly IReadOnlySet<string> EntityKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "person", "place", "org", "thing", "topic" };

    /// <summary>Reference field keys — union-merge, feed References/Link/orphan + CodeRefs.</summary>
    public static readonly IReadOnlySet<string> ReferenceKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "file", "ref" };

    /// <summary>Multi-valued keys accumulate across appends; everything else is scalar newer-wins.</summary>
    public static bool IsMultiValued(string key) =>
        EntityKeys.Contains(key) || ReferenceKeys.Contains(key);

    /// <summary>
    /// Merges incoming fields over existing per the key-class semantics: multi-valued keys
    /// union-dedupe (existing values keep their order); scalar keys are replaced by the
    /// incoming value array (newer-wins — first-wins would freeze stale statuses).
    /// Existing keys count first against the <see cref="MaxFields"/> cap. Null when both empty.
    /// </summary>
    public static Dictionary<string, string[]>? Merge(
        Dictionary<string, string[]>? existing,
        Dictionary<string, string[]>? incoming)
    {
        if (existing is not { Count: > 0 } && incoming is not { Count: > 0 })
            return null;

        var merged = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (existing is not null)
        {
            foreach (var kv in existing)
            {
                if (merged.Count >= MaxFields) break;
                merged[kv.Key] = kv.Value;
            }
        }
        if (incoming is not null)
        {
            foreach (var kv in incoming)
            {
                if (merged.TryGetValue(kv.Key, out var current))
                {
                    merged[kv.Key] = IsMultiValued(kv.Key)
                        ? current.Union(kv.Value, StringComparer.OrdinalIgnoreCase).ToArray()
                        : kv.Value;
                }
                else if (merged.Count < MaxFields)
                {
                    merged[kv.Key] = kv.Value;
                }
            }
        }
        return merged;
    }
}
