using FluentAssertions;
using Xunit;

namespace Scrinia.Merge.Tests;

public sealed class KeywordJaccardTests
{
    [Fact]
    public void Jaccard_IdenticalSets_Returns1()
    {
        var keywords = new[] { "auth", "token", "api" };

        double result = KeywordJaccard.Compute(keywords, keywords);

        result.Should().Be(1.0);
    }

    [Fact]
    public void Jaccard_DisjointSets_Returns0()
    {
        var a = new[] { "auth", "token" };
        var b = new[] { "database", "migration" };

        double result = KeywordJaccard.Compute(a, b);

        result.Should().Be(0.0);
    }

    [Fact]
    public void Jaccard_PartialOverlap_ReturnsCorrect()
    {
        // {a,b,c} vs {b,c,d} → intersection=2, union=4 → 0.5
        var a = new[] { "a", "b", "c" };
        var b = new[] { "b", "c", "d" };

        double result = KeywordJaccard.Compute(a, b);

        result.Should().Be(0.5);
    }

    [Fact]
    public void Jaccard_BothEmpty_Returns1()
    {
        double resultNull = KeywordJaccard.Compute(null, null);
        double resultEmpty = KeywordJaccard.Compute([], []);

        resultNull.Should().Be(1.0);
        resultEmpty.Should().Be(1.0);
    }

    [Fact]
    public void Jaccard_CaseInsensitive()
    {
        var a = new[] { "Auth", "TOKEN" };
        var b = new[] { "auth", "token" };

        double result = KeywordJaccard.Compute(a, b);

        result.Should().Be(1.0);
    }

    [Fact]
    public void Jaccard_OneNull_Returns1()
    {
        // Per implementation: if a or b is null, returns 1.0
        var keywords = new[] { "auth" };

        double result = KeywordJaccard.Compute(keywords, null);

        result.Should().Be(1.0);
    }
}
