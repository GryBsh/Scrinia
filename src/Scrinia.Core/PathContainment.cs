namespace Scrinia.Core;

/// <summary>Symlink-resolving, trailing-separator-safe path containment check for filesystem sandboxing.</summary>
public static class PathContainment
{
    // Windows/macOS default filesystems are case-insensitive; Linux is case-sensitive.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>True iff <paramref name="candidate"/> resolves to <paramref name="baseDir"/> or a path nested under it.
    /// Both operands are canonicalized (symlinks/junctions resolved on every existing segment) before comparison.</summary>
    public static bool IsWithin(string baseDir, string candidate)
    {
        string baseFull, candFull;
        try
        {
            baseFull = ResolveReal(baseDir);
            candFull = ResolveReal(candidate);
        }
        catch (IOException)
        {
            return false; // symlink loop / too many links: containment unprovable → deny (fail closed)
        }
        if (string.Equals(baseFull, candFull, PathComparison))
            return true;
        string withSep = baseFull.EndsWith(Path.DirectorySeparatorChar)
            ? baseFull
            : baseFull + Path.DirectorySeparatorChar;
        return candFull.StartsWith(withSep, PathComparison);
    }

    /// <summary>
    /// Canonicalizes <paramref name="path"/> to a real absolute path, resolving symlinks/junctions on every
    /// segment that already exists. The path may not exist yet (write destinations), so non-existent trailing
    /// segments are kept lexically. Resolving per-segment (not just the final node) means a symlinked ancestor
    /// is followed too.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.GetFullPath(string)"/> collapses any <c>..</c> segments lexically before symlink
    /// resolution begins. This is an accepted residual alongside the documented TOCTOU window: a
    /// traversal segment can't smuggle a path past containment, since the collapsed form is what gets
    /// resolved and compared.
    /// </remarks>
    private static string ResolveReal(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? "";
        if (root.Length == 0)
            return full;

        string[] segments = full[root.Length..]
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                   StringSplitOptions.RemoveEmptyEntries);

        string resolved = root;
        foreach (string seg in segments)
        {
            resolved = Path.Combine(resolved, seg);
            FileSystemInfo? info =
                Directory.Exists(resolved) ? new DirectoryInfo(resolved)
                : File.Exists(resolved) ? new FileInfo(resolved)
                : null;
            if (info is null)
                continue; // not-yet-existing segment: keep lexical form

            string? target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            // FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true).FullName is always absolute,
            // so no relative-combine branch is reachable here.
            if (target is not null)
                resolved = target;
        }
        return Path.GetFullPath(resolved);
    }
}
