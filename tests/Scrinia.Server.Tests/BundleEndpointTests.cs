using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Server.Auth;
using Scrinia.Server.Models;
using Xunit;

namespace Scrinia.Server.Tests;

/// <summary>
/// Covers the two branches of the ILocalMemoryStore guard added to the bundle endpoints
/// (F-8 / Task 3a): a local-backed store still exports cleanly, and a store that is not an
/// ILocalMemoryStore gets a clean refusal instead of an unhandled exception.
/// </summary>
public sealed class BundleEndpointTests
{
    [Fact]
    public async Task Export_against_local_backend_succeeds()
    {
        using var factory = new ScriniaServerFactory();
        var client = factory.CreateAuthenticatedClient();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/stores/{factory.PrimaryStore}/export",
            new ExportRequest(["some-topic"]));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "the primary store is FilesystemBackend-created and satisfies ILocalMemoryStore");
    }

    [Fact]
    public async Task Export_against_non_local_backend_returns_501()
    {
        using var factory = new NonLocalStoreServerFactory();
        var client = factory.CreateAuthenticatedClient();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/stores/{factory.RemoteStore}/export",
            new ExportRequest(["some-topic"]));

        // Pre-fix, this endpoint read ctx.LocalStore, whose getter threw NotSupportedException for
        // a non-ILocalMemoryStore store; Program.cs's global UseExceptionHandler turned that into
        // an opaque 500. Confirmed by running this assertion against the unfixed code before the
        // guard below existed — see task-3a-report.md for the captured red-check evidence.
        resp.StatusCode.Should().Be(HttpStatusCode.NotImplemented,
            because: "the configured backend hands back a non-local IMemoryStore, and the endpoint " +
                     "must refuse cleanly rather than let RequestContext.LocalStore throw");

        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body.Should().NotBeNull();
        body!.Error.Should().Contain("local filesystem");
    }

    // NOTE on request shape: a genuine multipart/form-data POST to /import — real file, real
    // boundary, over both TestServer and a real Kestrel socket — is rejected with an empty-body
    // 415 Unsupported Media Type before ANY endpoint code runs, for every store, local or not.
    // Root cause, isolated by elimination (no-body request reaches the handler; a JSON body
    // reaches the handler; multipart and text/plain content types do not): ImportBundle's
    // `string[]? topics = null` parameter carries no [FromQuery], so minimal API's parameter
    // binding infers it as an implicit JSON request body, and the framework 415s any request
    // whose Content-Type isn't application/json before the delegate executes — including the
    // multipart uploads this endpoint exists to accept. This is a pre-existing defect, orthogonal
    // to the ILocalMemoryStore guard (topics's binding source is untouched by this task) and out
    // of Task 3a's scope to fix. Recorded in task-3a-report.md as a new finding.
    //
    // Consequence for these tests: neither can reach the guard through a real upload, so both use
    // a bodyless POST instead (proven, below, to reach the handler past framework binding). That
    // request shape cannot exercise a successful import (there is no file, so the local case
    // legitimately stops at "Multipart file upload required"), but it is sufficient — and, given
    // the 415, currently the ONLY way — to prove the guard fires for a non-local store and does
    // NOT fire for a local one, using the identical request against both.

    [Fact]
    public async Task Import_against_local_backend_reaches_form_validation()
    {
        using var factory = new ScriniaServerFactory();
        var client = factory.CreateAuthenticatedClient();

        var resp = await client.PostAsync($"/api/v1/stores/{factory.PrimaryStore}/import", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "the primary store satisfies ILocalMemoryStore, so the guard must pass and " +
                     "execution must reach the real multipart-required check, not the 501 guard");

        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body.Should().NotBeNull();
        body!.Error.Should().Be("Multipart file upload required.");
    }

    [Fact]
    public async Task Import_against_non_local_backend_returns_501()
    {
        using var factory = new NonLocalStoreServerFactory();
        var client = factory.CreateAuthenticatedClient();

        // Bodyless, matching Import_against_local_backend_reaches_form_validation exactly except
        // for the store — isolates the guard as the only variable that can explain a different
        // status code between the two tests.
        var resp = await client.PostAsync($"/api/v1/stores/{factory.RemoteStore}/import", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotImplemented,
            because: "the configured backend hands back a non-local IMemoryStore, and the endpoint " +
                     "must refuse cleanly rather than let RequestContext.LocalStore throw");

        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body.Should().NotBeNull();
        body!.Error.Should().Contain("local filesystem");
    }

    // NOTE on request shape (contrast with the NOTE above at line 60): this fact and the two
    // below post a genuine multipart/form-data body built from a real, server-produced bundle —
    // exported via POST /export, which is JSON-bodied and therefore unaffected by the [FromQuery]
    // binding defect this task fixes. That keeps Step 2 (red check) failing for exactly the
    // reason under test — 415 from ImportBundle's own parameter binding — rather than failing to
    // produce a well-formed bundle in the first place.

    [Fact]
    public async Task Import_WithMultipartUpload_ReachesTheHandler()
    {
        using var factory = new ScriniaServerFactory();
        using var client = factory.CreateAuthenticatedClient();

        const string topic = "import-mp-topic";
        const string marker = "MARKER-import-with-multipart-upload-reaches-the-handler";
        await StoreMemoryAsync(client, factory.PrimaryStore, topic, "note", marker);

        byte[] bundleBytes = await ExportBundleAsync(client, factory.PrimaryStore, topic);

        using var content = new MultipartFormDataContent();
        var bundle = new ByteArrayContent(bundleBytes);
        bundle.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(bundle, "file", "x.scrinia-bundle");

        var response = await client.PostAsync(
            $"/api/v1/stores/{factory.SecondaryStore}/import", content);

        // The "because" text embeds the response body so a red-check run (pre-fix) captures the
        // exact 415 payload in the assertion-failure output, not just the status code.
        string responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().NotBe(HttpStatusCode.UnsupportedMediaType,
            because: "a complex-typed parameter with no [FromQuery] is inferred as JSON-body-bound, "
                   + "so every genuine multipart upload was rejected before the handler ran. "
                   + $"Response body was: \"{responseBody}\"");

        // Assert the import actually happened, not merely that it avoided 415 — a handler that
        // 500s or no-ops would also satisfy the assertion above.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await client.GetFromJsonAsync<ListResponse>(
            $"/api/v1/stores/{factory.SecondaryStore}/memories?scopes={topic}");
        list.Should().NotBeNull();
        list!.Memories.Should().ContainSingle(m => m.Name == "note" && m.Description == marker,
            because: "the exported bundle's single entry, carrying its original content preview, " +
                     "must have actually landed in the target store's index");
    }

    [Fact]
    public async Task Import_WithTopicsFilter_OnlyImportsSpecifiedTopic()
    {
        using var factory = new ScriniaServerFactory();
        using var client = factory.CreateAuthenticatedClient();

        const string topicA = "import-filter-topic-a";
        const string topicB = "import-filter-topic-b";
        const string markerA = "MARKER-topics-filter-topic-a";
        const string markerB = "MARKER-topics-filter-topic-b";
        await StoreMemoryAsync(client, factory.PrimaryStore, topicA, "note-a", markerA);
        await StoreMemoryAsync(client, factory.PrimaryStore, topicB, "note-b", markerB);

        byte[] bundleBytes = await ExportBundleAsync(client, factory.PrimaryStore, topicA, topicB);

        using var content = new MultipartFormDataContent();
        var bundle = new ByteArrayContent(bundleBytes);
        bundle.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(bundle, "file", "x.scrinia-bundle");

        // [FromQuery] string[] binds repeated keys only — a single "topics=topicA" query value
        // here binds to a one-element array, which is exactly the case under test.
        var response = await client.PostAsync(
            $"/api/v1/stores/{factory.SecondaryStore}/import?topics={topicA}", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var listA = await client.GetFromJsonAsync<ListResponse>(
            $"/api/v1/stores/{factory.SecondaryStore}/memories?scopes={topicA}");
        listA.Should().NotBeNull();
        listA!.Memories.Should().ContainSingle(m => m.Name == "note-a" && m.Description == markerA,
            because: "topics=topic-a was requested, so topic-a's entry must have landed");

        var listB = await client.GetFromJsonAsync<ListResponse>(
            $"/api/v1/stores/{factory.SecondaryStore}/memories?scopes={topicB}");
        listB.Should().NotBeNull();
        listB!.Memories.Should().BeEmpty(
            because: "topics=topic-a was requested, so topic-b must have been filtered out of the import");
    }

    [Fact]
    public async Task Import_WithRepeatedTopicsKeys_ImportsOnlyTheRequestedTopics()
    {
        using var factory = new ScriniaServerFactory();
        using var client = factory.CreateAuthenticatedClient();

        const string topicA = "import-repeated-topic-a";
        const string topicB = "import-repeated-topic-b";
        const string topicC = "import-repeated-topic-c";
        const string markerA = "MARKER-repeated-topic-a";
        const string markerB = "MARKER-repeated-topic-b";
        const string markerC = "MARKER-repeated-topic-c";
        await StoreMemoryAsync(client, factory.PrimaryStore, topicA, "note-a", markerA);
        await StoreMemoryAsync(client, factory.PrimaryStore, topicB, "note-b", markerB);
        await StoreMemoryAsync(client, factory.PrimaryStore, topicC, "note-c", markerC);

        byte[] bundleBytes = await ExportBundleAsync(client, factory.PrimaryStore, topicA, topicB, topicC);

        using var content = new MultipartFormDataContent();
        var bundle = new ByteArrayContent(bundleBytes);
        bundle.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(bundle, "file", "x.scrinia-bundle");

        // Pins the CHANGELOG's claim that `topics` takes repeated query keys
        // (?topics=a&topics=b), not a comma-separated value — nothing else in the suite
        // exercised more than one key before this test.
        var response = await client.PostAsync(
            $"/api/v1/stores/{factory.SecondaryStore}/import?topics={topicA}&topics={topicB}", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var listA = await client.GetFromJsonAsync<ListResponse>(
            $"/api/v1/stores/{factory.SecondaryStore}/memories?scopes={topicA}");
        listA.Should().NotBeNull();
        listA!.Memories.Should().ContainSingle(m => m.Name == "note-a" && m.Description == markerA,
            because: "topic-a was one of the two repeated topics keys, so its entry must have landed");

        var listB = await client.GetFromJsonAsync<ListResponse>(
            $"/api/v1/stores/{factory.SecondaryStore}/memories?scopes={topicB}");
        listB.Should().NotBeNull();
        listB!.Memories.Should().ContainSingle(m => m.Name == "note-b" && m.Description == markerB,
            because: "topic-b was one of the two repeated topics keys, so its entry must have landed");

        var listC = await client.GetFromJsonAsync<ListResponse>(
            $"/api/v1/stores/{factory.SecondaryStore}/memories?scopes={topicC}");
        listC.Should().NotBeNull();
        listC!.Memories.Should().BeEmpty(
            because: "topic-c was not among the repeated topics keys, so it must have been filtered out");
    }

    /// <summary>
    /// RULED 2026-08-10: an import that matched no topics is not a success. Returning 200 made a
    /// deploy step gating on the status code pass while nothing had been imported.
    /// </summary>
    [Fact]
    public async Task ImportBundle_WhenNoTopicsMatch_Returns422()
    {
        using var factory = new ScriniaServerFactory();
        using var client = factory.CreateAuthenticatedClient();

        const string topic = "import-no-match-topic";
        const string marker = "MARKER-import-no-topics-match";
        await StoreMemoryAsync(client, factory.PrimaryStore, topic, "note", marker);

        byte[] bundleBytes = await ExportBundleAsync(client, factory.PrimaryStore, topic);

        using var content = new MultipartFormDataContent();
        var bundle = new ByteArrayContent(bundleBytes);
        bundle.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(bundle, "file", "x.scrinia-bundle");

        var response = await client.PostAsync(
            $"/api/v1/stores/{factory.SecondaryStore}/import?topics=does-not-exist-in-bundle", content);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableContent,
            because: "the bundle contained a topic, but the filter matched none of them, so nothing " +
                     "was imported and that is not a success");
    }

    /// <summary>Stores a single-chunk memory under <c>/{topic}/{subject}</c>, using <paramref
    /// name="content"/> as both the body and the description so the returned content preview is
    /// a reliable, distinguishing marker for round-trip assertions.</summary>
    private static async Task StoreMemoryAsync(HttpClient client, string store, string topic, string subject, string content)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/stores/{store}/memories",
            new StoreRequest([content], $"/{topic}/{subject}", content));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    /// <summary>Exports the given topics from <paramref name="store"/> via the real export
    /// endpoint and returns the raw bundle bytes, for use as multipart upload content.</summary>
    private static async Task<byte[]> ExportBundleAsync(HttpClient client, string store, params string[] topics)
    {
        var resp = await client.PostAsJsonAsync($"/api/v1/stores/{store}/export", new ExportRequest(topics));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return await resp.Content.ReadAsByteArrayAsync();
    }
}

