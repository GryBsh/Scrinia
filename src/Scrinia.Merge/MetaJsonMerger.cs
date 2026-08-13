using System.Globalization;
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
        if (!TryMergeField(GetLong(ancestor, "originalBytes"), GetLong(ours, "originalBytes"), GetLong(theirs, "originalBytes"), out var originalBytesN)) return MergeResult.Conflict;
        if (!TryMergeField(GetInt(ancestor, "chunkCount"), GetInt(ours, "chunkCount"), GetInt(theirs, "chunkCount"), out var chunkCountN)) return MergeResult.Conflict;
        long originalBytes = originalBytesN ?? GetLong(ours, "originalBytes") ?? 0;
        int chunkCount = chunkCountN ?? GetInt(ours, "chunkCount") ?? 0;

        // Union the keywords (already known similar by Jaccard) and their provenance with them.
        //
        // DUPLICATE, AND THE DUPLICATION IS FORCED. The original is
        // Scrinia.Core.Merge.SidecarKeywordMerge.Union, which reconcile()'s auto-resolver calls;
        // this project is a standalone AOT executable with zero project references by hard
        // constraint, so it cannot call it. Change one and you must change the other:
        // Scrinia.Merge.Tests.SidecarKeywordMergeParityTests runs the same cases through both and
        // fails when they disagree. The rule and its reasoning live there. In summary: keywords
        // union case-insensitively and sort; agentKeywords is UNKNOWN unless BOTH sides recorded
        // it, because a reader treats a non-null list as the complete answer, so merging known
        // with unknown would positively assert that the unrecorded side'''s curated keywords were
        // auto-extracted and demote them with no way to tell anything was lost.
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kw in GetStrings(ours, "keywords") ?? []) if (!string.IsNullOrWhiteSpace(kw)) keywords.Add(kw);
        foreach (var kw in GetStrings(theirs, "keywords") ?? []) if (!string.IsNullOrWhiteSpace(kw)) keywords.Add(kw);

        string[]? oursAgent = GetStrings(ours, "agentKeywords");
        string[]? theirsAgent = GetStrings(theirs, "agentKeywords");
        HashSet<string>? agentKeywords = null;
        if (oursAgent is not null && theirsAgent is not null)
        {
            agentKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kw in oursAgent) if (!string.IsNullOrWhiteSpace(kw)) agentKeywords.Add(kw);
            foreach (var kw in theirsAgent) if (!string.IsNullOrWhiteSpace(kw)) agentKeywords.Add(kw);
        }

        // MALFORMED createdAt/updatedAt (present but not a JSON string, including an explicit JSON
        // null) must escalate rather than be silently treated as absent — see
        // TryGetTimestampString. This is a narrower gate than GetString's default behaviour, scoped
        // to these two fields only: reconcile()'s auto-resolver reads the identical shape through
        // MemoryTools.Lifecycle.TryReadTimestampString, a deliberate mirror of this check, so the
        // two mergers agree on malformed input the same way they agree on well-formed timestamps.
        if (!TryGetTimestampString(ours, "createdAt", out var oursCreated) ||
            !TryGetTimestampString(ours, "updatedAt", out var oursUpdated) ||
            !TryGetTimestampString(theirs, "createdAt", out var theirsCreated) ||
            !TryGetTimestampString(theirs, "updatedAt", out var theirsUpdated))
            return MergeResult.Conflict;

        var (createdAt, updatedAt) = SelectTimestamps(oursCreated, oursUpdated, theirsCreated, theirsUpdated);

        // Overlay the merged fields onto OURS' full object — preserves every unknown property
        // (contentPreview, chunkEntries, codeRefs, attributes, …) verbatim. termFrequencies is NOT
        // among them: it is merged below, because preserving it verbatim silently dropped theirs'
        // contribution on every merge.
        SetString(ours, "name", name);
        SetString(ours, "uri", uri);
        SetString(ours, "description", description);
        ours["originalBytes"] = originalBytes;
        ours["chunkCount"] = chunkCount;
        if (keywords.Count > 0)
            ours["keywords"] = new JsonArray(keywords.Order(StringComparer.OrdinalIgnoreCase)
                .Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
        else
            ours.Remove("keywords");
        if (agentKeywords is null)
            ours.Remove("agentKeywords");
        else
            ours["agentKeywords"] = new JsonArray(agentKeywords.Order(StringComparer.OrdinalIgnoreCase)
                .Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
        SetString(ours, "updatedAt", updatedAt);
        SetString(ours, "createdAt", createdAt);

        // ABSENT (the key is missing) contributes nothing to the union — a normal side, not an
        // error. MALFORMED (present but not an object, or holding a value that isn't an int) means
        // this driver cannot safely interpret what that side intended, so ours' termFrequencies is
        // left exactly as it was rather than silently dropped or rewritten around the part that
        // couldn't be read.
        if (TryGetIntMap(ours, "termFrequencies", out var oursTf)
            && TryGetIntMap(theirs, "termFrequencies", out var theirsTf))
        {
            var mergedTf = UnionTermFrequencies(oursTf, theirsTf);
            if (mergedTf is null)
                ours.Remove("termFrequencies");
            else
                ours["termFrequencies"] = new JsonObject(
                    mergedTf.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                            .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));
        }

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

    private static long? GetLong(JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<long>(out var i) ? i : null;

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

    /// <summary>Reads a createdAt/updatedAt-shaped JSON value into a string. Returns true for
    /// ABSENT (the key is missing; contributes nothing to <see cref="SelectTimestamps"/>) and for a
    /// well-formed JSON string. Returns false for MALFORMED input — present but not a JSON string,
    /// including an explicit JSON <c>null</c>, e.g. a numeric epoch or a hand-edited <c>null</c>
    /// from a foreign sidecar — so the caller can escalate to conflict instead of what plain
    /// <see cref="GetString"/> would do here: silently treat it as absent and let the other side's
    /// value win with no conflict raised.
    ///
    /// <para><c>reconcile()</c>'s auto-resolver reads the same shape through
    /// <c>MemoryTools.Lifecycle.TryReadTimestampString</c>, a deliberate mirror of this method's
    /// <c>TryGetPropertyValue</c>-before-inspecting-the-value shape — not, as an earlier version of
    /// this comment claimed, an exception thrown inside <c>JsonNode.GetValue&lt;string&gt;()</c>.
    /// That claim was true for a non-string value like a number, but false for an explicit JSON
    /// <c>null</c>: <c>node?[key]</c> returns a null reference for BOTH "absent" and "present but
    /// null," so the old reconcile()-side read silently treated a null <c>createdAt</c>/
    /// <c>updatedAt</c> as absent and auto-resolved on the other side, while this method already
    /// refused it — the two mergers disagreeing on exactly the shape this rule exists to align.
    /// </para></summary>
    private static bool TryGetTimestampString(JsonObject? o, string key, out string? value)
    {
        value = null;
        if (o is null || !o.TryGetPropertyValue(key, out var n))
            return true; // absent
        if (n is not JsonValue v || !v.TryGetValue<string>(out var s))
            return false; // malformed: present but not a JSON string
        value = s;
        return true;
    }

    /// <summary>Reads a termFrequencies-shaped JSON object into a dictionary. Returns true for
    /// ABSENT (<paramref name="map"/> is null; the side recorded no term frequencies and
    /// contributes nothing to a merge) and for a well-formed object. Returns false for MALFORMED
    /// input — present but not a JSON object, or containing a value that isn't readable as an int —
    /// so the caller can leave the field untouched rather than silently drop or rewrite around the
    /// part it couldn't read.</summary>
    private static bool TryGetIntMap(JsonObject? o, string key, out Dictionary<string, int>? map)
    {
        if (o is null || !o.TryGetPropertyValue(key, out var n))
        {
            map = null;
            return true; // absent
        }
        if (n is not JsonObject obj)
        {
            map = null;
            return false; // malformed: present but not an object
        }

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in obj)
        {
            if (value is JsonValue v && v.TryGetValue<int>(out int i))
            {
                result[name] = i;
            }
            else
            {
                map = null;
                return false; // malformed: a value isn't readable as an int
            }
        }
        map = result;
        return true;
    }

    // DUPLICATE, AND THE DUPLICATION IS FORCED. The original is
    // Scrinia.Core.Merge.SidecarTermFrequencyMerge.Union, which reconcile()'s auto-resolver calls;
    // this project is a standalone AOT executable with zero project references by hard constraint,
    // so it cannot call it. Change one and you must change the other:
    // Scrinia.Merge.Tests.SidecarTermFrequencyMergeParityTests runs the same cases through both and
    // fails when they disagree. The rule and its reasoning live there. In summary: union the keys,
    // take the MAX on a shared key (a count is a frequency, not a running total — summing would
    // inflate a term on every merge), and a null side contributes nothing rather than vetoing.
    private static Dictionary<string, int>? UnionTermFrequencies(
        Dictionary<string, int>? ours,
        Dictionary<string, int>? theirs)
    {
        if (ours is null && theirs is null) return null;

        // OrdinalIgnoreCase, matching the map every producer builds. TextAnalysis
        // .ComputeTermFrequencies uses OrdinalIgnoreCase, and Tokenize lowercases every token, so
        // no producer can emit two keys differing only in case — for real data the comparer is
        // unobservable.
        //
        // It is load-bearing for a hand-edited or foreign sidecar. Merging under Ordinal would let
        // "Auth" and "auth" both survive into the merged map, and
        // MemoryDerivation.ForMetadataUpdate copies that map into an OrdinalIgnoreCase dictionary
        // — which THROWS ArgumentException on exactly that pair. Folding here is what stops a
        // malformed sidecar becoming a crash three calls later.
        var merged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (ours is not null)
            foreach (var (term, count) in ours)
                merged[term] = count;
        if (theirs is not null)
            foreach (var (term, count) in theirs)
                merged[term] = merged.TryGetValue(term, out int existing) ? Math.Max(existing, count) : count;

        return merged;
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

    // DUPLICATE, AND THE DUPLICATION IS FORCED. The original is
    // Scrinia.Core.Merge.SidecarTimestampMerge.Select, which reconcile()'s auto-resolver calls;
    // this project is a standalone AOT executable with zero project references by hard constraint,
    // so it cannot call it. Change one and you must change the other:
    // Scrinia.Merge.Tests.SidecarTimestampMergeParityTests runs the same cases through both and
    // fails when they disagree. The rule and its reasoning live there. In summary: pick the LATEST
    // updatedAt and the EARLIEST createdAt, compared as parsed instants; an unparseable value is
    // treated as absent for comparison, and OURS is returned unchanged when neither side parses, so
    // a merge can never silently reformat a timestamp.
    private static (string? CreatedAt, string? UpdatedAt) SelectTimestamps(
        string? oursCreated, string? oursUpdated, string? theirsCreated, string? theirsUpdated) =>
        (Pick(oursCreated, theirsCreated, earliest: true),
         Pick(oursUpdated, theirsUpdated, earliest: false));

    private static string? Pick(string? ours, string? theirs, bool earliest)
    {
        bool oursOk = TryParse(ours, out var o);
        bool theirsOk = TryParse(theirs, out var t);

        if (!oursOk && !theirsOk) return ours;
        if (!oursOk) return theirs;
        if (!theirsOk) return ours;

        return (earliest ? o <= t : o >= t) ? ours : theirs;
    }

    private static bool TryParse(string? value, out DateTimeOffset parsed)
    {
        parsed = default;
        return !string.IsNullOrWhiteSpace(value)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed);
    }
}
