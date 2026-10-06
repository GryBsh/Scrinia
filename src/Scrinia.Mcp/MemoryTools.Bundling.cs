using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Encoding;
using Scrinia.Core.Formats;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Mcp;

public sealed partial class ScriniaMcpTools
{
    /// <summary>Export one or more local topics into a portable .scrinia-bundle file.</summary>
    internal Task<McpResponse> Export(
        [Description("Topic names to export (e.g. [\"api\", \"arch\"]).")] string[] topics,
        [Description("Output filename (saved to .scrinia/exports/). Defaults to auto-generated name.")] string? filename = null,
        CancellationToken cancellationToken = default)
    {
        if (topics is null || topics.Length == 0)
            return Task.FromResult(ResponseBuilder.Error(
                "At least one topic name is required.",
                ErrorCodes.InvalidParameter,
                "bundle('export', { topics: ['api', 'arch'] })"));

        var store = CurrentStore;

        string scriniaDir = Path.Combine(store.WorkspaceRoot, ".scrinia");
        FileMemoryStore.EnsureGitIgnore(store.WorkspaceRoot);
        string exportsDir = Path.Combine(scriniaDir, "exports");
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

        // Build-before-write + atomic publish + cleanup-on-failure live in ExportTopicsToZip: a
        // malformed topic never reaches here, and a failed publish removes the target. The bundle is
        // only on disk when the export fully succeeds.
        (exportedTopics, totalEntries) = Scrinia.Core.Bundles.BundleFormatService.ExportTopicsToZip(bundlePath, store, topics);

        if (exportedTopics.Count == 0)
        {
            try { File.Delete(bundlePath); } catch { }
            return Task.FromResult(ResponseBuilder.Error(
                "No entries found in the specified topics.",
                ErrorCodes.NotFound,
                "list() to see available topics with entry counts"));
        }

        long fileSize = new FileInfo(bundlePath).Length;
        return Task.FromResult(
            ResponseBuilder.Success($"Exported {exportedTopics.Count} topic(s) ({totalEntries} entries, {FormatBytes(fileSize)}) to {bundlePath}")
                .WithAction("exported"));
    }

    /// <summary>Import topics from a .scrinia-bundle file into the local workspace.</summary>
    internal Task<McpResponse> Import(
        [Description("Path to the .scrinia-bundle file (relative to workspace or absolute).")] string bundlePath,
        [Description("Optional topic names to import. If empty, imports all topics in the bundle.")] string[]? topics = null,
        [Description("When true, replaces existing entries if they conflict.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Resolve path relative to workspace root if not absolute
        string workspaceRoot = store.WorkspaceRoot;

        string resolvedPath = Path.IsPathRooted(bundlePath)
            ? bundlePath
            : Path.Combine(workspaceRoot, bundlePath);
        resolvedPath = Path.GetFullPath(resolvedPath);

        // SEC-041: prevent path traversal outside workspace
        if (!PathContainment.IsWithin(workspaceRoot, resolvedPath))
            return Task.FromResult(ResponseBuilder.Error(
                "Bundle path must be within the workspace.",
                ErrorCodes.InvalidPath,
                "Move the bundle file under the workspace root and retry."));

        if (!File.Exists(resolvedPath))
            return Task.FromResult(ResponseBuilder.Error(
                $"Bundle file not found: {resolvedPath}",
                ErrorCodes.NotFound,
                "Verify the bundle path is correct and the file exists."));

        try
        {
            var bundle = OpenBundleForImport(resolvedPath);
            using var stream = bundle.Stream;
            using var zip = bundle.Archive;

            var (topicCount, entryCount, names) =
                Scrinia.Core.Bundles.BundleFormatService.ImportTopicsFromZipAsync(
                    zip, store, topics, overwrite, cancellationToken).GetAwaiter().GetResult();

            if (topicCount == 0)
                return Task.FromResult(ResponseBuilder.Warning("No topics were imported (empty bundle or all filtered out)."));

            string message = topicCount > 0
                ? $"Imported {topicCount} topic(s) ({entryCount} entries): {string.Join(", ", names)}"
                : "No topics imported.";
            return Task.FromResult(ResponseBuilder.Success(message).WithAction("imported"));
        }
        catch (RestoreRecoveryException ex)
        {
            return Task.FromResult(RecoveryBlockedResponse(ex));
        }
        catch (BundleImportException ex)
        {
            string code = ex.Failure == BundleImportFailure.RecoveryRequired
                ? ErrorCodes.BundleRecoveryRequired
                : ErrorCodes.BundleImportFailed;
            return Task.FromResult(ResponseBuilder.Error(
                ex.Message,
                code,
                ex.Failure == BundleImportFailure.RecoveryRequired
                    ? "Resolve the retained bundle recovery evidence before retrying."
                    : "Retry the import after confirming the bundle and workspace are writable."));
        }
        catch (BundleValidationException ex)
        {
            return Task.FromResult(ResponseBuilder.Error(
                ex.Message,
                ErrorCodes.InvalidBundle,
                "Obtain a complete supported bundle and retry the import."));
        }
        catch (DurableWriteException ex)
        {
            return Task.FromResult(DurableWriteFailureResponse(ex));
        }
        catch (ScopeReadOnlyException ex)
        {
            return Task.FromResult(DurableWriteFailureResponse(ex));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(ResponseBuilder.Error(
                ex.Message,
                ErrorCodes.DurableWriteFailed,
                "Check permissions, free disk space if needed, and retry."));
        }
        catch (InvalidDataException ex)
        {
            return Task.FromResult(ResponseBuilder.Error(
                ex.Message,
                ErrorCodes.DurableWriteFailed,
                "Check permissions, free disk space if needed, and retry."));
        }
        catch (IOException ex)
        {
            return Task.FromResult(ResponseBuilder.Error(
                ex.Message,
                ErrorCodes.DurableWriteFailed,
                "Check permissions, free disk space if needed, and retry."));
        }
    }

    internal static (FileStream Stream, ZipArchive Archive) OpenBundleForImport(string path)
        => OpenBundleForImport(path, BundleReadLimits.Default);

    internal static (FileStream Stream, ZipArchive Archive) OpenBundleForImport(string path, BundleReadLimits limits)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            if (stream.Length > limits.CompressedFileBytes)
                throw new BundleValidationException("INVALID_BUNDLE: archive exceeds the compressed file size budget.");
            return (stream, new ZipArchive(stream, ZipArchiveMode.Read));
        }
        catch (BundleValidationException)
        {
            stream?.Dispose();
            throw;
        }
        catch (InvalidDataException exception)
        {
            stream?.Dispose();
            throw new BundleValidationException("INVALID_BUNDLE: the bundle archive is malformed.", exception);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            stream?.Dispose();
            throw new DurableWriteException("read bundle", path, exception);
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
