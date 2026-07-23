using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Agent;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// The no-op harness must return null from its ingest op so the runner's baseline path
/// (no LLM, purely mechanical ingestion) is exercised identically to the harness-on path.
/// </summary>
public class NoOpAgentHarnessTests
{
    [Fact]
    public async Task GenerateIngestAsync_ReturnsNull()
    {
        var result = await NoOpAgentHarness.Instance.GenerateIngestAsync(
            "any content here", CancellationToken.None);
        result.Should().BeNull();
    }
}
