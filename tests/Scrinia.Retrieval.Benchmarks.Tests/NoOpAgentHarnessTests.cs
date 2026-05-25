using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Agent;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// The no-op harness must return null from every op so the runner's baseline path
/// (no LLM, no transforms) is exercised identically to the harness-on path.
/// </summary>
public class NoOpAgentHarnessTests
{
    [Fact]
    public async Task GenerateAnchorsAsync_ReturnsNull()
    {
        var result = await NoOpAgentHarness.Instance.GenerateAnchorsAsync(
            "any content here", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task RewriteQueryAsync_ReturnsNull()
    {
        var result = await NoOpAgentHarness.Instance.RewriteQueryAsync(
            "any query here", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GenerateHypotheticalAsync_ReturnsNull()
    {
        var result = await NoOpAgentHarness.Instance.GenerateHypotheticalAsync(
            "any query here", CancellationToken.None);
        result.Should().BeNull();
    }
}
