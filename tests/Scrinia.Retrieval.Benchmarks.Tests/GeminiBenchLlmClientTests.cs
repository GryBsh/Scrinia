using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Tests for <see cref="GeminiBenchLlmClient"/>. Stubbed
/// <see cref="HttpMessageHandler"/> asserts generateContent request shape
/// (POST /v1beta/models/{model}:generateContent, x-goog-api-key header, system
/// instruction + user turn, generation config).
/// </summary>
public class GeminiBenchLlmClientTests
{
    private static LlmOptions Options(string? apiKey = "test-key", string baseUrl = "https://stub.gemini.test") =>
        new()
        {
            Model = "gemini-test-model",
            GeminiApiKey = apiKey,
            GeminiBaseUrl = baseUrl,
            Temperature = 0.1,
            RequestTimeoutSeconds = 30,
        };

    private static string GenerateContentResponse(string text) =>
        $$"""
        {
          "candidates": [{
            "content": {"parts": [{"text": {{JsonSerializer.Serialize(text)}}}]},
            "finishReason": "STOP"
          }]
        }
        """;

    [Fact]
    public async Task CompleteAsync_PostsToGenerateContentEndpoint_WithApiKeyHeader()
    {
        HttpRequestMessage? captured = null;
        var handler = StubHandler.Sync((req, _) =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(GenerateContentResponse("Gemini reply."), Encoding.UTF8, "application/json"),
            };
        });
        using var llm = new GeminiBenchLlmClient(Options(), new HttpClient(handler));

        string? reply = await llm.CompleteAsync("sysprompt", "userprompt", maxTokens: 80, jsonMode: false, CancellationToken.None);

        reply.Should().Be("Gemini reply.");
        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.ToString().Should().Be(
            "https://stub.gemini.test/v1beta/models/gemini-test-model:generateContent");
        captured.Headers.GetValues("x-goog-api-key").Should().ContainSingle().Which.Should().Be("test-key");
    }

    [Fact]
    public async Task RequestBody_HasSystemInstructionAndUserTurn()
    {
        string? bodyJson = null;
        var handler = StubHandler.Async(async (req, ct) =>
        {
            bodyJson = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(GenerateContentResponse("ok"), Encoding.UTF8, "application/json"),
            };
        });
        using var llm = new GeminiBenchLlmClient(Options(), new HttpClient(handler));

        await llm.CompleteAsync("system text", "user content", maxTokens: 120, jsonMode: false, CancellationToken.None);

        bodyJson.Should().NotBeNull();
        using var doc = JsonDocument.Parse(bodyJson!);
        var root = doc.RootElement;
        root.GetProperty("system_instruction").GetProperty("parts")[0].GetProperty("text").GetString().Should().Be("system text");
        var contents = root.GetProperty("contents");
        contents.GetArrayLength().Should().Be(1);
        contents[0].GetProperty("role").GetString().Should().Be("user");
        contents[0].GetProperty("parts")[0].GetProperty("text").GetString().Should().Be("user content");
        root.GetProperty("generationConfig").GetProperty("temperature").GetDouble().Should().Be(0.1);
        root.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32().Should().Be(120);
    }

    [Fact]
    public async Task CompleteAsync_OmitsResponseMimeType_WhenJsonModeFalse()
    {
        string? bodyJson = null;
        var handler = StubHandler.Async(async (req, ct) =>
        {
            bodyJson = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(GenerateContentResponse("ok"), Encoding.UTF8, "application/json"),
            };
        });
        using var llm = new GeminiBenchLlmClient(Options(), new HttpClient(handler));

        await llm.CompleteAsync("s", "u", maxTokens: 50, jsonMode: false, CancellationToken.None);

        using var doc = JsonDocument.Parse(bodyJson!);
        doc.RootElement.GetProperty("generationConfig")
            .TryGetProperty("responseMimeType", out _).Should().BeFalse(
                because: "jsonMode=false must omit responseMimeType");
    }

    [Fact]
    public async Task CompleteAsync_SetsResponseMimeType_WhenJsonModeTrue()
    {
        string? bodyJson = null;
        var handler = StubHandler.Async(async (req, ct) =>
        {
            bodyJson = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(GenerateContentResponse("""{"k":"v"}"""), Encoding.UTF8, "application/json"),
            };
        });
        using var llm = new GeminiBenchLlmClient(Options(), new HttpClient(handler));

        await llm.CompleteAsync("s", "u", maxTokens: 50, jsonMode: true, CancellationToken.None);

        using var doc = JsonDocument.Parse(bodyJson!);
        doc.RootElement.GetProperty("generationConfig").GetProperty("responseMimeType").GetString()
            .Should().Be("application/json");
    }

    [Fact]
    public async Task ParsesFirstCandidate_AndIgnoresExtras()
    {
        var handler = StubHandler.Sync((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"candidates":[{"content":{"parts":[{"text":"first reply"}]}},{"content":{"parts":[{"text":"second"}]}}]}""",
                Encoding.UTF8, "application/json"),
        });
        using var llm = new GeminiBenchLlmClient(Options(), new HttpClient(handler));

        (await llm.CompleteAsync("s", "u", maxTokens: 50, jsonMode: false, CancellationToken.None))
            .Should().Be("first reply");
    }

    [Fact]
    public async Task Non2xx_ReturnsNull()
    {
        var handler = StubHandler.Sync((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var llm = new GeminiBenchLlmClient(Options(), new HttpClient(handler));

        (await llm.CompleteAsync("s", "u", maxTokens: 50, jsonMode: false, CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task IsAvailable_True_WhenModelsEndpointReturns200()
    {
        var handler = StubHandler.Sync((req, _) =>
        {
            req.Method.Should().Be(HttpMethod.Get);
            req.RequestUri!.ToString().Should().Be("https://stub.gemini.test/v1beta/models");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"models":[]}""", Encoding.UTF8, "application/json"),
            };
        });
        using var llm = new GeminiBenchLlmClient(Options(), new HttpClient(handler));

        (await llm.IsAvailableAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task EmptyCandidateList_ReturnsNull()
    {
        var handler = StubHandler.Sync((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"candidates": []}""", Encoding.UTF8, "application/json"),
        });
        using var llm = new GeminiBenchLlmClient(Options(), new HttpClient(handler));

        (await llm.CompleteAsync("s", "u", maxTokens: 50, jsonMode: false, CancellationToken.None))
            .Should().BeNull();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        private StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> r) => _respond = r;
        public static StubHandler Sync(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> r) =>
            new((req, ct) => Task.FromResult(r(req, ct)));
        public static StubHandler Async(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> r) =>
            new(r);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => _respond(request, ct);
    }
}
