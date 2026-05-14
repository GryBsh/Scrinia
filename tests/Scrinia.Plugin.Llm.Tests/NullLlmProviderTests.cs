using FluentAssertions;
using Scrinia.Plugin.Llm;

namespace Scrinia.Plugin.Llm.Tests;

/// <summary>
/// Verifies the fallback used when no GGUF is on disk: still answers status truthfully and
/// returns null from CompleteAsync so the host's skip-and-continue path engages cleanly.
/// </summary>
public class NullLlmProviderTests
{
    [Fact]
    public void Defaults_ReportUnavailable()
    {
        var provider = new NullLlmProvider();
        provider.IsAvailable.Should().BeFalse();
        provider.ModelPath.Should().BeEmpty();
        provider.ModelArchitecture.Should().Be("none");
        provider.Hardware.Should().Be("none");
        provider.LastError.Should().BeNull();
    }

    [Fact]
    public void LastError_IsExposed_WhenProvided()
    {
        var provider = new NullLlmProvider("Model file missing.");
        provider.LastError.Should().Be("Model file missing.");
    }

    [Fact]
    public async Task CompleteAsync_AlwaysReturnsNull()
    {
        var provider = new NullLlmProvider();
        var result = await provider.CompleteAsync(
            system: "sys", user: "user", maxTokens: 64, temperature: 0.3,
            stopSequences: null, ct: CancellationToken.None);
        result.Should().BeNull();
    }
}
