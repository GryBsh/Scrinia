using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrinia.Merge;

public sealed record MergeConfig(
    double JaccardThreshold = 0.7,
    string Resolver = "none",
    string? ResolverCommand = null,
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

[JsonSerializable(typeof(MergeConfig))]
internal partial class MergeConfigJsonContext : JsonSerializerContext { }
