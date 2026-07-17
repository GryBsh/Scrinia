namespace Scrinia.Core;

/// <summary>Trailing-separator-safe path containment check for filesystem sandboxing.</summary>
public static class PathContainment
{
    /// <summary>True iff <paramref name="candidate"/> resolves to <paramref name="baseDir"/> or a path nested under it.</summary>
    public static bool IsWithin(string baseDir, string candidate)
    {
        string baseFull = Path.GetFullPath(baseDir);
        string candFull = Path.GetFullPath(candidate);
        if (string.Equals(baseFull, candFull, StringComparison.OrdinalIgnoreCase))
            return true;
        string withSep = baseFull.EndsWith(Path.DirectorySeparatorChar)
            ? baseFull
            : baseFull + Path.DirectorySeparatorChar;
        return candFull.StartsWith(withSep, StringComparison.OrdinalIgnoreCase);
    }
}
