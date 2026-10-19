using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Scrinia.Agent.Benchmarks;

public sealed record ModelSettings(Uri Endpoint, string Model,
    string ApiKeyEnvironmentVariable, float Temperature, int MaxOutputTokens, float? TopP = null, int? TopK = null);
public sealed record RunLimits(int MaxModelTurnsPerEpisode, int EpisodeSeconds,
    int CaseSeconds, int MaxModelAttempts);
public sealed record ScriniaSettings(string PayloadDirectory, string Executable,
    string[] PrefixArguments, Dictionary<string, string> WorkspaceConfig, string? HomeSeedDirectory);
public sealed record AttemptPaths(string Root, string Workspace, string Home, string Evidence);
public sealed record UsageTotals(long? InputTokens, long? OutputTokens);
public sealed record EpisodeResult(string FinalText, UsageTotals Usage, int ModelRequests, int ToolCalls, int ToolErrors, string? ReportedModelId = null);
public interface IRunEvents : IAsyncDisposable
{
    ValueTask WriteAsync(string phase, string kind, JsonElement data, CancellationToken ct);
}
#pragma warning disable CA1707 // Preserve existing machine-readable V1 failure codes.
public enum BenchmarkFailureCode
{
    INPUT_INVALID, PREFLIGHT_FAILED, EPISODE_DEADLINE, CASE_DEADLINE, PROVIDER_FAILED, PROVIDER_UNAVAILABLE,
    RESPONSE_TRUNCATED, MCP_DISCONNECTED, TOOL_COMPLETION_UNCERTAIN, JOURNAL_FAILED, CATALOG_DRIFT, MODEL_DRIFT
}
#pragma warning restore CA1707
public sealed class BenchmarkFailureException(BenchmarkFailureCode code, string message, Exception? inner = null) : Exception(message, inner)
{
    public BenchmarkFailureCode Code { get; } = code;
}

public sealed record RuntimeEpisodeRequest(string Phase, string Instructions,
    IReadOnlyList<ChatMessage> Messages, IReadOnlySet<string> AllowedToolNames,
    bool EnableResponseRecovery, bool? EnableThinking,
    Action<string, AIFunctionArguments>? ValidateToolCall, Action<ChatResponse>? ValidateResponse = null);
