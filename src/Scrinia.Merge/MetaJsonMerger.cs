using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrinia.Merge;

public sealed record MetaEntry(
    string? Name,
    string? Uri,
    int OriginalBytes,
    int ChunkCount,
    string? CreatedAt,
    string? Description,
    string[]? Keywords,
    string? UpdatedAt
);

[JsonSerializable(typeof(MetaEntry))]
internal sealed partial class MetaEntryJsonContext : JsonSerializerContext { }

public static class MetaJsonMerger
{
    public enum MergeResult { Resolved, Conflict }

    public static MergeResult Merge(string ancestorPath, string oursPath, string theirsPath, MergeConfig config)
    {
        var ours = JsonSerializer.Deserialize(File.ReadAllText(oursPath), MetaEntryJsonContext.Default.MetaEntry);
        var theirs = JsonSerializer.Deserialize(File.ReadAllText(theirsPath), MetaEntryJsonContext.Default.MetaEntry);

        double jaccard = KeywordJaccard.Compute(ours?.Keywords, theirs?.Keywords);

        if (jaccard >= config.JaccardThreshold)
        {
            // Union keywords
            var keywords = new HashSet<string>(ours?.Keywords ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var kw in theirs?.Keywords ?? [])
                keywords.Add(kw);

            // Take latest UpdatedAt
            string? updatedAt = CompareTimestamps(ours?.UpdatedAt, theirs?.UpdatedAt) > 0
                ? ours?.UpdatedAt : theirs?.UpdatedAt;

            // Keep earliest CreatedAt
            string? createdAt = CompareTimestamps(ours?.CreatedAt, theirs?.CreatedAt) < 0
                ? ours?.CreatedAt : theirs?.CreatedAt;

            // Merge: take ours as base, apply union keywords + latest timestamp
            var merged = ours! with
            {
                Keywords = keywords.Order().ToArray(),
                UpdatedAt = updatedAt,
                CreatedAt = createdAt
            };

            // Write merged result back to ours path (git convention: %A is the output)
            string json = JsonSerializer.Serialize(merged, MetaEntryJsonContext.Default.MetaEntry);
            File.WriteAllText(oursPath, json);
            return MergeResult.Resolved;
        }

        return MergeResult.Conflict;
    }

    private static int CompareTimestamps(string? a, string? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        return string.Compare(a, b, StringComparison.Ordinal);
    }
}
