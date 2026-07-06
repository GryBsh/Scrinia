using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Retrieval.Benchmarks.Core;

namespace Scrinia.Retrieval.Benchmarks.Loaders;

/// <summary>
/// Loads LongMemEval-cleaned (HuggingFace <c>xiaowu0162/longmemeval-cleaned</c>) into
/// <see cref="BenchmarkQuestion"/>s.
///
/// <para>Each top-level record has a question plus its own per-question haystack of
/// ~53 sessions. Each session is an array of turns with <c>role</c> and <c>content</c>.
/// Session-granularity document = concatenated user turns (matches MemPalace's
/// primary report); a session timestamp comes from the aligned <c>haystack_dates</c>
/// list. Ground truth is the set of <c>answer_session_ids</c>.</para>
///
/// <para>Splits: when <see cref="SplitMode.Dev"/> or <see cref="SplitMode.HeldOut"/>
/// is requested, the loader reads MemPalace's <c>lme_split_50_450.json</c> and
/// filters to the matching question_ids. <see cref="SplitMode.Full"/> emits every
/// record in the dataset.</para>
/// </summary>
public sealed class LongMemEvalLoader
{
    public enum SplitMode { Full, Dev, HeldOut }

    private readonly string _dataPath;
    private readonly SplitMode _split;
    private readonly string? _splitFile;
    private readonly Granularity _granularity;

    public enum Granularity { Session, Turn }

    public LongMemEvalLoader(string dataPath, SplitMode split = SplitMode.Full, string? splitFile = null, Granularity granularity = Granularity.Session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        _dataPath = dataPath;
        _split = split;
        _splitFile = splitFile;
        _granularity = granularity;
    }

