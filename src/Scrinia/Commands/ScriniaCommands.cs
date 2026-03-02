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
    public Task<int> List(string? workspaceRoot = null, string? scopes = null)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        var entries = ScriniaArtifactStore.ListScoped(scopes);
        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No memories stored.[/]");
            return Task.FromResult(0);
        }

        entries.Sort((a, b) => b.Entry.CreatedAt.CompareTo(a.Entry.CreatedAt));

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

    /// <summary>Search memories.</summary>
    /// <param name="query">Search term to match against memory names and descriptions.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="scopes">Comma-separated scopes to search (e.g. local,api,ephemeral).</param>
    /// <param name="limit">Maximum results to return.</param>
    public Task<int> Search([Argument] string query, string? workspaceRoot = null, string? scopes = null, int limit = 20)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        var matches = ScriniaArtifactStore.SearchAll(query, scopes, limit);
        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No matching memories found.[/]");
            return Task.FromResult(0);
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
        return Task.FromResult(0);
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
    public async Task<int> Store(
        [Argument] string name,
        [Argument] string? file = null,
        string? workspaceRoot = null,
        string? description = null,
        string? tags = null,
        string? keywords = null,
        string? reviewAfter = null,
        string? reviewWhen = null,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        string content;
        if (string.IsNullOrEmpty(file) || file == "-")
        {
            if (!Console.IsInputRedirected)
            {
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
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(result)}[/]");
        return 0;
    }

    /// <summary>Display memory content.</summary>
    /// <param name="name">Memory name to display (e.g. 'session-notes', 'api:auth-flow', '~scratch').</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="output">-o, Write output to a file instead of stdout.</param>
    public async Task<int> Show(
        [Argument] string name,
        string? workspaceRoot = null,
        string? output = null,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        var tools = new ScriniaMcpTools();
        string result = await tools.Show(name, cancellationToken);

        if (result.StartsWith("Error:", StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(result)}[/]");
            return 1;
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
    public async Task<int> Forget(
        [Argument] string name,
        string? workspaceRoot = null,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        var tools = new ScriniaMcpTools();
        string result = await tools.Forget(name, cancellationToken);

        if (result.StartsWith("Error:", StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(result)}[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]{Markup.Escape(result)}[/]");
        return 0;
    }

    /// <summary>Export topics to a .scrinia-bundle.</summary>
    /// <param name="topics">Comma-separated topic names to export (e.g. api,arch).</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="filename">-o, Output filename (saved to .scrinia/exports/).</param>
    public async Task<int> Export(
        [Argument] string topics,
        string? workspaceRoot = null,
        string? filename = null,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        string[] topicArray = topics
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (topicArray.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] At least one topic name is required.");
            return 1;
        }

        var tools = new ScriniaMcpTools();
        string result = await tools.Export(topicArray, filename, cancellationToken);

        if (result.StartsWith("Error:", StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(result)}[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]{Markup.Escape(result)}[/]");
        return 0;
    }

    /// <summary>Import from a .scrinia-bundle.</summary>
    /// <param name="path">Path to the .scrinia-bundle file.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="topics">Comma-separated topic names to import (imports all if omitted).</param>
    /// <param name="overwrite">Replace existing entries if they conflict.</param>
    public async Task<int> Import(
        [Argument] string path,
        string? workspaceRoot = null,
        string? topics = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        string[]? topicArray = topics?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var tools = new ScriniaMcpTools();
        string result = await tools.Import(path, topicArray, overwrite, cancellationToken);

        if (result.StartsWith("Error:", StringComparison.Ordinal) ||
            result.StartsWith("No topics", StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(result)}[/]");
            return 1;
        }

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
    public Task<int> Bundle(
        [Argument] string topic,
        [Argument] string files,
        string? workspaceRoot = null,
        string? output = null,
        string? description = null,
        string? tags = null,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        string sanitizedTopic = ScriniaArtifactStore.SanitizeName(topic.Trim());
        if (string.IsNullOrWhiteSpace(sanitizedTopic))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Topic name is required.");
            return Task.FromResult(1);
        }

        // Resolve file paths
        var filePaths = ResolveFiles(files);
        if (filePaths.Count == 0)
        {
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
        AnsiConsole.MarkupLine(
            $"[green]Bundled {entries.Count} file(s) into topic '{Markup.Escape(sanitizedTopic)}' " +
            $"({ScriniaMcpTools.FormatBytes(fileSize)}) at {Markup.Escape(bundlePath)}[/]");
        return Task.FromResult(0);
    }

    /// <summary>Download embedding model for the embeddings plugin.</summary>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    public async Task<int> Setup(
        string? workspaceRoot = null,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        string exeDir = AppContext.BaseDirectory;
        string pluginsDir = Path.Combine(exeDir, "plugins");
        string pluginName = WorkspaceSetup.GetPluginName("plugins:embeddings", "scri-plugin-embeddings");

        string ext = OperatingSystem.IsWindows() ? ".exe" : "";
        string pluginExe = Path.Combine(pluginsDir, $"{pluginName}{ext}");

        if (!File.Exists(pluginExe))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Embeddings plugin not found.");
            AnsiConsole.MarkupLine($"[dim]Expected at: {Markup.Escape(pluginExe)}[/]");
            return 1;
        }

        string modelDir = Path.Combine(pluginsDir, pluginName, "models", "all-MiniLM-L6-v2");
        Directory.CreateDirectory(modelDir);

        string[] files = ["model.onnx", "vocab.txt"];
        string[] repoPaths = ["onnx/model.onnx", "vocab.txt"];
        const string baseUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main";

        bool allExist = files.All(f => File.Exists(Path.Combine(modelDir, f)));
        if (allExist)
        {
            AnsiConsole.MarkupLine("[green]Embedding model already downloaded.[/]");
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(modelDir)}[/]");
            return 0;
        }

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        for (int i = 0; i < files.Length; i++)
        {
            string filePath = Path.Combine(modelDir, files[i]);
            if (File.Exists(filePath))
            {
                AnsiConsole.MarkupLine($"[dim]{files[i]} already exists, skipping.[/]");
                continue;
            }

            string url = $"{baseUrl}/{repoPaths[i]}";
            AnsiConsole.MarkupLine($"Downloading [blue]{files[i]}[/]...");

            string tmpPath = filePath + ".tmp";
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

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
                    var task = ctx.AddTask(files[i], maxValue: totalBytes ?? 0);
                    if (totalBytes is null) task.IsIndeterminate = true;

                    await using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    byte[] buffer = new byte[81920];
                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
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
            AnsiConsole.MarkupLine($"[green]Downloaded {files[i]} ({sizeStr})[/]");
        }

        AnsiConsole.MarkupLine($"[green]Embedding model ready at:[/] {Markup.Escape(modelDir)}");
        return 0;
    }

    /// <summary>Get or set workspace configuration.</summary>
    /// <param name="key">Config key (e.g. plugins:embeddings). Omit to list all.</param>
    /// <param name="value">Value to set. Omit to read current value.</param>
    /// <param name="unset">Remove the setting.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    public int Config(
        [Argument] string? key = null,
        [Argument] string? value = null,
        bool unset = false,
        string? workspaceRoot = null)
    {
        WorkspaceSetup.Configure(workspaceRoot);
        string root = ScriniaArtifactStore.WorkspaceRootPath;

        if (key is null)
        {
            // List all settings
            var config = WorkspaceConfig.Load(root);
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
            if (WorkspaceConfig.UnsetValue(root, key))
                AnsiConsole.MarkupLine($"[green]Unset '{Markup.Escape(key)}'.[/]");
            else
                AnsiConsole.MarkupLine($"[dim]'{Markup.Escape(key)}' was not set.[/]");
            return 0;
        }

        if (value is null)
        {
            // Get a single value
            string? current = WorkspaceConfig.GetValue(root, key);
            if (current is not null)
                AnsiConsole.WriteLine(current);
            else
                AnsiConsole.MarkupLine("[dim]not set[/]");
            return 0;
        }

        // Set a value
        WorkspaceConfig.SetValue(root, key, value);
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
