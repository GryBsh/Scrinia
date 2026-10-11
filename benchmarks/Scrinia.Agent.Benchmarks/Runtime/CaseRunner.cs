using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Scrinia.Agent.Benchmarks;

public sealed class CaseRunner(IAgentEpisode episode, IAnswerGrader grader)
{
    public async Task<CaseOutcome> RunAsync(BenchmarkCase item, int ordinal, int attempt,
        RunConfiguration config, RunJournal journal, string runRoot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var previous = (await journal.ReadOutcomesAsync(ct)).SingleOrDefault(o => o.QuestionId == item.Evaluation.QuestionId);
        if (previous?.State is CaseState.Correct or CaseState.Incorrect) return previous;
        if (previous is not null && attempt <= previous.Attempt) throw new InvalidDataException("A retry needs a fresh attempt number.");
        AnswerRecord? answer = previous?.AnswerSha256 is { } hash && previous.State is CaseState.Answered or CaseState.GradingFailed or CaseState.Cancelled
            ? journal.ReadAnswer(journal.PathsFor(ordinal, previous.Attempt), hash) : null;
        var paths = new AttemptWorkspace().Create(runRoot, ordinal, attempt, config.Scrinia);
        var events = new RequiredEvents(journal.CreateEvents(paths));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(config.Limits.CaseSeconds));
        string phase = answer is null ? "learn" : "judge";
        bool saved = false;
        await events.WriteAsync(phase, "attempt-start", ChatEvidence.Build(w =>
        {
            w.WriteNumber("caseSeconds", config.Limits.CaseSeconds);
            w.WriteNumber("episodeSeconds", config.Limits.EpisodeSeconds);
            w.WriteBoolean("gradingOnly", answer is not null);
            if (previous is not null) w.WriteNumber("previousAttempt", previous.Attempt);
        }), ct);
        CaseOutcome outcome;
        try
        {
            if (answer is null)
            {
                foreach (var history in item.Sessions) await RunEpisode(AgentInstructions.Learn(history));
                phase = "answer";
                var result = await RunEpisode(AgentInstructions.Answer(item.Answer));
                if (string.IsNullOrWhiteSpace(result.FinalText))
                    throw new BenchmarkFailureException(BenchmarkFailureCode.RESPONSE_TRUNCATED, "The final answer is empty.");
                answer = new(result.FinalText, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(result.FinalText))));
            }
            await Persist(() => journal.SaveAnswerAsync(paths, answer, deadline.Token));
            saved = true;
            phase = "judge";
            var grade = await Timed(async token => await grader.GradeAsync(item.Evaluation, item.Answer, answer,
                config.Judge, config.Limits, events, token));
            if (grade.FailureCode != "JUDGE_REQUEST_FAILED") CheckModel(journal.Manifest, "judgeModel", grade.ReportedModelId);
            await Persist(() => new AtomicJsonFile().WriteAsync(Path.Combine(paths.Evidence, "grade.json"),
                JsonSerializer.SerializeToUtf8Bytes(grade, RecordJsonContext.Default.GradeResult), deadline.Token));
            outcome = new(item.Evaluation.QuestionId, attempt, grade.Correct switch { true => CaseState.Correct, false => CaseState.Incorrect, _ => CaseState.GradingFailed },
                answer.Sha256, grade.Correct is null ? phase : null, grade.FailureCode);
        }
        catch (BenchmarkFailureException error) when (error.Code is BenchmarkFailureCode.JOURNAL_FAILED or BenchmarkFailureCode.CATALOG_DRIFT or BenchmarkFailureCode.MODEL_DRIFT)
        { throw; }
        catch (Exception error)
        {
            string code = error switch
            {
                OperationCanceledException when ct.IsCancellationRequested => "CANCELLED",
                OperationCanceledException when deadline.IsCancellationRequested => "CASE_DEADLINE",
                OperationCanceledException => "EPISODE_DEADLINE",
                BenchmarkFailureException typed => typed.Code.ToString(),
                EpisodeLimitException => "EPISODE_LIMIT",
                InvalidDataException => "RESPONSE_TRUNCATED",
                ModelContextProtocol.McpException => "MCP_DISCONNECTED",
                _ => "PROVIDER_FAILED"
            };
            outcome = new(item.Evaluation.QuestionId, attempt, ct.IsCancellationRequested ? CaseState.Cancelled : saved ? CaseState.GradingFailed : CaseState.ExecutionFailed,
                saved ? answer!.Sha256 : null, phase, code);
            await events.WriteAsync(phase, "failure", ChatEvidence.Build(w => { w.WriteString("code", code); w.WriteString("errorType", error.GetType().Name); }), CancellationToken.None);
        }
        await Persist(() => journal.CommitOutcomeAsync(paths, outcome, CancellationToken.None));
        return outcome;

        async Task<EpisodeResult> RunEpisode(EpisodeInput input) => await Timed(async token =>
        {
            await using var session = await ScriniaMcpSession.OpenAsync(config.Scrinia, paths, events, token);
            if (session.CatalogHash != journal.Manifest.ContentHashes["mcpCatalog"])
                throw new BenchmarkFailureException(BenchmarkFailureCode.CATALOG_DRIFT, "MCP tool catalog changed.");
            var result = await episode.RunAsync(input, session, config.Agent, config.Limits, events, token);
            CheckModel(journal.Manifest, "agentModel", result.ReportedModelId);
            return result;
        });
        async Task<T> Timed<T>(Func<CancellationToken, Task<T>> action)
        {
            using var episodeDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            episodeDeadline.CancelAfter(TimeSpan.FromSeconds(config.Limits.EpisodeSeconds));
            long start = Stopwatch.GetTimestamp();
            await events.WriteAsync(phase, "phase-start", ChatEvidence.Build(_ => { }), episodeDeadline.Token);
            try
            {
                var result = await action(episodeDeadline.Token);
                episodeDeadline.Token.ThrowIfCancellationRequested();
                return result;
            }
            finally
            {
                await events.WriteAsync(phase, "phase-end", ChatEvidence.Build(w => w.WriteNumber("durationMs", Stopwatch.GetElapsedTime(start).TotalMilliseconds)), CancellationToken.None);
            }
        }
    }

    internal static void CheckModel(RunManifest manifest, string key, string? actual)
    {
        if (manifest.ContentHashes.TryGetValue(key, out var expected) && expected != (actual ?? "unavailable"))
            throw new BenchmarkFailureException(BenchmarkFailureCode.MODEL_DRIFT, "Reported model identity changed.");
    }
    internal static async Task Persist(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new BenchmarkFailureException(BenchmarkFailureCode.JOURNAL_FAILED, "Run evidence could not be persisted.", error); }
    }
}

internal sealed class RequiredEvents(IRunEvents inner) : IRunEvents
{
    public async ValueTask WriteAsync(string phase, string kind, JsonElement data, CancellationToken ct) =>
        await CaseRunner.Persist(async () => await inner.WriteAsync(phase, kind, data, ct));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask; // The journal owns the sink.
}
public enum BenchmarkFailureCode
{
    INPUT_INVALID, PREFLIGHT_FAILED, EPISODE_DEADLINE, CASE_DEADLINE, PROVIDER_FAILED,
    RESPONSE_TRUNCATED, MCP_DISCONNECTED, TOOL_COMPLETION_UNCERTAIN, JOURNAL_FAILED, CATALOG_DRIFT, MODEL_DRIFT
}
public sealed class BenchmarkFailureException(BenchmarkFailureCode code, string message, Exception? inner = null) : Exception(message, inner)
{
    public BenchmarkFailureCode Code { get; } = code;
}
