using Microsoft.Extensions.AI;
using System.ClientModel;
using System.Text.Json;

namespace Scrinia.Agent.Benchmarks;

public sealed record GradeResult(bool? Correct, string RawResponse, UsageTotals Usage, string? FailureCode, string? ReportedModelId = null);
public interface IAnswerGrader
{
    Task<GradeResult> GradeAsync(EvaluationInput evaluation, AnswerInput input, AnswerRecord answer,
        ModelSettings settings, RunLimits limits, IRunEvents events, CancellationToken ct);
}
public sealed class AnswerGrader(Func<ModelSettings, IChatClient> createClient, string? expectedModelId = null) : IAnswerGrader
{
    public async Task<GradeResult> GradeAsync(EvaluationInput evaluation, AnswerInput input, AnswerRecord answer,
        ModelSettings settings, RunLimits limits, IRunEvents events, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        AtomicJsonFile.ValidateAnswerHash(answer.Text, answer.Sha256);
        string prompt = LongMemEvalRubric.Render(evaluation, input, answer.Text);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(limits.EpisodeSeconds));
        using var client = new ObservedChatClient(createClient(settings), settings, limits, events, "judge", expectedModelId);
        GradeResult result;
        try
        {
            var response = await client.GetResponseAsync([new(ChatRole.User, prompt)], new ChatOptions(), deadline.Token);
            string raw = response.Text;
            await events.WriteAsync("judge", "judge-raw", JsonSerializer.SerializeToElement(raw, ConfigurationJsonContext.Default.String), ct);
            bool hasTools = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Any();
            bool? correct = hasTools ? null : LongMemEvalRubric.ParseVerdict(raw);
            result = new(correct, raw, client.Usage, hasTools ? "JUDGE_TOOL_CALL" : correct is null ? "JUDGE_INVALID_VERDICT" : null, client.ReportedModelId);
        }
        catch (Exception e) when (e is HttpRequestException or ClientResultException or EpisodeLimitException or InvalidDataException)
        {
            result = new(null, "", client.Usage, "JUDGE_REQUEST_FAILED", client.ReportedModelId);
        }
        await events.WriteAsync("judge", "judge-verdict", JsonSerializer.SerializeToElement(result, RecordJsonContext.Default.GradeResult), ct);
        return result;
    }
}
