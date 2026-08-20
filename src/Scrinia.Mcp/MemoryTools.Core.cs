using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Migration;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Mcp;

public sealed partial class ScriniaMcpTools
{
    /// <summary>Recall: unpack an NMP/2 artifact (or a memory resolved by name) back to its
    /// original text content. Backs the MCP <c>recall</c> tool and <c>scri show</c>.</summary>
    internal async Task<McpResponse> Recall(
        [Description("The NMP/2 artifact text, or a memory name to resolve. " +
                     "Use the exact name shown by list (e.g. 'session-notes', '/api/auth-flow', '~scratch').")] string artifactOrName,
        [Description("Optional 1-based chunk index to retrieve a specific chunk.")] int? chunk = null,
        string actionLabel = "shown",
        CancellationToken cancellationToken = default)
    {
        // ── Agent config: read .md file first, NMP/2 fallback ────────────
        if (artifactOrName.StartsWith("/agent/", StringComparison.OrdinalIgnoreCase))
        {
            var agentStore = MemoryStoreContext.Current;
            if (agentStore is not null)
            {
                string agentSubject = artifactOrName["/agent/".Length..].Trim();
                if (PathRouter.TryToFileEntityPath("agent", agentSubject, agentStore.WorkspaceRoot, out string agentFilePath)
                    && File.Exists(agentFilePath))
                {
                    string mdContent = await File.ReadAllTextAsync(agentFilePath, cancellationToken);
                    SessionBudget.RecordAccess(artifactOrName, mdContent.Length);
                    return ResponseBuilder.Success(mdContent).WithAction(actionLabel)
                        .WithTruncationAdvice(RecallTruncationAdvice);
                }
            }
            // Fall through to NMP/2 resolution for legacy entries
        }

        string artifact;

        // Fast path: inline NMP/2 artifacts and file:// URIs don't need a store
        var resolved = await TryResolveWithoutStore(artifactOrName, cancellationToken);
        if (resolved != null)
        {
            artifact = resolved;
        }
        else
        {
            // Store-based resolution (memory name, ephemeral, etc.)
            var store = MemoryStoreContext.Current;
            if (store is null)
                return ResponseBuilder.Error(
                    $"Memory '{artifactOrName}' not found.",
                    ErrorCodes.NotFound,
                    "list() to see available memories",
                    "search({ query: '...' }) to find by keyword");

            try
            {
                artifact = await store.ResolveArtifactAsync(artifactOrName, cancellationToken);
            }
            catch (FileNotFoundException)
            {
                return ResponseBuilder.Error(
                    $"Memory '{artifactOrName}' not found.",
                    ErrorCodes.NotFound,
                    "list() to see available memories",
                    "search({ query: '...' }) to find by keyword");
            }
        }

        if (!artifact.TrimStart().StartsWith("NMP/2 ", StringComparison.Ordinal))
            return ResponseBuilder.Error(
                "Only NMP/2 artifacts are supported by this tool.",
                ErrorCodes.InvalidParameter,
                "Provide a memory name or an artifact whose header begins with 'NMP/2 '.");

        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);

        if (chunk is not null)
        {
            string chunkContent = Nmp2ChunkedEncoder.DecodeChunk(artifact, chunk.Value);
            SessionBudget.RecordAccess(artifactOrName, chunkContent.Length);
            return ResponseBuilder.Success($"Chunk {chunk}/{chunkCount}\n\n{chunkContent}").WithAction(actionLabel)
                .WithTruncationAdvice(RecallTruncationAdvice);
        }

        byte[] bytes = Nmp2Strategy.Instance.Decode(artifact);
        string decoded = System.Text.Encoding.UTF8.GetString(bytes);
        SessionBudget.RecordAccess(artifactOrName, decoded.Length);

        if (chunkCount > 1)
            return ResponseBuilder.Success($"({chunkCount} chunks)\n\n{decoded}").WithAction(actionLabel)
                .WithTruncationAdvice(RecallTruncationAdvice);

