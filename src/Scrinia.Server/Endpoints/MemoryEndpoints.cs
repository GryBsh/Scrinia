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

        group.MapPost("/memories", RememberMemory).RequirePermission(Permissions.Store);
        group.MapGet("/memories", ListMemory).RequirePermission(Permissions.Search);
        group.MapGet("/memories/{name}", RecallMemory).RequirePermission(Permissions.Read);
        group.MapGet("/memories/{name}/entry", GetEntry).RequirePermission(Permissions.Read);
        // R-2: PatchEntry takes HttpRequest (it parses the body manually — see its own doc comment),
        // not a typed parameter, which otherwise strips the request body from the generated OpenAPI
        // document (a generated client would see a PATCH that accepts no body). .Accepts restores it.
        group.MapPatch("/memories/{name}", PatchEntry).Accepts<MemoryEntry>("application/json")
            .RequirePermission(Permissions.Store);
        group.MapDelete("/memories/{name}", ForgetMemory).RequirePermission(Permissions.Forget);
        group.MapPost("/memories/{name}/append", AppendMemory).RequirePermission(Permissions.Append);
        group.MapPost("/memories/{name}/copy", CopyMemory).RequirePermission(Permissions.Copy);
        // {name} is the DESTINATION on this one — see CopyFromClient's doc comment.
        group.MapPost("/memories/{name}/copy-from-client", CopyFromClient).RequirePermission(Permissions.Copy);
        group.MapGet("/memories/{name}/chunks/{i:int}", GetChunk).RequirePermission(Permissions.Read);
        group.MapGet("/search", SearchMemory).RequirePermission(Permissions.Search);
        group.MapPost("/search", SearchMemoryPost).RequirePermission(Permissions.Search);
        group.MapPost("/export", ExportTopics).RequirePermission(Permissions.Export);
        group.MapPost("/import", ImportBundle).RequirePermission(Permissions.Import);
    }

    private const int MaxNameLength = 256;
    private const long MaxContentBytes = 5 * 1024 * 1024; // 5 MB per element

    // Bounds for the metadata copy-from-client carries — see CarriedEntryBoundsError's doc
    // comment for the full reasoning. Round 2 review split these into TOP-LEVEL bounds (this
    // group — mostly RESOURCE guards: Description and TermFrequencies have no local ceiling to
    // mirror at all, and Keywords/AgentKeywords is the one genuine parity guard among them) and
    // PER-CHUNK-ENTRY bounds (below, unchanged from round 1 — genuine PARITY guards, matched to
    // what TextAnalysis.ComputeChunkEntries always produces, since a chunk entry's fields are
    // never caller-curated the way the top-level Description is).

    /// <summary>Top-level Keywords/AgentKeywords — the LOCAL ceiling
    /// (<see cref="TextAnalysis.DefaultMaxMergedKeywords"/>), not <see cref="AnalyzeText"/>'s
    /// topN=25, which bounds only the auto half before agent keywords are merged in. A memory with
    /// 5+ agent keywords legitimately lands 26-30 total locally.
    /// <para><b>Why no local WRITE can exceed it, and the one read that still can (was a residual;
    /// narrowed by Task 14).</b> One
    /// mutation in the codebase adds keywords without a cap: <c>ApplyMetadataMutation</c>'s keyword
    /// merge in <c>MemoryTools.Core.cs</c>, whose additions <c>MemoryDerivation.ForMetadataUpdate</c>
    /// then unions into <c>AgentKeywords</c> — neither union caps. Its one production caller was
    /// <c>link()</c>, which minted a <c>ref:{name}</c>-shaped KEYWORD and let
    /// <c>AttributeExtractor.RouteKeywords</c> convert it back to an attribute; past
    /// <see cref="AttributeExtractor.MaxValueLength"/> that conversion gives up and the element
    /// stays a keyword, so linking one memory to enough long-named destinations really did carry
    /// 31+ keywords here and earn this 400 for a value the local path genuinely produced.
    /// <c>link()</c> now writes the <c>ref</c> attribute directly and mints no keyword at any name
    /// length, which leaves <c>UpdateMeta</c> with no production caller that passes
    /// <c>keywords</c> at all — it is <c>internal</c>, has no MCP tool and no CLI command, and the
    /// only remaining callers are this repo's tests. Every OTHER write path
    /// (<c>remember</c>, <c>append</c>) merges through <see cref="TextAnalysis.MergeKeywordsWithSource"/>,
    /// which caps at this same constant. The four remaining <c>TryUpdateEntry</c> call sites in
    /// <c>src/</c> all leave the keyword count where they found it or lower:
    /// <c>EntityTypingEventHandler</c> and <c>MaintenanceEventSink</c>'s first write change only
    /// attributes, <c>MaintenanceEventSink</c>'s second write only REMOVES the legacy
    /// <c>orphan</c> keyword, <c>MemoryTools.Exists</c> passes a mutation that returns null, and
    /// <see cref="PatchEntry"/> — in this file — refuses a keyword change outright.</para>
    ///
    /// <para><b>The limit of that claim.</b> It is about what this codebase WRITES today, not about
    /// what it can READ. <c>AttributeMigration.Forward</c> forward-fills a
    /// <c>RecommendedKeys</c>-shaped keyword into <c>Attributes</c> but deliberately LEAVES the
    /// keyword in place, and applies no count bound; so a pre-Task-14 sidecar already on disk,
    /// carrying 31+ <c>ref:&lt;long&gt;</c> keywords from the defect this comment describes, still
    /// reads back with them intact. Demote it to an ephemeral with <c>copy()</c> and promote it
    /// again and this bound fires on data no current write path could produce. Guarding
    /// <c>Forward</c> by length was considered and rejected: it would silently DROP references from
    /// legacy entries, which is the harm <see cref="KnownAttributes.MaxValuesPerKey"/>'s own doc
    /// argues against, and <c>Forward</c> adds no keyword — it only reads one that is already
    /// there. Migrating such an entry is a re-<c>remember</c>, which re-derives through the capped
    /// path.</para></summary>
    private const int MaxCarriedKeywords = TextAnalysis.DefaultMaxMergedKeywords;

    /// <summary>Top-level Description. No local ceiling exists — <c>MemoryDerivation.cs:152-154</c>
    /// stores a curated description VERBATIM, untruncated; the 500-char cap belongs to
    /// <see cref="ContentPreview.Generate"/>, which only ever runs on the AUTO snippet
    /// (<c>string.IsNullOrWhiteSpace(description) ? Generate(joined) : description</c>). This is
    /// therefore a RESOURCE guard, not a parity guard: 50,000 characters — roughly two orders of
    /// magnitude past any curated description a human would plausibly write, while still ~100x
    /// smaller than a single content element's 5&nbsp;MB cap, so many such entries cannot reproduce
    /// the unbounded-sidecar concern Critical 1 exists to close.</summary>
    private const int MaxCarriedDescriptionLength = 50_000;

    /// <summary>Top-level TermFrequencies. No local ceiling exists either:
    /// <see cref="AnalyzeText"/> prunes to <see cref="TextAnalysis.DefaultMaxTfTerms"/> (100), but
    /// <c>MemoryDerivation.ForStore</c> mutates that SAME dictionary twice more afterwards —
    /// <c>BoostKeywords</c> (one insert per merged-keyword token) and
    /// <c>AttributeExtractor.SeedTermFrequencies</c> (one insert per attribute key and per
    /// attribute-value token), both unconditional. A resource-guard derivation, not a tight upper
    /// bound: <c>DefaultMaxTfTerms</c> (the content-derived floor) plus, per attribute key up to
    /// <see cref="KnownAttributes.MaxAttributes"/>, one term for the key itself and up to
    /// <see cref="KnownAttributes.MaxValuesPerKey"/> for its values — undercounts a multi-word
    /// attribute VALUE (each contributes more than one token), but attribute-value token count is
    /// already unbounded on the LOCAL path too, so a formula bounding it exactly would be bounding
    /// something the parity guarantee was never about.</summary>
    private const int MaxCarriedTermFrequencyTerms =
        TextAnalysis.DefaultMaxTfTerms + KnownAttributes.MaxAttributes * (1 + KnownAttributes.MaxValuesPerKey);

    /// <summary>Per-chunk-entry ContentPreview only — <see cref="ContentPreview.Generate"/>'s own
    /// default <c>maxLength</c>. Unlike the top-level Description, a chunk entry's preview is NEVER
    /// caller-curated (<see cref="TextAnalysis.ComputeChunkEntries"/> always calls
    /// <c>Generate</c>), so this one genuinely is a parity guard and stays unchanged from round
    /// 1.</summary>
    private const int MaxCarriedChunkPreviewLength = 500;

    /// <summary>Per-chunk-entry Keywords only — <see cref="AnalyzeText"/>'s own <c>topN</c> default,
    /// NOT <see cref="TextAnalysis.DefaultMaxMergedKeywords"/>: <see cref="TextAnalysis.ComputeChunkEntries"/>
    /// calls <c>AnalyzeText</c> directly per chunk, never <c>MergeKeywordsWithSource</c>, so a chunk
    /// entry's keywords are the raw auto-extracted top 25, never merged with agent keywords. Stays
    /// unchanged from round 1.</summary>
    private const int MaxCarriedChunkKeywords = 25;

    /// <summary>True when the caller may write to <c>store</c> — i.e. the store is not
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
        if (!IsWritable(ctx, store))
            return Results.Json(new ErrorResponse($"Store '{store}' is read-only for this key."), statusCode: 403);

        if (req.Content is null || req.Content.Length == 0 || string.IsNullOrWhiteSpace(req.Name))
            return Results.BadRequest(new ErrorResponse("content and name are required."));

        if (req.Name.Length > MaxNameLength)
            return Results.BadRequest(new ErrorResponse($"Name must not exceed {MaxNameLength} characters."));

        if (req.Content.Any(c => c is not null && System.Text.Encoding.UTF8.GetByteCount(c) > MaxContentBytes))
            return Results.BadRequest(new ErrorResponse($"Each content element must not exceed {MaxContentBytes / (1024 * 1024)} MB."));

        // Resolved here, before the write, so a name the store refuses is a 400 rather than the
        // unhandled 500 that StoreAsync's internal ParseQualifiedName would otherwise produce.
        // See NameRefusal for why this is a probe and not a catch around StoreAsync.
        if (NameRefusal(ctx.Store!, req.Name) is { } nameRefusal)
            return nameRefusal;

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
        //
        // Normalized first, matching PatchEntry (R-6). Normalize can only ever SHRINK a map
        // (case-insensitive key dedupe, trimming, dropping blanks), so a body that is over-bounds
        // raw but within bounds normalized — two keys differing only by case, right at the cap —
        // was rejected here and accepted by PATCH. The store would have accepted either; a bound
        // that refuses a map the server's own write path produces is I-1's lesson, and the two
        // endpoints disagreeing about the same map is the same defect wearing a second hat.
        if (ValidateAttributeBounds(
                KnownAttributes.Normalize(req.Attributes),
                KnownAttributes.MaxAttributes, KnownAttributes.MaxValuesPerKey)
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

    /// <summary>Task 9 fix round 1, F-4: <c>scopes</c> is bound as repeated query parameters
    /// (<c>?scopes=a&amp;scopes=b</c>), matching <c>/search</c>'s encoding (Task 9, D-2), rather
    /// than the single comma-joined string this route used before. <c>IMemoryStore.ListScoped</c>
    /// promises list semantics (Commit 1 of Task 9) and <c>FileMemoryStore</c> honoured it, but
    /// this route re-joined the list on commas and the server split it back apart — so a scope
    /// named "odd,name" was addressable through every local caller and invisible to every remote
    /// one. <see cref="ValidateStringArrayBounds"/> is new here too: this route had never had a
    /// bound on <c>scopes</c> before, so this is a net gain, not just parity with
    /// <c>/search</c>.</summary>
    private static Task<IResult> ListMemory(
        string store,
        RequestContext ctx,
        string[]? scopes = null,
        int offset = 0,
        int limit = DefaultListLimit)
    {
        if (offset < 0) offset = 0;
        if (limit <= 0) limit = DefaultListLimit;
        if (limit > MaxListLimit) limit = MaxListLimit;

        if (ValidateStringArrayBounds(scopes, 64, 1024, "scopes") is { } scopesBoundsError)
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse(scopesBoundsError)));

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
        string decoded = Uri.UnescapeDataString(name);

        // Caught around the call rather than pre-probed: ResolveArtifactAsync — which is what
        // ShowAsync resolves through — dispatches an inline "NMP/2 " artifact and a file:// URI
        // BEFORE it ever reaches ParseQualifiedName, and both of those are names this route
        // accepts. A pre-probe would have to re-state that dispatch here as a second copy, and
        // would 400 a file:// URI (it contains a ':', which the bare-name branch refuses). Safe to
        // wrap whole because every ArgumentException ShowAsync can raise is about the caller's
        // name — the empty-input guard, Path.GetFullPath on a malformed file:// path, and
        // ParseQualifiedName; the decode below throws InvalidDataException/InvalidOperationException
        // for a corrupt stored artifact, never ArgumentException — and because nothing here writes,
        // so no refusal can describe a half-completed operation.
        ShowResponse? result;
        try
        {
            result = await MemoryOrchestrator.ShowAsync(ctx.Store!, decoded, ct);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new ErrorResponse(CallerFacingMessage(ex)));
        }

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

    /// <summary>Task 9, D-5: the search filter's old bound was a single 1KB cap on the whole
    /// comma-joined <c>scopes</c>/<c>excludeTopics</c> string. Converted to an array, that becomes
    /// a COUNT bound (how many scope names) and a PER-ELEMENT length bound (how long one scope
    /// name may be) — dropping either would let the total go unbounded again: a count-only bound
    /// admits arbitrarily long individual entries, and a length-only bound admits arbitrarily many
    /// short ones.
    ///
    /// <para><b>The numbers are a DELIBERATE loosening, not a preservation (Task 9 fix round 1,
    /// F-7).</b> 64 entries x 1024 chars/entry accepts up to 64 KB total per field, against the old
    /// single string's 1 KB ceiling — 64x. D-5 required "do not let the total go unbounded", which
    /// this satisfies, but it never said "keep the same ceiling", and it would not have been
    /// preservable anyway: the old 1 KB cap was already ambiguous once split across multiple scope
    /// names (was it 1 KB total, or 1 KB per name?), so there was no single old number to carry
    /// forward. 64 mirrors this file's existing <c>attributeFilter</c> key-count bound
    /// (<c>ValidateAttributeBounds(attributeFilter, 64, 64, ...)</c>) and 1024 mirrors the old
    /// single-string cap, now applied per element instead of to the whole request.</para></summary>
    private static string? ValidateStringArrayBounds(
        string[]? values, int maxCount, int maxElementLength, string fieldName)
    {
        if (values is { Length: int count } && count > maxCount)
            return $"{fieldName} may not exceed {maxCount} entries.";
        if (values is not null && values.Any(v => v is { Length: int len } && len > maxElementLength))
            return $"{fieldName} entries may not exceed {maxElementLength} characters.";
        return null;
    }

    private static Task<IResult> GetEntry(
        string store,
        string name,
        RequestContext ctx)
    {
        string decoded = Uri.UnescapeDataString(name);
        if (!TryResolveName(ctx.Store!, decoded, out string scope, out string subject, out IResult? refusal))
            return Task.FromResult(refusal!);

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
    /// guard: wiring any of these through must fail the derived-fields test).
    /// <b><c>TermFrequencies</c> does still CHANGE here</b> — just not from the body: attribute
    /// values feed the BM25 index, so <see cref="IMemoryStore.TryUpdateEntry"/> re-derives them
    /// from the attributes accepted above, via <see cref="MemoryDerivation.ForMetadataUpdate"/>.
    /// That is the store deriving, not the caller dictating, and it is what stops a remote
    /// metadata write — reached in production only through <c>link()</c> — from succeeding while
    /// leaving the entry unfindable by the attribute it just added. A caller-supplied map is
    /// still discarded whole. <c>Keywords</c> is the
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

        // Same probe every other write path makes — see NameRefusal's doc comment. PatchEntry
        // resolves `decoded` itself just below, so left unguarded a `/temp/...` name would resolve
        // straight to the persistent scope `local:temp` and patch it in place, the same defect the
        // other write routes were fixed against.
        if (NameRefusal(ctx.Store!, decoded) is { } nameRefusal)
            return nameRefusal;

        if (!TryResolveName(ctx.Store!, decoded, out string scope, out string subject, out IResult? refusal))
            return refusal!;

        bool found = false;
        bool conflict = false;
        bool keywordsRejected = false;
        bool written = false;

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

            // NOT captured for the response. TryUpdateEntry re-derives TermFrequencies from these
            // attributes AFTER this callback returns (MemoryDerivation.ForMetadataUpdate), so this
            // value is the entry one step before what actually gets stored. Answering with it put a
            // stale derived field in a 200 body — measured: the response said the new attribute's
            // term was absent while an immediate re-GET said it was present. That is exactly the
            // "a derivable field that can disagree with its payload" defect the complete-entry wire
            // shape exists to remove, so the response is read back from the store below instead.
            // `written` only records THAT a write happened, for the 500 guard.
            written = true;
            return current with
            {
                ContentPreview = req.Description,
                Attributes = normalizedAttributes,
                Keywords = current.Keywords, // never taken from the wire — see I-3 above
                UpdatedAt = DateTimeOffset.UtcNow,
            };
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

        if (!ok || !written)
            return Results.Json(new ErrorResponse("Update failed."), statusCode: 500);

        // Read back rather than echo what the mutation built: TryUpdateEntry re-derives
        // TermFrequencies after the callback returns, so the only way to answer with the entry as
        // STORED — every derived field included, none of them one step stale — is to ask the store.
        // In-process and served from the index cache TryUpdateEntry just refreshed, so this is not
        // a second round trip in any deployment that reaches this handler.
        var persisted = FindEntry(ctx.Store!, scope, subject);
        if (persisted is null)
            // The write succeeded and the entry is already gone, i.e. a concurrent delete landed
            // between the two. Reporting 200 with the pre-write copy would describe a memory that
            // no longer exists; 404 is the honest answer about the state the caller can observe.
            return Results.NotFound(new ErrorResponse(
                $"Memory '{decoded}' was updated but no longer exists — it was deleted concurrently."));

        return Results.Ok(MemoryEntry.From(ctx.Store!, persisted, scope));
    }

    private static async Task<IResult> ForgetMemory(
        string store,
        string name,
        RequestContext ctx,
        PluginPipeline pipeline,
        CancellationToken ct)
    {
        if (!IsWritable(ctx, store))
            return Results.Json(new ErrorResponse($"Store '{store}' is read-only for this key."), statusCode: 403);

        string decoded = Uri.UnescapeDataString(name);

        // ForgetAsync resolves the name itself (MemoryOrchestrator.ForgetAsync) and would otherwise
        // 500. Nothing has been deleted at this point, so the refusal is honest about the state.
        if (NameRefusal(ctx.Store!, decoded) is { } nameRefusal)
            return nameRefusal;

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
        if (!IsWritable(ctx, store))
            return Results.Json(new ErrorResponse($"Store '{store}' is read-only for this key."), statusCode: 403);

        string decoded = Uri.UnescapeDataString(name);
        if (string.IsNullOrWhiteSpace(req.Content))
            return Results.BadRequest(new ErrorResponse("content is required."));

        if (decoded.Length > MaxNameLength)
            return Results.BadRequest(new ErrorResponse($"Name must not exceed {MaxNameLength} characters."));

        if (System.Text.Encoding.UTF8.GetByteCount(req.Content) > MaxContentBytes)
            return Results.BadRequest(new ErrorResponse($"Content must not exceed {MaxContentBytes / (1024 * 1024)} MB."));

        // See RememberMemory's — an append creates the memory when it does not exist, so this is a
        // write path with the same unguarded-throw exposure.
        if (NameRefusal(ctx.Store!, decoded) is { } nameRefusal)
            return nameRefusal;

        // The same attribute bounds POST /memories enforces, normalized first for the same reason.
        // AppendRequest carries an attribute map now (it used to carry Content alone), and an
        // unbounded map on this path would be a hole straight around the guard on the other one —
        // the append endpoint reaches the identical KnownAttributes.Merge, which truncates past
        // MaxAttributes with no signal.
        if (ValidateAttributeBounds(
                KnownAttributes.Normalize(req.Attributes),
                KnownAttributes.MaxAttributes, KnownAttributes.MaxValuesPerKey)
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
        if (!IsWritable(ctx, store))
            return Task.FromResult<IResult>(Results.Json(new ErrorResponse($"Store '{store}' is read-only for this key."), statusCode: 403));

        string decoded = Uri.UnescapeDataString(name);
        if (string.IsNullOrWhiteSpace(req.Destination))
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse("destination is required.")));

        // BOTH names are caller-supplied and both are resolved inside CopyMemory
        // (FileMemoryStore.CopyMemory parses the source and the destination separately), so both
        // need the guard. Source first, matching the order CopyMemory itself resolves them, so the
        // message names the same one a caller with two bad names would see from the store.
        if (NameRefusal(ctx.Store!, decoded) is { } sourceRefusal)
            return Task.FromResult(sourceRefusal);
        if (NameRefusal(ctx.Store!, req.Destination) is { } destinationRefusal)
            return Task.FromResult(destinationRefusal);

        bool ok = ctx.Store!.CopyMemory(decoded, req.Destination, req.Overwrite, out string msg);
        if (!ok)
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse(msg)));
        return Task.FromResult<IResult>(Results.Ok(new { message = msg }));
    }

    /// <summary>
    /// Stores an artifact plus the metadata the CALLER derived, deriving nothing itself.
    ///
    /// <para><b><c>{name}</c> is the DESTINATION here</b> — the opposite of
    /// <see cref="CopyMemory"/>, where it is the source. That is deliberate and is the whole reason
    /// this route exists: the source is a client-side ephemeral this process has never seen, so
    /// there is no server-side name to put in the path. <b>The handler performs no name resolution
    /// against this store at all</b>, which is precisely what removes the defect it replaces — a
    /// promotion used to arrive as a bare name and get resolved here, silently copying an unrelated
    /// persistent memory that happened to share it.</para>
    ///
    /// <para>A separate route rather than an optional field on <see cref="CopyRequest"/>: a server
    /// predating this would ignore an unknown field and fall through to that same wrong resolution,
    /// reporting success. An unknown route 404s, which the client surfaces as a refusal.</para>
    /// </summary>
    private static Task<IResult> CopyFromClient(
        string store,
        string name,
        CopyFromClientRequest req,
        RequestContext ctx)
    {
        if (!IsWritable(ctx, store))
            return Task.FromResult<IResult>(Results.Json(new ErrorResponse($"Store '{store}' is read-only for this key."), statusCode: 403));

        string decoded = Uri.UnescapeDataString(name);
        if (string.IsNullOrWhiteSpace(decoded))
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse("A destination name is required.")));

        if (decoded.Length > MaxNameLength)
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse($"Name must not exceed {MaxNameLength} characters.")));

        // Same probe every other write path makes — see NameRefusal's doc comment. `decoded` is the
        // DESTINATION on this route (see the class doc comment above), and ParseQualifiedName is
        // invoked on it further down inside WriteCarriedCopy's setup, so left unguarded a
        // `/temp/...` destination would resolve straight to the persistent scope `local:temp` and
        // write permanent bytes there, the same defect the other write routes were fixed against.
        if (NameRefusal(ctx.Store!, decoded) is { } nameRefusal)
            return Task.FromResult(nameRefusal);

        if (req.Content is null || req.Content.Length == 0)
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse("content is required.")));

        if (req.Entry is null)
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse("entry is required.")));

        // Round 2 review, Important 3: EncodeChunks runs outside any try/catch below and calls
        // UTF8.GetBytes on each element — a null element throws ArgumentNullException there, an
        // unhandled 500. Rejected explicitly, before the size check (which already tolerated null
        // via `c is not null &&`, silently letting it through to that later crash).
        if (req.Content.Any(c => c is null))
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse("content elements must not be null.")));

        if (req.Content.Any(c => System.Text.Encoding.UTF8.GetByteCount(c) > MaxContentBytes))
            return Task.FromResult<IResult>(Results.BadRequest(
                new ErrorResponse($"Each content element must not exceed {MaxContentBytes / (1024 * 1024)} MB.")));

        // Normalized BEFORE bounds-checking, matching RememberMemory/PatchEntry (I-1/R-6): Normalize
        // can only ever shrink a map, so checking the raw one would reject a body the store itself
        // would accept.
        Dictionary<string, string[]>? normalizedAttributes = KnownAttributes.Normalize(req.Entry.Attributes);
        if (ValidateAttributeBounds(normalizedAttributes, KnownAttributes.MaxAttributes, KnownAttributes.MaxValuesPerKey)
            is { } boundsError)
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse(boundsError)));

        // Round 2 review, Critical 1: this is the ONLY wire entry point for TermFrequencies,
        // FileHashes, ChunkEntries, Keywords, AgentKeywords and Description — StoreRequest doesn't
        // carry them and PatchEntry refuses them — so nothing bounded them before now. An unbounded
        // request lands in CachedIndex for the process lifetime and every search scores against it.
        // See CarriedEntryBoundsError's doc comment for where each bound comes from.
        if (CarriedEntryBoundsError(req.Entry, req.Content.Length) is { } carriedBoundsError)
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse(carriedBoundsError)));

        if (ctx.Store is not ILocalMemoryStore local)
            return Task.FromResult<IResult>(Results.Json(
                new ErrorResponse("copy-from-client requires a store backed by a local filesystem."),
                statusCode: StatusCodes.Status501NotImplemented));

        // Round 2 review, Critical 2 (part 1 of 2): WriteCarriedCopy resolves `decoded` via
        // ParseQualifiedName internally. Pre-parsing HERE, in isolation, narrows the
        // ArgumentException catch to exactly the operation that can legitimately throw it — not the
        // disk write or LoadIndex's/Upsert's own sidecar I/O that follow inside WriteCarriedCopy, so
        // neither an exception thrown after the artifact has landed nor one from an unrelated I/O
        // fault elsewhere in the destination scope's index gets misattributed to "malformed
        // destination name". `dstScope` is kept (not discarded) for part 2 below.
        //
        // TASK 13 UPDATE. ParseQualifiedName no longer sanitizes only the leaf: it builds the
        // scope through MemoryNaming.BuildScopedTopicScope — the one scope constructor — which
        // sanitizes every segment and rejects the residue substitution cannot repair (a trailing
        // dot or space, a bare ".", NUL). That changes what reaches this catch, in both
        // directions:
        //   * '*','?','<','>','|','"' are now REPAIRED at source ('/a*b/x' stores under
        //     "local:a_b", exactly as the leaf has always treated them) and so no longer produce
        //     any error here at all — they used to reach Directory.CreateDirectory intact and throw
        //     IOException at part 2 below.
        //   * A trailing dot/space, a bare "." and NUL now throw ArgumentException and are caught
        //     HERE, one exception family earlier than before. Only NUL, deliberately: measured on
        //     this build, CON/PRN/AUX/COM1-9/LPT1-9 create real, writable directories and alias
        //     nothing, so refusing them would reject input that demonstrably works.
        // The scope-ALIASING defect this comment used to record as unfixed ("local:topic.",
        // "local:topic " and "local:." being distinct cache keys/locks over one directory) is fixed
        // at its source; no endpoint-level mitigation carries it any more.
        string dstScope;
        try
        {
            (dstScope, _) = local.ParseQualifiedName(decoded);
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse(CallerFacingMessage(ex))));
        }

        // Round 2 review, Critical 2 (part 2 of 2): the round-1 fix caught IOException around the
        // WHOLE WriteCarriedCopy call, which reintroduced — one exception family over — exactly what
        // part 1 above just fixed for ArgumentException: Upsert (sidecar write + read-time
        // forward-fill) runs AFTER AtomicWriteAllText has already succeeded, so a sidecar-write
        // failure, a locked index file, or a full disk there would 400 with "contains characters the
        // filesystem cannot store" over an artifact that DID land — the caller's name blamed for a
        // server-side fault. Fixed by probing ONLY the directory WriteCarriedCopy will need, before
        // any write: Path.GetDirectoryName(ArtifactPath(subject, scope)) == GetStoreDirForScope(scope)
        // always (ArtifactPath is Path.Combine(GetStoreDirForScope(scope), SanitizeName(subject) +
        // ".nmp2") — the directory component depends only on `scope`, never on `subject`'s casing),
        // so this probe needs no casing-adoption lookup to be exact. Directory.CreateDirectory is
        // idempotent, so probing it here changes nothing WriteCarriedCopy's own (identical) call
        // does not already do — it just runs it somewhere a throw can still honestly be blamed on
        // the name. Nothing past this point is wrapped in a catch: a failure inside WriteCarriedCopy
        // now propagates as an unhandled exception (the global handler's 500), which is the honest
        // answer for a fault this endpoint did not cause and cannot attribute to the caller.
        // TASK 13: this catch is NOT dead, but every cause it had at the time it was written is
        // gone. `dstScope` is now built from sanitized, validated segments, so no character in the
        // caller's name can reach here and fail. What remains is genuinely reachable and was always
        // in scope for it: a total path that exceeds the filesystem's limit (the name is capped at
        // 256, the workspace root beneath it is not), an ordinary FILE already occupying the
        // directory name, a full or disconnected volume. Kept for those, with the message widened
        // off "contains characters the filesystem cannot store" — which is now the one thing it
        // cannot be.
        //
        // ASYMMETRY, deliberate and currently unreachable: GetStoreDirForScope sits INSIDE this try
        // and throws ArgumentException (PathRouter.ToStoreDir, "Unknown scope: …") for a scope that
        // is neither "local" nor "local:…" — which catch (IOException) does not cover, so it would
        // escape as a 500. It cannot happen from here: `dstScope` comes from ParseQualifiedName one
        // block above, which returns only those two shapes. Left uncaught rather than widened,
        // because if that invariant ever broke, a 500 is the honest answer — the caller's name would
        // not be at fault, this endpoint's own resolver would be.
        try
        {
            Directory.CreateDirectory(local.GetStoreDirForScope(dstScope));
        }
        catch (IOException)
        {
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse(
                $"Destination name '{decoded}' could not be resolved to a usable "
                + "directory on the filesystem.")));
        }

        // The artifact is encoded HERE, from req.Content — never accepted as a raw string from the
        // caller — so its "C:{k}" chunk-count header can never disagree with the artifact's actual
        // chunk sections: EncodeChunks writes k = req.Content.Length and emits exactly that many
        // ##CHUNK: sections. WriteCarriedCopy's ChunkCount (Nmp2ChunkedEncoder.GetChunkCount, a
        // lenient scrape with no 1..MaxChunkCount range check) therefore cannot be handed a bogus,
        // mismatched count through this route — the concern that check exists for elsewhere. See
        // task-3-report.md for the verification.
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(req.Content);

        // Round 2 review, Important 5: OriginalBytes is DERIVED now, not carried — CarriedEntry no
        // longer has the field (RULED 2026-08-14). The encoder already computes the true value the
        // same way for both the single- and multi-chunk paths: the sum of each element's UTF-8 byte
        // length (Nmp2ChunkedEncoder.cs:201-205 for multi-chunk; Nmp2Strategy.Encode's `input.Length`
        // for the one-element case, which is the same sum with one term). Computed here rather than
        // trusted from the client, which could otherwise disagree with the artifact it travels with.
        long originalBytes = req.Content.Sum(c => (long)System.Text.Encoding.UTF8.GetByteCount(c));

        var carried = new CarriedMetadata(
            OriginalBytes: originalBytes,
            UpdatedAt: req.Entry.UpdatedAt,
            ContentPreview: req.Entry.Description,
            Attributes: normalizedAttributes,
            Keywords: req.Entry.Keywords,
            TermFrequencies: req.Entry.TermFrequencies,
            ChunkEntries: req.Entry.ChunkEntries?.Select(c => new ChunkEntry(
                c.ChunkIndex, c.ContentPreview, c.Keywords, c.TermFrequencies)).ToArray(),
            FileHashes: req.Entry.FileHashes,
            AgentKeywords: req.Entry.AgentKeywords);

        // Deliberately no try/catch here (round 2 review, Critical 2 part 2): both name-related
        // failure modes were already handled above, before this call, over a probe that changes
        // nothing about what actually happens on disk. Anything WriteCarriedCopy itself throws past
        // this point — a locked sidecar, a full disk, a corrupt index elsewhere in the scope — is a
        // server-side fault that happened AFTER the artifact may already have landed, so it must
        // propagate as an unhandled exception (a 500 via the global handler), not be swallowed into
        // a 400 that blames the caller's destination name for something it did not cause.
        bool ok = local.WriteCarriedCopy(decoded, artifact, carried, req.Overwrite, out string msg);

        if (!ok)
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse(msg)));
        return Task.FromResult<IResult>(Results.Ok(new { message = msg }));
    }

    /// <summary>
    /// Bounds for the metadata a copy-from-client request carries. This route is the ONLY wire
    /// entry point for these six fields — <see cref="StoreRequest"/> doesn't carry them and
    /// <c>PatchEntry</c> refuses them — so nothing bounded them before now; an unbounded request
    /// lands directly in <c>CachedIndex</c> for the process lifetime and every search scores
    /// against it.
    ///
    /// <para><b>Round 2 review correction:</b> round 1 bounded every field to the same numbers
    /// <c>TextAnalysis.ComputeChunkEntries</c>'s PER-CHUNK derivation produces (500/25/100),
    /// including the TOP-LEVEL fields — which are governed by a DIFFERENT derivation
    /// (<c>MemoryDerivation.ForStore</c>) with different, sometimes higher or absent, ceilings. A
    /// wire bound tighter than what a local <c>remember()</c> can legitimately produce recreates the
    /// exact "written locally, unpromotable remotely" defect this branch exists to remove, so the
    /// two field groups now have separate constants — see each constant's own doc comment (above,
    /// beside <c>MaxNameLength</c>) for which derivation and which number governs it.</para>
    ///
    /// <para><c>FileHashes</c> is unchanged from round 1: bounded by
    /// <see cref="KnownAttributes.MaxValuesPerKey"/> (256), 1:1 with the <c>file</c> attribute
    /// values that bound already governs, so a request cannot carry more hashes than a local write
    /// could ever have <c>file</c> values to hash — this one genuinely is a parity guard already.</para>
    ///
    /// <para><c>ChunkEntries</c> is also unchanged: bounded by <paramref name="contentLength"/>
    /// itself — a real derivation (<see cref="TextAnalysis.ComputeChunkEntries"/>) never produces
    /// more entries than chunks — and each <c>ChunkIndex</c> must address one of them (1-based).
    /// This is also Important 4's fix: an unvalidated, client-chosen <c>ChunkIndex</c> reaches
    /// <c>WeightedAttributeScorer</c>'s search-hit output at a fabricated position with a
    /// <c>totalChunks</c> that disagrees with it — the reachable form of the header-mismatch threat
    /// Step 4a of the original brief ruled unreachable: the artifact's own header can't disagree with
    /// its chunks, but this array is caller-authored data, not something this endpoint derives.
    /// Duplicate indices are not rejected — only the count and the range are, matching what a real
    /// derivation's shape guarantees, not everything it happens to never produce.</para>
    /// </summary>
    private static string? CarriedEntryBoundsError(CarriedEntry entry, int contentLength)
    {
        if (entry.Description is { Length: > MaxCarriedDescriptionLength })
            return $"description must not exceed {MaxCarriedDescriptionLength} characters.";
        if (entry.Keywords is { Length: > MaxCarriedKeywords })
            return $"keywords may not exceed {MaxCarriedKeywords} entries.";
        if (entry.AgentKeywords is { Length: > MaxCarriedKeywords })
            return $"agentKeywords may not exceed {MaxCarriedKeywords} entries.";
        if (entry.TermFrequencies is { Count: > MaxCarriedTermFrequencyTerms })
            return $"termFrequencies may not exceed {MaxCarriedTermFrequencyTerms} terms.";
        if (entry.FileHashes is { Count: > KnownAttributes.MaxValuesPerKey })
            return $"fileHashes may not exceed {KnownAttributes.MaxValuesPerKey} entries.";

        if (entry.ChunkEntries is { } chunkEntries)
        {
            if (chunkEntries.Length > contentLength)
                return $"chunkEntries may not carry more entries ({chunkEntries.Length}) than content has chunks ({contentLength}).";

            for (int i = 0; i < chunkEntries.Length; i++)
            {
                var c = chunkEntries[i];
                if (c.ChunkIndex < 1 || c.ChunkIndex > contentLength)
                    return $"chunkEntries[{i}].chunkIndex {c.ChunkIndex} is out of range [1, {contentLength}].";
                if (c.ContentPreview is { Length: > MaxCarriedChunkPreviewLength })
                    return $"chunkEntries[{i}].contentPreview must not exceed {MaxCarriedChunkPreviewLength} characters.";
                if (c.Keywords is { Length: > MaxCarriedChunkKeywords })
                    return $"chunkEntries[{i}].keywords may not exceed {MaxCarriedChunkKeywords} entries.";
                if (c.TermFrequencies is { Count: > TextAnalysis.DefaultMaxTfTerms })
                    return $"chunkEntries[{i}].termFrequencies may not exceed {TextAnalysis.DefaultMaxTfTerms} terms.";
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a caller-supplied memory name to its <c>(scope, subject)</c> pair, or produces the
    /// 400 that says why it cannot be.
    ///
    /// <para><b>Why every handler that resolves a name needs this.</b> Task 13 routed
    /// <see cref="IMemoryStore.ParseQualifiedName"/>'s multi-segment branch through
    /// <c>MemoryNaming.BuildScopedTopicScope</c>, which REFUSES a topic segment that substitution
    /// cannot repair — a trailing dot or space, a bare <c>"."</c>, <c>NUL</c>, a segment that
    /// sanitizes to nothing. Before that, the scope was interpolated from the raw segments and the
    /// method threw only for the removed <c>topic:subject</c> colon syntax and an empty name. So
    /// <c>/notes./x</c> is input this server ACCEPTED and stored one commit ago, and an unguarded
    /// <see cref="ArgumentException"/> turns it into a 500 — a server-fault status for a caller
    /// error, on routes an untrusted client reaches. <see cref="CopyFromClient"/> answered 400 for
    /// the same input because it alone had the guard; this makes the rest agree with it.</para>
    /// </summary>
    private static bool TryResolveName(
        IMemoryStore store, string name, out string scope, out string subject, out IResult? refusal)
    {
        try
        {
            (scope, subject) = store.ParseQualifiedName(name);
            refusal = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            scope = subject = "";
            refusal = Results.BadRequest(new ErrorResponse(CallerFacingMessage(ex)));
            return false;
        }
    }

    /// <summary>
    /// The 400 a caller-supplied name earns, or null when it resolves — for the handlers that hand
    /// the name to a pipeline/store member which resolves it INTERNALLY and therefore has no
    /// <c>(scope, subject)</c> to give back here.
    ///
    /// <para><b>Probed in isolation, deliberately.</b> The alternative — wrapping the whole
    /// <c>StoreAsync</c>/<c>AppendAsync</c>/<c>CopyMemory</c> call in
    /// <c>catch (ArgumentException)</c> — is the misattribution defect <see cref="CopyFromClient"/>
    /// documents at length: those calls write, and an <see cref="ArgumentException"/> raised after
    /// the artifact has landed would 400 with "malformed name" over a write that half-happened.
    /// Running the resolver alone, before any side effect, keeps the refusal attributable to
    /// exactly the thing it names. <see cref="IMemoryStore.ParseQualifiedName"/> is pure, so the
    /// second call the operation itself makes costs nothing but string work.</para>
    ///
    /// <para>An ephemeral <c>~name</c> is skipped because every operation behind this check tests
    /// <see cref="IMemoryStore.IsEphemeral"/> FIRST and routes it without parsing
    /// (<c>MemoryOrchestrator.Resolve</c>, <c>MemoryOrchestrator.ForgetAsync</c>,
    /// <c>FileMemoryStore.CopyMemory</c>) — probing one here would refuse a name the operation
    /// accepts. It is skipped rather than merely harmless: <c>ParseQualifiedName("~x")</c> happens
    /// not to throw today, but this check must not depend on that.</para>
    ///
    /// <para>A <c>/temp/</c>-prefixed name is a DIFFERENT case and is refused outright, ahead of
    /// that skip: it is the ephemeral alias, a surface form resolved only at the MCP facade and
    /// the CLI. It is well-formed and parses without throwing — <c>IsEphemeral</c> is also false
    /// for it, since the store's own ephemeral form is <c>~name</c> — so left unchecked it would
    /// resolve to the persistent scope <c>local:temp</c> and silently store permanent bytes under
    /// a name that means something else at every surface above the wire.</para>
    /// </summary>
    private static IResult? NameRefusal(IMemoryStore store, string name)
    {
        if (MemoryNaming.IsEphemeralAlias(name))
            return Results.BadRequest(new ErrorResponse(
                $"'{name}' names a client-side ephemeral memory and cannot be stored on the "
                + "server. Use '~name', or a persistent path."));
        if (store.IsEphemeral(name))
            return null;
        return TryResolveName(store, name, out _, out _, out IResult? refusal) ? null : refusal;
    }

    /// <summary>Strips <see cref="ArgumentException"/>'s auto-appended <c>" (Parameter 'x')"</c>
    /// suffix before a message reaches a caller — the throwing method's internal C# parameter name
    /// is an implementation detail, not part of a caller-facing refusal.</summary>
    private static string CallerFacingMessage(ArgumentException ex) =>
        ex.ParamName is { } p && ex.Message.EndsWith($" (Parameter '{p}')", StringComparison.Ordinal)
            ? ex.Message[..^$" (Parameter '{p}')".Length]
            : ex.Message;

    private static async Task<IResult> GetChunk(
        string store,
        string name,
        int i,
        RequestContext ctx,
        CancellationToken ct)
    {
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
        // Same reasoning as RecallMemory's: ResolveArtifactAsync accepts an inline artifact and a
        // file:// URI ahead of ParseQualifiedName, so the guard belongs around the call and not in
        // a pre-probe that would have to duplicate that dispatch. The chunk decode below is outside
        // this try — DecodeChunk's ArgumentOutOfRangeException is a server-side index error, not a
        // name refusal, and the range check above already makes it unreachable.
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new ErrorResponse(CallerFacingMessage(ex)));
        }

        int totalChunks = Nmp2ChunkedEncoder.GetChunkCount(artifact);
        if (i < 1 || i > totalChunks)
            return Results.BadRequest(new ErrorResponse($"Chunk index {i} out of range [1, {totalChunks}]."));

        string content = Nmp2ChunkedEncoder.DecodeChunk(artifact, i);
        return Results.Ok(new ChunkResponse(content, i, totalChunks));
    }

    /// <summary>Task 9, D-2: <c>scopes</c>/<c>excludeTopics</c> are repeated query parameters
    /// (<c>?scopes=a&amp;scopes=b</c>), bound natively by ASP.NET Core's <c>string[]?</c> model
    /// binding — not the old single comma-joined value, which could never express a scope name
    /// containing a comma.</summary>
    private static Task<IResult> SearchMemory(
        string store,
        RequestContext ctx,
        PluginPipeline pipeline,
        CancellationToken ct,
        string? q = null,
        string[]? scopes = null,
        int limit = 20,
        string[]? excludeTopics = null,
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

    /// <summary>The one search implementation behind both GET and POST. GET carries the array-typed
    /// filters via repeated query params; POST additionally carries attributeFilter, which is a
    /// dictionary of arrays and does not belong in a query string.</summary>
    private static async Task<IResult> RunSearch(
        RequestContext ctx,
        PluginPipeline pipeline,
        string? q,
        string[]? scopes,
        int limit,
        string[]? excludeTopics,
        string? pathPrefix,
        Dictionary<string, string[]>? attributeFilter,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Results.BadRequest(new ErrorResponse("q parameter is required."));

        // Input bounds validation. Task 9, D-5: scopes/excludeTopics used to be a single string
        // capped at 1KB total; converted to arrays that becomes a count bound plus a per-element
        // length bound (ValidateStringArrayBounds) — dropping either would leave the total
        // unbounded again. 64/1024 mirrors the attributeFilter bound below (64 entries) and the
        // old single-string cap (1024 chars), now applied per element rather than to the whole
        // request.
        if (q.Length > 10_240)
            return Results.BadRequest(new ErrorResponse("Query too long (max 10KB)."));
        if (ValidateStringArrayBounds(scopes, 64, 1024, "scopes") is { } scopesBoundsError)
            return Results.BadRequest(new ErrorResponse(scopesBoundsError));
        if (ValidateStringArrayBounds(excludeTopics, 64, 1024, "excludeTopics") is { } excludeBoundsError)
            return Results.BadRequest(new ErrorResponse(excludeBoundsError));
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
        if (req.Topics is null || req.Topics.Length == 0)
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse("topics is required.")));

        if (ctx.Store is not ILocalMemoryStore localStore)
            return Task.FromResult<IResult>(Results.Json(
                new ErrorResponse("Bundle export requires a store backed by a local filesystem."),
                statusCode: StatusCodes.Status501NotImplemented));

        // Task 13 made this a refusing path too, and it is a THIRD name-resolution surface, not the
        // {name} route: BundleFormatService.ExportTopicsToZip builds each topic's scope through
        // MemoryNaming.BuildScopedTopicScope, which throws ArgumentException for a topic
        // substitution cannot repair. Pre-validated with the non-throwing form, mirroring
        // BundleFormatService's own import-side pre-pass, so an unusable topic is a 400 naming the
        // topic rather than a 500 raised while the response stream is already being built.
        foreach (string topic in req.Topics)
        {
            // Null-tolerant: `topics` is a wire array, so an element can arrive null however the
            // DTO declares it, and TryBuildScopedTopicScope takes a nullable topic for exactly
            // that reason.
            if (!MemoryNaming.TryBuildScopedTopicScope(topic?.Trim(), out _))
                return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse(
                    $"Topic '{topic}' is not a usable scope name.")));
        }

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
