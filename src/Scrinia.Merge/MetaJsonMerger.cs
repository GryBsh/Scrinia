using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scrinia.Merge;

public static class MetaJsonMerger
{
    public enum MergeResult { Resolved, Conflict }

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static MergeResult Merge(string ancestorPath, string oursPath, string theirsPath, MergeConfig config)
    {
        JsonObject? ours = Parse(oursPath);
        JsonObject? theirs = Parse(theirsPath);
        JsonObject? ancestor = File.Exists(ancestorPath) ? Parse(ancestorPath) : null;

        // A missing/invalid side can't be auto-merged safely — let git surface the conflict.
        if (ours is null || theirs is null)
            return MergeResult.Conflict;

        double jaccard = KeywordJaccard.Compute(GetStrings(ours, "keywords"), GetStrings(theirs, "keywords"));
        if (jaccard < config.JaccardThreshold)
            return MergeResult.Conflict;

        // Per-field 3-way merge of scalar metadata: only-one-side-changed → take it; both changed → conflict.
        if (!TryMergeField(GetString(ancestor, "name"), GetString(ours, "name"), GetString(theirs, "name"), out var name)) return MergeResult.Conflict;
        if (!TryMergeField(GetString(ancestor, "uri"), GetString(ours, "uri"), GetString(theirs, "uri"), out var uri)) return MergeResult.Conflict;
        if (!TryMergeField(GetString(ancestor, "description"), GetString(ours, "description"), GetString(theirs, "description"), out var description)) return MergeResult.Conflict;
        if (!TryMergeField(GetInt(ancestor, "originalBytes"), GetInt(ours, "originalBytes"), GetInt(theirs, "originalBytes"), out var originalBytesN)) return MergeResult.Conflict;
        if (!TryMergeField(GetInt(ancestor, "chunkCount"), GetInt(ours, "chunkCount"), GetInt(theirs, "chunkCount"), out var chunkCountN)) return MergeResult.Conflict;
        int originalBytes = originalBytesN ?? GetInt(ours, "originalBytes") ?? 0;
        int chunkCount = chunkCountN ?? GetInt(ours, "chunkCount") ?? 0;

        // Union keywords (already known similar by Jaccard).
        var keywords = new HashSet<string>(GetStrings(ours, "keywords") ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var kw in GetStrings(theirs, "keywords") ?? []) keywords.Add(kw);

        string? oursUpdated = GetString(ours, "updatedAt"), theirsUpdated = GetString(theirs, "updatedAt");
        string? updatedAt = CompareTimestamps(oursUpdated, theirsUpdated) > 0 ? oursUpdated : theirsUpdated;
        string? oursCreated = GetString(ours, "createdAt"), theirsCreated = GetString(theirs, "createdAt");
        string? createdAt = CompareTimestamps(oursCreated, theirsCreated) < 0 ? oursCreated : theirsCreated;

        // Overlay the merged fields onto OURS' full object — preserves every unknown property
        // (contentPreview, termFrequencies, chunkEntries, codeRefs, attributes, …) verbatim.
        SetString(ours, "name", name);
        SetString(ours, "uri", uri);
        SetString(ours, "description", description);
        ours["originalBytes"] = originalBytes;
        ours["chunkCount"] = chunkCount;
        ours["keywords"] = new JsonArray(keywords.Order(StringComparer.OrdinalIgnoreCase)
            .Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
        SetString(ours, "updatedAt", updatedAt);
        SetString(ours, "createdAt", createdAt);

        File.WriteAllText(oursPath, ours.ToJsonString(WriteOptions));
        return MergeResult.Resolved;
    }

    private static JsonObject? Parse(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path), NodeOptions) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string? GetString(JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? GetInt(JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static string[]? GetStrings(JsonObject? o, string key)
    {
        if (o is null || !o.TryGetPropertyValue(key, out var n) || n is not JsonArray arr) return null;
        var list = new List<string>(arr.Count);
        foreach (var el in arr)
            if (el is JsonValue v && v.TryGetValue<string>(out var s)) list.Add(s);
        return list.ToArray();
    }

    private static void SetString(JsonObject o, string key, string? value)
    {
        if (value is null) o.Remove(key);
        else o[key] = value;
    }

    // Returns false when both sides changed the field to different values (real conflict).
    private static bool TryMergeField<T>(T ancestor, T ours, T theirs, out T result)
    {
        var cmp = EqualityComparer<T>.Default;
        if (cmp.Equals(ours, theirs)) { result = ours; return true; }
        if (cmp.Equals(ours, ancestor)) { result = theirs; return true; }
        if (cmp.Equals(theirs, ancestor)) { result = ours; return true; }
        result = ours; return false;
    }

    private static int CompareTimestamps(string? a, string? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        return string.Compare(a, b, StringComparison.Ordinal);
    }
}
