using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Commands.Hooks;
using Scrinia.Mcp;
using Spectre.Console;

namespace Scrinia.Commands;

public partial class ScriniaCommands
{
    /// <summary>Start the MCP server (stdio transport).</summary>
    /// <param name="workspaceRoot">Workspace root for local memory store. Defaults to current working directory.</param>
    /// <param name="remote">Scrinia.Server URL for remote mode (e.g. http://localhost:5000).</param>
    /// <param name="apiKey">API key for remote server authentication.</param>
    /// <param name="store">Target store name on the remote server (default: "default").</param>
    /// <param name="noAutoSetup">Skip the first-run embedding model download. Without the model the server still runs but falls back to BM25-only search.</param>
    public async Task<int> Serve(
        string? workspaceRoot = null,
        string? remote = null,
        string? apiKey = null,
        string? store = null,
        bool noAutoSetup = false,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(remote))
        {
            // Remote mode → HttpMemoryStore. The server owns embeddings for remote memories;
            // do NOT load local CLI plugins here — that would spin up a local embeddings
            // pipeline (and write a stray .scrinia/ into cwd) for memories that don't live
            // in a local store.
            var httpClient = new HttpClient { BaseAddress = new Uri(remote) };
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey ?? "");
            MemoryStoreContext.Current = new HttpMemoryStore(httpClient, store ?? "default");
        }
        else
        {
            // Local mode (default) → FileMemoryStore
            WorkspaceSetup.Configure(workspaceRoot);

            // First-run convenience: if the embedding model isn't on disk yet, fetch it
            // before the MCP server starts up. Writes status to stderr only — stdout is
            // the JSON-RPC channel once the host begins. If download fails we degrade to
            // BM25-only search rather than blocking startup.
            if (!noAutoSetup)
                await EnsureEmbeddingModelAsync(cancellationToken);

            // Load CLI plugins (embeddings, etc.) — sets SearchContributorContext + MemoryEventSinkContext
            await WorkspaceSetup.LoadPluginsAsync(cancellationToken);
        }

        var builder = Host.CreateApplicationBuilder();

        // MCP servers communicate via stdio; keep the log channel quiet so protocol
        // messages on stdout/stderr are not corrupted by host framework log output.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Register the MCP server with stdio transport and our tool classes. The server
        // `instructions` field is read as guidance by tools-only CLIs (Codex/Copilot); prompts and
        // resources surface on clients that support them (Claude Code, Gemini, OpenCode).
        builder.Services
            .AddMcpServer(options => options.ServerInstructions = ScriniaPrimer.McpInstructions)
            .WithStdioServerTransport()
            // Pass tool serializer options that source-gen the Dictionary<string,string[]> attribute params;
            // the trimmed single-file publish has no reflection fallback, so WithTools<>() would otherwise
            // throw NotSupportedException building the memory_* tools at startup.
            .WithTools<ScriniaMcpTools>(ScriniaMcpTools.CreateToolSerializerOptions())
            .WithPrompts<ScriniaMcpPrompts>()
            .WithResources<ScriniaMcpResources>();

        var host = builder.Build();
        await host.RunAsync(cancellationToken);
        return 0;
    }
}
