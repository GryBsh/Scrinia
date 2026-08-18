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
    public async Task Reconcile_RejectsAChoiceWithNoConflictId()
    {
        using var scope = new TestHelpers.StoreScope();
        var response = await Tools().Reconcile(conflictId: null, choice: "ours",
            content: null, CancellationToken.None);

        response.Status.Should().Be("error",
            "silently performing a read-only scan while reporting action: reconciled "
            + "tells the caller their choice was applied");
    }

    [Fact]
    public async Task Reconcile_StillScansWhenNeitherArgumentIsGiven()
    {
        using var scope = new TestHelpers.StoreScope();
        var response = await Tools().Reconcile(conflictId: null, choice: null,
            content: null, CancellationToken.None);

        response.Status.Should().NotBe("error");
    }
}
