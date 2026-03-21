using System.Text.Json;
using Scrinia.Server.Auth;
using Scrinia.Server.Chat;
using Scrinia.Server.Models;

namespace Scrinia.Server.Endpoints;

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app, ChatOptions chatOptions)
    {
        var group = app.MapGroup("/api/v1/stores/{store}/chat")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        group.MapPost("/", async (string store, ChatRequest req, RequestContext ctx,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (!ctx.HasPermission("chat"))
                return Results.Json(new ErrorResponse("Permission 'chat' required."), statusCode: 403);

            var available = chatOptions.GetAvailableProviders();
            if (available.Length == 0)
                return Results.Json(new ErrorResponse("No chat providers configured. Configure Scrinia:Chat in appsettings.json."),
                    statusCode: 503);

            // Use requested provider or first available
            string providerName = req.Provider ?? available[0];
            if (!available.Contains(providerName, StringComparer.OrdinalIgnoreCase))
                return Results.Json(new ErrorResponse($"Provider '{providerName}' is not configured."), statusCode: 400);

            var logger = loggerFactory.CreateLogger("Scrinia.Chat");
            var provider = ChatProviderFactory.Create(providerName, chatOptions, logger);
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
                finally
                {
                    if (provider is IDisposable d) d.Dispose();
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

/// <summary>IResult that streams SSE events with proper headers.</summary>
internal sealed class SseResult(Func<StreamWriter, Task> writeAsync) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.Append("Cache-Control", "no-cache");
        httpContext.Response.Headers.Append("X-Accel-Buffering", "no");

        await using var writer = new StreamWriter(httpContext.Response.Body, leaveOpen: true);
        await writeAsync(writer);
    }
}
