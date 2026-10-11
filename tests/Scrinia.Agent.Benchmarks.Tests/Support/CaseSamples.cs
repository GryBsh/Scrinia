using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scrinia.Agent.Benchmarks.Tests;

internal static class CaseSamples
{
    public static RunConfiguration Configuration(string root)
    {
        var payload = Path.Combine(root, "payload");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "scri.exe"), "test payload identity");
        var dataset = Path.Combine(root, "dataset.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures/minimal-longmemeval.json"), dataset, true);
        var model = new ModelSettings(new Uri("http://localhost:1234/v1"), "offline-fixture", "BENCH_AGENT_API_KEY", 0, 64);
        return new(1, dataset, [], new(payload, Path.Combine(payload, "scri.exe"), [], [], null),
            model, model with { ApiKeyEnvironmentVariable = "BENCH_JUDGE_API_KEY" }, new(16, 60, 600, 2), Path.Combine(root, "runs"));
    }

    public static JsonObject ConfigurationJson(string root) => JsonNode.Parse(
        JsonSerializer.Serialize(Configuration(root), ConfigurationJsonContext.Default.RunConfiguration))!.AsObject();

    public static async Task<DatasetSnapshot> Dataset() => await new LongMemEvalReader().LoadAsync(
        Path.Combine(AppContext.BaseDirectory, "Fixtures/minimal-longmemeval.json"), [], CancellationToken.None);
}
