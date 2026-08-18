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
        [Description("Optional comma-separated scope order to narrow to (same semantics as search).")] string? scopes = null,
        [Description("Optional comma-separated topic names to exclude (same semantics as search).")] string? excludeTopics = null,
        [Description("Max rows for the entities list / profile timeline (default 50).")] int limit = 50,
        CancellationToken cancellationToken = default)
        => (await FactsResponse(op, entity, entities, key, scopes, excludeTopics, limit, cancellationToken)).ToYaml();

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
        string? scopes,
        string? excludeTopics,
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
    /// scope+excludeTopics-filtered candidates otherwise (same derivation as the auto-surface path).</summary>
    private static EntityFactStore SelectFactStore(IMemoryStore store, string? scopes, string? excludeTopics)
    {
        // scopes/excludeTopics are still comma-strings here (Task 9 commit 3 converts the facade
        // and this handler to string[]?); split at this call site for now, matching the Core
        // contract this task already typed as a list.
        var scopeList = MemoryNaming.SplitCommaSeparated(scopes);
        var excludeList = MemoryNaming.SplitCommaSeparated(excludeTopics);
        if (scopeList is not { Count: > 0 } && excludeList is not { Count: > 0 })
            return store.GetEntityFactStore();
        return EntityFactDerivation.BuildStore(store.ListScoped(scopeList, excludeList));
    }
}
