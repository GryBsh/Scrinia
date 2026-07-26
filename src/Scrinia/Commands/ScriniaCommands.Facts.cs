using ConsoleAppFramework;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Commands;

public partial class ScriniaCommands
{
    /// <summary>Query the entity fact store: entities | profile | intersect.</summary>
    /// <param name="op">Operation: 'entities', 'profile', or 'intersect'.</param>
    /// <param name="target">profile: the entity ('key:value' or bare value). intersect: comma-separated entities (e.g. 'person:jolene,place:france'). Ignored for 'entities'.</param>
    /// <param name="key">entities: optional key filter, e.g. 'person'.</param>
    /// <param name="scopes">Comma-separated scopes to narrow to (same semantics as search).</param>
    /// <param name="excludeTopics">Comma-separated topic names to exclude (same semantics as search).</param>
    /// <param name="limit">Max rows for the entities list / profile timeline.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="json">Output as JSON instead of raw YAML.</param>
    public async Task<int> Facts(
        [Argument] string op,
        [Argument] string? target = null,
        string? key = null,
        string? scopes = null,
        string? excludeTopics = null,
        int limit = 50,
        string? workspaceRoot = null,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);
        await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

        string normalizedOp = (op ?? string.Empty).Trim().ToLowerInvariant();
        string? entity = normalizedOp == "profile" ? target : null;
        string[]? entities = normalizedOp == "intersect"
            ? (target ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;

        string yaml = await new ScriniaMcpTools().Facts(
            op!, entity, entities, key, scopes, excludeTopics, limit, cancellationToken);
        return EmitMcpResult(yaml, json, "facts");
    }
}
