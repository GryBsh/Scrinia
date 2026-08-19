namespace Scrinia.Server.Tests;

/// <summary>
/// Resolves paths relative to the repository root from a test assembly's output directory.
/// The walk up to <c>Scrinia.sln</c> lives in <see cref="Scrinia.TestDoubles.RepoPaths"/>;
/// this delegates to it so no call site here changes.
/// </summary>
public static class TestPaths
{
    /// <summary>
    /// Combines <paramref name="relativeSegments"/> onto the repository root, found by walking
    /// upward from <see cref="AppContext.BaseDirectory"/> until a directory containing
    /// <c>Scrinia.sln</c> is found.
    /// </summary>
    public static string RepoRoot(params string[] relativeSegments) =>
        Scrinia.TestDoubles.RepoPaths.Combine(relativeSegments);
}
