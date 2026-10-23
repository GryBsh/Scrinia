using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

public sealed class UserHomeResolverTests
{
    [Fact]
    public void Resolve_AbsoluteOverride_ReturnsItExactly()
    {
        string home = Path.GetFullPath(Path.Combine("test-home", "scrinia"));
        UserHome.Resolve(home, Path.GetFullPath("local-app-data"))
            .Should().Be(home);
    }

    [Fact]
    public void Resolve_UnsetOverride_UsesLocalApplicationDataScrinia()
    {
        string localData = Path.GetFullPath("local-app-data");
        UserHome.Resolve(null, localData)
            .Should().Be(Path.Combine(localData, "Scrinia"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-home")]
    public void Resolve_InvalidOverride_FailsClosed(string value)
    {
        Action resolve = () => UserHome.Resolve(value, Path.GetFullPath("local-app-data"));

        resolve.Should().Throw<ScriniaConfigurationException>();
    }
}
