using Scrinia.Core;
using static Scrinia.Core.RequireTextHelper;

namespace Scrinia.Tests;

public class RequireTextConsolidationTests
{
    [Fact]
    public void Returns_Value_When_Present()
    {
        var value = RequireText("hello", "result");
        Assert.Equal("hello", value);
    }

    [Fact]
    public void Throws_When_Whitespace_With_Canonical_Message()
    {
        var ex = Assert.Throws<ArgumentException>(() => RequireText("   ", "result"));
        Assert.StartsWith("Value cannot be null, empty, or whitespace.", ex.Message);
    }
}
