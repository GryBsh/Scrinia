using Scrinia.Retrieval.Benchmarks.Llm;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;

namespace Scrinia.Retrieval.Benchmarks.Judge;

/// <summary>
/// A single answerability verdict produced by <see cref="AnswerabilityJudge"/>. Splits two
/// distinct signals: <see cref="SurfacedFromContext"/> (the answerer produced an answer
/// rather than emitting the not-answerable sentinel) and <see cref="AnsweredCorrectly"/>
/// (the grader LLM judged that answer as matching the gold answer), plus the raw
/// answerer/grader text for debugging.
/// </summary>
public sealed record JudgeVerdict(bool SurfacedFromContext, bool AnsweredCorrectly, string Answer, string GraderVerdict);

/// <summary>
/// LLM-judge answerability metric: for a question, runs an ANSWERER call (question +
/// retrieved content → answer, no gold visible) and then a GRADER call (answer vs gold
/// → correct/incorrect), caching both and tallying outcome counters for the run summary.
///
/// <para>Never caches a failed/blank judge call — only genuine completions are persisted,
/// so a killed run can be resumed and will simply re-attempt whatever wasn't cached.</para>
/// </summary>
public sealed class AnswerabilityJudge
{
    private const string AnswerOp = "answer";
    private const string GradeOp = "grade";

    // A null/blank judge completion (a transport failure, or an HTTP-200 with empty content)
    // is re-attempted up to this many times total before the question is dropped from the
    // aggregate. The judge model runs at a non-zero temperature, so a re-attempt genuinely
    // varies rather than reproducing the same blank.
    private const int MaxCallAttempts = 3;

    /// <summary>The answerer surfaced an answer iff it did not emit the not-answerable sentinel.
    /// Case-insensitive, trimmed, prefix-matched so trailing chatter still counts as not-surfaced.</summary>
    public static bool IsSurfaced(string? answer) =>
        !(answer ?? string.Empty).Trim().StartsWith(JudgePrompts.NotAnswerableSentinel, StringComparison.OrdinalIgnoreCase);

    private readonly IBenchLlmClient _llm;
    private readonly LlmResponseCache _cache;
    private readonly string _providerId;
    private readonly string _modelSlug;
    private readonly int _answerMaxTokens;
    private readonly int _graderMaxTokens;
    private readonly FailureSink? _failureSink;

    private int _judgedCount;
    private int _noGoldCount;
    private int _failedCount;

    /// <param name="answerMaxTokens">The answerer call's <c>max_tokens</c>. On an
    /// OpenAI-compatible server this bounds the WHOLE request — the question + the retrieved
    /// content (up to judgeK memories) + the answer — not just the completion, so it must
    /// clear the prompt with room left to answer or the output truncates to empty.</param>
    /// <param name="graderMaxTokens">The grader call's <c>max_tokens</c>, likewise bounding
    /// the whole request (question + candidate answer + gold + the verdict). Too small and the
    /// verdict truncates to empty and the question is counted failed.</param>
    public AnswerabilityJudge(IBenchLlmClient llm, LlmResponseCache cache,
        string providerId, string modelSlug, int judgeK,
        int answerMaxTokens = 8192, int graderMaxTokens = 2048, FailureSink? failureSink = null)
    {
        _llm = llm;
        _cache = cache;
        _providerId = providerId;
        _modelSlug = modelSlug;
        JudgeK = judgeK;
        _answerMaxTokens = answerMaxTokens;
        _graderMaxTokens = graderMaxTokens;
        _failureSink = failureSink;
    }

    /// <summary>Path of the per-run failures NDJSON drain, if one was attached; else null.</summary>
    public string? FailuresPath => _failureSink?.FilePath;

    public int JudgeK { get; }
    public int JudgedCount => _judgedCount;
    public int NoGoldCount => _noGoldCount;
    public int FailedCount => _failedCount;

    /// <summary>
    /// Returns null when the question has no gold answer OR a judge call failed (both
    /// excluded from the aggregate; the reason is logged). Non-null = a real verdict.
    /// </summary>
    public async Task<JudgeVerdict?> JudgeAsync(
        string questionId, string question, string? goldAnswer,
        IReadOnlyList<string> retrievedContents, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(goldAnswer))
        {
            Interlocked.Increment(ref _noGoldCount);
            return null;
        }

