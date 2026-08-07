using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core;
using Scrinia.Core.Search;
using Scrinia.Server.Models;
using Scrinia.Server.Services;
using Xunit;

namespace Scrinia.Server.Tests.Conformance;

/// <summary>
/// One server, one corpus, two handles onto it: <see cref="Local"/> is the very
/// <see cref="IMemoryStore"/> instance the endpoints use (resolved from <see cref="StoreManager"/>,
/// not a second store opened on the same directory — two FileMemoryStore instances would fight
/// over the per-scope index cache and produce spurious differences). <see cref="Remote"/> is an
/// <c>HttpMemoryStore</c> driven over a real loopback socket.
///
/// It must be a real socket, not TestServer's in-memory handler: HttpMemoryStore sends
/// synchronously and Microsoft.AspNetCore.TestHost.ClientHandler.Send throws
/// NotSupportedException unconditionally ("risk of threadpool exhaustion"), with no server-side
/// switch to enable it. UseKestrel(0) + StartServer bind an ephemeral port before anything
/// touches Services or CreateClient, so CreateClient hands back a client over a real
/// SocketsHttpHandler.
///
/// The corpus is seeded through the REST API so it travels the real write path. Because Local is
/// the server's own instance, those writes are immediately visible to it.
/// </summary>
public sealed class StoreConformanceFixture : IAsyncLifetime
{
    private readonly ScriniaServerFactory _factory = new();
    private HttpClient _client = null!;

    public IMemoryStore Local { get; private set; } = null!;
    public IMemoryStore Remote { get; private set; } = null!;

    /// <summary>Every memory name seeded, in insertion order.</summary>
    public IReadOnlyList<string> SeededNames { get; private set; } = [];

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

        var names = new List<string>();

        // Ten entries under /api/, ten under /goal/, five ephemeral-free plain names. The path
        // split is what pathPrefix cases filter on; the counts are what fill-to-limit asserts.
        // The /goal/G-1/ bodies repeat "widget" so those ten strictly outrank the ten /api/
        // entries on the "widget" query — an unfiltered top-20 of 27 candidates provably pushes
        // /api/ entries out by score, not by tie-break order, and this stays true if the corpus
        // grows later. alpha/planning stay present since other tests query them.
        for (int i = 0; i < 10; i++)
            names.Add(await SeedAsync($"/api/widget-{i}", $"widget alpha handling number {i}"));
        for (int i = 0; i < 10; i++)
            names.Add(await SeedAsync($"/goal/G-1/widget-{i}", $"widget widget widget widget alpha planning number {i}"));
        for (int i = 0; i < 5; i++)
            names.Add(await SeedAsync($"note-{i}", $"widget alpha loose note number {i}"));

        // Two entries, same query term ("gizmo", distinct from "widget"/"alpha"/"planning" so
        // they don't perturb any other case's counts), differing only by tag — coverage for the
        // POST /search attributeFilter wire path, which nothing else here exercises.
        names.Add(await SeedAsync("/attr/tagged-gizmo", "gizmo attribute filter probe", tags: ["priority"]));
        names.Add(await SeedAsync("/attr/untagged-gizmo", "gizmo attribute filter probe"));

        SeededNames = names;
    }

    private async Task<string> SeedAsync(string name, string body, string[]? tags = null)
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new StoreRequest([body], name, body, tags));
        resp.EnsureSuccessStatusCode();
        return name;
    }

    public Task DisposeAsync()
    {
        // HttpMemoryStore owns _client's disposal (its Dispose() disposes the HttpClient it was
        // constructed with), so no separate _client.Dispose() here.
        (Remote as IDisposable)?.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Ordered-insensitive comparison key: the qualified label of each result.
    /// Score, CreatedAt and OriginalBytes are deliberately excluded — HttpMemoryStore
    /// fabricates the latter two (a documented limitation), and scoring differs because the
    /// server always has a PathSegmentScoreContributor registered.</summary>
    public static string[] Names(IMemoryStore store, IEnumerable<SearchResult> results) =>
        results.Select(r => r switch
        {
            EntryResult er => store.FormatQualifiedName(er.Item.Scope, er.Item.Entry.Name),
            ChunkEntryResult cr => store.FormatQualifiedName(cr.ParentItem.Scope, cr.ParentItem.Entry.Name),
            TopicResult tr => MemoryNaming.FormatScopeLabel(tr.Scope),
            _ => "",
        }).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();

    /// <summary>The raw internal scope of each result (e.g. "local", "local:goal/G-1") — unlike
    /// <see cref="Names"/>, this is NOT blind to a client that mis-recovers scope from the wire
    /// name: FormatQualifiedName reassembles the same qualified label whether or not the scope was
    /// parsed correctly, so a scope-only defect can hide behind identical Names() output. Compare
    /// this set too whenever cross-store parity is being asserted.</summary>
    public static string[] Scopes(IEnumerable<SearchResult> results) =>
        results.Select(r => r switch
        {
            EntryResult er => er.Item.Scope,
            ChunkEntryResult cr => cr.ParentItem.Scope,
            TopicResult tr => tr.Scope,
            _ => "",
        }).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
}

[CollectionDefinition(nameof(StoreConformanceCollection))]
public sealed class StoreConformanceCollection : ICollectionFixture<StoreConformanceFixture>;
