using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Scrinia.Server.Models;
using Xunit;

namespace Scrinia.Server.Tests;

/// <summary>
/// The search endpoint already bounds how many attribute *keys* a filter may carry
/// (attributeFilter is { Count: > 64 }, see RunSearch in MemoryEndpoints.cs), but until this
/// fix nothing bounded how many *values* a single key's array may carry. Kestrel's body limit
/// was the only backstop. These tests pin the per-key value-array bound (64, mirroring the
/// key-count bound) added immediately after the existing key-count check.
/// </summary>
public sealed class SearchAttributeFilterBoundsTests : IClassFixture<ScriniaServerFactory>
{
    private readonly HttpClient _client;
    private readonly string _base;

    public SearchAttributeFilterBoundsTests(ScriniaServerFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _base = $"/api/v1/stores/{factory.PrimaryStore}";
    }

    [Fact]
    public async Task Search_Post_AttributeFilterKeyWith1000Values_ReturnsBadRequest()
    {
        // Must POST: the GET handler (SearchMemory) passes attributeFilter: null unconditionally,
        // so a GET can never reach the check in the shared RunSearch implementation.
        var oversizedValues = Enumerable.Range(0, 1000).Select(i => $"value-{i}").ToArray();
        var req = new SearchRequest("beta", Limit: 10,
            AttributeFilter: new Dictionary<string, string[]> { ["tag"] = oversizedValues });

        var resp = await _client.PostAsJsonAsync($"{_base}/search", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "a single attributeFilter key carrying 1000 values is a resource-exhaustion " +
                     "vector the endpoint must reject, same as it already rejects too many keys");

        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body.Should().NotBeNull();
        body!.Error.Should().Contain("attributeFilter",
            because: "a bare 400 assertion would also pass if the request were rejected for an " +
                     "unrelated reason (e.g. missing query)");
    }

    [Fact]
    public async Task Search_Post_AttributeFilterKeyWithinBound_ReturnsOk()
    {
        var withinBoundValues = Enumerable.Range(0, 64).Select(i => $"value-{i}").ToArray();
        var req = new SearchRequest("beta", Limit: 10,
            AttributeFilter: new Dictionary<string, string[]> { ["tag"] = withinBoundValues });

        var resp = await _client.PostAsJsonAsync($"{_base}/search", req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "64 values on a single key is comfortably inside the bound; without this " +
                     "test a bound of zero would still pass the oversized-case test above");
    }
}
