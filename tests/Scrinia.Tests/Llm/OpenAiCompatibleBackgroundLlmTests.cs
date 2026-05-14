using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Llm;

namespace Scrinia.Tests.Llm;

/// <summary>
/// Tests for <see cref="OpenAiCompatibleBackgroundLlm"/>. Uses a stubbed
/// <see cref="HttpMessageHandler"/> so no network is required and the request shape
/// (URL, method, headers, body) can be asserted directly.
/// </summary>
public class OpenAiCompatibleBackgroundLlmTests
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
        using var llm = new OpenAiCompatibleBackgroundLlm(Options(), new HttpClient(handler));

        (await llm.IsAvailableAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAvailable_ReturnsFalse_OnHttpConnectError()
    {
        var handler = StubHandler.Sync((_, _) => throw new HttpRequestException("no route to host"));
        using var llm = new OpenAiCompatibleBackgroundLlm(Options(), new HttpClient(handler));

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
            // Second probe is the root of the host.
            url.Should().Be("http://stub.test/");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("Ollama is running"),
            };
        });
        using var llm = new OpenAiCompatibleBackgroundLlm(Options(), new HttpClient(handler));

        (await llm.IsAvailableAsync(CancellationToken.None)).Should().BeTrue();
        call.Should().Be(2);
    }

    [Fact]
    public async Task IsAvailable_ReturnsFalse_When_BothModelsAndRootFail()
    {
        var handler = StubHandler.Sync((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var llm = new OpenAiCompatibleBackgroundLlm(Options(), new HttpClient(handler));

        (await llm.IsAvailableAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateDescription_PostsChatCompletions_WithSystemAndUserMessages()
    {
        string? capturedBody = null;
        var handler = new StubHandler(async (req, _) =>
        {
            req.Method.Should().Be(HttpMethod.Post);
            req.RequestUri!.ToString().Should().Be("http://stub.test/v1/chat/completions");
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Chat("A short description."), Encoding.UTF8, "application/json"),
            };
        });
        using var llm = new OpenAiCompatibleBackgroundLlm(Options(), new HttpClient(handler));

        string? desc = await llm.GenerateDescriptionAsync("body text", CancellationToken.None);

        desc.Should().Be("A short description.");
        capturedBody.Should().NotBeNull();
        using var body = JsonDocument.Parse(capturedBody!);
        body.RootElement.GetProperty("model").GetString().Should().Be("test-model");
        body.RootElement.GetProperty("messages").GetArrayLength().Should().Be(2);
        body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString().Should().Be("system");
        body.RootElement.GetProperty("messages")[1].GetProperty("role").GetString().Should().Be("user");
        body.RootElement.GetProperty("max_tokens").GetInt32().Should().BeGreaterThan(0);
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
        using var llm = new OpenAiCompatibleBackgroundLlm(Options(apiKey: "sk-test"), new HttpClient(handler));

        await llm.GenerateDescriptionAsync("x", CancellationToken.None);

        authHeader.Should().Be("Bearer sk-test");
    }

    [Fact]
    public async Task GenerateDescription_ReturnsNull_OnNon2xx()
    {
        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var llm = new OpenAiCompatibleBackgroundLlm(Options(), new HttpClient(handler));

        (await llm.GenerateDescriptionAsync("x", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task GenerateDescription_ReturnsNull_OnEmptyChoiceContent()
    {
        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Chat(""), Encoding.UTF8, "application/json"),
            });
        using var llm = new OpenAiCompatibleBackgroundLlm(Options(), new HttpClient(handler));

        (await llm.GenerateDescriptionAsync("x", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ExtractFacts_ParsesNewlineDelimitedOutput()
    {
        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    Chat("Auth uses OAuth2 with PKCE.\nSession tokens stored in httpOnly cookies.\nRefresh tokens rotate every 24h."),
                    Encoding.UTF8, "application/json"),
            });
        using var llm = new OpenAiCompatibleBackgroundLlm(Options(), new HttpClient(handler));

        string[]? facts = await llm.ExtractFactsAsync("x", CancellationToken.None);

        facts.Should().NotBeNull();
        facts!.Should().HaveCount(3);
        facts.Should().Contain("Auth uses OAuth2 with PKCE.");
    }

    [Fact]
    public async Task ExtractFacts_StripsBulletsAndNumbering()
    {
        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    Chat("- First fact about something.\n* Second fact about another thing.\n1. Third fact numbered.\n2) Fourth fact alt-numbered."),
                    Encoding.UTF8, "application/json"),
            });
        using var llm = new OpenAiCompatibleBackgroundLlm(Options(), new HttpClient(handler));

        string[]? facts = await llm.ExtractFactsAsync("x", CancellationToken.None);

        facts.Should().NotBeNull();
        facts!.Should().HaveCount(4);
        facts.Should().Contain("First fact about something.");
        facts.Should().Contain("Third fact numbered.");
        facts!.All(f => !f.StartsWith('-') && !f.StartsWith('*') && !char.IsDigit(f[0])).Should().BeTrue();
    }

    [Fact]
    public async Task ExtractFacts_ReturnsNull_WhenNoFactsParsed()
    {
        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Chat("   \n   \n  "), Encoding.UTF8, "application/json"),
            });
        using var llm = new OpenAiCompatibleBackgroundLlm(Options(), new HttpClient(handler));

        (await llm.ExtractFactsAsync("x", CancellationToken.None)).Should().BeNull();
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
        using var llm = new OpenAiCompatibleBackgroundLlm(
            Options(baseUrl: "http://stub.test/v1/"), new HttpClient(handler));

        await llm.IsAvailableAsync(CancellationToken.None);
        capturedUri.Should().Be("http://stub.test/v1/models");
    }

    /// <summary>
    /// Stubbed message handler — async-only signature to keep callers unambiguous.
    /// Tests that don't need to await pre-build a Task.FromResult.
    /// </summary>
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
