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
    public async Task BuildJudge_RequiresProvider_WhenJudgeEnabled()
    {
        var act = async () => await RetrieverFactory.BuildJudgeAsync(["locomo", "--judge"], default);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*judge-provider*");
    }

    [Fact]
    public void JudgeMaxTokens_ParseDefaultsAndOverrides()
    {
        RetrieverFactory.JudgeAnswerMaxTokens(["--x", "y"]).Should().Be(8192);
        RetrieverFactory.JudgeAnswerMaxTokens(["--judge-answer-max-tokens", "2048"]).Should().Be(2048);
        RetrieverFactory.JudgeGradeMaxTokens(["--x", "y"]).Should().Be(2048);
        RetrieverFactory.JudgeGradeMaxTokens(["--judge-grade-max-tokens", "1024"]).Should().Be(1024);
    }
}
