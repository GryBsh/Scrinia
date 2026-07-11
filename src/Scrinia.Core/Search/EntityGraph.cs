namespace Scrinia.Core.Search;

/// <summary>
/// Bipartite entity graph + Personalized PageRank, used by the entity-graph retrieval
/// signal (<see cref="EntityGraphScoreContributor"/>). Nodes are <b>memories</b> (by
/// scoped key) and <b>entities</b> — attribute <c>(key, value)</c> nodes, one per distinct
/// tag: any field/keyword key, not just person/place/org/thing/topic; an edge links a memory
/// to each entity it was tagged with.
///
/// <para><b>What it's for:</b> the keyword+vector pool buries memories that share little
/// lexical/semantic overlap with the query but are <i>associated</i> via a shared entity
/// (query names entity A; the gold memory is about entity B that co-occurs with A
/// elsewhere). PPR seeded from the query's entities diffuses mass across those
/// associations, producing a per-memory score that lifts buried-but-related memories.
/// Validated as a within-pool precision lever in
/// <c>docs/research/benchmark-methodology-findings.md</c> finding #8.</para>
///
/// <para><b>Resolution is deliberately crude</b> — entities are matched by exact
/// lowercased string. This makes the signal a <i>lower bound</i>: under-linking ("maria"
/// ≠ "maria rodriguez") can only cost recall. Query seeding is lexical (no LLM): an entity
/// is a seed when its surface form appears in the query or all its tokens do.</para>
///
/// <para>Not thread-safe; callers build an instance per query (or under a write lock).</para>
/// </summary>
public sealed class EntityGraph
{
    // entity (lowercased value) -> set of memory keys tagged with it
    private readonly Dictionary<string, HashSet<string>> _entityToDocs = new(StringComparer.OrdinalIgnoreCase);
    // memory key -> set of entities it was tagged with
    private readonly Dictionary<string, HashSet<string>> _docToEntities = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The typed-keyword markers <see cref="ParseTypedEntities"/> recognizes as entity types
    /// (person/place/org/thing/topic). The legacy untyped <c>entity:</c> marker is NOT in this
    /// set — <see cref="ParseTypedEntities"/> keeps it as its own type <c>"entity"</c> instead.
    /// </summary>
    public static readonly IReadOnlySet<string> RealTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "person", "place", "org", "thing", "topic" };

    /// <summary>Number of distinct entity nodes currently in the graph.</summary>
    public int EntityCount => _entityToDocs.Count;

    /// <summary>Number of distinct memory nodes currently in the graph.</summary>
    public int DocCount => _docToEntities.Count;

    /// <summary>
    /// Records a memory and the entities it is tagged with. <paramref name="docKey"/> must be
    /// the key the caller scores by (e.g. <c>"{scope}|{name}"</c>), so PPR scores line up with
    /// the search candidate keys. Entity values are lowercased; blank entities are ignored.
    /// Idempotent per (doc, entity) pair.
    /// </summary>
    public void AddDoc(string docKey, IEnumerable<string> entities)
    {
        if (string.IsNullOrWhiteSpace(docKey) || entities is null) return;

        foreach (string raw in entities)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string entity = raw.Trim().ToLowerInvariant();
            if (entity.Length == 0) continue;

            if (!_entityToDocs.TryGetValue(entity, out var docs))
                _entityToDocs[entity] = docs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            docs.Add(docKey);

            if (!_docToEntities.TryGetValue(docKey, out var ents))
                _docToEntities[docKey] = ents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ents.Add(entity);
        }
    }

    /// <summary>Clears all nodes and edges.</summary>
    public void Reset()
    {
        _entityToDocs.Clear();
        _docToEntities.Clear();
    }

    /// <summary>
    /// Parses typed-keyword tags into <c>(type, value)</c> pairs — the graph's node source.
    /// Recognizes the <see cref="RealTypes"/> (person/place/org/thing/topic) plus the legacy
    /// untyped <c>entity:</c> marker (kept as type <c>"entity"</c> so pre-typing data still
    /// graphs; it never participates in a type conflict). Both type and value are lowercased;
    /// the value is kept whole so multi-word entities stay single nodes. Prefix detection mirrors
    /// <see cref="TextAnalysis.IndexTokensForKeyword"/>. Shared by the production contributor and
    /// the bench so "what counts as a graph tag" has one definition.
    /// </summary>
    public static IEnumerable<(string Type, string Value)> ParseTypedEntities(IEnumerable<string>? keywords)
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
    /// Fields-side sibling of <see cref="ParseTypedEntities"/>: yields (key, value) pairs for
    /// <b>every</b> field key, uniformly — not just <c>KnownFields.EntityKeys</c> — so any
    /// attribute (e.g. co-<c>file:</c>/co-<c>status:</c> memories) can become a graph
    /// association, not only the five blessed entity keys. Type is the lowercased field key,
    /// values lowercased and kept whole. Together with <see cref="ParseTypedEntities"/> they
    /// give Build its dual-read: legacy typed keywords ∪ unified fields.
    ///
    /// <para><see cref="Build"/> gives each of these (type, value) tuples its own <c>type:value</c>
    /// node, so arbitrary keys never cross-link on a shared value (<c>status:done</c> and
    /// <c>outcome:done</c> are distinct nodes; so are <c>tag:paris</c> and <c>place:paris</c>).</para>
    /// </summary>
    public static IEnumerable<(string Type, string Value)> ParseEntityFields(
        IReadOnlyDictionary<string, string[]>? fields)
    {
        if (fields is null) yield break;
        foreach (var kv in fields)
        {
            string type = kv.Key.ToLowerInvariant();
            foreach (string raw in kv.Value)
            {
                string value = raw.Trim().ToLowerInvariant();
                if (value.Length > 0) yield return (type, value);
            }
        }
    }

    /// <summary>
    /// Builds a graph from a set of <c>(memory, keywords)</c> docs. Delegates to the
    /// <see cref="Build(IEnumerable{ValueTuple{string, IReadOnlyList{string}, IReadOnlyDictionary{string, string[]}}})"/>
    /// 3-tuple overload with no fields, so behavior for legacy typed-keyword-only callers is
    /// unchanged.
    /// </summary>
    public static EntityGraph Build(IEnumerable<(string DocKey, IReadOnlyList<string>? Keywords)> docs)
        => Build(docs?.Select(d => (d.DocKey, d.Keywords, (IReadOnlyDictionary<string, string[]>?)null)) ?? []);

    /// <summary>
    /// Builds a graph from a set of <c>(memory, keywords, fields)</c> docs, applying
    /// <b>uniform <c>type:value</c> node identity</b>: every <c>(type, value)</c> pair is its own
    /// node, distinct by construction — no conflict pass, no bare-value folding. A value shared
    /// across unrelated keys (<c>status:done</c> vs <c>outcome:done</c>; <c>person:paris</c> vs
    /// <c>place:paris</c>) is never one node, so unrelated keys never falsely cross-link on a
    /// shared value. Per-doc tags are the union of <see cref="ParseTypedEntities"/> (legacy typed
    /// keywords) and <see cref="ParseEntityFields"/> (unified Fields), so a doc carrying either or
    /// both sources graphs identically. Use this rather than per-doc <see cref="AddDoc"/> whenever
    /// the input carries typed keywords or fields, so node identity is applied consistently.
    /// </summary>
    public static EntityGraph Build(
        IEnumerable<(string DocKey, IReadOnlyList<string>? Keywords, IReadOnlyDictionary<string, string[]>? Fields)> docs)
    {
        var graph = new EntityGraph();
        if (docs is null) return graph;

        // Uniform node identity: every (type, value) is the node "type:value". No conflict-split
        // pass — a value under two different keys (person:paris vs place:paris; status:done vs
        // outcome:done) is two distinct nodes by construction, so unrelated keys never cross-link.
        foreach (var (docKey, keywords, fields) in docs)
        {
            if (string.IsNullOrWhiteSpace(docKey)) continue;
            var nodeKeys = new List<string>();
            foreach (var (type, value) in ParseTypedEntities(keywords)) nodeKeys.Add($"{type}:{value}");
            foreach (var (type, value) in ParseEntityFields(fields)) nodeKeys.Add($"{type}:{value}");
            if (nodeKeys.Count == 0) continue;
            graph.AddDoc(docKey, nodeKeys);
        }
        return graph;
    }

    /// <summary>
    /// Personalized PageRank seeded from the query's entities, returning the stationary mass
    /// on each <b>memory</b> node (memory key → score). Returns an empty map when no entity in
    /// the graph is mentioned by the query (no seeds → nothing to diffuse).
    ///
    /// <para>The walk is a two-hop-per-iteration random walk with restart on the bipartite
    /// graph: mass flows entity→memory→entity each iteration, with probability
    /// <paramref name="alpha"/> teleporting back to the (uniform) seed set. After
    /// <paramref name="iterations"/> passes the memory-side distribution is returned raw —
    /// the caller normalizes and scales before use.</para>
    /// </summary>
    /// <param name="query">Raw query text; entities are seeded by lexical match against it.</param>
    /// <param name="alpha">Teleport (restart) probability back to the seed set. Standard ≈ 0.15.</param>
    /// <param name="iterations">Power-iteration passes. ~20 is ample for these graphs.</param>
    public IReadOnlyDictionary<string, double> Score(string query, double alpha, int iterations)
    {
        var empty = (IReadOnlyDictionary<string, double>)new Dictionary<string, double>();
        if (string.IsNullOrWhiteSpace(query) || _entityToDocs.Count == 0) return empty;

        var seeds = SeedEntities(query);
        if (seeds.Count == 0) return empty;

        if (alpha < 0) alpha = 0;
        if (alpha > 1) alpha = 1;
        if (iterations < 1) iterations = 1;

        double seedMass = 1.0 / seeds.Count;
        var restart = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (string s in seeds) restart[s] = seedMass;

        // entity-side distribution; initialised to the restart distribution.
        var entityScore = new Dictionary<string, double>(restart, StringComparer.OrdinalIgnoreCase);
        var docScore = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        for (int it = 0; it < iterations; it++)
        {
            // entity -> memory: each entity splits its mass evenly across its memories.
            docScore.Clear();
            foreach (var (entity, p) in entityScore)
            {
                if (p <= 0) continue;
                var docs = _entityToDocs[entity];
                double share = p / docs.Count;
                foreach (string d in docs)
                {
                    docScore.TryGetValue(d, out double acc);
                    docScore[d] = acc + share;
                }
            }

            // memory -> entity: each memory splits its mass evenly across its entities,
            // then teleport: (1-alpha) * diffused + alpha * restart.
            var nextEntity = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (doc, p) in docScore)
            {
                if (p <= 0) continue;
                var ents = _docToEntities[doc];
                double share = p / ents.Count;
                foreach (string e in ents)
                {
                    nextEntity.TryGetValue(e, out double acc);
                    nextEntity[e] = acc + share;
                }
            }

            entityScore = new Dictionary<string, double>(nextEntity.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (e, p) in nextEntity)
                entityScore[e] = (1 - alpha) * p;
            foreach (var (e, p) in restart)
            {
                entityScore.TryGetValue(e, out double acc);
                entityScore[e] = acc + alpha * p;
            }
        }

        // Final memory-side distribution reflects the converged entity distribution.
        docScore.Clear();
        foreach (var (entity, p) in entityScore)
        {
            if (p <= 0) continue;
            var docs = _entityToDocs[entity];
            double share = p / docs.Count;
            foreach (string d in docs)
            {
                docScore.TryGetValue(d, out double acc);
                docScore[d] = acc + share;
            }
        }

        return docScore;
    }

    /// <summary>
    /// Lexical seeding (floor): an entity node is a seed when its <em>surface form</em> appears
    /// in the query as a case-insensitive substring, or when every token of the surface appears
    /// in the tokenized query. The substring check catches multi-word entities ("home
    /// renovation"); the token check catches order/punctuation differences. The surface strips a
    /// node's type prefix (so <c>place:paris</c> still seeds from the query word "paris"); a query
    /// word that collides across types lights up every same-value node — disambiguation lives in
    /// the graph structure (no false cross-type bridge), not in lexical seeding. No LLM is
    /// involved.
    /// </summary>
    private List<string> SeedEntities(string query)
    {
        string queryLower = query.ToLowerInvariant();
        var queryTokens = new HashSet<string>(TextAnalysis.Tokenize(query), StringComparer.OrdinalIgnoreCase);

        var seeds = new List<string>();
        foreach (string node in _entityToDocs.Keys)
        {
            string surface = SurfaceOf(node);
            if (queryLower.Contains(surface, StringComparison.Ordinal))
            {
                seeds.Add(node);
                continue;
            }
            var entityTokens = TextAnalysis.Tokenize(surface);
            if (entityTokens.Count > 0 && entityTokens.All(queryTokens.Contains))
                seeds.Add(node);
        }
        return seeds;
    }

    /// <summary>The surface form a node seeds on: the value after the type prefix. Every node built
    /// by <see cref="Build"/> is <c>type:value</c>, so <c>place:paris</c> and <c>status:done</c> both
    /// seed from their bare value. A node with no <c>:</c> (defensive — direct <see cref="AddDoc"/>
    /// callers may pass bare values) seeds on the whole key.</summary>
    private static string SurfaceOf(string nodeKey)
    {
        int colon = nodeKey.IndexOf(':');
        return colon > 0 && colon < nodeKey.Length - 1 ? nodeKey[(colon + 1)..] : nodeKey;
    }
}
