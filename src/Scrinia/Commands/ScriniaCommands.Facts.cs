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
    /// <param name="scopes">Comma-separated scopes to narrow to (same semantics as search). A
    /// scope name containing a comma cannot be addressed this way; use the MCP `facts` tool
    /// instead, which takes scopes as a real array.</param>
    /// <param name="excludeTopics">Comma-separated topic names to exclude (same semantics as
    /// search). A topic name containing a comma cannot be addressed this way; use the MCP `facts`
    /// tool instead.</param>
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

        string normalizedOp = op.Trim().ToLowerInvariant();
        string? entity = normalizedOp == "profile" ? target : null;
        string[]? entities = normalizedOp == "intersect"
            ? (target ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;

        // Task 9, D-3: the CLI keeps comma-separated `--scopes`/`--exclude-topics` arguments on
        // purpose and splits at this boundary, same as `scri list`/`scri search`. A scope name
        // containing a comma is therefore unaddressable from `scri facts`, unlike the MCP `facts`
        // tool, which takes a real array.
        McpResponse response = await new ScriniaMcpTools().FactsResponse(
            op, entity, entities, key,
            MemoryNaming.SplitCommaSeparated(scopes), MemoryNaming.SplitCommaSeparated(excludeTopics),
            limit, cancellationToken);
        return EmitMcpResult(response, json, "facts");
    }
}
