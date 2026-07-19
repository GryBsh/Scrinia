using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Scrinia.Retrieval.Benchmarks.Core;

namespace Scrinia.Retrieval.Benchmarks.Loaders;

/// <summary>
/// Loads LoCoMo (<c>snap-research/locomo</c>, <c>data/locomo10.json</c>) into
/// <see cref="BenchmarkQuestion"/>s. 10 ultra-long conversations × ~200 QA pairs each
/// = 1986 questions total.
///
/// <para>Each top-level record has a <c>conversation</c> object keyed by
/// <c>session_N</c> (each value an array of dialogs) and <c>session_N_date_time</c>
/// (the session timestamp), plus a <c>qa</c> array of questions. Each question has
/// a <c>category</c> and an <c>evidence</c> array of <c>{dia_id, text}</c>. The
/// dialog ID format is <c>"D{session_num}:{dialog_idx_1_based}"</c>.</para>
///
/// <para>Session granularity (default, matches MemPalace's primary report): doc =
/// concatenated dialogs in that session, doc ID = <c>"session_N"</c>, ground-truth
/// session IDs derived from the evidence <c>dia_id</c> prefixes. Dialog granularity:
/// one doc per dialog turn, doc ID = <c>dia_id</c>, direct match.</para>
///
/// <para>One conversation = one per-question store; all that conversation's QA pairs
/// re-use the same store (vs LongMemEval where each question gets its own).</para>
/// </summary>
public sealed partial class LoCoMoLoader
{
    public enum Granularity { Session, Dialog }

    private readonly string _dataPath;
    private readonly Granularity _granularity;

    public LoCoMoLoader(string dataPath, Granularity granularity = Granularity.Session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        _dataPath = dataPath;
        _granularity = granularity;
    }

