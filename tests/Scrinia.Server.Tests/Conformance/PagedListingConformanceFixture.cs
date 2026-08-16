using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Server.Models;
using Scrinia.Server.Services;

namespace Scrinia.Server.Tests.Conformance;

/// <summary>
/// <see cref="StoreConformanceFixture"/>'s construction, over a corpus that crosses the
/// server's 200-row default page size. That fixture is the artifact forcing the two stores
/// to agree, but it seeds 27 entries and asserts only on search — structurally below the
/// boundary where C2 begins, so it could never have caught it.
///
/// <para>A separate fixture rather than a larger shared one: StoreConformanceTests pins exact
/// counts to its 27-entry corpus ("25 of the fixture's 27 seeded entries contain 'widget'",
/// "an unfiltered top-20 of 27 candidates"), and growing that corpus would break assertions
/// whose subject is something else entirely.</para>
///
/// <para>250 entries across 10 topic scopes. The count is chosen against the SERVER's default
/// page size (200, MemoryEndpoints.DefaultListLimit) — the boundary the defect actually sits
/// on — not the client's own 1000-entry page size. See the plan's "Known limitation".</para>
///
/// <para><b>Deviation from a plain <see cref="ScriniaServerFactory"/>:</b> seeding 250 entries
/// through the REST write path trips the server's default rate limit (100 requests/60s,
/// <c>Scrinia:RateLimit:PermitLimit</c> in appsettings.json) partway through — measured, not
/// theorized: the first run against a bare <c>ScriniaServerFactory</c> threw
/// <c>HttpRequestException: 429 (Too Many Requests)</c> out of <c>SeedAsync</c> well before any
/// listing assertion ran. <c>ScriniaServerFactory</c> is a pre-existing test file with no hook to
/// raise the limit per-instance, and <c>RateLimitTests.Exceeding_rate_limit_returns_429</c> pins
/// its default to exactly 100 — so that file cannot change. Rather than invent a second
/// rate-limit-relaxed factory, this fixture reuses <see cref="PagingRateLimitServerFactory"/>
/// (defined in <c>RemoteLoadIndexTests.cs</c>, public, already built for this exact problem for
/// <c>LoadIndex_ReturnsEveryEntry_AboveTheServersDefaultPageSize</c>). Its own doc comment records
/// why it uses a dedicated <c>ConfigureWebHost</c> override instead of
/// <c>ScriniaServerFactory.WithWebHostBuilder</c>: <c>WithWebHostBuilder</c> itself breaks
/// <c>UseKestrel(0)</c>'s ephemeral-port binding, measured there too.</para>
/// </summary>
public sealed class PagedListingConformanceFixture : IAsyncLifetime
{
    public const int TopicCount = 10;
    public const int PerTopic = 25;
    public const int TotalEntries = TopicCount * PerTopic;   // 250, above the server's 200 default

    // TotalEntries * 2 headroom, mirroring PagingRateLimitServerFactory's own call site
    // (`total * 2` in RemoteLoadIndexTests) rather than inventing a different margin.
    private readonly PagingRateLimitServerFactory _factory = new(TotalEntries * 2);
    private HttpClient _client = null!;

    /// <summary>The server's OWN store instance, resolved from StoreManager — not a second
    /// FileMemoryStore opened on the same directory, which would fight over the per-scope index
    /// cache and produce spurious differences.</summary>
    public IMemoryStore Local { get; private set; } = null!;
    public IMemoryStore Remote { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Order is load-bearing: UseKestrel must run before the host is materialized, which the
        // first touch of Services or CreateClient would do.
        _factory.UseKestrel(0);
        _factory.StartServer();

        _client = _factory.CreateAuthenticatedClient();
        Local = _factory.Services.GetRequiredService<StoreManager>().GetStore(_factory.PrimaryStore);
        // HttpMemoryStore's constructor assigns _http.Timeout, which throws if the HttpClient has
        // already issued a request — so construct it before the first SeedAsync.
        Remote = new HttpMemoryStore(_client, _factory.PrimaryStore);

        // Every topic gets the same number of entries, and every topic is non-empty. The second
        // part matters: FileMemoryStore.DiscoverTopics re-derives scopes from on-disk directory
        // names while HttpMemoryStore.DiscoverTopics derives them from the listing, so a topic
        // directory with zero entries would legitimately differ between the two and is not this
        // branch's subject.
        for (int t = 0; t < TopicCount; t++)
            for (int i = 0; i < PerTopic; i++)
                await SeedAsync($"/t{t:D2}/entry-{i:D3}", $"paged listing probe topic {t} number {i}");
    }

    private async Task SeedAsync(string name, string body)
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new StoreRequest([body], name, body));
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Order-insensitive comparison key for a listing: the qualified label of each
    /// entry and nothing else.</summary>
    public static string[] QualifiedNames(IMemoryStore store, IEnumerable<ScopedArtifact> items) =>
        items.Select(i => store.FormatQualifiedName(i.Scope, i.Entry.Name))
             .OrderBy(s => s, StringComparer.Ordinal).ToArray();

    public Task DisposeAsync()
    {
        // HttpMemoryStore owns _client's disposal (its Dispose() disposes the HttpClient it was
        // constructed with), so no separate _client.Dispose() here.
        (Remote as IDisposable)?.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }
}

[CollectionDefinition(nameof(PagedListingConformanceCollection))]
public sealed class PagedListingConformanceCollection
    : ICollectionFixture<PagedListingConformanceFixture>;
