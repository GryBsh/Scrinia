using System.Net;
using FluentAssertions;
using Scrinia;
using Scrinia.Core.Resilience;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// The listing wire carries <c>TermFrequencies</c>, <c>ChunkEntries</c> and <c>FileHashes</c> for
/// every entry of every page — and the client used to throw all three away. Both of its
/// <c>ArtifactEntry</c> construction sites (<c>LoadIndex</c> and <c>ListScoped</c>) read
/// name/bytes/count/createdAt/description/updatedAt/attributes/keywords and stopped there, so up to
/// 1000 entries' worth of derived metadata crossed the network per page and was discarded on
/// arrival.
///
/// <para>None of the three is cosmetic. <c>TermFrequencies</c> is what BM25 scores an entry
/// against; <c>ChunkEntries</c> is the per-chunk metadata an append must not destroy;
/// <c>FileHashes</c> is the drift baseline <c>list(mode:"drift")</c> compares against — a remote
/// store that reports every entry as having no tracked files reports no drift, ever, and looks
/// exactly like a workspace with nothing to report.</para>
///
/// <para>Driven by a stub handler rather than a live server because <c>FileHashes</c> is the one
/// field that cannot be provoked end-to-end without planting a real file under a real workspace
/// root for the server's hasher to find. The stub states the wire contract directly, which is what
/// is under test here: given a server that sends these fields, the client must read them. That the
/// server really does send them is pinned separately, over a socket, by
/// <c>Scrinia.Server.Tests.RemoteLoadIndexTests</c> and <c>ListResponseCompleteEntryTests</c>.</para>
/// </summary>
public sealed class HttpMemoryStoreDerivedFieldsTests
{
    /// <summary>One entry with every derived field populated. Deliberately NOT the minimal shape:
    /// a fixture that left them null could not tell "read correctly" from "dropped".</summary>
    private const string CompleteEntryListJson = """
        {"memories":[{
          "name":"derived","qualifiedName":"derived","scope":"local",
          "chunkCount":2,"originalBytes":128,
          "createdAt":"2026-01-01T00:00:00+00:00","updatedAt":"2026-01-02T00:00:00+00:00",
          "description":"an entry with everything",
          "attributes":{"tag":["alpha"]},
          "keywords":["zebra"],
          "termFrequencies":{"zebra":7},
          "chunkEntries":[
            {"chunkIndex":1,"contentPreview":"first","keywords":["one"],"termFrequencies":{"one":3}},
            {"chunkIndex":2,"contentPreview":"second","keywords":["two"],"termFrequencies":{"two":4}}
          ],
          "fileHashes":{"src/widget.cs":"deadbeef"},
          "agentKeywords":["zebra"]
        }],"total":1}
        """;

    /// <summary>Answers the listing endpoint and nothing else — an unexpected call 404s rather than
    /// being quietly satisfied.</summary>
    private sealed class ListHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));

        // LoadIndex and ListScoped both go through SendSync (the synchronous HttpClient.Send), so
        // the sync override is the one that matters; HttpMessageHandler's default throws.
        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = request.RequestUri!.AbsolutePath.EndsWith("/memories", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        CompleteEntryListJson, System.Text.Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.Headers.TryAddWithoutValidation(RemoteApiVersion.HeaderName, RemoteApiVersion.Current);
            return response;
        }
    }

    private static HttpMemoryStore NewStore() =>
        new(new HttpClient(new ListHandler()) { BaseAddress = new Uri("http://localhost:5000") },
            "default", retryOptions: new RetryOptions(MaxRetries: 0));

    [Fact]
    public void LoadIndex_CarriesEveryDerivedFieldTheWireSends()
    {
        using var store = NewStore();

        var entry = store.LoadIndex("local").Single();

        entry.TermFrequencies.Should().NotBeNull(
            because: "the server sends this entry's term frequencies and BM25 scores against them; " +
                     "dropping them on arrival makes a remote entry unscoreable")
            .And.ContainKey("zebra");
        entry.TermFrequencies!["zebra"].Should().Be(7);

        entry.ChunkEntries.Should().NotBeNull(
            because: "per-chunk metadata is on the wire, and an append derives over what the memory " +
                     "already carries — an entry that arrives without it looks like a memory with " +
                     "no chunk structure at all")
            .And.HaveCount(2);
        entry.ChunkEntries![1].ChunkIndex.Should().Be(2);
        entry.ChunkEntries[1].ContentPreview.Should().Be("second");
        entry.ChunkEntries[1].Keywords.Should().Equal(["two"]);
        entry.ChunkEntries[1].TermFrequencies.Should().NotBeNull().And.ContainKey("two");

        entry.FileHashes.Should().NotBeNull(
            because: "this is the drift baseline; a remote store that discards it reports 'no drift' " +
                     "for every memory, which is indistinguishable from having nothing to report")
            .And.ContainKey("src/widget.cs");
        entry.FileHashes!["src/widget.cs"].Should().Be("deadbeef");

        // The fields that were already read — asserted so a regression that swaps the mapper for
        // something narrower cannot pass by satisfying only the three above.
        entry.AgentKeywords.Should().Equal(["zebra"],
            because: "which keywords the caller supplied is on the wire too, and a compaction on " +
                     "this side reads it to decide what keeps agent weight — dropping it on " +
                     "arrival sends every remote memory down the legacy guess instead");

        entry.Keywords.Should().Equal(["zebra"]);
        entry.Attributes.Should().NotBeNull().And.ContainKey("tag");
        entry.ChunkCount.Should().Be(2);
        entry.OriginalBytes.Should().Be(128);
        entry.ContentPreview.Should().Be("an entry with everything");
        entry.UpdatedAt.Should().Be(DateTimeOffset.Parse("2026-01-02T00:00:00+00:00",
            System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary><c>ListScoped</c> is a second, independent construction site, not a redundant
    /// sibling: it used to build its own <c>ArtifactEntry</c> with its own field list, so dropping
    /// a field there would have passed every <c>LoadIndex</c> assertion above.
    /// <c>MaintenanceEventSink</c> and the CLI's drift listing both read entries through this path
    /// specifically.</summary>
    [Fact]
    public void ListScoped_CarriesEveryDerivedFieldTheWireSends()
    {
        using var store = NewStore();

        var entry = store.ListScoped(["local"]).Single().Entry;

        entry.TermFrequencies.Should().NotBeNull().And.ContainKey("zebra");
        entry.ChunkEntries.Should().NotBeNull().And.HaveCount(2);
        entry.FileHashes.Should().NotBeNull().And.ContainKey("src/widget.cs");
    }
}
