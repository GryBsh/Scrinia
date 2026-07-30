using System.Threading.RateLimiting;
using Scrinia.Server.Auth;
using Scrinia.Server.Models;
using Scrinia.Server.Services;

namespace Scrinia.Server.Endpoints;

public static class KeyEndpoints
{
    public static void MapKeyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/keys")
            .RequireAuthorization("ManageKeys")
            .RequireRateLimiting("api");

        group.MapPost("/", CreateKey);
        group.MapGet("/", ListKeys);
        group.MapGet("/{keyId}", GetKey);
        group.MapDelete("/{keyId}", RevokeKey);
    }

    private static IResult CreateKey(
        CreateKeyRequest req,
        RequestContext ctx,
        ApiKeyStore keyStore,
        StoreManager storeManager)
    {
        if (string.IsNullOrWhiteSpace(req.UserId) || req.Stores is null || req.Stores.Length == 0)
            return Results.BadRequest(new ErrorResponse("userId and stores are required."));

        // Validate all requested stores exist
        foreach (string store in req.Stores)
        {
            if (!storeManager.StoreExists(store))
                return Results.BadRequest(new ErrorResponse($"Store '{store}' does not exist."));
        }

        // Privilege escalation prevention: caller cannot grant stores they don't have
        foreach (string store in req.Stores)
        {
            if (!ctx.CanAccessStore(store))
                return Results.Json(
                    new ErrorResponse($"Cannot grant access to store '{store}' — you don't have access to it."),
                    statusCode: 403);
        }

        // Privilege escalation prevention: caller cannot grant permissions they don't have
        if (req.Permissions is { Length: > 0 })
        {
            foreach (string perm in req.Permissions)
            {
                if (!ctx.HasPermission(perm))
                    return Results.Json(
                        new ErrorResponse($"Cannot grant permission '{perm}' — you don't have it."),
                        statusCode: 403);
            }
        }

        var (rawKey, keyId, userId) = keyStore.CreateKey(req.UserId, req.Stores, req.Permissions, req.Label);

        return Results.Created($"/api/v1/keys/{keyId}",
            new CreateKeyResponse(rawKey, keyId, userId, req.Stores, req.Permissions ?? []));
    }

    // A caller may manage a key iff it is a global admin, or every store the key grants is
    // one the caller can access (all, not any — a key granting an out-of-scope store stays hidden).
    private static bool CanManage(RequestContext ctx, string[] keyStores) =>
        ctx.IsGlobalAdmin || (keyStores.Length > 0 && keyStores.All(ctx.CanAccessStore));

    private static IResult ListKeys(RequestContext ctx, ApiKeyStore keyStore)
    {
        var dtos = keyStore.ListKeys()
            .Where(k => CanManage(ctx, k.Stores))
            .Select(k => new KeySummaryDto(
                k.Id, k.UserId, k.Stores, k.Permissions,
                k.Label, k.CreatedAt, k.LastUsedAt, k.Revoked))
            .ToArray();
        return Results.Ok(dtos);
    }

    private static IResult GetKey(string keyId, RequestContext ctx, ApiKeyStore keyStore)
    {
        var key = keyStore.GetKey(keyId);
        if (key is null || !CanManage(ctx, key.Stores))
            return Results.NotFound(new ErrorResponse($"Key '{keyId}' not found."));

        return Results.Ok(new KeySummaryDto(
            key.Id, key.UserId, key.Stores, key.Permissions,
            key.Label, key.CreatedAt, key.LastUsedAt, key.Revoked));
    }

    private static IResult RevokeKey(string keyId, RequestContext ctx, ApiKeyStore keyStore)
    {
        var key = keyStore.GetKey(keyId);
        if (key is null || !CanManage(ctx, key.Stores))
            return Results.NotFound(new ErrorResponse($"Key '{keyId}' not found or already revoked."));

        bool revoked = keyStore.RevokeKey(keyId);
        if (!revoked)
            return Results.NotFound(new ErrorResponse($"Key '{keyId}' not found or already revoked."));

        return Results.Ok(new { message = $"Key '{keyId}' revoked." });
    }
}
