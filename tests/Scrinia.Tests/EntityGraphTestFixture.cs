using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// The one place tests build an <see cref="EntityFactStore"/> and run the entity-graph scoring
/// walk. <c>EntityGraphTests</c>, <c>KnownAttributesTests</c> and
/// <c>StateMarkerNotAnEntityTests</c> each carried a byte-identical private copy of both helpers,
/// and each copy hand-reimplemented <see cref="EntityFactDerivation.Derive"/>'s body — the
/// ParseEntityKeywords ∪ ParseEntityAttributes union — rather than calling it. A change to what
/// counts as an entity would have left all three passing against the old rule.
///
/// <para><see cref="StoreOf"/> now calls the real derivation, so these tests are pinned to
/// production's definition of a memory's entity set rather than to a stale transcription of it.
/// </para>
/// </summary>
internal static class EntityGraphTestFixture
{
    /// <summary>
    /// Builds a store from (docKey, keywords, attributes) triples through the production
    /// derivation. Doc keys land as <c>"s|{docKey}"</c>
    /// (<c>MemoryEntities.ScoringKey = "{Scope}|{Subject}"</c>).
    /// </summary>
    public static EntityFactStore StoreOf(
        params (string DocKey, string[]? Keywords, IReadOnlyDictionary<string, string[]>? Attributes)[] docs) =>
        EntityFactDerivation.BuildStore(docs.Select(d => new ScopedArtifact("s", new ArtifactEntry(
            Name: d.DocKey,
            Uri: "",
            OriginalBytes: 0,
            ChunkCount: 1,
            CreatedAt: default,
            Keywords: d.Keywords,
            Attributes: d.Attributes?.ToDictionary(kv => kv.Key, kv => kv.Value)))));

    /// <summary>
    /// Runs the same seed → Personalized PageRank walk <see cref="EntityGraphScoreContributor"/>
    /// runs in production, returning the raw (unnormalized, unscaled) PPR mass — matching what the
    /// deleted <c>EntityGraph.Score</c> used to return.
    /// </summary>
    public static IReadOnlyDictionary<string, double> ScoreQuery(
        EntityFactStore store, string query, double alpha, int iterations)
    {
        var (entityToDocs, docToEntities) = store.ScoringAdjacency();
        var seeds = EntityQuerySeeder.Seed(query, entityToDocs.Keys);
        return seeds.Count == 0
            ? new Dictionary<string, double>()
            : PersonalizedPageRank.Score(entityToDocs, docToEntities, seeds, alpha, iterations);
    }
}
