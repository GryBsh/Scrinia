using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public class PathContainmentTests
{
    [Theory]
    [InlineData("/root/.scrinia", "/root/.scrinia/a.md", true)]
    [InlineData("/root/.scrinia", "/root/.scrinia", true)]
    [InlineData("/root/.scrinia", "/root/.scrinia-backup/x", false)] // sibling sharing the prefix
    [InlineData("/root/.scrinia", "/root/other/x", false)]
    public void IsWithin_RespectsDirectoryBoundary(string baseDir, string candidate, bool expected)
    {
        Assert.Equal(expected, PathContainment.IsWithin(baseDir, candidate));
    }
}
