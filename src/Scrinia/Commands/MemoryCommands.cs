using ConsoleAppFramework;

namespace Scrinia.Commands;

/// <summary>
/// Memory CRUD command group. Mirrors the MCP <c>memory(action=...)</c> dispatcher
/// so anything the agent can do through MCP, a human can do from the shell.
/// Wraps <see cref="ScriniaCommands"/> implementations (which are kept internal
/// to keep the top-level <c>--help</c> focused on lifecycle and infrastructure).
/// </summary>
public sealed class MemoryCommands
{
    private readonly ScriniaCommands _impl = new();

    /// <summary>List stored memories.</summary>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="scopes">Comma-separated scopes to list (e.g. local,api,ephemeral).</param>
    /// <param name="summary">Show summary (topics, keywords, stats) instead of full table.</param>
    /// <param name="offset">Starting index for paginated output (0-based).</param>
    /// <param name="limit">Maximum entries to show (0 = unlimited).</param>
    /// <param name="json">Output as JSON instead of a table.</param>
    public Task<int> List(string? workspaceRoot = null, string? scopes = null,
        bool summary = false, int offset = 0, int limit = 0, bool json = false)
        => _impl.List(workspaceRoot, scopes, summary, offset, limit, json);

    /// <summary>Search memories.</summary>
    /// <param name="query">Search query string.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="scopes">Comma-separated scopes to search.</param>
    /// <param name="limit">Maximum results.</param>
    /// <param name="json">Output as JSON instead of a table.</param>
    public Task<int> Search(
        [Argument] string query,
        string? workspaceRoot = null,
        string? scopes = null,
        int limit = 20,
        bool json = false,
        CancellationToken cancellationToken = default)
        => _impl.Search(query, workspaceRoot, scopes, limit, json, cancellationToken);

    /// <summary>Display memory content.</summary>
    /// <param name="name">Memory name to display (e.g. 'session-notes', 'api:auth-flow', '~scratch').</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="output">-o, Write output to a file instead of stdout.</param>
    /// <param name="json">Output as JSON instead of raw text.</param>
    public Task<int> Show(
        [Argument] string name,
        string? workspaceRoot = null,
        string? output = null,
        bool json = false,
        CancellationToken cancellationToken = default)
        => _impl.Show(name, workspaceRoot, output, json, cancellationToken);

    /// <summary>Store a file as a named memory.</summary>
    /// <param name="name">Memory name (e.g. 'session-notes', 'api:auth-flow', '~scratch').</param>
    /// <param name="file">File path to read content from. Use '-' or omit for stdin.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="description">-d, Description for the memory.</param>
    /// <param name="tags">-t, Comma-separated tags.</param>
    /// <param name="keywords">-k, Comma-separated keywords for search.</param>
    /// <param name="reviewAfter">ISO 8601 date after which this memory should be reviewed.</param>
    /// <param name="reviewWhen">Free-text condition for when this memory should be reviewed.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    public Task<int> Store(
        [Argument] string name,
        [Argument] string? file = null,
        string? workspaceRoot = null,
        string? description = null,
        string? tags = null,
        string? keywords = null,
        string? reviewAfter = null,
        string? reviewWhen = null,
        bool json = false,
        CancellationToken cancellationToken = default)
        => _impl.Store(name, file, workspaceRoot, description, tags, keywords,
            reviewAfter, reviewWhen, json, cancellationToken);

    /// <summary>Delete a stored memory.</summary>
    /// <param name="name">Memory name to delete.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    public Task<int> Forget(
        [Argument] string name,
        string? workspaceRoot = null,
        bool json = false,
        CancellationToken cancellationToken = default)
        => _impl.Forget(name, workspaceRoot, json, cancellationToken);

    /// <summary>Append a new chunk to an existing memory.</summary>
    /// <param name="name">Memory name to append to (e.g. 'session-notes', '/sessions/2026-05-13').</param>
    /// <param name="file">File path to read content from. Use '-' or omit for stdin.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    public Task<int> Append(
        [Argument] string name,
        [Argument] string? file = null,
        string? workspaceRoot = null,
        bool json = false,
        CancellationToken cancellationToken = default)
        => _impl.Append(name, file, workspaceRoot, json, cancellationToken);

    /// <summary>Compact a multi-chunk memory by merging chunks. Archives the original version.</summary>
    /// <param name="name">Memory name to compact.</param>
    /// <param name="keepRecent">-k, Keep only the N most recent chunks. 0 = merge all into one.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    public Task<int> Compact(
        [Argument] string name,
        int keepRecent = 0,
        string? workspaceRoot = null,
        bool json = false,
        CancellationToken cancellationToken = default)
        => _impl.Compact(name, keepRecent, workspaceRoot, json, cancellationToken);

    /// <summary>Create a bidirectional link between two memories.</summary>
    /// <param name="from">Source memory name.</param>
    /// <param name="to">Target memory name.</param>
    /// <param name="reason">-r, Optional reason for the connection.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    public Task<int> Link(
        [Argument] string from,
        [Argument] string to,
        string? reason = null,
        string? workspaceRoot = null,
        bool json = false,
        CancellationToken cancellationToken = default)
        => _impl.Link(from, to, reason, workspaceRoot, json, cancellationToken);
}
