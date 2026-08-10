using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Server.Models;
using Scrinia.Server.Services;
using Xunit;

namespace Scrinia.Server.Tests;

/// <summary>
/// The store request wire carries an agent-supplied attribute map.
///
/// Why these assertions read the PERSISTED entry rather than the request that was sent: until this
/// change, the only construction of the client's <c>StoreApiRequest</c> was
/// <c>new StoreApiRequest([text], qualifiedName)</c> — content and name, nothing else. Nothing has
/// ever crossed this wire, so a "does the field round-trip" assertion would have gone green against
/// a wire carrying nothing at all.
///
/// Why <c>person</c> and <c>custom</c> and not <c>tag</c>/<c>review</c>: the removed legacy carriers
/// (<c>tags</c>, <c>reviewAfter</c>, <c>reviewWhen</c>) were translated server-side into exactly the
/// <c>tag</c> and <c>review</c> attribute keys. Asserting on those two would pass under the old
/// translation and prove nothing about the attribute map reaching the store.
///
/// The bodies are raw JSON rather than a typed <see cref="StoreRequest"/> so the test states the
/// wire contract directly and does not silently follow a DTO rename.
/// </summary>
public sealed class StoreAttributesWireTests
{
    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Store_request_attributes_reach_the_persisted_entry()
    {
        using var factory = new ScriniaServerFactory();
        var client = factory.CreateAuthenticatedClient();

        // Content is deliberately free of "key: value" prose so nothing in the assertion below can
        // be satisfied by AttributeExtractor's content extraction instead of the wire.
        var resp = await client.PostAsync(
            $"/api/v1/stores/{factory.PrimaryStore}/memories",
            Json("""
                 {
                   "content": ["Wire probe body with no inline attribute syntax."],
                   "name": "wire-attributes-test",
                   "attributes": { "person": ["maria"], "anchor": ["§3"], "custom": ["v"] }
                 }
                 """));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        var store = factory.Services.GetRequiredService<StoreManager>().GetStore(factory.PrimaryStore);
        var entry = store.ListScoped(null)
            .Should().ContainSingle(a => a.Entry.Name == "wire-attributes-test").Subject.Entry;

        entry.Attributes.Should().NotBeNull(
            because: "the agent-supplied attribute map must survive the request boundary");
        entry.Attributes!.Should().ContainKey("person")
            .WhoseValue.Should().Contain("maria");
        entry.Attributes.Should().ContainKey("anchor")
            .WhoseValue.Should().Contain("§3");
        entry.Attributes.Should().ContainKey("custom")
            .WhoseValue.Should().Contain("v");
    }

    [Fact]
    public async Task Store_rejects_more_attribute_keys_than_the_domain_cap()
    {
        using var factory = new ScriniaServerFactory();
        var client = factory.CreateAuthenticatedClient();

        // MaxAttributes + 1. KnownAttributes.Merge silently truncates past the cap, so the API has
        // to refuse rather than accept-and-drop.
        var pairs = Enumerable.Range(0, Scrinia.Core.Search.KnownAttributes.MaxAttributes + 1)
            .Select(i => $"\"k{i}\":[\"v\"]");
        var resp = await client.PostAsync(
            $"/api/v1/stores/{factory.PrimaryStore}/memories",
            Json($$"""
                  {
                    "content": ["over-cap probe"],
                    "name": "wire-attributes-too-many-keys",
                    "attributes": { {{string.Join(",", pairs)}} }
                  }
                  """));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("32");
    }

    [Fact]
    public async Task Store_rejects_more_than_64_values_on_a_single_attribute_key()
    {
        using var factory = new ScriniaServerFactory();
        var client = factory.CreateAuthenticatedClient();

        var values = Enumerable.Range(0, 65).Select(i => $"\"v{i}\"");
        var resp = await client.PostAsync(
            $"/api/v1/stores/{factory.PrimaryStore}/memories",
            Json($$"""
                  {
                    "content": ["over-cap probe"],
                    "name": "wire-attributes-too-many-values",
                    "attributes": { "person": [{{string.Join(",", values)}}] }
                  }
                  """));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("64 values per key");
    }
}
