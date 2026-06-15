namespace Scrinia.Core.Search;

/// <summary>
/// Bipartite entity graph + Personalized PageRank, used by the entity-graph retrieval
/// signal (<see cref="EntityGraphScoreContributor"/>). Nodes are <b>memories</b> (by
/// scoped key) and <b>entities</b> (agent-extracted typed-keyword values —
/// person/place/org/thing/topic); an edge links a memory to each entity it was tagged with.
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
    /// The fixed set of real entity types. A value claimed by two or more of these across a
    /// candidate set is ambiguous and gets split into per-type nodes by <see cref="Build"/>
    /// (e.g. a person and a place sharing a name stop being one node). The legacy untyped
    /// <c>entity:</c> marker is NOT in this set — it never claims a type and never causes a split.
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
    /// Builds a graph from a set of <c>(memory, keywords)</c> docs, applying <b>conflict-driven
    /// node identity</b>: a tag's node is its bare <c>value</c> unless that value is claimed by
    /// two or more <see cref="RealTypes"/> across the whole set — then it splits to
    /// <c>type:value</c> so e.g. a person and a place sharing a name stop being one node.
    /// Single-type values never split (no needless fragmentation); the legacy untyped
    /// <c>entity:</c> marker always folds to bare <c>value</c> and never triggers or joins a
    /// split (conservative — under-linking is the documented lower-bound stance, never a false
    /// bridge). Two passes are required: the conflict set must be known before any node key is
    /// assigned. Use this rather than per-doc <see cref="AddDoc"/> whenever the input carries
    /// typed keywords, so splitting is applied consistently.
    /// </summary>
    public static EntityGraph Build(IEnumerable<(string DocKey, IReadOnlyList<string>? Keywords)> docs)
    {
        var graph = new EntityGraph();
        if (docs is null) return graph;

        // Pass 1: parse tags per doc; record which real types claim each value.
        var parsed = new List<(string DocKey, List<(string Type, string Value)> Tags)>();
        var realTypesByValue = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (docKey, keywords) in docs)
        {
            if (string.IsNullOrWhiteSpace(docKey) || keywords is not { Count: > 0 }) continue;
            var tags = new List<(string Type, string Value)>();
            foreach (var pair in ParseTypedEntities(keywords)) tags.Add(pair);
            if (tags.Count == 0) continue;
            parsed.Add((docKey, tags));
            foreach (var (type, value) in tags)
            {
                if (!RealTypes.Contains(type)) continue;   // legacy "entity" doesn't claim a type
                if (!realTypesByValue.TryGetValue(value, out var set))
                    realTypesByValue[value] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(type);
            }
        }

        // Pass 2: resolve node keys (split only on genuine conflict) and populate.
        foreach (var (docKey, tags) in parsed)
        {
            var nodeKeys = new List<string>(tags.Count);
            foreach (var (type, value) in tags)
                nodeKeys.Add(ResolveNodeKey(type, value, realTypesByValue));
            graph.AddDoc(docKey, nodeKeys);
        }
        return graph;
    }

    /// <summary>Node key for a tag: <c>type:value</c> only when the value is genuinely ambiguous
    /// (claimed by 2+ real types in the set); bare <c>value</c> otherwise.</summary>
    private static string ResolveNodeKey(
        string type, string value, Dictionary<string, HashSet<string>> realTypesByValue) =>
        RealTypes.Contains(type)
            && realTypesByValue.TryGetValue(value, out var types)
            && types.Count >= 2
            ? $"{type}:{value}"
            : value;

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
    /// split node's type prefix (so <c>place:paris</c> still seeds from the query word "paris");
    /// a query word that collides across types lights up every split variant — disambiguation
    /// lives in the graph structure (no false cross-type bridge), not in lexical seeding. No LLM
    /// is involved.
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

    /// <summary>The surface form a node seeds on: the value after a real-type prefix (so a split
    /// node <c>place:paris</c> seeds from "paris"), or the whole key when it carries no real-type
    /// prefix (bare values, legacy keys, or values that merely happen to contain a colon).</summary>
    private static string SurfaceOf(string nodeKey)
    {
        int colon = nodeKey.IndexOf(':');
        if (colon <= 0 || colon >= nodeKey.Length - 1) return nodeKey;
        return RealTypes.Contains(nodeKey[..colon]) ? nodeKey[(colon + 1)..] : nodeKey;
    }
}
