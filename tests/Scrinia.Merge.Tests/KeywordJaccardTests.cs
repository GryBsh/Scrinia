using Scrinia.Merge;
using FluentAssertions;
using Xunit;

namespace Scrinia.Merge.Tests;

public sealed class KeywordJaccardTests
{
    [Fact]
    public void BothEmpty_IsIdentical()
    {
        KeywordJaccard.Compute(null, null).Should().Be(1.0);
        KeywordJaccard.Compute([], []).Should().Be(1.0);
        KeywordJaccard.Compute(null, []).Should().Be(1.0);
    }

    [Fact]
    public void OneEmptyOnePopulated_IsDisjoint()
    {
        KeywordJaccard.Compute(null, ["auth"]).Should().Be(0.0);
        KeywordJaccard.Compute(["auth"], null).Should().Be(0.0);
        KeywordJaccard.Compute([], ["auth"]).Should().Be(0.0);
    }

    [Fact]
    public void Overlap_IsRatio()
    {
        KeywordJaccard.Compute(["a", "b"], ["a", "b"]).Should().Be(1.0);
        KeywordJaccard.Compute(["a", "b"], ["a", "c"]).Should().BeApproximately(1.0 / 3.0, 1e-9);
    }
}
