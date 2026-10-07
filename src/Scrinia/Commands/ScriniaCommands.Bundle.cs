using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
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
        McpResponse response;
        try
        {
            response = await tools.Export(topicArray, filename, cancellationToken);
        }
        catch (Scrinia.Core.Bundles.BundleImportException exception)
        {
            return EmitBundleFailure(exception, json, "export");
        }
        catch (RestoreRecoveryException exception)
        {
            return EmitRecoveryBlocked(exception, json, "export");
        }

        if (response.IsError)
        {
            if (json) { WriteJsonError(response.Error ?? "unknown error", code: response.ErrorCode); return 1; }
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(FormatCliError(response))}");
            return 1;
        }

        string result = response.ToYaml();

        if (json)
        {
            WriteJson(new CliExportOutput(result), CliJsonContext.Default.CliExportOutput);
            return response.IsWarning ? 1 : 0;
        }
        return RenderEnvelope(response, result);
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
        await EmbeddingsHost.LoadPluginsAsync(WorkspaceSetup.Root, cancellationToken);

        // Parse the CLI's comma-separated topic filter at this local command boundary before
        // dispatching to the MCP implementation.
        string[]? topicArray = topics?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var tools = new ScriniaMcpTools();
        McpResponse response = await tools.Import(path, topicArray, overwrite, cancellationToken);

        if (response.IsError)
        {
            if (json)
            {
                WriteJson(new CliImportOutput(response.ToYaml(), response.ErrorCode, response.Status),
                    CliJsonContext.Default.CliImportOutput);
                return 1;
            }
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(FormatCliError(response))}");
            return 1;
        }

        string result = response.ToYaml();

        if (json)
        {
            WriteJson(new CliImportOutput(result), CliJsonContext.Default.CliImportOutput);
            return response.IsWarning ? 1 : 0;
        }
        return RenderEnvelope(response, result);
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

        string sanitizedTopic = WorkspaceSetup.Store.SanitizeName(topic.Trim());
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
        string exportsDir = Path.Combine(WorkspaceSetup.Store.WorkspaceRoot, ".scrinia", "exports");
        FileMemoryStore.EnsureGitIgnore(WorkspaceSetup.Store.WorkspaceRoot);
        Directory.CreateDirectory(exportsDir);

        if (!TryResolveBundlePath(exportsDir, output, out string bundlePath, out string? pathError, sanitizedTopic))
        {
            if (json) { WriteJsonError(pathError!); return Task.FromResult(1); }
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(pathError!)}");
            return Task.FromResult(1);
        }

        var entries = new List<ArtifactEntry>();
        var artifactContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string content = File.ReadAllText(filePath);
            string entryName = WorkspaceSetup.Store.SanitizeName(Path.GetFileNameWithoutExtension(filePath));

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
            long originalBytes = System.Text.Encoding.UTF8.GetByteCount(content);

            Dictionary<string, string[]>? attributes = tagArray is { Length: > 0 }
                ? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [KnownAttributes.TagKey] = tagArray }
                : null;

            // Derive metadata through the single authoritative pipeline (the store derives; callers
            // must not). ForStore recomputes chunk count, term frequencies, keywords, chunk entries,
            // auto-derived attributes, and the preview — no hand-derived duplicate. fileHasher is
            // null so a bundle never captures the exporting machine's file paths as a drift baseline.
            var derived = MemoryDerivation.ForStore(
                [content],
                description: string.IsNullOrWhiteSpace(description) ? null : description,
                attributes: attributes,
                fileHasher: null);

            entries.Add(new ArtifactEntry(
                Name: entryName,
                Uri: "",
                OriginalBytes: originalBytes,
                ChunkCount: Nmp2ChunkedEncoder.GetChunkCount(artifact),
                CreatedAt: DateTimeOffset.UtcNow,
                ContentPreview: derived.ContentPreview,
                Keywords: derived.Keywords.Length > 0 ? derived.Keywords : null,
                TermFrequencies: derived.TermFrequencies.Count > 0 ? derived.TermFrequencies : null,
                ChunkEntries: derived.ChunkEntries,
                Attributes: derived.Attributes,
                AgentKeywords: derived.AgentKeywords));

            artifactContents[entryName] = artifact;
        }

        // Warn about requested path segments that resolved to nothing (typos, misspellings).
        var missingPaths = CollectUnresolvedPaths(files);
        if (missingPaths.Count > 0)
        {
            string message = $"The following requested path(s) did not resolve to any files: {string.Join(", ", missingPaths)}";
            if (json)
                Console.Error.WriteLine($"[scrinia:bundle-warn] {message}");
            else
                AnsiConsole.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(message)}[/]");
        }

        // Create the bundle zip. The archive layout has ONE definition, in BundleFormatService —
        // this command used to duplicate it inline, along with a hard-coded manifest version.
        using (var stream = new FileStream(bundlePath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Scrinia.Core.Bundles.BundleFormatService.WriteTopic(
                zip, WorkspaceSetup.Store, sanitizedTopic, entries,
                artifactContents.Select(kv => (kv.Key, kv.Value)));

            Scrinia.Core.Bundles.BundleFormatService.WriteManifest(
                zip, [sanitizedTopic], entries.Count, fileEntities: null);
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

    /// <summary>Reports each comma-separated path segment in <paramref name="filesArg"/> that would
    /// resolve to zero files under <see cref="ResolveFiles"/> — i.e. a plain file that doesn't exist
    /// or a glob with no matches in an existing directory. Used to warn the caller on typos.</summary>
    private static List<string> CollectUnresolvedPaths(string filesArg) =>
        ScanFilesArg(filesArg)
            .Where(r => !r.Resolved)
            .Select(r => r.Source)
            .ToList();

    /// <summary>Resolves the bundle output path inside <paramref name="exportsDir"/>, rejecting any
    /// --output that escapes it (absolute path or traversal).</summary>
    internal static bool TryResolveBundlePath(
        string exportsDir, string? output, out string bundlePath, out string? error, string defaultPrefix = "bundle")
    {
        error = null;
        string bundleName = string.IsNullOrWhiteSpace(output)
            ? $"{defaultPrefix}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}"
            : output;
        if (!bundleName.EndsWith(".scrinia-bundle", StringComparison.OrdinalIgnoreCase))
            bundleName += ".scrinia-bundle";

        bundlePath = Path.GetFullPath(Path.Combine(exportsDir, bundleName));
        if (!PathContainment.IsWithin(exportsDir, bundlePath))
        {
            error = $"--output '{output}' must stay within the exports directory.";
            bundlePath = "";
            return false;
        }
        return true;
    }
}
