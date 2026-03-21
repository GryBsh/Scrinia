using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Server.Auth;
using Scrinia.Server.Chat;
using Scrinia.Server.Models;
using Xunit;

namespace Scrinia.Server.Tests;

public sealed class ChatEndpointTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;

    public ChatEndpointTests(ScriniaServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Chat_returns_401_without_auth()
    {
        var client = _factory.CreateClient(); // no auth
        var req = new ChatRequest([new ChatMessage("user", "hello")]);
        var content = new StringContent(
            JsonSerializer.Serialize(req, ChatJsonContext.Default.ChatRequest),
            Encoding.UTF8, "application/json");

        var resp = await client.PostAsync("/api/v1/stores/test-store/chat/", content);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Chat_returns_403_without_chat_permission()
    {
        // Create a key without 'chat' permission
        var keyStore = _factory.Services.GetRequiredService<ApiKeyStore>();
        var (rawKey, _, _) = keyStore.CreateKey("no-chat-user", ["test-store"],
            ["read", "search"], "no-chat");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawKey);

        var req = new ChatRequest([new ChatMessage("user", "hello")]);
        var content = new StringContent(
            JsonSerializer.Serialize(req, ChatJsonContext.Default.ChatRequest),
            Encoding.UTF8, "application/json");

        var resp = await client.PostAsync("/api/v1/stores/test-store/chat/", content);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Chat_returns_503_when_no_providers_configured()
    {
        // Default test setup has no chat providers configured
        var client = _factory.CreateAuthenticatedClient();

        var req = new ChatRequest([new ChatMessage("user", "hello")]);
        var content = new StringContent(
            JsonSerializer.Serialize(req, ChatJsonContext.Default.ChatRequest),
            Encoding.UTF8, "application/json");

        var resp = await client.PostAsync("/api/v1/stores/test-store/chat/", content);
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body.Should().NotBeNull();
        body!.Error.Should().Contain("No chat providers configured");
    }

    [Fact]
    public async Task Chat_providers_returns_empty_when_none_configured()
    {
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.GetAsync("/api/v1/stores/test-store/chat/providers");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<ChatProvidersResponse>(
            ChatJsonContext.Default.ChatProvidersResponse);
        body.Should().NotBeNull();
        body!.Providers.Should().BeEmpty();
    }

    [Fact]
    public async Task Chat_providers_returns_403_without_chat_permission()
    {
        var keyStore = _factory.Services.GetRequiredService<ApiKeyStore>();
        var (rawKey, _, _) = keyStore.CreateKey("no-chat-user2", ["test-store"],
            ["read"], "no-chat2");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawKey);

        var resp = await client.GetAsync("/api/v1/stores/test-store/chat/providers");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
