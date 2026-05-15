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
using Scrinia.Core.Llm;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Commands.Hooks;
using Scrinia.Mcp;
using Spectre.Console;

namespace Scrinia.Commands;

public class ScriniaCommands
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
            // Remote mode → HttpMemoryStore
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
        }

        // Load CLI plugins (embeddings, etc.) — sets SearchContributorContext + MemoryEventSinkContext
        await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

        var builder = Host.CreateApplicationBuilder();

        // MCP servers communicate via stdio; keep the log channel quiet so protocol
        // messages on stdout/stderr are not corrupted by host framework log output.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Register the MCP server with stdio transport and our tool classes.
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<ScriniaMcpTools>();

        var host = builder.Build();
        await host.RunAsync(cancellationToken);
        return 0;
    }

    /// <summary>List stored memories.</summary>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="scopes">Comma-separated scopes to list (e.g. local,api,ephemeral).</param>
    /// <param name="summary">Show summary (topics, keywords, stats) instead of full table.</param>
    /// <param name="offset">Starting index for paginated output (0-based).</param>
    /// <param name="limit">Maximum entries to show (0 = unlimited).</param>
    /// <param name="json">Output as JSON instead of a table.</param>
    internal Task<int> List(string? workspaceRoot = null, string? scopes = null,
        bool summary = false, int offset = 0, int limit = 0, bool json = false)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        var entries = ScriniaArtifactStore.ListScoped(scopes);
        if (entries.Count == 0)
        {
            if (json)
                WriteJson(new CliListOutput([], 0, null), CliJsonContext.Default.CliListOutput);
            else
                AnsiConsole.MarkupLine("[yellow]No memories stored.[/]");
            return Task.FromResult(0);
        }

        entries.Sort((a, b) => b.Entry.CreatedAt.CompareTo(a.Entry.CreatedAt));

        if (summary)
        {
            var summaryData = BuildCliSummary(entries, json);
            if (json)
                WriteJson(summaryData, CliJsonContext.Default.CliListSummaryOutput);
            else
                AnsiConsole.Write(new Markup(summaryData.Rendered!));
            return Task.FromResult(0);
        }

        // Apply pagination
        int total = entries.Count;
        if (offset > 0) entries = entries.Skip(offset).ToList();
        if (limit > 0) entries = entries.Take(limit).ToList();

        if (json)
        {
            var items = entries.Select(item =>
            {
                var e = item.Entry;
                string name = item.Scope == "ephemeral"
                    ? $"~{e.Name}"
                    : ScriniaArtifactStore.FormatQualifiedName(item.Scope, e.Name);
                bool isStale = e.ReviewAfter.HasValue && e.ReviewAfter.Value <= DateTimeOffset.UtcNow;
                bool needsReview = !isStale && !string.IsNullOrEmpty(e.ReviewWhen);
                return new CliMemoryEntry(
                    name, e.ChunkCount, e.OriginalBytes, (int)(e.OriginalBytes / 4),
                    e.CreatedAt.ToString("o"), e.UpdatedAt?.ToString("o"),
                    e.Description, e.Tags, e.ReviewAfter?.ToString("o"), e.ReviewWhen,
                    isStale, needsReview);
            }).ToArray();
            WriteJson(new CliListOutput(items, total, null), CliJsonContext.Default.CliListOutput);
            return Task.FromResult(0);
        }

        if (offset > 0 || limit > 0)
        {
            int showEnd = offset + entries.Count;
            AnsiConsole.MarkupLine($"[dim]Showing {offset + 1}-{showEnd} of {total} memories.[/]");
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Name")
            .AddColumn(new TableColumn("Chunks").RightAligned())
            .AddColumn(new TableColumn("Size").RightAligned())
            .AddColumn(new TableColumn("~Tokens").RightAligned())
            .AddColumn("Created")
            .AddColumn("Description");

        foreach (var item in entries)
        {
            var e = item.Entry;
            string name = item.Scope == "ephemeral"
                ? $"~{e.Name}"
                : ScriniaArtifactStore.FormatQualifiedName(item.Scope, e.Name);

            int estTokens = (int)(e.OriginalBytes / 4);

            // Review markers
            string reviewPrefix = "";
            if (e.ReviewAfter.HasValue && e.ReviewAfter.Value <= DateTimeOffset.UtcNow)
                reviewPrefix = "[stale] ";
            else if (!string.IsNullOrEmpty(e.ReviewWhen))
                reviewPrefix = "[review?] ";

            string desc = e.Description.Replace('\n', ' ').Replace('\r', ' ');
            string fullDesc = reviewPrefix + desc;
            if (fullDesc.Length > 60) fullDesc = fullDesc[..57] + "...";

            table.AddRow(
                Markup.Escape(name),
                e.ChunkCount.ToString(),
                ScriniaMcpTools.FormatBytes(e.OriginalBytes),
                estTokens.ToString(),
                e.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                Markup.Escape(fullDesc));
        }

        AnsiConsole.Write(table);
        return Task.FromResult(0);
    }

    private static CliListSummaryOutput BuildCliSummary(List<ScopedArtifact> entries, bool forJson)
    {
        long totalBytes = entries.Sum(e => e.Entry.OriginalBytes);
        int totalTokens = (int)(totalBytes / 4);
        int staleCount = entries.Count(e => e.Entry.ReviewAfter.HasValue && e.Entry.ReviewAfter.Value <= DateTimeOffset.UtcNow);
        int reviewCount = entries.Count(e => !string.IsNullOrEmpty(e.Entry.ReviewWhen)
            && !(e.Entry.ReviewAfter.HasValue && e.Entry.ReviewAfter.Value <= DateTimeOffset.UtcNow));
        int ephemeralCount = entries.Count(e => e.Scope == "ephemeral");

        var grouped = entries
            .Where(e => e.Scope != "ephemeral")
            .GroupBy(e => MemoryNaming.FormatScopeLabel(e.Scope))
            .OrderBy(g => g.Key)
            .ToList();

        int topicCount = grouped.Count(g => g.Key != "local");

        // Build scopes list
        var scopeEntries = grouped.Select(g =>
        {
            string label = g.Key == "local" ? "local" : $"topic:{g.Key}";
            return new CliScopeEntry(label, g.Count(), g.Sum(e => e.Entry.OriginalBytes));
        }).ToList();
        if (ephemeralCount > 0)
            scopeEntries.Add(new CliScopeEntry("ephemeral", ephemeralCount, entries.Where(e => e.Scope == "ephemeral").Sum(e => e.Entry.OriginalBytes)));

        // Top keywords
        var keywordCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in entries)
        {
            if (item.Entry.Keywords is { Length: > 0 })
                foreach (var kw in item.Entry.Keywords)
                    keywordCounts[kw] = keywordCounts.GetValueOrDefault(kw) + 1;
            if (item.Entry.Tags is { Length: > 0 })
                foreach (var tag in item.Entry.Tags)
                    keywordCounts[tag] = keywordCounts.GetValueOrDefault(tag) + 1;
        }
        var topKeywords = keywordCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(kv => kv.Key)
            .ToArray();

        // Build rendered text for CLI display
        string? rendered = null;
        if (!forJson)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[bold]Memory Summary[/]");
            sb.AppendLine($"[blue]{entries.Count} memories[/] — {ScriniaMcpTools.FormatBytes(totalBytes)} (~{totalTokens:N0} tokens)");
            var parts = new List<string>();
            if (topicCount > 0) parts.Add($"{topicCount} topic{(topicCount == 1 ? "" : "s")}");
            if (ephemeralCount > 0) parts.Add($"{ephemeralCount} ephemeral");
            if (staleCount > 0) parts.Add($"[red]{staleCount} stale[/]");
            if (reviewCount > 0) parts.Add($"[yellow]{reviewCount} need review[/]");
            if (parts.Count > 0) sb.AppendLine(string.Join(" · ", parts));
            sb.AppendLine();
            sb.AppendLine("[bold]Scopes[/]");
            foreach (var scope in scopeEntries)
                sb.AppendLine($"  [dim]•[/] [green]{Markup.Escape(scope.Name)}[/] — {scope.Count} {(scope.Count == 1 ? "memory" : "memories")}, {ScriniaMcpTools.FormatBytes(scope.TotalBytes)}");
            if (topKeywords.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("[bold]Top keywords[/]");
                sb.AppendLine($"  {Markup.Escape(string.Join(", ", topKeywords))}");
            }
            rendered = sb.ToString();
        }

        return new CliListSummaryOutput(
            entries.Count, totalBytes, totalTokens, topicCount, ephemeralCount,
            staleCount, reviewCount,
            scopeEntries.ToArray(),
            topKeywords.Length > 0 ? topKeywords : null,
            rendered);
    }

    /// <summary>Search memories.</summary>
    /// <param name="query">Search term to match against memory names and descriptions.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="scopes">Comma-separated scopes to search (e.g. local,api,ephemeral).</param>
    /// <param name="limit">Maximum results to return.</param>
    /// <param name="json">Output as JSON instead of a table.</param>
    internal async Task<int> Search([Argument] string query, string? workspaceRoot = null, string? scopes = null, int limit = 20, bool json = false, CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);
        await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

        var matches = ScriniaArtifactStore.SearchAll(query, scopes, limit);
        if (matches.Count == 0)
        {
            if (json)
                WriteJson(new CliSearchOutput([], 0, query), CliJsonContext.Default.CliSearchOutput);
            else
                AnsiConsole.MarkupLine("[yellow]No matching memories found.[/]");
            return 0;
        }

        if (json)
        {
            var results = matches.Select<SearchResult, CliSearchResult>(match => match switch
            {
                ChunkEntryResult cr => new CliSearchResult("chunk",
                    cr.ParentItem.Scope == "ephemeral" ? $"~{cr.ParentItem.Entry.Name}" : ScriniaArtifactStore.FormatQualifiedName(cr.ParentItem.Scope, cr.ParentItem.Entry.Name),
                    cr.Score, (int)(cr.ParentItem.Entry.OriginalBytes / cr.TotalChunks / 4),
                    cr.Chunk.ContentPreview ?? cr.ParentItem.Entry.Description,
                    cr.Chunk.ChunkIndex, cr.TotalChunks),
                EntryResult er => new CliSearchResult("entry",
                    er.Item.Scope == "ephemeral" ? $"~{er.Item.Entry.Name}" : ScriniaArtifactStore.FormatQualifiedName(er.Item.Scope, er.Item.Entry.Name),
                    er.Score, (int)(er.Item.Entry.OriginalBytes / 4),
                    er.Item.Entry.Description, null, null),
                TopicResult tr => new CliSearchResult("topic",
                    MemoryNaming.FormatScopeLabel(tr.Scope),
                    tr.Score, 0, tr.Description, null, null),
                _ => new CliSearchResult("unknown", "", 0, 0, "", null, null),
            }).ToArray();
            WriteJson(new CliSearchOutput(results, results.Length, query), CliJsonContext.Default.CliSearchOutput);
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Type")
            .AddColumn("Name")
            .AddColumn(new TableColumn("Score").RightAligned())
            .AddColumn(new TableColumn("~Tokens").RightAligned())
            .AddColumn("Description");

        foreach (var match in matches)
        {
            if (match is ChunkEntryResult cr)
            {
                string name = cr.ParentItem.Scope == "ephemeral"
                    ? $"~{cr.ParentItem.Entry.Name}"
                    : ScriniaArtifactStore.FormatQualifiedName(cr.ParentItem.Scope, cr.ParentItem.Entry.Name);
                string chunkLabel = $"{name} [chunk {cr.Chunk.ChunkIndex}/{cr.TotalChunks}]";
                string desc = cr.Chunk.ContentPreview ?? cr.ParentItem.Entry.Description;
                desc = desc.Replace('\n', ' ').Replace('\r', ' ');
                if (desc.Length > 60) desc = desc[..57] + "...";
                int estTokens = (int)(cr.ParentItem.Entry.OriginalBytes / cr.TotalChunks / 4);

                table.AddRow("chunk", Markup.Escape(chunkLabel), $"{cr.Score:F0}", estTokens.ToString(), Markup.Escape(desc));
            }
            else if (match is EntryResult er)
            {
                string name = er.Item.Scope == "ephemeral"
                    ? $"~{er.Item.Entry.Name}"
                    : ScriniaArtifactStore.FormatQualifiedName(er.Item.Scope, er.Item.Entry.Name);
                string desc = er.Item.Entry.Description.Replace('\n', ' ').Replace('\r', ' ');
                if (desc.Length > 60) desc = desc[..57] + "...";
                int estTokens = (int)(er.Item.Entry.OriginalBytes / 4);

                table.AddRow("entry", Markup.Escape(name), $"{er.Score:F0}", estTokens.ToString(), Markup.Escape(desc));
            }
            else if (match is TopicResult tr)
            {
                string label = MemoryNaming.FormatScopeLabel(tr.Scope);
                string desc = tr.Description.Replace('\n', ' ').Replace('\r', ' ');
                if (desc.Length > 60) desc = desc[..57] + "...";

                table.AddRow("topic", Markup.Escape(label), $"{tr.Score:F0}", "", Markup.Escape(desc));
            }
        }

        AnsiConsole.Write(table);
        return 0;
    }

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
    internal async Task<int> Store(
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
    {
        WorkspaceSetup.Configure(workspaceRoot);
        await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

        string content;
        if (string.IsNullOrEmpty(file) || file == "-")
        {
            if (!Console.IsInputRedirected)
            {
                if (json) { WriteJsonError("No file specified and stdin is not redirected."); return 1; }
                AnsiConsole.MarkupLine("[red]Error:[/] No file specified and stdin is not redirected.");
                AnsiConsole.MarkupLine("Usage: scri store <name> <file> or pipe content via stdin.");
                return 1;
            }
            content = await Console.In.ReadToEndAsync(cancellationToken);
        }
        else
        {
            if (!File.Exists(file))
            {
                if (json) { WriteJsonError($"File not found: {file}"); return 1; }
                AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(file)}");
                return 1;
            }
            content = await File.ReadAllTextAsync(file, cancellationToken);
        }

        string[]? tagArray = tags?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[]? keywordArray = keywords?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var tools = new ScriniaMcpTools();
        string result = await tools.Store([content], name, description ?? "", tagArray,
            keywordArray, reviewAfter, reviewWhen, cancellationToken: cancellationToken);

        if (json)
        {
            var (scope, subject) = ScriniaArtifactStore.ParseQualifiedName(name);
            string qualifiedName = ScriniaArtifactStore.FormatQualifiedName(scope, subject);
            long bytes = System.Text.Encoding.UTF8.GetByteCount(content);
            WriteJson(new CliStoreOutput(qualifiedName, 1, bytes, result),
                CliJsonContext.Default.CliStoreOutput);
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]{Markup.Escape(result)}[/]");
        }
        return 0;
    }

    /// <summary>Display memory content.</summary>
    /// <param name="name">Memory name to display (e.g. 'session-notes', 'api:auth-flow', '~scratch').</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="output">-o, Write output to a file instead of stdout.</param>
    /// <param name="json">Output as JSON instead of raw text.</param>
    internal async Task<int> Show(
        [Argument] string name,
        string? workspaceRoot = null,
        string? output = null,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        var tools = new ScriniaMcpTools();
        string result = await tools.Show(name, cancellationToken: cancellationToken);

        if (result.StartsWith("Error:", StringComparison.Ordinal))
        {
            if (json) { WriteJsonError(result); return 1; }
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(result)}[/]");
            return 1;
        }

        if (json)
        {
            WriteJson(new CliShowOutput(name, result, result.Length), CliJsonContext.Default.CliShowOutput);
            return 0;
        }

        if (!string.IsNullOrEmpty(output))
        {
            await File.WriteAllTextAsync(output, result, cancellationToken);
            AnsiConsole.MarkupLine($"[green]Written to {Markup.Escape(output)}[/]");
        }
        else
        {
            Console.Write(result);
        }

        return 0;
    }

    /// <summary>Delete a stored memory.</summary>
    /// <param name="name">Memory name to delete (e.g. 'session-notes', 'api:auth-flow', '~scratch').</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    internal async Task<int> Forget(
        [Argument] string name,
        string? workspaceRoot = null,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);
        await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

        var tools = new ScriniaMcpTools();
        string result = await tools.Forget(name, cancellationToken);

        if (result.StartsWith("Error:", StringComparison.Ordinal))
        {
            if (json) { WriteJsonError(result); return 1; }
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(result)}[/]");
            return 1;
        }

        if (json)
            WriteJson(new CliForgetOutput(name, true, result), CliJsonContext.Default.CliForgetOutput);
        else
            AnsiConsole.MarkupLine($"[green]{Markup.Escape(result)}[/]");
        return 0;
    }

    // ── MCP-passthrough commands ────────────────────────────────────────────
    //
    // Each of the six commands below wires the CLI directly into the same
    // ScriniaMcpTools handler that the MCP server invokes. Output is the raw
    // YAML response (terminal-friendly when read by humans, parseable by tools);
    // --json wraps the response in CliMcpOutput so callers get a stable shape.

    private static int EmitMcpResult(string yaml, bool json, string action)
    {
        bool isError = yaml.Contains("status: error", StringComparison.Ordinal);
        string status = isError ? "error"
            : yaml.Contains("status: warning", StringComparison.Ordinal) ? "warning"
            : "success";

        if (json)
        {
            WriteJson(new CliMcpOutput(action, status, yaml), CliJsonContext.Default.CliMcpOutput);
        }
        else
        {
            // Pass YAML straight through — same pattern as Show.
            Console.Write(yaml);
            if (!yaml.EndsWith('\n')) Console.WriteLine();
        }
        return isError ? 1 : 0;
    }

    /// <summary>Print the embedded agent guide. Call once per session.</summary>
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

    /// <summary>Append a new chunk to an existing memory.</summary>
    /// <param name="name">Memory name to append to (e.g. 'session-notes', '/sessions/2026-05-11').</param>
    /// <param name="file">File path to read content from. Use '-' or omit for stdin.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    internal async Task<int> Append(
        [Argument] string name,
        [Argument] string? file = null,
        string? workspaceRoot = null,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);
        await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

        string content;
        if (string.IsNullOrEmpty(file) || file == "-")
        {
            if (!Console.IsInputRedirected)
            {
                if (json) { WriteJsonError("No file specified and stdin is not redirected."); return 1; }
                AnsiConsole.MarkupLine("[red]Error:[/] No file specified and stdin is not redirected.");
                AnsiConsole.MarkupLine("Usage: scri append <name> <file> or pipe content via stdin.");
                return 1;
            }
            content = await Console.In.ReadToEndAsync(cancellationToken);
        }
        else
        {
            if (!File.Exists(file))
            {
                if (json) { WriteJsonError($"File not found: {file}"); return 1; }
                AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(file)}");
                return 1;
            }
            content = await File.ReadAllTextAsync(file, cancellationToken);
        }

        var tools = new ScriniaMcpTools();
        string yaml = await tools.Append(content, name, cancellationToken);
        return EmitMcpResult(yaml, json, "append");
    }

    /// <summary>Compact a multi-chunk memory by merging chunks. Archives the original version.</summary>
    /// <param name="name">Memory name to compact.</param>
    /// <param name="keepRecent">-k, Keep only the N most recent chunks. 0 = merge all into one.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    internal async Task<int> Compact(
        [Argument] string name,
        int keepRecent = 0,
        string? workspaceRoot = null,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);
        await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

        var tools = new ScriniaMcpTools();
        string yaml = await tools.Compact(name, keepRecent, cancellationToken);
        return EmitMcpResult(yaml, json, "compact");
    }

    /// <summary>Create a bidirectional link between two memories.</summary>
    /// <param name="from">Source memory name.</param>
    /// <param name="to">Target memory name.</param>
    /// <param name="reason">-r, Optional reason for the connection.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    internal async Task<int> Link(
        [Argument] string from,
        [Argument] string to,
        string? reason = null,
        string? workspaceRoot = null,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);
        await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

        var tools = new ScriniaMcpTools();
        string yaml = await tools.Link(from, to, reason, cancellationToken);
        return EmitMcpResult(yaml, json, "link");
    }

    /// <summary>Resume agent context — profile, patterns, today's session log, available skills.</summary>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    /// <param name="hook">Emit the agent-CLI hook envelope (JSON with hookSpecificOutput.additionalContext wrapping the YAML payload in &lt;scrinia-restored-memory&gt; tags with an imperative framing line). Used by `scri setup --hooks` SessionStart wiring. Human invocations of `scri restore` should leave this off — the YAML default is more readable.</param>
    public async Task<int> Restore(
        string? workspaceRoot = null,
        bool json = false,
        bool hook = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);
        await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

        var tools = new ScriniaMcpTools();
        string yaml = await tools.Restore(cancellationToken);

        if (hook)
        {
            // Wrap the YAML payload in the same hook envelope shape `scri hint` uses, with
            // a header line that reads as an instruction to the model (not a status dump).
            // The agent CLI unwraps additionalContext into the model's context off-transcript.
            string payload =
                "<scrinia-restored-memory>\n" +
                "The following memories were saved during prior sessions in this workspace. " +
                "Reference them when relevant; call memory('search', '<name>') to retrieve " +
                "the full content of any entry below.\n\n" +
                yaml.TrimEnd() + "\n" +
                "</scrinia-restored-memory>";

            Console.WriteLine(HintCommand.BuildHookEnvelope("SessionStart", payload));
            return 0;
        }

        return EmitMcpResult(yaml, json, "restore");
    }

    /// <summary>Scan for merge conflicts in .scrinia/ or resolve a specific conflict.</summary>
    /// <param name="conflictId">Workspace-relative path under .scrinia/ to the conflicted file (e.g. "local/skills/qa.nmp2"). Omit to scan.</param>
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
        string yaml = await tools.Reconcile(conflictId, choice, mergedContent, cancellationToken);
        return EmitMcpResult(yaml, json, "reconcile");
    }

    /// <summary>Export topics to a .scrinia-bundle.</summary>
    /// <param name="topics">Comma-separated topic names to export (e.g. api,arch).</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="filename">-o, Output filename (saved to .scrinia/exports/).</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    internal async Task<int> Export(
        [Argument] string topics,
        string? workspaceRoot = null,
        string? filename = null,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        string[] topicArray = topics
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (topicArray.Length == 0)
        {
            if (json) { WriteJsonError("At least one topic name is required."); return 1; }
            AnsiConsole.MarkupLine("[red]Error:[/] At least one topic name is required.");
            return 1;
        }

        var tools = new ScriniaMcpTools();
        string result = await tools.Export(topicArray, filename, cancellationToken);

        if (result.StartsWith("Error:", StringComparison.Ordinal))
        {
            if (json) { WriteJsonError(result); return 1; }
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(result)}[/]");
            return 1;
        }

        if (json)
            WriteJson(new CliExportOutput("", result), CliJsonContext.Default.CliExportOutput);
        else
            AnsiConsole.MarkupLine($"[green]{Markup.Escape(result)}[/]");
        return 0;
    }

    /// <summary>Import from a .scrinia-bundle.</summary>
    /// <param name="path">Path to the .scrinia-bundle file.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="topics">Comma-separated topic names to import (imports all if omitted).</param>
    /// <param name="overwrite">Replace existing entries if they conflict.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    internal async Task<int> Import(
        [Argument] string path,
        string? workspaceRoot = null,
        string? topics = null,
        bool overwrite = false,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);
        await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

        string[]? topicArray = topics?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var tools = new ScriniaMcpTools();
        string result = await tools.Import(path, topicArray, overwrite, cancellationToken);

        if (result.StartsWith("Error:", StringComparison.Ordinal) ||
            result.StartsWith("No topics", StringComparison.Ordinal))
        {
            if (json) { WriteJsonError(result); return 1; }
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(result)}[/]");
            return 1;
        }

        if (json)
            WriteJson(new CliImportOutput(result), CliJsonContext.Default.CliImportOutput);
        else
            AnsiConsole.MarkupLine($"[green]{Markup.Escape(result)}[/]");
        return 0;
    }

    /// <summary>Bundle raw files into a .scrinia-bundle.</summary>
    /// <param name="topic">Topic name for the bundle.</param>
    /// <param name="files">Comma-separated file paths or glob pattern (e.g. docs/*.md).</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="output">-o, Output filename (default: {topic}-{timestamp}.scrinia-bundle).</param>
    /// <param name="description">-d, Description for all entries.</param>
    /// <param name="tags">-t, Comma-separated tags for all entries.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    internal Task<int> Bundle(
        [Argument] string topic,
        [Argument] string files,
        string? workspaceRoot = null,
        string? output = null,
        string? description = null,
        string? tags = null,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        string sanitizedTopic = ScriniaArtifactStore.SanitizeName(topic.Trim());
        if (string.IsNullOrWhiteSpace(sanitizedTopic))
        {
            if (json) { WriteJsonError("Topic name is required."); return Task.FromResult(1); }
            AnsiConsole.MarkupLine("[red]Error:[/] Topic name is required.");
            return Task.FromResult(1);
        }

        // Resolve file paths
        var filePaths = ResolveFiles(files);
        if (filePaths.Count == 0)
        {
            if (json) { WriteJsonError("No files matched the pattern."); return Task.FromResult(1); }
            AnsiConsole.MarkupLine("[red]Error:[/] No files matched the pattern.");
            return Task.FromResult(1);
        }

        string[]? tagArray = tags?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Determine output path
        string exportsDir = Path.Combine(ScriniaArtifactStore.GetStoreDirForScope("local"), "..", "exports");
        exportsDir = Path.GetFullPath(exportsDir);
        Directory.CreateDirectory(exportsDir);

        string bundleName = string.IsNullOrWhiteSpace(output)
            ? $"{sanitizedTopic}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}"
            : output;
        if (!bundleName.EndsWith(".scrinia-bundle", StringComparison.OrdinalIgnoreCase))
            bundleName += ".scrinia-bundle";

        string bundlePath = Path.Combine(exportsDir, bundleName);

        var entries = new List<ArtifactEntry>();
        var artifactContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string content = File.ReadAllText(filePath);
            string entryName = ScriniaArtifactStore.SanitizeName(Path.GetFileNameWithoutExtension(filePath));

            // Handle duplicate names by appending a suffix
            string uniqueName = entryName;
            int suffix = 2;
            while (artifactContents.ContainsKey(uniqueName))
            {
                uniqueName = $"{entryName}-{suffix}";
                suffix++;
            }
            entryName = uniqueName;

            string artifact = Nmp2ChunkedEncoder.Encode(content);
            int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);
            long originalBytes = System.Text.Encoding.UTF8.GetByteCount(content);
            string preview = ScriniaArtifactStore.GenerateContentPreview(content);

            string desc = !string.IsNullOrWhiteSpace(description)
                ? description
                : content[..Math.Min(200, content.Length)];

            entries.Add(new ArtifactEntry(
                Name: entryName,
                Uri: "",
                OriginalBytes: originalBytes,
                ChunkCount: chunkCount,
                CreatedAt: DateTimeOffset.UtcNow,
                Description: desc,
                Tags: tagArray,
                ContentPreview: preview));

            artifactContents[entryName] = artifact;
        }

        // Create the bundle zip
        using (var stream = new FileStream(bundlePath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            // Write topic index
            string indexJson = JsonSerializer.Serialize(
                new Scrinia.Core.Bundles.BundleIndex(entries),
                Scrinia.Core.Bundles.BundleJsonContext.Default.BundleIndex);
            var indexEntry = zip.CreateEntry($"topics/{sanitizedTopic}/index.json");
            using (var writer = new StreamWriter(indexEntry.Open()))
                writer.Write(indexJson);

            // Write artifact files
            foreach (var (name, artifactContent) in artifactContents)
            {
                string zipEntryName = $"topics/{sanitizedTopic}/{ScriniaArtifactStore.SanitizeName(name)}.nmp2";
                var zipEntry = zip.CreateEntry(zipEntryName);
                using var writer = new StreamWriter(zipEntry.Open());
                writer.Write(artifactContent);
            }

            // Write manifest
            var manifest = new Scrinia.Core.Bundles.BundleManifest(1, DateTimeOffset.UtcNow.ToString("o"), [sanitizedTopic], entries.Count);
            string manifestJson = JsonSerializer.Serialize(
                manifest,
                Scrinia.Core.Bundles.BundleJsonContext.Default.BundleManifest);
            var manifestEntry = zip.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifestEntry.Open()))
                writer.Write(manifestJson);
        }

        long fileSize = new FileInfo(bundlePath).Length;

        if (json)
        {
            string msg = $"Bundled {entries.Count} file(s) into topic '{sanitizedTopic}' ({ScriniaMcpTools.FormatBytes(fileSize)})";
            WriteJson(new CliBundleOutput(bundlePath, entries.Count, sanitizedTopic, fileSize, msg),
                CliJsonContext.Default.CliBundleOutput);
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[green]Bundled {entries.Count} file(s) into topic '{Markup.Escape(sanitizedTopic)}' " +
                $"({ScriniaMcpTools.FormatBytes(fileSize)}) at {Markup.Escape(bundlePath)}[/]");
        }
        return Task.FromResult(0);
    }

    /// <summary>Download embedding and (optional) LLM models for built-in providers and installed plugins.</summary>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="multiUser">Configure git merge drivers for multi-user collaboration.</param>
    /// <param name="resolver">Conflict resolver when --multi-user is set (none, claude, copilot).</param>
    /// <param name="llmDownload">Download the LLM plugin model without prompting (~900MB GGUF).</param>
    /// <param name="noLlmDownload">Skip the LLM plugin model download without prompting.</param>
    /// <param name="noOllama">Skip Ollama auto-detection even if it is running. Use to force local-only setup.</param>
    /// <param name="hooks">Install SessionStart/Stop hooks into detected agent CLIs (Claude Code, etc.). Opt-in.</param>
    /// <param name="uninstallHooks">Remove scrinia-managed hooks from agent CLIs and exit.</param>
    /// <param name="project">With --hooks / --uninstall-hooks, target workspace-local config (.claude/...) instead of user-global (~/.claude/...).</param>
    /// <param name="llm">Tier 2 LLM provider to configure non-interactively. Values: <c>auto</c>, <c>claude-cli</c>, <c>codex-cli</c>, <c>copilot-cli</c>, <c>openai</c>, <c>anthropic</c>, <c>gemini</c>, <c>plugin</c>, <c>none</c>. When omitted, setup writes <c>auto</c> (runtime picks the best available backend per startup). Secondary keys (API key for anthropic/gemini, base-URL + model for openai) are prompted only when missing from existing config.</param>
    public async Task<int> Setup(
        string? workspaceRoot = null,
        bool multiUser = false,
        string? resolver = null,
        bool llmDownload = false,
        bool noLlmDownload = false,
        bool noOllama = false,
        bool hooks = false,
        bool uninstallHooks = false,
        bool project = false,
        string? llm = null,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        // Hook-management flags short-circuit the model-download flow — they're standalone
        // operations users typically run after the regular setup is complete.
        if (uninstallHooks)
        {
            return await UninstallHooksFlowAsync(project);
        }
        if (hooks)
        {
            return await InstallHooksFlowAsync(project);
        }

        if (multiUser)
        {
            ConfigureMultiUser(resolver);
        }

        string exeDir = AppContext.BaseDirectory;

        // ── Step 0: Ollama auto-detect (offers to wire embeddings + completion through Ollama
        //           if it's already running, skipping the local-model downloads below). ──
        bool ollamaConfigured = !noOllama && await TryConfigureOllamaAsync(cancellationToken);
        if (ollamaConfigured)
        {
            // Reindex any existing vectors against the new Ollama provider signature.
            await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                "[green]Setup complete via Ollama.[/] Skipping local-model downloads. " +
                "Run [cyan]scri config[/] to inspect or adjust settings.");
            return 0;
        }

        // Ollama path was declined / unreachable / explicitly skipped — clear any stale
        // Ollama-derived keys from a prior run so the user doesn't end up with a config
        // pointing at a service they're no longer using. Without this, switching off
        // Ollama leaves Provider=ollama in place and startup tries it every time before
        // falling back, which surfaces as confusing "Ollama unreachable" warnings.
        ClearStaleOllamaConfig(ScriniaArtifactStore.WorkspaceRootPath);

        // ── Step 1: Built-in Model2Vec model (always) ──
        AnsiConsole.MarkupLine("[bold]Built-in embeddings (Model2Vec / MiniLM-L6-v2)[/]");

        string modelDir = Path.Combine(exeDir, "models", "m2v-MiniLM-L6-v2");
        Directory.CreateDirectory(modelDir);

        string[] files = ["model.safetensors", "vocab.txt"];
        string baseUrl = Model2VecModelManager.ModelBaseUrl;

        bool allExist = files.All(f => File.Exists(Path.Combine(modelDir, f)));
        if (allExist)
        {
            AnsiConsole.MarkupLine("[green]  Model already downloaded.[/]");
            AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(modelDir)}[/]");
        }
        else
        {
            await DownloadFilesAsync(baseUrl, files, modelDir, cancellationToken);
            AnsiConsole.MarkupLine($"[green]  Model ready at:[/] {Markup.Escape(modelDir)}");
        }

        // ── Step 2: Vulkan plugin GGUF model (if plugin is installed) ──
        string pluginsDir = Path.Combine(exeDir, "plugins");
        string pluginName = WorkspaceSetup.GetPluginName("plugins:embeddings", "scri-plugin-embeddings");
        string ext = OperatingSystem.IsWindows() ? ".exe" : "";

        // Check both subdirectory layout (multi-file publish) and flat layout (single-file)
        string pluginExe = Path.Combine(pluginsDir, pluginName, $"{pluginName}{ext}");
        if (!File.Exists(pluginExe))
            pluginExe = Path.Combine(pluginsDir, $"{pluginName}{ext}");

        if (File.Exists(pluginExe))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Vulkan plugin (GPU acceleration)[/]");

            string vulkanModelsDir = Path.Combine(pluginsDir, pluginName);
            Directory.CreateDirectory(vulkanModelsDir);

            const string ggufFile = "all-MiniLM-L6-v2-Q8_0.gguf";
            const string ggufUrl = "https://huggingface.co/second-state/All-MiniLM-L6-v2-Embedding-GGUF/resolve/main/all-MiniLM-L6-v2-Q8_0.gguf";

            if (File.Exists(Path.Combine(vulkanModelsDir, ggufFile)))
            {
                AnsiConsole.MarkupLine("[green]  GGUF model already downloaded.[/]");
                AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(vulkanModelsDir)}[/]");
            }
            else
            {
                await DownloadFilesAsync(ggufUrl.Replace($"/{ggufFile}", ""), [ggufFile], vulkanModelsDir, cancellationToken);
                AnsiConsole.MarkupLine($"[green]  GGUF model ready at:[/] {Markup.Escape(vulkanModelsDir)}");
            }
        }
        else
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Vulkan plugin not installed — skipping GPU model download.[/]");
        }

        // Explicitly record the embeddings provider that setup just configured. The
        // runtime defaults to model2vec when no value is set, but writing it explicitly
        // means re-running setup after declining a previously-configured Ollama doesn't
        // leave the user wondering which provider is active. Plugin presence still
        // takes precedence at runtime (LoadPluginsAsync handles that branch).
        WorkspaceConfig.SetValue(
            ScriniaArtifactStore.WorkspaceRootPath,
            "Scrinia:Embeddings:Provider",
            "model2vec");

        // ── Step 3: LLM plugin GGUF model (if plugin is installed) ──
        string llmPluginName = WorkspaceSetup.GetPluginName("plugins:llm", "scri-plugin-llm");
        string llmPluginExe = Path.Combine(pluginsDir, llmPluginName, $"{llmPluginName}{ext}");
        if (!File.Exists(llmPluginExe))
            llmPluginExe = Path.Combine(pluginsDir, $"{llmPluginName}{ext}");

        if (File.Exists(llmPluginExe))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]LLM plugin (Tier 2 consolidation)[/]");

            string llmModelsDir = Path.Combine(pluginsDir, llmPluginName);
            Directory.CreateDirectory(llmModelsDir);

            // Defaults intentionally inlined to avoid pulling Scrinia.Plugin.Llm (and transitively
            // LLamaSharp) into the trimmed CLI publish. Kept in sync with LlmModelManager.
            const string defaultLlmFile = "LFM2.5-1.2B-Instruct-Q5_K_M.gguf";
            const string defaultLlmUrl =
                "https://huggingface.co/LiquidAI/LFM2.5-1.2B-Instruct-GGUF/resolve/main/LFM2.5-1.2B-Instruct-Q5_K_M.gguf";

            string llmFile = WorkspaceSetup.GetConfigValue("Scrinia:Llm:LocalModelFile") ?? defaultLlmFile;
            string llmUrl = WorkspaceSetup.GetConfigValue("Scrinia:Llm:LocalModelUrl") ?? defaultLlmUrl;

            string llmFilePath = Path.Combine(llmModelsDir, llmFile);
            if (File.Exists(llmFilePath))
            {
                AnsiConsole.MarkupLine("[green]  LLM model already downloaded.[/]");
                AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(llmFilePath)}[/]");
            }
            else
            {
                bool proceed;
                if (llmDownload && noLlmDownload)
                {
                    AnsiConsole.MarkupLine(
                        "[yellow]  --llm-download and --no-llm-download both set; treating as decline.[/]");
                    proceed = false;
                }
                else if (llmDownload) proceed = true;
                else if (noLlmDownload) proceed = false;
                else
                {
                    // Default to No so a non-interactive `scri setup` (CI, scripted install)
                    // never silently downloads a gigabyte. Interactive users get a prompt.
                    proceed = AnsiConsole.Confirm(
                        $"  Download LLM model ([cyan]{Markup.Escape(llmFile)}[/], ~900MB) for Tier 2 consolidation?",
                        defaultValue: false);
                }

                if (proceed)
                {
                    string llmBaseUrl = llmUrl[..llmUrl.LastIndexOf('/')];
                    string filePart = llmUrl[(llmUrl.LastIndexOf('/') + 1)..];
                    await DownloadFilesAsync(llmBaseUrl, [filePart], llmModelsDir, cancellationToken);

                    // If the download filename differs from the configured filename
                    // (which is what the plugin will look for), rename atomically.
                    string downloaded = Path.Combine(llmModelsDir, filePart);
                    if (!string.Equals(filePart, llmFile, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(downloaded))
                    {
                        File.Move(downloaded, llmFilePath, overwrite: true);
                    }
                    AnsiConsole.MarkupLine($"[green]  LLM model ready at:[/] {Markup.Escape(llmModelsDir)}");
                }
                else
                {
                    AnsiConsole.MarkupLine(
                        "[dim]  Skipped. Run again with --llm-download to fetch, or set " +
                        "Scrinia:Llm:LocalModelUrl/LocalModelFile to override the default.[/]");
                }
            }
        }

        // ── Step 4: Tier 2 LLM backend selection ──
        // Pick the LLM provider Scrinia uses for Tier 2 consolidation. Auto (the default)
        // resolves at startup. Explicit values let the user pin a backend non-interactively
        // — e.g. `scri setup --llm claude-cli` for users with an active Claude Code
        // subscription who want zero API-key configuration.
        ConfigureLlmBackend(llm);

        return 0;
    }

    /// <summary>
    /// LLM backend selection step. Writes <c>Scrinia:Llm:Provider</c> and prompts only for
    /// secondary keys not already present in config. When <paramref name="llmArg"/> is null
    /// and no provider is currently set, defaults to <c>auto</c> (runtime resolves per
    /// startup). When a provider is already set and no arg is passed, leaves config alone
    /// so a re-run of <c>scri setup</c> doesn't silently downgrade a deliberate Anthropic /
    /// Gemini configuration to <c>auto</c>.
    /// </summary>
    private static void ConfigureLlmBackend(string? llmArg)
    {
        string root = ScriniaArtifactStore.WorkspaceRootPath;
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Tier 2 LLM backend[/]");

        string? existingProvider = WorkspaceConfig.GetValue(root, "Scrinia:Llm:Provider");

        // Re-run protection: if Ollama setup OR a prior explicit configuration already
        // wrote a provider and the user didn't override via --llm, respect that decision.
        if (llmArg is null && !string.IsNullOrEmpty(existingProvider))
        {
            AnsiConsole.MarkupLine(
                $"  [dim]Provider already set to '{Markup.Escape(existingProvider)}' — leaving as-is. " +
                "Pass --llm <value> to change it.[/]");
            return;
        }

        string chosen = (llmArg ?? "auto").Trim().ToLowerInvariant();
        if (!IsKnownLlmProvider(chosen))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]  Unknown --llm value '{Markup.Escape(chosen)}'. " +
                "Valid: auto, claude-cli, codex-cli, copilot-cli, openai, anthropic, gemini, plugin, none. " +
                "Falling back to auto.[/]");
            chosen = "auto";
        }

        WorkspaceConfig.SetValue(root, "Scrinia:Llm:Provider", chosen);

        switch (chosen)
        {
            case "auto":
                AnsiConsole.MarkupLine(
                    "  [green]Provider = auto[/] [dim]— runtime picks: HTTP probe → agent CLIs " +
                    "(claude → codex → copilot) on PATH → bundled plugin → none.[/]");
                break;
            case "claude-cli":
            case "codex-cli":
            case "copilot-cli":
                AnsiConsole.MarkupLine(
                    $"  [green]Provider = {Markup.Escape(chosen)}[/] [dim]— reuses your existing " +
                    "CLI subscription auth; no API key needed.[/]");
                break;
            case "anthropic":
                EnsureSecretAsync(root, "Scrinia:Llm:AnthropicApiKey", "Anthropic API key");
                EnsureValueAsync(root, "Scrinia:Llm:Model", "Model identifier", defaultValue: "claude-haiku-4-5");
                break;
            case "gemini":
                EnsureSecretAsync(root, "Scrinia:Llm:GeminiApiKey", "Gemini API key");
                EnsureValueAsync(root, "Scrinia:Llm:Model", "Model identifier", defaultValue: "gemini-2.0-flash");
                break;
            case "openai":
                EnsureValueAsync(root, "Scrinia:Llm:BaseUrl", "OpenAI-compatible base URL", defaultValue: "https://api.openai.com/v1");
                EnsureValueAsync(root, "Scrinia:Llm:Model", "Model identifier", defaultValue: "gpt-4o-mini");
                EnsureSecretAsync(root, "Scrinia:Llm:ApiKey", "API key (leave blank for local endpoints)", allowEmpty: true);
                break;
            case "plugin":
                AnsiConsole.MarkupLine(
                    "  [green]Provider = plugin[/] [dim]— uses the bundled scri-plugin-llm subprocess. " +
                    "Run --llm-download or pre-place the GGUF in plugins/scri-plugin-llm.[/]");
                break;
            case "none":
                AnsiConsole.MarkupLine(
                    "  [green]Provider = none[/] [dim]— Tier 2 consolidation disabled; " +
                    "`scri consolidate --with-llm` becomes a no-op.[/]");
                break;
        }
    }

    private static bool IsKnownLlmProvider(string value) => value switch
    {
        "auto" or "claude-cli" or "codex-cli" or "copilot-cli"
            or "openai" or "anthropic" or "gemini" or "plugin" or "none" => true,
        _ => false,
    };

    /// <summary>
    /// Prompts the user for a value if the named config key isn't already set. Secrets
    /// are masked. Empty input is rejected unless <paramref name="allowEmpty"/> is true.
    /// </summary>
    private static void EnsureSecretAsync(string root, string key, string label, bool allowEmpty = false)
    {
        string? existing = WorkspaceConfig.GetValue(root, key);
        if (!string.IsNullOrEmpty(existing))
        {
            AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(key)} already set — keeping existing value.[/]");
            return;
        }

        var prompt = new Spectre.Console.TextPrompt<string>($"  [cyan]{Markup.Escape(label)}:[/]")
            .Secret();
        if (allowEmpty) prompt.AllowEmpty();
        string entered = AnsiConsole.Prompt(prompt);
        if (!string.IsNullOrEmpty(entered))
            WorkspaceConfig.SetValue(root, key, entered);
    }

    private static void EnsureValueAsync(string root, string key, string label, string defaultValue)
    {
        string? existing = WorkspaceConfig.GetValue(root, key);
        if (!string.IsNullOrEmpty(existing))
        {
            AnsiConsole.MarkupLine(
                $"  [dim]{Markup.Escape(key)} already set to '{Markup.Escape(existing)}' — keeping.[/]");
            return;
        }

        string entered = AnsiConsole.Prompt(
            new Spectre.Console.TextPrompt<string>($"  [cyan]{Markup.Escape(label)}[/] [dim](default: {Markup.Escape(defaultValue)})[/]:")
                .DefaultValue(defaultValue));
        WorkspaceConfig.SetValue(root, key, entered);
    }

    /// <summary>
    /// Walks the user through installing scrinia SessionStart/Stop hooks for each detected
    /// agent CLI. Prompts per CLI so the user can opt in selectively; user-authored hooks
    /// in the target config files are preserved (we own only blocks marked with our
    /// sentinel). Invoked by <c>scri setup --hooks</c>.
    /// </summary>
    private static async Task<int> InstallHooksFlowAsync(bool project)
    {
        var scope = project ? HookScope.Project : HookScope.User;
        string? workspaceRoot = scope == HookScope.Project ? ScriniaArtifactStore.WorkspaceRootPath : null;

        AnsiConsole.MarkupLine($"[bold]Agent CLI hooks ({scope})[/]");
        AnsiConsole.MarkupLine(
            "  [dim]SessionStart → `scri restore`, SessionEnd → `scri consolidate --auto`, " +
            "UserPromptSubmit → `scri hint`.[/]");

        int configured = await AgentHookSetup.InstallAsync(scope, workspaceRoot);
        if (configured == 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]No hooks installed.[/] Run with at least one supported CLI (claude) on PATH.");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]Installed hooks for {configured} agent CLI(s).[/]");
        return 0;
    }

    /// <summary>Removes scrinia-managed hooks from every detected CLI at the chosen scope.</summary>
    private static async Task<int> UninstallHooksFlowAsync(bool project)
    {
        var scope = project ? HookScope.Project : HookScope.User;
        string? workspaceRoot = scope == HookScope.Project ? ScriniaArtifactStore.WorkspaceRootPath : null;

        AnsiConsole.MarkupLine($"[bold]Removing scrinia hooks ({scope})[/]");
        int removed = await AgentHookSetup.UninstallAsync(scope, workspaceRoot);
        if (removed == 0)
        {
            AnsiConsole.MarkupLine("[dim]No supported agent CLIs detected on PATH.[/]");
        }
        return 0;
    }

    /// <summary>
    /// Probes for a running Ollama at the default URL and, if detected, walks the user through
    /// picking embedding + completion models and pulling them if missing. On success writes
    /// <c>Scrinia:Embeddings:*</c> + <c>Scrinia:Llm:*</c> config and returns true so the caller
    /// can skip the local-model download steps.
    ///
    /// Returns false (continue with local setup) when Ollama isn't running, the user declines,
    /// or any required pull fails. All prompts default to safe values so an accidental Enter
    /// doesn't trigger a multi-GB download.
    /// </summary>
    private static async Task<bool> TryConfigureOllamaAsync(CancellationToken ct)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Ollama auto-detect[/]");

        // Single probe-and-list call. Setup is interactive so we can afford a generous timeout
        // — better to wait 5s and detect than to silently miss because IPv6/firewall added a
        // round-trip. The probe also surfaces the actual error string when it fails so the
        // user can diagnose ("ConnectionRefused" vs "TimeoutException" vs "HTTP 503") instead
        // of just seeing "no Ollama detected".
        var probe = await OllamaSetup.ProbeAsync(OllamaSetup.DefaultBaseUrl, timeoutSeconds: 5, ct);
        if (!probe.Reachable)
        {
            AnsiConsole.MarkupLine(
                $"[dim]  No Ollama detected at {OllamaSetup.DefaultBaseUrl}: {Markup.Escape(probe.Error ?? "unknown")}. " +
                "Continuing with local setup.[/]");
            return false;
        }

        AnsiConsole.MarkupLine(
            $"[green]  Ollama detected at {OllamaSetup.DefaultBaseUrl}.[/] " +
            $"[dim]({probe.Models.Count} model(s) installed)[/]");
        if (!AnsiConsole.Confirm("  Use Ollama for Scrinia embeddings + completion?", defaultValue: true))
            return false;

        var installed = probe.Models;
        var pulledNames = new HashSet<string>(installed.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        var pulledEmbedding = installed.Where(m => OllamaSetup.LooksLikeEmbeddingModel(m.Name)).ToList();
        var pulledChat = installed.Where(m => !OllamaSetup.LooksLikeEmbeddingModel(m.Name)).ToList();

        string root = ScriniaArtifactStore.WorkspaceRootPath;

        // -- Embedding model selection --
        string? embeddingModel = await PromptForOllamaModelAsync(
            label: "embedding model",
            defaultModel: OllamaSetup.DefaultEmbeddingModel,
            installedRelevant: pulledEmbedding,
            installedAll: pulledNames,
            ct: ct);
        if (embeddingModel is null) return false;

        // -- Completion model selection --
        string? completionModel = await PromptForOllamaModelAsync(
            label: "completion model",
            defaultModel: OllamaSetup.DefaultCompletionModel,
            installedRelevant: pulledChat,
            installedAll: pulledNames,
            fallbackOnPullFailure: OllamaSetup.FallbackCompletionModel,
            ct: ct);
        if (completionModel is null) return false;

        // -- Write config (Embeddings uses raw host URL, Llm uses /v1 OpenAI-compat suffix) --
        WorkspaceConfig.SetValue(root, "Scrinia:Embeddings:Provider", "ollama");
        WorkspaceConfig.SetValue(root, "Scrinia:Embeddings:OllamaBaseUrl", OllamaSetup.DefaultBaseUrl);
        WorkspaceConfig.SetValue(root, "Scrinia:Embeddings:OllamaModel", embeddingModel);
        WorkspaceConfig.SetValue(root, "Scrinia:Llm:Provider", "openai");
        WorkspaceConfig.SetValue(root, "Scrinia:Llm:BaseUrl", $"{OllamaSetup.DefaultBaseUrl}/v1");
        WorkspaceConfig.SetValue(root, "Scrinia:Llm:Model", completionModel);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]  Config written:[/]");
        AnsiConsole.MarkupLine($"    [dim]Scrinia:Embeddings:Provider = ollama[/]");
        AnsiConsole.MarkupLine($"    [dim]Scrinia:Embeddings:OllamaModel = {Markup.Escape(embeddingModel)}[/]");
        AnsiConsole.MarkupLine($"    [dim]Scrinia:Llm:Provider = openai[/]");
        AnsiConsole.MarkupLine($"    [dim]Scrinia:Llm:Model = {Markup.Escape(completionModel)}[/]");
        return true;
    }

    /// <summary>
    /// Interactive picker for an Ollama model. Shows pulled candidates as a selection list with
    /// "Pull {default}" and "Type a name" options. When the chosen name isn't already pulled,
    /// confirms and runs <see cref="OllamaSetup.PullModelAsync"/>. Returns the final model name
    /// when ready, or <c>null</c> when the user aborts or all pull attempts fail.
    /// </summary>
    private static async Task<string?> PromptForOllamaModelAsync(
        string label,
        string defaultModel,
        List<OllamaSetup.OllamaModelInfo> installedRelevant,
        HashSet<string> installedAll,
        CancellationToken ct,
        string? fallbackOnPullFailure = null)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Select {label}:[/]");

        const string pullDefaultOption = "__pull_default__";
        const string typeNameOption = "__type__";

        var prompt = new SelectionPrompt<string>()
            .Title($"  Choose an Ollama {label}")
            .PageSize(10);

        foreach (var m in installedRelevant)
            prompt.AddChoice(m.Name);
        if (!installedAll.Contains(defaultModel))
            prompt.AddChoice(pullDefaultOption);
        prompt.AddChoice(typeNameOption);

        prompt.UseConverter(s => s switch
        {
            pullDefaultOption => $"Pull {defaultModel}",
            typeNameOption => "Type a name…",
            _ => $"{s} (already pulled)",
        });

        string choice = AnsiConsole.Prompt(prompt);
        string targetModel;
        if (choice == pullDefaultOption)
        {
            targetModel = defaultModel;
        }
        else if (choice == typeNameOption)
        {
            targetModel = AnsiConsole.Prompt(
                new TextPrompt<string>($"  Enter Ollama {label} tag:")
                    .DefaultValue(defaultModel)
                    .ValidationErrorMessage("[red]Model name cannot be empty.[/]")
                    .Validate(s => !string.IsNullOrWhiteSpace(s)));
        }
        else
        {
            targetModel = choice;
        }

        // Pull if not already on disk. Default-No so a typo isn't a multi-GB download.
        if (!installedAll.Contains(targetModel))
        {
            bool wantsPull = AnsiConsole.Confirm(
                $"  [cyan]{Markup.Escape(targetModel)}[/] is not yet pulled. Pull it now?",
                defaultValue: false);
            if (!wantsPull) return null;

            bool ok = await OllamaSetup.PullModelAsync(OllamaSetup.DefaultBaseUrl, targetModel, ct);
            if (!ok)
            {
                if (fallbackOnPullFailure is not null
                    && !string.Equals(fallbackOnPullFailure, targetModel, StringComparison.OrdinalIgnoreCase))
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]  Pull failed. Trying fallback [cyan]{Markup.Escape(fallbackOnPullFailure)}[/]…[/]");
                    if (await OllamaSetup.PullModelAsync(OllamaSetup.DefaultBaseUrl, fallbackOnPullFailure, ct))
                    {
                        return fallbackOnPullFailure;
                    }
                }
                AnsiConsole.MarkupLine(
                    $"[red]  Pull failed. Run [cyan]ollama pull {Markup.Escape(targetModel)}[/] manually, then re-run setup.[/]");
                return null;
            }
        }

        return targetModel;
    }

    /// <summary>
    /// Remove Ollama-derived config keys from a prior <c>scri setup</c> run. Called on the
    /// non-Ollama branch of setup so a user switching off Ollama doesn't carry stale
    /// <c>Scrinia:Embeddings:Provider=ollama</c> + URL + model into the next startup. Only
    /// touches keys that were demonstrably Ollama-installed — anything pointing elsewhere
    /// (custom OpenAI base URL, user-set Anthropic key) is preserved.
    /// </summary>
    internal static void ClearStaleOllamaConfig(string root)
    {
        bool wasOllamaEmbeddings = string.Equals(
            WorkspaceConfig.GetValue(root, "Scrinia:Embeddings:Provider"),
            "ollama",
            StringComparison.OrdinalIgnoreCase);

        if (wasOllamaEmbeddings)
        {
            WorkspaceConfig.UnsetValue(root, "Scrinia:Embeddings:Provider");
            WorkspaceConfig.UnsetValue(root, "Scrinia:Embeddings:OllamaBaseUrl");
            WorkspaceConfig.UnsetValue(root, "Scrinia:Embeddings:OllamaModel");
        }

        // LLM block written by the Ollama path points at localhost:11434/v1 with
        // Provider=openai. If we see that exact shape, treat it as Ollama-installed and
        // clear it. Custom user OpenAI configs (api.openai.com or a self-hosted LM Studio
        // URL) are preserved — they aren't ours to remove.
        string? llmProvider = WorkspaceConfig.GetValue(root, "Scrinia:Llm:Provider");
        string? llmBaseUrl = WorkspaceConfig.GetValue(root, "Scrinia:Llm:BaseUrl");
        bool llmWasOllamaInstalled =
            string.Equals(llmProvider, "openai", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(llmBaseUrl)
            && llmBaseUrl.Contains("localhost:11434", StringComparison.OrdinalIgnoreCase);

        if (llmWasOllamaInstalled)
        {
            WorkspaceConfig.UnsetValue(root, "Scrinia:Llm:Provider");
            WorkspaceConfig.UnsetValue(root, "Scrinia:Llm:BaseUrl");
            WorkspaceConfig.UnsetValue(root, "Scrinia:Llm:Model");
        }

        if (wasOllamaEmbeddings || llmWasOllamaInstalled)
        {
            AnsiConsole.MarkupLine(
                "[dim]  Cleared stale Ollama-derived config keys from a prior setup run.[/]");
        }
    }

    private static void ConfigureMultiUser(string? resolver)
    {
        string root = ScriniaArtifactStore.WorkspaceRootPath;
        string scriniaDir = Path.Combine(root, ".scrinia");

        // 1. Configure git merge drivers
        RunGit(root, "config", "merge.scrinia-meta.driver",
            $".scrinia/hooks/scri-merge meta %O %A %B");
        RunGit(root, "config", "merge.scrinia-nmp2.driver",
            $".scrinia/hooks/scri-merge nmp2 %O %A %B");
        AnsiConsole.MarkupLine("[green]  Git merge drivers configured.[/]");

        // 2. Create/update .scrinia/.gitattributes
        string gitattributesPath = Path.Combine(scriniaDir, ".gitattributes");
        Directory.CreateDirectory(scriniaDir);
        File.WriteAllText(gitattributesPath,
            "*.meta.json merge=scrinia-meta\n*.nmp2 merge=scrinia-nmp2\n");
        AnsiConsole.MarkupLine($"[green]  Created:[/] {Markup.Escape(gitattributesPath)}");

        // 3. Create .scrinia/merge.config
        string resolverValue = resolver ?? "none";
        string mergeConfigPath = Path.Combine(scriniaDir, "merge.config");
        string mergeConfigJson = JsonSerializer.Serialize(
            new MergeConfig(JaccardThreshold: 0.7, Resolver: resolverValue, ConflictDir: "conflict"),
            CliJsonContext.Default.MergeConfig);
        File.WriteAllText(mergeConfigPath, mergeConfigJson + "\n");
        AnsiConsole.MarkupLine($"[green]  Created:[/] {Markup.Escape(mergeConfigPath)}");

        // 4. Add .scrinia/hooks/scri-merge* to .gitignore if not already there
        string gitignorePath = Path.Combine(root, ".gitignore");
        const string hookEntry = ".scrinia/hooks/scri-merge*";
        bool needsEntry = true;
        if (File.Exists(gitignorePath))
        {
            string content = File.ReadAllText(gitignorePath);
            if (content.Contains(hookEntry, StringComparison.Ordinal))
                needsEntry = false;
        }
        if (needsEntry)
        {
            using var writer = File.AppendText(gitignorePath);
            writer.WriteLine();
            writer.WriteLine("# Scrinia merge driver binary (platform-specific)");
            writer.WriteLine(hookEntry);
            AnsiConsole.MarkupLine($"[green]  Updated:[/] {Markup.Escape(gitignorePath)}");
        }

        // 5. Print instructions
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            "[bold]Multi-user merge driver configured.[/] " +
            "Copy scri-merge binary to [blue].scrinia/hooks/[/] for your platform.");
    }

    private static void RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");
        process.WaitForExit(10_000);
        if (process.ExitCode != 0)
        {
            string error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {error}");
        }
    }

    /// <summary>
    /// First-run helper used by <see cref="Serve"/>. If the built-in embedding model is missing,
    /// quietly downloads it before the MCP host opens its stdio channel. All output goes to stderr
    /// to keep the JSON-RPC stdout pristine. Any failure is logged but not fatal — the server still
    /// starts; semantic search degrades to BM25-only via NullEmbeddingProvider.
    /// </summary>
    private static async Task EnsureEmbeddingModelAsync(CancellationToken ct)
    {
        string exeDir = AppContext.BaseDirectory;
        string modelDir = Path.Combine(exeDir, "models", "m2v-MiniLM-L6-v2");
        string safetensors = Path.Combine(modelDir, "model.safetensors");
        string vocab = Path.Combine(modelDir, "vocab.txt");

        if (File.Exists(safetensors) && File.Exists(vocab))
            return;

        try { Directory.CreateDirectory(modelDir); }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[scrinia] auto-setup: cannot create {modelDir}: {ex.Message} — semantic search disabled");
            return;
        }

        await Console.Error.WriteLineAsync("[scrinia] first-run: downloading embedding model (~50MB)…");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        string baseUrl = Model2VecModelManager.ModelBaseUrl;
        foreach (string file in new[] { "model.safetensors", "vocab.txt" })
        {
            string target = Path.Combine(modelDir, file);
            if (File.Exists(target)) continue;

            string tmp = target + ".tmp";
            try
            {
                using var resp = await http.GetAsync($"{baseUrl}/{file}", HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using (var fs = File.Create(tmp))
                    await resp.Content.CopyToAsync(fs, ct);
                File.Move(tmp, target, overwrite: true);
                await Console.Error.WriteLineAsync($"[scrinia] downloaded {file}");
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                await Console.Error.WriteLineAsync($"[scrinia] auto-setup failed ({file}): {ex.Message} — semantic search disabled, run `scri setup` to retry");
                return;
            }
        }
        await Console.Error.WriteLineAsync("[scrinia] embedding model ready");
    }

    private static async Task DownloadFilesAsync(string baseUrl, string[] files, string targetDir, CancellationToken ct)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        foreach (string file in files)
        {
            string filePath = Path.Combine(targetDir, file);
            if (File.Exists(filePath))
            {
                AnsiConsole.MarkupLine($"  [dim]{file} already exists, skipping.[/]");
                continue;
            }

            string url = $"{baseUrl}/{file}";
            AnsiConsole.MarkupLine($"  Downloading [blue]{file}[/]...");

            string tmpPath = filePath + ".tmp";
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);

            await AnsiConsole.Progress()
                .AutoClear(true)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new TransferSpeedColumn(),
                    new RemainingTimeColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask(file, maxValue: totalBytes ?? 0);
                    if (totalBytes is null) task.IsIndeterminate = true;

                    await using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    byte[] buffer = new byte[81920];
                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                        task.Increment(bytesRead);
                    }
                });

            File.Move(tmpPath, filePath, overwrite: true);

            long size = new FileInfo(filePath).Length;
            string sizeStr = size switch
            {
                < 1024 => $"{size} B",
                < 1024 * 1024 => $"{size / 1024.0:F1} KB",
                _ => $"{size / (1024.0 * 1024):F1} MB",
            };
            AnsiConsole.MarkupLine($"  [green]Downloaded {file} ({sizeStr})[/]");
        }
    }

    /// <summary>Get or set workspace configuration.</summary>
    /// <param name="key">Config key (e.g. plugins:embeddings). Omit to list all.</param>
    /// <param name="value">Value to set. Omit to read current value.</param>
    /// <param name="unset">Remove the setting.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    public async Task<int> Config(
        [Argument] string? key = null,
        [Argument] string? value = null,
        bool unset = false,
        string? workspaceRoot = null,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);
        string root = ScriniaArtifactStore.WorkspaceRootPath;

        if (key is null)
        {
            // List all settings
            var config = WorkspaceConfig.Load(root);
            if (json)
            {
                WriteJson(new CliConfigOutput(new Dictionary<string, string>(config, StringComparer.OrdinalIgnoreCase), null, null),
                    CliJsonContext.Default.CliConfigOutput);
                return 0;
            }

            if (config.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No configuration set.[/]");
                return 0;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Key")
                .AddColumn("Value");

            foreach (var (k, v) in config.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                table.AddRow(Markup.Escape(k), Markup.Escape(v));

            AnsiConsole.Write(table);
            return 0;
        }

        if (unset)
        {
            bool wasSet = WorkspaceConfig.UnsetValue(root, key);
            if (json)
                WriteJson(new CliConfigOutput(null, key, null), CliJsonContext.Default.CliConfigOutput);
            else if (wasSet)
                AnsiConsole.MarkupLine($"[green]Unset '{Markup.Escape(key)}'.[/]");
            else
                AnsiConsole.MarkupLine($"[dim]'{Markup.Escape(key)}' was not set.[/]");
            return 0;
        }

        if (value is null)
        {
            // Get a single value
            string? current = WorkspaceConfig.GetValue(root, key);
            if (json)
            {
                WriteJson(new CliConfigOutput(null, key, current), CliJsonContext.Default.CliConfigOutput);
                return 0;
            }
            if (current is not null)
                AnsiConsole.WriteLine(current);
            else
                AnsiConsole.MarkupLine("[dim]not set[/]");
            return 0;
        }

        // Set a value
        WorkspaceConfig.SetValue(root, key, value);
        if (json)
            WriteJson(new CliConfigOutput(null, key, value), CliJsonContext.Default.CliConfigOutput);
        else
            AnsiConsole.MarkupLine($"[green]Set '{Markup.Escape(key)}' = '{Markup.Escape(value)}'.[/]");

        // Embedding settings affect vector identity. After a Scrinia:Embeddings:* write we
        // load the active provider against the new config. For a Provider switch we
        // unconditionally rebuild — the plugin path doesn't participate in the in-process
        // signature-mismatch quarantine that drives MaybeReindexAfterModelSwitch, so a
        // gating-on-signature approach would silently skip ollama→vulkan / vulkan→model2vec
        // transitions. For other Embeddings:* keys we still rely on the signature gate.
        if (key.StartsWith("Scrinia:Embeddings:", StringComparison.OrdinalIgnoreCase))
        {
            bool providerSwitch = key.Equals("Scrinia:Embeddings:Provider", StringComparison.OrdinalIgnoreCase);
            await TryReindexAfterConfigChangeAsync(providerSwitch, cancellationToken);
        }

        return 0;
    }

    /// <summary>
    /// Reload the active embedding pipeline against the just-written config and trigger a
    /// reindex. If <paramref name="forceReindex"/> is true (provider key changed), runs an
    /// unconditional rebuild via <see cref="WorkspaceSetup.ForceReindexAsync"/> so the new
    /// provider — including plugin-owned ones — gets a clean vector set. Otherwise relies on
    /// the in-process signature-mismatch path inside <see cref="WorkspaceSetup.LoadPluginsAsync"/>.
    /// Errors are surfaced as warnings so a reindex failure doesn't fail the config command itself.
    /// </summary>
    private static async Task TryReindexAfterConfigChangeAsync(bool forceReindex, CancellationToken ct)
    {
        try
        {
            // Configure must have already run by the caller — workspace root and
            // MemoryStoreContext.Current are populated.
            await WorkspaceSetup.LoadPluginsAsync(ct);

            if (forceReindex)
                await WorkspaceSetup.ForceReindexAsync(ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[scrinia:warn] Reindex after config change failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Rebuild vector embeddings for every persistent memory in the workspace.
    /// Use after switching embedding model or recovering from a corrupted vector file.</summary>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="json">Emit result as JSON instead of human-readable progress.</param>
    public async Task<int> Reindex(
        string? workspaceRoot = null,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        // Wire the active embedding provider before forcing the rebuild. LoadPluginsAsync also
        // runs the signature-mismatch auto-reindex (a no-op when signatures match), then we
        // override with an unconditional pass so the command does what its name says even on
        // a "vectors are stale, I just want them rebuilt" recovery flow.
        await WorkspaceSetup.LoadPluginsAsync(cancellationToken);
        var result = await WorkspaceSetup.ForceReindexAsync(cancellationToken);

        if (result is null)
        {
            if (json)
                WriteJsonError("Reindex skipped — no embedding provider is available. Run `scri setup` first.");
            else
                AnsiConsole.MarkupLine(
                    "[yellow]Reindex skipped[/] — no embedding provider is available. " +
                    "Run [italic]scri setup[/] first.");
            return 1;
        }

        string summary =
            $"Embedded {result.Embedded}/{result.Total}, " +
            $"{result.Skipped} skipped, {result.Failed} failed.";
        if (json)
            WriteJson(new CliReindexOutput(result.Embedded, summary), CliJsonContext.Default.CliReindexOutput);
        else
            AnsiConsole.MarkupLine($"[green]Reindex complete.[/] {Markup.Escape(summary)}");
        return result.Failed == 0 ? 0 : 1;
    }

    /// <summary>Pre-send relevance hint. Looks up the prompt against BM25 and emits a hook-output
    /// envelope telling the agent which stored memories look relevant. Wired into agent CLIs
    /// via the UserPromptSubmit hook. Default output is JSON
    /// (<c>{"hookSpecificOutput":{"hookEventName":"UserPromptSubmit","additionalContext":"..."}}</c>)
    /// understood by Claude Code, Codex, and Copilot — the additionalContext channel injects
    /// the hint without polluting the user's transcript. Returns empty stdout when the prompt
    /// is short, no matches clear the score floor, or hints are disabled in config.</summary>
    /// <param name="prompt">Prompt text. If omitted, read from stdin.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="plain">Emit the human-readable single-line hint instead of the hook JSON envelope. Use for direct CLI invocation; hooks use the default JSON shape.</param>
    /// <param name="json">Emit raw structured HintResult JSON (count + matches array). For programmatic consumers.</param>
    public async Task<int> Hint(
        string? prompt = null,
        string? workspaceRoot = null,
        bool plain = false,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        // Disabled → silent zero. Single config flag so a user who finds the hint noisy
        // can shut it off without touching every agent CLI's hook config.
        string? enabled = WorkspaceSetup.GetConfigValue("Scrinia:Hint:Enabled");
        if (enabled is not null && enabled.Equals("false", StringComparison.OrdinalIgnoreCase))
            return 0;

        // Resolve thresholds from config with sane defaults.
        double minScore = HintCommand.DefaultMinScore;
        string? cfgScore = WorkspaceSetup.GetConfigValue("Scrinia:Hint:MinScore");
        if (cfgScore is not null && double.TryParse(cfgScore,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out double parsedScore))
            minScore = parsedScore;

        int minPromptChars = HintCommand.DefaultMinPromptChars;
        string? cfgChars = WorkspaceSetup.GetConfigValue("Scrinia:Hint:MinPromptChars");
        if (cfgChars is not null && int.TryParse(cfgChars, out int parsedChars) && parsedChars >= 0)
            minPromptChars = parsedChars;

        // Stdin fallback when no positional arg given (the common hook-invocation shape).
        // CLIs deliver hook input as either plain prompt text or a JSON envelope like
        // {"prompt": "...", "session_id": "...", ...}. Auto-detect: try JSON first; on
        // failure or absent prompt key, use the raw stdin as the prompt verbatim.
        string actualPrompt = prompt ?? ExtractPromptFromStdin(await ReadAllStdinAsync(cancellationToken));

        var store = MemoryStoreContext.Current
            ?? throw new InvalidOperationException("Workspace store not configured. Call Configure first.");

        var hint = new HintCommand(store);
        var result = hint.Compute(actualPrompt, minScore, minPromptChars);

        if (!result.Emitted)
            return 0;

        if (json)
        {
            // Raw structured HintResult shape for programmatic consumers — distinct from
            // the hook envelope (which wraps a model-facing instruction string).
            var matches = string.Join(",", result.Matches.Select(m =>
                $"{{\"scope\":\"{m.Scope}\",\"name\":\"{JsonEscape(m.Name)}\",\"score\":{m.Score.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"));
            Console.WriteLine($"{{\"count\":{result.Matches.Count},\"matches\":[{matches}]}}");
        }
        else if (plain)
        {
            Console.WriteLine(HintCommand.FormatPlain(result));
        }
        else
        {
            // Default: hook envelope. The agent CLIs unwrap additionalContext and inject
            // it into the model's context discreetly (off-transcript on Claude Code).
            Console.WriteLine(HintCommand.FormatHook(result));
        }
        return 0;
    }

    private static async Task<string> ReadAllStdinAsync(CancellationToken ct)
    {
        if (Console.IsInputRedirected)
        {
            using var reader = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }
        return string.Empty;
    }

    /// <summary>
    /// Heuristically extract a prompt string from hook stdin. Each CLI delivers
    /// UserPromptSubmit input differently — Claude Code wraps it in JSON
    /// (<c>{prompt, session_id, ...}</c>); some setups pipe plain text. Try JSON first
    /// looking for the canonical <c>prompt</c> key; on any failure fall back to the raw
    /// stdin string.
    /// </summary>
    internal static string ExtractPromptFromStdin(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        string trimmed = raw.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return raw;
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(trimmed);
            if (doc.RootElement.TryGetProperty("prompt", out var promptEl)
                && promptEl.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return promptEl.GetString() ?? raw;
            }
        }
        catch (System.Text.Json.JsonException) { /* fall through */ }
        return raw;
    }

    private static string JsonEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

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

    /// <summary>Migrate .scrinia/ data from v1 (topic:name) to v2 (path) structure. One-shot — hidden from top-level help.</summary>
    /// <param name="workspace">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="dryRun">Print what would be copied without actually doing it.</param>
    /// <param name="backup">Create a timestamped backup of .scrinia/ before migrating.</param>
    /// <param name="cleanup">Remove v1 originals after verifying migration.</param>
    [Hidden]
    public Task<int> Migrate(
        string? workspace = null,
        bool dryRun = false,
        bool backup = true,
        bool cleanup = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspace);
        string root = ScriniaArtifactStore.WorkspaceRootPath;
        string scriniaDir = Path.Combine(root, ".scrinia");

        if (!Directory.Exists(scriniaDir))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No .scrinia/ directory found.");
            return Task.FromResult(1);
        }

        // ── Cleanup mode: remove v1 originals ───────────────────────────
        if (cleanup)
        {
            return RunCleanup(scriniaDir, dryRun);
        }

        // ── Backup ──────────────────────────────────────────────────────
        if (backup && !dryRun)
        {
            string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            string backupDir = Path.Combine(root, $".scrinia-backup-{timestamp}");
            AnsiConsole.MarkupLine($"[dim]Backing up .scrinia/ to {Markup.Escape(Path.GetFileName(backupDir))}...[/]");
            CopyDirectoryRecursive(scriniaDir, backupDir);
            AnsiConsole.MarkupLine("[green]Backup complete.[/]");
        }

        // ── Gather migration plan ───────────────────────────────────────
        var plan = BuildMigrationPlan(scriniaDir);

        if (plan.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to migrate — no v1 files found.[/]");
            return Task.FromResult(0);
        }

        // ── Execute or report ───────────────────────────────────────────
        int migrated = 0;
        int skipped = 0;
        int errors = 0;

        if (dryRun)
            AnsiConsole.MarkupLine($"[bold][DRY RUN][/] Would migrate {plan.Count} files:");

        foreach (var (source, target) in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relSource = Path.GetRelativePath(scriniaDir, source).Replace('\\', '/');
            string relTarget = Path.GetRelativePath(scriniaDir, target).Replace('\\', '/');

            if (File.Exists(target))
            {
                if (!dryRun)
                    skipped++;
                else
                    AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(relSource)} → {Markup.Escape(relTarget)} (skip, exists)[/]");
                continue;
            }

            if (dryRun)
            {
                AnsiConsole.MarkupLine($"  {Markup.Escape(relSource)} → {Markup.Escape(relTarget)}");
                continue;
            }

            try
            {
                string? targetDir = Path.GetDirectoryName(target);
                if (targetDir is not null)
                    Directory.CreateDirectory(targetDir);
                File.Copy(source, target);
                migrated++;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error copying {Markup.Escape(relSource)}: {Markup.Escape(ex.Message)}[/]");
                errors++;
            }
        }

        if (!dryRun)
        {
            AnsiConsole.MarkupLine($"[green]Migrated {migrated} files to v2 path structure.[/]");
            if (skipped > 0)
                AnsiConsole.MarkupLine($"[dim]Skipped {skipped} files (already exist at target).[/]");
            if (errors > 0)
                AnsiConsole.MarkupLine($"[red]{errors} errors during migration.[/]");
            AnsiConsole.MarkupLine("[dim]Original files preserved in topics/ for fallback.[/]");
            AnsiConsole.MarkupLine("[dim]Run 'scri migrate --cleanup' to remove originals after verifying.[/]");
        }

        return Task.FromResult(errors > 0 ? 1 : 0);
    }

    private static List<(string Source, string Target)> BuildMigrationPlan(string scriniaDir)
    {
        var plan = new List<(string Source, string Target)>();
        string memoriesDir = Path.Combine(scriniaDir, "memories");

        // 1. Scan .scrinia/topics/ for .nmp2 and .meta.json files
        string topicsDir = Path.Combine(scriniaDir, "topics");
        if (Directory.Exists(topicsDir))
        {
            foreach (string file in Directory.EnumerateFiles(topicsDir, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file);
                if (!ext.Equals(".nmp2", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip version files — they live in topic/versions/ subdirs
                string relPath = Path.GetRelativePath(topicsDir, file).Replace('\\', '/');
                if (relPath.Contains("/versions/", StringComparison.OrdinalIgnoreCase))
                    continue;

                string target = MapTopicFileToV2(relPath, memoriesDir);
                plan.Add((file, target));
            }
        }

        // 2. Scan .scrinia/agent/ for markdown files → memories/agent/
        string agentDir = Path.Combine(scriniaDir, "agent");
        if (Directory.Exists(agentDir))
        {
            foreach (string file in Directory.EnumerateFiles(agentDir, "*", SearchOption.AllDirectories))
            {
                string relPath = Path.GetRelativePath(agentDir, file).Replace('\\', '/');
                string target = Path.Combine(memoriesDir, "agent", relPath.Replace('/', Path.DirectorySeparatorChar));
                plan.Add((file, target));
            }
        }

        // 3. Scan .scrinia/skills/ → memories/skill/
        string skillsDir = Path.Combine(scriniaDir, "skills");
        if (Directory.Exists(skillsDir))
        {
            foreach (string file in Directory.EnumerateFiles(skillsDir, "*", SearchOption.AllDirectories))
            {
                string relPath = Path.GetRelativePath(skillsDir, file).Replace('\\', '/');
                string target = Path.Combine(memoriesDir, "skill", relPath.Replace('/', Path.DirectorySeparatorChar));
                plan.Add((file, target));
            }
        }

        // 4. Scan .scrinia/workflows/ → memories/workflow/
        string workflowsDir = Path.Combine(scriniaDir, "workflows");
        if (Directory.Exists(workflowsDir))
        {
            foreach (string file in Directory.EnumerateFiles(workflowsDir, "*", SearchOption.AllDirectories))
            {
                string relPath = Path.GetRelativePath(workflowsDir, file).Replace('\\', '/');
                string target = Path.Combine(memoriesDir, "workflow", relPath.Replace('/', Path.DirectorySeparatorChar));
                plan.Add((file, target));
            }
        }

        return plan;
    }

    /// <summary>
    /// Maps a relative path under topics/ to its v2 target under memories/.
    /// Handles the three v1 layouts:
    ///   entity/{topic}/{file} → {topic}/{file}     (strip entity/ prefix)
    ///   memory/{topic}/{file} → {topic}/{file}     (strip memory/ prefix)
    ///   agent/{file}          → agent/{file}        (keep as-is)
    ///   {topic}/{file}        → {topic}/{file}      (flat topic, keep as-is)
    /// </summary>
    private static string MapTopicFileToV2(string relPath, string memoriesDir)
    {
        // relPath uses forward slashes, e.g. "entity/goal/G-5.nmp2" or "arch/overview.nmp2"
        string[] parts = relPath.Split('/');

        string mappedRelPath;
        if (parts.Length >= 3 &&
            parts[0].Equals("entity", StringComparison.OrdinalIgnoreCase))
        {
            // entity/goal/G-5.nmp2 → goal/G-5.nmp2
            mappedRelPath = string.Join(Path.DirectorySeparatorChar.ToString(), parts[1..]);
        }
        else if (parts.Length >= 3 &&
                 parts[0].Equals("memory", StringComparison.OrdinalIgnoreCase))
        {
            // memory/api/auth-flow.nmp2 → api/auth-flow.nmp2
            mappedRelPath = string.Join(Path.DirectorySeparatorChar.ToString(), parts[1..]);
        }
        else
        {
            // agent/profile.nmp2 → agent/profile.nmp2
            // arch/overview.nmp2 → arch/overview.nmp2
            mappedRelPath = relPath.Replace('/', Path.DirectorySeparatorChar);
        }

        return Path.Combine(memoriesDir, mappedRelPath);
    }

    private static Task<int> RunCleanup(string scriniaDir, bool dryRun)
    {
        string topicsDir = Path.Combine(scriniaDir, "topics");
        string memoriesDir = Path.Combine(scriniaDir, "memories");

        if (!Directory.Exists(memoriesDir))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No memories/ directory found. Run 'scri migrate' first.");
            return Task.FromResult(1);
        }

        if (!Directory.Exists(topicsDir))
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to clean up — topics/ directory does not exist.[/]");
            return Task.FromResult(0);
        }

        // Only remove files from topics/ that have a corresponding file in memories/
        var plan = BuildMigrationPlan(scriniaDir);
        int removed = 0;
        int kept = 0;

        foreach (var (source, target) in plan)
        {
            if (!File.Exists(target))
            {
                kept++;
                continue;
            }

            string relSource = Path.GetRelativePath(scriniaDir, source).Replace('\\', '/');

            if (dryRun)
            {
                AnsiConsole.MarkupLine($"  [red]Would remove[/] {Markup.Escape(relSource)}");
            }
            else
            {
                File.Delete(source);
                removed++;
            }
        }

        if (dryRun)
        {
            AnsiConsole.MarkupLine($"[bold][DRY RUN][/] Would remove {plan.Count - kept} v1 files.");
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]Removed {removed} v1 files.[/]");
            if (kept > 0)
                AnsiConsole.MarkupLine($"[dim]Kept {kept} files (no v2 counterpart found).[/]");

            // Clean up empty directories in topics/
            CleanEmptyDirectories(topicsDir);
        }

        return Task.FromResult(0);
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: false);
        }

        foreach (string dir in Directory.GetDirectories(sourceDir))
        {
            string destDir = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyDirectoryRecursive(dir, destDir);
        }
    }

    private static void CleanEmptyDirectories(string dir)
    {
        foreach (string subDir in Directory.GetDirectories(dir))
        {
            CleanEmptyDirectories(subDir);
        }

        if (!Directory.EnumerateFileSystemEntries(dir).Any())
        {
            try { Directory.Delete(dir); } catch { /* ignore */ }
        }
    }

    /// <summary>Run deterministic consolidation passes against the local store. Tier 1: no LLM call, mechanical only.</summary>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="auto">When set, skip if .scrinia/.last-consolidation indicates a recent run (debounce). Intended for hook-driven invocation.</param>
    /// <param name="dryRun">Report what would change without modifying anything.</param>
    /// <param name="debounceMinutes">Minimum minutes between auto runs. Hooks fire on every Stop event; this prevents wasted work.</param>
    /// <param name="sessionAgeDays">Compact multi-chunk session entries older than N days (preserves content, drops chunk granularity).</param>
    /// <param name="json">Output as JSON instead of a styled table.</param>
    /// <param name="withLlm">After Tier 1 mechanical compaction, run an LLM pass: backfill auto-fallback descriptions, summarize compacted sessions, and extract atomic facts. Requires a configured background LLM (OpenAI-compatible endpoint or bundled plugin). Exits 2 with a hint if no backend is available.</param>
    public async Task<int> Consolidate(
        string? workspaceRoot = null,
        bool auto = false,
        bool dryRun = false,
        int debounceMinutes = 30,
        int sessionAgeDays = 7,
        bool json = false,
        bool withLlm = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);
        if (withLlm) await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

        string scriniaDir = Path.Combine(ScriniaArtifactStore.WorkspaceRootPath, ".scrinia");
        string debounceFile = Path.Combine(scriniaDir, ".last-consolidation");

        // Hook-friendly debounce: hooks fire on every Stop event, but consolidation only earns
        // its keep if there's been new activity. The debounce file lets us cheaply no-op when
        // the last run is recent. Corrupt or missing file → proceed (fail-open).
        if (auto && File.Exists(debounceFile))
        {
            try
            {
                string lastRunStr = File.ReadAllText(debounceFile).Trim();
                if (DateTimeOffset.TryParse(lastRunStr, out var lastRun))
                {
                    double minutesSince = (DateTimeOffset.UtcNow - lastRun).TotalMinutes;
                    if (minutesSince < debounceMinutes)
                    {
                        string reason = $"Last run {minutesSince:F1}m ago (debounce {debounceMinutes}m)";
                        if (json)
                            WriteJson(
                                new CliConsolidateOutput("skipped", reason, 0, 0, 0, 0, [], null),
                                CliJsonContext.Default.CliConsolidateOutput);
                        else
                            AnsiConsole.MarkupLine($"[dim]consolidate: skipped — {reason}[/]");
                        return 0;
                    }
                }
            }
            catch { /* corrupt debounce file → proceed */ }
        }

        var allEntries = ScriniaArtifactStore.ListScoped(null);
        long totalBytes = allEntries.Sum(e => e.Entry.OriginalBytes);
        int staleCount = allEntries.Count(e => e.Entry.ReviewAfter.HasValue && e.Entry.ReviewAfter.Value <= DateTimeOffset.UtcNow);

        // Compact old multi-chunk session entries. Sessions are append-only logs that accumulate
        // chunks across a day's work; once a session is past its useful window, chunk-level
        // search granularity stops earning its sidecar overhead. Single-chunk artifacts still
        // search fine via entry-level keywords and TF.
        var ageThreshold = DateTimeOffset.UtcNow.AddDays(-sessionAgeDays);
        var compactionCandidates = allEntries
            .Where(e => IsSessionScope(e.Scope))
            .Where(e => e.Entry.ChunkCount > 1)
            .Where(e => (e.Entry.UpdatedAt ?? e.Entry.CreatedAt) <= ageThreshold)
            .ToList();

        var compacted = new List<string>();
        if (!dryRun)
        {
            foreach (var item in compactionCandidates)
            {
                try
                {
                    CompactEntryToSingleChunk(item);
                    compacted.Add(ScriniaArtifactStore.FormatQualifiedName(item.Scope, item.Entry.Name));
                }
                catch (Exception ex)
                {
                    if (!json)
                        AnsiConsole.MarkupLine($"[red]compact failed for {Markup.Escape(item.Entry.Name)}: {Markup.Escape(ex.Message)}[/]");
                }
            }

            Directory.CreateDirectory(scriniaDir);
            File.WriteAllText(debounceFile, DateTimeOffset.UtcNow.ToString("o"));
        }

        // ── Tier 2: LLM pass ─────────────────────────────────────────────────
        // After mechanical compaction, optionally do the language-model work:
        // backfill descriptions, summarize the sessions Tier 1 just touched, and
        // extract atomic facts onto each entry. A progress file under .scrinia/
        // makes the pass idempotent and resumable.
        LlmConsolidator.Result? llmResult = null;
        if (withLlm)
        {
            var llm = BackgroundLlmContext.Default;
            if (llm is null)
            {
                if (json)
                    WriteJsonError("--with-llm requested but no background LLM is configured. " +
                        "Start Ollama (or another OpenAI-compatible server) at " +
                        "http://localhost:11434/v1, or run `scri config Scrinia:Llm:BaseUrl <url>`.");
                else
                    AnsiConsole.MarkupLine("[red]--with-llm requested but no background LLM is configured.[/]\n" +
                        "Start Ollama (or another OpenAI-compatible server) at [italic]http://localhost:11434/v1[/], " +
                        "or run [italic]scri config Scrinia:Llm:BaseUrl <url>[/].");
                return 2;
            }

            // Re-fetch entries after Tier 1: compaction changed UpdatedAt and chunk counts.
            var freshEntries = ScriniaArtifactStore.ListScoped(null);
            var justCompacted = new HashSet<string>(compacted, StringComparer.OrdinalIgnoreCase);

            llmResult = await LlmConsolidator.RunAsync(
                llm,
                freshEntries,
                justCompacted,
                scriniaDir,
                dryRun,
                onWarning: msg =>
                {
                    if (!json) AnsiConsole.MarkupLine($"[yellow]llm: {Markup.Escape(msg)}[/]");
                },
                cancellationToken);
        }

        if (json)
        {
            WriteJson(
                new CliConsolidateOutput(
                    Status: dryRun ? "preview" : "completed",
                    Reason: null,
                    TotalMemories: allEntries.Count,
                    TotalBytes: totalBytes,
                    StaleCount: staleCount,
                    CompactionCandidates: compactionCandidates.Count,
                    Compacted: compacted.ToArray(),
                    Llm: llmResult is null ? null : new CliConsolidateLlmStats(
                        llmResult.Processed, llmResult.DescriptionsBackfilled,
                        llmResult.SessionsSummarized, llmResult.FactsExtracted,
                        llmResult.Skipped, llmResult.Failed)),
                CliJsonContext.Default.CliConsolidateOutput);
            return 0;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[bold]consolidate{(dryRun ? " (dry-run)" : "")}[/]");
        sb.AppendLine($"[blue]{allEntries.Count} memories[/] — {ScriniaMcpTools.FormatBytes(totalBytes)}");
        if (staleCount > 0)
            sb.AppendLine($"[yellow]{staleCount} entries past reviewAfter[/] — review with [italic]scrinia list[/]");
        if (compactionCandidates.Count > 0)
        {
            string verb = dryRun ? "would compact" : "compacted";
            sb.AppendLine($"[green]{verb} {compactionCandidates.Count} multi-chunk session{(compactionCandidates.Count == 1 ? "" : "s")} older than {sessionAgeDays}d[/]");
            foreach (var name in (dryRun ? compactionCandidates.Select(c => ScriniaArtifactStore.FormatQualifiedName(c.Scope, c.Entry.Name)) : compacted).Take(10))
                sb.AppendLine($"  [dim]•[/] {Markup.Escape(name)}");
            int total = dryRun ? compactionCandidates.Count : compacted.Count;
            if (total > 10)
                sb.AppendLine($"  [dim]… and {total - 10} more[/]");
        }
        else if (llmResult is null)
        {
            sb.AppendLine("[dim]Nothing to consolidate.[/]");
        }
        if (llmResult is not null)
        {
            sb.AppendLine($"[green]LLM pass[/]: processed {llmResult.Processed}, skipped {llmResult.Skipped}, failed {llmResult.Failed}");
            if (llmResult.DescriptionsBackfilled > 0)
                sb.AppendLine($"  [dim]•[/] {llmResult.DescriptionsBackfilled} description{(llmResult.DescriptionsBackfilled == 1 ? "" : "s")} backfilled");
            if (llmResult.SessionsSummarized > 0)
                sb.AppendLine($"  [dim]•[/] {llmResult.SessionsSummarized} session{(llmResult.SessionsSummarized == 1 ? "" : "s")} summarized");
            if (llmResult.FactsExtracted > 0)
                sb.AppendLine($"  [dim]•[/] {llmResult.FactsExtracted} entr{(llmResult.FactsExtracted == 1 ? "y" : "ies")} fact-extracted");
        }
        AnsiConsole.Write(new Markup(sb.ToString()));
        return 0;
    }

    /// <summary>True when the scope is the session-log scope under any namespacing variant.</summary>
    private static bool IsSessionScope(string scope)
    {
        if (!scope.StartsWith("local-topic:", StringComparison.Ordinal)) return false;
        string topicPart = scope["local-topic:".Length..];
        return topicPart.Equals("sessions", StringComparison.OrdinalIgnoreCase)
            || topicPart.Equals("memory/sessions", StringComparison.OrdinalIgnoreCase)
            || topicPart.EndsWith("/sessions", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decodes a multi-chunk artifact and re-encodes it as a single chunk. Archives the original
    /// so the prior chunked form remains recoverable. Updates the sidecar to reflect the new
    /// chunk count and clears per-chunk metadata (which is no longer accurate after merge).
    /// </summary>
    private static void CompactEntryToSingleChunk(ScopedArtifact item)
    {
        string path = ScriniaArtifactStore.FindArtifactPath(item.Entry.Name, item.Scope);
        if (!File.Exists(path)) return;

        string artifact = File.ReadAllText(path);
        if (Nmp2ChunkedEncoder.GetChunkCount(artifact) <= 1) return;

        ScriniaArtifactStore.ArchiveVersion(item.Entry.Name, item.Scope);

        byte[] allBytes = Nmp2Strategy.Instance.Decode(artifact);
        string fullText = System.Text.Encoding.UTF8.GetString(allBytes);
        string compacted = Nmp2ChunkedEncoder.Encode(fullText);
        File.WriteAllText(path, compacted);

        var updated = item.Entry with
        {
            ChunkCount = 1,
            OriginalBytes = allBytes.LongLength,
            UpdatedAt = DateTimeOffset.UtcNow,
            ChunkEntries = null,
        };
        ScriniaArtifactStore.Upsert(updated, item.Scope);
    }
}
