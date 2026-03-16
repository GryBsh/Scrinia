using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
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

    /// <summary>Start the MCP server (stdio transport for Claude Desktop / Claude Code).</summary>
    /// <param name="workspaceRoot">Workspace root for local memory store. Defaults to current working directory.</param>
    /// <param name="remote">Scrinia.Server URL for remote mode (e.g. http://localhost:5000).</param>
    /// <param name="apiKey">API key for remote server authentication.</param>
    /// <param name="store">Target store name on the remote server (default: "default").</param>
    /// <param name="stdio">Use stdio transport (default, required for Claude Desktop / Claude Code).</param>
    public async Task<int> Serve(
        string? workspaceRoot = null,
        string? remote = null,
        string? apiKey = null,
        string? store = null,
        bool stdio = true,
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
        }

        // Load CLI plugins (embeddings, etc.) — sets SearchContributorContext + MemoryEventSinkContext
        await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

        var builder = Host.CreateApplicationBuilder();

        // MCP servers communicate via stdio; keep the log channel quiet so protocol
        // messages on stdout/stderr are not corrupted by host framework log output.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Register the MCP server with stdio transport and our tool class.
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
    public Task<int> List(string? workspaceRoot = null, string? scopes = null,
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
    public async Task<int> Search([Argument] string query, string? workspaceRoot = null, string? scopes = null, int limit = 20, bool json = false, CancellationToken cancellationToken = default)
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
                    ScriniaArtifactStore.FormatScopeLabel(tr.Scope),
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
                string label = ScriniaArtifactStore.FormatScopeLabel(tr.Scope);
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
    public async Task<int> Store(
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
    public async Task<int> Show(
        [Argument] string name,
        string? workspaceRoot = null,
        string? output = null,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        var tools = new ScriniaMcpTools();
        string result = await tools.Show(name, cancellationToken);

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
    public async Task<int> Forget(
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

    /// <summary>Export topics to a .scrinia-bundle.</summary>
    /// <param name="topics">Comma-separated topic names to export (e.g. api,arch).</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="filename">-o, Output filename (saved to .scrinia/exports/).</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    public async Task<int> Export(
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
    public async Task<int> Import(
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
    public Task<int> Bundle(
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
            string indexJson = JsonSerializer.Serialize(new BundleIndex(entries), ScriniaMcpTools.BundleJsonOptions);
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
            var manifest = new BundleManifest(1, DateTimeOffset.UtcNow.ToString("o"), [sanitizedTopic], entries.Count);
            string manifestJson = JsonSerializer.Serialize(manifest, ScriniaMcpTools.BundleJsonOptions);
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

    /// <summary>Download embedding models for built-in and optional Vulkan plugin.</summary>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    public async Task<int> Setup(
        string? workspaceRoot = null,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        string exeDir = AppContext.BaseDirectory;

        // ── Step 1: Built-in Model2Vec model (always) ──
        AnsiConsole.MarkupLine("[bold]Built-in embeddings (Model2Vec / MiniLM-L6-v2)[/]");

        string modelDir = Path.Combine(exeDir, "models", "m2v-MiniLM-L6-v2");
        Directory.CreateDirectory(modelDir);

        string[] files = ["model.safetensors", "vocab.txt"];
        const string baseUrl = "https://huggingface.co/grybsh/m2v-MiniLM-L6-v2/resolve/main";

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

        return 0;
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
    public int Config(
        [Argument] string? key = null,
        [Argument] string? value = null,
        bool unset = false,
        string? workspaceRoot = null,
        bool json = false)
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
        return 0;
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
