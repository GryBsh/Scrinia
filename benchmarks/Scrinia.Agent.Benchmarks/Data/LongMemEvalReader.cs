using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrinia.Agent.Benchmarks;

public sealed class LongMemEvalReader
{
    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
    {
        "single-session-user", "single-session-assistant", "single-session-preference",
        "temporal-reasoning", "knowledge-update", "multi-session"
    };

    private static readonly string[] DateFormats =
    [
        "yyyy/MM/dd (ddd) HH:mm", "yyyy/MM/dd HH:mm", "yyyy-MM-dd",
        "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"
    ];

    public async Task<DatasetSnapshot> LoadAsync(string path, IReadOnlyList<string> selectedIds, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(selectedIds);
        var selection = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in selectedIds)
            if (string.IsNullOrWhiteSpace(id) || !selection.Add(id))
                throw InputDiagnostics.Invalid("Invalid or duplicate selected question_id.");

        PathSafety.RequireNoLinks(path);
        byte[] bytes = await File.ReadAllBytesAsync(path, ct);
        RawLongMemEvalCase?[]? source;
        try
        {
            // Hash the original bytes, but permit the UTF-8 preamble produced by text editors.
            int offset = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xef, 0xbb, 0xbf]) ? 3 : 0;
            source = JsonSerializer.Deserialize(bytes.AsSpan(offset), LongMemEvalJsonContext.Default.RawLongMemEvalCaseArray);
        }
        catch (JsonException ex)
        {
            throw InputDiagnostics.Invalid($"Invalid dataset JSON at line {ex.LineNumber}, byte {ex.BytePositionInLine}.");
        }
        if (source is null || source.Length == 0)
            throw InputDiagnostics.Invalid("Dataset must contain at least one case.");

        var indexed = new Dictionary<string, RawLongMemEvalCase>(StringComparer.Ordinal);
        var orderedIds = new List<string>(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var record = source[i] ?? throw Invalid($"record[{i}]", "record", "must be an object");
            string id = Required(record.QuestionId, $"record[{i}]", "question_id");
            if (!indexed.TryAdd(id, record)) throw Invalid(id, "question_id", "is duplicated");
            orderedIds.Add(id);
        }
        var requested = selectedIds.Count == 0 ? (IReadOnlyList<string>)orderedIds : selectedIds;
        var cases = new List<BenchmarkCase>(requested.Count);
        foreach (string id in requested)
        {
            ct.ThrowIfCancellationRequested();
            if (!indexed.TryGetValue(id, out var record)) throw Invalid(id, "question_id", "was not found");
            cases.Add(Project(record, id));
        }
        return new(Convert.ToHexStringLower(SHA256.HashData(bytes)), cases);
    }

    private static BenchmarkCase Project(RawLongMemEvalCase record, string id)
    {
        string question = Required(record.Question, id, "question");
        string questionDate = Required(record.QuestionDate, id, "question_date");
        ParseDate(questionDate, id, "question_date");
        string category = Required(record.QuestionType, id, "question_type");
        if (!Categories.Contains(category)) throw Invalid(id, "question_type", "is unsupported");
        string answer = record.Answer.ValueKind switch
        {
            JsonValueKind.String => Required(record.Answer.GetString(), id, "answer"),
            JsonValueKind.Number => record.Answer.GetRawText(),
            _ => throw Invalid(id, "answer", "must be a string or number")
        };

        var sessions = record.HaystackSessions;
        var dates = record.HaystackDates;
        var ids = record.HaystackSessionIds;
        if (sessions is null || sessions.Length == 0)
            throw Invalid(id, "haystack_sessions", "must be nonempty");
        if (dates is null || dates.Length != sessions.Length)
            throw Invalid(id, "haystack_dates", "must align with haystack_sessions");
        if (ids is null || ids.Length != sessions.Length)
            throw Invalid(id, "haystack_session_ids", "must align with haystack_sessions");

        var ordered = new List<(DateTimeOffset Instant, int Index, string Date, TranscriptTurn[] Turns)>();
        for (int i = 0; i < sessions.Length; i++)
        {
            // Source IDs identify reusable transcripts, not unique dated occurrences.
            _ = Required(ids[i], id, $"haystack_session_ids[{i}]");
            string date = Required(dates[i], id, $"haystack_dates[{i}]");
            var instant = ParseDate(date, id, $"haystack_dates[{i}]");
            var turns = sessions[i];
            if (turns is null || turns.Length == 0) throw Invalid(id, $"haystack_sessions[{i}]", "must be nonempty");
            var visible = new TranscriptTurn[turns.Length];
            for (int j = 0; j < turns.Length; j++)
            {
                string field = $"haystack_sessions[{i}][{j}]";
                var turn = turns[j] ?? throw Invalid(id, field, "must be an object");
                string role = Required(turn.Role, id, field + ".role");
                if (role is not ("user" or "assistant")) throw Invalid(id, field + ".role", "must be user or assistant");
                visible[j] = new(role, Required(turn.Content, id, field + ".content"));
            }
            ordered.Add((instant, i, date, visible));
        }
        LearningSession[] projected = ordered.OrderBy(s => s.Instant).ThenBy(s => s.Index)
            .Select((s, i) => new LearningSession(i + 1, s.Date, s.Turns)).ToArray();
        return new(new(id, category, id.EndsWith("_abs", StringComparison.Ordinal), answer),
            new(question, questionDate), projected);
    }

    private static string Required(string? value, string id, string field)
        => !string.IsNullOrWhiteSpace(value) ? value : throw Invalid(id, field, "must be nonempty");

    private static DateTimeOffset ParseDate(string value, string id, string field)
        => DateTimeOffset.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date : throw Invalid(id, field, "has an unsupported date format");

    private static InvalidDataException Invalid(string id, string field, string reason)
        => InputDiagnostics.Mark(new InvalidDataException($"Case '{id}': {field} {reason}."), $"Dataset field {field} {reason}.");
}

// Source-only records never cross the agent boundary. Extra benchmark annotations are ignored.
internal sealed record RawLongMemEvalCase(
    [property: JsonPropertyName("question_id")] string? QuestionId,
    [property: JsonPropertyName("question_type")] string? QuestionType,
    [property: JsonPropertyName("question")] string? Question,
    [property: JsonPropertyName("question_date")] string? QuestionDate,
    [property: JsonPropertyName("answer")] JsonElement Answer,
    [property: JsonPropertyName("haystack_session_ids")] string?[]? HaystackSessionIds,
    [property: JsonPropertyName("haystack_dates")] string?[]? HaystackDates,
    [property: JsonPropertyName("haystack_sessions")] RawLongMemEvalTurn?[]?[]? HaystackSessions);
internal sealed record RawLongMemEvalTurn(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] string? Content);
