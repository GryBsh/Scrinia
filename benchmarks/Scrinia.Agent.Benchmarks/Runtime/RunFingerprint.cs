using System.Security.Cryptography;
using System.Text.Json;

namespace Scrinia.Agent.Benchmarks;

public static class RunFingerprint
{
    public static string Compute(RunConfiguration config, DatasetSnapshot dataset,
        IReadOnlyDictionary<string, string> contentHashes)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", config.SchemaVersion);
            writer.WriteString("dataset", dataset.Sha256);
            writer.WriteStartArray("caseIds");
            foreach (var id in config.CaseIds.Length == 0 ? dataset.Cases.Select(c => c.Evaluation.QuestionId) : config.CaseIds)
                writer.WriteStringValue(id);
            writer.WriteEndArray();
            writer.WritePropertyName("agent");
            JsonSerializer.Serialize(writer, config.Agent, ConfigurationJsonContext.Default.ModelSettings);
            writer.WritePropertyName("judge");
            JsonSerializer.Serialize(writer, config.Judge, ConfigurationJsonContext.Default.ModelSettings);
            writer.WritePropertyName("limits");
            JsonSerializer.Serialize(writer, config.Limits, ConfigurationJsonContext.Default.RunLimits);
            writer.WriteString("executable", Path.GetFileName(config.Scrinia.Executable));
            writer.WriteStartArray("prefixArguments");
            foreach (var argument in config.Scrinia.PrefixArguments) writer.WriteStringValue(Path.GetFileName(argument));
            writer.WriteEndArray();
            WriteSorted(writer, "workspaceConfig", config.Scrinia.WorkspaceConfig);
            WriteSorted(writer, "contentHashes", contentHashes);
            writer.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }

    public static string HashTree(string directory)
    {
        PathSafety.RequireNoLinks(directory);
        if (!Directory.Exists(directory)) throw new InvalidDataException("Asset directory does not exist.");
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        Visit(directory);
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) WriteSorted(writer, null, files);
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));

        void Visit(string path)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                PathSafety.RequireNoLinks(entry);
                if (Directory.Exists(entry)) Visit(entry);
                else
                {
                    using var stream = File.OpenRead(entry);
                    files.Add(Path.GetRelativePath(directory, entry).Replace('\\', '/'), Convert.ToHexStringLower(SHA256.HashData(stream)));
                }
            }
        }
    }

    private static void WriteSorted(Utf8JsonWriter writer, string? name, IEnumerable<KeyValuePair<string, string>> values)
    {
        if (name is not null) writer.WritePropertyName(name);
        writer.WriteStartObject();
        foreach (var (key, value) in values.OrderBy(p => p.Key, StringComparer.Ordinal)) writer.WriteString(key, value);
        writer.WriteEndObject();
    }
}
