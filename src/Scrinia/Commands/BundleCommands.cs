using ConsoleAppFramework;

namespace Scrinia.Commands;

/// <summary>Export, import, and pack portable memory bundles.</summary>
public sealed class BundleCommands
{
    private readonly ScriniaCommands _impl = new();

    /// <summary>Export topics to a .scrinia-bundle.</summary>
    /// <param name="topics">Comma-separated topic names to export (e.g. api,arch).</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="filename">-o, Output filename (saved to .scrinia/exports/).</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    public Task<int> Export(
        [Argument] string topics,
        string? workspaceRoot = null,
        string? filename = null,
        bool json = false,
        CancellationToken cancellationToken = default)
        => _impl.Export(topics, workspaceRoot, filename, json, cancellationToken);

    /// <summary>Import from a .scrinia-bundle.</summary>
    /// <param name="path">Path to the .scrinia-bundle file.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="topics">Comma-separated topic names to import (imports all if omitted).</param>
    /// <param name="overwrite">Replace existing entries if they conflict.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    public Task<int> Import(
        [Argument] string path,
        string? workspaceRoot = null,
        string? topics = null,
        bool overwrite = false,
        bool json = false,
        CancellationToken cancellationToken = default)
        => _impl.Import(path, workspaceRoot, topics, overwrite, json, cancellationToken);

    /// <summary>Pack raw files into a .scrinia-bundle.</summary>
    /// <param name="topic">Topic name for the bundle.</param>
    /// <param name="files">Comma-separated file paths or glob pattern (e.g. docs/*.md).</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="output">-o, Output filename (default: {topic}-{timestamp}.scrinia-bundle).</param>
    /// <param name="description">-d, Description for all entries.</param>
    /// <param name="tags">-t, Comma-separated tags for all entries.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    public Task<int> Pack(
        [Argument] string topic,
        [Argument] string files,
        string? workspaceRoot = null,
        string? output = null,
        string? description = null,
        string? tags = null,
        bool json = false,
        CancellationToken cancellationToken = default)
        => _impl.Bundle(topic, files, workspaceRoot, output, description, tags, json, cancellationToken);
}
