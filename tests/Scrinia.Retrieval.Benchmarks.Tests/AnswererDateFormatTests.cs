using Scrinia.Retrieval.Benchmarks.Core;
using Xunit;

public class AnswererDateFormatTests
{
    [Fact]
    public void Apply_prefixes_known_date_and_passes_null_through()
    {
        var d = new DateTimeOffset(2023, 5, 4, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("created: 2023-05-04\nfoo", AnswererDateFormat.Apply("foo", d));
        Assert.Equal("foo", AnswererDateFormat.Apply("foo", null));
    }

    [Fact]
    public void Present_detects_leading_and_embedded_created_lines()
    {
        Assert.True(AnswererDateFormat.Present("created: 2023-05-04\nfoo"));
        Assert.True(AnswererDateFormat.Present("head\ncreated: 2023-05-04\nfoo"));
        Assert.False(AnswererDateFormat.Present("foo bar"));
    }
}
