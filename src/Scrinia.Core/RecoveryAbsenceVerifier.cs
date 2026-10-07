namespace Scrinia.Core;

/// <summary>Proves an authorized recovery member is absent without treating inspection failure as absence.</summary>
internal static class RecoveryAbsenceVerifier
{
    internal static AsyncLocal<Func<string, Exception?>?> InspectionFailureInjector { get; } = new();

    internal static void RequireMissing(string authorizedDirectory, string path, CancellationToken cancellationToken)
    {
        string boundary = Path.GetFullPath(authorizedDirectory);
        string fullPath = Path.GetFullPath(path);
        string canonicalBoundary = ResolveBoundary(boundary);
        if (!PathContainment.IsWithin(boundary, fullPath))
            throw new IOException("Recovery member is outside its authorized directory.");

        // Resolve the admitted base, preserving every segment beneath it for reparse inspection.
        string relative = Path.GetRelativePath(boundary, fullPath);
        if (!IsMemberRelativePath(relative))
            throw new IOException("Recovery member cannot be inspected beneath its authorized directory.");
        string inspectedPath = Path.Combine(canonicalBoundary, relative);
        string parent = Path.GetDirectoryName(inspectedPath)
            ?? throw new IOException("Recovery member has no inspectable parent.");
        RequireInspectableParents(parent, canonicalBoundary, cancellationToken);
        try
        {
            Inspect(inspectedPath, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // A not-found stat alone can conceal a dangling entry or a changed parent.
            // Enumerate the validated parent and reject any entry with the target name.
            string name = Path.GetFileName(inspectedPath);
            foreach (string entry in Directory.EnumerateFileSystemEntries(parent))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.GetFileName(entry).Equals(name, PathComparison))
                    throw new IOException("An entry exists at a committed absent recovery member.");
            }
            RequireInspectableParents(parent, canonicalBoundary, cancellationToken);
            if (!string.Equals(ResolveBoundary(boundary), canonicalBoundary, PathComparison)
                || !PathContainment.IsWithin(boundary, fullPath))
                throw new IOException("Recovery member boundary changed during absence verification.");
            return;
        }
        throw new IOException("An entry exists at a committed absent recovery member.");
    }

    private static string ResolveBoundary(string boundary)
    {
        try { return PathContainment.ResolveCanonical(boundary); }
        catch (ArgumentException exception)
        { throw new IOException("Recovery member boundary cannot be resolved safely.", exception); }
    }

    private static readonly StringComparison PathComparison = OperatingSystem.IsLinux()
        ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static bool IsMemberRelativePath(string relative) =>
        !Path.IsPathRooted(relative) && relative != "."
        && !relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..", StringComparer.Ordinal);

    private static void RequireInspectableParents(string parent, string boundary, CancellationToken cancellationToken)
    {
        for (string? current = parent; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes = Inspect(current, cancellationToken);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Recovery member parent is not an inspectable directory.");
            if (string.Equals(current, boundary, PathComparison)) return;
        }
        throw new IOException("Recovery member has no inspectable authorized boundary.");
    }

    private static FileAttributes Inspect(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Exception? failure = InspectionFailureInjector.Value?.Invoke(path);
        if (failure is not null) throw failure;
        return File.GetAttributes(path);
    }
}
