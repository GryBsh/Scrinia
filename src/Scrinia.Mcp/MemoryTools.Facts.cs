using System.ComponentModel;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Search;

namespace Scrinia.Mcp;

public sealed partial class ScriniaMcpTools
{
    [McpServerTool(Name = "facts"), Description(
        "Query the entity fact store directly (aggregate/relational questions search can't express). " +
        "op 'entities' lists the known entity vocabulary (optional key filter); 'profile' returns one " +
        "entity's count, date span, co-occurrences, and per-memory timeline; 'intersect' returns memories " +
        "mentioning ALL given entities. Entities are 'key:value' (e.g. person:jolene) or a bare value " +
        "(resolved across keys; ambiguous values return a disambiguation list). Cross-scope by default; " +
        "scopes/excludeTopics narrow it like search. " +
        "Example: facts({ op: 'intersect', entities: ['person:jolene', 'place:france'] }).")]
    public async Task<string> Facts(
        [Description("Operation: 'entities', 'profile', or 'intersect'.")] string op,
        [Description("profile: the target entity — 'key:value' or a bare value.")] string? entity = null,
        [Description("intersect: the entities (>=2) — each 'key:value' or a bare value.")] string[]? entities = null,
        [Description("entities: optional key filter, e.g. 'person'.")] string? key = null,
        [Description("Optional scope order to narrow to (same semantics as search). An empty array behaves the same as omitting it.")] string[]? scopes = null,
        [Description("Optional topic names to exclude (same semantics as search). An empty array behaves the same as omitting it.")] string[]? excludeTopics = null,
        [Description("Max rows for the entities list / profile timeline (default 50).")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return (await FactsResponse(op, entity, entities, key, scopes, excludeTopics, limit, cancellationToken)).ToYaml();
        }
        catch (RestoreRecoveryException ex)
        {
            return RecoveryBlockedResponse(ex).ToYaml();
        }
    }

    /// <summary>
    /// The typed <c>facts</c> handler. Split out from the public tool method so the CLI
    /// (<c>scri facts</c>) can read <see cref="McpResponse.Status"/> for its exit code instead of
    /// grepping the rendered document — every other CLI passthrough already calls an internal
    /// handler, and <c>facts</c> was the one verb whose tool method was also its handler.
    /// </summary>
    internal Task<McpResponse> FactsResponse(
        string op,
        string? entity,
        string[]? entities,
        string? key,
        string[]? scopes,
        string[]? excludeTopics,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var facts = SelectFactStore(CurrentStore, scopes, excludeTopics);

        FactsResult result;
        switch ((op ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "entities":
                result = FactsQuery.EntitiesList(facts, key, limit);
                break;
            case "profile":
                if (string.IsNullOrWhiteSpace(entity))
                    return Task.FromResult(ResponseBuilder.Error(
                        "facts profile requires 'entity'.", ErrorCodes.InvalidParameter,
                        "facts({ op: 'profile', entity: 'person:jolene' })"));
                result = FactsQuery.Profile(facts, entity, limit);
                break;
            case "intersect":
                if (entities is null || entities.Length < 2)
                    return Task.FromResult(ResponseBuilder.Error(
                        "facts intersect requires 'entities' with at least 2 members.", ErrorCodes.InvalidParameter,
                        "facts({ op: 'intersect', entities: ['person:jolene', 'place:france'] })"));
                result = FactsQuery.Intersect(facts, entities);
                break;
            default:
                return Task.FromResult(ResponseBuilder.Error(
                    $"unknown facts op '{op}'. Use 'entities', 'profile', or 'intersect'.",
                    ErrorCodes.InvalidParameter,
                    "facts({ op: 'entities' })"));
        }

        return Task.FromResult(FactsResponseFormatter.ToResponse(result));
    }

    /// <summary>Cross-scope cached store when unfiltered; a scoped store built from the
    /// scope+excludeTopics-filtered candidates otherwise (same derivation as the auto-surface path).
    /// Task 9, D-4: an empty scopes/excludeTopics array is "unfiltered", identically to null.</summary>
    private static EntityFactStore SelectFactStore(IMemoryStore store, string[]? scopes, string[]? excludeTopics)
    {
        // Task 9 fix round 2, L-1: normalize BEFORE the gate, not after. The raw arrays can carry
        // a caller-sent [""] or [null] that means "no filter" (Task 9, D-4/NormalizeFilterList),
        // but gating on the raw array took the "filtered" branch for them anyway — re-deriving the
        // whole fact store from a full ListScoped instead of using the cached cross-scope store.
        // Correctness was never at stake (ListScoped normalizes its own inputs and would answer
        // the same either way), only cost: rebuilding the entire store because a caller sent a
        // blank filter is exactly the needless-repeated-read this project avoids elsewhere.
        var normalizedScopes = MemoryNaming.NormalizeFilterList(scopes);
        var normalizedExcludeTopics = MemoryNaming.NormalizeFilterList(excludeTopics);
        if (normalizedScopes is not { Count: > 0 } && normalizedExcludeTopics is not { Count: > 0 })
            return store.GetEntityFactStore();
        return EntityFactDerivation.BuildStore(store.ListScoped(normalizedScopes, normalizedExcludeTopics));
    }
}
