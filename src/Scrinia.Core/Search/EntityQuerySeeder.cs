namespace Scrinia.Core.Search;

/// <summary>
/// The query→entity seeding rule shared by <see cref="EntityGraph"/>'s PPR seeding and the
/// retrieval auto-surface path: given a query and a set of <c>"type:value"</c> entity keys, return
/// which keys the query names. A key is named when the lowercased query contains the key's surface
/// value as a substring, OR every token of the surface appears among the query tokens. Extracted
/// into one place so the two consumers cannot drift. Pure, ordinal, culture-invariant.
/// </summary>
public static class EntityQuerySeeder
{
    public static List<string> Seed(string query, IEnumerable<string> entityKeys)
    {
        var seeds = new List<string>();
        if (string.IsNullOrWhiteSpace(query) || entityKeys is null) return seeds;

        string queryLower = query.ToLowerInvariant();
        var queryTokens = new HashSet<string>(TextAnalysis.Tokenize(query), StringComparer.OrdinalIgnoreCase);

        foreach (string key in entityKeys)
        {
            string surface = SurfaceOf(key);
            if (queryLower.Contains(surface, StringComparison.Ordinal))
            {
                seeds.Add(key);
                continue;
            }
            var entityTokens = TextAnalysis.Tokenize(surface);
            if (entityTokens.Count > 0 && entityTokens.All(queryTokens.Contains))
                seeds.Add(key);
        }
        return seeds;
    }

    /// <summary>The surface a key seeds on: the value after the first <c>:</c>. A key with no
    /// usable colon seeds on the whole string (defensive parity with the graph's old behavior).</summary>
    internal static string SurfaceOf(string nodeKey)
    {
        int colon = nodeKey.IndexOf(':');
        return colon > 0 && colon < nodeKey.Length - 1 ? nodeKey[(colon + 1)..] : nodeKey;
    }
}
