using FluentAssertions;
using Scrinia.Core.Resilience;
using Xunit;

namespace Scrinia.Tests;

public class RemoteTransportGuardTests
{
    private static List<string> Run(string url, bool hasApiKey, bool allowInsecure)
    {
        var warnings = new List<string>();
        RemoteTransportGuard.Validate(new Uri(url), hasApiKey, allowInsecure, warnings.Add);
        return warnings;
    }

    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://[::1]:5000")]
    public void Loopback_WithKey_IsAllowedSilently(string url)
    {
        var warnings = Run(url, hasApiKey: true, allowInsecure: false);
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Https_WithKey_IsAllowedSilently()
    {
        var warnings = Run("https://example.com", hasApiKey: true, allowInsecure: false);
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void NonLoopbackHttp_WithKey_NoOptIn_Throws()
    {
        var act = () => Run("http://10.0.0.5:5000", hasApiKey: true, allowInsecure: false);
        act.Should().Throw<InsecureTransportException>();
    }

    [Fact]
    public void NonLoopbackHttp_WithKey_OptIn_WarnsAndProceeds()
    {
        var warnings = Run("http://10.0.0.5:5000", hasApiKey: true, allowInsecure: true);
        warnings.Should().ContainSingle();
    }

    [Fact]
    public void NonLoopbackHttp_NoKey_IsAllowed()
    {
        var warnings = Run("http://10.0.0.5:5000", hasApiKey: false, allowInsecure: false);
        warnings.Should().BeEmpty();
    }
}
