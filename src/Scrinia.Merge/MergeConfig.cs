using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrinia.Merge;

public sealed record MergeConfig(
    double JaccardThreshold = 0.7,
    string ConflictDir = "conflict"
)
{
    private static readonly MergeConfig Default = new();

    public static MergeConfig Load(string scriniaDir)
    {
        var configPath = Path.Combine(scriniaDir, "merge.config");
        if (!File.Exists(configPath))
            return Default;

        try
        {
            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize(json, MergeConfigJsonContext.Default.MergeConfig)
                   ?? Default;
        }
        catch
        {
            return Default;
        }
    }
}

// merge.config on disk uses camelCase keys (e.g. "conflictDir"). Without this policy,
// property names mismatch on read and Load silently falls back to every default value.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MergeConfig))]
internal sealed partial class MergeConfigJsonContext : JsonSerializerContext { }
