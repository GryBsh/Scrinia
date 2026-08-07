using FluentAssertions;
using Scrinia.Core.Resilience;
using Xunit;

namespace Scrinia.Tests;

public class HttpErrorBodyTests
{
    [Fact]
    public void Truncate_ShortBody_IsUnchanged()
    {
        HttpErrorBody.Truncate("boom").Should().Be("boom");
    }

    [Fact]
    public void Truncate_LongBody_IsCutToLimitWithEllipsis()
    {
        string body = new('x', 600);

        string result = HttpErrorBody.Truncate(body);

        result.Should().HaveLength(503, "500 characters plus the three-character ellipsis");
        result.Should().StartWith(new string('x', 500)).And.EndWith("...");
    }

    [Fact]
    public void Truncate_ExactlyAtLimit_IsUnchanged()
    {
        string body = new('x', 500);
        HttpErrorBody.Truncate(body).Should().Be(body, "the cut is strictly greater-than");
    }
}
