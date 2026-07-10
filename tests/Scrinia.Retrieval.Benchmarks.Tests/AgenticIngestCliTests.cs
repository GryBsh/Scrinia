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
    public void EpisodeMaxTokens_ParsesDefault32768()
    {
        // The default must clear a whole episode, not one tool call: on an OpenAI-compatible
        // server max_tokens bounds prompt+generation together, and the ingest prompt (guide +
        // full session transcript + tool schemas, then growing each turn with recalls/search
        // hits) starts near 5k tokens. 4096 could not even fit turn one; the default is now
        // sized to a server context window.
        RetrieverFactory.EpisodeMaxTokens(["--x", "y"]).Should().Be(32768);
        RetrieverFactory.EpisodeMaxTokens(["--episode-max-tokens", "8192"]).Should().Be(8192);
    }
}