    public async IAsyncEnumerable<BenchmarkQuestionGroup> LoadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(_dataPath))
            throw new FileNotFoundException("LoCoMo data file not found", _dataPath);

        await using var stream = File.OpenRead(_dataPath);
        var doc = await JsonDocument.ParseAsync(stream, default, ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) yield break;

        int sampleIdx = 0;
        foreach (var rec in doc.RootElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            string sampleId = rec.TryGetProperty("sample_id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? $"conv_{sampleIdx}"
                : $"conv_{sampleIdx}";

            var (haystack, _) = ParseConversation(rec);
            if (haystack.Count == 0) { sampleIdx++; continue; }

            var qaQuestions = ParseQa(rec, sampleId, haystack);
            if (qaQuestions.Count == 0) { sampleIdx++; continue; }

            yield return new BenchmarkQuestionGroup(sampleId, haystack, qaQuestions);
            sampleIdx++;
        }
    }

    private (List<HaystackDoc> Haystack, Dictionary<int, string> SessionIdByNum) ParseConversation(JsonElement rec)
    {
        var haystack = new List<HaystackDoc>();
        var sessionIdByNum = new Dictionary<int, string>();

        if (!rec.TryGetProperty("conversation", out var convoEl) || convoEl.ValueKind != JsonValueKind.Object)
            return (haystack, sessionIdByNum);

        // Two kinds of keys appear under conversation: `session_N` (dialogs array)
        // and `session_N_date_time` (timestamp string). Pull both, indexed by N.
        var sessions = new SortedDictionary<int, (string Key, JsonElement Dialogs, DateTimeOffset? Date)>();
        foreach (var prop in convoEl.EnumerateObject())
        {
            if (prop.Name.EndsWith("_date_time", StringComparison.Ordinal))
            {
                string baseKey = prop.Name[..^"_date_time".Length];
                if (TryParseSessionNumber(baseKey, out int n)
                    && prop.Value.ValueKind == JsonValueKind.String
                    && TryParseSessionDate(prop.Value.GetString(), out var dt))
                {
                    var existing = sessions.TryGetValue(n, out var pre) ? pre : (baseKey, default, (DateTimeOffset?)null);
                    sessions[n] = (existing.Key, existing.Dialogs, dt);
                }
            }
            else if (TryParseSessionNumber(prop.Name, out int n) && prop.Value.ValueKind == JsonValueKind.Array)
            {
                var existing = sessions.TryGetValue(n, out var pre) ? pre : default;
                sessions[n] = (prop.Name, prop.Value, existing.Date);
            }
        }

        foreach (var (n, (key, dialogsEl, date)) in sessions)
        {
            if (dialogsEl.ValueKind != JsonValueKind.Array) continue;
            sessionIdByNum[n] = key;

            if (_granularity == Granularity.Session)
            {
                string text = JoinDialogs(dialogsEl);
                if (string.IsNullOrWhiteSpace(text)) continue;
                haystack.Add(new HaystackDoc(key, text, date));
            }
            else
            {
                int dialogIdx = 1;
                foreach (var dialog in dialogsEl.EnumerateArray())
                {
                    string text = FormatDialog(dialog);
                    if (!string.IsNullOrWhiteSpace(text))
                        // dialog docs carry their parent session so one agentic-ingest episode sees the whole session.
                        haystack.Add(new HaystackDoc($"D{n}:{dialogIdx}", text, date, SessionId: key));
                    dialogIdx++;
                }
            }
        }
        return (haystack, sessionIdByNum);
    }

    private List<BenchmarkQuestion> ParseQa(JsonElement rec, string sampleId, IReadOnlyList<HaystackDoc> haystack)
    {
        var result = new List<BenchmarkQuestion>();
        if (!rec.TryGetProperty("qa", out var qaEl) || qaEl.ValueKind != JsonValueKind.Array)
            return result;

        int qaIdx = 0;
        foreach (var qa in qaEl.EnumerateArray())
        {
            if (!qa.TryGetProperty("question", out var qEl) || qEl.ValueKind != JsonValueKind.String) continue;
            string question = qEl.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(question)) continue;

            string category = ResolveCategory(qa);

            string? goldAnswer = null;
            if (qa.TryGetProperty("answer", out var ansEl))
            {
                goldAnswer = ansEl.ValueKind switch
                {
                    JsonValueKind.String => ansEl.GetString(),
                    JsonValueKind.Number => ansEl.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null,
                };
                if (string.IsNullOrWhiteSpace(goldAnswer)) goldAnswer = null;
            }

            var diaIds = ExtractEvidenceDiaIds(qa);
            if (diaIds.Count == 0) { qaIdx++; continue; }

            string[] groundTruth = _granularity == Granularity.Dialog
                ? diaIds.ToArray()
                : ExtractSessionIdsFromDiaIds(diaIds).ToArray();

            result.Add(new BenchmarkQuestion(
                Id: $"{sampleId}_qa{qaIdx}",
                QueryText: question,
                Haystack: haystack,
                GroundTruthIds: groundTruth,
                GroundTruthTexts: [],
                Category: category,
                GoldAnswer: goldAnswer));
            qaIdx++;
        }
        return result;
    }

    private static List<string> ExtractEvidenceDiaIds(JsonElement qa)
    {
        var list = new List<string>();
        if (!qa.TryGetProperty("evidence", out var evEl) || evEl.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var ev in evEl.EnumerateArray())
        {
            // evidence entries are either {dia_id, text} objects or bare dia_id strings —
            // both shapes appear in published locomo dumps.
            switch (ev.ValueKind)
            {
                case JsonValueKind.Object when ev.TryGetProperty("dia_id", out var dEl) && dEl.ValueKind == JsonValueKind.String:
                    if (dEl.GetString() is { Length: > 0 } s) list.Add(s);
                    break;
                case JsonValueKind.String:
                    if (ev.GetString() is { Length: > 0 } str) list.Add(str);
                    break;
            }
        }
        return list;
    }

    /// <summary>
    /// LoCoMo ships QA category as either a string (newer dumps) or an integer 1-5
    /// (the snap-research <c>locomo10.json</c> we point at by default). Map ints to
    /// the labels MemPalace's bench script uses so per-category aggregates compare
    /// across systems without manual joining. MemPalace's mapping is
    /// <c>{1: "Single-hop", 2: "Temporal", 3: "Temporal-inference", 4: "Open-domain",
    /// 5: "Adversarial"}</c>; we keep snake_case to match the convention used for our
    /// other per-category labels. An earlier version of this method labeled 2 as
    /// <c>"multi_hop"</c> and 3 as <c>"temporal_reasoning"</c> — those were silent
    /// renames inherited from a different paper and invalidated cross-system
    /// per-category comparisons.
    /// </summary>
    private static string ResolveCategory(JsonElement qa)
    {
        if (!qa.TryGetProperty("category", out var catEl)) return "uncategorized";
        return catEl.ValueKind switch
        {
            JsonValueKind.String => catEl.GetString() ?? "uncategorized",
            JsonValueKind.Number when catEl.TryGetInt32(out int n) => n switch
            {
                1 => "single_hop",
                2 => "temporal",
                3 => "temporal_inference",
                4 => "open_domain",
                5 => "adversarial",
                _ => $"category_{n}",
            },
            _ => "uncategorized",
        };
    }

    private static IEnumerable<string> ExtractSessionIdsFromDiaIds(IReadOnlyList<string> diaIds)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in diaIds)
        {
            var m = DiaIdRegex().Match(id);
            if (!m.Success) continue;
            string sessionId = $"session_{m.Groups[1].Value}";
            if (seen.Add(sessionId)) yield return sessionId;
        }
    }

    private static string JoinDialogs(JsonElement dialogsEl)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var dialog in dialogsEl.EnumerateArray())
        {
            string formatted = FormatDialog(dialog);
            if (string.IsNullOrWhiteSpace(formatted)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(formatted);
        }
        return sb.ToString();
    }

    private static string FormatDialog(JsonElement dialog)
    {
        if (dialog.ValueKind != JsonValueKind.Object) return string.Empty;
        string speaker = dialog.TryGetProperty("speaker", out var sEl) && sEl.ValueKind == JsonValueKind.String
            ? sEl.GetString() ?? string.Empty : string.Empty;
        string text = dialog.TryGetProperty("text", out var tEl) && tEl.ValueKind == JsonValueKind.String
            ? tEl.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return string.IsNullOrEmpty(speaker) ? text : $"{speaker}: {text}";
    }

    /// <summary>
    /// Parses LoCoMo session timestamps of the form <c>"1:56 pm on 8 May, 2023"</c>. The lenient
    /// <see cref="DateTimeOffset.TryParse(string?, out DateTimeOffset)"/> rejects that shape (the
    /// <c>"h:mm tt on"</c> prefix), so strip everything up to and including <c>" on "</c> and parse
    /// the <c>"8 May, 2023"</c> remainder; fall back to a plain parse for any already-standard value
    /// (e.g. ISO). No-timezone values are assumed UTC so the surfaced date is offset-stable. Every
    /// one of the 288 timestamps in <c>locomo10.json</c> parses via the strip path; the prior plain
    /// TryParse matched zero of them, silently leaving <c>Timestamp</c> null for every session.
    /// </summary>
    internal static bool TryParseSessionDate(string? raw, out DateTimeOffset dt)
    {
        dt = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        int onIdx = raw.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        string candidate = onIdx >= 0 ? raw[(onIdx + 4)..] : raw;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        const System.Globalization.DateTimeStyles styles = System.Globalization.DateTimeStyles.AssumeUniversal;
        return DateTimeOffset.TryParse(candidate, ci, styles, out dt)
            || DateTimeOffset.TryParse(raw, ci, styles, out dt);
    }

    [GeneratedRegex(@"^session_(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionKeyRegex();

    private static bool TryParseSessionNumber(string key, out int n)
    {
        var m = SessionKeyRegex().Match(key);
        if (m.Success && int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out n))
            return true;
        n = 0;
        return false;
    }

    [GeneratedRegex(@"^D(\d+):(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DiaIdRegex();
}
