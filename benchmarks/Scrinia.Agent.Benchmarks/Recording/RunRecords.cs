using System.Text.Json;

namespace Scrinia.Agent.Benchmarks;

public enum CaseState { Pending, Running, Answered, Correct, Incorrect, ExecutionFailed, GradingFailed, Cancelled }
public sealed record UsageTotals(long? InputTokens, long? OutputTokens);
public sealed record AnswerRecord(string Text, string Sha256);
public sealed record CaseOutcome(string QuestionId, int Attempt, CaseState State,
    string? AnswerSha256, string? FailurePhase, string? FailureCode);
public sealed record RunEvent(long Sequence, DateTimeOffset AtUtc, string Phase, string Kind, JsonElement Data);
public sealed record RunManifest(int SchemaVersion, string Fingerprint, string[] SelectedIds,
    JsonElement ResolvedConfiguration, Dictionary<string, string> ContentHashes);
public interface IRunEvents : IAsyncDisposable
{
    ValueTask WriteAsync(string phase, string kind, JsonElement data, CancellationToken ct);
}
internal sealed record OutcomeEnvelope(int SchemaVersion, CaseOutcome Outcome, int EventCount, string EventSha256);
