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
        var results = fx.Remote.SearchAll(new SearchQuery("widget") { Limit = 50 });

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
        var localResults = fx.Local.SearchAll(new SearchQuery(query) { Limit = 100 });
        var remoteResults = fx.Remote.SearchAll(new SearchQuery(query) { Limit = 100 });

        StoreConformanceFixture.Names(fx.Remote, remoteResults).Should()
            .BeEquivalentTo(StoreConformanceFixture.Names(fx.Local, localResults));

        // Names() alone is blind to a scope-only defect: FormatQualifiedName reassembles the
        // same qualified label whether or not the client recovered the scope correctly from the
        // wire name, so compare the raw scope sets directly too.
        StoreConformanceFixture.Scopes(remoteResults).Should()
            .BeEquivalentTo(StoreConformanceFixture.Scopes(localResults));
    }

    /// <summary>A query matching nothing must come back empty from both stores — not "empty from
    /// one and junk from the other". Split out of the parity theory, where it could only ever have
    /// compared an empty set to an empty set.</summary>
    [Fact]
    public void NoMatches_BothStores_ReturnEmpty()
    {
        fx.Local.SearchAll(new SearchQuery("nonexistentterm") { Limit = 100 }).Should().BeEmpty();
        fx.Remote.SearchAll(new SearchQuery("nonexistentterm") { Limit = 100 }).Should().BeEmpty();
    }

    /// <summary>All 25 seeded entries match "widget", so a limit of 5 must fill exactly — under-filling
    /// is the exact defect class this branch exists to fix, so this assertion must be exact, not an
    /// upper bound.</summary>
    [Fact]
    public void Unfiltered_LimitIsHonoured_ByBothStores()
    {
        fx.Local.SearchAll(new SearchQuery("widget") { Limit = 5 }).Should().HaveCount(5);
        fx.Remote.SearchAll(new SearchQuery("widget") { Limit = 5 }).Should().HaveCount(5);
    }

    /// <summary>A limit of 12 is tight enough that push-down filtering and post-hoc filtering are
    /// not mathematically forced to agree (unlike a limit of 100, which never truncates the
    /// 27-candidate corpus and makes the two approaches indistinguishable). The ten /goal/G-1/
    /// entries deterministically outrank the ten /api/ ones (see the fixture), so a pre-fix
    /// remote — which fetches an unfiltered top-12 and filters after — is starved down to
    /// whatever /api/ entries survive that cut, not all 10.</summary>
    [Fact]
    public void PathPrefix_BothStores_ReturnSameEntries()
    {
        var local = StoreConformanceFixture.Names(fx.Local,
            fx.Local.SearchAll(new SearchQuery("widget") { Limit = 12, PathPrefix = "/api/" }));
        var remote = StoreConformanceFixture.Names(fx.Remote,
            fx.Remote.SearchAll(new SearchQuery("widget") { Limit = 12, PathPrefix = "/api/" }));

        remote.Should().BeEquivalentTo(local);
        remote.Should().OnlyContain(n => n.StartsWith("/api/"));
        local.Should().HaveCount(10);
        remote.Should().HaveCount(10);
    }

    /// <summary>"goal/G-1" (with the '/') passes through <c>BuildScopedTopicScope</c> unchanged to
    /// the real corpus scope "local:goal/G-1". ("G-1" without the slash is a decoy — it routes to
    /// "local:g-1", a scope nothing occupies, and excludes nothing on either store.) Standalone
    /// count/containment assertions cover the case where both stores regress together, which
    /// BeEquivalentTo alone cannot catch: 27 "widget" matches minus the ten /goal/G-1/ entries and
    /// the "goal/G-1" topic itself leaves 16.</summary>
    [Fact]
    public void ExcludeTopics_BothStores_ReturnSameEntries()
    {
        var local = StoreConformanceFixture.Names(fx.Local,
            fx.Local.SearchAll(new SearchQuery("widget") { Limit = 100, ExcludeTopics = "goal/G-1" }));
        var remote = StoreConformanceFixture.Names(fx.Remote,
            fx.Remote.SearchAll(new SearchQuery("widget") { Limit = 100, ExcludeTopics = "goal/G-1" }));

        remote.Should().BeEquivalentTo(local);
        local.Should().HaveCount(16);
        remote.Should().HaveCount(16);
        remote.Should().NotContain(n => n.StartsWith("/goal/", StringComparison.Ordinal) || n == "goal/G-1");
    }

    /// <summary>The fill-to-limit case the interface's own doc comment describes and its default
    /// implementation broke: only 10 of the 27 "widget" matches sit under /api/, and the ten
    /// /goal/G-1/ entries deterministically outrank them (see the fixture). A pre-fix remote
    /// fetches an unfiltered top-12 — entirely /goal/G-1/ entries plus at most a sliver of /api/ —
    /// and filters after, so it is starved well below 10. A limit of 12 must still return all 10
    /// /api/ matches when filtering happens server-side, before the top-K cut.</summary>
    [Fact]
    public void PathPrefix_FillsToLimit_NotThePostLimitRemnant()
    {
        var remote = StoreConformanceFixture.Names(fx.Remote,
            fx.Remote.SearchAll(new SearchQuery("widget") { Limit = 12, PathPrefix = "/api/" }));

        remote.Should().HaveCount(10);
    }

    /// <summary>The POST /search attributeFilter path has no other coverage: it's a distinct wire
    /// method (BuildSearchPost/SearchApiRequest) from the GET path every other case here exercises,
    /// so a break in the JSON round-trip, the camelCase mapping, or SendSync's content buffering in
    /// the retry loop would pass every other test in this file. Two seeded entries share the query
    /// term "gizmo" and differ only by a "priority" tag; only the tagged one may survive the
    /// filter.</summary>
    [Fact]
    public void AttributeFilter_BothStores_ReturnSameEntries()
    {
        var filter = new Dictionary<string, string[]> { [KnownAttributes.TagKey] = ["priority"] };

        var local = StoreConformanceFixture.Names(fx.Local,
            fx.Local.SearchAll(new SearchQuery("gizmo") { Limit = 100, AttributeFilter = filter }));
        var remote = StoreConformanceFixture.Names(fx.Remote,
            fx.Remote.SearchAll(new SearchQuery("gizmo") { Limit = 100, AttributeFilter = filter }));

        remote.Should().BeEquivalentTo(local);
        remote.Should().Equal("/attr/tagged-gizmo");
    }
}
