using System.Text.Json;
using FluentAssertions;

namespace Scrinia.Server.Tests;

public sealed class OpenApiTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;

    public OpenApiTests(ScriniaServerFactory factory) => _factory = factory;

    [Fact]
    public async Task OpenApi_spec_returns_valid_json()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("\"openapi\"");
    }

    /// <summary>
    /// R-2. <c>PatchEntry</c> takes <c>HttpRequest</c> instead of a typed parameter (it parses the
    /// body manually), which strips the request body from the generated OpenAPI document unless
    /// <c>.Accepts&lt;MemoryEntry&gt;("application/json")</c> is chained onto the <c>MapPatch</c>
    /// registration — measured across both commits with the same probe: at <c>6ccea4a</c>,
    /// <c>PATCH /memories/{name}</c> had a request body in the generated document; at <c>aaf0914</c>
    /// it did not, so a generated client would see a PATCH that accepts no body. The existing
    /// <c>OpenApi_spec_returns_valid_json</c> test only checks the document parses, which is why
    /// nothing caught this — this test opens the actual path/operation and asserts the request body
    /// is present.
    /// </summary>
    [Fact]
    public async Task OpenApi_Patch_MemoriesByName_HasARequestBody()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = doc.RootElement.GetProperty("paths");
        paths.TryGetProperty("/api/v1/stores/{store}/memories/{name}", out var pathItem).Should().BeTrue(
            because: "the PATCH route must appear in the generated document under its full path");
        pathItem.TryGetProperty("patch", out var patchOp).Should().BeTrue(
            because: "the path item must carry a patch operation");

        patchOp.TryGetProperty("requestBody", out _).Should().BeTrue(
            because: "PatchEntry's HttpRequest parameter strips the body from generated OpenAPI " +
                     "unless .Accepts<MemoryEntry>(...) restores it — a generated client must see " +
                     "that this PATCH accepts a body");
    }

    [Fact]
    public async Task Scalar_ui_returns_html()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/scalar/v1");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("html");
    }
}
