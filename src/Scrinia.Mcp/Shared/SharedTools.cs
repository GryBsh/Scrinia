using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Scrinia.Mcp.SharedContracts;

/// <summary>
/// The ten prefixed shared-memory MCP tools. This surface is prompt-isolated and process-free:
/// every input is an operation argument (query, base, path, manifest, …) and none of them is a
/// prompt, a correlation identifier, or a session identifier (§7.3). No handler branches on, or
/// forwards, the initiating prompt text, and the shared surface never launches a process — its sole
/// Git contact is the print-only, operation-based staging guidance in <c>SharedGitGuidance</c>.
/// SharedServer dispatches these contracts through Core's admitted durable operations while
/// preserving these two guarantees (see <c>SharedNoPromptCorrelationTests</c> and
/// <c>SharedNoGitProcessTests</c>).
/// </summary>
[McpServerToolType]
public sealed class SharedTools
{
    private readonly SharedServer? _server;
    public SharedTools(SharedServer? server = null) => _server = server;
    private SharedServer Server => _server ?? throw new InvalidOperationException("The shared server is not configured.");
    [McpServerTool(Name = "shared_bases"), Description("List enabled shared bases.")]
    public Task<string> Bases(CancellationToken cancellationToken = default) => Task.FromResult(Server.Bases(cancellationToken).ToYaml());
    [McpServerTool(Name = "shared_search"), Description("Search enabled shared bases.")]
    public Task<string> Search(string query, string? @base = null, int limit = 20, string? pathPrefix = null, Dictionary<string, string[]>? attributeFilter = null, string? context = null, string[]? excludeTopics = null, CancellationToken cancellationToken = default) => Task.FromResult(Server.Search(query, @base, limit, pathPrefix, attributeFilter, context, excludeTopics, cancellationToken).ToYaml());
    [McpServerTool(Name = "shared_list"), Description("List persistent entries in enabled shared bases.")]
    public Task<string> List(string? @base = null, int offset = 0, int limit = 50, string mode = "summary", string[]? excludeTopics = null, CancellationToken cancellationToken = default) => Task.FromResult(Server.List(@base, offset, limit, mode, excludeTopics, cancellationToken).ToYaml());
    [McpServerTool(Name = "shared_recall"), Description("Recall one persistent entry from a shared base.")]
    public Task<string> Recall(string @base, string path, int? chunk = null, CancellationToken cancellationToken = default) => Task.FromResult(Server.Recall(@base, path, chunk, cancellationToken).ToYaml());
    [McpServerTool(Name = "shared_facts"), Description("Query facts from one shared base.")]
    public Task<string> Facts(string @base, string op, string? entity = null, string[]? entities = null, string? key = null, int limit = 50, string[]? excludeTopics = null, CancellationToken cancellationToken = default) => Task.FromResult(Server.Facts(@base, op, entity, entities, key, limit, excludeTopics, cancellationToken).ToYaml());
    [McpServerTool(Name = "shared_remember"), Description("Add chunks to a memory in one shared base, creating it if missing. Preserves existing content unless replace is explicitly true. Preserve explicitly stated details of the facts you store, including dates, durations, quantities, and units. Keep qualifiers attached to their facts, preserving distinctions such as another event versus the same event, planned versus completed, and still true versus no longer true; do not infer missing values or event relationships. Separate existing facts from goals to change them; retain a short source quotation when a paraphrase could change the meaning.")]
    public Task<string> Remember(string @base, string path, string[] content, Dictionary<string, string[]>? attributes = null, string[]? keywords = null, [Description("Set true only to replace the entire memory; false appends the supplied chunks.")] bool replace = false, CancellationToken cancellationToken = default) => Task.FromResult(Server.Mutate(@base, path, replace ? "remember" : "append", content, attributes, keywords, null, null, 0, cancellationToken).ToYaml());
    internal Task<string> Append(string @base, string path, string content, Dictionary<string, string[]>? attributes = null, string[]? keywords = null, CancellationToken cancellationToken = default) => Task.FromResult(Server.Mutate(@base, path, "append", [content], attributes, keywords, null, null, 0, cancellationToken).ToYaml());
    [McpServerTool(Name = "shared_forget"), Description("Forget one persistent entry from a shared base.")]
    public Task<string> Forget(string @base, string path, CancellationToken cancellationToken = default) => Task.FromResult(Server.Mutate(@base, path, "forget", null, null, null, null, null, 0, cancellationToken).ToYaml());
    [McpServerTool(Name = "shared_compact"), Description("Compact one persistent entry in a shared base.")]
    public Task<string> Compact(string @base, string path, int keepRecent = 0, CancellationToken cancellationToken = default) => Task.FromResult(Server.Mutate(@base, path, "compact", null, null, null, null, null, keepRecent, cancellationToken).ToYaml());
    [McpServerTool(Name = "shared_link"), Description("Link two persistent entries in one shared base.")]
    public Task<string> Link(string @base, string path, string destination, string? reason = null, CancellationToken cancellationToken = default) => Task.FromResult(Server.Mutate(@base, path, "link", null, null, null, destination, reason, 0, cancellationToken).ToYaml());
    [McpServerTool(Name = "shared_refresh"), Description("Refresh one enabled shared base.")]
    public Task<string> Refresh(string @base, CancellationToken cancellationToken = default) => Task.FromResult(Server.Refresh(@base, cancellationToken).ToYaml());
}
