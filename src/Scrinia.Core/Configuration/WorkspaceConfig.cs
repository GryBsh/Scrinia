using System.Collections.Concurrent;
using System.Text.Json;

namespace Scrinia.Core.Configuration;

public interface IWorkspaceConfig : ITrackingPolicyStore
{
    string? GetValue(string key);
    void SetValue(string key, string value);
    void UnsetValue(string key);
    IReadOnlyList<string> GetEnabledBases();
    IReadOnlyList<string> UpdateEnabledBases(Func<IReadOnlyList<string>, IReadOnlyList<string>> mutate);
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

    public static void SetValue(string workspaceRoot, string key, string value) =>
        Mutate(workspaceRoot, values => values[key] = value);

    public static bool UnsetValue(string workspaceRoot, string key)
    {
        bool removed = false;
        Mutate(workspaceRoot, values => removed = values.Remove(key));
        return removed;
    }

    public string? GetValue(string key) => _values.TryGetValue(key, out string? value) ? value : null;

    public void SetValue(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        if (_workspaceRoot is not null)
        {
            SetValue(_workspaceRoot, key, value);
            ReplaceValues(Load(_workspaceRoot)._values);
            return;
        }
        _values[key] = value;
    }

    public void UnsetValue(string key)
    {
        if (_workspaceRoot is null)
        {
            _values.Remove(key);
            return;
        }

        UnsetValue(_workspaceRoot, key);
        ReplaceValues(Load(_workspaceRoot)._values);
    }
    public IReadOnlyList<string> GetEnabledBases() => ParseEnabled(GetValue(EnabledKey));
    public IReadOnlyList<string> UpdateEnabledBases(Func<IReadOnlyList<string>, IReadOnlyList<string>> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        if (_workspaceRoot is null)
        {
            IReadOnlyList<string> next = mutate(GetEnabledBases());
            if (next.Count == 0) _values.Remove(EnabledKey);
            else _values[EnabledKey] = string.Join(';', next);
            return next;
        }

        IReadOnlyList<string> updated = [];
        Mutate(_workspaceRoot, values =>
        {
            IReadOnlyList<string> next = mutate(ParseEnabled(values.GetValueOrDefault(EnabledKey)));
            if (next.Count == 0) values.Remove(EnabledKey);
            else values[EnabledKey] = string.Join(';', next);
            updated = next;
        });
        ReplaceValues(Load(_workspaceRoot)._values);
        return updated;
    }

    public TrackingPolicy GetTrackingPolicy() => ParseTracking(GetValue(TrackingKey));

    public void Save()
    {
        if (string.IsNullOrEmpty(_workspaceRoot))
            throw new InvalidOperationException("A workspace root is required to save configuration.");
        string directory = Path.Combine(_workspaceRoot, ".scrinia");
        string path = Path.Combine(directory, "config.json");
        Directory.CreateDirectory(directory);
        using (FileLock.AcquireExclusive(ConfigurationLock.GetPath(path)))
        {
            string json = JsonSerializer.Serialize(_values, ConfigJsonContext.Default.DictionaryStringString);
            try
            {
                DurableFile.AtomicWriteAllText(path, json);
            }
            catch (DurableWriteException exception)
            {
                throw new ScriniaConfigurationException("The workspace config could not be written atomically.", exception);
            }
        }
        Cache[_workspaceRoot] = new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase);
    }

    private static void Mutate(string workspaceRoot, Action<Dictionary<string, string>> applyMutation)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
        ArgumentNullException.ThrowIfNull(applyMutation);

        string directory = Path.Combine(workspaceRoot, ".scrinia");
        string path = Path.Combine(directory, "config.json");
        Directory.CreateDirectory(directory);
        using (FileLock.AcquireExclusive(ConfigurationLock.GetPath(path)))
        {
            Dictionary<string, string> current = ReadValues(workspaceRoot);
            applyMutation(current);
            string json = JsonSerializer.Serialize(current, ConfigJsonContext.Default.DictionaryStringString);
            try
            {
                DurableFile.AtomicWriteAllText(path, json);
            }
            catch (DurableWriteException exception)
            {
                throw new ScriniaConfigurationException("The workspace config could not be written atomically.", exception);
            }
            Cache[workspaceRoot] = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);
        }
    }

    private void ReplaceValues(Dictionary<string, string> values)
    {
        _values.Clear();
        foreach ((string key, string value) in values)
            _values[key] = value;
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
