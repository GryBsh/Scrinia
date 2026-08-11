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
    private static void WriteJson<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        Console.Write(JsonSerializer.Serialize(value, typeInfo));
        Console.WriteLine();
    }

    private static void WriteJsonError(string message)
    {
        WriteJson(new CliErrorOutput(message), CliJsonContext.Default.CliErrorOutput);
    }

    /// <summary>
    /// Human-facing rendering of an error envelope: the message, then one indented bullet per
    /// <see cref="McpResponse.ActionNeeded"/> entry.
    /// </summary>
    /// <remarks>
    /// The recovery hints are the difference between "not found" and "not found — here is how to
    /// find it", so the terminal must not drop them. They are deliberately absent from the
    /// <c>--json</c> path: <see cref="CliErrorOutput"/> is the shared machine-readable error
    /// envelope for every command, and its single <c>error</c> field stays a clean message rather
    /// than carrying embedded newlines and bullet prose.
    /// </remarks>
    internal static string FormatCliError(McpResponse r)
    {
        string message = r.Error ?? "unknown error";
        if (r.ActionNeeded is not { Length: > 0 })
            return message;

        var sb = new System.Text.StringBuilder(message);
        foreach (string hint in r.ActionNeeded)
            sb.Append("\n  - ").Append(hint);
        return sb.ToString();
    }

    /// <summary>
    /// Writes a response envelope in its status-appropriate colour and returns the process exit
    /// code. Every non-JSON envelope print goes through here: printing a warning in green and
    /// returning 0 reported "matched nothing" as success on four separate commands.
    /// </summary>
    private static int RenderEnvelope(McpResponse response, string yaml)
    {
        string colour = response.IsWarning ? "yellow" : "green";
        AnsiConsole.MarkupLine($"[{colour}]{Markup.Escape(yaml)}[/]");
        return response.IsWarning ? 1 : 0;
    }

    // ── MCP-passthrough commands ────────────────────────────────────────────
    //
    // Each of the six commands below wires the CLI directly into the same
    // ScriniaMcpTools handler that the MCP server invokes. Output is the raw
    // YAML response (terminal-friendly when read by humans, parseable by tools);
    // --json wraps the response in CliMcpOutput so callers get a stable shape.

    private static int EmitMcpResult(McpResponse r, bool json, string action)
    {
        // A warning envelope means the command completed without doing what was asked — an empty
        // import, an entity that matched nothing, an update specifying no changes. Exiting 0 for
        // that reported a no-op as success to any script gating on this command.
        bool failed = r.IsError || r.IsWarning;

        if (json)
        {
            // --json output is unchanged: CliMcpOutput still carries the rendered YAML.
            WriteJson(new CliMcpOutput(action, r.Status, r.ToYaml()), CliJsonContext.Default.CliMcpOutput);
        }
        else
        {
            // Pass YAML straight through — same pattern as Show.
            string yaml = r.ToYaml();
            Console.Write(yaml);
            if (!yaml.EndsWith('\n')) Console.WriteLine();
        }
        return failed ? 1 : 0;
    }

    /// <summary>Print the embedded agent guide.</summary>
    /// <param name="json">Output as JSON instead of raw markdown.</param>
    public async Task<int> Guide(
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        var tools = new ScriniaMcpTools();
        string yaml = await tools.Guide(cancellationToken);

        if (json)
        {
            WriteJson(new CliMcpOutput("guide", "success", yaml), CliJsonContext.Default.CliMcpOutput);
            return 0;
        }

        // Human-friendly: print the raw guide markdown instead of the YAML envelope.
        string? guide = EmbeddedPrompts.LoadGuide();
        Console.Write(guide ?? yaml);
        if (guide is not null && !guide.EndsWith('\n')) Console.WriteLine();
        return 0;
    }

    /// <summary>Resume agent context — profile, patterns, today's session log, available skills.</summary>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    /// <param name="hook">Emit the agent-CLI hook envelope (JSON with hookSpecificOutput.additionalContext wrapping the YAML payload in &lt;scrinia-restored-memory&gt; tags with an imperative framing line). Used by `scri setup --hooks` SessionStart wiring. Human invocations of `scri restore` should leave this off — the YAML default is more readable.</param>
    /// <param name="client">Agent CLI the hook payload is being formatted for (codex, claude, opencode, copilot). Only meaningful with --hook; tailors verbosity/envelope per client. Omit for the reference format.</param>
    public async Task<int> Restore(
        string? workspaceRoot = null,
        bool json = false,
        bool hook = false,
        string? client = null,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);
        WorkspaceSetup.WarnOnOrphanStores();
        await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

        var tools = new ScriniaMcpTools();
        McpResponse response = await tools.Restore(cancellationToken);

        if (hook)
        {
            // Client-tailored restore payload (terser framing for Claude, bare payload for
            // envelope-less clients). The agent CLI injects additionalContext into the model's
            // context off-transcript; no --client → the frozen reference shape.
            Console.WriteLine(HintCommand.FormatRestoreHook(response.ToYaml(), HintCommand.ParseClient(client)));
            return 0;
        }

        return EmitMcpResult(response, json, "restore");
    }

    /// <summary>Scan for merge conflicts in .scrinia/ or resolve a specific conflict.</summary>
    /// <param name="conflictId">Workspace-relative path under .scrinia/ to the conflicted file (e.g. "memories/local/auth-flow.nmp2"). Omit to scan.</param>
    /// <param name="choice">Resolution when resolving: 'ours', 'theirs', or 'merged'.</param>
    /// <param name="mergedContent">Content for 'merged' resolution. Use '-' for stdin.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    public async Task<int> Reconcile(
        string? conflictId = null,
        string? choice = null,
        string? mergedContent = null,
        string? workspaceRoot = null,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);
        await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

        // Allow piping the merged content for 'merged' resolution.
        if (mergedContent == "-")
        {
            if (!Console.IsInputRedirected)
            {
                if (json) { WriteJsonError("--merged-content '-' requires stdin to be redirected."); return 1; }
                AnsiConsole.MarkupLine("[red]Error:[/] --merged-content '-' requires stdin to be redirected.");
                return 1;
            }
            mergedContent = await Console.In.ReadToEndAsync(cancellationToken);
        }

        var tools = new ScriniaMcpTools();
        McpResponse response = await tools.Reconcile(conflictId, choice, mergedContent, cancellationToken);
        return EmitMcpResult(response, json, "reconcile");
    }

    private static List<string> ResolveFiles(string filesArg)
    {
        var result = new List<string>();

        // Try comma-separated first
        string[] parts = filesArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string part in parts)
        {
            // Check if it contains wildcard characters (glob)
            if (part.Contains('*') || part.Contains('?'))
            {
                string directory = Path.GetDirectoryName(part) ?? ".";
                string pattern = Path.GetFileName(part);
                if (Directory.Exists(directory))
                {
                    result.AddRange(Directory.GetFiles(directory, pattern));
                }
            }
            else if (File.Exists(part))
            {
                result.Add(Path.GetFullPath(part));
            }
        }

        return result;
    }
}