/// <summary>
/// A second, standalone WebApplicationFactory (rather than reusing ScriniaServerFactory) so
/// swapping in <see cref="FakeNonLocalBackend"/> cannot leak into the stores every other test in
/// this assembly shares via the IClassFixture instance.
/// </summary>
public sealed class NonLocalStoreServerFactory : WebApplicationFactory<Program>
{
    private readonly string _tempDir;

    public string RemoteStore { get; } = "remote-store";
    private string TestApiKey { get; set; } = "";

    public NonLocalStoreServerFactory()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinium-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Scrinia:DataDir", _tempDir);
        builder.UseSetting($"Scrinia:Stores:{RemoteStore}", Path.Combine(_tempDir, "stores", RemoteStore));

        builder.ConfigureServices(services =>
        {
            // Registered after Program.cs's own AddSingleton<IStorageBackend, FilesystemBackend>(),
            // so this wins on resolution — the same override mechanism StoreManager's factory
            // delegate depends on to let plugins replace the backend (Program.cs:95).
            services.AddSingleton<IStorageBackend>(new FakeNonLocalBackend());

            var sp = services.BuildServiceProvider();
            var keyStore = sp.GetRequiredService<ApiKeyStore>();
            var (rawKey, _, _) = keyStore.CreateKey(
                "non-local-test-user", [RemoteStore], ["export", "import"], "non-local-test");
            TestApiKey = rawKey;
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestApiKey);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }
}

