namespace Scrinia.Core.Search;

/// <summary>
/// The Personalized PageRank walk extracted from <see cref="EntityGraph"/> so both the graph and the
/// persisted <see cref="EntityFactStore"/> share one implementation. Bipartite two-hop random walk
/// with restart: mass flows entity→doc→entity each iteration, teleporting to the (uniform) seed set
/// with probability <paramref name="alpha"/>. Returns the stationary mass per doc key. Pure.
/// </summary>
public static class PersonalizedPageRank
{
    public static IReadOnlyDictionary<string, double> Score(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> entityToDocs,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> docToEntities,
        IReadOnlyCollection<string> seeds, double alpha, int iterations)
    {
        var docScore = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (entityToDocs.Count == 0 || seeds.Count == 0) return docScore;

        if (alpha < 0) alpha = 0;
        if (alpha > 1) alpha = 1;
        if (iterations < 1) iterations = 1;

        double seedMass = 1.0 / seeds.Count;
        var restart = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (string s in seeds) restart[s] = seedMass;

        var entityScore = new Dictionary<string, double>(restart, StringComparer.OrdinalIgnoreCase);

        for (int it = 0; it < iterations; it++)
        {
            docScore.Clear();
            foreach (var (entity, p) in entityScore)
            {
                if (p <= 0) continue;
                var docs = entityToDocs[entity];
                double share = p / docs.Count;
                foreach (string d in docs)
                {
                    docScore.TryGetValue(d, out double acc);
                    docScore[d] = acc + share;
                }
            }

            var nextEntity = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (doc, p) in docScore)
            {
                if (p <= 0) continue;
                var ents = docToEntities[doc];
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

        docScore.Clear();
        foreach (var (entity, p) in entityScore)
        {
            if (p <= 0) continue;
            var docs = entityToDocs[entity];
            double share = p / docs.Count;
            foreach (string d in docs)
            {
                docScore.TryGetValue(d, out double acc);
                docScore[d] = acc + share;
            }
        }

        return docScore;
    }
}
