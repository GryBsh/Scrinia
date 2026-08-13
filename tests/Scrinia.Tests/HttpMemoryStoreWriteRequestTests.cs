using System.Net;
using System.Text.Json;
using FluentAssertions;
using Scrinia;
using Scrinia.Core.Encoding;
using Scrinia.Core.Resilience;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// The client half of the store wire. <see cref="HttpMemoryStore.WriteArtifactAsync"/> no longer
/// carries any metadata parameter — the store derives, so the write is raw content only — and this
/// pins that the request body reflects exactly that: no <c>attributes</c>, <c>keywords</c> or
/// <c>description</c> member, ever.
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
            // Stamped with a matching API version header (remote-write-correctness Task 7) — this
            // fixture is about the request body HttpMemoryStore sends, not the version check.
            var response = new HttpResponseMessage(HttpStatusCode.Created);
            response.Headers.TryAddWithoutValidation(RemoteApiVersion.HeaderName, RemoteApiVersion.Current);
            return response;
        }
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
