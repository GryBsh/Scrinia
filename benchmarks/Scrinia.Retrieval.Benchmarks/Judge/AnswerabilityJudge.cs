using Scrinia.Retrieval.Benchmarks.Llm;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;

namespace Scrinia.Retrieval.Benchmarks.Judge;

/// <summary>
/// A single answerability verdict produced by <see cref="AnswerabilityJudge"/>: whether
/// the retrieved context let the answerer LLM produce a response the grader LLM judged
/// as matching the gold answer, plus the raw answerer/grader text for debugging.
/// </summary>
public sealed record JudgeVerdict(bool Answerable, string Answer, string GraderVerdict);

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

    private readonly IBenchLlmClient _llm;
    private readonly LlmResponseCache _cache;
    private readonly string _providerId;
    private readonly string _modelSlug;

    private int _judgedCount;
    private int _noGoldCount;
    private int _failedCount;

    public AnswerabilityJudge(IBenchLlmClient llm, LlmResponseCache cache,
        string providerId, string modelSlug, int judgeK)
    {
        _llm = llm;
        _cache = cache;
        _providerId = providerId;
        _modelSlug = modelSlug;
        JudgeK = judgeK;
    }

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
            () => _llm.CompleteAsync(JudgePrompts.AnswererSystem, JudgePrompts.AnswererUser(question, distinctContents), 512, false, ct),
            failureLabel: "answerer",
            questionId).ConfigureAwait(false);
        if (answer is null) return null;

        string? graderReply = await GetOrCallAsync(
            GradeOp,
            LlmResponseCache.Hash(string.Join("|", _modelSlug, question, answer, goldAnswer)),
            () => _llm.CompleteAsync(JudgePrompts.GraderSystem, JudgePrompts.GraderUser(question, answer, goldAnswer), 16, false, ct),
            failureLabel: "grader",
            questionId).ConfigureAwait(false);
        if (graderReply is null) return null;

        bool answerable = JudgePrompts.ParseGrade(graderReply);
        Interlocked.Increment(ref _judgedCount);
        return new JudgeVerdict(answerable, answer, graderReply);
    }

    /// <summary>
    /// Cache-through call: on hit (and the cached value is non-blank) returns the cached
    /// string; on miss (or a defensively-treated blank cache entry) invokes <paramref name="call"/>,
    /// caching the result only if it's non-blank. A blank/null result is logged, tallied
    /// as a failure, and never cached.
    /// </summary>
    private async Task<string?> GetOrCallAsync(
        string op, string hash, Func<Task<string?>> call, string failureLabel, string questionId)
    {
        if (_cache.TryGetString(op, hash, out string? cached) && !string.IsNullOrWhiteSpace(cached))
            return cached;

        string? result = await call().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result))
        {
            Console.Error.WriteLine($"[scrinia-bench:judge] {failureLabel} failed for {questionId}: null/blank completion");
            Interlocked.Increment(ref _failedCount);
            return null;
        }

        _cache.PutString(op, hash, result);
        return result;
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
