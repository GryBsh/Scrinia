using System.Linq;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Server.Models;
using Scrinia.Server.Services;

namespace Scrinia.Server.Tests;

/// <summary>
/// Before this test existed, <c>SearchResultItem</c> carried no byte count or creation time at
/// all, so <c>HttpMemoryStore</c> (the remote client) fabricated both on every result:
/// <c>OriginalBytes: 0</c> and <c>CreatedAt: DateTimeOffset.UtcNow</c> — a caller reading a remote
/// search result's creation time got the time of the query, not the memory's real age.
///
/// These assertions pin the ACTUAL values, not merely "non-zero" / "not today": a "not zero"
/// assertion would pass on any fabricated non-zero value, and "not today" would pass on any
/// wrong-but-old value. The ground truth is read directly from the persisted
/// <see cref="Scrinia.Core.Models.ArtifactEntry"/> via the server's own store instance (mirrors
/// <see cref="StoreAttributesWireTests"/>), not recomputed, so the test cannot accidentally agree
/// with a wrong formula on both sides.
///
/// A topic result is a genuine absent-value case (a topic is a scope label, not a memory — it has
/// no byte count or creation time to report), covered separately below.
/// </summary>
public sealed class SearchResultMetadataTests
{
    private static StoreRequest Body(string name, string content) =>
        new([content], name, content);

    [Fact]
    public async Task Entry_search_result_carries_the_real_byte_count_and_creation_time()
    {
        using var factory = new ScriniaServerFactory();
        var client = factory.CreateAuthenticatedClient();

        const string name = "search-metadata-entry-probe";
        const string content = "Search metadata probe body with distinctive filler token zzqpr7.";

        var storeResp = await client.PostAsJsonAsync(
            $"/api/v1/stores/{factory.PrimaryStore}/memories", Body(name, content));
        storeResp.EnsureSuccessStatusCode();

        var store = factory.Services.GetRequiredService<StoreManager>().GetStore(factory.PrimaryStore);
        var entry = store.ListScoped(null)
            .Should().ContainSingle(a => a.Entry.Name == name).Subject.Entry;

        var searchResp = await client.GetFromJsonAsync<SearchResponse>(
            $"/api/v1/stores/{factory.PrimaryStore}/search?q=zzqpr7&limit=10");

        searchResp.Should().NotBeNull();
        var item = searchResp!.Results
            .Should().ContainSingle(r => r.Type == "entry", because: "the probe body has a single " +
                "unique term and fits in one chunk, so it must surface as exactly one entry result")
            .Subject;

        item.OriginalBytes.Should().Be(entry.OriginalBytes,
            because: "the real persisted size must reach the wire, not a fabricated 0");
        item.CreatedAt.Should().Be(entry.CreatedAt,
            because: "the real persisted creation time must reach the wire, not the time of the query");
    }

    /// <summary>
    /// A topic is a scope label produced by grouping candidates under a "local:" scope prefix
    /// (<c>FileMemoryStore.BuildTopicInfosFromCandidates</c>) — it is not itself a memory, so it
    /// has no byte count or creation time. Storing one entry under a distinctively-named path
    /// segment creates a topic whose name equals that segment; searching for the segment name
    /// scores the topic via <c>WeightedAttributeScorer.ScoreTopicTerm</c>'s exact-name match
    /// (weight 100), guaranteeing the topic result surfaces regardless of BM25 tie-breaking.
    /// </summary>
    [Fact]
    public async Task Topic_search_result_leaves_both_new_fields_null()
    {
        using var factory = new ScriniaServerFactory();
        var client = factory.CreateAuthenticatedClient();

        const string topic = "zzqtopicprobe123";
        var storeResp = await client.PostAsJsonAsync(
            $"/api/v1/stores/{factory.PrimaryStore}/memories",
            Body($"/{topic}/entry-a", "Filler content for the topic probe test."));
        storeResp.EnsureSuccessStatusCode();

        var searchResp = await client.GetFromJsonAsync<SearchResponse>(
            $"/api/v1/stores/{factory.PrimaryStore}/search?q={topic}&limit=10");

        searchResp.Should().NotBeNull();
        var item = searchResp!.Results
            .Should().ContainSingle(r => r.Type == "topic",
                because: "querying the topic's own name must surface exactly one topic result")
            .Subject;

        item.OriginalBytes.Should().BeNull(
            because: "a topic is a scope label, not a memory — it has no real byte count to report, " +
                "and aggregating member entries into a synthetic count would be a fabrication " +
                "wearing a different hat");
        item.CreatedAt.Should().BeNull(
            because: "a topic has no real creation time either, and DateTimeOffset.MinValue would " +
                "just be the same fabrication with a different disguise");
    }
}
