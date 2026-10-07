namespace Scrinia.Core;

/// <summary>Proves an authorized recovery member is absent without treating inspection failure as absence.</summary>
internal static class RecoveryAbsenceVerifier
{
    internal static AsyncLocal<Func<string, Exception?>?> InspectionFailureInjector { get; } = new();

    internal static void RequireMissing(string path, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        string parent = Path.GetDirectoryName(fullPath)
            ?? throw new IOException("Recovery member has no inspectable parent.");
        RequireInspectableParents(parent, cancellationToken);
        try
        {
            Inspect(fullPath, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // A not-found stat alone can conceal a dangling entry or a changed parent.
            // Enumerate the validated parent and reject any entry with the target name.
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string name = Path.GetFileName(fullPath);
            foreach (string entry in Directory.EnumerateFileSystemEntries(parent))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.GetFileName(entry).Equals(name, comparison))
                    throw new IOException("An entry exists at a committed absent recovery member.");
            }
            RequireInspectableParents(parent, cancellationToken);
            return;
        }
        throw new IOException("An entry exists at a committed absent recovery member.");
    }

    private static void RequireInspectableParents(string parent, CancellationToken cancellationToken)
    {
        for (string? current = parent; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes = Inspect(current, cancellationToken);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Recovery member parent is not an inspectable directory.");
        }
    }

    private static FileAttributes Inspect(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Exception? failure = InspectionFailureInjector.Value?.Invoke(path);
        if (failure is not null) throw failure;
        return File.GetAttributes(path);
    }
}
