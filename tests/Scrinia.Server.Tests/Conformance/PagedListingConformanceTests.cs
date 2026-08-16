using FluentAssertions;
using Scrinia.Core.Models;

namespace Scrinia.Server.Tests.Conformance;

/// <summary>
/// The test that would have caught C2: the same corpus, both store implementations, identical
/// listings — over a corpus large enough for the server's default page size to matter.
/// </summary>
[Collection(nameof(PagedListingConformanceCollection))]
public sealed class PagedListingConformanceTests(PagedListingConformanceFixture fx)
{
    [Fact]
    public void ListScoped_Unfiltered_BothStores_ReturnEveryEntry()
    {
        var local = fx.Local.ListScoped(null);
        var remote = fx.Remote.ListScoped(null);

        local.Should().HaveCount(PagedListingConformanceFixture.TotalEntries,
            because: "the fixture seeded exactly that many through the real REST write path");

        remote.Should().HaveCount(PagedListingConformanceFixture.TotalEntries,
            because: "a remote listing that stops at the server's 200-row default answers "
                   + "confidently and wrongly, with no error and no truncation marker");

        PagedListingConformanceFixture.QualifiedNames(fx.Remote, remote).Should()
            .BeEquivalentTo(PagedListingConformanceFixture.QualifiedNames(fx.Local, local),
                because: "the same corpus read through either store is the same set of memories");
    }

    /// <summary>The sharpest case. FileMemoryStore.ListScoped enumerates ResolveReadScopes then
    /// LoadIndex(scope) per scope, so entries arrive scope-by-scope and contiguous — truncating
    /// at 200 of 250 makes whole topics VANISH rather than thinning each evenly. Set equality,
    /// so this holds however the server orders scopes; it does not depend on predicting which
    /// topic disappears.</summary>
    [Fact]
    public void DiscoverTopics_BothStores_SeeEveryTopic()
    {
        var local = fx.Local.DiscoverTopics();
        var remote = fx.Remote.DiscoverTopics();

        local.Should().HaveCount(PagedListingConformanceFixture.TopicCount,
            because: "the fixture seeded that many non-empty topic scopes");

        remote.Should().BeEquivalentTo(local,
            because: "a topic whose entries all fall past a truncated listing does not merely "
                   + "lose entries — it stops existing, and code that lists it reports it empty");
    }

    /// <summary>ResolveReadScopes(null) is built on DiscoverTopics, so it inherits the whole
    /// defect. This is the member that decides which scopes any later read even looks at.</summary>
    [Fact]
    public void ResolveReadScopes_Unfiltered_BothStores_Agree()
    {
        fx.Remote.ResolveReadScopes(null).Should()
            .BeEquivalentTo(fx.Local.ResolveReadScopes(null),
                because: "the set of readable scopes decides what every later read can see");
    }

    [Fact]
    public void GatherTopicInfos_BothStores_ReportSameEntryCounts()
    {
        var local = fx.Local.GatherTopicInfos(null)
            .ToDictionary(t => t.Scope, t => t.EntryNames.Length);
        var remote = fx.Remote.GatherTopicInfos(null)
            .ToDictionary(t => t.Scope, t => t.EntryNames.Length);

        remote.Should().BeEquivalentTo(local,
            because: "per-topic counts come straight off the listing, so a truncated one "
                   + "under-reports every topic past the cut");
    }

    /// <summary>Closes C2's stated harm. This compares two genuinely different implementations
    /// — FileMemoryStore's generation-invalidated facts.json cache against the IMemoryStore
    /// default's rebuild from ListScoped(null) — over a corpus large enough that truncation
    /// changes the answer. It is also what makes keeping the default (spec D-2) defensible
    /// rather than merely convenient.
    ///
    /// <para><b>Deviation from the task brief's verbatim code (reported, not silent):</b> the
    /// fixture's seeded bodies ("paged listing probe topic N number M") only produce plain,
    /// non-"type:value" auto-derived keywords, and no entry carries any Attributes at all. Neither
    /// <c>EntityGraph.ParseEntityKeywords</c> nor <c>ParseEntityAttributes</c> can find an entity in
    /// that content, so <c>EntityFactStore</c> was empty for BOTH stores regardless of truncation —
    /// verified: under the deliberately-reverted <c>FetchAllPages</c> (Step 2), the brief's plain
    /// comparison PASSED (0 entities vs 0 entities for all three keys), which is exactly the
    /// never-observed-to-fail hazard this task exists to rule out. Re-storing one entry per topic
    /// scope with real entity-shaped attributes below gives every topic (including whichever ones a
    /// truncated listing drops) a distinct entity under each of the three keys the brief named, so a
    /// truncated corpus now derives a genuinely different, smaller fact set than the untruncated
    /// one.</para>
    ///
    /// <para><b>Why <see cref="IMemoryStore.StoreAsync"/> and not <see cref="IMemoryStore.TryUpdateEntry"/>:</b>
    /// this fixture's <c>PagingRateLimitServerFactory</c> key carries only <c>["search", "store"]</c>
    /// (see that factory's own doc comment on why it cannot gain the plain <c>ScriniaServerFactory</c>'s
    /// full permission set) — measured, not theorized: <c>TryUpdateEntry</c>'s <c>GET .../entry</c>
    /// step requires <c>"read"</c> and threw <c>403 Forbidden</c> the first time this test used it.
    /// <c>StoreAsync</c> needs only <c>"store"</c>, which the key has, and re-storing under the exact
    /// same wire name (<c>FormatQualifiedName</c> of the same scope+subject the fixture already
    /// seeded) is an overwrite of the existing entry — see <c>FileMemoryStore.StoreAsync</c>'s
    /// case-insensitive <c>existingEntry</c> lookup — not a new one, so <see cref="PagedListingConformanceFixture.TotalEntries"/>
    /// and every topic's entry count stay exactly as the fixture left them.</para>
    /// </summary>
    [Fact]
    public void GetEntityFactStore_BothStores_DeriveTheSameFacts()
    {
        for (int t = 0; t < PagedListingConformanceFixture.TopicCount; t++)
        {
            string scope = $"local:t{t:D2}";
            string body = $"paged listing probe topic {t} number 0";
            var write = new MemoryWrite(
                "entry-000", scope, [body], Description: body,
                Attributes: new Dictionary<string, string[]>
                {
                    ["topic"] = [$"probe-topic-{t:D2}"],
                    ["mention"] = [$"probe-mention-{t:D2}"],
                    ["thing"] = [$"probe-thing-{t:D2}"],
                });

            var stored = fx.Remote.StoreAsync(write).GetAwaiter().GetResult();

            stored.Attributes.Should().ContainKey("thing",
                because: $"re-storing '{scope}/entry-000' with entity-shaped attributes must "
                       + "persist them, or the rest of this test would silently compare two "
                       + "empty fact stores instead of exercising truncation");
        }

        var local = fx.Local.GetEntityFactStore();
        var remote = fx.Remote.GetEntityFactStore();

        foreach (string key in new[] { "topic", "mention", "thing" })
        {
            remote.Entities(key).Should().BeEquivalentTo(local.Entities(key),
                because: $"entity '{key}' facts derived over a truncated corpus are wrong, "
                       + "and the remote store reaches them through the interface default");
        }
    }
}
