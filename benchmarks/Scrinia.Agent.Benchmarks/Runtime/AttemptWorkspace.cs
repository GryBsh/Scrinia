using System.Text.Json;

namespace Scrinia.Agent.Benchmarks;

public sealed class AttemptWorkspace
{
    public AttemptPaths Create(string runRoot, int caseOrdinal, int attemptNumber, ScriniaSettings settings)
    {
        if (caseOrdinal < 1 || attemptNumber < 1) throw new ArgumentOutOfRangeException(nameof(caseOrdinal));
        runRoot = Path.GetFullPath(runRoot);
        PathSafety.RequireNoLinks(runRoot);
        PathSafety.RequireSeparate(runRoot, settings.PayloadDirectory);
        ConfigurationReader.ValidateWorkspaceConfig(settings.WorkspaceConfig);
        if (settings.HomeSeedDirectory is { } seed)
        {
            PathSafety.RequireSeparate(runRoot, seed);
            ValidateSeed(seed);
        }
        ConfigurationReader.ValidateRequestedAssets(settings);
        var parent = Path.Combine(runRoot, "cases", caseOrdinal.ToString("D6"), "attempts");
        Directory.CreateDirectory(parent);
        PathSafety.RequireNoLinks(parent);
        var root = Path.Combine(parent, attemptNumber.ToString("D6"));
        // A create-new sibling reservation prevents concurrent creators from sharing an attempt.
        // Retain it even on initialization failure: retries must allocate a fresh attempt number.
        using var reservation = new FileStream(root + ".reserved", FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Attempt already exists.");
        var result = new AttemptPaths(root, Path.Combine(root, "workspace"), Path.Combine(root, "home"), Path.Combine(root, "evidence"));
        Directory.CreateDirectory(result.Workspace);
        Directory.CreateDirectory(result.Home);
        Directory.CreateDirectory(result.Evidence);
        if (settings.HomeSeedDirectory is { } source)
        {
            var before = RunFingerprint.HashTree(source);
            CopyAssets(source, result.Home);
            if (before != RunFingerprint.HashTree(result.Home) || before != RunFingerprint.HashTree(source))
                throw new IOException("Seed assets changed while preparing the attempt.");
        }
        var configDirectory = Directory.CreateDirectory(Path.Combine(result.Workspace, ".scrinia")).FullName;
        var config = new Dictionary<string, string>(settings.WorkspaceConfig, StringComparer.OrdinalIgnoreCase)
        {
            ["Scrinia:Workspace:Tracking"] = "local", ["Scrinia:Shared:Enabled"] = "false"
        };
        File.WriteAllText(Path.Combine(configDirectory, "config.json"), JsonSerializer.Serialize(config, ConfigurationJsonContext.Default.DictionaryStringString));
        return result;
    }
    public Dictionary<string, string?> CreateEnvironment(AttemptPaths paths)
    {
        var environment = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var key in new[] { "PATH", "PATHEXT", "SYSTEMROOT", "WINDIR", "SYSTEMDRIVE", "PROGRAMFILES", "PROCESSOR_ARCHITECTURE" })
            if (Environment.GetEnvironmentVariable(key) is { } value) environment[key] = value;
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (dotnetRoot is not null && Path.IsPathFullyQualified(dotnetRoot) &&
            File.Exists(Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")))
        {
            PathSafety.RequireNoLinks(dotnetRoot);
            environment["DOTNET_ROOT"] = dotnetRoot;
        }
        foreach (var key in new[] { "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "TMPDIR", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME" })
        {
            var directory = Path.Combine(paths.Root, "environment", key.ToLowerInvariant());
            PathSafety.RequireNoLinks(directory);
            Directory.CreateDirectory(directory);
            environment[key] = directory;
        }
        environment["SCRINIA_HOME"] = paths.Home;
        return environment;
    }

    internal static void ValidateSeed(string seed)
    {
        if (!Directory.Exists(seed)) throw new InvalidDataException("Requested home seed does not exist.");
        PathSafety.RequireNoLinks(seed);
        foreach (var entry in Directory.EnumerateFileSystemEntries(seed))
        {
            PathSafety.RequireNoLinks(entry);
            if (Path.GetFileName(entry) is not ("models" or "plugins") || !Directory.Exists(entry))
                throw new InvalidDataException("Home seed may contain only models/ and plugins/ assets.");
        }
        _ = RunFingerprint.HashTree(seed);
    }

    private static void CopyAssets(string source, string target)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            PathSafety.RequireNoLinks(entry);
            var destination = Path.Combine(target, Path.GetFileName(entry));
            if (!PathSafety.Contains(target, destination)) throw new InvalidDataException("Seed asset escaped its destination.");
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(destination);
                CopyAssets(entry, destination);
            }
            else File.Copy(entry, destination, overwrite: false);
        }
    }
}
