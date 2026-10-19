namespace Scrinia.Agent.Benchmarks;

public static class RuntimeEnvironment
{
    public static Dictionary<string, string?> Create(AttemptPaths paths)
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

}
