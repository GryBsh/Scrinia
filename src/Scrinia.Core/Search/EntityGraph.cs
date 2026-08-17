namespace Scrinia.Core.Search;

/// <summary>
/// Static parsers that turn a memory's key:value keywords and attributes into
/// <c>(type, value)</c> entity-graph node identities. Node identity is uniform: every
/// <c>(type, value)</c> pair is its own <c>type:value</c> node, distinct by construction — a
/// value shared across unrelated keys (<c>status:done</c> vs <c>outcome:done</c>;
/// <c>person:paris</c> vs <c>place:paris</c>) is never one node, so unrelated keys never
/// falsely cross-link on a shared value.
///
/// <para>These parsers are the choke point <see cref="EntityFactDerivation"/> reads through to
/// build the live <see cref="EntityFactStore"/>, which <see cref="EntityGraphScoreContributor"/>
/// scores via <see cref="EntityFactStore.ScoringAdjacency"/> and <see cref="PersonalizedPageRank"/>
/// — the graph-building and PPR-walk implementation this type used to carry directly. That
/// instance side (Build/Score/AddDoc) was deleted as dead code once the store-backed path
/// replaced it in production; only the parsers below are still live.</para>
/// </summary>
public static class EntityGraph
{
    /// <summary>
    /// The key:value keyword markers <see cref="ParseEntityKeywords"/> recognizes as entity types
    /// (person/place/org/thing/topic). The legacy untyped <c>entity:</c> marker is NOT in this
    /// set — <see cref="ParseEntityKeywords"/> keeps it as its own type <c>"entity"</c> instead.
    /// </summary>
    public static readonly IReadOnlySet<string> RealTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "person", "place", "org", "thing", "topic" };

    /// <summary>
    /// Parses key:value keyword tags into <c>(type, value)</c> pairs — the graph's node source.
    /// Recognizes the <see cref="RealTypes"/> (person/place/org/thing/topic) plus the legacy
    /// untyped <c>entity:</c> marker (kept as type <c>"entity"</c> so pre-typing data still
    /// graphs; it never participates in a type conflict). Both type and value are lowercased;
    /// the value is kept whole so multi-word entities stay single nodes. Prefix detection mirrors
    /// <see cref="TextAnalysis.IndexTokensForKeyword"/>. Shared by the production contributor and
    /// the bench so "what counts as a graph tag" has one definition.
    /// </summary>
    public static IEnumerable<(string Type, string Value)> ParseEntityKeywords(IEnumerable<string>? keywords)
    {
        if (keywords is null) yield break;
        foreach (string kw in keywords)
        {
            if (string.IsNullOrEmpty(kw)) continue;
            int colon = kw.IndexOf(':');
            if (colon <= 0 || colon >= kw.Length - 1) continue;
            string type = kw[..colon].Trim().ToLowerInvariant();
            if (!RealTypes.Contains(type) && type != "entity") continue;
            string value = kw[(colon + 1)..].Trim().ToLowerInvariant();
            if (value.Length > 0) yield return (type, value);
        }
    }

    /// <summary>
    /// Attributes-side sibling of <see cref="ParseEntityKeywords"/>: yields (key, value) pairs for
    /// <b>every</b> attribute key, uniformly — not just <c>KnownAttributes.EntityKeys</c> — so any
    /// attribute (e.g. co-<c>file:</c>/co-<c>status:</c> memories) can become a graph
    /// association, not only the five blessed entity keys. Type is the lowercased attribute key,
    /// values lowercased and kept whole. Together with <see cref="ParseEntityKeywords"/> they
    /// give the store its dual-read: legacy key:value keywords ∪ unified attributes.
    ///
    /// <para><b>Every key except a state marker.</b> "Uniformly, not just the blessed entity keys"
    /// is right for keys whose values NAME something — two memories sharing <c>status:done</c> are
    /// genuinely related by it. A boolean marker is the case where it breaks down: every entry
    /// carrying <see cref="KnownAttributes.OrphanKey"/> shares the one value, so it forms a single
    /// hub node linking every unrelated entry that has it, and the hub grows with the corpus.
    /// <see cref="KnownAttributes.StateMarkerKeys"/> is the one place that distinction is drawn —
    /// see it for the measurements. This function is the choke point the graph,
    /// <c>EntityQuerySeeder</c>'s node keys and <c>EntityFactDerivation</c> all read through, so
    /// excluding here covers all three. It does not touch BM25: a state marker still seeds the
    /// term-frequency index and still answers an <c>attributeFilter</c>.</para>
    /// </summary>
    public static IEnumerable<(string Type, string Value)> ParseEntityAttributes(
        IReadOnlyDictionary<string, string[]>? attributes)
    {
        if (attributes is null) yield break;
        foreach (var kv in attributes)
        {
            if (KnownAttributes.IsStateMarker(kv.Key)) continue;
            string type = kv.Key.ToLowerInvariant();
            foreach (string raw in kv.Value)
            {
                string value = raw.Trim().ToLowerInvariant();
                if (value.Length > 0) yield return (type, value);
            }
        }
    }
}
