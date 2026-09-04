using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

public sealed class UserHomeResolverTests
{
    [Fact]
    public void Resolve_AbsoluteOverride_ReturnsItExactly()
    {
        UserHome.Resolve(@"C:\scrinia-home", @"C:\local-app-data")
            .Should().Be(@"C:\scrinia-home");
    }

    [Fact]
    public void Resolve_UnsetOverride_UsesLocalApplicationDataScrinia()
    {
        UserHome.Resolve(null, @"C:\local-app-data")
            .Should().Be(Path.Combine(@"C:\local-app-data", "Scrinia"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-home")]
    public void Resolve_InvalidOverride_FailsClosed(string value)
    {
        Action resolve = () => UserHome.Resolve(value, @"C:\local-app-data");

        resolve.Should().Throw<ScriniaConfigurationException>();
    }
}
