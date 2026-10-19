using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using System.Runtime.ExceptionServices;

namespace Scrinia.Agent.Benchmarks;

public sealed class RuntimeAgentEpisode(Func<ModelSettings, IChatClient> createClient, string? expectedModelId = null)
{
    public async Task<EpisodeResult> RunAsync(RuntimeEpisodeRequest input, ScriniaMcpSession session, ModelSettings settings,
        RunLimits limits, IRunEvents events, CancellationToken ct)
    {
        var tools = session.Tools.Where(t => input.AllowedToolNames.Contains(t.Name)).ToArray();
        if (input.AllowedToolNames.Any(name => !tools.Any(t => string.Equals(t.Name, name, StringComparison.Ordinal))))
            throw new InvalidDataException("An allowed tool name is not in the MCP catalog.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, session.FailureToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(limits.EpisodeSeconds));
        string phase = input.Phase;
        var observed = new ObservedChatClient(createClient(settings), settings, limits, events, phase, expectedModelId, enableResponseRecovery: input.EnableResponseRecovery, enableThinking: input.EnableThinking);
        var observation = new ToolObservation(events, phase, deadline, input.ValidateToolCall);
        // This is the SDK's function loop. Configure its ceiling explicitly so its default
        // 40 iterations cannot silently undercut the benchmark's declared request budget.
        using var functions = new FunctionInvokingChatClient(observed)
        {
            AllowConcurrentInvocation = false, MaximumIterationsPerRequest = limits.MaxModelTurnsPerEpisode + 1
        };
        var agent = new ChatClientAgent(functions, new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
            ChatOptions = new()
            {
                Instructions = input.Instructions,
                Tools = tools.Select(t => (AITool)new ObservedTool(t, observation)).ToList()
            }
        });
        try
        {
            var response = await agent.RunAsync(input.Messages, cancellationToken: deadline.Token);
            observation.ThrowIfFailed();
            await session.CheckHealthAsync();
            deadline.Token.ThrowIfCancellationRequested();
            var final = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
            if (final is null || final.Contents.OfType<FunctionCallContent>().Any())
                throw new InvalidDataException("Agent did not produce a complete final response.");
            return new(final.Text, observed.Usage, observed.ModelRequests, observation.Calls, observation.Errors, observed.ReportedModelId);
        }
        catch
        {
            observation.ThrowIfFailed();
            await session.CheckHealthAsync();
            throw;
        }
    }

    private sealed class ToolObservation(IRunEvents events, string phase, CancellationTokenSource abort,
        Action<string, AIFunctionArguments>? validateToolCall)
    {
        public int Calls { get; private set; }
        public int Errors { get; private set; }
        private ExceptionDispatchInfo? _failure;

        public async ValueTask<object?> InvokeAsync(AIFunction tool, AIFunctionArguments arguments, CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                validateToolCall?.Invoke(tool.Name, arguments);
                await events.WriteAsync(phase, "tool-intent", ChatEvidence.Build(w =>
                {
                    w.WriteString("name", tool.Name); w.WritePropertyName("arguments"); ChatEvidence.WriteValue(w, arguments);
                }), ct);
                Calls++;
                object? result;
                try { result = await tool.InvokeAsync(arguments, ct); }
                catch (Exception transportError) when (transportError is not OperationCanceledException)
                { throw new BenchmarkFailureException(BenchmarkFailureCode.TOOL_COMPLETION_UNCERTAIN, "Tool completion is uncertain; the attempt must not replay it.", transportError); }
                bool error = ChatEvidence.IsToolError(result);
                if (error) Errors++;
                await events.WriteAsync(phase, "tool-result", ChatEvidence.Build(w =>
                {
                    w.WriteString("name", tool.Name); w.WriteBoolean("error", error);
                    w.WritePropertyName("result"); ChatEvidence.WriteValue(w, result);
                }), ct);
                return result;
            }
            catch (Exception error)
            {
                // A transport exception can follow a committed write. Abort the episode;
                // never give the SDK another opportunity to replay an ambiguous mutation.
                _failure = ExceptionDispatchInfo.Capture(error);
                try
                {
                    await events.WriteAsync(phase, "tool-error", ChatEvidence.Build(w =>
                    {
                        w.WriteString("name", tool.Name); w.WriteString("errorType", error.GetType().Name);
                    }), CancellationToken.None);
                }
                catch (Exception evidenceError) { _failure = ExceptionDispatchInfo.Capture(evidenceError); }
                finally { await abort.CancelAsync(); }
                _failure.Throw();
                throw;
            }
        }

        public void ThrowIfFailed() => _failure?.Throw();
    }

    private sealed class ObservedTool(AIFunction inner, ToolObservation observation) : DelegatingAIFunction(inner)
    {
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            observation.InvokeAsync(InnerFunction, arguments, cancellationToken);
    }
}
