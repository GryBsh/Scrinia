using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Server.Tests.Conformance;

/// <summary>
/// The test that would have caught the original defect: the same query, the same corpus, both
/// store implementations, identical result sets. Assertions are ranking-independent by
/// construction — see StoreConformanceFixture for why order can legitimately differ.
/// </summary>
[Collection(nameof(StoreConformanceCollection))]
public sealed class StoreConformanceTests(StoreConformanceFixture fx)
{
    /// <summary>HttpMemoryStore.SearchAll sends synchronously (HttpClient.Send), which TestServer's
    /// in-memory handler refuses. This asserts the fixture's Kestrel loopback host is really in
    /// play, because every other test in this file depends on it.</summary>
    [Fact]
    public void Remote_SynchronousSend_Works()
    {
        var results = fx.Remote.SearchAll("widget", scopes: null, limit: 50);

        results.Should().NotBeEmpty(because: "the fixture seeded 25 entries all containing 'widget'");
    }

    /// <summary>Every InlineData query here must legitimately match something in the seeded
    /// corpus — a query that matches nothing compares an empty set to an empty set and cannot
    /// catch a cross-store parity regression; see <see cref="NoMatches_BothStores_ReturnEmpty"/>
    /// for that case, tested honestly on its own terms.</summary>
    [Theory]
    [InlineData("widget")]
    [InlineData("alpha")]
    [InlineData("planning")]
    public void Unfiltered_BothStores_ReturnSameEntries(string query)
    {
        var local = StoreConformanceFixture.Names(fx.Local,
            fx.Local.SearchAll(query, scopes: null, limit: 100));
        var remote = StoreConformanceFixture.Names(fx.Remote,
            fx.Remote.SearchAll(query, scopes: null, limit: 100));

        remote.Should().BeEquivalentTo(local);
    }

    /// <summary>A query matching nothing must come back empty from both stores — not "empty from
    /// one and junk from the other". Split out of the parity theory, where it could only ever have
    /// compared an empty set to an empty set.</summary>
    [Fact]
    public void NoMatches_BothStores_ReturnEmpty()
    {
        fx.Local.SearchAll("nonexistentterm", scopes: null, limit: 100).Should().BeEmpty();
        fx.Remote.SearchAll("nonexistentterm", scopes: null, limit: 100).Should().BeEmpty();
    }

    /// <summary>All 25 seeded entries match "widget", so a limit of 5 must fill exactly — under-filling
    /// is the exact defect class this branch exists to fix, so this assertion must be exact, not an
    /// upper bound.</summary>
    [Fact]
    public void Unfiltered_LimitIsHonoured_ByBothStores()
    {
        fx.Local.SearchAll("widget", scopes: null, limit: 5).Should().HaveCount(5);
        fx.Remote.SearchAll("widget", scopes: null, limit: 5).Should().HaveCount(5);
    }

    [Fact]
    public void PathPrefix_BothStores_ReturnSameEntries()
    {
        var local = StoreConformanceFixture.Names(fx.Local,
            fx.Local.SearchAll("widget", null, 100, null, null, null, "/api/", null));
        var remote = StoreConformanceFixture.Names(fx.Remote,
            fx.Remote.SearchAll("widget", null, 100, null, null, null, "/api/", null));

        remote.Should().BeEquivalentTo(local);
        remote.Should().OnlyContain(n => n.StartsWith("/api/"));
    }

    [Fact]
    public void ExcludeTopics_BothStores_ReturnSameEntries()
    {
        var local = StoreConformanceFixture.Names(fx.Local,
            fx.Local.SearchAll("widget", null, 100, "G-1"));
        var remote = StoreConformanceFixture.Names(fx.Remote,
            fx.Remote.SearchAll("widget", null, 100, "G-1"));

        remote.Should().BeEquivalentTo(local);
    }

    /// <summary>The fill-to-limit case the interface's own doc comment describes and its default
    /// implementation broke: 25 seeded entries all match "widget", only 10 sit under /api/. A
    /// limit of 20 must return all 10 matching entries, not the /api/ remnant of an unfiltered
    /// top-20. Ranking-independent: 10 &lt; 20, so every match fits regardless of order.</summary>
    [Fact]
    public void PathPrefix_FillsToLimit_NotThePostLimitRemnant()
    {
        var remote = StoreConformanceFixture.Names(fx.Remote,
            fx.Remote.SearchAll("widget", null, 20, null, null, null, "/api/", null));

        remote.Should().HaveCount(10);
    }
}
