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

    public const string ReviewKey = "review";

    /// <summary>The attribute key legacy free-form tags migrate to (see AttributeMigration.TagKey).</summary>
    public const string TagKey = "tag";

    /// <summary>The attribute key retrieval anchors migrate to (display + searchable facet).
    /// Anchors also remain folded into Keywords additively; the redundant fold is retired in P6.5.</summary>
    public const string AnchorKey = "anchor";

    /// <summary>The guided attribute keys — what the guide teaches and agents write most.
    /// The engine treats any key uniformly; this set is guidance + the weight-table seed, not a gate.</summary>
    public static readonly IReadOnlySet<string> RecommendedKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "person", "place", "org", "thing", "topic", "file", "ref", "review" };

    // Per-attribute-key scoring/influence multiplier. This is the curation mechanism that
    // replaces the old "only EntityKeys graph" gate: a soft, tunable knob. Seeded at parity
    // (every key 1.0) so it changes no existing score today; tune values later on
    // real-workflow signal, never a benchmark fit. Consumed by WeightedFieldScorer now and
    // by the entity graph in its separate tuning effort.
    private static readonly Dictionary<string, double> AttributeWeights =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            // (intentionally empty at parity — add entries here to tune, e.g. ["person"] = 1.5)
        };

    public static double AttributeWeight(string key) =>
        AttributeWeights.TryGetValue(key, out double w) ? w : 1.0;

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
