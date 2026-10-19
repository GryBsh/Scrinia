using System.Text.Json;

namespace Scrinia.Commands;

internal static class EmbeddingsBundlePolicy
{
    private static readonly Dictionary<string, IReadOnlyList<string>> Files = Load();

    internal static IReadOnlyList<string> GetFileNames(string rid) =>
        Files.TryGetValue(rid, out var names) ? names : throw new EmbeddingsBundleException(
            EmbeddingsInstallStatus.IncompatibleIdentity, "Unsupported embeddings bundle RID.");

    private static Dictionary<string, IReadOnlyList<string>> Load()
    {
        using Stream stream = typeof(EmbeddingsBundlePolicy).Assembly.GetManifestResourceStream("Scrinia.EmbeddingsPayloadPolicy.json")
            ?? throw new InvalidOperationException("Embedded embeddings payload policy is missing.");
        using JsonDocument document = JsonDocument.Parse(stream);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (JsonProperty platform in document.RootElement.EnumerateObject())
        {
            var names = new List<string> { platform.Value.GetProperty("executable").GetString()! };
            names.AddRange(platform.Value.GetProperty("nativeFiles").EnumerateArray().Select(file => file.GetProperty("name").GetString()!));
            result.Add(platform.Name, names.AsReadOnly());
        }
        return result;
    }
}
