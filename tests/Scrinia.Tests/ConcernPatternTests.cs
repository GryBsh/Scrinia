using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests for concern keyword pattern detection in ConcernAdd.
/// Validates that when 3+ active concerns share a non-noise keyword,
/// the response includes a "Pattern detected" suggestion.
/// </summary>
public sealed class ConcernPatternTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;

    public ConcernPatternTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
    }

    public void Dispose() => _scope.Dispose();

    private async Task InitProject()
    {
        await _tools.ProjectInit("Goals: test concern pattern detection", CancellationToken.None);
    }

    [Fact]
    public async Task ThreeConcernsSharingKeyword_ShowsPattern()
    {
        // Arrange
        await InitProject();

        // Use a distinctive word "authentication" in all 3 descriptions
        // so TextAnalysis extracts it as a keyword for each concern
        await _tools.ConcernAdd(
            "Risk: authentication token expiry not handled in login flow",
            "high", "06", id: "auth-pat-1", CancellationToken.None);

        await _tools.ConcernAdd(
            "Risk: authentication credentials stored in plaintext",
            "high", "06", id: "auth-pat-2", CancellationToken.None);

        // Act — the 3rd concern should trigger pattern detection
        string result = await _tools.ConcernAdd(
            "Risk: authentication session hijacking possible",
            "medium", "06", id: "auth-pat-3", CancellationToken.None);

        // Assert — response should contain pattern suggestion for "authentication"
        result.Should().Contain("Pattern detected",
            "when 3 active concerns share a keyword, pattern detection should fire");
        result.Should().Contain("authentication",
            "the shared keyword 'authentication' should appear in the pattern suggestion");
    }

    [Fact]
    public async Task TwoConcernsSharingKeyword_NoPattern()
    {
        // Arrange
        await InitProject();

        // Only 2 concerns share the distinctive keyword "serialization"
        await _tools.ConcernAdd(
            "Risk: serialization format not validated on input",
            "medium", "06", id: "ser-pat-1", CancellationToken.None);

        // Act
        string result = await _tools.ConcernAdd(
            "Risk: serialization overhead causing latency spikes",
            "low", "06", id: "ser-pat-2", CancellationToken.None);

        // Assert — only 2 concerns share the keyword, threshold is 3
        result.Should().NotContain("Pattern detected",
            "pattern detection should NOT fire when fewer than 3 concerns share a keyword");
    }

    [Fact]
    public async Task NoiseKeywordsExcluded()
    {
        // Arrange
        await InitProject();

        // All 3 concerns share severity:high and phase:06 (noise prefixes)
        // but have completely different content keywords (no overlapping words)
        await _tools.ConcernAdd(
            "Database connection pool exhaustion detected",
            "high", "06", id: "noise-1", CancellationToken.None);

        await _tools.ConcernAdd(
            "Network latency causing upstream timeouts",
            "high", "06", id: "noise-2", CancellationToken.None);

        // Act
        string result = await _tools.ConcernAdd(
            "Memory consumption growing unbounded overnight",
            "high", "06", id: "noise-3", CancellationToken.None);

        // Assert — the shared noise keywords (severity:high, phase:06, status:active)
        // should all be excluded by the noise prefix filter
        result.Should().NotContain("Pattern detected",
            "noise-prefix keywords like severity: and phase: should be excluded from pattern detection");
    }
}
