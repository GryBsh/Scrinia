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
    public void AgenticIngest_RejectsExplicitRerank()
    {
        string[] args = ["locomo", "--data", "x.json", "--agentic-ingest", "--agent-provider", "openai", "--rerank", "cross-encoder"];
        var act = async () => await RetrieverFactory.BuildAsync(args, "locomo", default);
        act.Should().ThrowAsync<ArgumentException>().WithMessage("*rerank*");
    }

    [Fact]
    public void EpisodeMaxTurns_ParsesDefault16()
    {
        RetrieverFactory.EpisodeMaxTurns(["--x", "y"]).Should().Be(16);
        RetrieverFactory.EpisodeMaxTurns(["--episode-max-turns", "8"]).Should().Be(8);
    }

    [Fact]
    public void EpisodeMaxTokens_ParsesDefault4096()
    {
        // Guards the truncation fix: the per-turn budget must default well above the old
        // 1024 so a tool call carrying real memory content isn't cut off mid-arguments.
        RetrieverFactory.EpisodeMaxTokens(["--x", "y"]).Should().Be(4096);
        RetrieverFactory.EpisodeMaxTokens(["--episode-max-tokens", "8192"]).Should().Be(8192);
    }
}
