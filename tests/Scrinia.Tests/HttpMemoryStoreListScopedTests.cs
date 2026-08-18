using System.Net;
using FluentAssertions;
using Scrinia;
using Scrinia.Core.Resilience;

namespace Scrinia.Tests;

/// <summary>
/// <c>HttpMemoryStore.ListScoped</c> is a store implementation's own domain entry point for a raw
/// <c>IReadOnlyList&lt;string&gt;?</c> scopes list — it does not go through
/// <see cref="Scrinia.Core.Search.SearchQuery"/>'s init-accessor normalization, so it needs the same
/// <c>MemoryNaming.NormalizeFilterList</c> rule applied at its own entry (Task 9 fix round 1, F-2).
/// Before the fix, a null element reached a bare <c>.Trim()</c> on
/// <c>scopes.Any(s =&gt; s.Trim().Equals("ephemeral", ...))</c> and threw a
/// <c>NullReferenceException</c> — reachable from the MCP <c>list</c>/<c>facts</c> tools and from
/// <c>GatherTopicInfos</c>/<c>DiscoverTopics</c> whenever the local process is configured against a
/// remote store.
///
/// <para>Uses the same stub-<see cref="HttpMessageHandler"/> seam as
/// <c>HttpMemoryStoreLoadIndexPagingTests</c> rather than a live Kestrel socket, so this runs as a
/// fast unit test.</para>
/// </summary>
public sealed class HttpMemoryStoreListScopedTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        /// <summary>The query string of every request received, in order.</summary>
        public List<string> Queries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));

        // ListScoped -> FetchAllPages goes through SendSync (HttpClient.Send), so the sync
        // override is the one that matters.
        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Queries.Add(request.RequestUri!.Query);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"memories":[],"total":0}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
            response.Headers.TryAddWithoutValidation(
                Scrinia.Core.Resilience.RemoteApiVersion.HeaderName, RemoteApiVersion.Current);
            return response;
        }
    }

    private static HttpMemoryStore MakeStore(out RecordingHandler handler)
    {
        handler = new RecordingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        return new HttpMemoryStore(http, "default", retryOptions: new RetryOptions(MaxRetries: 0));
    }

    [Fact]
    public void ListScoped_ScopesContainingOnlyANullElement_NeverThrows()
    {
        using var store = MakeStore(out _);

        var act = () => store.ListScoped(new string[] { null! });

        act.Should().NotThrow<NullReferenceException>(
            "a null scope element must never reach the bare .Trim() this method used to have");
    }

    [Fact]
    public void ListScoped_ScopesWithARealEntryAndANullElement_NeverThrows()
    {
        using var store = MakeStore(out _);

        var act = () => store.ListScoped(new string[] { "local", null! });

        act.Should().NotThrow<NullReferenceException>();
    }
}
