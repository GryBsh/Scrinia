using FluentAssertions;

namespace Scrinia.Tests.Shared;

public sealed class SharedCompactionRetentionTests
{
    [Fact]
    public async Task KeepRecentRetainsOnlyRequestedNewestChunks()
    {
        using var fixture = new SharedOperationFixture();
        await fixture.Tools.Remember("team", "/events", ["old fact", "middle fact", "new fact"], description: "Event history.");

        (await fixture.Tools.Compact("team", "/events", keepRecent: 1)).Should().Contain("mutation: applied");
        string recalled = await fixture.Tools.Recall("team", "/events");

        recalled.Should().Contain("new fact").And.NotContain("old fact").And.NotContain("middle fact");
    }
}
