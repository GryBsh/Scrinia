namespace Scrinia.Core;

/// <summary>
/// Maps <see cref="ParsedPath"/> instances to their filesystem locations under <c>.scrinia/</c>:
/// hand-authored file entities (skills, agent configs) resolve to a top-level directory,
/// everything else resolves under <c>.scrinia/memories/</c>. Pure and side-effect-free.
/// </summary>
public static class PathRouter
{
    private const string MemoriesDir = "memories";

    /// <summary>
    /// Path segment name -> on-disk directory name for hand-authored file entities.
    /// <para>The singular/plural mismatch ("skill" -> "skills", "agent" -> "agent") is historical.
    /// It was previously implicit in five separate call sites; this table is the only place it
    /// exists.</para>
    /// <para>These directories sit at the top level of <c>.scrinia/</c>, deliberately NOT under
    /// <c>memories/</c>: their <c>.meta.json</c> sidecars share a filename namespace with the
    /// store's own <c>ArtifactEntry</c> sidecars, so a file entity inside a store scope directory
    /// would be destroyed by <see cref="FileMemoryStore.SaveIndex"/>'s orphan sweep.</para>
    /// </summary>
    private static readonly Dictionary<string, string> FileEntityDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["skill"] = "skills",
        ["agent"] = "agent",
    };

    /// <summary>True when <paramref name="segment"/> names a hand-authored file-entity kind.</summary>
    public static bool IsFileEntity(string? segment) =>
        !string.IsNullOrEmpty(segment) && FileEntityDirs.ContainsKey(segment);

    /// <summary>
    /// Resolves a file-entity kind to its directory. The single authority for where skills and
    /// agent configs live on disk.
    /// </summary>
    /// <exception cref="ArgumentException">The kind is not a known file entity. Callers pass a
    /// literal, never user input — an unknown kind is a programming error, matching
    /// <see cref="ToStoreDir"/>'s contract.</exception>
    public static string ToFileEntityDir(string kind, string workspaceRoot)
    {
        if (!FileEntityDirs.TryGetValue(kind, out string? dirName))
            throw new ArgumentException($"Unknown file-entity kind: {kind}", nameof(kind));
        return Path.Combine(workspaceRoot, ".scrinia", dirName);
    }

    /// <summary>
    /// Resolves a file entity's full path, enforcing both name validity and directory containment.
    /// Returns false for a name that is invalid or would escape — callers surface their own error.
    /// <para>Validation lives here so it cannot be forgotten: it was previously duplicated at two
    /// call sites and absent from the rest.</para>
    /// </summary>
    public static bool TryToFileEntityPath(string kind, string? name, string workspaceRoot, out string path)
    {
        path = "";
        if (!FileEntityName.IsValid(name))
            return false;

        string dir = ToFileEntityDir(kind, workspaceRoot);
        string candidate = Path.Combine(dir, name + ".md");
        if (!PathContainment.IsWithin(dir, candidate))
            return false;

        path = candidate;
        return true;
    }

    /// <summary>
    /// Maps a parsed path to its filesystem location. Hand-authored file entities (skills, agent
    /// configs) resolve to the top level of <c>.scrinia/</c>; everything else resolves under
    /// <c>.scrinia/memories/</c>. Returns <see langword="null"/> for ephemeral <c>/temp/</c> paths.
    /// </summary>
    /// <param name="path">The parsed path to resolve.</param>
    /// <param name="workspaceRoot">Absolute path to the workspace root directory.</param>
    /// <returns>
    /// The resolved filesystem path, or <see langword="null"/> when
    /// <paramref name="path"/> is ephemeral or is a malformed/invalid file-entity reference.
    /// </returns>
    public static string? ToFilesystemPath(ParsedPath path, string workspaceRoot)
    {
        if (IsEphemeral(path)) return null;

        var segments = path.Segments;
        if (segments.Count == 0) return null;

        // Hand-authored file entities are flat and live outside the store.
        if (IsFileEntity(segments[0].Value))
        {
            if (segments.Count != 2) return null;
            return TryToFileEntityPath(segments[0].Value, segments[1].Value, workspaceRoot, out string fePath)
                ? fePath
                : null;
        }

        // Everything else is an NMP/2 memory under .scrinia/memories/.
        string dir = Path.Combine(workspaceRoot, ".scrinia", MemoriesDir);
        for (int i = 0; i < segments.Count - 1; i++)
            dir = Path.Combine(dir, segments[i].Value);

        return Path.Combine(dir, segments[^1].Value + ".nmp2");
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
    /// Maps an internal scope string ("local", "local:findings", "local:goal/G-5") to its
    /// store directory under <c>.scrinia/memories/</c>. The single authority for
    /// scope-to-directory resolution (v3 layout — see <see cref="StoreLayoutMigration"/>).
    /// </summary>
    public static string ToStoreDir(string scope, string workspaceRoot)
    {
        string memories = Path.Combine(workspaceRoot, ".scrinia", MemoriesDir);
        if (scope == "local")
            return memories;

        if (scope.StartsWith("local:", StringComparison.Ordinal))
            return Path.Combine(memories,
                scope["local:".Length..].Replace('/', Path.DirectorySeparatorChar));

        throw new ArgumentException($"Unknown scope: {scope}");
    }
}
