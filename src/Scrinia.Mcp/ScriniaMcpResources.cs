using System.ComponentModel;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Encoding;

namespace Scrinia.Mcp;

/// <summary>
/// MCP resources exposed by the Scrinia server. Lets a stored memory be pulled into context as an
/// attachment via an <c>@</c>-mention on clients that honor MCP resources (Claude Code, OpenCode).
/// Tools-only clients (Codex, Copilot) ignore resources — they reach memories through the tools and
/// the server <c>instructions</c> field instead.
///
/// <para>Read-only: the resource resolves a memory name/path to its decoded text using the same
/// store + NMP/2 decode path the <c>memory_recall</c>/<c>show</c> tool uses.</para>
/// </summary>
[McpServerResourceType]
public sealed class ScriniaMcpResources
{
    [McpServerResource(
        UriTemplate = "scrinia://memory/{name}",
        Name = "scrinia-memory",
        MimeType = "text/markdown"),
     Description("Read a stored Scrinia memory by its name/path (as shown by memory_list, " +
                 "e.g. 'session-notes' or 'api:auth-flow').")]
    public static async Task<string> ReadMemory(
        [Description("Memory name or qualified path to read.")] string name,
        CancellationToken cancellationToken = default)
    {
        var store = MemoryStoreContext.Current
            ?? throw new InvalidOperationException("No memory store configured for this request.");

        // Same resolution + decode path as the show/recall tool: resolve to the NMP/2 artifact,
        // then decode to the original UTF-8 text. Translate a missing memory into a clean MCP error
        // rather than leaking a FileNotFoundException (mirrors REST's 404 in MemoryEndpoints).
        string artifact;
        try
        {
            artifact = await store.ResolveArtifactAsync(name, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            throw new McpException($"Memory '{name}' not found.");
        }

        byte[] bytes = Nmp2Strategy.Instance.Decode(artifact);
        return Encoding.UTF8.GetString(bytes);
    }
}
