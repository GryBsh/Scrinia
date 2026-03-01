using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Scrinia.Server.Models;
using Xunit;

namespace Scrinia.Server.Tests;

public sealed class MemoryEndpointTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;
    private readonly HttpClient _client;
    private readonly string _base;

    public MemoryEndpointTests(ScriniaServerFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _base = $"/api/v1/stores/{factory.PrimaryStore}";
    }

    [Fact]
    public async Task Store_and_Show_roundtrip()
    {
        var req = new StoreRequest(["Hello, World!"], "roundtrip-test", "A test memory");
        var storeResp = await _client.PostAsJsonAsync($"{_base}/memories", req);
        storeResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var storeBody = await storeResp.Content.ReadFromJsonAsync<StoreResponse>();
        storeBody.Should().NotBeNull();
        storeBody!.QualifiedName.Should().Be("roundtrip-test");
        storeBody.ChunkCount.Should().Be(1);

        var showResp = await _client.GetAsync($"{_base}/memories/roundtrip-test");
        showResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var showBody = await showResp.Content.ReadFromJsonAsync<ShowResponse>();
        showBody.Should().NotBeNull();
        showBody!.Content.Should().Be("Hello, World!");
    }

    [Fact]
    public async Task List_returns_stored_memories()
    {
        var req = new StoreRequest(["List test content"], "list-test", "For list");
        await _client.PostAsJsonAsync($"{_base}/memories", req);

        var listResp = await _client.GetAsync($"{_base}/memories");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var listBody = await listResp.Content.ReadFromJsonAsync<ListResponse>();
        listBody.Should().NotBeNull();
        listBody!.Total.Should().BeGreaterThan(0);
        listBody.Memories.Should().Contain(m => m.QualifiedName == "list-test");
    }

    [Fact]
    public async Task Search_finds_matching_memory()
    {
        var req = new StoreRequest(["Kubernetes deployment patterns with pods and services"], "k8s-patterns", "K8s guide");
        await _client.PostAsJsonAsync($"{_base}/memories", req);

        var searchResp = await _client.GetAsync($"{_base}/search?q=kubernetes");
        searchResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var searchBody = await searchResp.Content.ReadFromJsonAsync<SearchResponse>();
        searchBody.Should().NotBeNull();
        searchBody!.Results.Should().Contain(r => r.Name == "k8s-patterns");
    }

    [Fact]
    public async Task Forget_removes_memory()
    {
        var req = new StoreRequest(["Temporary content"], "forget-test");
        await _client.PostAsJsonAsync($"{_base}/memories", req);

        var deleteResp = await _client.DeleteAsync($"{_base}/memories/forget-test");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var showResp = await _client.GetAsync($"{_base}/memories/forget-test");
        showResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Append_adds_chunk_to_existing()
    {
        var req = new StoreRequest(["First chunk"], "append-test");
        await _client.PostAsJsonAsync($"{_base}/memories", req);

        var appendResp = await _client.PostAsJsonAsync(
            $"{_base}/memories/append-test/append",
            new AppendRequest("Second chunk"));
        appendResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var appendBody = await appendResp.Content.ReadFromJsonAsync<AppendResponse>();
        appendBody.Should().NotBeNull();
        appendBody!.ChunkCount.Should().Be(2);
    }

    [Fact]
    public async Task Copy_duplicates_memory()
    {
        var req = new StoreRequest(["Copy source"], "copy-src");
        await _client.PostAsJsonAsync($"{_base}/memories", req);

        var copyResp = await _client.PostAsJsonAsync(
            $"{_base}/memories/copy-src/copy",
            new CopyRequest("copy-dst"));
        copyResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var showResp = await _client.GetAsync($"{_base}/memories/copy-dst");
        showResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var showBody = await showResp.Content.ReadFromJsonAsync<ShowResponse>();
        showBody!.Content.Should().Be("Copy source");
    }

    [Fact]
    public async Task Chunks_endpoint_returns_chunk()
    {
        var req = new StoreRequest(["Chunk A", "Chunk B"], "chunked-test");
        await _client.PostAsJsonAsync($"{_base}/memories", req);

        var chunkResp = await _client.GetAsync($"{_base}/memories/chunked-test/chunks/2");
        chunkResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var chunkBody = await chunkResp.Content.ReadFromJsonAsync<ChunkResponse>();
        chunkBody.Should().NotBeNull();
        chunkBody!.Content.Should().Be("Chunk B");
        chunkBody.ChunkIndex.Should().Be(2);
        chunkBody.TotalChunks.Should().Be(2);
    }

    [Fact]
    public async Task Show_returns_404_for_missing()
    {
        var resp = await _client.GetAsync($"{_base}/memories/does-not-exist");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Search_without_query_returns_400()
    {
        var resp = await _client.GetAsync($"{_base}/search");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

}
