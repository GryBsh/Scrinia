using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Cli;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class AgenticIngestCliTests
{
    [Fact]
    public void AgenticIngest_RequiresAgentProvider()
    {
        string[] args = ["locomo", "--data", "x.json", "--agentic-ingest"];
        var act = async () => await RetrieverFactory.BuildAsync(args, "locomo", default);
        act.Should().ThrowAsync<ArgumentException>().WithMessage("*agent-provider*");
    }

    [Fact]
    public void EpisodeMaxTurns_ParsesDefault16()
    {
        RetrieverFactory.EpisodeMaxTurns(["--x", "y"]).Should().Be(16);
        RetrieverFactory.EpisodeMaxTurns(["--episode-max-turns", "8"]).Should().Be(8);
    }
}
