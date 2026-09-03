using FluentAssertions;
using Scrinia.Mcp;

namespace Scrinia.Tests.Mcp;

public class ToolFamilyConsistencyTests
{
    private static ScriniaMcpTools Tools() => new();

    [Theory]
    [InlineData("ful")]      // misspelling of "full"
    [InlineData("summry")]   // misspelling of "summary"
    [InlineData("verbose")]  // plausible but unsupported
    public async Task List_RejectsAnUnrecognisedMode(string mode)
    {
        using var scope = new TestHelpers.StoreScope();
        var response = await Tools().List(scopes: null, mode: mode, offset: 0, limit: 50,
            excludeTopics: null, CancellationToken.None);

        response.Status.Should().Be("error",
            "falling through to summary reports success for a call that did something else");
        response.Error.Should().Contain(mode);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("full")]
    [InlineData("drift")]
    public async Task List_StillAcceptsEveryDocumentedMode(string mode)
    {
        using var scope = new TestHelpers.StoreScope();
        var response = await Tools().List(scopes: null, mode: mode, offset: 0, limit: 50,
            excludeTopics: null, CancellationToken.None);

        response.Status.Should().NotBe("error");
    }

    [Fact]
    public async Task List_TreatsExplicitNullModeAsSummary()
    {
        // A caller can send "mode": null over JSON-RPC -- distinct from omitting the field, and
        // distinct from a typo. Pre-task behaviour fell through to summary and succeeded; the
        // unknown-mode guard must not have overreached into rejecting this too.
        using var scope = new TestHelpers.StoreScope();
        var response = await Tools().List(scopes: null, mode: null!, offset: 0, limit: 50,
            excludeTopics: null, CancellationToken.None);

        response.Status.Should().NotBe("error");
    }

    [Fact]
    public async Task List_TreatsEmptyModeAsSummary()
    {
        using var scope = new TestHelpers.StoreScope();
        var response = await Tools().List(scopes: null, mode: "", offset: 0, limit: 50,
            excludeTopics: null, CancellationToken.None);

        response.Status.Should().NotBe("error");
    }

}
