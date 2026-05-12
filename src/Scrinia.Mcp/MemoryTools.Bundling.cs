using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Mcp;

public sealed partial class ScriniaMcpTools
{
    /// <summary>Export one or more local topics into a portable .scrinia-bundle file.</summary>
    internal Task<string> Export(
        [Description("Topic names to export (e.g. [\"api\", \"arch\"]).")] string[] topics,
        [Description("Output filename (saved to .scrinia/exports/). Defaults to auto-generated name.")] string? filename = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;
        if (topics is null || topics.Length == 0)
            return Task.FromResult(ResponseBuilder.Error(
                "At least one topic name is required.",
                ErrorCodes.InvalidParameter,
                "bundle('export', { topics: ['api', 'arch'] })").ToYaml());

        string exportsDir = Path.Combine(store.GetStoreDirForScope("local"), "..", "exports");
        exportsDir = Path.GetFullPath(exportsDir);
        Directory.CreateDirectory(exportsDir);

        string bundleName = string.IsNullOrWhiteSpace(filename)
            ? $"export-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}"
            : filename;
        if (!bundleName.EndsWith(".scrinia-bundle", StringComparison.OrdinalIgnoreCase))
            bundleName += ".scrinia-bundle";

        // Sanitize filename: strip control characters and path separators
        bundleName = new string(bundleName.Where(c => !char.IsControl(c) && c != '/' && c != '\\').ToArray());
        bundleName = Path.GetFileName(bundleName);

        string bundlePath = Path.Combine(exportsDir, bundleName);

        List<string> exportedTopics;
        int totalEntries;

        using (var stream = new FileStream(bundlePath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            (exportedTopics, totalEntries) = Scrinia.Core.Bundles.BundleFormatService.ExportTopicsToZip(zip, store, topics);

            if (exportedTopics.Count == 0)
            {
                try { File.Delete(bundlePath); } catch { }
                return Task.FromResult(ResponseBuilder.Error(
                    "No entries found in the specified topics.",
                    ErrorCodes.NotFound,
                    "memory('list') to see available topics with entry counts").ToYaml());
            }
        }

        long fileSize = new FileInfo(bundlePath).Length;
        return Task.FromResult(
            ResponseBuilder.Success($"Exported {exportedTopics.Count} topic(s) ({totalEntries} entries, {FormatBytes(fileSize)}) to {bundlePath}")
                .WithAction("exported").ToYaml());
    }

    /// <summary>Import topics from a .scrinia-bundle file into the local workspace.</summary>
    internal Task<string> Import(
        [Description("Path to the .scrinia-bundle file (relative to workspace or absolute).")] string bundlePath,
        [Description("Optional topic names to import. If empty, imports all topics in the bundle.")] string[]? topics = null,
        [Description("When true, replaces existing entries if they conflict.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Resolve path relative to workspace root if not absolute
        string storeDir = store.GetStoreDirForScope("local");
        string workspaceRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(storeDir)!, ".."));

        string resolvedPath = Path.IsPathRooted(bundlePath)
            ? bundlePath
            : Path.Combine(workspaceRoot, bundlePath);
        resolvedPath = Path.GetFullPath(resolvedPath);

        // SEC-041: prevent path traversal outside workspace
        if (!resolvedPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ResponseBuilder.Error(
                "Bundle path must be within the workspace.",
                ErrorCodes.InvalidPath,
                "Move the bundle file under the workspace root and retry.").ToYaml());

        if (!File.Exists(resolvedPath))
            return Task.FromResult(ResponseBuilder.Error(
                $"Bundle file not found: {resolvedPath}",
                ErrorCodes.NotFound,
                "Verify the bundle path is correct and the file exists.").ToYaml());

        try
        {
            using var stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            var (topicCount, entryCount, names) =
                Scrinia.Core.Bundles.BundleFormatService.ImportTopicsFromZip(zip, store, topics, overwrite);

            if (topicCount == 0)
                return Task.FromResult(ResponseBuilder.Warning("No topics were imported (empty bundle or all filtered out).").ToYaml());

            return Task.FromResult(
                ResponseBuilder.Success($"Imported {topicCount} topic(s) ({entryCount} entries): {string.Join(", ", names)}")
                    .WithAction("imported").ToYaml());
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(ResponseBuilder.Error(
                ex.Message,
                ErrorCodes.Internal,
                "Verify the bundle file is a valid .scrinia-bundle archive.").ToYaml());
        }
    }

    public static string FormatBytes(long bytes) =>
        bytes switch
        {
            < 1_024 => $"{bytes} B",
            < 1_048_576 => $"{bytes / 1_024.0:F1} KB",
            < 1_073_741_824 => $"{bytes / 1_048_576.0:F1} MB",
            _ => $"{bytes / 1_073_741_824.0:F1} GB",
        };
}