        var distinctContents = DistinctTopK(retrievedContents, JudgeK);

        string? answer = await GetOrCallAsync(
            AnswerOp,
            LlmResponseCache.Hash(string.Join("|", _modelSlug, question, JudgeK, string.Join("", distinctContents))),
            JudgePrompts.AnswererSystem, JudgePrompts.AnswererUser(question, distinctContents), _answerMaxTokens,
            failureLabel: "answerer",
            questionId, ct).ConfigureAwait(false);
        if (answer is null) return null;

        string? graderReply = await GetOrCallAsync(
            GradeOp,
            LlmResponseCache.Hash(string.Join("|", _modelSlug, question, answer, goldAnswer)),
            JudgePrompts.GraderSystem, JudgePrompts.GraderUser(question, answer, goldAnswer), _graderMaxTokens,
            failureLabel: "grader",
            questionId, ct).ConfigureAwait(false);
        if (graderReply is null) return null;

        bool correct = JudgePrompts.ParseGrade(graderReply);
        Interlocked.Increment(ref _judgedCount);
        return new JudgeVerdict(IsSurfaced(answer), correct, answer, graderReply);
    }

    /// <summary>
    /// Cache-through call: on hit (and the cached value is non-blank) returns the cached
    /// string; otherwise calls the model (system + user prompt, <paramref name="maxTokens"/>) up to
    /// <see cref="MaxCallAttempts"/> times and returns the first non-blank result, caching it. A
    /// blank result (a 200 with empty content) is retried; a recovery on a later attempt is logged,
    /// and every attempt is counted in a single unit so the retry lines and the final failure line
    /// agree. If EVERY attempt is blank it is logged, tallied, drained to the failure sink (with the
    /// full prompt + every attempt's raw), and never cached (so a resumed run re-attempts it).
    /// </summary>
    private async Task<string?> GetOrCallAsync(
        string op, string hash, string systemPrompt, string userPrompt, int maxTokens,
        string failureLabel, string questionId, CancellationToken ct)
    {
        if (_cache.TryGetString(op, hash, out string? cached) && !string.IsNullOrWhiteSpace(cached))
            return cached;

        var attemptLog = new List<FailureAttempt>();
        for (int attempt = 1; attempt <= MaxCallAttempts; attempt++)
        {
            string? result = await _llm.CompleteAsync(systemPrompt, userPrompt, maxTokens, false, ct).ConfigureAwait(false);
            attemptLog.Add(new FailureAttempt(attempt, result, null));
            if (!string.IsNullOrWhiteSpace(result))
            {
                _cache.PutString(op, hash, result);
                if (attempt > 1)
                    Console.Error.WriteLine(
                        $"[scrinia-bench:judge] {failureLabel} recovered for {questionId} on attempt {attempt}/{MaxCallAttempts}");
                return result;
            }

            if (attempt < MaxCallAttempts)
                Console.Error.WriteLine(
                    $"[scrinia-bench:judge] {failureLabel} blank for {questionId} (attempt {attempt}/{MaxCallAttempts}), retrying");
        }

        Console.Error.WriteLine(
            $"[scrinia-bench:judge] {failureLabel} failed for {questionId}: blank after {MaxCallAttempts} attempts");
        Interlocked.Increment(ref _failedCount);
        _failureSink?.Record(new FailureRecord(
            Role: failureLabel, Id: questionId, Reason: "empty-completion",
            Attempts: MaxCallAttempts, MaxTokens: maxTokens, PromptChars: systemPrompt.Length + userPrompt.Length,
            SystemPrompt: systemPrompt, UserPrompt: userPrompt, AttemptLog: attemptLog));
        return null;
    }

    /// <summary>
    /// Top-<paramref name="k"/> of <paramref name="contents"/>, deduplicated (exact-string
    /// match) while preserving first-seen order; blanks are dropped.
    /// </summary>
    private static List<string> DistinctTopK(IReadOnlyList<string> contents, int k)
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        int limit = Math.Min(k, contents.Count);
        for (int i = 0; i < limit; i++)
        {
            string content = contents[i];
            if (string.IsNullOrWhiteSpace(content)) continue;
            if (seen.Add(content)) result.Add(content);
        }
        return result;
    }
}
