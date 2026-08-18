using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Server.Auth;
using Scrinia.Server.Models;

namespace Scrinia.Server.Endpoints;

/// <summary>
/// Embeddings status, settings and reindex — under the {store}-scoped group so the auth
/// middleware resolves RequestContext.Store before the handler runs (finding H6), and under
/// Endpoints/ so the endpoint conventions apply to it like every other group.
/// </summary>
public static class EmbeddingsEndpoints
{
    public static void MapEmbeddingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/stores/{store}/embeddings")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        group.MapGet("/status", GetStatus).RequirePermission(Permissions.Read);
        group.MapGet("/settings", GetSettings).RequirePermission(Permissions.Read);
        // A full re-embed of every memory in the store is unbounded work, so it gets its own
        // permission rather than riding on 'store'.
        group.MapPost("/reindex", Reindex).RequirePermission(Permissions.Reindex);
    }

    // The admin service is resolved from HttpContext.RequestServices rather than taken as a
    // handler parameter. It is optional — absent when no model is installed and no plugin is
    // loaded — and minimal-API parameter inference treats an unregistered complex type as a
    // body parameter, which MapGet rejects at endpoint-construction time. This is also what
    // the routes being replaced already did (Program.cs:548, :556, :564).
    private static IResult GetStatus(HttpContext http)
    {
        var admin = http.RequestServices.GetService<IEmbeddingsAdmin>();
        if (admin is null)
            return Results.Ok(new EmbeddingsStatusResponse("none", "", false, 0, 0.0, 0));

        return Results.Ok(new EmbeddingsStatusResponse(
            admin.ProviderName, admin.Hardware, admin.IsAvailable,
            admin.Dimensions, admin.SemanticWeight, admin.TotalVectorCount));
    }

    private static IResult GetSettings(HttpContext http)
    {
        var admin = http.RequestServices.GetService<IEmbeddingsAdmin>();
        if (admin is null)
            return Results.Ok(new EmbeddingsSettingsResponse("none", "", 0.0));

        return Results.Ok(new EmbeddingsSettingsResponse(
            admin.ProviderName, admin.Hardware, admin.SemanticWeight));
    }

    private static async Task<IResult> Reindex(
        string store, HttpContext http, RequestContext ctx, CancellationToken ct)
    {
        if (!MemoryEndpoints.IsWritable(ctx, store))
            return Results.Json(new ErrorResponse($"Store '{store}' is read-only for this key."), statusCode: 403);

        var admin = http.RequestServices.GetService<IEmbeddingsAdmin>();
        if (admin is null || !admin.IsAvailable)
            return Results.BadRequest(new ErrorResponse("Embedding provider is not available."));

        // ctx.Store is resolved by the auth middleware from the {store} route value. That is
        // the whole point of this move — the previous inline group had no {store} segment.
        if (ctx.Store is null)
            return Results.BadRequest(new ErrorResponse("No store context available."));

        int count = await admin.ReindexStoreAsync(ctx.Store, ct);
        return Results.Ok(new ReindexResponse($"Reindexed {count} memories."));
    }
}