        return ResponseBuilder.Success(decoded).WithAction(actionLabel)
            .WithTruncationAdvice(RecallTruncationAdvice);
    }

    // ── Persistent memory tools ───────────────────────────────────────────────

    /// <summary>Remember: compress text and persist it as a named artifact in a memory scope.
    /// Backs the MCP <c>remember</c> tool and <c>scri store</c>.</summary>
    internal async Task<McpResponse> Remember(
        [Description("The text content to compress and store. " +
                     "Pass a single element for a single-chunk artifact, or multiple elements to control " +
                     "chunk boundaries — each element becomes one independently retrievable chunk.")] string[] content,
        [Description("Human-readable name for this artifact (e.g. \"session-notes\", \"my-codebase\"). " +
                     "Invalid filename characters are replaced with '_'. " +
                     "Naming: '/path/to/memory' (e.g. '/api/auth-flow'), '/temp/subject' (ephemeral).")] string name,
        [Description("Optional description. If empty, the first 200 characters of content are used.")] string description = "",
        [Description("Optional tags for categorization.")] string[]? tags = null,
        [Description("Optional keywords for search. Merged with auto-extracted content terms; 'key:value' " +
                     "entries route into attributes. This Description is inert — internal helper, not an " +
                     "MCP tool — the guidance callers see is on the public 'remember' tool's keywords " +
                     "parameter in MemoryTools.cs.")] string[]? keywords = null,
        [Description("Optional retrieval anchors, TF-seeded into search. This Description is inert — " +
                     "internal helper, not an MCP tool. The public 'remember' tool has no dedicated " +
                     "anchors parameter; a caller reaches this through its attributes parameter " +
                     "(attributes: { anchor: [...] }) in MemoryTools.cs, which documents what an anchor " +
                     "is for.")]
        string[]? anchors = null,
        [Description("Optional ISO 8601 date after which this memory should be reviewed for staleness.")] string? reviewAfter = null,
        [Description("Optional free-text condition describing when this memory should be reviewed (e.g. 'when auth system changes').")] string? reviewWhen = null,
        IReadOnlyDictionary<string, string[]>? attributes = null,
        string actionLabel = "stored",
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // ── Agent config path (/agent/* → .scrinia/agent/{name}.md) ──
        if (name.StartsWith("/agent/", StringComparison.OrdinalIgnoreCase))
        {
            string agentSubject = name["/agent/".Length..].Trim();

            // Validated first, so /agent/ and /skill/ answer the same way for the same bad input:
            // a bad name is INVALID_PARAMETER on both, never whatever the next guard would have
            // said. The path check below stays as defence in depth.
            if (!FileEntityName.IsValid(agentSubject))
                return ResponseBuilder.Error(
                    "Invalid agent config name. Use letters, digits, '-', '_' or '.' only.",
                    ErrorCodes.InvalidParameter,
                    "Use a path like '/agent/profile' for the agent config.");

            // The /agent/ branch writes a .md file plus a JSON sidecar into the workspace. It used
            // to be guarded here, at the point of use rather than at the method entry, so that a
            // remote store refused only THIS path and still served every other remember(). With one
            // store the guard could no longer fail, so it is gone and the branch reads `store`
            // directly.
            string agentDir = PathRouter.ToFileEntityDir("agent", store.WorkspaceRoot);
            if (!PathRouter.TryToFileEntityPath("agent", agentSubject, store.WorkspaceRoot, out string filePath))
                return ResponseBuilder.Error(
                    "Invalid agent config name. Use letters, digits, '-', '_' or '.' only.", ErrorCodes.InvalidParameter,
                    "Use a path like '/agent/profile' for the agent config.");
            FileMemoryStore.EnsureGitIgnore(store.WorkspaceRoot);
            Directory.CreateDirectory(agentDir);

            ArchiveFileVersion(filePath, Path.Combine(agentDir, "versions"));

            string agentContent = string.Join("\n", content);
            await File.WriteAllTextAsync(filePath, agentContent, cancellationToken);

            string now = DateTimeOffset.UtcNow.ToString("o");
            var existingMeta = ReadSidecarMeta(filePath, ScriniaMcpJsonContext.Default.AgentFileMeta);
            var meta = new AgentFileMeta(
                CreatedAt: existingMeta?.CreatedAt ?? now,
                UpdatedAt: now);
            WriteSidecarMeta(filePath, meta, ScriniaMcpJsonContext.Default.AgentFileMeta);

            long agentBytes = System.Text.Encoding.UTF8.GetByteCount(agentContent);
            return ResponseBuilder.Success($"Remembered: agent:{agentSubject} ({FormatBytes(agentBytes)}).")
                .WithFileChanges().WithAction(actionLabel);
        }

        // ── Every other path: the STORE derives, this tool does not ──────
        //
        // Everything that used to stand here — AnalyzeText, the keyword merge and its TF boosts,
        // the anchor seeding, the attribute assembly, the chunk entries, the file hashing, the
        // auto-derived review condition, the carrier fold and the two hand-built entries — was one
        // of four copies of the same contract, and they had drifted. The one implementation is
        // MemoryDerivation, reached through IMemoryStore.StoreAsync; the rulings each behaviour
        // rests on are in docs/superpowers/analysis/2026-08-11-derivation-inventory.md.
        //
        // The ephemeral branch is gone with it: the store routes a ~name to ephemeral storage
        // itself, so there is no second write path here to keep in step with the first.
        var (ephemeral, scope, subject) = Resolve(store, name);

        var entry = await store.StoreAsync(
            // The `keywords` argument is the CALLER's raw list (D-01). Nothing derived is ever
            // handed back in as input — which is why this method now computes nothing to hand it.
            new MemoryWrite(subject, scope, content, description,
                WriteAttributes(attributes, tags, anchors, reviewAfter, reviewWhen), keywords),
            cancellationToken);

        string qualifiedName = QualifiedName(store, entry, ephemeral, scope);

        // Fire event sink (embeddings, etc.) — fire-and-forget so embedding latency
        // never blocks the response. Failure logged to stderr; the artifact is already on disk.
        // CancellationToken.None: the sink runs detached on a background Task —
        // propagating the per-request CT would abort it the moment the user-facing
        // response returns, leaving importance unscored or vectors half-written.
        // The sink re-parses this name to decide which scope to embed under; under v3
        // FormatQualifiedName is round-trip-safe for every scope, so it is the same value the
        // response displays.
        FireEventSinkAsync(sink => sink.OnStoredAsync(qualifiedName, content, store, CancellationToken.None));

        return StoredResponse(entry, qualifiedName, ephemeral, actionLabel);
    }

    /// <summary>Splits a user-facing name into the resolved <c>(scope, subject)</c> pair every
    /// store member takes. An ephemeral <c>~name</c> is its own whole address and keeps its prefix
    /// — the store routes it to ephemeral storage itself. Mirrors
    /// <c>MemoryOrchestrator.Resolve</c>, deliberately: the two tool surfaces over the same store
    /// must resolve a name the same way.</summary>
    private static (bool Ephemeral, string Scope, string Subject) Resolve(IMemoryStore store, string name)
    {
        if (store.IsEphemeral(name))
            return (true, "", name);
        var (scope, subject) = store.ParseQualifiedName(name);
        return (false, scope, subject);
    }

    /// <summary>The address a written entry is reported and embedded under. Reads the name off the
    /// ENTRY, not off the caller's input, so a write that landed on a memory stored under a
    /// different casing reports the memory's own name.</summary>
    private static string QualifiedName(
        IMemoryStore store, ArtifactEntry entry, bool ephemeral, string scope) =>
        ephemeral ? $"~{entry.Name}" : store.FormatQualifiedName(scope, entry.Name);

    /// <summary>The response shape a whole-memory write answers with, shared by <c>remember</c> and
    /// by the create-via-append case so the two cannot drift. An ephemeral write reports no file
    /// changes because it made none.</summary>
    private static McpResponse StoredResponse(
        ArtifactEntry entry, string qualifiedName, bool ephemeral, string actionLabel)
    {
        string counts = $"{entry.ChunkCount} {(entry.ChunkCount == 1 ? "chunk" : "chunks")}, "
                      + FormatBytes(entry.OriginalBytes);
        return ephemeral
            ? ResponseBuilder.Success($"Remembered: {qualifiedName} ({counts}) [ephemeral]")
                .WithAction(actionLabel)
            : ResponseBuilder.Success($"Remembered: {qualifiedName} ({counts}).")
                .WithFileChanges().WithAction(actionLabel);
    }

    /// <summary>
    /// Folds the tool surface's separate carrier parameters — tags, anchors and the two review
    /// halves — into the one flat attribute map <see cref="MemoryWrite"/> carries.
    ///
    /// <para>Input mapping, not derivation. Under the single pipeline <c>tag</c>, <c>anchor</c> and
    /// <c>review</c> are ordinary attribute keys (D-03, D-07, D-08): there is no separate carrier
    /// to re-fold after the fact, and no second seeding pass. The review halves travel as the
    /// caller wrote them — the store canonicalizes a bare date into ISO-8601 round-trip form
    /// (D-06), which is precisely the step that must not be duplicated out here.</para>
    ///
    /// <para>The carrier folds in LAST so an explicit <c>tags:</c>/<c>anchors:</c>/<c>reviewAfter:</c>
    /// argument wins a collision with the same key in <paramref name="attributes"/>, which is the
    /// order the pre-collapse write used.</para>
    /// </summary>
    private static Dictionary<string, string[]>? WriteAttributes(
        IReadOnlyDictionary<string, string[]>? attributes,
        string[]? tags, string[]? anchors, string? reviewAfter = null, string? reviewWhen = null)
    {
        var carrier = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (tags is { Length: > 0 })
            carrier[KnownAttributes.TagKey] = tags;
        if (anchors is { Length: > 0 })
            carrier[KnownAttributes.AnchorKey] = anchors;
        string[] review = new[] { reviewAfter, reviewWhen }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToArray();
        if (review.Length > 0)
            carrier[KnownAttributes.ReviewKey] = review;

        // Normalize handles the blank-value and blank-key dropping both halves used to do by hand;
        // the store normalizes again on the way in, so this is only about getting the two maps into
        // one comparable shape before the merge.
        var explicitAttributes = KnownAttributes.Normalize(attributes);
        return carrier.Count == 0
            ? explicitAttributes
            : KnownAttributes.Merge(explicitAttributes, KnownAttributes.Normalize(carrier));
    }

    /// <summary>Update metadata on an existing memory without re-encoding its content.
    ///
    /// <para><paramref name="attributes"/> is the DIRECT road to <c>Attributes</c>;
    /// <paramref name="keywords"/> is the indirect one, where a <c>key:value</c>-shaped element is
    /// routed by <see cref="AttributeExtractor.RouteKeywords"/>. Both are supported and they merge
    /// together, but a caller that knows it wants an attribute should say so: routing is
    /// CONDITIONAL — past <see cref="AttributeExtractor.MaxValueLength"/>, or for a value starting
    /// <c>//</c>, the element is NOT routed and stays a plain keyword.</para>
    ///
    /// <para><b>The bound a caller must respect</b>, because nothing here enforces it: whatever
    /// arrives as a plain keyword is unioned into <c>Keywords</c> UNCAPPED, and
    /// <see cref="MemoryDerivation.ForMetadataUpdate"/> unions the same terms into
    /// <c>AgentKeywords</c> uncapped. Keep the entry's total at or under
    /// <see cref="TextAnalysis.DefaultMaxMergedKeywords"/> (30), which is where every other write
    /// path caps. Nothing FAILS past it any more — the bound that used to be enforced was the
    /// promotion wire's, and it went with the server — so an over-long list now just grows,
    /// diluting the entry's own term weights with no error to notice. See the comment on the union
    /// itself in <see cref="ApplyMetadataMutation"/>.</para>
    /// </summary>
    internal Task<McpResponse> UpdateMeta(
        [Description("Memory name (e.g. '/api/auth-flow', 'session-notes').")] string name,
        [Description("Keywords to add (merged with existing, not replaced).")] string[]? keywords = null,
        [Description("New description (replaces existing if provided).")] string? description = null,
        [Description("ISO 8601 date for review (replaces existing if provided).")] string? reviewAfter = null,
        [Description("Condition for review (replaces existing if provided).")] string? reviewWhen = null,
        [Description("Attributes to merge (multi-valued keys union; scalar keys are replaced).")]
        IReadOnlyDictionary<string, string[]>? attributes = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Track what changed for the response message
        var changes = new List<string>();
        bool found = false;
        bool persisted;
        string qualifiedName;
        bool ephemeral = store.IsEphemeral(name);

        // The ephemeral branch sits AHEAD of ParseQualifiedName, where the refusal it replaces sat:
        // ParseQualifiedName does not strip `~`, so a ~name reaching it resolves to a persistent
        // address that is not this memory. An ephemeral lives in this process — never on disk — so
        // three members serve it: GetEphemeral, the projection pair on EphemeralEntry, and
        // RememberEphemeral.
        if (ephemeral)
        {
            string key = MemoryNaming.StripEphemeralPrefix(name);
            qualifiedName = $"~{key}";

            // TryUpdateEphemeral, not GetEphemeral → mutate → RememberEphemeral. That shape is the
            // racy read-modify-write the persistent branch below moved OFF of, and reintroducing it
            // here would be worse than where it started: the value a lost update drops is the `ref`
            // attribute, i.e. it manufactures the dangling one-sided ref this whole tool's precheck
            // exists to prevent.
            persisted = store.TryUpdateEphemeral(key, existing =>
            {
                found = true;

                // Reset, because a CAS retry re-runs this against a NEWER entry — see
                // IMemoryStore.TryUpdateEphemeral. The message must describe the mutation that
                // actually landed, not the union of every attempt.
                changes.Clear();

                var current = existing.ToArtifactEntry();
                var mutated = ApplyMetadataMutation(
                    current, keywords, description, reviewAfter, reviewWhen, attributes, changes);

                // Null is "nothing to change" — reported by the shared changes.Count == 0 warning
                // below, exactly as the persistent path reports it.
                if (mutated is null) return null;

                // ForMetadataUpdate is the store's rule, and this callback runs INSIDE the store
                // member that owns the swap — the same position FileMemoryStore.TryUpdateEntry
                // applies it from. Written back as `existing with { … }`, never a fresh
                // EphemeralEntry: ArtifactEntry has no Artifact, so a rebuild would report a
                // successful update and empty the memory.
                return existing.WithMetadataFrom(MemoryDerivation.ForMetadataUpdate(current, mutated));
            });
        }
        else
        {
            var (scope, subject) = store.ParseQualifiedName(name);
            qualifiedName = store.FormatQualifiedName(scope, subject);

            // Routed through TryUpdateEntry rather than the LoadIndex → mutate → Upsert shape this
            // used to have: it runs under FileMemoryStore's per-scope write lock, so
            // load→mutate→persist is atomic instead of a racy read-modify-write. (The shape it
            // replaced was also how link() came to report "Linked 'a' <-> 'b'" while the `ref`
            // attribute reached nothing — on the remote store Upsert was a deliberate no-op, so the
            // mutated copy was handed to a method that discards it.) `found`/`changes` are captured out of the
            // callback because a false return has three distinct causes the caller must tell apart:
            // no such entry, nothing to change, and "found but not persistable".
            //
            // `found` stays HERE rather than moving into ApplyMetadataMutation: it means "the store
            // handed us an entry", a fact about TryUpdateEntry, not about the mutation.
            persisted = store.TryUpdateEntry(subject, scope, entry =>
            {
                found = true;
                return ApplyMetadataMutation(
                    entry, keywords, description, reviewAfter, reviewWhen, attributes, changes);
            });
        }

        if (!found)
            return Task.FromResult(ResponseBuilder.Error(
                $"Memory '{qualifiedName}' not found.",
                ErrorCodes.NotFound,
                "list() to see available memories",
                "remember({ path: '...', content: [...] }) to create it"));

        if (changes.Count == 0)
            return Task.FromResult(ResponseBuilder.Warning("No changes specified. Provide at least one of: keywords, attributes, description, reviewAfter, reviewWhen."));

        // Found, had something to change, and the store still would not persist it. Reporting
        // success here is the defect this branch exists to close, so it says so plainly instead.
        // It was reachable when a metadata-only write could be refused for touching keywords, which
        // are derived from content a metadata endpoint does not carry; against a filesystem store
        // it is a genuine fault rather than a contract. Unreachable for an ephemeral: the write is
        // a dictionary assignment in this process, and the only other way out of that branch is the
        // null mutation the warning above already answered.
        if (!persisted)
            return Task.FromResult(ResponseBuilder.Error(
                $"Metadata for '{qualifiedName}' was not updated: the active store refused a " +
                "metadata-only change. Changing keywords is the usual cause — they are derived " +
                "from the memory's content, so they can only change through a write that carries it.",
                ErrorCodes.NotSupported,
                "remember({ path: '...', content: [...] }) — a full write carries attributes, keywords and description together"));

        // An ephemeral write touches no file, so it reports no file changes.
        var success = ResponseBuilder.Success($"Updated metadata for '{qualifiedName}': {string.Join(", ", changes)}.");
        return Task.FromResult(
            (ephemeral ? success : success.WithFileChanges()).WithAction("updated"));
    }

    /// <summary>
    /// The metadata mutation itself: takes the entry the store handed over, folds
    /// <paramref name="keywords"/>/<paramref name="description"/>/<paramref name="reviewAfter"/>/
    /// <paramref name="reviewWhen"/>/<paramref name="attributes"/> into it, and returns the entry to
    /// persist — or <c>null</c> for "no write", which is what
    /// <see cref="IMemoryStore.TryUpdateEntry"/> takes null to mean.
    ///
    /// <para><paramref name="attributes"/> and the attribute-shaped half of
    /// <paramref name="keywords"/> are folded into ONE incoming map before it meets the entry, so
    /// the <c>N attribute(s) merged</c> count stays a count of KEYS actually merged when both are
    /// supplied and naming the same key twice reports one, not two.</para>
    ///
    /// <para><paramref name="changes"/> is an accumulator, appended to in place, and it is how the
    /// caller renders the response. <b>It is the caller's job to make sure it describes one
    /// mutation.</b> On the persistent path that is free — <c>TryUpdateEntry</c> invokes its
    /// mutation exactly once in both implementations
    /// (<see cref="IMemoryStore.TryUpdateEntry"/>'s default and <c>FileMemoryStore</c>'s locked
    /// override). On the ephemeral path it
    /// is not: <see cref="IMemoryStore.TryUpdateEphemeral"/> is a compare-and-swap that re-runs its
    /// mutation against the newer entry when a concurrent writer wins the swap, so
    /// <c>UpdateMeta</c>'s ephemeral callback clears the list on entry.</para>
    ///
    /// <para>Extracted from the callback it used to be so that the ephemeral and persistent paths
    /// share one mutation rather than two that agree today. Deliberately static, and deliberately
    /// not given <c>found</c>: whether the store had an entry to hand over is a fact about the
    /// lookup, not about the mutation.</para>
    /// </summary>
    private static ArtifactEntry? ApplyMetadataMutation(
        ArtifactEntry entry,
        string[]? keywords,
        string? description,
        string? reviewAfter,
        string? reviewWhen,
        IReadOnlyDictionary<string, string[]>? attributes,
        List<string> changes)
    {
        // Route key:value-shaped additions into Attributes; only plain ones merge into Keywords.
        var (routedAttributes, plainAdds) = AttributeExtractor.RouteKeywords(keywords);

        // The two roads join here. Normalize is what every other write path canonicalizes an
        // agent-supplied map with, and it applies exactly what RouteKeywords applies to the value it
        // keeps — lowercase the key, trim the value, drop blanks — so a direct `{ref: ["x"]}` and a
        // routed `"ref:x"` produce the same stored value rather than two spellings of one reference.
        // Merged with Merge, not by hand: the union/replace rule per key class is that method's, and
        // a second copy of it here is how a direct write and a routed one start disagreeing.
        var incomingAttributes = KnownAttributes.Merge(
            routedAttributes, KnownAttributes.Normalize(attributes));

        // NO CAP HERE, BY RULING — and this is the constraint a new caller has to respect, because
        // nothing downstream will enforce it. Every OTHER write path merges through
        // TextAnalysis.MergeKeywordsWithSource, which caps at TextAnalysis.DefaultMaxMergedKeywords
        // (30); this union and ForMetadataUpdate's AgentKeywords union downstream of it are the only
        // two that do not.
        //
        // Until the server was dropped, exceeding that count had a VISIBLE consequence: the entry
        // became unpromotable, because the promotion wire refused more than the same constant and
        // answered 400. That failure is gone, and its absence is the reason to state the bound here
        // rather than assume something catches it — an over-long list now grows silently, diluting
        // the entry's own term weights against every other entry's. link() used to be the one
        // production caller that got here, by minting a ref:-shaped keyword that degraded past
        // AttributeExtractor.MaxValueLength; it writes the attribute directly now, so today the
        // only callers are tests.
        string[]? mergedKeywords = entry.Keywords;
        if (plainAdds is { Length: > 0 })
        {
            var existing = entry.Keywords ?? [];
            mergedKeywords = existing.Union(plainAdds, StringComparer.OrdinalIgnoreCase).ToArray();
            int addedCount = mergedKeywords.Length - existing.Length;
            if (addedCount > 0) changes.Add($"{addedCount} keyword(s) added");
        }

        Dictionary<string, string[]>? mergedAttributes = entry.Attributes;
        if (incomingAttributes is not null)
        {
            mergedAttributes = KnownAttributes.Merge(entry.Attributes, incomingAttributes);
            changes.Add($"{incomingAttributes.Count} attribute(s) merged");
        }

        // Replace description if provided — displayed via ContentPreview (updatedContentPreview
        // below); this block only tracks the "changes" message.
        if (!string.IsNullOrWhiteSpace(description))
        {
            changes.Add("description updated");
        }

        // Carry-forward defaults for review sourced from Attributes["review"] (the successor
        // carrier) rather than the legacy ReviewAfter/ReviewWhen members.
        AttributeReaders.TryReadReview(entry.Attributes, out var existingReviewDate, out var existingReviewWhen);

        // Parse reviewAfter if provided — through the store's own rule (invariant culture, a
        // bare date is UTC), not a bare DateTimeOffset.TryParse. This is a metadata-only write,
        // so it reaches neither derivation entry point and had kept its own parse: the same
        // "2026-08-01" persisted as UTC through remember() and as the writer's local zone
        // through UpdateMeta (no caller outside link() and the tests), which is D-06's defect
        // surviving one caller past the ruling that removed it everywhere else.
        DateTimeOffset? parsedReviewAfter = existingReviewDate;
        if (!string.IsNullOrWhiteSpace(reviewAfter))
        {
            if (MemoryDerivation.TryParseReviewDate(reviewAfter, out var ra))
            {
                parsedReviewAfter = ra;
                changes.Add($"reviewAfter set to {ra:yyyy-MM-dd}");
            }
            else
            {
                changes.Add("reviewAfter ignored (invalid date)");
            }
        }

        // Replace reviewWhen if provided
        string? updatedReviewWhen = existingReviewWhen;
        if (reviewWhen is not null)
        {
            updatedReviewWhen = string.IsNullOrWhiteSpace(reviewWhen) ? null : reviewWhen;
            changes.Add(updatedReviewWhen is not null ? "reviewWhen updated" : "reviewWhen cleared");
        }

        // Null means "no write"; the caller reports it as its "no changes specified" warning.
        if (changes.Count == 0) return null;

        // Attributes-native direct write of the review state, paired with the Forward call at the
        // end of this method. (This comment used to say "dual-write with the legacy
        // ReviewAfter/ReviewWhen members below" — false twice over: ArtifactEntry has carried no
        // such members since the Attributes cutover, and there is nothing below.)
        // ADD/REPLACE ONLY — no Remove(ReviewKey) — matches AttributeMigration.Forward's
        // incoming-only fold: when neither reviewAfter nor reviewWhen is present, Forward's
        // `incoming` carries no "review" key, so KnownAttributes.Merge PRESERVES any existing
        // Attributes["review"] rather than clearing it. KnownAttributes.Merge always returns a NEW
        // dictionary, so this never mutates entry.Attributes (== mergedAttributes when
        // incomingAttributes is null) in place.
        var reviewParts = new List<string>(2);
        if (parsedReviewAfter is { } ra2) reviewParts.Add(ra2.ToString("o", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(updatedReviewWhen)) reviewParts.Add(updatedReviewWhen);
        if (reviewParts.Count > 0)
            mergedAttributes = KnownAttributes.Merge(mergedAttributes,
                new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [KnownAttributes.ReviewKey] = reviewParts.ToArray() });

        // ContentPreview direct write — Merge 3 Task 2b option A: an explicit new description
        // wins and becomes the displayed ContentPreview; otherwise the existing ContentPreview
        // is preserved (superseding the old Merge-2 null-only backfill).
        string? updatedContentPreview = !string.IsNullOrWhiteSpace(description)
            ? description
            : entry.ContentPreview;

        // TermFrequencies is deliberately NOT set here. This block used to compute the BM25
        // boost for the added keywords and attributes itself — caller-side derivation, at a
        // weight (+5) that disagreed with the store path's (+3, D-08), seeding raw keyword
        // strings rather than index tokens (against D-14), and reachable only through UpdateMeta
        // — no caller outside link() and the tests. (It could not persist over HTTP at all, so
        // this write reported full success while discarding the boost, and a remote corpus ranked
        // differently from a local one forever.) MemoryDerivation.ForMetadataUpdate re-derives it —
        // inside TryUpdateEntry on the persistent path, and at UpdateMeta's ephemeral branch, which
        // IS the store for a ~name.
        var updated = entry with
        {
            Keywords = mergedKeywords,
            Attributes = mergedAttributes,
            ContentPreview = updatedContentPreview,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return AttributeMigration.Forward(updated);
    }

    /// <summary>Returns a summary or full listing of persisted memories.</summary>
    internal Task<McpResponse> List(
        [Description("Optional scope order, e.g. ['local', 'api', 'ephemeral']. " +
                     "Topic names filter to local topics (e.g. 'api' shows api topic entries). " +
                     "An empty array behaves the same as omitting it.")] string[]? scopes = null,
        [Description("'summary' (default), 'full' for paginated table, 'drift' for code reference drift check.")] string mode = "summary",
        [Description("Starting index for full mode (0-based). Ignored in summary mode.")] int offset = 0,
        [Description("Maximum entries to return in full mode (default 50). Ignored in summary mode.")] int limit = 50,
        [Description("Optional topic names to exclude from results. " +
                     "Use ['plan', 'task', 'project', 'learn'] to hide planning namespaces from knowledge listings. " +
                     "An empty array behaves the same as omitting it.")] string[]? excludeTopics = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // A caller can send "mode": null over JSON-RPC, distinct from omitting the field.
        // Pre-task behaviour treated null and empty identically to the "summary" default, and
        // facts() normalises its op the same way ((op ?? string.Empty).Trim()) -- match that so
        // list() does not newly disagree with the sibling it is meant to converge on.
        if (string.IsNullOrWhiteSpace(mode))
            mode = "summary";

        if (!string.Equals(mode, "summary", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "drift", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ResponseBuilder.Error(
                $"unknown mode '{mode}'. Use 'summary', 'full', or 'drift'.",
                ErrorCodes.InvalidParameter,
                "list({ mode: 'full' })"));
        }

        // Drift detection re-hashes files on disk. This used to be the one mode a remote store
        // refused, guarded here rather than at the method entry so every other mode still listed.
        if (string.Equals(mode, "drift", StringComparison.OrdinalIgnoreCase))
            return BuildDriftList(store);

        List<ScopedArtifact> entries = store.ListScoped(scopes, excludeTopics);
        if (entries.Count == 0)
            return Task.FromResult(ResponseBuilder.Success("No memories stored.").WithAction("listed")
                .WithTruncationAdvice(ListTruncationAdvice));

        entries.Sort((a, b) => b.Entry.CreatedAt.CompareTo(a.Entry.CreatedAt));

        if (!string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ResponseBuilder.Success(BuildSummary(entries, store)).WithAction("listed")
                .WithTruncationAdvice(ListTruncationAdvice));

        return Task.FromResult(ResponseBuilder.Success(BuildFullList(entries, store, offset, limit)).WithAction("listed")
            .WithTruncationAdvice(ListTruncationAdvice));
    }

    internal static string BuildSummary(List<ScopedArtifact> entries, IMemoryStore store)
    {
        long totalBytes = entries.Sum(e => e.Entry.OriginalBytes);
        int totalTokens = (int)(totalBytes / 4);
        var now = DateTimeOffset.UtcNow;
        int staleCount = entries.Count(e =>
            AttributeReaders.GetReviewStatus(e.Entry.Attributes, now).IsStale);
        int reviewCount = entries.Count(e =>
            AttributeReaders.GetReviewStatus(e.Entry.Attributes, now).NeedsReview);
        int ephemeralCount = entries.Count(e => e.Scope == "ephemeral");

        // Group by scope
        var grouped = entries
            .Where(e => e.Scope != "ephemeral")
            .GroupBy(e => MemoryNaming.FormatScopeLabel(e.Scope))
            .OrderBy(g => g.Key)
            .ToList();

        int topicCount = grouped.Count(g => g.Key != "local");

        // Every terminator below is an explicit '\n', not AppendLine: AppendLine emits \r\n on
        // Windows, but ToYaml later splits this builder's return value on '\n' only — a \r\n
        // terminator here leaves a stray \r on every rendered line.
        var sb = new System.Text.StringBuilder();
        sb.Append($"## Memory Summary").Append('\n');
        sb.Append($"**{entries.Count} memories** — {FormatBytes(totalBytes)} (~{totalTokens:N0} tokens)").Append('\n');
        if (topicCount > 0 || ephemeralCount > 0 || staleCount > 0 || reviewCount > 0)
        {
            var parts = new List<string>();
            if (topicCount > 0) parts.Add($"{topicCount} topic{(topicCount == 1 ? "" : "s")}");
            if (ephemeralCount > 0) parts.Add($"{ephemeralCount} ephemeral");
            if (staleCount > 0) parts.Add($"{staleCount} stale");
            if (reviewCount > 0) parts.Add($"{reviewCount} need review");
            sb.Append(string.Join(" · ", parts)).Append('\n');
        }
        sb.Append('\n');

        // Topics with entry counts and total size
        sb.Append("### Scopes").Append('\n');
        foreach (var group in grouped)
        {
            string label = group.Key == "local" ? "local" : $"/{group.Key}";
            long groupBytes = group.Sum(e => e.Entry.OriginalBytes);
            sb.Append($"- **{label}** — {group.Count()} {(group.Count() == 1 ? "memory" : "memories")}, {FormatBytes(groupBytes)}").Append('\n');
        }
        if (ephemeralCount > 0)
            sb.Append($"- **ephemeral** — {ephemeralCount} {(ephemeralCount == 1 ? "memory" : "memories")}").Append('\n');
        sb.Append('\n');

        // Top keywords + top attributes — computed by the shared aggregator that also backs
        // `scri list --summary`, so the two surfaces never diverge (they used to: the MCP side
        // double-reported the key:value keywords the CLI filtered, and rendered attributes per value
        // rather than per key).
        var (topKeywords, topAttributes) = AggregateSummaryFacets(entries);
        if (topKeywords.Length > 0)
        {
            sb.Append("### Top keywords").Append('\n');
            sb.Append(string.Join(", ", topKeywords)).Append('\n');
            sb.Append('\n');
        }
        if (topAttributes.Length > 0)
        {
            sb.Append("### Top attributes").Append('\n');
            sb.Append(string.Join(", ", topAttributes.Select(a => $"{a.Key} ({a.Count})"))).Append('\n');
            sb.Append('\n');
        }

        sb.Append("Use `list({ mode: 'full' })` to see all entries, or `search({ query: '...' })` to find specific memories.");
        return sb.ToString();
    }

    /// <summary>
    /// Aggregates the "Top keywords" and "Top attributes" facets for the list summary. Shared by
    /// the MCP <c>list</c> tool (<see cref="BuildSummary"/>) and the <c>scri list --summary</c> CLI
    /// so both surfaces report identically. Key:value keyword-shaped entries (e.g. <c>person:maria</c>)
    /// are excluded from keywords — they're routed into structured Attributes and counted there
    /// (per key) instead, to avoid double-reporting the same facet.
    /// </summary>
    internal static (string[] TopKeywords, (string Key, int Count)[] TopAttributes) AggregateSummaryFacets(
        IEnumerable<ScopedArtifact> entries)
    {
        var keywordCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var attributeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in entries)
        {
            if (item.Entry.Keywords is { Length: > 0 })
                foreach (var kw in item.Entry.Keywords)
                {
                    if (KeyValueKeywordShape.IsMatch(kw)) continue;
                    keywordCounts[kw] = keywordCounts.GetValueOrDefault(kw) + 1;
                }
            foreach (var tag in AttributeReaders.DisplayTags(item.Entry.Attributes))
                keywordCounts[tag] = keywordCounts.GetValueOrDefault(tag) + 1;

            if (item.Entry.Attributes is { Count: > 0 } attributes)
                foreach (var key in attributes.Keys)
                    attributeCounts[key] = attributeCounts.GetValueOrDefault(key) + 1;
        }

        var topKeywords = keywordCounts
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(10).Select(kv => kv.Key).ToArray();
        var topAttributes = attributeCounts
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(10).Select(kv => (kv.Key, kv.Value)).ToArray();
        return (topKeywords, topAttributes);
    }

    /// <summary>Matches legacy typed-prefix keywords (e.g. <c>person:maria</c>) so the raw keyword
    /// tally can exclude them — they're routed into structured Attributes.</summary>
    private static readonly Regex KeyValueKeywordShape =
        new(@"^[A-Za-z][A-Za-z0-9_-]{0,29}:.+", RegexOptions.Compiled);

    internal static string BuildFullList(List<ScopedArtifact> entries, IMemoryStore store, int offset, int limit)
    {
        int total = entries.Count;
        if (offset < 0) offset = 0;
        if (limit < 1) limit = 50;
        var page = entries.Skip(offset).Take(limit).ToList();

        // Workspace root for the [drift] column, resolved only when the page actually carries an
        // entry with FileHashes — the root is cheap, but the column below it is a re-hash per
        // referenced file, and most pages have none.
        string? workspaceRoot = page.Any(p => p.Entry.FileHashes is { Count: > 0 })
            ? store.WorkspaceRoot
            : null;

        // Build qualified names first to compute dynamic column width (never truncate names)
        var rows = new List<(string Name, ArtifactEntry Entry)>(page.Count);
        int nameW = 4; // min width = "name".Length
        foreach (var item in page)
        {
            var e = item.Entry;
            string qualifiedName = item.Scope == "ephemeral"
                ? $"~{e.Name}"
                : store.FormatQualifiedName(item.Scope, e.Name);
            rows.Add((qualifiedName, e));
            if (qualifiedName.Length > nameW) nameW = qualifiedName.Length;
        }

        const int chunkW = 7;
        const int bytesW = 10;
        const int tokensW = 8;
        const int dateW = 17;

        // Every terminator below is an explicit '\n', not AppendLine: AppendLine emits \r\n on
        // Windows, but ToYaml later splits this builder's return value on '\n' only — a \r\n
        // terminator here leaves a stray \r on every rendered line.
        var sb = new System.Text.StringBuilder();

        // Pagination header
        int showing = offset + 1;
        int showingEnd = offset + page.Count;
        sb.Append($"Showing {showing}-{showingEnd} of {total} memories.").Append('\n');
        sb.Append('\n');

        sb.Append(
            $"{"name".PadRight(nameW)}  {"chunks",chunkW}  {"bytes",bytesW}  {"~tokens",tokensW}  {"created",dateW}  description").Append('\n');
        sb.Append(new string('-', nameW + chunkW + bytesW + tokensW + dateW + 18)).Append('\n');

        foreach (var (qualifiedName, e) in rows)
        {
            string sizeStr = FormatBytes(e.OriginalBytes);
            int estTokens = (int)(e.OriginalBytes / 4);
            string dateStr = e.CreatedAt.ToString("yyyy-MM-dd HH:mm");

            // Review markers
            var status = AttributeReaders.GetReviewStatus(e.Attributes, DateTimeOffset.UtcNow);
            string reviewPrefix = status.IsStale ? "[stale] " : status.NeedsReview ? "[review?] " : "";

            // Drift marker — only check entries that have FileHashes
            string driftPrefix = "";
            if (workspaceRoot is not null && e.FileHashes is { Count: > 0 })
            {
                bool hasDrift = false;
                foreach (var (path, storedHash) in e.FileHashes)
                {
                    var fullPath = ResolveWorkspacePath(workspaceRoot, path);
                    if (fullPath is null || !File.Exists(fullPath))
                    {
                        hasDrift = true;
                        break;
                    }
                    var currentHash = ComputeFileHash(fullPath);
                    if (currentHash is null || !currentHash.Equals(storedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        hasDrift = true;
                        break;
                    }
                }
                if (hasDrift) driftPrefix = "[drift] ";
            }

            string desc = AttributeReaders.PreviewText(e.ContentPreview);
            desc = desc.Replace('\n', ' ').Replace('\r', ' ');
            string fullDesc = reviewPrefix + driftPrefix + desc;
            if (fullDesc.Length > 60) fullDesc = fullDesc[..57] + "...";

            sb.Append(
                $"{qualifiedName.PadRight(nameW)}  {e.ChunkCount,chunkW}  {sizeStr,bytesW}  {estTokens,tokensW}  {dateStr,-dateW}  {fullDesc}").Append('\n');
        }

        if (showingEnd < total)
            sb.Append($"\nUse list(mode=\"full\", offset={showingEnd}) for more.").Append('\n');

        return sb.ToString().TrimEnd();
    }

    /// <summary>Search memories across local and topic scopes using a query.</summary>
    internal async Task<McpResponse> Search(
        [Description("Search term matched against memory names and descriptions.")] string query,
        [Description("Optional scope order, e.g. ['local', 'api', 'ephemeral']. " +
                     "Topic names filter to local topics (e.g. 'api' shows api topic entries). " +
                     "An empty array behaves the same as omitting it.")] string[]? scopes = null,
        [Description("Maximum results to return.")] int limit = 20,
        [Description("Optional topic names to exclude from results. " +
                     "Use ['plan', 'task', 'project', 'learn'] to hide planning namespaces from knowledge searches. " +
                     "An empty array behaves the same as omitting it.")] string[]? excludeTopics = null,
        [Description("Optional context terms appended to the query for scoring only — bridges vocabulary mismatches when the agent knows synonyms or related concepts.")] string? context = null,
        [Description("Optional path prefix to scope results, e.g. '/findings/'.")] string? pathPrefix = null,
        [Description("Optional attribute filter (map key→values); AND across keys, OR within a key's values.")] IReadOnlyDictionary<string, string[]>? attributeFilter = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;
        var outcome = await SearchPipeline.ExecuteAsync(
            store,
            new SearchQuery(query)
            {
                Scopes = scopes,
                Limit = limit,
                ExcludeTopics = excludeTopics,
                PathPrefix = pathPrefix,
                AttributeFilter = attributeFilter,
            },
            SearchContributorContext.Current,
            EntityGraphScoreContext.Current,
            context,
            surfaceEntityFacts: true,
            cancellationToken);
        var matches = outcome.Matches;

        if (matches.Count == 0)
            return ResponseBuilder.Success("No matching memories found.").WithAction("searched")
                .WithTruncationAdvice(SearchTruncationAdvice);

        string body = SearchResultRenderer.FormatRichSearchResults(store, outcome.Matches, outcome.EntityFacts);
        return ResponseBuilder.Success(body).WithAction("searched")
            .WithTruncationAdvice(SearchTruncationAdvice);
    }

    /// <summary>Removes a stored artifact and its index entry.</summary>
    internal async Task<McpResponse> Forget(
        [Description("The artifact name (e.g. \"session-notes\", \"/api/auth\", \"/temp/scratch\") or its file:// URI.")] string nameOrUri,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Ephemeral memory (~name)
        if (store.IsEphemeral(nameOrUri))
        {
            string key = MemoryNaming.StripEphemeralPrefix(nameOrUri);
            if (!store.ForgetEphemeral(key))
                return ResponseBuilder.Error(
                    $"No ephemeral memory found with name '~{key}'.",
                    ErrorCodes.NotFound,
                    "list() to see ephemeral memories currently held in process");

            try { await (MemoryEventSinkContext.Current?.OnForgottenAsync($"~{key}", true, store, cancellationToken) ?? Task.CompletedTask); }
            catch { /* plugin errors must not block forget */ }

            return ResponseBuilder.Success($"Forgot: ~{key}").WithAction("forgotten");
        }

        // Backward compat: resolve file:// URIs to their memory name, then delete by name
        if (nameOrUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            string name = FileMemoryStore.NameFromUri(nameOrUri);

            bool removedAny = false;
            var removedScopes = new List<string>();
            foreach (string s in store.ResolveReadScopes())
            {
                store.DeleteArtifact(name, s);
                if (store.Remove(name, s))
                {
                    removedScopes.Add(s);
                    removedAny = true;
                }
            }

            if (!removedAny)
                return ResponseBuilder.Error(
                    $"No artifact found with name or URI '{nameOrUri}'.",
                    ErrorCodes.NotFound,
                    "list() to see available memories");

            var sink = MemoryEventSinkContext.Current;
            if (sink is not null)
            {
                foreach (string s in removedScopes)
                {
                    // Notify per scope the memory lived in, so vectors are removed from EVERY
                    // scope's index — not just `local` (see the matching comment on the
                    // non-file:// path below).
                    try { await sink.OnForgottenAsync(store.FormatQualifiedName(s, name), true, store, cancellationToken); }
                    catch { /* plugin errors must not block forget */ }
                }
            }

            return ResponseBuilder.Success($"Forgot: {name}.").WithFileChanges().WithAction("forgotten");
        }

        var (scope, subject) = store.ParseQualifiedName(nameOrUri);
        string qualifiedName = store.FormatQualifiedName(scope, subject);
        // The sink re-parses this to find which scope's vectors to remove. Under v3
        // FormatQualifiedName is round-trip-safe, so this equals qualifiedName above.
        string sinkQualifiedName = store.FormatQualifiedName(scope, subject);

        // Delete the artifact file
        bool deleted = store.DeleteArtifact(subject, scope);

        // Remove index entry
        bool removed = store.Remove(subject, scope);
        if (!removed && !deleted)
            return ResponseBuilder.Error(
                $"No artifact found with name '{nameOrUri}'.",
                ErrorCodes.NotFound,
                "list() to see available memories");

        try { await (MemoryEventSinkContext.Current?.OnForgottenAsync(sinkQualifiedName, deleted || removed, store, cancellationToken) ?? Task.CompletedTask); }
        catch { /* plugin errors must not block forget */ }

        return ResponseBuilder.Success($"Forgot: {qualifiedName}.").WithFileChanges().WithAction("forgotten");
    }

    // ── Append/Reflect/Budget tools ─────────────────────────────────────────

    /// <summary>
    /// Append content as a new chunk to an existing memory, or create it if it does not exist.
    /// Preserves the memory's curated retrieval metadata (anchors, tags, keywords, description,
    /// file references) and merges any caller-supplied <paramref name="tags"/> / <paramref name="keywords"/> /
    /// <paramref name="anchors"/> into it — appended content can introduce new concepts users will
    /// query for, and the metadata `remember` set must not be silently discarded on append.
    /// </summary>
    internal async Task<McpResponse> Append(
        [Description("The text content to append.")] string content,
        [Description("Memory name to append to (e.g. 'session-notes', '/api/auth-flow', '~scratch').")] string name,
        [Description("Optional description. Replaces the memory's description; if omitted, the existing description is preserved.")] string? description = null,
        [Description("Optional tags to merge into the memory's existing tags (deduplicated).")] string[]? tags = null,
        [Description("Optional keywords merged with the memory's existing + auto-extracted terms (deduplicated).")] string[]? keywords = null,
        [Description("Optional retrieval anchors, merged into the memory's existing anchors. This " +
                     "Description is inert — internal helper, not an MCP tool. The public 'append' tool " +
                     "has no dedicated anchors parameter; a caller reaches this through its attributes " +
                     "parameter (attributes: { anchor: [...] }) in MemoryTools.cs, which documents what " +
                     "an anchor is for.")] string[]? anchors = null,
        IReadOnlyDictionary<string, string[]>? attributes = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // ── The STORE derives, and the store also resolves ───────────────
        //
        // What stood here was the fourth copy of the derivation contract (BuildAppendMetadata plus
        // its two per-branch write sites) wrapped around a hand-rolled read-decode-re-encode. All
        // of it is IMemoryStore.AppendAsync's now: the artifact read under the entry's own casing,
        // the chunk decode, the carry-forward of everything the memory already holds — its curated
        // keywords (D-12), its drift baseline (D-21), its earlier chunk entries and its preview
        // (D-20) — the tag/anchor union (D-17), and the single seeding pass (D-08).
        //
        // The casing ruling that used to live here lives there too, with the same reasoning and
        // its own guards (FileMemoryStore.AppendAsync resolves identity from the index BEFORE the
        // artifact read, so an append under a mismatched casing stays an append instead of
        // replacing the memory with the appended chunk alone). CaseSensitiveMcpAppendTests still
        // proves the outcome end-to-end through this tool.
        //
        // Appending to a memory that does not exist yet is a store, and AppendAsync performs it
        // itself rather than handing the decision back — which is why there is no create branch
        // here and no second call into Remember.
        var (ephemeral, scope, subject) = Resolve(store, name);

        var entry = await store.AppendAsync(
            new MemoryWrite(subject, scope, [content], description,
                WriteAttributes(attributes, tags, anchors), keywords),
            cancellationToken);

        string qualifiedName = QualifiedName(store, entry, ephemeral, scope);

        // Fire event sink (embeddings, etc.) — fire-and-forget so embedding latency
        // never blocks the response. Failure logged to stderr; the artifact is already on disk.
        // CancellationToken.None — see the Remember path above for rationale.
        //
        // UpdatedAt is the create-vs-append discriminator, the same one MemoryOrchestrator.
        // AppendAsync uses for the same decision: a real append always sets it, and it is null only
        // for a first write. A create answers as a store — the "Remembered:" message and the
        // OnStored sink — which is what append() has always returned for that case, when it
        // returned Remember()'s own response.
        bool created = entry.UpdatedAt is null;
        FireEventSinkAsync(sink => created
            ? sink.OnStoredAsync(qualifiedName, [content], store, CancellationToken.None)
            : sink.OnAppendedAsync(qualifiedName, content, store, CancellationToken.None));

        if (created)
            return StoredResponse(entry, qualifiedName, ephemeral, "stored");

        return ResponseBuilder.Success($"Appended chunk {entry.ChunkCount} to {qualifiedName} ({entry.ChunkCount} {(entry.ChunkCount == 1 ? "chunk" : "chunks")}, {FormatBytes(entry.OriginalBytes)}).")
            .WithFileChanges().WithAction("appended");
    }

    // kt removed — knowledge transfer is a learnable goal, not a fixed tool.
    // The agent should treat "produce KT documents" as a goal, execute it, retrospect, and save a skill.

    /// <summary>Find all memories that reference a file path or memory name.</summary>
    internal Task<McpResponse> References(
        [Description("Target to search for — a file path (e.g. 'FileMemoryStore.cs') or memory name (e.g. 'api:auth-flow').")] string target,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;
        string fileKey = $"file:{target}";
        string refKey = $"ref:{target}";

        // Search all scopes for entries with matching ref keywords
        var allEntries = store.ListScoped(null);
        var matches = allEntries
            .Where(sa =>
                (sa.Entry.Keywords is not null &&
                 sa.Entry.Keywords.Any(k =>
                     k.Equals(fileKey, StringComparison.OrdinalIgnoreCase) ||
                     k.Equals(refKey, StringComparison.OrdinalIgnoreCase) ||
                     k.EndsWith($"/{target}", StringComparison.OrdinalIgnoreCase)))
                ||
                (sa.Entry.Attributes is not null &&
                 (MatchesRefAttribute(sa.Entry.Attributes, "file", target) ||
                  MatchesRefAttribute(sa.Entry.Attributes, "ref", target))))
            .Select(sa => store.FormatQualifiedName(sa.Scope, sa.Entry.Name))
            .Distinct()
            .ToList();

        if (matches.Count == 0)
            return Task.FromResult(ResponseBuilder.Success($"No memories reference '{target}'.").WithAction("searched"));

        string result = $"Found {matches.Count} memory(s) referencing '{target}':\n" +
            string.Join("\n", matches.Select(m => $"- {m}"));
        return Task.FromResult(ResponseBuilder.Success(result).WithAction("searched"));
    }

    /// <summary>Reference-attribute match: exact value or path-suffix ("/{target}") — the attributes-side
    /// twin of the legacy file:/ref: keyword predicate in References.</summary>
    private static bool MatchesRefAttribute(Dictionary<string, string[]> attributes, string key, string target)
    {
        foreach (var kv in attributes)
        {
            if (!kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string v in kv.Value)
            {
                if (v.Equals(target, StringComparison.OrdinalIgnoreCase)
                    || v.EndsWith($"/{target}", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="name"/> names a memory that exists, without writing to it.
    ///
    /// <para><b>Probed through the very members the write goes through</b>, so the answer cannot
    /// disagree with what <see cref="UpdateMeta"/> resolves a moment later:
    /// <see cref="IMemoryStore.GetEphemeral"/> for a <c>~name</c>, and
    /// <see cref="IMemoryStore.TryUpdateEntry"/> with a mutation that returns null for everything
    /// else. That member's contract is exactly this probe — the mutation runs if and only if the
    /// entry exists, and a null return persists nothing — so both implementations short-circuit
    /// before their write.</para>
    ///
    /// <para>A <c>LoadIndex</c> scan would have been a SECOND resolution rule to keep in step with
    /// <c>FileMemoryStore.TryUpdateEntry</c>'s sidecar lookup.</para>
    /// </summary>
    private static bool Exists(IMemoryStore store, string name)
    {
        if (store.IsEphemeral(name))
            return store.GetEphemeral(MemoryNaming.StripEphemeralPrefix(name)) is not null;

        var (scope, subject) = store.ParseQualifiedName(name);
        bool found = false;
        store.TryUpdateEntry(subject, scope, _ => { found = true; return null; });
        return found;
    }

    /// <summary>
    /// Create a bidirectional relationship between two memories. Writes a unified
    /// <c>ref</c> attribute on each side, handed to <see cref="UpdateMeta"/> as an attribute and
    /// never minted as a <c>ref:{name}</c>-shaped keyword for routing to turn back into one.
    /// Refuses without writing anything unless both memories already exist.
    /// <para>Either side may be an ephemeral <c>~name</c> <i>as far as this method is concerned</i>
    /// — but that only reaches a caller whose process is the one holding the ephemeral. It is real
    /// for the MCP tool, served by a long-lived <c>scri serve</c>; it is unreachable for the CLI
    /// (<c>ScriniaCommands.Link</c>), where every invocation is a fresh process whose ephemeral map
    /// is necessarily empty, so a <c>~name</c> can only ever resolve as missing.</para>
    /// </summary>
    internal async Task<McpResponse> Link(
        [Description("Source memory name.")] string from,
        [Description("Target memory name.")] string to,
        [Description("Reason for the connection.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        // BOTH sides are checked BEFORE either is written. link makes two writes and cannot undo
        // the first if the second fails, and the surviving half is a `ref` pointing at a memory
        // that carries no `ref` back — a link the graph believes in, and worse than either a clean
        // link or a clean refusal.
        //
        // The predicate is EXISTENCE on both sides. It used to be "neither side is ephemeral",
        // which delivered the guarantee above only for an ephemeral side: a non-existent PERSISTENT
        // side sailed past it, failed its own UpdateMeta, and left the other side written and
        // linked to nothing. That was checked into the repo as
        // tests/Scrinia.Tests/Golden/__snapshots__/link-partial-failure.yaml, which recorded
        // "Partial link failure" alongside "/patterns/retry: 1 attribute(s) merged" — the golden
        // renamed by this change. So this is where that guarantee is first true as stated.
        //
        // An ephemeral side is no longer refused at all: UpdateMeta handles a ~name, in both store
        // modes, so existence is the only thing left for link itself to check.
        var store = CurrentStore;
        string? missing = !Exists(store, from) ? from : !Exists(store, to) ? to : null;
        if (missing is not null)
            return ResponseBuilder.Error(
                $"Memory '{missing}' not found — nothing was written.",
                ErrorCodes.NotFound,
                "list() to see available memories",
                $"remember({{ path: '{missing}', content: [...] }}) to create it, then link it");

        // Each side's reference is handed over AS AN ATTRIBUTE. It used to be minted as a
        // `ref:{name}` KEYWORD and left to AttributeExtractor.RouteKeywords to turn back into one,
        // which is a conditional conversion: routing only keeps a value up to
        // AttributeExtractor.MaxValueLength (120), and past that the element stays a plain keyword.
        // ApplyMetadataMutation's keyword merge then unions it into Keywords with no cap, and
        // MemoryDerivation.ForMetadataUpdate unions the same term into AgentKeywords — so linking
        // one memory to enough long-named destinations grew two lists that nothing bounds. Measured
        // on the ephemeral path this reached after Task 8: 31 links to a 134-character name left
        // Keywords at 33 and AgentKeywords at 31, past the 30 every capped write path stops at. The
        // same loop with short names left both untouched, which is the whole tell: the growth was
        // the DEGRADATION, not the linking. Writing the attribute directly removes the conversion,
        // so the destination's name length stops mattering at all.
        McpResponse result1 = await UpdateMeta(
            from, attributes: ReferenceTo(to), cancellationToken: cancellationToken);
        McpResponse result2 = await UpdateMeta(
            to, attributes: ReferenceTo(from), cancellationToken: cancellationToken);

        if (result1.IsError || result2.IsError)
            return ResponseBuilder.Error(
                $"Partial link failure:\n  {from}: {result1.Error ?? result1.Content}\n  {to}: {result2.Error ?? result2.Content}",
                ErrorCodes.Internal,
                $"Verify both '{from}' and '{to}' exist via recall.");

        string linkMsg = $"Linked '{from}' <-> '{to}'.";
        if (!string.IsNullOrWhiteSpace(reason))
            linkMsg += $" Reason: {reason}";
        return ResponseBuilder.Success(linkMsg).WithAction("linked");
    }

    /// <summary>The one-key attribute map naming <paramref name="name"/> as a reference — the shape
    /// <see cref="Link"/> hands each side. <c>"ref"</c> is spelled the same way
    /// <see cref="AttributeExtractor.ExtractRefAttributes"/> spells it, and it is a
    /// <see cref="KnownAttributes.ReferenceKeys"/> member, so it union-merges with whatever
    /// references the entry already carries rather than replacing them.</summary>
    private static Dictionary<string, string[]> ReferenceTo(string name) =>
        new(StringComparer.OrdinalIgnoreCase) { ["ref"] = [name] };

    private static Task<McpResponse> BuildDriftList(IMemoryStore store)
    {
        string workspaceRoot = store.WorkspaceRoot;

        var allEntries = store.ListScoped(null);
        var results = new List<string>();
        int driftCount = 0, missingCount = 0, okCount = 0;

        foreach (var sa in allEntries)
        {
            if (sa.Entry.FileHashes is null or { Count: 0 }) continue;

            // Pass sa.Scope straight in — FormatQualifiedName already handles the "local"/"local:topic"
            // forms. The earlier pre-strip of the "local:" prefix fed it a bare topic ("api"), which
            // it can't recognise, so topic-scoped drift rows lost their "/topic/" path.
            //
            // The "~" is prepended here, not inside FormatQualifiedName, matching the sibling
            // renderer in BuildFullList: FormatQualifiedName only special-cases "local:", so scope
            // "ephemeral" comes back as the BARE name. That was unreachable until Task 6 gave
            // ephemerals a drift baseline — before it they never had FileHashes, so the loop's
            // guard above skipped every one of them. Now they reach this line, and a bare name in
            // a drift row is a name the reader cannot act on: recall('x') either misses or, worse,
            // returns an unrelated persistent memory that happens to share the name.
            string qualName = sa.Scope == "ephemeral"
                ? $"~{sa.Entry.Name}"
                : store.FormatQualifiedName(sa.Scope, sa.Entry.Name);

            foreach (var (path, storedHash) in sa.Entry.FileHashes)
            {
                var fullPath = ResolveWorkspacePath(workspaceRoot, path);
                if (fullPath is null || !File.Exists(fullPath))
                {
                    results.Add($"  {qualName} → {path} [MISSING]");
                    missingCount++;
                }
                else
                {
                    var currentHash = ComputeFileHash(fullPath);
                    if (currentHash is null || !currentHash.Equals(storedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add($"  {qualName} → {path} [DRIFT]");
                        driftCount++;
                    }
                    else okCount++;
                }
            }
        }

        if (results.Count == 0)
        {
            string msg = okCount > 0
                ? $"All {okCount} code references are current. No drift detected."
                : "No memories have code references. Add a file attribute on remember (attributes: { file: ['path'] }) to track file dependencies.";
            return Task.FromResult(ResponseBuilder.Success(msg).WithAction("listed")
                .WithTruncationAdvice(ListTruncationAdvice));
        }

        string driftResponse = $"Code reference drift detected ({driftCount} drifted, {missingCount} missing, {okCount} ok):\n" +
            string.Join("\n", results);
        var driftWarnings = new List<string>();
        if (driftCount > 0) driftWarnings.Add($"{driftCount} code reference(s) have drifted (files changed since stored).");
        if (missingCount > 0) driftWarnings.Add($"{missingCount} code reference(s) point to missing files.");
        return Task.FromResult(
            ResponseBuilder.Success(driftResponse).WithAction("listed").WithActionNeeded(driftWarnings.ToArray())
                .WithTruncationAdvice(ListTruncationAdvice));
    }

    private static string? ResolveWorkspacePath(string workspaceRoot, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath.Trim()));
        return PathContainment.IsWithin(workspaceRoot, fullPath) ? fullPath : null;
    }

    private static string? ComputeFileHash(string fullPath)
    {
        try { return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fullPath))); }
        catch { return null; }
    }

    /// <summary>
    /// Detach an event-sink call from the response path so embedding latency (which can be
    /// 100–400 ms for remote providers) never blocks the MCP write response.
    /// The sink is resolved at fire-time so a concurrent context switch won't bind us to a stale sink.
    /// Errors are logged to stderr; the on-disk artifact is already durable by the time this runs,
    /// so a missed embedding only means the entry won't show in semantic search until the next write.
    ///
    /// <para>Registered with <see cref="BackgroundWork"/> rather than dropped on the floor: the
    /// CLI drains outstanding sinks before exiting (so indexing isn't lost when the process ends
    /// right after a write), and tests can wait for a write's side effects instead of racing them.</para>
    /// </summary>
    private static void FireEventSinkAsync(Func<IMemoryEventSink, Task> action)
    {
        var sink = MemoryEventSinkContext.Current;
        if (sink is null) return;
        BackgroundWork.Fire(_ => action(sink), "event sink");
    }
}
