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
    public Task<string> Facts(
        [Description("Operation: 'entities', 'profile', or 'intersect'.")] string op,
        [Description("profile: the target entity — 'key:value' or a bare value.")] string? entity = null,
        [Description("intersect: the entities (>=2) — each 'key:value' or a bare value.")] string[]? entities = null,
        [Description("entities: optional key filter, e.g. 'person'.")] string? key = null,
        [Description("Optional comma-separated scope order to narrow to (same semantics as search).")] string? scopes = null,
        [Description("Optional comma-separated topic names to exclude (same semantics as search).")] string? excludeTopics = null,
        [Description("Max rows for the entities list / profile timeline (default 50).")] int limit = 50,
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
                        "facts({ op: 'profile', entity: 'person:jolene' })").ToYaml());
                result = FactsQuery.Profile(facts, entity, limit);
                break;
            case "intersect":
                if (entities is null || entities.Length < 2)
                    return Task.FromResult(ResponseBuilder.Error(
                        "facts intersect requires 'entities' with at least 2 members.", ErrorCodes.InvalidParameter,
                        "facts({ op: 'intersect', entities: ['person:jolene', 'place:france'] })").ToYaml());
                result = FactsQuery.Intersect(facts, entities);
                break;
            default:
                return Task.FromResult(ResponseBuilder.Error(
                    $"unknown facts op '{op}'. Use 'entities', 'profile', or 'intersect'.",
                    ErrorCodes.InvalidParameter,
                    "facts({ op: 'entities' })").ToYaml());
        }

        return Task.FromResult(FactsResponseFormatter.ToYaml(result));
    }

    /// <summary>Cross-scope cached store when unfiltered; a scoped store built from the
    /// scope+excludeTopics-filtered candidates otherwise (same derivation as the auto-surface path).</summary>
    private static EntityFactStore SelectFactStore(IMemoryStore store, string? scopes, string? excludeTopics)
    {
        if (string.IsNullOrWhiteSpace(scopes) && string.IsNullOrWhiteSpace(excludeTopics))
            return store.GetEntityFactStore();

        var memories = store.ListScoped(scopes, excludeTopics)
            .Select(sa => EntityFactDerivation.Derive(sa, store.FormatRoundTripQualifiedName(sa.Scope, sa.Entry.Name)))
            .Where(m => m is not null)
            .Select(m => m!);
        return new EntityFactStore(memories);
    }
}
