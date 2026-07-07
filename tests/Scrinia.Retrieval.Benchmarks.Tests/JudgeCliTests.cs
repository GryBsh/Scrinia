using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Cli;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class JudgeCliTests
{
    [Fact]
    public void JudgeK_ParsesDefault10()
    {
        RetrieverFactory.JudgeK(["--x", "y"]).Should().Be(10);
        RetrieverFactory.JudgeK(["--judge-k", "20"]).Should().Be(20);
    }

    [Fact]
    public async Task BuildJudge_ReturnsNull_WhenJudgeFlagAbsent()
    {
        (await RetrieverFactory.BuildJudgeAsync(["locomo", "--data", "x.json"], default)).Should().BeNull();
    }

    [Fact]
    public void BuildJudge_RequiresProvider_WhenJudgeEnabled()
    {
        var act = async () => await RetrieverFactory.BuildJudgeAsync(["locomo", "--judge"], default);
        act.Should().ThrowAsync<ArgumentException>().WithMessage("*judge-provider*");
    }
}
