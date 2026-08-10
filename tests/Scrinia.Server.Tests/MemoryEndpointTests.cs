using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Scrinia.Core.Search;
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
    public async Task List_respects_offset_and_limit_with_total_unchanged()
    {
        // Seed 12 deterministically-named entries.
        for (int i = 0; i < 12; i++)
        {
            var req = new StoreRequest([$"pagination chunk {i}"], $"page-{i:D2}");
            await _client.PostAsJsonAsync($"{_base}/memories", req);
        }

        var page1 = await _client.GetFromJsonAsync<ListResponse>($"{_base}/memories?limit=5&offset=0");
        page1.Should().NotBeNull();
        page1!.Memories.Length.Should().BeLessOrEqualTo(5,
            because: "limit must be honoured exactly");
        int totalSeenInPage1 = page1.Total;
        totalSeenInPage1.Should().BeGreaterOrEqualTo(12,
            because: "Total must reflect the unpaginated count, not the page size");

        var page2 = await _client.GetFromJsonAsync<ListResponse>($"{_base}/memories?limit=5&offset=5");
        page2.Should().NotBeNull();
        page2!.Total.Should().Be(totalSeenInPage1,
            because: "Total is stable across pages within a list call");

        var page1Names = page1.Memories.Select(m => m.QualifiedName).ToHashSet();
        var page2Names = page2.Memories.Select(m => m.QualifiedName).ToHashSet();
        page1Names.Should().NotIntersectWith(page2Names,
            because: "consecutive pages must not return overlapping entries");
    }

    [Fact]
    public async Task List_clamps_limit_above_max()
    {
        // Asking for limit > 1000 must be clamped silently — the response should never
        // exceed the hard cap regardless of how many entries exist.
        var resp = await _client.GetFromJsonAsync<ListResponse>($"{_base}/memories?limit=999999");
        resp.Should().NotBeNull();
        resp!.Memories.Length.Should().BeLessOrEqualTo(1000,
            because: "limit must be clamped to MaxListLimit (1000)");
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

    [Fact]
    public async Task List_description_reflects_content_preview_not_legacy_description()
    {
        // Content longer than the legacy 200-char Description truncation but within the
        // 500-char ContentPreview cap, so the two carriers diverge when no explicit
        // description is supplied. The tag arrives on the request's attribute map under the "tag"
        // key, which now surfaces on list via the full Attributes map (not a DisplayTags flatten).
        string longContent = string.Concat(Enumerable.Repeat("segment ", 40)).TrimEnd();
        var req = new StoreRequest([longContent], "preview-cutover-test", Description: "",
            Attributes: new Dictionary<string, string[]> { [KnownAttributes.TagKey] = ["alpha"] });
        var storeResp = await _client.PostAsJsonAsync($"{_base}/memories", req);
        storeResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var listBody = await _client.GetFromJsonAsync<ListResponse>($"{_base}/memories?limit=1000");
        listBody.Should().NotBeNull();
        var item = listBody!.Memories.Should().ContainSingle(m => m.QualifiedName == "preview-cutover-test").Subject;

        item.Description.Should().Be(longContent,
            because: "display Description now sources ContentPreview (spec: Description fully removed from display), not the legacy 200-char truncation");
        item.Attributes.Should().NotBeNull();
        item.Attributes!.Should().ContainKey(KnownAttributes.TagKey)
            .WhoseValue.Should().Contain("alpha");
    }

    private async Task SeedAsync(string name, string body, string[]? tags = null)
    {
        var resp = await _client.PostAsJsonAsync($"{_base}/memories",
            new StoreRequest([body], name, body,
                Attributes: tags is { Length: > 0 }
                    ? new Dictionary<string, string[]> { [KnownAttributes.TagKey] = tags }
                    : null));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Search_Get_WithPathPrefix_ExcludesOtherSubtrees()
    {
        await SeedAsync("/api/alpha-note", "alpha content");
        await SeedAsync("/goal/G-9/alpha-note", "alpha content");

        var resp = await _client.GetFromJsonAsync<SearchResponse>(
            $"{_base}/search?q=alpha&limit=50&pathPrefix=/api/");

        resp!.Results.Select(r => r.Name).Should().Contain(n => n.Contains("/api/alpha-note"));
        resp.Results.Select(r => r.Name).Should().NotContain(n => n.Contains("/goal/"));
    }

    [Fact]
    public async Task Search_Post_WithAttributeFilter_ReturnsOnlyMatchingEntries()
    {
        await SeedAsync("/api/tagged-note", "beta content", tags: ["urgent"]);
        await SeedAsync("/api/untagged-note", "beta content");

        var resp = await _client.PostAsJsonAsync($"{_base}/search",
            new SearchRequest("beta", Limit: 50,
                AttributeFilter: new Dictionary<string, string[]> { [KnownAttributes.TagKey] = ["urgent"] }));
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>();

        body!.Results.Select(r => r.Name).Should().Contain(n => n.Contains("tagged-note"));
        body.Results.Select(r => r.Name).Should().NotContain(n => n.Contains("untagged-note"));
    }

    [Fact]
    public async Task Search_Post_MissingQuery_ReturnsBadRequest()
    {
        var resp = await _client.PostAsJsonAsync($"{_base}/search", new SearchRequest("", Limit: 10));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
