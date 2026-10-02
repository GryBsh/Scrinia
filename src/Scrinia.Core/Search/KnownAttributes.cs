namespace Scrinia.Core.Search;

/// <summary>
/// The single schema point for attributes — the ONLY place that knows which attribute keys
/// are multi-valued and what merge semantics apply. Replaces the three divergent typed-prefix
/// lists (EntityGraph.RealTypes stays as an alias view for graph node identity; the benchmark
/// and guide copies are retired). Entity keys feed the entity graph; reference keys feed
/// References/Link/orphan tracking and FileHashes hashing; everything else is scalar state.
/// </summary>
public static class KnownAttributes
{
    public const int MaxAttributes = 32;

    /// <summary>
    /// Maximum values retained under a single attribute key. Enforced by <see cref="Merge"/> — the
    /// one point every write path funnels through, so every write keeps the same values for the
    /// same input.
    ///
    /// <para>Sized to be unreachable from auto-extraction rather than to be tight. The previous
    /// transport bound of 64 was reachable from ROUTINE extraction: nothing caps <c>file</c> or
    /// <c>ref</c> values (unlike <c>mention</c>, capped at
    /// <see cref="AttributeExtractor.MaxMentionSpans"/>), and a memory citing 70 distinct paths —
    /// an ordinary shape for a code-heavy note — stored fine locally and was refused over the
    /// transport. Raising the
    /// bound alone would only move that cliff, and capping at 64 to match would start discarding
    /// file references from real content; 256 distinct values under one key is past what any
    /// extractor produces from a single memory, so this bounds storage growth without truncating
    /// anything a caller meant.</para>
    /// </summary>
    public const int MaxValuesPerKey = 256;

    /// <summary>Truncates a value array to <see cref="MaxValuesPerKey"/>, keeping the first values —
    /// the same "existing order is preserved" rule <see cref="Merge"/> applies to unions.</summary>
    private static string[] Cap(string[] values) =>
        values.Length > MaxValuesPerKey ? values[..MaxValuesPerKey] : values;

