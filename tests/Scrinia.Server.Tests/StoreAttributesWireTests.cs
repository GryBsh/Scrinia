using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Server.Models;
using Scrinia.Server.Services;

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

    /// <summary>
    /// The list response wire carries the full attributes map, not a flattened tag list.
    ///
    /// Why the response is read as raw JSON rather than through the client's typed
    /// <see cref="ListResponse"/>: at the point this test is written the DTO still exposes the old
    /// <c>tags</c> field, so a typed read would fail to compile instead of failing the assertion —
    /// exactly the "goes green for the wrong reason" trap Task 3's sibling test above avoids on the
    /// request side. Reading raw JSON keeps the test meaningful before AND after the DTO rename.
    ///
    /// Why <c>person</c> and <c>custom</c> and not <c>tag</c>: the old projection —
    /// <c>AttributeReaders.DisplayTags(item.Entry.Attributes).ToArray()</c> — could already surface
    /// the <c>tag</c> key. Asserting on it would pass under that flattening and prove nothing about
    /// the full map reaching the wire.
    /// </summary>
    [Fact]
    public async Task List_response_attributes_reach_the_wire()
    {
        using var factory = new ScriniaServerFactory();
        var client = factory.CreateAuthenticatedClient();

        var storeResp = await client.PostAsync(
            $"/api/v1/stores/{factory.PrimaryStore}/memories",
            Json("""
                 {
                   "content": ["List probe body with no inline attribute syntax."],
                   "name": "list-attributes-test",
                   "attributes": { "person": ["maria"], "custom": ["v"] }
                 }
                 """));
        storeResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var listResp = await client.GetAsync(
            $"/api/v1/stores/{factory.PrimaryStore}/memories?limit=1000");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("memories").EnumerateArray().ToList();
        var item = items.Single(m => m.GetProperty("qualifiedName").GetString() == "list-attributes-test");

        item.TryGetProperty("attributes", out var attributes).Should().BeTrue(
            because: "the response must carry the attributes map, not a flattened tag list");
        attributes.GetProperty("person").EnumerateArray().Select(v => v.GetString())
            .Should().Contain("maria");
        attributes.GetProperty("custom").EnumerateArray().Select(v => v.GetString())
            .Should().Contain("v");
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
    public async Task Store_rejects_more_values_on_a_single_key_than_the_shared_bound()
    {
        using var factory = new ScriniaServerFactory();
        var client = factory.CreateAuthenticatedClient();

        var values = Enumerable.Range(0, Scrinia.Core.Search.KnownAttributes.MaxValuesPerKey + 1)
            .Select(i => $"\"v{i}\"");
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
        body!.Error.Should().Contain(
            $"{Scrinia.Core.Search.KnownAttributes.MaxValuesPerKey} values per key");
    }

    /// <summary>
    /// The bound used to be 64, remote-only, and reachable from ROUTINE auto-extraction rather than
    /// from a caller deliberately supplying too many values: nothing caps <c>file</c>/<c>ref</c>
    /// values locally, so a file-reference-heavy memory citing 70 distinct paths stored fine
    /// in-process and came back <c>400 attributes may not exceed 64 values per key</c> over HTTP.
    /// 70 is the measured repro count. It must now store, on both sides of the wire.
    /// </summary>
    [Fact]
    public async Task Store_accepts_seventy_file_refs_the_local_path_also_accepts()
    {
        using var factory = new ScriniaServerFactory();
        var client = factory.CreateAuthenticatedClient();

        var values = Enumerable.Range(0, 70).Select(i => $"\"src/Scrinia/File{i}.cs\"");
        var resp = await client.PostAsync(
            $"/api/v1/stores/{factory.PrimaryStore}/memories",
            Json($$"""
                  {
                    "content": ["file-reference-heavy probe"],
                    "name": "wire-attributes-seventy-file-refs",
                    "attributes": { "file": [{{string.Join(",", values)}}] }
                  }
                  """));

        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            because: "70 distinct file refs is an ordinary auto-extraction result, not abuse — " +
                     "rejecting it made remote reject content the local store accepts");
    }

    // ── The append path, which grew an attribute map of its own ────────────────

    /// <summary>
    /// <c>AppendRequest</c> used to be <c>(string Content)</c> — no attributes at all — so the two
    /// bounds above lived only on <c>POST /memories</c> and there was nothing on the append path
    /// for them to guard. Widening it to mirror <c>MemoryWrite</c> opens a second door to the exact
    /// same <c>KnownAttributes.Merge</c>, which truncates past <c>MaxAttributes</c> with NO signal.
    /// A bound enforced on one door and not the other is not a bound.
    /// <para>Both caps are asserted, not just one: they are separate checks, and a copy of the guard
    /// that enforced only the key cap would satisfy a single-case test while leaving the values cap
    /// open on this path.</para>
    /// </summary>
    [Fact]
    public async Task Append_rejects_more_attribute_keys_than_the_store_path_does()
    {
        using var factory = new ScriniaServerFactory();
        var client = factory.CreateAuthenticatedClient();
        string basePath = $"/api/v1/stores/{factory.PrimaryStore}/memories";

        (await client.PostAsync(basePath, Json("""
            {"content": ["seed"], "name": "wire-append-attr-bounds"}
            """))).StatusCode.Should().Be(HttpStatusCode.Created);

        var pairs = Enumerable.Range(0, Scrinia.Core.Search.KnownAttributes.MaxAttributes + 1)
            .Select(i => $"\"k{i}\":[\"v\"]");
        var resp = await client.PostAsync(
            $"{basePath}/wire-append-attr-bounds/append",
            Json($$"""
                  {
                    "content": "over-cap probe",
                    "attributes": { {{string.Join(",", pairs)}} }
                  }
                  """));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain($"{Scrinia.Core.Search.KnownAttributes.MaxAttributes} keys");
    }

    /// <inheritdoc cref="Append_rejects_more_attribute_keys_than_the_store_path_does"/>
    [Fact]
    public async Task Append_rejects_more_values_on_a_single_key_than_the_store_path_does()
    {
        using var factory = new ScriniaServerFactory();
        var client = factory.CreateAuthenticatedClient();
        string basePath = $"/api/v1/stores/{factory.PrimaryStore}/memories";

        (await client.PostAsync(basePath, Json("""
            {"content": ["seed"], "name": "wire-append-value-bounds"}
            """))).StatusCode.Should().Be(HttpStatusCode.Created);

        var values = Enumerable.Range(0, Scrinia.Core.Search.KnownAttributes.MaxValuesPerKey + 1)
            .Select(i => $"\"v{i}\"");
        var resp = await client.PostAsync(
            $"{basePath}/wire-append-value-bounds/append",
            Json($$"""
                  {
                    "content": "over-cap probe",
                    "attributes": { "person": [{{string.Join(",", values)}}] }
                  }
                  """));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain(
            $"{Scrinia.Core.Search.KnownAttributes.MaxValuesPerKey} values per key");
    }

    /// <summary>
    /// The other half of the widening, over the wire: an append that carries a description,
    /// attributes and keywords must have all three reach the stored entry. They had no wire field
    /// at all before, so an append made remotely silently kept none of them while the identical
    /// local call kept all three (D-18).
    /// </summary>
    [Fact]
    public async Task Append_carries_description_attributes_and_keywords_to_the_stored_entry()
    {
        using var factory = new ScriniaServerFactory();
        var client = factory.CreateAuthenticatedClient();
        string basePath = $"/api/v1/stores/{factory.PrimaryStore}/memories";

        (await client.PostAsync(basePath, Json("""
            {"content": ["seed body"], "name": "wire-append-metadata"}
            """))).StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await client.PostAsync(
            $"{basePath}/wire-append-metadata/append",
            Json("""
                 {
                   "content": "appended body",
                   "description": "a description supplied at append time",
                   "attributes": { "person": ["maria"] },
                   "keywords": ["zebra"]
                 }
                 """));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AppendResponse>();
        body.Should().NotBeNull();
        body!.Entry.Description.Should().Be("a description supplied at append time");
        body.Entry.Attributes.Should().NotBeNull().And.ContainKey("person");
        body.Entry.Keywords.Should().NotBeNull().And.Contain("zebra");
        body.Entry.ChunkCount.Should().Be(2,
            because: "it was an append, not a replace — a metadata assertion on a memory that got " +
                     "silently overwritten would pass for the wrong reason");
    }
}
