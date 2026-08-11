using System.Diagnostics;

namespace Scrinia.Merge.Tests;

/// <summary>
/// A throwaway git repository wired to the built scri-merge binary. Assertions run against
/// what `git merge` produces rather than a direct Handle() call: git passes extensionless
/// temp files, which is the one input shape a direct call never reproduces.
/// </summary>
internal sealed class GitMergeHarness : IDisposable
{
    public string RepoDir { get; }
    private readonly string _driverPath;

    public GitMergeHarness()
    {
        RepoDir = Path.Combine(Path.GetTempPath(), $"scrinia-gitmerge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RepoDir);
        _driverPath = LocateDriver();

        Git("init -q .");
        Git("config user.email test@scrinia.invalid");
        Git("config user.name scrinia-test");
        Git("config commit.gpgsign false");
        // Line-ending rewriting would alter artifact bytes between commit and checkout and
        // break content assertions.
        Git("config core.autocrlf false");
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to the repo root — the directory
    /// containing Scrinia.sln.
    /// </summary>
    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Scrinia.sln")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException(
                $"Could not find Scrinia.sln walking up from {AppContext.BaseDirectory}.");
        return dir.FullName;
    }

    /// <summary>Absolute path to the built scri-merge executable.</summary>
    internal static string LocateDriver()
    {
        string repoRoot = FindRepoRoot();
        string bin = Path.Combine(repoRoot, "src", "Scrinia.Merge", "bin");
        if (!Directory.Exists(bin))
            throw new InvalidOperationException($"{bin} does not exist. Build Scrinia.sln first.");

        // Select by the configuration the TEST ASSEMBLY was built in, not by mtime. A stale
        // Release build sitting next to a fresh Debug one would otherwise get picked by
        // OrderByDescending(GetLastWriteTimeUtc) whenever it happens to be newer, and the
        // resulting failure would read as a driver bug rather than a harness bug.
#if DEBUG
        const string Configuration = "Debug";
#else
        const string Configuration = "Release";
#endif
        string configSegment = Path.DirectorySeparatorChar + Configuration + Path.DirectorySeparatorChar;

        var allCandidates = Directory.EnumerateFiles(bin, "scri-merge*", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p) is "scri-merge.exe" or "scri-merge")
            .ToList();

        var configCandidates = allCandidates
            .Where(p => p.Contains(configSegment, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // A local `dotnet publish` of the AOT driver leaves extra Release candidates alongside
        // the plain build output: bin/Release/.../native/ (AOT native image) and
        // bin/Release/.../publish/ (publish staging dir). Neither is what `dotnet build`/`dotnet
        // test` produces, so exclude them by path SEGMENT (not substring — "publish" must not
        // match a hypothetical "unpublished" directory) rather than let
        // Directory.EnumerateFiles's unspecified order pick one arbitrarily. Do not reinstate
        // these as valid candidates: an ambiguous harness is exactly the defect this method
        // exists to prevent.
        var preferredCandidates = configCandidates
            .Where(p => !p.Split(Path.DirectorySeparatorChar)
                .Any(segment => segment.Equals("native", StringComparison.OrdinalIgnoreCase)
                             || segment.Equals("publish", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (preferredCandidates.Count == 1)
            return preferredCandidates[0];

        if (preferredCandidates.Count > 1)
            throw new InvalidOperationException(
                $"Ambiguous scri-merge executable for configuration '{Configuration}' under {bin} — " +
                $"{preferredCandidates.Count} candidates found and none can be preferred over another: " +
                $"{string.Join(", ", preferredCandidates)}. " +
                "Delete the stale build outputs so exactly one remains.");

        throw new InvalidOperationException(
            $"No scri-merge executable built for configuration '{Configuration}' under {bin}. " +
            $"Searched: {(allCandidates.Count == 0 ? "(no scri-merge executables found at all)" : string.Join(", ", allCandidates))}. " +
            "Build Scrinia.sln in this configuration first.");
    }

    /// <summary>
    /// Registers the built driver for one kind. Git runs driver commands through sh even on
    /// Windows, so the path is forward-slashed and single-quoted: a backslash would be read
    /// as an escape character.
    /// </summary>
    public void RegisterDriver(string kind, bool passPathname = true)
    {
        string p = _driverPath.Replace('\\', '/');
        string placeholders = passPathname ? "%O %A %B %P" : "%O %A %B";
        Git($"config merge.scrinia-{kind}.driver \"'{p}' {kind} {placeholders}\"");
    }

    /// <summary>Registers an arbitrary sh command as a driver, for probing git's own dispatch.</summary>
    public void RegisterRawDriver(string kind, string command) =>
        Git($"config merge.scrinia-{kind}.driver \"{command}\"");

    public void WriteFile(string relativePath, string content)
    {
        string full = Path.Combine(RepoDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public string ReadFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoDir, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public bool FileExists(string relativePath) =>
        File.Exists(Path.Combine(RepoDir, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public void CommitAll(string message)
    {
        Git("add -A");
        Git($"commit -q -m \"{message}\"");
    }

    public (int ExitCode, string StdOut, string StdErr) Git(string arguments, bool throwOnFailure = true)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = RepoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start git. Is it on PATH?");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (throwOnFailure && proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {arguments} failed ({proc.ExitCode}).\nstdout: {stdout}\nstderr: {stderr}");
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>Runs `git merge` without throwing — a conflict is an expected outcome here.</summary>
    public (int ExitCode, string StdOut, string StdErr) Merge(string branch) =>
        Git($"merge {branch}", throwOnFailure: false);

    /// <summary>Porcelain status code for one path, e.g. "UU" for an unmerged file.</summary>
    public string StatusCode(string relativePath)
    {
        var (_, stdout, _) = Git($"status --porcelain -- {relativePath}", throwOnFailure: false);
        string? line = stdout.Split('\n')
            .FirstOrDefault(l => l.Contains(Path.GetFileName(relativePath), StringComparison.Ordinal));
        return line is { Length: >= 2 } ? line[..2] : "";
    }

    /// <summary>
    /// Commits <paramref name="baseContent"/>, branches "other" carrying
    /// <paramref name="theirsContent"/>, and leaves the default branch carrying
    /// <paramref name="oursContent"/>. A null side commits an unrelated file instead, so the
    /// branch still diverges without touching the artifact.
    /// </summary>
    public void SeedThreeWay(string relativePath, string baseContent, string? oursContent, string? theirsContent)
    {
        WriteFile(relativePath, baseContent);
        CommitAll("base");
        string defaultBranch = Git("rev-parse --abbrev-ref HEAD").StdOut.Trim();

        Git("checkout -q -b other");
        if (theirsContent is not null) WriteFile(relativePath, theirsContent);
        else WriteFile("unrelated-theirs.txt", "x");
        CommitAll("theirs");

        Git($"checkout -q {defaultBranch}");
        if (oursContent is not null) WriteFile(relativePath, oursContent);
        else WriteFile("unrelated-ours.txt", "x");
        CommitAll("ours");
    }

    public void Dispose()
    {
        try { ForceDelete(RepoDir); } catch { }
    }

    // git marks objects under .git read-only; a plain recursive delete fails on Windows.
    private static void ForceDelete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }
}
