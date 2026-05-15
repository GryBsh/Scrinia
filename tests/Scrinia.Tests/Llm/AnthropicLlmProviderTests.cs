using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Llm;
using Scrinia.Core.Llm.Providers;

namespace Scrinia.Tests.Llm;

/// <summary>
/// Tests for <see cref="AnthropicLlmProvider"/>. Stubbed
/// <see cref="HttpMessageHandler"/> asserts Messages API request shape
/// (POST /v1/messages, x-api-key header, system field, content blocks).
/// </summary>
public class AnthropicLlmProviderTests
{
    private static LlmOptions Options(string? apiKey = "test-key", string baseUrl = "https://stub.anthropic.test") =>
        new()
        {
            Model = "claude-test-model",
            AnthropicApiKey = apiKey,
            AnthropicBaseUrl = baseUrl,
            Temperature = 0.1,
            RequestTimeoutSeconds = 30,
        };

    private static string MessagesResponse(string text) =>
        $$"""
        {
          "id": "msg_x",
          "model": "claude-test-model",
          "content": [{"type": "text", "text": {{JsonSerializer.Serialize(text)}}}],
          "stop_reason": "end_turn"
        }
        """;

    [Fact]
    public async Task GenerateDescription_PostsToMessagesEndpoint_WithApiKeyHeader()
    {
        HttpRequestMessage? captured = null;
        var handler = StubHandler.Sync((req, _) =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MessagesResponse("A description."), Encoding.UTF8, "application/json"),
            };
        });
        using var llm = new AnthropicLlmProvider(Options(), new HttpClient(handler));

        string? desc = await llm.GenerateDescriptionAsync("test content", CancellationToken.None);

        desc.Should().Be("A description.");
        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.ToString().Should().Be("https://stub.anthropic.test/v1/messages");
        captured.Headers.GetValues("x-api-key").Should().ContainSingle().Which.Should().Be("test-key");
        captured.Headers.GetValues("anthropic-version").Should().ContainSingle();
    }

    [Fact]
    public async Task RequestBody_HasSystemFieldAndUserMessage()
    {
        string? bodyJson = null;
        var handler = StubHandler.Async(async (req, ct) =>
        {
            bodyJson = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MessagesResponse("ok"), Encoding.UTF8, "application/json"),
            };
        });
        using var llm = new AnthropicLlmProvider(Options(), new HttpClient(handler));

        await llm.GenerateDescriptionAsync("the content", CancellationToken.None);

        bodyJson.Should().NotBeNull();
        using var doc = JsonDocument.Parse(bodyJson!);
        var root = doc.RootElement;
        root.GetProperty("model").GetString().Should().Be("claude-test-model");
        root.GetProperty("system").GetString().Should().NotBeNullOrEmpty();
        var messages = root.GetProperty("messages");
        messages.GetArrayLength().Should().Be(1);
        messages[0].GetProperty("role").GetString().Should().Be("user");
        messages[0].GetProperty("content").GetString().Should().Contain("the content");
    }

    [Fact]
    public async Task ConcatenatesMultipleTextBlocks()
    {
        var handler = StubHandler.Sync((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"content": [{"type": "text", "text": "first "}, {"type": "text", "text": "second"}]}""",
                Encoding.UTF8, "application/json"),
        });
        using var llm = new AnthropicLlmProvider(Options(), new HttpClient(handler));

        (await llm.GenerateDescriptionAsync("x", CancellationToken.None)).Should().Be("first second");
    }

    [Fact]
    public async Task IgnoresNonTextContentBlocks()
    {
        // Tool-use blocks (which we don't request) have type=tool_use with no text field —
        // must be skipped, not crash the parser.
        var handler = StubHandler.Sync((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"content": [{"type": "tool_use", "id": "x"}, {"type": "text", "text": "real reply"}]}""",
                Encoding.UTF8, "application/json"),
        });
        using var llm = new AnthropicLlmProvider(Options(), new HttpClient(handler));

        (await llm.GenerateDescriptionAsync("x", CancellationToken.None)).Should().Be("real reply");
    }

    [Fact]
    public async Task Non2xx_ReturnsNull()
    {
        var handler = StubHandler.Sync((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var llm = new AnthropicLlmProvider(Options(), new HttpClient(handler));

        (await llm.GenerateDescriptionAsync("x", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task IsAvailable_TrueOn200_ProbingMessagesEndpoint()
    {
        var handler = StubHandler.Sync((req, _) =>
        {
            req.Method.Should().Be(HttpMethod.Get);
            req.RequestUri!.ToString().Should().Be("https://stub.anthropic.test/v1/messages");
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);  // expected: GET is not allowed
        });
        using var llm = new AnthropicLlmProvider(Options(), new HttpClient(handler));

        (await llm.IsAvailableAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAvailable_FalseOn401()
    {
        var handler = StubHandler.Sync((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var llm = new AnthropicLlmProvider(Options(), new HttpClient(handler));

        (await llm.IsAvailableAsync(CancellationToken.None)).Should().BeFalse();
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
