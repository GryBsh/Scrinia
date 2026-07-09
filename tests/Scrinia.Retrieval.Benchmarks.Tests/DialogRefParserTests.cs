using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Core;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class DialogRefParserTests
{
    [Fact]
    public void BareId_ReturnsSingle() =>
        DialogRefParser.ExtractDialogIds("derived from D2:1 only").Should().Equal("D2:1");

    [Fact]
    public void CommaList_SplitsEach() =>
        DialogRefParser.ExtractDialogIds("ref: D2:1, D2:3, D2:5")
            .Should().Equal("D2:1", "D2:3", "D2:5");

    [Fact]
    public void FullRange_ExpandsInclusive() =>
        DialogRefParser.ExtractDialogIds("ref: D9:1-D9:5")
            .Should().Equal("D9:1", "D9:2", "D9:3", "D9:4", "D9:5");

    [Fact]
    public void ShortRange_ExpandsInclusive() =>
        DialogRefParser.ExtractDialogIds("ref: D9:1-3")
            .Should().Equal("D9:1", "D9:2", "D9:3");

    [Fact]
    public void CrossSessionRange_YieldsEndpointsOnly() =>
        DialogRefParser.ExtractDialogIds("see D9:15-D10:2")
            .Should().Equal("D9:15", "D10:2");

    [Fact]
    public void DescendingRange_YieldsEndpointsOnly() =>
        DialogRefParser.ExtractDialogIds("D3:5-D3:2")
            .Should().Equal("D3:5", "D3:2");

    [Fact]
    public void Duplicates_AreCollapsed_OrderPreserved() =>
        DialogRefParser.ExtractDialogIds("D2:1 and again D2:1 then D2:2")
            .Should().Equal("D2:1", "D2:2");

    [Fact]
    public void NoDialogIds_ReturnsEmpty() =>
        DialogRefParser.ExtractDialogIds("In 2024 they went to Lisbon. No ids here.")
            .Should().BeEmpty();

    [Fact]
    public void NullOrBlank_ReturnsEmpty()
    {
        DialogRefParser.ExtractDialogIds(null).Should().BeEmpty();
        DialogRefParser.ExtractDialogIds("   ").Should().BeEmpty();
    }

    [Fact]
    public void RangeAndList_InOneString_ParsesBoth() =>
        DialogRefParser.ExtractDialogIds("ref: D2:1, D9:1-D9:3")
            .Should().Equal("D2:1", "D9:1", "D9:2", "D9:3");

    [Fact]
    public void IdEmbeddedInLargerToken_IsIgnored() =>
        DialogRefParser.ExtractDialogIds("token ID2:1 xyzD3:4").Should().BeEmpty();
}
