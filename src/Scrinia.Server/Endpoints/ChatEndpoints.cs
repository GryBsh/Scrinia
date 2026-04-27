using System.Text.Json;
using Scrinia.Server.Auth;
using Scrinia.Server.Chat;
using Scrinia.Server.Models;
using Scrinia.Server.Sse;

namespace Scrinia.Server.Endpoints;

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app, ChatOptions chatOptions)
    {
        var group = app.MapGroup("/api/v1/stores/{store}/chat")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        group.MapPost("/", async (string store, ChatRequest req, RequestContext ctx,
            ChatProviderCache providerCache, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (!ctx.HasPermission("chat"))
                return Results.Json(new ErrorResponse("Permission 'chat' required."), statusCode: 403);

            // ── Input validation (SEC-051) ──────────────────────────────────
            const int MaxMessages = 1000;
            const int MaxContentBytes = 1_048_576; // 1 MB per message
            const int MaxToolCalls = 100;

            if (req.Messages is null || req.Messages.Length == 0)
                return Results.BadRequest(new ErrorResponse("messages is required."));
            if (req.Messages.Length > MaxMessages)
                return Results.BadRequest(new ErrorResponse($"Too many messages (max {MaxMessages})."));

            foreach (var msg in req.Messages)
            {
                if (msg.Content != null && System.Text.Encoding.UTF8.GetByteCount(msg.Content) > MaxContentBytes)
                    return Results.BadRequest(new ErrorResponse($"Message content exceeds {MaxContentBytes / (1024 * 1024)} MB limit."));
                if (msg.ToolCalls != null && msg.ToolCalls.Length > MaxToolCalls)
                    return Results.BadRequest(new ErrorResponse($"Too many tool calls in a single message (max {MaxToolCalls})."));
            }

            var available = chatOptions.GetAvailableProviders();
            if (available.Length == 0)
                return Results.Json(new ErrorResponse("No chat providers configured. Configure Scrinia:Chat in appsettings.json."),
                    statusCode: 503);

            // Use requested provider or first available
            string providerName = req.Provider ?? available[0];
            if (!available.Contains(providerName, StringComparer.OrdinalIgnoreCase))
                return Results.Json(new ErrorResponse($"Provider '{providerName}' is not configured."), statusCode: 400);

            var logger = loggerFactory.CreateLogger("Scrinia.Chat");
            var provider = providerCache.GetOrCreate(providerName);
            if (provider is null)
                return Results.Json(new ErrorResponse($"Failed to create provider '{providerName}'."), statusCode: 503);

            return new SseResult(async writer =>
            {
                try
                {
                    await foreach (var evt in AgentLoop.RunAsync(req.Messages, ctx.Store!, provider, ct))
                    {
                        string json = JsonSerializer.Serialize(evt, ChatJsonContext.Default.ChatEvent);
                        await writer.WriteAsync($"data: {json}\n\n");
                        await writer.FlushAsync();
                    }
                }
                catch (OperationCanceledException) { /* client disconnected */ }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Chat stream error for store {Store}", store);
                    string errorJson = JsonSerializer.Serialize(
                        new ChatEvent("error", Error: "An internal error occurred."),
                        ChatJsonContext.Default.ChatEvent);
                    try { await writer.WriteAsync($"data: {errorJson}\n\n"); } catch { /* best-effort */ }
                }
            });
        });

        // GET available providers (for UI picker)
        group.MapGet("/providers", (RequestContext ctx) =>
        {
            if (!ctx.HasPermission("chat"))
                return Results.Json(new ErrorResponse("Permission 'chat' required."), statusCode: 403);

            var available = chatOptions.GetAvailableProviders();
            return Results.Ok(new ChatProvidersResponse(available));
        });
    }
}