/// <summary>Hands back a store that is deliberately not an <see cref="ILocalMemoryStore"/>,
/// regardless of the requested name — standing in for a plugin-supplied remote backend.</summary>
internal sealed class FakeNonLocalBackend : IStorageBackend
{
    public string BackendId => "fake-non-local";
    public IMemoryStore CreateStore(string storeName, string storePath) => new FakeNonLocalMemoryStore();
}

/// <summary>
/// A do-nothing <see cref="IMemoryStore"/> that deliberately does NOT implement
/// <see cref="ILocalMemoryStore"/> — the point of the double. Not shared with
/// tests/Scrinia.Tests/FakeMemoryStore.cs: that type is internal to the Scrinia.Tests assembly
/// and this test project has no visibility into it.
/// </summary>
internal sealed class FakeNonLocalMemoryStore : IMemoryStore
{
    public (string Scope, string Subject) ParseQualifiedName(string name) => ("local", name);
    public string FormatQualifiedName(string scope, string subject) => $"{scope}:{subject}";
    public bool IsEphemeral(string name) => name.StartsWith('~');
    public string SanitizeName(string name) => name;
    public Task<string> ResolveArtifactAsync(string nameOrArtifact, CancellationToken ct = default) => Task.FromResult("");
    public IReadOnlyList<ArtifactEntry> LoadIndex(string scope = "local") => [];
    public void Upsert(ArtifactEntry entry, string scope = "local") { }
    public bool Remove(string name, string scope = "local") => false;
    public List<ScopedArtifact> ListScoped(string? scopes = null) => [];
    public IReadOnlyList<SearchResult> SearchAll(SearchQuery query) => [];
    public void RememberEphemeral(string key, EphemeralEntry entry) { }
    public bool ForgetEphemeral(string key) => false;
    public EphemeralEntry? GetEphemeral(string key) => null;
    public bool CopyMemory(string src, string dst, bool overwrite, out string message) { message = ""; return false; }
    public void ArchiveVersion(string subject, string scope = "local") { }
    public string ArtifactUri(string name, string scope = "local") => "";
    public Task WriteArtifactAsync(string subject, string scope, string artifactText,
        ArtifactWriteMetadata? meta = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> ReadArtifactAsync(string subject, string scope, CancellationToken ct = default) => Task.FromResult("");
    public bool DeleteArtifact(string subject, string scope) => false;
    public string[] DiscoverTopics() => [];
    public List<TopicInfo> GatherTopicInfos(string? scopes = null) => [];
    public IReadOnlyList<string> ResolveReadScopes(string? scopes = null) => ["local"];
}
