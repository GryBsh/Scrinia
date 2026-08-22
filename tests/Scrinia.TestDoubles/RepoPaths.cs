namespace Scrinia.TestDoubles;

/// <summary>
/// Resolves paths relative to the repository root from a test assembly's output directory,
/// for tests that read source files on disk rather than reflecting over loaded assemblies.
///
/// <para>This lived in three places — a private copy in <c>Scrinia.Tests/StoreBoundaryTests.cs</c>
/// and <c>Scrinia.Merge.Tests/GitMergeHarness.cs</c>, plus this shared helper. The former now
/// delegates here. <c>Scrinia.Merge.Tests</c> keeps its copy deliberately: it
/// does not reference this project, and a ProjectReference for six lines costs more than the
/// duplication does.</para>
/// </summary>
public static class RepoPaths
{
    // Walking up to Scrinia.sln is a File.Exists per ancestor directory; this is now the
    // shared entry point for three test projects, so the walk is done once per process and
    // cached rather than repeated on every Root()/Combine() call.
    private static readonly Lazy<string> CachedRoot = new(FindRoot);

    /// <summary>The repository root — the nearest ancestor of the test output directory
    /// containing <c>Scrinia.sln</c>.</summary>
    public static string Root() => CachedRoot.Value;

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Scrinia.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            $"Could not find Scrinia.sln walking up from {AppContext.BaseDirectory}.");
    }

    /// <summary>Combines <paramref name="relativeSegments"/> onto <see cref="Root"/>.</summary>
    public static string Combine(params string[] relativeSegments) =>
        Path.Combine([Root(), .. relativeSegments]);
}
