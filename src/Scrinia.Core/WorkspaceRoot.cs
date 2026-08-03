namespace Scrinia.Core;

/// <summary>
/// Resolves the workspace root for a working directory.
///
/// <para>The root is the nearest enclosing git repository, identified by a <c>.git</c> entry.
/// Resolution never consults whether a store exists, so a repository with no store cannot be
/// bound to an ancestor's, and the walk stops at the first repository boundary, so it cannot
/// leave the project.</para>
/// </summary>
public static class WorkspaceRoot
{
    /// <summary>
    /// Returns the workspace root for <paramref name="startDir"/>: the nearest ancestor
    /// containing a <c>.git</c> entry, examining <paramref name="startDir"/> itself first.
    /// When no repository encloses it, returns <paramref name="startDir"/> unchanged.
    /// </summary>
    public static string Resolve(string startDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDir);

        string full = Path.GetFullPath(startDir);
        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
        {
            if (IsRepositoryRoot(dir.FullName))
                return dir.FullName;
        }
        return full;
    }

    /// <summary>
    /// Returns <paramref name="explicitRoot"/> when the caller supplied one, otherwise resolves
    /// from <paramref name="startDir"/>. An explicit root is taken verbatim and no walk occurs.
    /// </summary>
    public static string Resolve(string? explicitRoot, string startDir) =>
        !string.IsNullOrWhiteSpace(explicitRoot)
            ? Path.GetFullPath(explicitRoot)
            : Resolve(startDir);

    /// <summary>
    /// True when <paramref name="dir"/> holds a <c>.git</c> entry. Worktrees and submodules
    /// write <c>.git</c> as a file containing a <c>gitdir:</c> pointer rather than a directory,
    /// so both forms count.
    /// </summary>
    private static bool IsRepositoryRoot(string dir)
    {
        string git = Path.Combine(dir, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }

    /// <summary>
    /// Returns <c>.scrinia</c> directories that resolution passed over: every ancestor strictly
    /// above <paramref name="root"/>, plus <paramref name="startDir"/> itself when it differs
    /// from the root. The root's own store is excluded — it is the one being used.
    /// </summary>
    public static IReadOnlyList<string> FindOrphanStores(string root, string startDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(startDir);

        string fullRoot = Path.GetFullPath(root);
        string fullStart = Path.GetFullPath(startDir);
        var found = new List<string>();

        if (!string.Equals(fullStart, fullRoot, StringComparison.OrdinalIgnoreCase))
            AddIfStore(fullStart, found);

        var above = new DirectoryInfo(fullRoot).Parent;
        for (var dir = above; dir is not null; dir = dir.Parent)
            AddIfStore(dir.FullName, found);

        return found;
    }

    private static void AddIfStore(string dir, List<string> into)
    {
        string store = Path.Combine(dir, ".scrinia");
        if (Directory.Exists(store) && !into.Contains(store, StringComparer.OrdinalIgnoreCase))
            into.Add(store);
    }
}
