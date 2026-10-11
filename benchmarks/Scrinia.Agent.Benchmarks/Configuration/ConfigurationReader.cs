using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scrinia.Agent.Benchmarks;

public sealed class ConfigurationReader
{
    public RunConfiguration Read(string configurationPath)
    {
        var path = Path.GetFullPath(configurationPath);
        PathSafety.RequireNoLinks(path);
        var options = new JsonSerializerOptions(ConfigurationJsonContext.Default.Options)
        {
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true
        };
        RunConfiguration config;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            RejectDuplicateProperties(document.RootElement);
            config = document.RootElement.Deserialize(new ConfigurationJsonContext(options).RunConfiguration)
                ?? throw InputDiagnostics.Invalid("Configuration must be an object.");
        }
        catch (JsonException e)
        {
            // Do not echo raw values: malformed configuration can contain credentials.
            throw InputDiagnostics.Invalid($"Invalid configuration JSON at line {e.LineNumber}, byte {e.BytePositionInLine}.");
        }
        if (config.SchemaVersion != 1) throw InputDiagnostics.Invalid("Unsupported schemaVersion; expected 1.");
        ValidateModel(config.Agent, "agent");
        ValidateModel(config.Judge, "judge");
        var limits = config.Limits;
        if (limits is null || limits.MaxModelTurnsPerEpisode is < 1 or > 1024 ||
            limits.EpisodeSeconds is < 1 or > 86400 || limits.CaseSeconds is < 1 or > 604800 ||
            limits.MaxModelAttempts is < 1 or > 5 || limits.CaseSeconds < limits.EpisodeSeconds)
            throw InputDiagnostics.Invalid("Invalid limits: all bounds must be positive and caseSeconds must cover episodeSeconds.");
        if (config.CaseIds is null || config.CaseIds.Any(string.IsNullOrWhiteSpace) ||
            config.CaseIds.Distinct(StringComparer.Ordinal).Count() != config.CaseIds.Length)
            throw InputDiagnostics.Invalid("caseIds must contain distinct nonempty IDs.");
        var basis = Path.GetDirectoryName(path)!;
        var dataset = Resolve(config.Dataset, basis, "dataset");
        if (!File.Exists(dataset)) throw InputDiagnostics.Invalid("Dataset file does not exist.");
        var output = Resolve(config.OutputDirectory, basis, "outputDirectory");
        var settings = config.Scrinia ?? throw InputDiagnostics.Invalid("scrinia is required.");
        var payload = Resolve(settings.PayloadDirectory, basis, "scrinia.payloadDirectory");
        if (!Directory.Exists(payload)) throw InputDiagnostics.Invalid("Product payload directory does not exist.");
        var executable = Resolve(settings.Executable, payload, "scrinia.executable");
        if (!File.Exists(executable)) throw InputDiagnostics.Invalid("Scrinia executable or dotnet host does not exist.");
        if (settings.PrefixArguments is null) throw InputDiagnostics.Invalid("prefixArguments is required.");
        string[] arguments = settings.PrefixArguments.Select(p => Resolve(p, basis, "scrinia.prefixArguments")).ToArray();
        if (arguments.Length == 0)
        {
            if (!PathSafety.Contains(payload, executable) ||
                Path.GetFileName(executable) is not ("scri" or "scri.exe"))
                throw InputDiagnostics.Invalid("Use scri from the payload or an absolute dotnet host with the payload scri.dll.");
        }
        else if (arguments.Length != 1 || !Path.IsPathFullyQualified(settings.Executable) ||
            Path.GetFileNameWithoutExtension(executable) != "dotnet" || Path.GetFileName(arguments[0]) != "scri.dll" ||
            !PathSafety.Contains(payload, arguments[0]) || !File.Exists(arguments[0]))
            throw InputDiagnostics.Invalid("prefixArguments permits only the payload scri.dll with an absolute dotnet host.");
        var seed = settings.HomeSeedDirectory is null ? null : Resolve(settings.HomeSeedDirectory, basis, "scrinia.homeSeedDirectory");
        if (seed is not null) AttemptWorkspace.ValidateSeed(seed);
        ValidateWorkspaceConfig(settings.WorkspaceConfig);
        ValidateRequestedAssets(settings with { HomeSeedDirectory = seed });
        foreach (var input in new[] { dataset, payload, seed, executable }.OfType<string>())
        {
            PathSafety.RequireSeparate(output, input);
            PathSafety.RequireNoLinks(input);
        }
        PathSafety.RequireNoLinks(output);
        if (seed is not null) PathSafety.RequireSeparate(payload, seed);
        return config with { Dataset = dataset, OutputDirectory = output,
            Scrinia = settings with { PayloadDirectory = payload, Executable = executable, PrefixArguments = arguments, HomeSeedDirectory = seed } };
    }

    internal static void ValidateWorkspaceConfig(Dictionary<string, string> values)
    {
        if (values is null) throw InputDiagnostics.Invalid("workspaceConfig is required.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null || !seen.Add(key))
                throw InputDiagnostics.Invalid("Workspace keys must be distinct and values must be strings.");
            // Store routing and credentials are controlled by isolation, never by workspace overrides.
            var normalized = key.Replace("_", "", StringComparison.Ordinal).Replace(":", "", StringComparison.Ordinal).ToLowerInvariant();
            if (normalized.Contains("shared") || normalized.Contains("tracking") || normalized.Contains("store") ||
                normalized.Contains("path") || normalized.Contains("directory") || normalized.Contains("apikey") ||
                normalized.Contains("secret") || normalized.Contains("password") || normalized.Contains("credential") ||
                normalized.Contains("authorization") || normalized.Contains("token") || normalized.Contains("endpoint"))
                throw InputDiagnostics.Invalid("Workspace configuration cannot override isolation or contain credentials.");
        }
    }

    internal static void ValidateRequestedAssets(ScriniaSettings settings)
    {
        var values = new Dictionary<string, string>(settings.WorkspaceConfig, StringComparer.OrdinalIgnoreCase);
        if (!values.ContainsKey("plugins:embeddings") && !values.ContainsKey("plugins:embeddings:version")) return;
        var name = values.GetValueOrDefault("plugins:embeddings", "scri-plugin-embeddings");
        var version = values.GetValueOrDefault("plugins:embeddings:version");
        if (!Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant) ||
            (version is not null && !Regex.IsMatch(version, "^[0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)))
            throw InputDiagnostics.Invalid("Plugin names and versions must be single directory names.");
        if (settings.HomeSeedDirectory is null) throw InputDiagnostics.Invalid("Requested plugin requires homeSeedDirectory assets.");
        var pluginRoot = Path.Combine(settings.HomeSeedDirectory, "plugins", name);
        if (version is not null) pluginRoot = Path.Combine(pluginRoot, version);
        if (!Directory.Exists(pluginRoot) || !Directory.EnumerateFiles(pluginRoot,
            name + (OperatingSystem.IsWindows() ? ".exe" : ""), SearchOption.AllDirectories).Any())
            throw InputDiagnostics.Invalid("Requested plugin executable is missing from seed assets.");
    }

    private static void ValidateModel(ModelSettings model, string name)
    {
        if (model is null || model.Endpoint is null || !model.Endpoint.IsAbsoluteUri ||
            model.Endpoint.Scheme is not ("http" or "https") || model.Endpoint.UserInfo.Length != 0 ||
            model.Endpoint.Query.Length != 0 || model.Endpoint.Fragment.Length != 0 ||
            string.IsNullOrWhiteSpace(model.Model) ||
            !Regex.IsMatch(model.ApiKeyEnvironmentVariable ?? "", "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant) ||
            !float.IsFinite(model.Temperature) || model.Temperature is < 0 or > 2 || model.MaxOutputTokens is < 1 or > 131072)
            throw InputDiagnostics.Invalid($"Invalid {name} model settings.");
    }

    private static string Resolve(string value, string basis, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) throw InputDiagnostics.Invalid($"{field} must be nonempty.");
        try { return Path.GetFullPath(value, basis); }
        catch (ArgumentException) { throw InputDiagnostics.Invalid($"Invalid path in {field}."); }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw InputDiagnostics.Invalid("Duplicate configuration property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
}

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
