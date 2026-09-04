using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Scrinia.Mcp.SharedContracts;

[McpServerToolType]
public sealed class SharedTools
{
    [McpServerTool(Name = "shared_bases"), Description("List enabled shared bases.")]
    public Task<string> Bases(CancellationToken cancellationToken = default) => Unsupported();
    [McpServerTool(Name = "shared_search"), Description("Search enabled shared bases.")]
    public Task<string> Search(string query, string? @base = null, int limit = 20, string? pathPrefix = null, Dictionary<string, string[]>? attributeFilter = null, string? context = null, string[]? excludeTopics = null, CancellationToken cancellationToken = default) => Unsupported();
    [McpServerTool(Name = "shared_list"), Description("List persistent entries in enabled shared bases.")]
    public Task<string> List(string? @base = null, int offset = 0, int limit = 50, string mode = "summary", string[]? excludeTopics = null, CancellationToken cancellationToken = default) => Unsupported();
    [McpServerTool(Name = "shared_recall"), Description("Recall one persistent entry from a shared base.")]
    public Task<string> Recall(string @base, string path, int? chunk = null, CancellationToken cancellationToken = default) => Unsupported();
    [McpServerTool(Name = "shared_facts"), Description("Query facts from one shared base.")]
    public Task<string> Facts(string @base, string op, string? entity = null, string[]? entities = null, string? key = null, int limit = 50, string[]? excludeTopics = null, CancellationToken cancellationToken = default) => Unsupported();
    [McpServerTool(Name = "shared_remember"), Description("Store persistent content in one shared base.")]
    public Task<string> Remember(string @base, string path, string[] content, Dictionary<string, string[]>? attributes = null, string[]? keywords = null, CancellationToken cancellationToken = default) => Unsupported();
    [McpServerTool(Name = "shared_append"), Description("Append persistent content in one shared base.")]
    public Task<string> Append(string @base, string path, string content, Dictionary<string, string[]>? attributes = null, string[]? keywords = null, CancellationToken cancellationToken = default) => Unsupported();
    [McpServerTool(Name = "shared_forget"), Description("Forget one persistent entry from a shared base.")]
    public Task<string> Forget(string @base, string path, CancellationToken cancellationToken = default) => Unsupported();
    [McpServerTool(Name = "shared_compact"), Description("Compact one persistent entry in a shared base.")]
    public Task<string> Compact(string @base, string path, int keepRecent = 0, CancellationToken cancellationToken = default) => Unsupported();
    [McpServerTool(Name = "shared_link"), Description("Link two persistent entries in one shared base.")]
    public Task<string> Link(string @base, string path, string destination, string? reason = null, CancellationToken cancellationToken = default) => Unsupported();
    [McpServerTool(Name = "shared_refresh"), Description("Refresh one enabled shared base.")]
    public Task<string> Refresh(string @base, CancellationToken cancellationToken = default) => Unsupported();

    private static Task<string> Unsupported() => Task.FromException<string>(new NotSupportedException("Shared tool handlers are supplied by the shared server."));
}
