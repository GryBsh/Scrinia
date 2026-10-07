using ConsoleAppFramework;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Commands;

public partial class ScriniaCommands
{
    /// <summary>Query entity facts.</summary>
    /// <param name="op">Operation: entities, profile, or intersect.</param>
    /// <param name="target">Entity for profile, or comma-separated entities for intersect; unused by entities.</param>
    /// <param name="key">entities: optional key filter, e.g. 'person'.</param>
    /// <param name="scopes">Comma-separated scopes; use MCP facts for a scope containing a comma.</param>
    /// <param name="excludeTopics">Comma-separated topics to exclude; use MCP facts for a topic containing a comma.</param>
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
        await EmbeddingsHost.LoadPluginsAsync(WorkspaceSetup.Root, cancellationToken);

        string normalizedOp = op.Trim().ToLowerInvariant();
        string? entity = normalizedOp == "profile" ? target : null;
        string[]? entities = normalizedOp == "intersect"
            ? (target ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;

        // Task 9, D-3: the CLI keeps comma-separated `--scopes`/`--exclude-topics` arguments on
        // purpose and splits at this boundary, same as `scri list`/`scri search`. A scope name
        // containing a comma is therefore unaddressable from `scri facts`, unlike the MCP `facts`
        // tool, which takes a real array.
        McpResponse response;
        try
        {
            response = await new ScriniaMcpTools().FactsResponse(
                op, entity, entities, key,
                MemoryNaming.SplitCommaSeparated(scopes), MemoryNaming.SplitCommaSeparated(excludeTopics),
                limit, cancellationToken);
        }
        catch (Scrinia.Core.Bundles.BundleImportException exception)
        {
            return EmitBundleFailure(exception, json, "facts");
        }
        catch (RestoreRecoveryException exception)
        {
            return EmitRecoveryBlocked(exception, json, "facts");
        }
        return EmitMcpResult(response, json, "facts");
    }
}
