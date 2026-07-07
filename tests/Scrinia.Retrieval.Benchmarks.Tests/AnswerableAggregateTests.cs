using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Core;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class AnswerableAggregateTests
{
    private static QuestionResult Q(bool? answerable) =>
        new("id", "q", "cat", [], [], [], 0, 0, 0, 0, Answerable: answerable);

    [Fact]
    public void Aggregate_AveragesAnswerable_OverJudgedOnly()
    {
        // 2 judged (one true, one false) + 1 un-judged (null) -> 0.5, not 1/3.
        var agg = BenchmarkScorer.Aggregate([Q(true), Q(false), Q(null)]);
        agg.Answerable.Should().Be(0.5);
    }

    [Fact]
    public void Aggregate_Answerable_IsZero_WhenNoneJudged()
    {
        var agg = BenchmarkScorer.Aggregate([Q(null), Q(null)]);
        agg.Answerable.Should().Be(0.0);
    }
}
