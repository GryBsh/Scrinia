using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
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

        group.MapPost("/memories", RememberMemory);
        group.MapGet("/memories", ListMemory);
        group.MapGet("/memories/{name}", RecallMemory);
        group.MapGet("/memories/{name}/entry", GetEntry);
        // R-2: PatchEntry takes HttpRequest (it parses the body manually — see its own doc comment),
        // not a typed parameter, which otherwise strips the request body from the generated OpenAPI
        // document (a generated client would see a PATCH that accepts no body). .Accepts restores it.
        group.MapPatch("/memories/{name}", PatchEntry).Accepts<MemoryEntry>("application/json");
        group.MapDelete("/memories/{name}", ForgetMemory);
        group.MapPost("/memories/{name}/append", AppendMemory);
        group.MapPost("/memories/{name}/copy", CopyMemory);
        group.MapGet("/memories/{name}/chunks/{i:int}", GetChunk);
        group.MapGet("/search", SearchMemory);
        group.MapPost("/search", SearchMemoryPost);
        group.MapPost("/export", ExportTopics);
        group.MapPost("/import", ImportBundle);
    }

    private const int MaxNameLength = 256;
    private const long MaxContentBytes = 5 * 1024 * 1024; // 5 MB per element

    /// <summary>True when the caller may write to <paramref name="store"/> — i.e. the store is not
    /// marked read-only by an SSO store_access claim. API keys default to read-write.</summary>
    internal static bool IsWritable(RequestContext ctx, string store) =>
        !string.Equals(ctx.GetStoreAccessLevel(store), "read-only", StringComparison.OrdinalIgnoreCase);

    private static async Task<IResult> RememberMemory(
        string store,
        StoreRequest req,
        RequestContext ctx,
        PluginPipeline pipeline,
        CancellationToken ct)
    {
        if (!ctx.HasPermission("store"))
            return Results.Json(new ErrorResponse("Permission 'store' required."), statusCode: 403);

        if (!IsWritable(ctx, store))
            return Results.Json(new ErrorResponse($"Store '{store}' is read-only for this key."), statusCode: 403);

        if (req.Content is null || req.Content.Length == 0 || string.IsNullOrWhiteSpace(req.Name))
            return Results.BadRequest(new ErrorResponse("content and name are required."));

        if (req.Name.Length > MaxNameLength)
            return Results.BadRequest(new ErrorResponse($"Name must not exceed {MaxNameLength} characters."));

        if (req.Content.Any(c => c is not null && System.Text.Encoding.UTF8.GetByteCount(c) > MaxContentBytes))
            return Results.BadRequest(new ErrorResponse($"Each content element must not exceed {MaxContentBytes / (1024 * 1024)} MB."));

        // Attribute-map bounds, both of them.
        //
        // Key cap: KnownAttributes.MaxAttributes, not the search filter's 64. KnownAttributes.Merge
        // caps at MaxAttributes and truncates past it with no signal, so accepting a larger map
        // would drop keys silently. This does NOT diverge from local: Merge caps the map on the MCP
        // side too, so an MCP-originated wire map can never arrive over this bound.
        //
        // It only partly achieves "say no rather than truncate quietly", and the gap is worth
        // knowing: Merge counts the extraction-derived keys FIRST, so a request of 30 keys against
        // content yielding 5 extracted keys still loses 3 to truncation, under this bound. Rejecting
        // on the post-merge count would need the merge to have run, which is past the point where a
        // 400 is the right answer. What this bound removes is the grossly-over case, not every case.
        //
        // Values-per-key cap. This used to be a remote-ONLY bound of 64, justified as a deliberate
        // asymmetry — but it was reachable from routine auto-extraction, not just from a caller
        // deliberately supplying 65 values: nothing caps `file`/`ref` values locally, so a memory
        // citing 70 distinct paths stored fine in-process and 400'd here. The bound now lives in
        // KnownAttributes.MaxValuesPerKey and is enforced by KnownAttributes.Merge, which every
        // write path funnels through, so this check can no longer fire for a map produced by
        // Scrinia's own code — local and remote accept the same input. It stays as the wire guard
        // for a hand-rolled client that never went through Merge, and, like the key cap above, says
        // no rather than truncating quietly.
        //
        // Both bounds run through the shared ValidateAttributeBounds rather than a private copy, so
        // the append path below — which now carries an attribute map too — cannot drift into a
        // laxer version of the same guard.
        if (ValidateAttributeBounds(req.Attributes, KnownAttributes.MaxAttributes, KnownAttributes.MaxValuesPerKey)
            is { } storeBoundsError)
            return Results.BadRequest(new ErrorResponse(storeBoundsError));

        try
        {
            var result = await pipeline.StoreAsync(ctx.Store!, req, ct);
            return Results.Created(
                $"/api/v1/stores/{store}/memories/{Uri.EscapeDataString(result.Entry.QualifiedName)}", result);
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken != ct)
        {
            return Results.Conflict(new ErrorResponse(ex.Message));
        }
    }

    private const int DefaultListLimit = 200;
    private const int MaxListLimit = 1000;

    private static Task<IResult> ListMemory(
        string store,
        RequestContext ctx,
        string? scopes = null,
        int offset = 0,
        int limit = DefaultListLimit)
    {
        if (!ctx.HasPermission("search"))
            return Task.FromResult<IResult>(Results.Json(new ErrorResponse("Permission 'search' required."), statusCode: 403));

        if (offset < 0) offset = 0;
        if (limit <= 0) limit = DefaultListLimit;
        if (limit > MaxListLimit) limit = MaxListLimit;

        // ListScoped materialises a full enumeration; capture the count once, then page from it.
        var items = ctx.Store!.ListScoped(scopes);
        var sourceList = items as List<ScopedArtifact> ?? items.ToList();
        int total = sourceList.Count;

        var paged = new List<MemoryEntry>(Math.Min(limit, Math.Max(0, total - offset)));
        for (int i = offset; i < total && paged.Count < limit; i++)
        {
            var item = sourceList[i];
            // Every field comes from the ArtifactEntry the store already handed back, projected by
            // the one mapper this API has — see MemoryEntry.From.
            paged.Add(MemoryEntry.From(ctx.Store!, item.Entry, item.Scope));
        }

        return Task.FromResult<IResult>(Results.Ok(new ListResponse(paged.ToArray(), total)));
    }

    private static async Task<IResult> RecallMemory(
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

    /// <summary>Case-insensitive single-entry lookup — the same resolution every store-side lookup
    /// in this codebase already agrees on (mirrors <see cref="IMemoryStore.TryUpdateEntry"/>'s
    /// default: <c>SanitizeName</c> compared with <see cref="StringComparison.OrdinalIgnoreCase"/>),
    /// so "archive" and "Archive" resolve to the one memory here too.</summary>
    private static ArtifactEntry? FindEntry(IMemoryStore store, string scope, string subject) =>
        store.LoadIndex(scope).FirstOrDefault(
            e => store.SanitizeName(e.Name).Equals(store.SanitizeName(subject), StringComparison.OrdinalIgnoreCase));

    /// <summary>Attribute-map bounds shared by every wire path that accepts a caller-supplied
    /// attribute dictionary. Returns an error message, or null when within bounds.
    /// <paramref name="fieldName"/> lets each caller's error text name its own field
    /// (<c>attributeFilter</c> for search, <c>attributes</c> for a PATCH body) without duplicating
    /// the check itself. <paramref name="maxKeys"/>/<paramref name="maxValuesPerKey"/> are NOT one
    /// shared number: the search filter's 64/64 is a request-shape bound with no relationship to
    /// storage, while a PATCH body must accept whatever <see cref="KnownAttributes.MaxAttributes"/>/
    /// <see cref="KnownAttributes.MaxValuesPerKey"/> the store itself would accept — a validator
    /// using the search filter's tighter numbers rejects entries the server's own write path
    /// produces (I-1: a memory legitimately stored with 70 <c>file</c> values 400s on an unchanged
    /// PATCH round-trip under a 64 bound), breaking the round-trip the complete-entry wire shape
    /// exists for.</summary>
    private static string? ValidateAttributeBounds(
        Dictionary<string, string[]>? attributes, int maxKeys, int maxValuesPerKey, string fieldName = "attributes")
    {
        if (attributes is { Count: int count } && count > maxKeys)
            return $"{fieldName} may not exceed {maxKeys} keys.";
        if (attributes is not null && attributes.Any(kv => kv.Value is { Length: int len } && len > maxValuesPerKey))
            return $"{fieldName} may not exceed {maxValuesPerKey} values per key.";
        return null;
    }

    private static Task<IResult> GetEntry(
        string store,
        string name,
        RequestContext ctx)
    {
        if (!ctx.HasPermission("read"))
            return Task.FromResult<IResult>(Results.Json(new ErrorResponse("Permission 'read' required."), statusCode: 403));

        string decoded = Uri.UnescapeDataString(name);
        var (scope, subject) = ctx.Store!.ParseQualifiedName(decoded);
        var entry = FindEntry(ctx.Store!, scope, subject);
        if (entry is null)
            return Task.FromResult<IResult>(Results.NotFound(new ErrorResponse($"Memory '{decoded}' not found.")));

        return Task.FromResult<IResult>(Results.Ok(MemoryEntry.From(ctx.Store!, entry, scope)));
    }

    /// <summary>True when <paramref name="patched"/> is null, or is the same sequence as
    /// <paramref name="stored"/> (order-sensitive — a caller only ever gets <paramref name="stored"/>
    /// from a prior GET, so an unedited round-trip compares byte-for-byte identical). Used to allow
    /// a complete-entry round-trip that leaves <c>Keywords</c> alone while refusing one that tries to
    /// change it (I-3, see <see cref="PatchEntry"/>'s doc comment for why a change is refused rather
    /// than applied).</summary>
    private static bool KeywordsUnchanged(string[]? patched, string[]? stored) =>
        patched is null || (patched ?? []).SequenceEqual(stored ?? [], StringComparer.Ordinal);

    /// <summary>
    /// PATCH is metadata-only over a **complete** entry body: the whole point of this wire shape is
    /// that a caller GETs the full entry, edits one field, and PATCHes the lot back — so every field
    /// the store alone derives must survive untouched no matter what the body carries for it, and
    /// every field the caller does own must actually be present, or an incomplete body silently
    /// wipes curated data (I-4) instead of being rejected.
    ///
    /// <para><b>What the body may change:</b> <c>Description</c> (→ <c>ContentPreview</c>) and
    /// <c>Attributes</c> (canonicalized via <see cref="KnownAttributes.Normalize"/> — the same
    /// canonicalizer every other write path funnels through, so a PATCH-written key doesn't persist
    /// with different casing/whitespace than a locally-written one; see I-2). Both are read
    /// straight off the parsed body — <see cref="Results.BadRequest(object?)"/> below rejects the
    /// request outright if either key is missing from the raw JSON, since a missing key binds to
    /// null/default just as readily as an explicit "clear this" and the two must not be confused
    /// (I-4). <c>Name</c>/<c>QualifiedName</c>/<c>Scope</c> are resolved from the route, never the
    /// body.</para>
    ///
    /// <para><b>What the body may NOT change:</b> <c>TermFrequencies</c>, <c>ChunkEntries</c>,
    /// <c>FileHashes</c>, <c>ChunkCount</c>, <c>OriginalBytes</c>, <c>CreatedAt</c> are never
    /// assigned from the body even though it carries values for all of them (I-5's mutation-tested
    /// guard: wiring any of these through must fail the derived-fields test). <c>Keywords</c> is the
    /// one field that looks caller-owned but is not: <see cref="IMemoryStore"/>'s own contract says
    /// a caller that computes keywords "has re-created the defect this branch exists to remove," and
    /// unlike <c>Description</c>/<c>Attributes</c>, the store's actual keyword pipeline
    /// (<see cref="MemoryDerivation.ForStore"/>) needs the memory's CONTENT to run — it classifies
    /// agent-vs-auto terms and computes BM25 boosts off <c>TextAnalysis.AnalyzeText</c>, which PATCH
    /// has no content to feed. Re-deriving from the stored artifact here would make PATCH a second,
    /// content-reading derivation entry point — exactly what this branch collapsed away. So: a body
    /// whose <c>Keywords</c> is null or identical to the stored value (the only way to genuinely
    /// leave it alone in a complete-entry body) is accepted and the stored value is kept verbatim; a
    /// body that tries to actually CHANGE <c>Keywords</c> is refused with <c>400</c> — that change
    /// must go through <c>StoreAsync</c>/<c>AppendAsync</c>, which have content to derive from.</para>
    ///
    /// <para>Routes through <see cref="IMemoryStore.TryUpdateEntry"/>, never <see cref="PluginPipeline"/>
    /// — that fires no event sinks, which matters because the entity-typing sink writes promotions
    /// back through this exact seam; going through the pipeline would let a remote metadata patch
    /// re-enter the sink that produced it. No <c>ArchiveVersion</c> call either: this is a metadata
    /// write, not a content write.</para>
    ///
    /// <para>Optimistic concurrency: the body's <c>UpdatedAt</c> is compared against the stored
    /// value inside the same atomic load-mutate-persist <c>TryUpdateEntry</c> already performs (under
    /// <c>FileMemoryStore</c>'s per-scope write lock), so the compare-and-write cannot race a
    /// concurrent writer. A mismatch returns <c>409</c> and leaves the stored entry untouched.</para>
    ///
    /// <para><b>Not re-derived here (M-3):</b> <c>FileHashes</c> is left exactly as stored even when
    /// the patched <c>Attributes["file"]</c> differs from what produced it, so the two can disagree
    /// after a PATCH that edits <c>file</c> values. Deliberate, not an oversight: re-hashing needs
    /// filesystem access PATCH has no reason to assume is available (a store-derived hasher is a
    /// <c>StoreAsync</c>-time concern — see <c>MemoryDerivation.ForStore</c>'s <c>fileHasher</c>
    /// delegate), and doing it here would be a second drift-baseline computation site. A caller that
    /// changes <c>file</c> references and wants a fresh baseline re-stores the content instead.</para>
    /// </summary>
    private static async Task<IResult> PatchEntry(
        string store,
        string name,
        HttpRequest request,
        RequestContext ctx)
    {
        if (!ctx.HasPermission("store"))
            return Results.Json(new ErrorResponse("Permission 'store' required."), statusCode: 403);

        if (!IsWritable(ctx, store))
            return Results.Json(new ErrorResponse($"Store '{store}' is read-only for this key."), statusCode: 403);

        // R-3: taking HttpRequest instead of a typed parameter took ASP.NET's model binding out of
        // the path, and model binding was also enforcing Content-Type. The .Accepts<MemoryEntry>
        // ("application/json") metadata on the MapPatch registration (added for R-2) makes routing
        // reject a mismatched Content-Type (e.g. text/plain) before this handler ever runs — but a
        // request with NO Content-Type header at all is not rejected by that matcher (measured: F-1),
        // so this explicit check is what catches that one case and is not otherwise dead weight.
        if (!request.HasJsonContentType())
            return Results.Json(new ErrorResponse("Content-Type must be application/json."),
                statusCode: StatusCodes.Status415UnsupportedMediaType);

        MemoryEntry req;
        try
        {
            using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
            var rootEl = doc.RootElement;

            // R-1: a well-formed but non-object JSON root ("[]", "123", "\"hello\"", "true", "null")
            // parses fine — JsonDocument.ParseAsync never throws for it — but TryGetProperty below
            // throws InvalidOperationException on a non-object element, which the catch(JsonException)
            // does not see, turning a malformed request into an unhandled 500 for an authenticated
            // caller. Model binding used to reject all of these with 400; reject them the same way,
            // explicitly, before the first TryGetProperty call.
            if (rootEl.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new ErrorResponse("PATCH body must be a JSON object."));

            // I-4: the wire is a COMPLETE-entry contract — the body IS the entry. A key missing
            // from the raw JSON binds to null/default exactly like an explicit "clear this field"
            // would, so without this check an incomplete body silently wipes curated metadata
            // (proven: {"name":"…","updatedAt":"<current>"} alone returned 200 and erased the
            // description, every keyword, and every attribute). Checking key PRESENCE rather than
            // value keeps a legitimate "this field is genuinely null" round-trip working — only an
            // omitted key is rejected, not an explicit null.
            if (!rootEl.TryGetProperty("description", out _)
                || !rootEl.TryGetProperty("attributes", out _)
                || !rootEl.TryGetProperty("keywords", out _))
                return Results.BadRequest(new ErrorResponse(
                    "PATCH requires a complete entry body — \"description\", \"attributes\" and " +
                    "\"keywords\" must all be present (explicit null is fine; an omitted key is not), " +
                    "or an incomplete body would silently erase curated metadata."));

            req = rootEl.Deserialize(ServerJsonContext.Default.MemoryEntry)
                ?? throw new JsonException("PATCH body deserialized to null.");
        }
        catch (JsonException)
        {
            return Results.BadRequest(new ErrorResponse("Malformed request body."));
        }

        // I-2: canonicalize before persisting — the same call every other write path makes, so a
        // PATCH-written key/value doesn't persist with different casing/whitespace than a local
        // write of the same map would. Replace semantics, not KnownAttributes.Merge: the body is
        // the caller's complete, already-edited attribute map (read via GET, edited, sent back),
        // not a delta to fold over what's already stored.
        //
        // R-6: normalize BEFORE bounds-checking (I-1's lesson again — never reject an attribute map
        // the server itself could legitimately produce). Normalize can only ever shrink a map
        // (case-insensitive key dedupe, trimming, dropping blanks), so a raw body that is over-bounds
        // before normalization but within bounds after (e.g. two keys differing only by case) must
        // not be rejected — checking the raw map first did exactly that.
        Dictionary<string, string[]>? normalizedAttributes = KnownAttributes.Normalize(req.Attributes);

        // I-1: the write-path bound (KnownAttributes.MaxAttributes / MaxValuesPerKey), not the
        // search filter's tighter 64/64 — see ValidateAttributeBounds's doc comment for why using
        // the search bound here broke the round-trip this wire shape exists for.
        if (ValidateAttributeBounds(normalizedAttributes, KnownAttributes.MaxAttributes, KnownAttributes.MaxValuesPerKey)
            is { } boundsError)
            return Results.BadRequest(new ErrorResponse(boundsError));

        string decoded = Uri.UnescapeDataString(name);
        var (scope, subject) = ctx.Store!.ParseQualifiedName(decoded);

        bool found = false;
        bool conflict = false;
        bool keywordsRejected = false;
        ArtifactEntry? persisted = null;

        bool ok = ctx.Store!.TryUpdateEntry(subject, scope, current =>
        {
            found = true;
            if (current.UpdatedAt != req.UpdatedAt)
            {
                conflict = true;
                return null;
            }

            // I-3: a genuine Keywords CHANGE has no content here to re-derive against, so it is
            // refused rather than persisted raw (which would let a remote client write a keyword
            // list no local path could ever produce). An unedited round-trip (null, or identical to
            // what's stored) is not a "change" and is allowed through.
            if (!KeywordsUnchanged(req.Keywords, current.Keywords))
            {
                keywordsRejected = true;
                return null;
            }

            var next = current with
            {
                ContentPreview = req.Description,
                Attributes = normalizedAttributes,
                Keywords = current.Keywords, // never taken from the wire — see I-3 above
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            persisted = next;
            return next;
        });

        if (!found)
            return Results.NotFound(new ErrorResponse($"Memory '{decoded}' not found."));

        if (conflict)
            return Results.Json(
                new ErrorResponse("UpdatedAt does not match the stored value — the entry was modified concurrently."),
                statusCode: StatusCodes.Status409Conflict);

        if (keywordsRejected)
            return Results.BadRequest(new ErrorResponse(
                "keywords may not be changed by PATCH — it requires content to re-derive against, " +
                "which this endpoint does not have. Re-store or append to change keywords."));

        if (!ok || persisted is null)
            return Results.Json(new ErrorResponse("Update failed."), statusCode: 500);

        return Results.Ok(MemoryEntry.From(ctx.Store!, persisted, scope));
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

        if (!IsWritable(ctx, store))
            return Results.Json(new ErrorResponse($"Store '{store}' is read-only for this key."), statusCode: 403);

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

        if (!IsWritable(ctx, store))
            return Results.Json(new ErrorResponse($"Store '{store}' is read-only for this key."), statusCode: 403);

        string decoded = Uri.UnescapeDataString(name);
        if (string.IsNullOrWhiteSpace(req.Content))
            return Results.BadRequest(new ErrorResponse("content is required."));

        if (decoded.Length > MaxNameLength)
            return Results.BadRequest(new ErrorResponse($"Name must not exceed {MaxNameLength} characters."));

        if (System.Text.Encoding.UTF8.GetByteCount(req.Content) > MaxContentBytes)
            return Results.BadRequest(new ErrorResponse($"Content must not exceed {MaxContentBytes / (1024 * 1024)} MB."));

        // The same attribute bounds POST /memories enforces. AppendRequest carries an attribute map
        // now (it used to carry Content alone), and an unbounded map on this path would be a hole
        // straight around the guard on the other one — the append endpoint reaches the identical
        // KnownAttributes.Merge, which truncates past MaxAttributes with no signal.
        if (ValidateAttributeBounds(req.Attributes, KnownAttributes.MaxAttributes, KnownAttributes.MaxValuesPerKey)
            is { } appendBoundsError)
            return Results.BadRequest(new ErrorResponse(appendBoundsError));

        try
        {
            var result = await pipeline.AppendAsync(ctx.Store!, decoded, req, ct);
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

        if (!IsWritable(ctx, store))
            return Task.FromResult<IResult>(Results.Json(new ErrorResponse($"Store '{store}' is read-only for this key."), statusCode: 403));

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

    private static Task<IResult> SearchMemory(
        string store,
        RequestContext ctx,
        PluginPipeline pipeline,
        CancellationToken ct,
        string? q = null,
        string? scopes = null,
        int limit = 20,
        string? excludeTopics = null,
        string? pathPrefix = null)
        => RunSearch(ctx, pipeline, q, scopes, limit, excludeTopics, pathPrefix, null, ct);

    private static Task<IResult> SearchMemoryPost(
        string store,
        SearchRequest req,
        RequestContext ctx,
        PluginPipeline pipeline,
        CancellationToken ct)
        => RunSearch(ctx, pipeline, req.Q, req.Scopes, req.Limit, req.ExcludeTopics, req.PathPrefix,
            req.AttributeFilter, ct);

    /// <summary>The one search implementation behind both GET and POST. GET carries the plain-string
    /// filters; POST additionally carries attributeFilter, which is a dictionary of arrays and does
    /// not belong in a query string.</summary>
    private static async Task<IResult> RunSearch(
        RequestContext ctx,
        PluginPipeline pipeline,
        string? q,
        string? scopes,
        int limit,
        string? excludeTopics,
        string? pathPrefix,
        Dictionary<string, string[]>? attributeFilter,
        CancellationToken ct)
    {
        if (!ctx.HasPermission("search"))
            return Results.Json(new ErrorResponse("Permission 'search' required."), statusCode: 403);

        if (string.IsNullOrWhiteSpace(q))
            return Results.BadRequest(new ErrorResponse("q parameter is required."));

        // Input bounds validation
        if (q.Length > 10_240)
            return Results.BadRequest(new ErrorResponse("Query too long (max 10KB)."));
        if (scopes is { Length: > 1024 })
            return Results.BadRequest(new ErrorResponse("Scopes parameter too long (max 1KB)."));
        if (excludeTopics is { Length: > 1024 })
            return Results.BadRequest(new ErrorResponse("excludeTopics parameter too long (max 1KB)."));
        if (pathPrefix is { Length: > 1024 })
            return Results.BadRequest(new ErrorResponse("pathPrefix parameter too long (max 1KB)."));
        if (ValidateAttributeBounds(attributeFilter, 64, 64, "attributeFilter") is { } filterBoundsError)
            return Results.BadRequest(new ErrorResponse(filterBoundsError));
        limit = Math.Clamp(limit, 1, 1000);

        var results = await pipeline.SearchAsync(ctx.Store!, q, scopes, limit,
            excludeTopics, pathPrefix, attributeFilter, ct);
        var mapped = results.Select<SearchResult, SearchResultItem>(r => r switch
        {
            EntryResult er => new SearchResultItem(
                "entry",
                er.Item.Scope == "ephemeral"
                    ? $"~{er.Item.Entry.Name}"
                    : ctx.Store!.FormatQualifiedName(er.Item.Scope, er.Item.Entry.Name),
                er.Score,
                AttributeReaders.PreviewText(er.Item.Entry.ContentPreview),
                OriginalBytes: er.Item.Entry.OriginalBytes,
                CreatedAt: er.Item.Entry.CreatedAt),
            ChunkEntryResult cr => new SearchResultItem(
                "chunk",
                cr.ParentItem.Scope == "ephemeral"
                    ? $"~{cr.ParentItem.Entry.Name}"
                    : ctx.Store!.FormatQualifiedName(cr.ParentItem.Scope, cr.ParentItem.Entry.Name),
                cr.Score,
                cr.Chunk.ContentPreview ?? AttributeReaders.PreviewText(cr.ParentItem.Entry.ContentPreview),
                cr.Chunk.ChunkIndex,
                cr.TotalChunks,
                OriginalBytes: cr.ParentItem.Entry.OriginalBytes,
                CreatedAt: cr.ParentItem.Entry.CreatedAt),
            // A topic is a scope label, not a memory — no real byte count or creation time exists
            // to report, so both stay null rather than being aggregated from member entries.
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

        if (ctx.Store is not ILocalMemoryStore localStore)
            return Task.FromResult<IResult>(Results.Json(
                new ErrorResponse("Bundle export requires a store backed by a local filesystem."),
                statusCode: StatusCodes.Status501NotImplemented));

        var stream = BundleService.ExportToStream(localStore, req.Topics);
        string filename = $"export-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.scrinia-bundle";
        if (!string.IsNullOrWhiteSpace(req.Filename))
        {
            // Sanitize: strip control chars, path separators, keep only safe filename characters
            string sanitized = new(req.Filename
                .Where(c => !char.IsControl(c) && c != '/' && c != '\\' && c != '\r' && c != '\n')
                .ToArray());
            if (!string.IsNullOrWhiteSpace(sanitized))
                filename = Path.GetFileName(sanitized); // final safety net against path traversal
        }

        return Task.FromResult<IResult>(Results.File(stream, "application/octet-stream", filename));
    }

    private static async Task<IResult> ImportBundle(
        string store,
        HttpRequest request,
        RequestContext ctx,
        // CLI comma-splits; the HTTP endpoint binds repeated ?topics= keys. Deliberate — see
        // "Bundle commands: topic filters differ deliberately between CLI and HTTP" in
        // docs/architecture/cli.md.
        [FromQuery] string[]? topics = null,
        bool overwrite = false)
    {
        if (!ctx.HasPermission("import"))
            return Results.Json(new ErrorResponse("Permission 'import' required."), statusCode: 403);

        if (!IsWritable(ctx, store))
            return Results.Json(new ErrorResponse($"Store '{store}' is read-only for this key."), statusCode: 403);

        if (ctx.Store is not ILocalMemoryStore localStore)
            return Results.Json(
                new ErrorResponse("Bundle import requires a store backed by a local filesystem."),
                statusCode: StatusCodes.Status501NotImplemented);

        // Kestrel's global MaxRequestBodySize (10 MB, set in Program.cs) is below the
        // intended 50 MB bundle-import ceiling (FormOptions.MultipartBodyLengthLimit).
        // Raise the per-request limit before the body is read so imports up to 50 MB
        // aren't rejected with 413 before the multipart limit ever applies. Must run
        // before the first access to request.Form, which triggers body reading.
        var maxBodySizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxBodySizeFeature is not null && !maxBodySizeFeature.IsReadOnly)
            maxBodySizeFeature.MaxRequestBodySize = 50 * 1024 * 1024;

        if (!request.HasFormContentType || request.Form.Files.Count == 0)
            return Results.BadRequest(new ErrorResponse("Multipart file upload required."));

        var file = request.Form.Files[0];
        using var stream = file.OpenReadStream();

        try
        {
            var (topicCount, entryCount, names, skippedCategories) = BundleService.ImportFromStream(localStore, stream, topics, overwrite);
            // RULED 2026-08-10: "imported nothing" is not a success. A 200 here made any deploy
            // step gating on the status code pass while the bundle had not been imported. 422 —
            // the request was well-formed and authorised, but yielded nothing to import.
            if (topicCount == 0 && skippedCategories.Count == 0)
                return Results.Json(
                    new ErrorResponse("No topics were imported (empty bundle or all filtered out)."),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            return Results.Ok(new
            {
                message = $"Imported {topicCount} topic(s) ({entryCount} entries): {string.Join(", ", names)}",
                skippedCategories,
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
        catch (System.IO.InvalidDataException)
        {
            return Results.BadRequest(new ErrorResponse("Invalid or corrupted bundle file."));
        }
    }
}
