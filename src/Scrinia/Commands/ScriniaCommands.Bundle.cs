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
        McpResponse response = await tools.Export(topicArray, filename, cancellationToken);

        if (response.IsError)
        {
            if (json) { WriteJsonError(response.Error ?? "unknown error"); return 1; }
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(FormatCliError(response))}");
            return 1;
        }

        string result = response.ToYaml();

        if (json)
        {
            WriteJson(new CliExportOutput("", result), CliJsonContext.Default.CliExportOutput);
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
        await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

        // CLI comma-splits; the HTTP endpoint binds repeated ?topics= keys. Deliberate — see
        // "Bundle commands: topic filters differ deliberately between CLI and HTTP" in
        // docs/architecture/cli.md.
        string[]? topicArray = topics?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var tools = new ScriniaMcpTools();
        McpResponse response = await tools.Import(path, topicArray, overwrite, cancellationToken);

        if (response.IsError)
        {
            if (json) { WriteJsonError(response.Error ?? "unknown error"); return 1; }
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
        string exportsDir = Path.Combine(WorkspaceSetup.LocalStore.WorkspaceRoot, ".scrinia", "exports");
        FileMemoryStore.EnsureGitIgnore(WorkspaceSetup.LocalStore.WorkspaceRoot);
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
            int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);
            long originalBytes = System.Text.Encoding.UTF8.GetByteCount(content);
            string preview = ContentPreview.Generate(content);

            string desc = !string.IsNullOrWhiteSpace(description)
                ? description
                : content[..Math.Min(200, content.Length)];

            // An explicit -d description is the more intentional value — prefer it as the
            // display carrier; otherwise fall back to the auto-generated content preview.
            string entryContentPreview = !string.IsNullOrWhiteSpace(description) ? desc : preview;

            Dictionary<string, string[]>? attributes = tagArray is { Length: > 0 }
                ? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [KnownAttributes.TagKey] = tagArray }
                : null;

            entries.Add(new ArtifactEntry(
                Name: entryName,
                Uri: "",
                OriginalBytes: originalBytes,
                ChunkCount: chunkCount,
                CreatedAt: DateTimeOffset.UtcNow,
                ContentPreview: entryContentPreview,
                Attributes: attributes));

            artifactContents[entryName] = artifact;
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
