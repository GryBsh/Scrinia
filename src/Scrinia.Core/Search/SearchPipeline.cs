using Scrinia.Core.Models;   // ScopedArtifact lives in Scrinia.Core.Models, a SIBLING namespace —
                             // without this the IReadOnlyList<ScopedArtifact> declarations are CS0246.

namespace Scrinia.Core.Search;

/// <summary>The result of a scoring-pipeline run: the ranked matches plus the (possibly empty)
/// entity fact summaries for entities the query named. EntityFacts is always empty unless the
/// caller passes surfaceEntityFacts: true.</summary>
public readonly record struct SearchOutcome(
    IReadOnlyList<SearchResult> Matches,
    IReadOnlyList<EntityFactSummary> EntityFacts);

/// <summary>
/// The single scoring pipeline behind the MCP <c>search</c> tool, the <c>scri search</c> CLI
/// command, and the server's <c>/search</c> endpoint. It lives in Core because that is the one
/// project all three reference — the server previously carried its own copy in
/// <c>PluginPipeline.SearchAsync</c> which had drifted to support no filters at all.
/// <para>Contributors are parameters, not ambient lookups: callers resolve them at the edge. That
/// keeps the pipeline callable from a test without installing AsyncLocal state.</para>
/// <para><b>The pipeline owns <see cref="SearchQuery.SupplementalScores"/>,
/// <see cref="SearchQuery.EntityGraphScores"/>, and <see cref="SearchQuery.Candidates"/>.</b>
/// <see cref="ExecuteAsync"/> unconditionally overwrites all three on the query it passes to
/// <c>IMemoryStore.SearchAll</c> with what it computed (or <see langword="null"/>, when no
/// contributor ran) — a caller-supplied value on the incoming <see cref="SearchQuery"/> is
/// replaced, never merged with or deferred to. No current caller sets any of the three before
/// calling in, so this is a documented constraint rather than an observed bug.</para>
/// </summary>
public static class SearchPipeline
{
    public static async Task<SearchOutcome> ExecuteAsync(
        IMemoryStore store,
        SearchQuery query,
        ISearchScoreContributor? contributor,
        EntityGraphScoreContributor? entityContributor,
        string? context = null,
        bool surfaceEntityFacts = false,
        CancellationToken ct = default)
    {
        // Augment the query with caller-supplied context for scoring. The bare query is preserved
        // for any user-facing echo; the effective query feeds BM25, attribute scoring, and
        // embeddings alike so a single signal carries through all three.
        string effectiveQuery = string.IsNullOrWhiteSpace(context)
            ? query.Query
            : $"{query.Query} {context}";

        // Both contributors run over the same excludeTopics-filtered candidates so excluded topics
        // don't influence scoring. The entity-graph signal is a separate fusion source, not summed
        // into supplemental — see EntityGraphScoreContributor.
        IReadOnlyList<ScopedArtifact>? candidates = null;
        if (contributor is not null || entityContributor is not null || surfaceEntityFacts)
            candidates = store.ListScoped(query.Scopes, query.ExcludeTopics);

        IReadOnlyDictionary<string, double>? supplemental = null;
        IReadOnlyDictionary<string, double>? entityScores = null;
        if (candidates is not null)
        {
            if (contributor is not null)
                supplemental = await contributor.ComputeScoresAsync(effectiveQuery, candidates, store, ct);
            if (entityContributor is not null)
            {
                var factStore = query.Scopes is not { Count: > 0 } && query.ExcludeTopics is not { Count: > 0 }
                    ? store.GetEntityFactStore()                       // cached, no rebuild
                    : EntityFactDerivation.BuildStore(candidates);     // scoped / topic-excluded
                entityScores = entityContributor.Score(factStore, query: effectiveQuery);
            }
        }

        var effective = query with
        {
            Query = effectiveQuery,
            SupplementalScores = supplemental is { Count: > 0 } ? supplemental : null,
            EntityGraphScores = entityScores,
            // Built from this query's own Scopes/ExcludeTopics a few lines above, which is the
            // contract SearchQuery.Candidates documents. Null when no contributor ran, in which
            // case the store materializes them itself.
            Candidates = candidates,
        };

        var filtered = store.SearchAll(effective);

        IReadOnlyList<EntityFactSummary> entityFacts = [];
        if (surfaceEntityFacts && candidates is { Count: > 0 })
            entityFacts = BuildEntityFacts(store, effectiveQuery, candidates);

        return new SearchOutcome(filtered, entityFacts);
    }

    /// <summary>Builds up to 3 entity fact summaries from the same scope+excludeTopics-filtered
    /// candidate set the scorer used — never a separate store read, never a hidden scope.</summary>
    private static IReadOnlyList<EntityFactSummary> BuildEntityFacts(
        IMemoryStore store, string effectiveQuery, IReadOnlyList<ScopedArtifact> candidates)
    {
        var factStore = EntityFactDerivation.BuildStore(candidates);

        // Map "type:value" -> EntityRef so we round-trip named keys without re-parsing colons.
        var refByKey = new Dictionary<string, EntityRef>(StringComparer.Ordinal);
        foreach (var e in factStore.Entities())
            refByKey[$"{e.Key}:{e.Value}"] = e;
        if (refByKey.Count == 0) return [];

        var named = EntityQuerySeeder.Seed(effectiveQuery, refByKey.Keys)
            .Select(k => refByKey[k]);

        return EntityFactSummarizer.Summarize(factStore, named);
    }
}