    /// <summary>Entity attribute keys — union-merge, graph-building. Same set as EntityGraph.RealTypes.</summary>
    public static readonly IReadOnlySet<string> EntityKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "person", "place", "org", "thing", "topic" };

    /// <summary>Reference attribute keys — union-merge, feed References/Link/orphan + FileHashes.</summary>
    public static readonly IReadOnlySet<string> ReferenceKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "file", "ref" };

    public const string ReviewKey = "review";

    /// <summary>The attribute key carrying the "no inbound references" marker
    /// (<c>MaintenanceEventSink</c>). Lives here, with the other named keys, because this is the
    /// schema point — the sink writes it but does not own what it means.</summary>
    public const string OrphanKey = "orphan";

    /// <summary>
    /// Attribute keys whose value is machine-set STATE, not the name of a thing.
    ///
    /// <para>The distinction is not cosmetic and this set is the one place it is expressed.
    /// <see cref="EntityGraph.ParseEntityAttributes"/> deliberately yields every attribute key, so
    /// that arbitrary keys (co-<c>file:</c>, co-<c>status:</c>) can form graph associations rather
    /// than only the five blessed <see cref="EntityKeys"/>. That is right for keys whose values
    /// NAME something — two memories sharing <c>status:done</c> really are related by it. It is
    /// wrong for a boolean marker: every orphan in a corpus shares the single value <c>true</c>, so
    /// <c>orphan:true</c> becomes one hub node linking every unrelated orphan to every other, and
    /// the hub grows as the corpus ages. Measured before this set existed: five semantically
    /// unrelated orphans each collected 12.14 entity-graph points on a query naming none of them,
    /// and because <c>EntityQuerySeeder</c> seeds on a node's value, the query "is that true" seeded
    /// the hub and scored every orphan 31.52–50.00 — while the query "orphan" scored nothing,
    /// because the seeder never looks at the key. Unreachable by its own name, reachable by a
    /// generic English word.</para>
    ///
    /// <para>Excluding them at <see cref="EntityGraph.ParseEntityAttributes"/> covers the graph, the
    /// PPR seeding built from its node keys, and <c>EntityFactDerivation</c> — all three read
    /// through that one function. It does NOT affect BM25: attribute keys and values still seed the
    /// term-frequency index, so a state marker stays searchable and stays available to
    /// <c>attributeFilter</c>. It is only barred from being treated as an ENTITY.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> StateMarkerKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { OrphanKey };

    /// <summary>True when <paramref name="key"/> carries machine-set state rather than the name of
    /// a thing — see <see cref="StateMarkerKeys"/>.</summary>
    public static bool IsStateMarker(string key) => StateMarkerKeys.Contains(key);

    /// <summary>The attribute key legacy free-form tags migrate to (see AttributeMigration.TagKey).</summary>
    public const string TagKey = "tag";

    /// <summary>The attribute key retrieval anchors migrate to — a searchable facet + display carrier.
    /// Anchors are TF-seeded into BM25 at write time; they are NOT folded into Keywords (that redundant
    /// fold was retired in P6.5).</summary>
    public const string AnchorKey = "anchor";

    /// <summary>The neutral key untyped prose entity spans are written under at write time
    /// (Piece 2). Multi-valued — a memory mentions several spans. The async typing sink promotes
    /// individual values from here into the typed entity keys; anything it cannot confidently type
    /// stays here and still aggregates (it is an ordinary attribute key, so the entity graph and
    /// EntityFactStore already treat each mention:value as an entity).</summary>
    public const string MentionKey = "mention";

    /// <summary>The guided attribute keys — what the guide teaches and agents write most.
    /// Guidance + the weight-table seed for freshly agent-supplied input: the keywords ARGUMENT
    /// (<see cref="AttributeExtractor.RouteKeywords"/>) treats any key uniformly there, because
    /// every element was typed on purpose.
    ///
    /// <para>It IS a gate on exactly TWO routes, and only those two: inline prose
    /// <c>key:value</c> routing (<see cref="AttributeExtractor"/>) and the legacy
    /// <c>key:value</c>-shaped-Keywords forward-fill
    /// (<see cref="Scrinia.Core.Migration.AttributeMigration.Forward"/>). Both read uncontrolled
    /// text — pasted logs, quoted code, transcripts, legacy free text — where an arbitrary key
    /// (<c>TODO:fix</c> in a pasted comment) must not mint an attribute.</para>
    ///
    /// <para><b>It is NOT a general gate on uncontrolled text.</b> The line-start
    /// <c>key: value</c> and <c>**Bold:**</c> label routes read the same uncontrolled content and
    /// are UNGATED — a line reading <c>TODO: cleanup</c> mints <c>todo</c> today. Their guard is
    /// shape, not vocabulary: a line-start position plus mandatory whitespace after the colon is
    /// deliberate-looking enough on its own, where a bare inline <c>key:value</c> anywhere in a
    /// sentence is not. Widening this set therefore changes those two routes and nothing
    /// else.</para></summary>
    public static readonly IReadOnlySet<string> RecommendedKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "person", "place", "org", "thing", "topic", "file", "ref", "review" };

    // Per-attribute-key scoring/influence multiplier. This is the curation mechanism that
    // replaces the old "only EntityKeys graph" gate: a soft, tunable knob. Seeded at parity
    // (every key 1.0) so it changes no existing score today; tune values later on
    // real-workflow signal, never a benchmark fit. Consumed by WeightedAttributeScorer now and
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
        EntityKeys.Contains(key)
        || ReferenceKeys.Contains(key)
        || string.Equals(key, MentionKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>Canonicalizes an agent-supplied attribute map before it is merged: lowercases and
    /// trims keys, trims values, drops blank keys/values and any key left with no values. Null when
    /// nothing survives. Matches the Attributes convention (AttributeExtractor lowercases keys).
    ///
    /// <para>Every write path calls this before merging caller-supplied values, so the same map
    /// produces the same attributes regardless of which facade supplied it. It lives here, next to
    /// <see cref="Merge"/>, so a second copy cannot drift.</para></summary>
    public static Dictionary<string, string[]>? Normalize(IReadOnlyDictionary<string, string[]>? attributes)
    {
        if (attributes is not { Count: > 0 }) return null;
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in attributes)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null) continue;
            var vals = kv.Value.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToArray();
            if (vals.Length == 0) continue;
            result[kv.Key.Trim().ToLowerInvariant()] = vals;
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Merges incoming attributes over existing per the key-class semantics: multi-valued keys
    /// union-dedupe (existing values keep their order); scalar keys are replaced by the
    /// incoming value array (newer-wins — first-wins would freeze stale statuses).
    /// Existing keys count first against the <see cref="MaxAttributes"/> cap. Null when both empty.
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
                if (merged.Count >= MaxAttributes) break;
                merged[kv.Key] = Cap(kv.Value);
            }
        }
        if (incoming is not null)
        {
            foreach (var kv in incoming)
            {
                if (merged.TryGetValue(kv.Key, out var current))
                {
                    merged[kv.Key] = Cap(IsMultiValued(kv.Key)
                        ? current.Union(kv.Value, StringComparer.OrdinalIgnoreCase).ToArray()
                        : kv.Value);
                }
                else if (merged.Count < MaxAttributes)
                {
                    merged[kv.Key] = Cap(kv.Value);
                }
            }
        }
        return merged;
    }
}
