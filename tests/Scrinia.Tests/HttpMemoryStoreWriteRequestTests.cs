using System.Net;
using System.Text.Json;
using FluentAssertions;
using Scrinia;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Resilience;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// The client half of the store wire: <see cref="HttpMemoryStore.WriteArtifactAsync"/> must put the
/// <see cref="ArtifactWriteMetadata"/> it is handed into the request body.
///
/// This is the assertion the server-side StoreAttributesWireTests cannot make. That test proves the
/// server persists an attribute map it receives; this one proves the client sends one. Until this
/// change the only construction here was <c>new StoreApiRequest([text], qualifiedName)</c> — the
/// metadata parameter was accepted and dropped, and remember() against a remote store reported
/// success while discarding every agent-supplied attribute.
/// </summary>
public class HttpMemoryStoreWriteRequestTests
{
    /// <summary>Captures the request body verbatim and answers 201, so the assertions run against
    /// the bytes that would actually leave the process.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    [Fact]
    public async Task WriteArtifactAsync_PutsWriteMetadataOnTheRequest()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        using var store = new HttpMemoryStore(http, "default", retryOptions: new RetryOptions(MaxRetries: 0));

        await store.WriteArtifactAsync("note", "local", Nmp2ChunkedEncoder.Encode("hello world"),
            new ArtifactWriteMetadata(
                Attributes: new Dictionary<string, string[]> { ["person"] = ["maria"], ["custom"] = ["v"] },
                Keywords: ["portcullis"],
                Description: "a curated preview"));

        handler.Body.Should().NotBeNull("precondition: the write posted a body");
        using var doc = JsonDocument.Parse(handler.Body!);
        var root = doc.RootElement;

        var attributes = root.GetProperty("attributes");
        attributes.GetProperty("person").EnumerateArray().Select(v => v.GetString())
            .Should().Contain("maria");
        attributes.GetProperty("custom").EnumerateArray().Select(v => v.GetString())
            .Should().Contain("v");

        // Keywords travel WITH the attributes, not instead of them. The map crossing here is the
        // caller's pre-AttributeMigration.Forward map; the server re-runs Forward to re-fold
        // recognized "key:value" keywords into attributes. Drop the keywords from the wire and
        // Forward has nothing to re-fold, so that class of agent-supplied keyword is lost silently.
        root.GetProperty("keywords").EnumerateArray().Select(v => v.GetString())
            .Should().Contain("portcullis");

        root.GetProperty("description").GetString().Should().Be("a curated preview");
    }

    [Fact]
    public async Task WriteArtifactAsync_WithoutMetadata_OmitsTheOptionalMembers()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        using var store = new HttpMemoryStore(http, "default", retryOptions: new RetryOptions(MaxRetries: 0));

        await store.WriteArtifactAsync("note", "local", Nmp2ChunkedEncoder.Encode("hello world"));

        using var doc = JsonDocument.Parse(handler.Body!);
        doc.RootElement.TryGetProperty("attributes", out _).Should().BeFalse(
            because: "a write with no metadata must not send an empty or null attribute map");
        doc.RootElement.TryGetProperty("keywords", out _).Should().BeFalse();
        doc.RootElement.GetProperty("name").GetString().Should().Be("note");
    }
}
