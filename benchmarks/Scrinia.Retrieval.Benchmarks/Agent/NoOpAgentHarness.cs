namespace Scrinia.Retrieval.Benchmarks.Agent;

/// <summary>
/// Pass-through <see cref="IAgentHarness"/>. All three methods return null without
/// calling any LLM. Used for the bench's baseline runs (<c>--agent off</c>) so the
/// runner code path is identical whether or not an agent is configured — the
/// substrate measurement is the agent-null result.
/// </summary>
public sealed class NoOpAgentHarness : IAgentHarness
{
    public static NoOpAgentHarness Instance { get; } = new();

    public Task<IReadOnlyList<string>?> GenerateAnchorsAsync(string content, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>?>(null);

    public Task<string?> RewriteQueryAsync(string query, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    public Task<string?> GenerateHypotheticalAsync(string query, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}
