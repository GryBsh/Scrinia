namespace Scrinia.Server.Tests;

/// <summary>
/// Resolves paths relative to the repository root from a test assembly's output directory.
/// Shared by any test that needs to walk source files on disk rather than reflect over the
/// loaded assemblies.
/// </summary>
public static class TestPaths
{
    /// <summary>
    /// Combines <paramref name="relativeSegments"/> onto the repository root, found by walking
    /// upward from <see cref="AppContext.BaseDirectory"/> until a directory containing
    /// <c>Scrinia.sln</c> is found.
    /// </summary>
    public static string RepoRoot(params string[] relativeSegments) =>
        Path.Combine([FindRepoRoot(), .. relativeSegments]);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Scrinia.sln")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException(
                $"Could not find Scrinia.sln walking up from {AppContext.BaseDirectory}.");
        return dir.FullName;
    }
}
