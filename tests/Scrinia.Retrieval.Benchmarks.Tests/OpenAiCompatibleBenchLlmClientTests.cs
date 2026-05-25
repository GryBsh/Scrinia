using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Tests for <see cref="OpenAiCompatibleBenchLlmClient"/>. Uses a stubbed
/// <see cref="HttpMessageHandler"/> so no network is required and the request shape
/// (URL, method, headers, body) can be asserted directly.
/// </summary>
public class OpenAiCompatibleBenchLlmClientTests
{
    private static LlmOptions Options(string baseUrl = "http://stub.test/v1", string? apiKey = null) =>
        new()
        {
            BaseUrl = baseUrl,
            Model = "test-model",
            ApiKey = apiKey,
            Temperature = 0.1,
        };

    private static string Chat(string content) =>
        $$"""
        {
          "id": "x",
          "model": "test-model",
          "choices": [{"index": 0, "message": {"role": "assistant", "content": {{JsonSerializer.Serialize(content)}}}, "finish_reason": "stop"}]
        }
        """;

    [Fact]
    public async Task IsAvailable_ReturnsTrue_When_ModelsEndpointReturns200()
    {
        var handler = StubHandler.Sync((req, _) =>
        {
            req.Method.Should().Be(HttpMethod.Get);
            req.RequestUri!.ToString().Should().Be("http://stub.test/v1/models");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json"),
            };
        });
        using var llm = new OpenAiCompatibleBenchLlmClient(Options(), new HttpClient(handler));

        (await llm.IsAvailableAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAvailable_ReturnsFalse_OnHttpConnectError()
    {
        var handler = StubHandler.Sync((_, _) => throw new HttpRequestException("no route to host"));
        using var llm = new OpenAiCompatibleBenchLlmClient(Options(), new HttpClient(handler));

        (await llm.IsAvailableAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsAvailable_FallsBackToRootProbe_When_ModelsEndpointReturns404()
    {
        // Reproduces Ollama-without-models behaviour: GET /v1/models 404s but the server is
        // up and POST /v1/chat/completions works. The fallback root probe should succeed.
        int call = 0;
        var handler = StubHandler.Sync((req, _) =>
        {
            call++;
            string url = req.RequestUri!.ToString();
            if (call == 1)
            {
                url.Should().Be("http://stub.test/v1/models");
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            url.Should().Be("http://stub.test/");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("Ollama is running"),
            };
        });
        using var llm = new OpenAiCompatibleBenchLlmClient(Options(), new HttpClient(handler));

        (await llm.IsAvailableAsync(CancellationToken.None)).Should().BeTrue();
        call.Should().Be(2);
    }

    [Fact]
    public async Task IsAvailable_ReturnsFalse_When_BothModelsAndRootFail()
    {
        var handler = StubHandler.Sync((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var llm = new OpenAiCompatibleBenchLlmClient(Options(), new HttpClient(handler));

        (await llm.IsAvailableAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_PostsChatCompletions_WithSystemAndUserMessages()
    {
        string? capturedBody = null;
        var handler = new StubHandler(async (req, _) =>
        {
            req.Method.Should().Be(HttpMethod.Post);
            req.RequestUri!.ToString().Should().Be("http://stub.test/v1/chat/completions");
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Chat("A short reply."), Encoding.UTF8, "application/json"),
            };
        });
        using var llm = new OpenAiCompatibleBenchLlmClient(Options(), new HttpClient(handler));

        string? reply = await llm.CompleteAsync("sysprompt", "userprompt", maxTokens: 80, jsonMode: false, CancellationToken.None);

        reply.Should().Be("A short reply.");
        capturedBody.Should().NotBeNull();
        using var body = JsonDocument.Parse(capturedBody!);
        body.RootElement.GetProperty("model").GetString().Should().Be("test-model");
        body.RootElement.GetProperty("messages").GetArrayLength().Should().Be(2);
        body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString().Should().Be("system");
        body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("sysprompt");
        body.RootElement.GetProperty("messages")[1].GetProperty("role").GetString().Should().Be("user");
        body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString().Should().Be("userprompt");
        body.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(80);
    }

    [Fact]
    public async Task CompleteAsync_OmitsResponseFormat_WhenJsonModeFalse()
    {
        string? capturedBody = null;
        var handler = new StubHandler(async (req, _) =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Chat("ok"), Encoding.UTF8, "application/json"),
            };
        });
        using var llm = new OpenAiCompatibleBenchLlmClient(Options(), new HttpClient(handler));

        await llm.CompleteAsync("s", "u", maxTokens: 50, jsonMode: false, CancellationToken.None);

        using var body = JsonDocument.Parse(capturedBody!);
        body.RootElement.TryGetProperty("response_format", out _).Should().BeFalse(
            because: "jsonMode=false must not ship response_format on the wire");
    }

    [Fact]
    public async Task CompleteAsync_SetsResponseFormat_WhenJsonModeTrue()
    {
        string? capturedBody = null;
        var handler = new StubHandler(async (req, _) =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Chat("""{"k":"v"}"""), Encoding.UTF8, "application/json"),
            };
        });
        using var llm = new OpenAiCompatibleBenchLlmClient(Options(), new HttpClient(handler));

        await llm.CompleteAsync("s", "u", maxTokens: 50, jsonMode: true, CancellationToken.None);

        using var body = JsonDocument.Parse(capturedBody!);
        body.RootElement.TryGetProperty("response_format", out var rf).Should().BeTrue(
            because: "jsonMode=true must request wire-level JSON-mode");
        rf.GetProperty("type").GetString().Should().Be("json_object");
    }

    [Fact]
    public async Task BearerToken_IsAttached_WhenApiKeyConfigured()
    {
        string? authHeader = null;
        var handler = StubHandler.Sync((req, _) =>
        {
            authHeader = req.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Chat("ok"), Encoding.UTF8, "application/json"),
            };
        });
        using var llm = new OpenAiCompatibleBenchLlmClient(Options(apiKey: "sk-test"), new HttpClient(handler));

        await llm.CompleteAsync("s", "u", maxTokens: 50, jsonMode: false, CancellationToken.None);

        authHeader.Should().Be("Bearer sk-test");
    }

    [Fact]
    public async Task CompleteAsync_ReturnsNull_OnNon2xx()
    {
        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var llm = new OpenAiCompatibleBenchLlmClient(Options(), new HttpClient(handler));

        (await llm.CompleteAsync("s", "u", maxTokens: 50, jsonMode: false, CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task CompleteAsync_ReturnsNull_OnEmptyChoiceContent()
    {
        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Chat(""), Encoding.UTF8, "application/json"),
            });
        using var llm = new OpenAiCompatibleBenchLlmClient(Options(), new HttpClient(handler));

        (await llm.CompleteAsync("s", "u", maxTokens: 50, jsonMode: false, CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task BuildUri_TolerantOfTrailingSlashOnBaseUrl()
    {
        string? capturedUri = null;
        var handler = StubHandler.Sync((req, _) =>
        {
            capturedUri = req.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json"),
            };
        });
        using var llm = new OpenAiCompatibleBenchLlmClient(
            Options(baseUrl: "http://stub.test/v1/"), new HttpClient(handler));

        await llm.IsAvailableAsync(CancellationToken.None);
        capturedUri.Should().Be("http://stub.test/v1/models");
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public static StubHandler Sync(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) =>
            new((req, ct) => Task.FromResult(respond(req, ct)));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
}
