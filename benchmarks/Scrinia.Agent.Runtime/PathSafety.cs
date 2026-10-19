namespace Scrinia.Agent.Benchmarks;

internal static class PathSafety
{
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static bool Contains(string parent, string child)
    {
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        child = Path.GetFullPath(child);
        return string.Equals(parent, child, Comparison) || child.StartsWith(parent + Path.DirectorySeparatorChar, Comparison);
    }
    internal static void RequireSeparate(string first, string second)
    {
        if (Contains(first, second) || Contains(second, first)) throw InputDiagnostics.Invalid("Input and output paths must not overlap.");
    }
    internal static void RequireNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw InputDiagnostics.Invalid("Links and nonregular entries are not supported in benchmark paths.");
                UnixPathType.RequireRegularOrDirectory(current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
