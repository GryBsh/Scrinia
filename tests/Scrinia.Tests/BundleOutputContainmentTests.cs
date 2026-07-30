using Scrinia.Commands;
using FluentAssertions;
using Xunit;

namespace Scrinia.Tests;

public sealed class BundleOutputContainmentTests
{
    private static readonly string Exports = Path.Combine(Path.GetTempPath(), "exp-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultOrPlainName_ResolvesInsideExports()
    {
        ScriniaCommands.TryResolveBundlePath(Exports, "my-topic", out string path, out string? err)
            .Should().BeTrue();
        err.Should().BeNull();
        Scrinia.Core.PathContainment.IsWithin(Exports, path).Should().BeTrue();
    }

    [Theory]
    [InlineData("../../escape")]
    [InlineData("/abs/escape")]
    public void TraversalOrAbsolute_IsRejected(string output)
    {
        ScriniaCommands.TryResolveBundlePath(Exports, output, out _, out string? err)
            .Should().BeFalse();
        err.Should().NotBeNull();
    }
}
