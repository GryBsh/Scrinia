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
/// command, and (while it existed) the server's <c>/search</c> endpoint. It lives in Core because
/// that is the one project they all reference — the server previously carried its own copy in
/// <c>PluginPipeline.SearchAsync</c> which had drifted to support no filters at all.
/// <para>Contributors are parameters, not ambient lookups: callers resolve them at the edge. That
/// keeps the pipeline callable from a test without installing AsyncLocal state.</para>
/// <para><b>The pipeline owns <see cref="SearchQuery.SupplementalScores"/>,
/// <see cref="SearchQuery.EntityGraphScores"/>, and <see cref="SearchQuery.Candidates"/>.</b>
/// <see cref="ExecuteAsync"/> is the sole materialization path: it produces the
/// bounded, query-eligible candidate snapshot exactly once via
/// <see cref="IMemoryStore.SelectLexicalCandidates(SearchQuery, int)"/> and passes it to
/// <c>IMemoryStore.SearchAll</c>, which suppresses its own <c>ListScoped</c> when
/// <c>Candidates</c> is set. Supplemental and entity-graph scores and surfaced entity
/// facts are computed over that same snapshot.</para>
/// </summary>
public static class SearchPipeline
{
    public static Task<SearchOutcome> ExecuteAsync(
        IMemoryStore store,
        SearchQuery query,
        ISearchScoreContributor? contributor,
        EntityGraphScoreContributor? entityContributor,
        string? context,
        bool surfaceEntityFacts,
        CancellationToken ct = default) =>
        ExecuteAsync(store, query, contributor, entityContributor, context,
            surfaceEntityFacts, candidateOptions: null, ct: ct);

    public static async Task<SearchOutcome> ExecuteAsync(
        IMemoryStore store,
        SearchQuery query,
        ISearchScoreContributor? contributor,
        EntityGraphScoreContributor? entityContributor,
        string? context = null,
        bool surfaceEntityFacts = false,
        SearchCandidateOptions? candidateOptions = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(query);

        // Augment the query with caller-supplied context for scoring. The bare query is preserved
        // for any user-facing echo; the effective query feeds BM25, attribute scoring, and
        // embeddings alike so a single signal carries through all three.
        string effectiveQuery = string.IsNullOrWhiteSpace(context)
            ? query.Query
            : $"{query.Query} {context}";

        // Sole materialization: apply query eligibility before the lexical top-K cut.
        // Supplied candidates are scope-filtered without re-listing. Scoring and entity
        // facts then consume this same bounded snapshot.
        var options = candidateOptions ?? SearchCandidateOptions.Current;
        var selectionQuery = query with { Query = effectiveQuery };
        ScopedArtifact[] candidates = store.SelectLexicalCandidates(selectionQuery, options.CandidateLimit).ToArray();

        IReadOnlyDictionary<string, double>? supplemental = null;
        IReadOnlyDictionary<string, double>? entityScores = null;
        if (contributor is not null)
            supplemental = await contributor.ComputeScoresAsync(effectiveQuery, candidates, store, ct);
        if (entityContributor is not null)
        {
            // Facts are a scoring input, not a separate corpus. Building from the snapshot keeps
            // entity traversal bounded by the same lexical top-K that feeds every other signal.
            var factStore = EntityFactDerivation.BuildStore(candidates);
            entityScores = entityContributor.Score(factStore, query: effectiveQuery);
        }

        var effective = query with
        {
            Query = effectiveQuery,
            SupplementalScores = supplemental is { Count: > 0 } ? supplemental : null,
            EntityGraphScores = entityScores,
            Candidates = candidates,
        };

        var filtered = store.SearchAll(effective);

        IReadOnlyList<EntityFactSummary> entityFacts = [];
        if (surfaceEntityFacts && candidates.Length > 0)
            entityFacts = BuildEntityFacts(store, effectiveQuery, candidates);

        return new SearchOutcome(filtered, entityFacts);
    }

    /// <summary>Builds up to 3 entity fact summaries from the same bounded, query-eligible
    /// candidate snapshot the scorer used — never a separate store read, never a hidden scope.</summary>
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
