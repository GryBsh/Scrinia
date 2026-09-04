using System.Collections.Concurrent;
using System.Text.Json;

namespace Scrinia.Core.Configuration;

public interface IWorkspaceConfig : ITrackingPolicyStore
{
    string? GetValue(string key);
    void SetValue(string key, string value);
    void UnsetValue(string key);
    IReadOnlyList<string> GetEnabledBases();
    void Save();
}

public sealed class WorkspaceConfig : IWorkspaceConfig
{
    public const string TrackingKey = "Scrinia:Workspace:Tracking";
    public const string EnabledKey = "Scrinia:Shared:Enabled";
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _workspaceRoot;
    private readonly Dictionary<string, string> _values;

    public WorkspaceConfig(Dictionary<string, string>? values = null, string? workspaceRoot = null)
    {
        _values = new Dictionary<string, string>(values ?? [], StringComparer.OrdinalIgnoreCase);
        _workspaceRoot = workspaceRoot;
    }

    public static WorkspaceConfig Load(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
        return new WorkspaceConfig(new(Cached(workspaceRoot), StringComparer.OrdinalIgnoreCase), workspaceRoot);
    }

    public static Dictionary<string, string> LoadValues(string workspaceRoot) =>
        new(Load(workspaceRoot)._values, StringComparer.OrdinalIgnoreCase);

    public static void Save(string workspaceRoot, Dictionary<string, string> values) =>
        new WorkspaceConfig(values, workspaceRoot).Save();

    public string? GetValue(string key) => _values.TryGetValue(key, out string? value) ? value : null;

    public void SetValue(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        _values[key] = value;
    }

    public void UnsetValue(string key) => _values.Remove(key);
    public IReadOnlyList<string> GetEnabledBases() => ParseEnabled(GetValue(EnabledKey));
    public TrackingPolicy GetTrackingPolicy() => ParseTracking(GetValue(TrackingKey));

    public void Save()
    {
        if (string.IsNullOrEmpty(_workspaceRoot))
            throw new InvalidOperationException("A workspace root is required to save configuration.");
        string directory = Path.Combine(_workspaceRoot, ".scrinia");
        string path = Path.Combine(directory, "config.json");
        Directory.CreateDirectory(directory);
        using (FileLock.AcquireExclusive(path + ".lock"))
        {
            string json = JsonSerializer.Serialize(_values, ConfigJsonContext.Default.DictionaryStringString);
            DurableFile.AtomicWriteAllText(path, json);
        }
        Cache[_workspaceRoot] = new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> ParseEnabled(string? value)
    {
        if (value is null)
            return [];
        if (value.Length == 0)
            throw Invalid("Enabled must contain one or more canonical base names.");
        string[] names = value.Split(';', StringSplitOptions.None);
        if (names.Any(name => !IsCanonicalName(name)) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw Invalid("Enabled must contain unique, canonical, semicolon-separated base names.");
        return names;
    }

    public static TrackingPolicy ParseTracking(string? value) => value switch
    {
        null => TrackingPolicy.Unset,
        "local" => TrackingPolicy.Local,
        "project-git" => TrackingPolicy.ProjectGit,
        _ => throw Invalid("Tracking must be exactly 'local' or 'project-git.'"),
    };

    private static Dictionary<string, string> Cached(string root) => Cache.GetOrAdd(root, static value => ReadValues(value));

    private static Dictionary<string, string> ReadValues(string root)
    {
        string path = Path.Combine(root, ".scrinia", "config.json");
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Dictionary<string, string>? values = JsonSerializer.Deserialize(File.ReadAllBytes(path), ConfigJsonContext.Default.DictionaryStringString);
            var result = new Dictionary<string, string>(values ?? [], StringComparer.OrdinalIgnoreCase);
            ParseEnabled(result.GetValueOrDefault(EnabledKey));
            ParseTracking(result.GetValueOrDefault(TrackingKey));
            return result;
        }
        catch (JsonException exception)
        {
            throw Invalid("The workspace config contains invalid JSON.", exception);
        }
    }

    private static bool IsCanonicalName(string value) => value.Length is >= 1 and <= 32 && value[0] is >= 'a' and <= 'z' &&
        value[1..].All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static ScriniaConfigurationException Invalid(string message, Exception? inner = null) =>
        inner is null ? new ScriniaConfigurationException(message) : new ScriniaConfigurationException(message, inner);
}
