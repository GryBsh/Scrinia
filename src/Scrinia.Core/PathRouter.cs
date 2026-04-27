namespace Scrinia.Core;

/// <summary>
/// Maps <see cref="ParsedPath"/> instances to their filesystem locations under
/// <c>.scrinia/memories/</c>.  Pure and side-effect-free except for
/// <see cref="ToLegacyPath"/> which probes the filesystem for v1 fallback reads.
/// </summary>
public static class PathRouter
{
    private const string MemoriesDir = "memories";

    /// <summary>
    /// Maps a parsed path to its filesystem location under <c>.scrinia/memories/</c>.
    /// Returns <see langword="null"/> for ephemeral <c>/temp/</c> paths.
    /// </summary>
    /// <param name="path">The parsed path to resolve.</param>
    /// <param name="workspaceRoot">Absolute path to the workspace root directory.</param>
    /// <returns>
    /// The resolved filesystem path, or <see langword="null"/> when
    /// <paramref name="path"/> is ephemeral.
    /// </returns>
    public static string? ToFilesystemPath(ParsedPath path, string workspaceRoot)
    {
        if (IsEphemeral(path)) return null;

        // Build directory from all segments except the last.
        // Last segment becomes the filename.
        string scriniaDir = Path.Combine(workspaceRoot, ".scrinia", MemoriesDir);

        var segments = path.Segments;
        if (segments.Count == 0) return null;

        // Build subdirectory path from all but last segment.
        string dir = scriniaDir;
        for (int i = 0; i < segments.Count - 1; i++)
            dir = Path.Combine(dir, segments[i].Value);

        // Determine extension based on first segment.
        string ext = segments[0].Value.ToLowerInvariant() switch
        {
            "agent" or "skill" => ".md",
            "workflow" => ".json",
            _ => ".nmp2"
        };

        string leaf = segments[^1].Value;
        return Path.Combine(dir, leaf + ext);
    }

    /// <summary>
    /// Returns the versions directory for the given path (a <c>versions/</c> folder
    /// that is a sibling of the artifact file's parent).
    /// </summary>
    /// <param name="path">The parsed path.</param>
    /// <param name="workspaceRoot">Absolute path to the workspace root directory.</param>
    /// <returns>The absolute path to the versions directory.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="path"/> is ephemeral.
    /// </exception>
    public static string ToVersionsDir(ParsedPath path, string workspaceRoot)
    {
        string? filePath = ToFilesystemPath(path, workspaceRoot);
        if (filePath is null)
            throw new InvalidOperationException("Ephemeral paths have no versions directory");

        return Path.Combine(Path.GetDirectoryName(filePath)!, "versions");
    }

    /// <summary>
    /// Returns the metadata sidecar path (<c>.meta.json</c>) for the given path.
    /// </summary>
    /// <param name="path">The parsed path.</param>
    /// <param name="workspaceRoot">Absolute path to the workspace root directory.</param>
    /// <returns>The absolute path to the metadata sidecar file.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="path"/> is ephemeral.
    /// </exception>
    public static string ToMetadataPath(ParsedPath path, string workspaceRoot)
    {
        string? filePath = ToFilesystemPath(path, workspaceRoot);
        if (filePath is null)
            throw new InvalidOperationException("Ephemeral paths have no metadata");

        return Path.ChangeExtension(filePath, ".meta.json");
    }

    /// <summary>
    /// Returns <see langword="true"/> when the path starts with <c>/temp/</c>,
    /// indicating it should be stored in the ephemeral in-memory cache only.
    /// </summary>
    /// <param name="path">The parsed path to check.</param>
    /// <returns><see langword="true"/> when the path is ephemeral.</returns>
    public static bool IsEphemeral(ParsedPath path) =>
        path.Segments.Count > 0 &&
        path.Segments[0].Value.Equals("temp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Converts a v2 path to the v1 legacy filesystem location for fallback reads.
    /// Only simple 2-segment paths have v1 equivalents.  Probes the filesystem
    /// to find the first existing legacy location.
    /// </summary>
    /// <param name="path">The parsed path.</param>
    /// <param name="workspaceRoot">Absolute path to the workspace root directory.</param>
    /// <returns>
    /// The first existing legacy path, or <see langword="null"/> when no v1 equivalent
    /// exists or no file is found.
    /// </returns>
    public static string? ToLegacyPath(ParsedPath path, string workspaceRoot)
    {
        // v1 had flat topic:subject -> .scrinia/topics/{topic}/{subject}.nmp2
        // Only simple 2-segment paths have v1 equivalents.
        if (path.Segments.Count != 2) return null;

        string topic = path.Segments[0].Value;
        string subject = path.Segments[1].Value;

        // Check both legacy flat and G-53 namespaced paths.
        string flatPath = Path.Combine(workspaceRoot, ".scrinia", "topics", topic, subject + ".nmp2");
        if (File.Exists(flatPath)) return flatPath;

        // G-53 namespaced: entity topics went to topics/entity/{topic}/
        string namespacedPath = Path.Combine(workspaceRoot, ".scrinia", "topics", "entity", topic, subject + ".nmp2");
        if (File.Exists(namespacedPath)) return namespacedPath;

        // Memory namespace: topics/memory/{topic}/
        string memoryPath = Path.Combine(workspaceRoot, ".scrinia", "topics", "memory", topic, subject + ".nmp2");
        if (File.Exists(memoryPath)) return memoryPath;

        // Agent namespace: topics/agent/
        if (topic.Equals("agent", StringComparison.OrdinalIgnoreCase))
        {
            string agentMdPath = Path.Combine(workspaceRoot, ".scrinia", "agent", subject + ".md");
            if (File.Exists(agentMdPath)) return agentMdPath;

            string agentNmpPath = Path.Combine(workspaceRoot, ".scrinia", "topics", "agent", subject + ".nmp2");
            if (File.Exists(agentNmpPath)) return agentNmpPath;
        }

        return null;
    }
}
