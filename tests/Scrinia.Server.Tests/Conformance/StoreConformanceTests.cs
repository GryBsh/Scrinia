using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Server.Tests.Conformance;

/// <summary>
/// The test that would have caught the original defect: the same query, the same corpus, both
/// store implementations, identical result sets. Assertions are ranking-independent by
/// construction — see StoreConformanceFixture for why order can legitimately differ.
///
/// <para><b>TopicResult is excluded from cross-store equivalence checks.</b> A discovered
/// pre-existing defect, unrelated to this branch's filtering/overload-collapsing work:
/// <c>HttpMemoryStore.SearchAll</c> (<c>src/Scrinia/HttpMemoryStore.cs:352-360,382-385</c>)
/// reconstructs a topic result's scope only when the wire's <c>item.Name</c> contains ':' — but
/// a topic's wire name is <c>MemoryNaming.FormatScopeLabel(tr.Scope)</c> (e.g. "api",
/// "goal/G-1"), which never does, so every remote TopicResult comes back with scope hardcoded to
/// "local". The seeded corpus's "widget" query legitimately matches both the "api" and
/// "goal/G-1" topics (their auto-generated descriptions surface the shared name fragment
/// "widget"), so local returns two distinct topic labels while remote collapses both to "local"
/// after Distinct(). Verified this survives verbatim into the plan's Task 3 replacement of
/// SearchAll, so it is not fixed anywhere in this branch's scope. Recorded as a residual, not
/// fixed here — fixing it is a HttpMemoryStore wire-format change with no bearing on filtering.
/// </para>
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

    /// <summary>Entries and chunks only — see the class remarks for why TopicResult is excluded
    /// from this comparison. Every InlineData query here must legitimately match something in the
    /// seeded corpus — a query that matches nothing compares an empty set to an empty set and
    /// cannot catch a cross-store parity regression; see <see cref="NoMatches_BothStores_ReturnEmpty"/>
    /// for that case, tested honestly on its own terms.</summary>
    [Theory]
    [InlineData("widget")]
    [InlineData("alpha")]
    [InlineData("planning")]
    public void Unfiltered_BothStores_ReturnSameEntries(string query)
    {
        var local = StoreConformanceFixture.Names(fx.Local,
            fx.Local.SearchAll(query, scopes: null, limit: 100).Where(r => r is not TopicResult));
        var remote = StoreConformanceFixture.Names(fx.Remote,
            fx.Remote.SearchAll(query, scopes: null, limit: 100).Where(r => r is not TopicResult));

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
}
