using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Tests for <see cref="OpenAiCompatibleBenchLlmClient.ChatWithToolsAsync"/>. Uses a stubbed
/// <see cref="HttpMessageHandler"/> so no network is required, mirroring the pattern in
/// <see cref="OpenAiCompatibleBenchLlmClientTests"/>.
/// </summary>
public sealed class ChatWithToolsWireTests
{
    private static LlmOptions Options(string baseUrl = "http://stub.test/v1", string? apiKey = null) =>
        new()
        {
            BaseUrl = baseUrl,
            Model = "test-model",
            ApiKey = apiKey,
            Temperature = 0.1,
        };

    private static readonly IReadOnlyList<ToolChatMessage> Messages =
    [
        new ToolChatMessage("system", "you are a test"),
        new ToolChatMessage("user", "remember this"),
    ];

    private static readonly IReadOnlyList<ToolSpec> Tools =
    [
        new ToolSpec("memory_remember", "Remember something", """{"type":"object","properties":{}}"""),
    ];

    [Fact]
    public async Task ParsesToolCalls_FromAssistantMessage()
    {
        string toolCallsJson =
            """
            [{"id": "call_1", "type": "function", "function": {"name": "memory_remember", "arguments": "{\"path\":\"/x\",\"content\":[\"a\"]}"}}]
            """;
        string responseBody =
            $$"""
            {
              "id": "x",
              "model": "test-model",
              "choices": [{"index": 0, "message": {"role": "assistant", "content": "", "tool_calls": {{toolCallsJson}}}, "finish_reason": "tool_calls"}]
            }
            """;
        var handler = StubHandler.Sync((req, _) =>
        {
            req.Method.Should().Be(HttpMethod.Post);
            req.RequestUri!.ToString().Should().Be("http://stub.test/v1/chat/completions");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        });
        using var llm = new OpenAiCompatibleBenchLlmClient(Options(), new HttpClient(handler));

        ToolTurnResult? result = await llm.ChatWithToolsAsync(Messages, Tools, maxTokens: 100, CancellationToken.None);

        result.Should().NotBeNull();
        result!.ToolCalls.Should().HaveCount(1);
        result.ToolCalls[0].ToolName.Should().Be("memory_remember");
        result.ToolCalls[0].ArgumentsJson.Should().Contain("\"path\":\"/x\"");
    }

    [Fact]
    public async Task ReturnsEmptyToolCalls_WhenAssistantJustText()
    {
        string responseBody =
            $$"""
            {
              "id": "x",
              "model": "test-model",
              "choices": [{"index": 0, "message": {"role": "assistant", "content": {{JsonSerializer.Serialize("Sure, done.")}}}, "finish_reason": "stop"}]
            }
            """;
        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        using var llm = new OpenAiCompatibleBenchLlmClient(Options(), new HttpClient(handler));

        ToolTurnResult? result = await llm.ChatWithToolsAsync(Messages, Tools, maxTokens: 100, CancellationToken.None);

        result.Should().NotBeNull();
        result!.ToolCalls.Should().BeEmpty();
        result.AssistantContent.Should().Be("Sure, done.");
    }

    [Fact]
    public async Task ReturnsNull_OnTransportFailure()
    {
        var handler = StubHandler.Sync((_, _) => throw new HttpRequestException("no route to host"));
        using var llm = new OpenAiCompatibleBenchLlmClient(Options(), new HttpClient(handler));

        ToolTurnResult? result = await llm.ChatWithToolsAsync(Messages, Tools, maxTokens: 100, CancellationToken.None);

        result.Should().BeNull();
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
