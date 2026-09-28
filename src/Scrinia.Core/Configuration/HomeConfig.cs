using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrinia.Core.Configuration;

public sealed class HomeConfig
{
    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("sharedBases")]
    public required SharedBasesMap SharedBases { get; init; }
}

public sealed class SharedBaseEntry
{
    [JsonPropertyName("root")]
    public required string Root { get; init; }

    [JsonIgnore]
    public string Name { get; init; } = "";

    [JsonIgnore]
    public TrackingPolicy Tracking { get; init; } = TrackingPolicy.ProjectGit;

    internal SharedBaseEntry WithIdentity(string name) => new() { Root = Root, Name = name, Tracking = Tracking };
}

public sealed class SharedBasesMap : Dictionary<string, SharedBaseEntry>
{
    public SharedBasesMap() : base(StringComparer.Ordinal) { }
    public SharedBasesMap(IDictionary<string, SharedBaseEntry> entries) : base(entries, StringComparer.Ordinal) { }
}

public interface IHomeConfigStore
{
    HomeConfig Load(string homeDirectory);
    void Save(string homeDirectory, HomeConfig config);
}

public sealed class HomeConfigStore : IHomeConfigStore
{
    private const int CurrentSchemaVersion = 1;
    private const string ConfigFileName = "config.json";

    public HomeConfig Load(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(homeDirectory);
        string configPath = Path.Combine(homeDirectory, ConfigFileName);
        if (!File.Exists(configPath))
            return EmptyConfig();

        try
        {
            byte[] bytes = File.ReadAllBytes(configPath);
            ValidateDocument(bytes);
            HomeConfig? config = JsonSerializer.Deserialize(bytes, HomeConfigJsonContext.Default.HomeConfig);
            return config ?? throw Invalid("The home config must be a JSON object.");
        }
        catch (ScriniaConfigurationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Invalid("The home config contains invalid JSON.", exception);
        }
        catch (IOException exception)
        {
            throw new ScriniaConfigurationException("The home config could not be read.", exception);
        }
    }

    public void Save(string homeDirectory, HomeConfig config)
    {
        ArgumentException.ThrowIfNullOrEmpty(homeDirectory);
        ArgumentNullException.ThrowIfNull(config);
        ValidateConfig(config);

        Directory.CreateDirectory(homeDirectory);
        string configPath = Path.Combine(homeDirectory, ConfigFileName);
        string lockPath = ConfigurationLock.GetPath(configPath);
        using (FileLock.AcquireExclusive(lockPath))
        {
            SaveLocked(configPath, config);
        }
    }

    internal void SaveLocked(string configPath, HomeConfig config)
    {
        string json = JsonSerializer.Serialize(config, HomeConfigJsonContext.Default.HomeConfig);
        try { DurableFile.AtomicWriteAllText(configPath, json); }
        catch (DurableWriteException exception)
        {
            throw new ScriniaConfigurationException("The home config could not be written atomically.", exception);
        }
    }

    private static HomeConfig EmptyConfig() => new() { SchemaVersion = CurrentSchemaVersion, SharedBases = new SharedBasesMap() };

    private static void ValidateDocument(byte[] bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Invalid("The home config must be a JSON object.");

            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!properties.Add(property.Name) || property.Name is not ("schemaVersion" or "sharedBases"))
                    throw Invalid("The home config has duplicate or unknown top-level properties.");
            }

            if (!properties.Contains("schemaVersion") || !properties.Contains("sharedBases"))
                throw Invalid("The home config requires schemaVersion and sharedBases.");
            if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number ||
                !root.GetProperty("schemaVersion").TryGetInt32(out int schemaVersion) ||
                schemaVersion != CurrentSchemaVersion)
                throw Invalid("The home config schemaVersion must be 1.");
            if (root.GetProperty("sharedBases").ValueKind != JsonValueKind.Object)
                throw Invalid("The home config sharedBases must be an object.");

            var baseNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty baseProperty in root.GetProperty("sharedBases").EnumerateObject())
            {
                if (!baseNames.Add(baseProperty.Name) || !SharedBaseRegistry.IsCanonicalName(baseProperty.Name) || baseProperty.Value.ValueKind != JsonValueKind.Object)
                    throw Invalid("The home config contains an invalid shared base.");
                var fields = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty field in baseProperty.Value.EnumerateObject())
                {
                    if (!fields.Add(field.Name) || field.Name != "root")
                        throw Invalid("A shared base contains duplicate or unknown properties.");
                }
                if (!fields.Contains("root") || baseProperty.Value.GetProperty("root").ValueKind != JsonValueKind.String)
                    throw Invalid("A shared base requires a root.");
                string? rootPath = baseProperty.Value.GetProperty("root").GetString();
                if (!IsValidBaseRoot(rootPath))
                    throw Invalid("A shared base root must be an absolute path outside .scrinia.");
            }
        }
        catch (JsonException exception)
        {
            throw Invalid("The home config contains invalid JSON.", exception);
        }
    }

    private static void ValidateConfig(HomeConfig config)
    {
        if (config.SchemaVersion != CurrentSchemaVersion || config.SharedBases is null)
            throw Invalid("The home config schema is invalid.");
        foreach (var pair in config.SharedBases)
        {
            string name = pair.Key;
            SharedBaseEntry entry = pair.Value;
            if (!SharedBaseRegistry.IsCanonicalName(name) || entry is null || !IsValidBaseRoot(entry.Root))
                throw Invalid("The home config contains an invalid shared base.");
        }
    }

    private static bool IsValidBaseRoot(string? root)
    {
        if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root))
            return false;

        string withoutTrailingSeparators = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.Equals(Path.GetFileName(withoutTrailingSeparators), ".scrinia", StringComparison.OrdinalIgnoreCase);
    }

    private static ScriniaConfigurationException Invalid(string message, Exception? innerException = null) =>
        innerException is null ? new ScriniaConfigurationException(message) : new ScriniaConfigurationException(message, innerException);
}
