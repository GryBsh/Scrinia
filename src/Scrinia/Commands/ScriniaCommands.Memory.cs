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
            var now = DateTimeOffset.UtcNow;
            var items = entries.Select(item => BuildCliMemoryEntry(item, now)).ToArray();
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
            var st = AttributeReaders.GetReviewStatus(e.Attributes, DateTimeOffset.UtcNow);
            string reviewPrefix = st.IsStale ? "[stale] " : st.NeedsReview ? "[review?] " : "";

            string desc = AttributeReaders.PreviewText(e.ContentPreview).Replace('\n', ' ').Replace('\r', ' ');
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

    /// <summary>Builds the JSON <c>list</c> entry for one artifact, sourcing review status and
    /// description/preview from the attributes carriers (<c>Attributes</c>/<c>ContentPreview</c>)
    /// via <see cref="AttributeReaders"/>, and surfacing the raw <c>Attributes</c> dict
    /// verbatim.</summary>
    internal static CliMemoryEntry BuildCliMemoryEntry(ScopedArtifact item, DateTimeOffset now)
    {
        var e = item.Entry;
        string name = item.Scope == "ephemeral"
            ? $"~{e.Name}"
            : ScriniaArtifactStore.FormatQualifiedName(item.Scope, e.Name);
        var st = AttributeReaders.GetReviewStatus(e.Attributes, now);
        return new CliMemoryEntry(
            name, e.ChunkCount, e.OriginalBytes, (int)(e.OriginalBytes / 4),
            e.CreatedAt.ToString("o"), e.UpdatedAt?.ToString("o"),
            AttributeReaders.PreviewText(e.ContentPreview),
            e.Attributes,
            st.IsStale, st.NeedsReview);
    }

    internal static CliListSummaryOutput BuildCliSummary(List<ScopedArtifact> entries, bool forJson)
    {
        long totalBytes = entries.Sum(e => e.Entry.OriginalBytes);
        int totalTokens = (int)(totalBytes / 4);
        var now = DateTimeOffset.UtcNow;
        int staleCount = entries.Count(e => AttributeReaders.GetReviewStatus(e.Entry.Attributes, now).IsStale);
        int reviewCount = entries.Count(e => AttributeReaders.GetReviewStatus(e.Entry.Attributes, now).NeedsReview);
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
            string label = g.Key == "local" ? "local" : $"/{g.Key}";
            return new CliScopeEntry(label, g.Count(), g.Sum(e => e.Entry.OriginalBytes));
        }).ToList();
        if (ephemeralCount > 0)
            scopeEntries.Add(new CliScopeEntry("ephemeral", ephemeralCount, entries.Where(e => e.Scope == "ephemeral").Sum(e => e.Entry.OriginalBytes)));

        // Top keywords + top attributes come from the shared aggregator that also backs the MCP
        // `list` summary, so the CLI and tool surfaces report identically (key:value keywords excluded
        // from the keyword tally, attributes counted per key).
        var (topKeywords, topAttributesRaw) = ScriniaMcpTools.AggregateSummaryFacets(entries);
        var topAttributes = topAttributesRaw
            .Select(a => new CliAttributeEntry(a.Key, a.Count))
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
            if (topAttributes.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("[bold]Top attributes[/]");
                sb.AppendLine($"  {Markup.Escape(string.Join(", ", topAttributes.Select(f => $"{f.Key} ({f.Count})")))}");
            }
            rendered = sb.ToString();
        }

        return new CliListSummaryOutput(
            entries.Count, totalBytes, totalTokens, topicCount, ephemeralCount,
            staleCount, reviewCount,
            scopeEntries.ToArray(),
            topKeywords.Length > 0 ? topKeywords : null,
            topAttributes.Length > 0 ? topAttributes : null,
            rendered);
    }

    /// <summary>Search memories.</summary>
    /// <param name="query">Search term to match against memory names and descriptions.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="scopes">Comma-separated scopes to search (e.g. local,api,ephemeral).</param>
    /// <param name="limit">Maximum results to return.</param>
    /// <param name="context">Context terms appended to the query for scoring only — bridges vocabulary mismatches.</param>
    /// <param name="excludeTopics">Comma-separated topic names to exclude from results.</param>
    /// <param name="pathPrefix">Path prefix to scope results, e.g. '/findings/'.</param>
    /// <param name="json">Output as JSON instead of a table.</param>
    public async Task<int> Search([Argument] string query, string? workspaceRoot = null, string? scopes = null,
        int limit = 20, string? context = null, string? excludeTopics = null, string? pathPrefix = null,
        bool json = false, CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);
        await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

        // Route through the same scoring pipeline as the MCP `search` tool so the CLI ranks with
        // the embeddings + entity-graph contributors it just loaded (not BM25-only) and surfaces
        // the same per-hit attributes/dates/anchors the tool does. MemoryStoreContext.Current is
        // set by WorkspaceSetup.Configure above.
        var store = MemoryStoreContext.Current!;
        var matches = await ScriniaMcpTools.ExecuteSearchAsync(
            store, query, scopes, limit, excludeTopics, context, pathPrefix,
            attributeFilter: null, cancellationToken);
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
            var results = matches.Select(BuildCliSearchResult).ToArray();
            WriteJson(new CliSearchOutput(results, results.Length, query), CliJsonContext.Default.CliSearchOutput);
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Type")
            .AddColumn("Name")
            .AddColumn(new TableColumn("Score").RightAligned())
            .AddColumn(new TableColumn("~Tokens").RightAligned())
            .AddColumn("Attributes")
            .AddColumn("Description");

        foreach (var match in matches)
        {
            var r = BuildCliSearchResult(match);
            string name = match is ChunkEntryResult cr
                ? $"{r.Name} [chunk {cr.Chunk.ChunkIndex}/{cr.TotalChunks}]"
                : r.Name;
            string desc = r.Description.Replace('\n', ' ').Replace('\r', ' ');
            if (desc.Length > 60) desc = desc[..57] + "...";
            string attrs = FormatAttributesInline(r.Attributes);
            string tokens = r.Type == "topic" ? "" : r.EstimatedTokens.ToString();
            table.AddRow(r.Type, Markup.Escape(name), $"{r.Score:F0}", tokens, Markup.Escape(attrs), Markup.Escape(desc));
        }

        AnsiConsole.Write(table);
        return 0;
    }

    /// <summary>Projects a <see cref="SearchResult"/> into the CLI's JSON/table shape, surfacing the
    /// same per-hit attributes/dates/anchors the MCP <c>search</c> tool renders (finding: the CLI
    /// used to drop them). Chunk hits carry their parent entry's attributes/dates; topic hits carry
    /// none.</summary>
    internal static CliSearchResult BuildCliSearchResult(SearchResult match) => match switch
    {
        ChunkEntryResult cr => new CliSearchResult("chunk",
            cr.ParentItem.Scope == "ephemeral" ? $"~{cr.ParentItem.Entry.Name}" : ScriniaArtifactStore.FormatQualifiedName(cr.ParentItem.Scope, cr.ParentItem.Entry.Name),
            cr.Score, (int)(cr.ParentItem.Entry.OriginalBytes / cr.TotalChunks / 4),
            cr.Chunk.ContentPreview ?? AttributeReaders.PreviewText(cr.ParentItem.Entry.ContentPreview),
            cr.Chunk.ChunkIndex, cr.TotalChunks,
            cr.ParentItem.Entry.Attributes,
            cr.ParentItem.Entry.CreatedAt.ToString("o"), cr.ParentItem.Entry.UpdatedAt?.ToString("o"),
            DisplayAnchorsOrNull(cr.ParentItem.Entry.Attributes)),
        EntryResult er => new CliSearchResult("entry",
            er.Item.Scope == "ephemeral" ? $"~{er.Item.Entry.Name}" : ScriniaArtifactStore.FormatQualifiedName(er.Item.Scope, er.Item.Entry.Name),
            er.Score, (int)(er.Item.Entry.OriginalBytes / 4),
            AttributeReaders.PreviewText(er.Item.Entry.ContentPreview), null, null,
            er.Item.Entry.Attributes,
            er.Item.Entry.CreatedAt.ToString("o"), er.Item.Entry.UpdatedAt?.ToString("o"),
            DisplayAnchorsOrNull(er.Item.Entry.Attributes)),
        TopicResult tr => new CliSearchResult("topic",
            MemoryNaming.FormatScopeLabel(tr.Scope),
            tr.Score, 0, tr.Description, null, null, null, null, null, null),
        _ => new CliSearchResult("unknown", "", 0, 0, "", null, null, null, null, null, null),
    };

    private static string[]? DisplayAnchorsOrNull(Dictionary<string, string[]>? attributes)
    {
        var anchors = AttributeReaders.DisplayAnchors(attributes);
        return anchors.Count > 0 ? anchors.ToArray() : null;
    }

    /// <summary>Compact one-line rendering of an attribute map for the search table's Attributes
    /// column (e.g. "topic: auth; org: acme"). Empty when the hit carries no attributes.</summary>
    private static string FormatAttributesInline(Dictionary<string, string[]>? attributes)
    {
        if (attributes is not { Count: > 0 }) return "";
        return string.Join("; ", attributes.Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value)}"));
    }

    /// <summary>Store a file as a named memory.</summary>
    /// <param name="name">Memory name (e.g. 'session-notes', '/api/auth-flow', '~scratch').</param>
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
        string result = await tools.Remember([content], name, description ?? "", tagArray,
            keywordArray, reviewAfter: reviewAfter, reviewWhen: reviewWhen, cancellationToken: cancellationToken);

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
    /// <param name="name">Memory name to display (e.g. 'session-notes', '/api/auth-flow', '~scratch').</param>
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
        string result = await tools.Recall(name, cancellationToken: cancellationToken);

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
    /// <param name="name">Memory name to delete (e.g. 'session-notes', '/api/auth-flow', '~scratch').</param>
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

    /// <summary>Append a new chunk to an existing memory.</summary>
    /// <param name="name">Memory name to append to (e.g. 'session-notes', '/sessions/2026-05-11').</param>
    /// <param name="file">File path to read content from. Use '-' or omit for stdin.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    public async Task<int> Append(
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
        string yaml = await tools.Append(content, name, cancellationToken: cancellationToken);
        return EmitMcpResult(yaml, json, "append");
    }

    /// <summary>Compact a multi-chunk memory by merging chunks. Archives the original version.</summary>
    /// <param name="name">Memory name to compact.</param>
    /// <param name="keepRecent">-k, Keep only the N most recent chunks. 0 = merge all into one.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    public async Task<int> Compact(
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
    public async Task<int> Link(
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
}
