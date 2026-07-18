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
        var ancestor = File.Exists(ancestorPath)
            ? JsonSerializer.Deserialize(File.ReadAllText(ancestorPath), MetaEntryJsonContext.Default.MetaEntry)
            : null;

        // A missing/invalid side can't be auto-merged safely — let git surface the conflict.
        if (ours is null || theirs is null)
            return MergeResult.Conflict;

        double jaccard = KeywordJaccard.Compute(ours.Keywords, theirs.Keywords);
        if (jaccard < config.JaccardThreshold)
            return MergeResult.Conflict;

        // Per-field 3-way merge of scalar metadata: if only one side changed a field, take that
        // side; if both changed it differently, it's a genuine conflict.
        if (!TryMergeField(ancestor?.Name, ours.Name, theirs.Name, out var name)) return MergeResult.Conflict;
        if (!TryMergeField(ancestor?.Uri, ours.Uri, theirs.Uri, out var uri)) return MergeResult.Conflict;
        if (!TryMergeField(ancestor?.Description, ours.Description, theirs.Description, out var description)) return MergeResult.Conflict;
        if (!TryMergeField(ancestor?.OriginalBytes, (int?)ours.OriginalBytes, theirs.OriginalBytes, out var originalBytesN)) return MergeResult.Conflict;
        if (!TryMergeField(ancestor?.ChunkCount, (int?)ours.ChunkCount, theirs.ChunkCount, out var chunkCountN)) return MergeResult.Conflict;
        int originalBytes = originalBytesN ?? ours.OriginalBytes;
        int chunkCount = chunkCountN ?? ours.ChunkCount;

        // Union keywords (already known similar by Jaccard).
        var keywords = new HashSet<string>(ours.Keywords ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var kw in theirs.Keywords ?? [])
            keywords.Add(kw);

        string? updatedAt = CompareTimestamps(ours.UpdatedAt, theirs.UpdatedAt) > 0 ? ours.UpdatedAt : theirs.UpdatedAt;
        string? createdAt = CompareTimestamps(ours.CreatedAt, theirs.CreatedAt) < 0 ? ours.CreatedAt : theirs.CreatedAt;

        var merged = ours with
        {
            Name = name,
            Uri = uri,
            Description = description,
            OriginalBytes = originalBytes,
            ChunkCount = chunkCount,
            Keywords = keywords.Order().ToArray(),
            UpdatedAt = updatedAt,
            CreatedAt = createdAt,
        };

        File.WriteAllText(oursPath, JsonSerializer.Serialize(merged, MetaEntryJsonContext.Default.MetaEntry));
        return MergeResult.Resolved;
    }

    // Returns false when both sides changed the field to different values (real conflict).
    private static bool TryMergeField<T>(T ancestor, T ours, T theirs, out T result)
    {
        var cmp = EqualityComparer<T>.Default;
        if (cmp.Equals(ours, theirs)) { result = ours; return true; }        // same on both sides
        if (cmp.Equals(ours, ancestor)) { result = theirs; return true; }    // only theirs changed
        if (cmp.Equals(theirs, ancestor)) { result = ours; return true; }    // only ours changed
        result = ours; return false;                                          // both changed differently
    }

    private static int CompareTimestamps(string? a, string? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        return string.Compare(a, b, StringComparison.Ordinal);
    }
}
