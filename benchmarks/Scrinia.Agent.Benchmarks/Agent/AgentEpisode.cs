using Microsoft.Extensions.AI;

namespace Scrinia.Agent.Benchmarks;

public enum EpisodePhase { Learn, Answer }
public sealed record EpisodeInput(EpisodePhase Phase, string UserMessage);
public interface IAgentEpisode
{
    Task<EpisodeResult> RunAsync(EpisodeInput input, ScriniaMcpSession session, ModelSettings settings,
        RunLimits limits, IRunEvents events, CancellationToken ct);
}
public sealed class AgentEpisode(Func<ModelSettings, IChatClient> createClient, string? expectedModelId = null) : IAgentEpisode
{
    public async Task<EpisodeResult> RunAsync(EpisodeInput input, ScriniaMcpSession session, ModelSettings settings,
        RunLimits limits, IRunEvents events, CancellationToken ct)
    {
        var request = new RuntimeEpisodeRequest(input.Phase == EpisodePhase.Learn ? "learn" : "answer",
            AgentInstructions.ForPhase(input.Phase), [new(ChatRole.User, input.UserMessage)],
            session.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal), true, null, null);
        var result = await new RuntimeAgentEpisode(createClient, expectedModelId).RunAsync(request, session, settings, limits, events, ct);
        if (input.Phase == EpisodePhase.Answer && string.IsNullOrWhiteSpace(result.FinalText))
            throw new InvalidDataException("Agent did not produce a complete final response.");
        return result;
    }
}
