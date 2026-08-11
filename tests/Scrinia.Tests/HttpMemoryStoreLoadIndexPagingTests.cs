using System.Net;
using FluentAssertions;
using Scrinia;
using Scrinia.Core.Resilience;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Crosses <c>HttpMemoryStore.LoadIndex</c>'s page boundary — the one thing the Kestrel-backed
/// <c>RemoteLoadIndexTests.LoadIndex_ReturnsEveryEntry_AboveTheServersDefaultPageSize</c> cannot
/// do. That test seeds 205 against a client page size of 1000, so the loop runs exactly once and
/// its two pieces of arithmetic — <c>offset += list.Memories.Length</c> and
/// <c>offset >= list.Total</c> — never execute a second iteration. Measured: changing the
/// increment to <c>offset += pageSize</c> left it green.
///
/// <para>The seam is a stub handler that behaves like a server whose own page cap is smaller than
/// the client's request, which is the real-world shape this must survive: the client asks for 1000
/// and gets whatever the server is willing to give. Serving 2 at a time out of 5 forces three
/// requests, so a wrong increment (1000) sends the second request past the end and returns 2
/// entries instead of 5, and a wrong terminator loops forever or stops early.</para>
/// </summary>
public sealed class HttpMemoryStoreLoadIndexPagingTests
{
    /// <summary>Serves <see cref="Total"/> entries <see cref="PageSize"/> at a time, honouring the
    /// caller's <c>offset</c> and deliberately ignoring its (larger) <c>limit</c>.</summary>
    private sealed class PagedListHandler(int total, int pageSize) : HttpMessageHandler
    {
        public int Total { get; } = total;
        public int PageSize { get; } = pageSize;

        /// <summary>Every offset the client asked for, in order — so the test can assert the walk
        /// itself, not merely its result.</summary>
        public List<int> RequestedOffsets { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));

        // LoadIndex goes through SendSync (the synchronous HttpClient.Send), so the sync override
        // is the one that matters; HttpMessageHandler's own default throws NotSupportedException.
        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Hand-parsed rather than via HttpUtility: the query LoadIndex builds is a fixed,
            // three-parameter string with no escaping to unpick beyond the scope name.
            string? offsetParam = request.RequestUri!.Query.TrimStart('?')
                .Split('&')
                .FirstOrDefault(p => p.StartsWith("offset=", StringComparison.Ordinal));
            int offset = offsetParam is not null && int.TryParse(offsetParam["offset=".Length..], out int o)
                ? o : 0;
            RequestedOffsets.Add(offset);

            int take = Math.Clamp(Total - offset, 0, PageSize);
            string items = string.Join(",", Enumerable.Range(offset, take).Select(i => $$"""
                {
                  "name": "probe-{{i:D4}}",
                  "qualifiedName": "probe-{{i:D4}}",
                  "scope": "local",
                  "chunkCount": 1,
                  "originalBytes": 10,
                  "createdAt": "2026-08-10T00:00:00+00:00",
                  "updatedAt": null,
                  "description": "probe {{i}}",
                  "attributes": null
                }
                """));

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"memories":[{{items}}],"total":{{Total}}}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
            response.Headers.TryAddWithoutValidation(RemoteApiVersion.HeaderName, RemoteApiVersion.Current);
            return response;
        }
    }

    [Fact]
    public void LoadIndex_WalksEveryPage_WhenTheServerServesFewerThanRequested()
    {
        var handler = new PagedListHandler(total: 5, pageSize: 2);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        using var store = new HttpMemoryStore(http, "default", retryOptions: new RetryOptions(MaxRetries: 0));

        var entries = store.LoadIndex("local");

        entries.Should().HaveCount(5,
            because: "the loop must keep requesting until it has collected Total entries, however " +
                     "many the server chooses to return per page");
        entries.Select(e => e.Name).Should().Equal(
            "probe-0000", "probe-0001", "probe-0002", "probe-0003", "probe-0004");
        // The offset must advance by the number of entries actually returned — advancing by the
        // requested page size (1000) instead sends the second request straight past the end.
        handler.RequestedOffsets.Should().Equal([0, 2, 4]);
    }

    /// <summary>The terminator, isolated: when the first page already holds everything the server
    /// says exists, <c>offset >= list.Total</c> must stop the loop rather than issue a second
    /// request that returns nothing.</summary>
    [Fact]
    public void LoadIndex_StopsAfterOnePage_WhenTheFirstPageIsComplete()
    {
        var handler = new PagedListHandler(total: 3, pageSize: 10);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        using var store = new HttpMemoryStore(http, "default", retryOptions: new RetryOptions(MaxRetries: 0));

        var entries = store.LoadIndex("local");

        entries.Should().HaveCount(3);
        // Total was reached on the first page, so no second round trip is warranted.
        handler.RequestedOffsets.Should().Equal([0]);
    }
}
