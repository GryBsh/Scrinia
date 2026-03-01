using System.Threading.RateLimiting;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Search;
using Scrinia.Plugin.Abstractions;
using Scrinia.Server.Auth;
using Scrinia.Server.Models;
using Scrinia.Server.Services;

namespace Scrinia.Server.Endpoints;

public static class MemoryEndpoints
{
    public static void MapMemoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/stores/{store}")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        group.MapPost("/memories", StoreMemory);
        group.MapGet("/memories", ListMemories);
        group.MapGet("/memories/{name}", ShowMemory);
        group.MapDelete("/memories/{name}", ForgetMemory);
        group.MapPost("/memories/{name}/append", AppendMemory);
        group.MapPost("/memories/{name}/copy", CopyMemory);
        group.MapGet("/memories/{name}/chunks/{i:int}", GetChunk);
        group.MapGet("/search", SearchMemories);
        group.MapPost("/export", ExportTopics);
        group.MapPost("/import", ImportBundle);
    }

    private const int MaxNameLength = 256;
    private const long MaxContentBytes = 5 * 1024 * 1024; // 5 MB per element

    private static async Task<IResult> StoreMemory(
        string store,
        StoreRequest req,
        RequestContext ctx,
        PluginPipeline pipeline,
        CancellationToken ct)
    {
        if (!ctx.HasPermission("store"))
            return Results.Json(new ErrorResponse("Permission 'store' required."), statusCode: 403);

        if (req.Content is null || req.Content.Length == 0 || string.IsNullOrWhiteSpace(req.Name))
            return Results.BadRequest(new ErrorResponse("content and name are required."));

        if (req.Name.Length > MaxNameLength)
            return Results.BadRequest(new ErrorResponse($"Name must not exceed {MaxNameLength} characters."));

        if (req.Content.Any(c => c is not null && c.Length > MaxContentBytes))
            return Results.BadRequest(new ErrorResponse($"Each content element must not exceed {MaxContentBytes / (1024 * 1024)} MB."));

        try
        {
            var result = await pipeline.StoreAsync(ctx.Store!, req, ct);
            return Results.Created($"/api/v1/stores/{store}/memories/{Uri.EscapeDataString(result.QualifiedName)}", result);
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken != ct)
        {
            return Results.Conflict(new ErrorResponse(ex.Message));
        }
    }

    private static Task<IResult> ListMemories(
        string store,
        RequestContext ctx,
        string? scopes = null)
    {
        if (!ctx.HasPermission("search"))
            return Task.FromResult<IResult>(Results.Json(new ErrorResponse("Permission 'search' required."), statusCode: 403));

        var items = ctx.Store!.ListScoped(scopes);
        var mapped = items.Select(item =>
        {
            string qualifiedName = item.Scope == "ephemeral"
                ? $"~{item.Entry.Name}"
                : ctx.Store!.FormatQualifiedName(item.Scope, item.Entry.Name);
            return new MemoryListItem(
                item.Entry.Name,
                qualifiedName,
                MemoryNaming.FormatScopeLabel(item.Scope),
                item.Entry.ChunkCount,
                item.Entry.OriginalBytes,
                item.Entry.CreatedAt,
                item.Entry.UpdatedAt,
                item.Entry.Description,
                item.Entry.Tags);
        }).ToArray();

        return Task.FromResult<IResult>(Results.Ok(new ListResponse(mapped, mapped.Length)));
    }

    private static async Task<IResult> ShowMemory(
        string store,
        string name,
        RequestContext ctx,
        CancellationToken ct)
    {
        if (!ctx.HasPermission("read"))
            return Results.Json(new ErrorResponse("Permission 'read' required."), statusCode: 403);

        string decoded = Uri.UnescapeDataString(name);
        var result = await MemoryOrchestrator.ShowAsync(ctx.Store!, decoded, ct);
        if (result is null)
            return Results.NotFound(new ErrorResponse($"Memory '{decoded}' not found."));
        return Results.Ok(result);
    }

    private static async Task<IResult> ForgetMemory(
        string store,
        string name,
        RequestContext ctx,
        PluginPipeline pipeline,
        CancellationToken ct)
    {
        if (!ctx.HasPermission("forget"))
            return Results.Json(new ErrorResponse("Permission 'forget' required."), statusCode: 403);

        string decoded = Uri.UnescapeDataString(name);

        try
        {
            bool ok = await pipeline.ForgetAsync(ctx.Store!, decoded, ct);
            if (!ok)
                return Results.NotFound(new ErrorResponse($"Memory '{decoded}' not found."));
            return Results.Ok(new { message = $"Forgot: {decoded}" });
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken != ct)
        {
            return Results.Conflict(new ErrorResponse(ex.Message));
        }
    }

    private static async Task<IResult> AppendMemory(
        string store,
        string name,
        AppendRequest req,
        RequestContext ctx,
        PluginPipeline pipeline,
        CancellationToken ct)
    {
        if (!ctx.HasPermission("append"))
            return Results.Json(new ErrorResponse("Permission 'append' required."), statusCode: 403);

        string decoded = Uri.UnescapeDataString(name);
        if (string.IsNullOrWhiteSpace(req.Content))
            return Results.BadRequest(new ErrorResponse("content is required."));

        if (decoded.Length > MaxNameLength)
            return Results.BadRequest(new ErrorResponse($"Name must not exceed {MaxNameLength} characters."));

        if (req.Content.Length > MaxContentBytes)
            return Results.BadRequest(new ErrorResponse($"Content must not exceed {MaxContentBytes / (1024 * 1024)} MB."));

        try
        {
            var result = await pipeline.AppendAsync(ctx.Store!, decoded, req.Content, ct);
            return Results.Ok(result);
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken != ct)
        {
            return Results.Conflict(new ErrorResponse(ex.Message));
        }
    }

    private static Task<IResult> CopyMemory(
        string store,
        string name,
        CopyRequest req,
        RequestContext ctx)
    {
        if (!ctx.HasPermission("copy"))
            return Task.FromResult<IResult>(Results.Json(new ErrorResponse("Permission 'copy' required."), statusCode: 403));

        string decoded = Uri.UnescapeDataString(name);
        if (string.IsNullOrWhiteSpace(req.Destination))
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse("destination is required.")));

        bool ok = ctx.Store!.CopyMemory(decoded, req.Destination, req.Overwrite, out string msg);
        if (!ok)
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse(msg)));
        return Task.FromResult<IResult>(Results.Ok(new { message = msg }));
    }

    private static async Task<IResult> GetChunk(
        string store,
        string name,
        int i,
        RequestContext ctx,
        CancellationToken ct)
    {
        if (!ctx.HasPermission("read"))
            return Results.Json(new ErrorResponse("Permission 'read' required."), statusCode: 403);

        string decoded = Uri.UnescapeDataString(name);
        string artifact;
        try
        {
            artifact = await ctx.Store!.ResolveArtifactAsync(decoded, ct);
        }
        catch (FileNotFoundException)
        {
            return Results.NotFound(new ErrorResponse($"Memory '{decoded}' not found."));
        }

        int totalChunks = Nmp2ChunkedEncoder.GetChunkCount(artifact);
        if (i < 1 || i > totalChunks)
            return Results.BadRequest(new ErrorResponse($"Chunk index {i} out of range [1, {totalChunks}]."));

        string content = Nmp2ChunkedEncoder.DecodeChunk(artifact, i);
        return Results.Ok(new ChunkResponse(content, i, totalChunks));
    }

    private static async Task<IResult> SearchMemories(
        string store,
        RequestContext ctx,
        PluginPipeline pipeline,
        CancellationToken ct,
        string? q = null,
        string? scopes = null,
        int limit = 20)
    {
        if (!ctx.HasPermission("search"))
            return Results.Json(new ErrorResponse("Permission 'search' required."), statusCode: 403);

        if (string.IsNullOrWhiteSpace(q))
            return Results.BadRequest(new ErrorResponse("q parameter is required."));

        var results = await pipeline.SearchAsync(ctx.Store!, q, scopes, limit, ct);
        var mapped = results.Select<SearchResult, SearchResultItem>(r => r switch
        {
            EntryResult er => new SearchResultItem(
                "entry",
                er.Item.Scope == "ephemeral"
                    ? $"~{er.Item.Entry.Name}"
                    : ctx.Store!.FormatQualifiedName(er.Item.Scope, er.Item.Entry.Name),
                er.Score,
                er.Item.Entry.Description),
            ChunkEntryResult cr => new SearchResultItem(
                "chunk",
                cr.ParentItem.Scope == "ephemeral"
                    ? $"~{cr.ParentItem.Entry.Name}"
                    : ctx.Store!.FormatQualifiedName(cr.ParentItem.Scope, cr.ParentItem.Entry.Name),
                cr.Score,
                cr.Chunk.ContentPreview ?? cr.ParentItem.Entry.Description,
                cr.Chunk.ChunkIndex,
                cr.TotalChunks),
            TopicResult tr => new SearchResultItem(
                "topic",
                MemoryNaming.FormatScopeLabel(tr.Scope),
                tr.Score,
                tr.Description),
            _ => new SearchResultItem("unknown", "", r.Score, null),
        }).ToArray();

        return Results.Ok(new SearchResponse(mapped));
    }

    private static Task<IResult> ExportTopics(
        string store,
        ExportRequest req,
        RequestContext ctx)
    {
        if (!ctx.HasPermission("export"))
            return Task.FromResult<IResult>(Results.Json(new ErrorResponse("Permission 'export' required."), statusCode: 403));

        if (req.Topics is null || req.Topics.Length == 0)
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse("topics is required.")));

        var stream = BundleService.ExportToStream(ctx.Store!, req.Topics);
        string filename = string.IsNullOrWhiteSpace(req.Filename)
            ? $"export-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.scrinia-bundle"
            : req.Filename;

        return Task.FromResult<IResult>(Results.File(stream, "application/octet-stream", filename));
    }

    private static async Task<IResult> ImportBundle(
        string store,
        HttpRequest request,
        RequestContext ctx,
        string[]? topics = null,
        bool overwrite = false)
    {
        if (!ctx.HasPermission("import"))
            return Results.Json(new ErrorResponse("Permission 'import' required."), statusCode: 403);

        if (!request.HasFormContentType || request.Form.Files.Count == 0)
            return Results.BadRequest(new ErrorResponse("Multipart file upload required."));

        var file = request.Form.Files[0];
        using var stream = file.OpenReadStream();

        try
        {
            var (topicCount, entryCount, names) = BundleService.ImportFromStream(ctx.Store!, stream, topics, overwrite);
            if (topicCount == 0)
                return Results.Ok(new { message = "No topics were imported." });
            return Results.Ok(new { message = $"Imported {topicCount} topic(s) ({entryCount} entries): {string.Join(", ", names)}" });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
    }
}
