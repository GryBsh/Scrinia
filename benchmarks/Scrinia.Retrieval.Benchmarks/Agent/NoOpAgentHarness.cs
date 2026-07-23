namespace Scrinia.Retrieval.Benchmarks.Agent;

/// <summary>
/// Pass-through <see cref="IAgentHarness"/>. Returns null without calling any LLM.
/// Wired for the bench's baseline runs (bare <c>--agent</c> absent) so the runner code
/// path is identical whether or not an agent is configured — the substrate measurement
/// is the agent-null result.
/// </summary>
public sealed class NoOpAgentHarness : IAgentHarness
{
    public static NoOpAgentHarness Instance { get; } = new();

    public Task<AgentIngest?> GenerateIngestAsync(string content, CancellationToken ct) =>
        Task.FromResult<AgentIngest?>(null);
}