    public async IAsyncEnumerable<BenchmarkQuestion> LoadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(_dataPath))
            throw new FileNotFoundException("LongMemEval data file not found", _dataPath);

        HashSet<string>? filter = await LoadSplitFilterAsync(ct);

        await using var stream = File.OpenRead(_dataPath);
        var records = await JsonSerializer.DeserializeAsync<List<LongMemEvalRecord>>(stream, JsonOptions, ct);
        if (records is null) yield break;

        foreach (var rec in records)
        {
            ct.ThrowIfCancellationRequested();
            if (rec is null || string.IsNullOrWhiteSpace(rec.Question) || string.IsNullOrWhiteSpace(rec.QuestionId))
                continue;

            if (filter is not null && !filter.Contains(rec.QuestionId))
                continue;

            var haystack = BuildHaystack(rec);
            if (haystack.Count == 0) continue;

            var groundTruth = (rec.AnswerSessionIds ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            if (groundTruth.Length == 0) continue;

            // For turn granularity the labelled session_id needs to expand to *every*
            // turn doc under that session, since the scorer only matches on hit.Id.
            // Session granularity preserves the labelled ID directly.
            string[] expandedGroundTruth = _granularity == Granularity.Turn
                ? ExpandSessionIdsToTurnIds(groundTruth, rec)
                : groundTruth;

            yield return new BenchmarkQuestion(
                Id: rec.QuestionId,
                QueryText: rec.Question,
                Haystack: haystack,
                GroundTruthIds: expandedGroundTruth,
                GroundTruthTexts: [],
                Category: rec.QuestionType ?? "uncategorized");
        }
    }

    private List<HaystackDoc> BuildHaystack(LongMemEvalRecord rec)
    {
        var docs = new List<HaystackDoc>();
        if (rec.HaystackSessions is null || rec.HaystackSessionIds is null) return docs;

        int n = Math.Min(rec.HaystackSessions.Count, rec.HaystackSessionIds.Count);
        for (int i = 0; i < n; i++)
        {
            var session = rec.HaystackSessions[i];
            string sessionId = rec.HaystackSessionIds[i];
            DateTimeOffset? sessionDate = TryParseDate(
                rec.HaystackDates is not null && i < rec.HaystackDates.Count ? rec.HaystackDates[i] : null);

            if (session is null || string.IsNullOrWhiteSpace(sessionId)) continue;

            if (_granularity == Granularity.Session)
            {
                string text = JoinUserTurns(session);
                if (string.IsNullOrWhiteSpace(text)) continue;
                docs.Add(new HaystackDoc(sessionId, text, sessionDate));
            }
            else // Turn
            {
                int turnIdx = 0;
                foreach (var turn in session)
                {
                    if (turn is null) continue;
                    if (!string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrWhiteSpace(turn.Content)) continue;
                    // turn docs carry their parent session so one agentic-ingest episode sees the whole session.
                    docs.Add(new HaystackDoc($"{sessionId}_turn_{turnIdx}", turn.Content, sessionDate, SessionId: sessionId));
                    turnIdx++;
                }
            }
        }
        return docs;
    }

    private static string JoinUserTurns(IReadOnlyList<LongMemEvalTurn> session)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var t in session)
        {
            if (t is null) continue;
            if (!string.Equals(t.Role, "user", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(t.Content)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(t.Content);
        }
        return sb.ToString();
    }

    private static string[] ExpandSessionIdsToTurnIds(string[] sessionIds, LongMemEvalRecord rec)
    {
        if (rec.HaystackSessions is null || rec.HaystackSessionIds is null) return sessionIds;
        var target = new HashSet<string>(sessionIds, StringComparer.OrdinalIgnoreCase);
        var expanded = new List<string>();
        int n = Math.Min(rec.HaystackSessions.Count, rec.HaystackSessionIds.Count);
        for (int i = 0; i < n; i++)
        {
            if (!target.Contains(rec.HaystackSessionIds[i])) continue;
            var session = rec.HaystackSessions[i];
            if (session is null) continue;
            int turnIdx = 0;
            foreach (var t in session)
            {
                if (t is null) continue;
                if (!string.Equals(t.Role, "user", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(t.Content)) continue;
                expanded.Add($"{rec.HaystackSessionIds[i]}_turn_{turnIdx}");
                turnIdx++;
            }
        }
        return expanded.ToArray();
    }

    private async Task<HashSet<string>?> LoadSplitFilterAsync(CancellationToken ct)
    {
        if (_split == SplitMode.Full) return null;

        string splitPath = _splitFile ?? GuessSplitFilePath(_dataPath);
        if (!File.Exists(splitPath))
            throw new FileNotFoundException(
                $"--split={_split.ToString().ToLowerInvariant()} requires lme_split_50_450.json " +
                $"(searched: {splitPath}). Download from MemPalace's benchmarks/ dir or pass --split-file.", splitPath);

        await using var stream = File.OpenRead(splitPath);
        var split = await JsonSerializer.DeserializeAsync<LmeSplitFile>(stream, JsonOptions, ct);

        IReadOnlyList<string>? ids = _split switch
        {
            SplitMode.Dev => split?.Dev,
            SplitMode.HeldOut => split?.HeldOut,
            _ => null,
        };
        if (ids is null || ids.Count == 0)
            throw new InvalidOperationException($"split file {splitPath} did not contain '{_split.ToString().ToLowerInvariant()}' ids");

        return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
    }

    private static string GuessSplitFilePath(string dataPath)
    {
        string? dir = Path.GetDirectoryName(dataPath);
        return Path.Combine(dir ?? string.Empty, "lme_split_50_450.json");
    }

    private static DateTimeOffset? TryParseDate(string? raw) =>
        DateTimeOffset.TryParse(raw, out var dto) ? dto : null;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // `answer` is intentionally omitted — System.Text.Json skips unmapped fields, so
    // we sidestep the fact that LongMemEval ships `answer` as either a string or a
    // list-of-strings depending on the question_type (knowledge-update questions can
    // have multiple correct answers across time). Retrieval scoring only uses the
    // labelled session IDs, never the answer text.
    private sealed record LongMemEvalRecord(
        [property: JsonPropertyName("question_id")] string? QuestionId,
        [property: JsonPropertyName("question_type")] string? QuestionType,
        [property: JsonPropertyName("question")] string? Question,
        [property: JsonPropertyName("question_date")] string? QuestionDate,
        [property: JsonPropertyName("answer_session_ids")] IReadOnlyList<string>? AnswerSessionIds,
        [property: JsonPropertyName("haystack_sessions")] IReadOnlyList<IReadOnlyList<LongMemEvalTurn>>? HaystackSessions,
        [property: JsonPropertyName("haystack_session_ids")] IReadOnlyList<string>? HaystackSessionIds,
        [property: JsonPropertyName("haystack_dates")] IReadOnlyList<string>? HaystackDates);

    private sealed record LongMemEvalTurn(
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("content")] string? Content);

    private sealed record LmeSplitFile(
        [property: JsonPropertyName("dev")] IReadOnlyList<string>? Dev,
        [property: JsonPropertyName("held_out")] IReadOnlyList<string>? HeldOut);
}
