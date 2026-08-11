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
            var agentStore = MemoryStoreContext.Current as ILocalMemoryStore;
            if (agentStore is not null)
            {
                string agentSubject = artifactOrName["/agent/".Length..].Trim();
                if (PathRouter.TryToFileEntityPath("agent", agentSubject, agentStore.WorkspaceRoot, out string agentFilePath)
                    && File.Exists(agentFilePath))
                {
                    string mdContent = await File.ReadAllTextAsync(agentFilePath, cancellationToken);
                    SessionBudget.RecordAccess(artifactOrName, mdContent.Length);
                    return ResponseBuilder.Success(mdContent).WithAction(actionLabel);
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
            return ResponseBuilder.Success($"Chunk {chunk}/{chunkCount}\n\n{chunkContent}").WithAction(actionLabel);
        }

        byte[] bytes = Nmp2Strategy.Instance.Decode(artifact);
        string decoded = System.Text.Encoding.UTF8.GetString(bytes);
        SessionBudget.RecordAccess(artifactOrName, decoded.Length);

        if (chunkCount > 1)
            return ResponseBuilder.Success($"({chunkCount} chunks)\n\n{decoded}").WithAction(actionLabel);

        return ResponseBuilder.Success(decoded).WithAction(actionLabel);
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
        [Description("Optional keywords for search. Merged with auto-extracted content terms. " +
                     "Prefer key:value keywords using one of these entity types: 'person:<name>', 'place:<name>', " +
                     "'org:<name>', 'thing:<name>', 'topic:<theme>'. These entries, and any other " +
                     "key:value keyword, become structured attributes — an exact-match retrieval signal and " +
                     "entity-graph input, so a query mentioning one entity can surface this memory via a " +
                     "related entity even with no text overlap. Plain keywords remain search keywords. " +
                     "Use the same canonical name for an entity across memories; the type lets a person and a " +
                     "place that share a name stay distinct.")] string[]? keywords = null,
        [Description("Optional retrieval anchors — phrases, questions, or category-level paraphrases that bridge likely user queries to this entry's content. Use vocabulary you expect users to query with, not vocabulary already in the content. Indexed for both attribute-score lookup and BM25 via term-frequency seeding.")]
        string[]? anchors = null,
        [Description("Optional ISO 8601 date after which this memory should be reviewed for staleness.")] string? reviewAfter = null,
        [Description("Optional free-text condition describing when this memory should be reviewed (e.g. 'when auth system changes').")] string? reviewWhen = null,
        IReadOnlyDictionary<string, string[]>? attributes = null,
        string actionLabel = "stored",
        CancellationToken cancellationToken = default)
    {
        // The whole store, not a narrowed one: the ephemeral branch below returns before any layout
        // member is read, and a remote store implements ephemeral storage for real. Narrowing
        // here would break remember({ path: '/temp/...' }) in remote mode.
        var store = CurrentStore;
        string joined = string.Concat(content);

        // Compute text analysis: keywords + term frequencies (single-pass)
        var (autoKeywords, tf) = TextAnalysis.AnalyzeText(joined);

        // Unified attributes: key:value-shaped agent keywords route to Attributes; only plain
        // keywords participate in the keyword merge. Prefixed-keyword writes are retired.
        var (routedAttributes, plainKeywords) = AttributeExtractor.RouteKeywords(keywords);
        var (mergedKeywords, agentKeywordSet) = TextAnalysis.MergeKeywordsWithSource(plainKeywords, autoKeywords);

        // References land in Attributes (file/ref), not as prefixed keywords.
        string rawContent = string.Join("\n", content);
        var refAttributes = AttributeExtractor.ExtractRefAttributes(rawContent);

        // Boost keywords in TF: agent keywords +5, auto-extracted +2. Key:value keywords
        // (prefix:value) seed their tokenized VALUE so the parts are BM25-matchable — the
        // monolithic "prefix:value" string is never a tokenized query term, so seeding it
        // as-is is dead weight. See TextAnalysis.IndexTokensForKeyword.
        foreach (string kw in mergedKeywords)
        {
            int boost = agentKeywordSet.Contains(kw) ? 5 : 2;
            foreach (string token in TextAnalysis.IndexTokensForKeyword(kw))
            {
                tf.TryGetValue(token, out int count);
                tf[token] = count + boost;
            }
        }

        // Seed agent-supplied retrieval anchors into the TF dict so BM25 picks up
        // anchor-derived tokens. Each token gets +2 — the same weight auto-extracted
        // keywords get; the attribute-score branch (+15) carries the agent-curated boost.
        // Empty/whitespace anchors are dropped here; null is treated the same as [].
        string[]? cleanedAnchors = null;
        if (anchors is { Length: > 0 })
        {
            cleanedAnchors = anchors
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .ToArray();
            if (cleanedAnchors.Length == 0)
                cleanedAnchors = null;
            else
            {
                foreach (string anchor in cleanedAnchors)
                {
                    foreach (string token in TextAnalysis.Tokenize(anchor))
                    {
                        tf.TryGetValue(token, out int count);
                        tf[token] = count + 2;
                    }
                }
            }
        }

        // Assemble unified attributes: routed keywords ∪ content extraction ∪ references ∪ prose
        // mention spans, then fold in explicit agent attributes (they win scalar collisions), then
        // seed key/value tokens into TF.
        var entryAttributes = KnownAttributes.Merge(
            KnownAttributes.Merge(
                KnownAttributes.Merge(routedAttributes, AttributeExtractor.Extract(joined)),
                refAttributes),
            AttributeExtractor.ExtractMentionSpans(joined));
        entryAttributes = KnownAttributes.Merge(entryAttributes, KnownAttributes.Normalize(attributes));
        entryAttributes = AttributeExtractor.DropMentionsShadowedByTypedKeys(entryAttributes);
        AttributeExtractor.SeedTermFrequencies(entryAttributes, tf);

        ChunkEntry[]? chunkEntries = content.Length > 1
            ? TextAnalysis.ComputeChunkEntries(content)
            : null;

        // ── Ephemeral path (/temp/...) ──────────────────────────
        if (name.StartsWith("/temp/", StringComparison.OrdinalIgnoreCase))
            name = "~" + name["/temp/".Length..];
        if (store.IsEphemeral(name))
        {
            string key = MemoryNaming.StripEphemeralPrefix(name);
            string ephArtifact = content.Length == 1
                ? Nmp2ChunkedEncoder.Encode(content[0])
                : Nmp2ChunkedEncoder.EncodeChunks(content);
            int ephChunkCount = Nmp2ChunkedEncoder.GetChunkCount(ephArtifact);
            long ephBytes = System.Text.Encoding.UTF8.GetByteCount(joined);
            // Merge 3 Task 2b: an explicit curated description becomes the displayed
            // ContentPreview (option A) — it wins over the auto content snippet.
            string ephPreview = !string.IsNullOrWhiteSpace(description)
                ? description
                : ContentPreview.Generate(joined);

            // Check if updating existing ephemeral entry
            var existingEph = store.GetEphemeral(key);
            DateTimeOffset ephCreatedAt = existingEph?.CreatedAt ?? DateTimeOffset.UtcNow;
            DateTimeOffset? ephUpdatedAt = existingEph is not null ? DateTimeOffset.UtcNow : null;

            // Dual-write: fold tag/anchor into Attributes at write time, mirroring how
            // AttributeMigration.Forward populates the persistent path's Attributes (Task 1).
            // EphemeralEntry carries only Tags/RetrievalAnchors as legacy attribute-attributes
            // (no ReviewAfter/ReviewWhen/FileHashes), so only tag/anchor route here.
            var ephIncoming = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (tags is { Length: > 0 }) ephIncoming[KnownAttributes.TagKey] = tags;
            if (cleanedAnchors is { Length: > 0 }) ephIncoming[KnownAttributes.AnchorKey] = cleanedAnchors;
            var ephAttributes = ephIncoming.Count > 0 ? KnownAttributes.Merge(entryAttributes, ephIncoming) : entryAttributes;

            var ephEntry = new EphemeralEntry(
                Name: key,
                Artifact: ephArtifact,
                OriginalBytes: ephBytes,
                ChunkCount: ephChunkCount,
                CreatedAt: ephCreatedAt,
                ContentPreview: ephPreview,
                Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
                TermFrequencies: tf.Count > 0 ? tf : null,
                UpdatedAt: ephUpdatedAt,
                ChunkEntries: chunkEntries,
                Attributes: ephAttributes);

            store.RememberEphemeral(key, ephEntry);

            // Fire event sink (embeddings, etc.) — fire-and-forget so embedding latency
            // never blocks the response. Failure logged to stderr; the artifact is already on disk.
            // CancellationToken.None: the sink runs detached on a background Task —
            // propagating the per-request CT would abort it the moment the user-facing
            // response returns, leaving importance unscored or vectors half-written.
            FireEventSinkAsync(sink => sink.OnStoredAsync($"~{key}", content, store, CancellationToken.None));

            return ResponseBuilder.Success($"Remembered: ~{key} ({ephChunkCount} {(ephChunkCount == 1 ? "chunk" : "chunks")}, {FormatBytes(ephBytes)}) [ephemeral]")
                .WithAction(actionLabel);
        }

        // ── Agent config path (/agent/* → .scrinia/agent/{name}.md) ──
        if (name.StartsWith("/agent/", StringComparison.OrdinalIgnoreCase))
        {
            string agentSubject = name["/agent/".Length..].Trim();

            // Validate before the guard, so /agent/ and /skill/ answer the same way for the same
            // bad input. SkillCreate validates its name first; without this hoist, a remote
            // remember({ path: '/agent/bad name' }) reported NOT_SUPPORTED while the /skill/
            // sibling reported INVALID_PARAMETER. The path check below stays as defence in depth.
            if (!FileEntityName.IsValid(agentSubject))
                return ResponseBuilder.Error(
                    "Invalid agent config name. Use letters, digits, '-', '_' or '.' only.",
                    ErrorCodes.InvalidParameter,
                    "Use a path like '/agent/profile' for the agent config.");

            // The /agent/ branch is genuinely local-only — it writes a .md file plus a JSON
            // sidecar into the workspace — so it is guarded here, where it is actually reached.
            // Guarding at the method entry instead would refuse every remote remember().
            // Narrowed from the local `store`, not re-read from the ambient context: only part
            // of this method needs local files.
            if (store is not ILocalMemoryStore agentLayout)
                return ResponseBuilder.Error(
                    "An /agent/ config is written as a file in the local workspace, and the active store is remote.",
                    ErrorCodes.NotSupported,
                    "remember() to a regular memory path, which a remote store persists server-side.");

            string agentDir = PathRouter.ToFileEntityDir("agent", agentLayout.WorkspaceRoot);
            if (!PathRouter.TryToFileEntityPath("agent", agentSubject, agentLayout.WorkspaceRoot, out string filePath))
                return ResponseBuilder.Error(
                    "Invalid agent config name. Use letters, digits, '-', '_' or '.' only.", ErrorCodes.InvalidParameter,
                    "Use a path like '/agent/profile' for the agent config.");
            FileMemoryStore.EnsureGitIgnore(agentLayout.WorkspaceRoot);
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

        // ── Persistent path ──────────────────────────────────────────────
        var (scope, subject) = store.ParseQualifiedName(name);

        // Check if entry already exists (for versioning + UpdatedAt)
        var existingEntries = store.LoadIndex(scope);
        var existingEntry = existingEntries.FirstOrDefault(e => e.Name == subject);
        DateTimeOffset createdAt = existingEntry?.CreatedAt ?? DateTimeOffset.UtcNow;
        DateTimeOffset? updatedAt = existingEntry is not null ? DateTimeOffset.UtcNow : null;

        // Archive previous version before overwriting
        if (existingEntry is not null)
            store.ArchiveVersion(subject, scope);

        string artifact = content.Length == 1
            ? Nmp2ChunkedEncoder.Encode(content[0])
            : Nmp2ChunkedEncoder.EncodeChunks(content);

        // Merge 3 Task 2b: an explicit curated description becomes the displayed
        // ContentPreview (option A) — it wins over the auto content snippet.
        string contentPreview = !string.IsNullOrWhiteSpace(description)
            ? description
            : ContentPreview.Generate(joined);

        // New model: the `review` attribute carries review dates and/or free-text conditions.
        // Feed date-parseable values into the existing reviewAfter parse (first wins); join the
        // rest into reviewWhen. Only when the legacy params weren't explicitly supplied.
        if (reviewAfter is null && reviewWhen is null
            && entryAttributes is not null && entryAttributes.TryGetValue(KnownAttributes.ReviewKey, out var reviewVals))
        {
            var whenParts = new List<string>();
            foreach (string v in reviewVals)
            {
                if (reviewAfter is null && DateTimeOffset.TryParse(v, out _))
                    reviewAfter = v;
                else
                    whenParts.Add(v);
            }
            if (whenParts.Count > 0)
                reviewWhen = string.Join("; ", whenParts);
        }

        // Parse reviewAfter
        DateTimeOffset? parsedReviewAfter = null;
        if (!string.IsNullOrWhiteSpace(reviewAfter) && DateTimeOffset.TryParse(reviewAfter, out var ra))
            parsedReviewAfter = ra;

        // Auto-set reviewWhen for content with count patterns (unless explicit reviewWhen provided)
        if (string.IsNullOrWhiteSpace(reviewWhen) && !store.IsEphemeral(name))
        {
            if (CountPattern.IsMatch(joined))
                reviewWhen = "when counts in this memory change";
        }

        // Compute code-reference hashes from the `file` attribute values (drift detection). File
        // dependencies flow in exclusively via attributes.file now — the legacy codeRefs parameter
        // was retired.
        var allRefPaths = (entryAttributes is not null && entryAttributes.TryGetValue("file", out var fileRefs)
                ? fileRefs : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Drift detection hashes files on disk, so it needs a local workspace. A remote store has
        // none; the memory is stored without file hashes rather than the whole call failing. The
        // root is resolved lazily so a remember() with no file refs never asks for one at all.
        // .ToList() above is required: allRefPaths is read twice now and Distinct is lazy.
        string? workspaceRoot = allRefPaths.Count > 0
            ? (store as ILocalMemoryStore)?.WorkspaceRoot
            : null;

        Dictionary<string, string>? codeRefDict = null;
        if (workspaceRoot is not null)
        {
            foreach (var refPath in allRefPaths)
            {
                var fullPath = ResolveWorkspacePath(workspaceRoot, refPath);
                if (fullPath is null || !File.Exists(fullPath)) continue;
                var hash = ComputeFileHash(fullPath);
                if (hash is not null)
                {
                    codeRefDict ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    codeRefDict[refPath.Trim()] = hash;
                }
            }
        }

        // Dual-write: fold tag/anchor/review/file into Attributes at write time — the attributes-native
        // write path (byte-identical to AttributeMigration.Forward's fold, Task 1). Inserted here,
        // immediately before the write, so it captures the auto-derived reviewWhen (above) and
        // the completed codeRefDict (above) — inserting any earlier would fold stale/empty values.
        // It has to precede the *write*, not merely the ArtifactEntry construction below: a remote
        // store's Upsert is a no-op, so WriteArtifactAsync is the only channel these attributes
        // have. Nothing between here and the construction reassigns entryAttributes, so the entry
        // still sees exactly this merged map.
        var carrier = AttributeMigration.BuildCarrierAttributes(
            tags, codeRefDict?.Keys, parsedReviewAfter,
            string.IsNullOrWhiteSpace(reviewWhen) ? null : reviewWhen, cleanedAnchors);
        if (carrier.Count > 0)
            entryAttributes = KnownAttributes.Merge(entryAttributes, carrier);

        await store.WriteArtifactAsync(subject, scope, artifact,
            new ArtifactWriteMetadata(
                Attributes: entryAttributes,
                Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
                Description: contentPreview),
            cancellationToken);

        string uri = store.ArtifactUri(subject, scope);

        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);
        long originalBytes = System.Text.Encoding.UTF8.GetByteCount(joined);
        string qualifiedName = store.FormatQualifiedName(scope, subject);

        var entry = new ArtifactEntry(
            Name: subject,
            Uri: uri,
            OriginalBytes: originalBytes,
            ChunkCount: chunkCount,
            CreatedAt: createdAt,
            ContentPreview: contentPreview,
            Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
            TermFrequencies: tf.Count > 0 ? tf : null,
            UpdatedAt: updatedAt,
            ChunkEntries: chunkEntries,
            FileHashes: codeRefDict,
            Attributes: entryAttributes);
        entry = AttributeMigration.Forward(entry);

        store.Upsert(entry, scope);

        // Fire event sink (embeddings, etc.) — fire-and-forget so embedding latency
        // never blocks the response. Failure logged to stderr; the artifact is already on disk.
        // CancellationToken.None — see the ephemeral path above for rationale.
        // The sink re-parses this to decide which scope to embed under; under v3
        // FormatQualifiedName is round-trip-safe for every scope (no more namespace-prefix
        // stripping), so this is the same value as the display `qualifiedName` above.
        string sinkQualifiedName = store.FormatQualifiedName(scope, subject);
        FireEventSinkAsync(sink => sink.OnStoredAsync(sinkQualifiedName, content, store, CancellationToken.None));

        return ResponseBuilder.Success($"Remembered: {qualifiedName} ({chunkCount} {(chunkCount == 1 ? "chunk" : "chunks")}, {FormatBytes(originalBytes)}).")
            .WithFileChanges().WithAction(actionLabel);
    }

    /// <summary>Update metadata on an existing memory without re-encoding its content.</summary>
    internal Task<McpResponse> UpdateMeta(
        [Description("Memory name (e.g. '/api/auth-flow', 'session-notes').")] string name,
        [Description("Keywords to add (merged with existing, not replaced).")] string[]? keywords = null,
        [Description("New description (replaces existing if provided).")] string? description = null,
        [Description("ISO 8601 date for review (replaces existing if provided).")] string? reviewAfter = null,
        [Description("Condition for review (replaces existing if provided).")] string? reviewWhen = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        if (store.IsEphemeral(name))
            return Task.FromResult(ResponseBuilder.Error(
                "Update does not support ephemeral memories. Use remember instead.",
                ErrorCodes.InvalidParameter,
                "remember({ path: '...', content: [...] }) — overwrites an existing ephemeral entry"));

        var (scope, subject) = store.ParseQualifiedName(name);
        string qualifiedName = store.FormatQualifiedName(scope, subject);

        // Track what changed for the response message
        var changes = new List<string>();
        bool found = false;

        // Routed through TryUpdateEntry rather than the LoadIndex → mutate → Upsert shape this
        // used to have. Remotely, Upsert is a deliberate no-op, so that shape mutated a copy,
        // handed it to a method that discards it, and still reported "updated" — link() said
        // "Linked 'a' <-> 'b'" while the `ref` attribute never reached the server. TryUpdateEntry
        // is the seam that already fails closed there (HttpMemoryStore overrides it precisely so a
        // mutation it cannot persist is refused rather than silently dropped), and locally it
        // additionally runs under FileMemoryStore's per-scope write lock, so load→mutate→persist
        // is atomic instead of a racy read-modify-write. `found`/`changes` are captured out of the
        // callback because a false return has three distinct causes the caller must tell apart:
        // no such entry, nothing to change, and "found but not persistable".
        bool persisted = store.TryUpdateEntry(subject, scope, entry =>
        {
            found = true;

            // Route key:value-shaped additions into Attributes; only plain ones merge into Keywords.
            var (routedAttributes, plainAdds) = AttributeExtractor.RouteKeywords(keywords);

            string[]? mergedKeywords = entry.Keywords;
            int addedCount = 0;
            if (plainAdds is { Length: > 0 })
            {
                var existing = entry.Keywords ?? [];
                mergedKeywords = existing.Union(plainAdds, StringComparer.OrdinalIgnoreCase).ToArray();
                addedCount = mergedKeywords.Length - existing.Length;
                if (addedCount > 0) changes.Add($"{addedCount} keyword(s) added");
            }

            Dictionary<string, string[]>? mergedAttributes = entry.Attributes;
            if (routedAttributes is not null)
            {
                mergedAttributes = KnownAttributes.Merge(entry.Attributes, routedAttributes);
                changes.Add($"{routedAttributes.Count} attribute(s) merged");
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

            // Parse reviewAfter if provided
            DateTimeOffset? parsedReviewAfter = existingReviewDate;
            if (!string.IsNullOrWhiteSpace(reviewAfter))
            {
                if (DateTimeOffset.TryParse(reviewAfter, out var ra))
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

            // Null means "no write" to TryUpdateEntry; the caller reports it as the warning below.
            if (changes.Count == 0) return null;

            // Attributes-native direct write (dual-write with the legacy ReviewAfter/ReviewWhen
            // members below + the Forward call at the end of this callback). ADD/REPLACE ONLY — no
            // Remove(ReviewKey) — matches AttributeMigration.Forward's incoming-only fold: when
            // neither reviewAfter nor reviewWhen is present, Forward's `incoming` carries no "review"
            // key, so KnownAttributes.Merge PRESERVES any existing Attributes["review"] rather than
            // clearing it. KnownAttributes.Merge always returns a NEW dictionary, so this never mutates
            // entry.Attributes (== mergedAttributes when routedAttributes is null) in place.
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

            // Build the updated entry via term frequencies merge
            var updatedTf = entry.TermFrequencies;
            if (plainAdds is { Length: > 0 } && addedCount > 0)
            {
                var tf = entry.TermFrequencies is not null
                    ? new Dictionary<string, int>(entry.TermFrequencies)
                    : new Dictionary<string, int>();
                var existing = entry.Keywords ?? [];
                foreach (string kw in plainAdds)
                {
                    if (!existing.Contains(kw, StringComparer.OrdinalIgnoreCase))
                    {
                        tf.TryGetValue(kw, out int count);
                        tf[kw] = count + 5;
                    }
                }
                updatedTf = tf;
            }

            if (routedAttributes is not null)
            {
                var tfForAttributes = updatedTf is not null
                    ? new Dictionary<string, int>(updatedTf) : new Dictionary<string, int>();
                // Agent-curated attributes seed at +5, matching agent keywords (not the +3 auto rate).
                foreach (var kv in routedAttributes)
                    foreach (string v in kv.Value)
                        foreach (string token in TextAnalysis.Tokenize(v))
                        {
                            tfForAttributes.TryGetValue(token, out int c);
                            tfForAttributes[token] = c + 5;
                        }
                updatedTf = tfForAttributes;
            }

            var updated = entry with
            {
                Keywords = mergedKeywords,
                TermFrequencies = updatedTf,
                Attributes = mergedAttributes,
                ContentPreview = updatedContentPreview,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            return AttributeMigration.Forward(updated);
        });

        if (!found)
            return Task.FromResult(ResponseBuilder.Error(
                $"Memory '{qualifiedName}' not found.",
                ErrorCodes.NotFound,
                "list() to see available memories",
                "remember({ path: '...', content: [...] }) to create it"));

        if (changes.Count == 0)
            return Task.FromResult(ResponseBuilder.Warning("No changes specified. Provide at least one of: keywords, description, reviewAfter, reviewWhen."));

        // Found, had something to change, and the store still could not persist it — remotely,
        // where a metadata-only mutation has no channel (Upsert is a no-op and there is no
        // attributes-only PATCH endpoint yet). Reporting success here is the defect this branch
        // exists to close, so say so plainly instead.
        if (!persisted)
            return Task.FromResult(ResponseBuilder.Error(
                $"Metadata for '{qualifiedName}' was not updated: the active store cannot persist a " +
                "metadata-only change (a remote store has no attributes-only update endpoint yet).",
                ErrorCodes.NotSupported,
                "remember({ path: '...', content: [...] }) — a full write does carry attributes, keywords and description to a remote store"));

        return Task.FromResult(
            ResponseBuilder.Success($"Updated metadata for '{qualifiedName}': {string.Join(", ", changes)}.")
                .WithFileChanges().WithAction("updated"));
    }

    /// <summary>Returns a summary or full listing of persisted memories.</summary>
    internal Task<McpResponse> List(
        [Description("Optional comma-separated scope order, e.g. local,api,ephemeral. " +
                     "Topic names filter to local topics (e.g. 'api' shows api topic entries).")] string? scopes = null,
        [Description("'summary' (default), 'full' for paginated table, 'drift' for code reference drift check.")] string mode = "summary",
        [Description("Starting index for full mode (0-based). Ignored in summary mode.")] int offset = 0,
        [Description("Maximum entries to return in full mode (default 50). Ignored in summary mode.")] int limit = 50,
        [Description("Optional comma-separated topic names to exclude from results. " +
                     "Use 'plan,task,project,learn' to hide planning namespaces from knowledge listings.")] string? excludeTopics = null,
        CancellationToken cancellationToken = default)
    {
        // The whole store, not a narrowed one: only the drift branch is unconditionally local.
        // Summary mode, the empty-store early return, and full mode over entries without file
        // attributes all worked remotely before the split and must keep working.
        var store = CurrentStore;

        // Only this branch is refused: drift detection re-hashes files on disk, so it needs the
        // workspace. The guard sits here rather than at the method entry so a remote list() in
        // any other mode still returns its listing.
        if (string.Equals(mode, "drift", StringComparison.OrdinalIgnoreCase))
        {
            if (store is not ILocalMemoryStore driftStore)
                return Task.FromResult(ResponseBuilder.Error(
                    "Drift detection re-hashes referenced files on disk, and the active store is remote.",
                    ErrorCodes.NotSupported,
                    "list() without mode='drift' for a plain listing."));
            return BuildDriftList(driftStore);
        }

        List<ScopedArtifact> entries = store.ListScoped(scopes, excludeTopics);
        if (entries.Count == 0)
            return Task.FromResult(ResponseBuilder.Success("No memories stored.").WithAction("listed"));

        entries.Sort((a, b) => b.Entry.CreatedAt.CompareTo(a.Entry.CreatedAt));

        if (!string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ResponseBuilder.Success(BuildSummary(entries, store)).WithAction("listed"));

        return Task.FromResult(ResponseBuilder.Success(BuildFullList(entries, store, offset, limit)).WithAction("listed"));
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

        // Derive workspace root for the [drift] column (only reached if an entry has FileHashes).
        // Re-hashing needs files on disk, and a non-local store has none — so the page is returned
        // without the column rather than the whole listing failing. Same degrade as the remember()
        // path above (:387), and the same shape a remote store already gets here, since
        // HttpMemoryStore.ListScoped never populates FileHashes. Reachable for real: the server
        // installs whatever an IStorageBackend plugin's CreateStore returned as the ambient store,
        // and nothing obliges that to be an ILocalMemoryStore.
        string? workspaceRoot = page.Any(p => p.Entry.FileHashes is { Count: > 0 })
            ? (store as ILocalMemoryStore)?.WorkspaceRoot
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
        [Description("Optional comma-separated scope order, e.g. local,api,ephemeral. " +
                     "Topic names filter to local topics (e.g. 'api' shows api topic entries).")] string? scopes = null,
        [Description("Maximum results to return.")] int limit = 20,
        [Description("Optional comma-separated topic names to exclude from results. " +
                     "Use 'plan,task,project,learn' to hide planning namespaces from knowledge searches.")] string? excludeTopics = null,
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
            return ResponseBuilder.Success("No matching memories found.").WithAction("searched");

        string body = SearchResultRenderer.FormatRichSearchResults(store, outcome.Matches, outcome.EntityFacts);
        return ResponseBuilder.Success(body).WithAction("searched");
    }

    /// <summary>Copies a memory artifact from one scope to another.</summary>
    internal Task<McpResponse> Copy(
        [Description("Memory name or file:// URI to copy.")] string nameOrUri,
        [Description("Destination as qualified name (e.g. '/api/auth-flow' or 'my-notes'). " +
                     "Use '~name' for ephemeral destination.")] string destination,
        [Description("When true, replaces destination memory if it already exists.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        bool ok = CurrentStore.CopyMemory(nameOrUri, destination, overwrite, out string msg);
        if (!ok) return Task.FromResult(ResponseBuilder.Error(msg, ErrorCodes.Internal));
        return Task.FromResult(ResponseBuilder.Success(msg).WithAction("copied"));
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
        [Description("Optional retrieval anchors merged into the memory's existing anchors (deduplicated) — phrases/questions that bridge likely user queries to this content. Supply these when the appended content introduces concepts users may search for in different words.")] string[]? anchors = null,
        IReadOnlyDictionary<string, string[]>? attributes = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        string? existingArtifact = null;
        try
        {
            existingArtifact = await store.ResolveArtifactAsync(name, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // Will create new
        }

        if (existingArtifact is null)
        {
            // Non-existent → create as single-chunk (same as Store). Forward any
            // caller-supplied metadata so a create-via-append is not lossy.
            return await this.Remember([content], name,
                description: description ?? "",
                tags: tags,
                keywords: keywords,
                anchors: anchors,
                attributes: attributes,
                cancellationToken: cancellationToken);
        }

        // Append as new chunk
        string newArtifact = Nmp2ChunkedEncoder.AppendChunk(existingArtifact, content);

        // Decode full result for metadata
        byte[] fullBytes = Nmp2Strategy.Instance.Decode(newArtifact);
        string fullText = System.Text.Encoding.UTF8.GetString(fullBytes);
        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(newArtifact);
        long originalBytes = fullBytes.LongLength;

        // Merge 3 Task 2b: entry-level ContentPreview is computed per-branch below (option A —
        // new description wins; otherwise PRESERVE the existing entry's ContentPreview rather
        // than regenerating from content, which would silently drop a curated description).

        // Build chunk entry for the newly appended content (single-pass)
        var (newKw, newTf) = TextAnalysis.AnalyzeText(content);
        foreach (string k in newKw) { newTf.TryGetValue(k, out int c); newTf[k] = c + 2; }

        // Unified attributes: key:value-shaped agent keywords route to Attributes, same as Store.
        // Only the plain remainder participates in the entry-level keyword merge below.
        var (routedNewAttributes, plainNewKeywords) = AttributeExtractor.RouteKeywords(keywords);

        // Ref/file references are no longer folded into chunk-level keywords — they land in
        // Attributes via the entry-level merge in BuildAppendMetadata.
        var newChunkEntry = new ChunkEntry(
            ChunkIndex: chunkCount,
            ContentPreview: ContentPreview.Generate(content),
            Keywords: newKw.Length > 0 ? newKw : null,
            TermFrequencies: newTf.Count > 0 ? newTf : null);

        string qualifiedName;
        // Round-trip-safe form handed to the event sink — see the matching comment on the
        // Remember/Store sink dispatch. Kept distinct from `qualifiedName` (display) because
        // FormatQualifiedName's display form strips the "memory/"/"entity/" namespace prefix,
        // which would reparse to the wrong scope for any v1-namespaced topic.
        string sinkQualifiedName;

        if (store.IsEphemeral(name))
        {
            string key = MemoryNaming.StripEphemeralPrefix(name);
            var existingEph = store.GetEphemeral(key);
            DateTimeOffset createdAt = existingEph?.CreatedAt ?? DateTimeOffset.UtcNow;

            // Merge 3 Task 2b: new description wins; otherwise preserve the existing entry's
            // ContentPreview (which now carries either a prior curated description or the prior
            // auto preview) rather than regenerating from content — that would drop a curated
            // description on a no-op append. See Append_PreservesCuratedAnchorsTagsAndDescription.
            string contentPreview = !string.IsNullOrWhiteSpace(description)
                ? description
                : (existingEph?.ContentPreview ?? ContentPreview.Generate(fullText));

            // meta.Description is unused post-Merge-3 (the legacy Description member it fed is
            // removed); ContentPreview (computed above) is the surviving display carrier.
            var meta = BuildAppendMetadata(
                fullText, description, tags, plainNewKeywords, anchors,
                existingEph?.ContentPreview, AttributeReaders.DisplayTags(existingEph?.Attributes).ToArray(),
                existingEph?.Keywords, AttributeReaders.DisplayAnchors(existingEph?.Attributes).ToArray(),
                content, routedNewAttributes, existingEph?.Attributes, attributes);

            ChunkEntry[]? existingChunks = existingEph?.ChunkEntries;
            ChunkEntry[] updatedChunks = existingChunks is not null
                ? [.. existingChunks, newChunkEntry]
                : [newChunkEntry];

            var ephEntry = new EphemeralEntry(
                Name: key,
                Artifact: newArtifact,
                OriginalBytes: originalBytes,
                ChunkCount: chunkCount,
                CreatedAt: createdAt,
                ContentPreview: contentPreview,
                Keywords: meta.Keywords,
                TermFrequencies: meta.Tf,
                UpdatedAt: DateTimeOffset.UtcNow,
                ChunkEntries: updatedChunks,
                Attributes: meta.Attributes);

            store.RememberEphemeral(key, ephEntry);
            qualifiedName = $"~{key}";
            sinkQualifiedName = qualifiedName;
        }
        else
        {
            var (scope, subject) = store.ParseQualifiedName(name);

            // Check existing entry for versioning + timestamps
            var existingEntries = store.LoadIndex(scope);
            var existingEntry = existingEntries.FirstOrDefault(e => e.Name == subject);
            DateTimeOffset createdAt = existingEntry?.CreatedAt ?? DateTimeOffset.UtcNow;

            // Merge 3 Task 2b: new description wins; otherwise preserve the existing entry's
            // ContentPreview (which now carries either a prior curated description or the prior
            // auto preview) rather than regenerating from content — that would drop a curated
            // description on a no-op append. See Append_PreservesCuratedAnchorsTagsAndDescription.
            string contentPreview = !string.IsNullOrWhiteSpace(description)
                ? description
                : (existingEntry?.ContentPreview ?? ContentPreview.Generate(fullText));

            // meta.Description is unused post-Merge-3 (the legacy Description member it fed is
            // removed); ContentPreview (computed above) is the surviving display carrier.
            var meta = BuildAppendMetadata(
                fullText, description, tags, plainNewKeywords, anchors,
                existingEntry?.ContentPreview, AttributeReaders.DisplayTags(existingEntry?.Attributes).ToArray(),
                existingEntry?.Keywords, AttributeReaders.DisplayAnchors(existingEntry?.Attributes).ToArray(),
                content, routedNewAttributes, existingEntry?.Attributes, attributes);

            ChunkEntry[]? existingChunks = existingEntry?.ChunkEntries;
            ChunkEntry[] updatedChunks = existingChunks is not null
                ? [.. existingChunks, newChunkEntry]
                : [newChunkEntry];

            // Archive previous version
            if (existingEntry is not null)
                store.ArchiveVersion(subject, scope);

            // Dual-write gap: BuildAppendMetadata already folds tag/anchor into meta.Attributes, but
            // review/file aren't available there — they come from existingEntry, which is only a
            // local at this construction site. Fold them here so Attributes carries the full
            // legacy carrier set even though tags/anchors are already in meta.Attributes (passed null
            // below to avoid double-folding them).
            //
            // Hoisted above the write (was after it), for the same reason remember()'s carrier fold
            // was: remotely, WriteArtifactAsync re-runs the server's whole store path and replaces
            // the entry with server-extracted attributes only, and the Upsert below is a no-op — so
            // this map has to travel WITH the write or it is destroyed by it. Measured before the
            // hoist: an append to a memory holding tag/anchor/person left only the freshly extracted
            // mention values behind. Nothing in this block reads a value produced by the write: it
            // reads existingEntry (loaded above) and meta (built above) and nothing else, and
            // `uri`/`qualifiedName` — the only post-write locals — are not referenced here.
            AttributeReaders.TryReadReview(existingEntry?.Attributes, out var existingReviewDate, out var existingReviewWhen);
            var carrier = AttributeMigration.BuildCarrierAttributes(
                tags: null, existingEntry?.FileHashes?.Keys, existingReviewDate,
                existingReviewWhen, anchors: null);
            var appendAttrs = carrier.Count > 0 ? KnownAttributes.Merge(meta.Attributes, carrier) : meta.Attributes;

            await store.WriteArtifactAsync(subject, scope, newArtifact,
                new ArtifactWriteMetadata(
                    Attributes: appendAttrs,
                    Keywords: meta.Keywords,
                    Description: contentPreview),
                cancellationToken);

            string uri = store.ArtifactUri(subject, scope);
            qualifiedName = store.FormatQualifiedName(scope, subject);
            sinkQualifiedName = store.FormatQualifiedName(scope, subject);

            var entry = new ArtifactEntry(
                Name: subject,
                Uri: uri,
                OriginalBytes: originalBytes,
                ChunkCount: chunkCount,
                CreatedAt: createdAt,
                ContentPreview: contentPreview,
                Keywords: meta.Keywords,
                TermFrequencies: meta.Tf,
                UpdatedAt: DateTimeOffset.UtcNow,
                ChunkEntries: updatedChunks,
                FileHashes: existingEntry?.FileHashes,
                Attributes: appendAttrs);
            entry = AttributeMigration.Forward(entry);

            store.Upsert(entry, scope);
        }

        // Fire event sink (embeddings, etc.) — fire-and-forget so embedding latency
        // never blocks the response. Failure logged to stderr; the artifact is already on disk.
        // CancellationToken.None — see the ephemeral path above for rationale.
        FireEventSinkAsync(sink => sink.OnAppendedAsync(sinkQualifiedName, content, store, CancellationToken.None));

        return ResponseBuilder.Success($"Appended chunk {chunkCount} to {qualifiedName} ({chunkCount} {(chunkCount == 1 ? "chunk" : "chunks")}, {FormatBytes(originalBytes)}).")
            .WithFileChanges().WithAction("appended");
    }

    /// <summary>
    /// Computes the entry-level metadata for an append: re-indexes auto-keywords + term
    /// frequencies over the full post-append text, then merges the memory's existing curated
    /// metadata with any caller-supplied additions. This is the carry-forward that keeps
    /// <c>append</c> from discarding what <c>remember</c> set — the appended content is
    /// re-indexed, but anchors, tags, description, and curated keywords survive and grow.
    /// The returned <c>Description</c> is unused post-Merge-3 (it fed the now-removed legacy
    /// Description member); <paramref name="existingDescription"/> is sourced from the caller's
    /// <c>ContentPreview</c>, the surviving curated-description carrier.
    /// </summary>
    private (string Description, string[]? Tags, string[]? Keywords, Dictionary<string, int>? Tf, string[]? Anchors, Dictionary<string, string[]>? Attributes)
        BuildAppendMetadata(
            string fullText,
            string? newDescription, string[]? newTags, string[]? newKeywords, string[]? newAnchors,
            string? existingDescription, string[]? existingTags, string[]? existingKeywords, string[]? existingAnchors,
            string appendedContent, Dictionary<string, string[]>? routedNewAttributes, Dictionary<string, string[]>? existingAttributes,
            IReadOnlyDictionary<string, string[]>? attributes)
    {
        var (autoKeywords, tf) = TextAnalysis.AnalyzeText(fullText);

        // Existing curated keywords + caller additions are treated as "agent" terms so they
        // survive and keep the +5 TF boost; auto-extracted terms from the full text fill the
        // rest at +2. (Previously-auto keywords folded into the curated set get the agent
        // boost too — a negligible, intentional over-weight in exchange for never losing a
        // curated term.) Mirrors Store's keyword/TF contract.
        // Ref/file references are no longer concatenated in here as prefixed keywords —
        // they land in Attributes via the merge below. Dropping the concat here is intentional:
        // entry-level keywords no longer carry file:/ref: entries.
        string[] curatedKeywords = UnionDistinct(existingKeywords, newKeywords);
        var (mergedKeywords, agentKeywordSet) = TextAnalysis.MergeKeywordsWithSource(
            curatedKeywords.Length > 0 ? curatedKeywords : null, autoKeywords);
        foreach (string kw in mergedKeywords)
        {
            int boost = agentKeywordSet.Contains(kw) ? 5 : 2;
            foreach (string token in TextAnalysis.IndexTokensForKeyword(kw))
            {
                tf.TryGetValue(token, out int count);
                tf[token] = count + boost;
            }
        }

        // Anchors: union of existing + new (whitespace-dropped, deduped). Seed anchor tokens
        // into TF at +2 so BM25 picks up anchor vocabulary not present in the content —
        // same contract as Store.
        string[]? mergedAnchors = MergeAnchors(existingAnchors, newAnchors);
        if (mergedAnchors is not null)
        {
            foreach (string anchor in mergedAnchors)
                foreach (string token in TextAnalysis.Tokenize(anchor))
                {
                    tf.TryGetValue(token, out int count);
                    tf[token] = count + 2;
                }
        }

        string[] mergedTags = UnionDistinct(existingTags, newTags);

        // Explicit new description wins; otherwise preserve the existing one; the content
        // slice is only reached when neither the memory nor the caller supplied a description.
        string desc = !string.IsNullOrWhiteSpace(newDescription)
            ? newDescription!
            : !string.IsNullOrWhiteSpace(existingDescription)
                ? existingDescription!
                : fullText[..Math.Min(200, fullText.Length)];

        // Unified attributes: incoming = routed new keywords ∪ content extraction ∪ references
        // from the appended chunk; merged per KnownAttributes (entity/ref union, scalar
        // newer-wins). Seed the merged set at +3 — TF is rebuilt from scratch per append,
        // so carried-forward attributes must re-seed (same contract as merged anchors).
        var incoming = KnownAttributes.Merge(
            KnownAttributes.Merge(routedNewAttributes, AttributeExtractor.Extract(appendedContent)),
            AttributeExtractor.ExtractRefAttributes(appendedContent));
        incoming = KnownAttributes.Merge(incoming, KnownAttributes.Normalize(attributes));   // explicit agent attributes
        var mergedAttributes = KnownAttributes.Merge(existingAttributes, incoming);

        // Dual-write: fold the fully-merged tag/anchor set into Attributes at write time —
        // same mechanism as Store, mirroring AttributeMigration.Forward for the persistent
        // path (Task 1). Uses the already-merged mergedTags/mergedAnchors (existing ∪ new)
        // so Attributes carries the same values that land in Tags/RetrievalAnchors below.
        var tagAnchorIncoming = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (mergedTags.Length > 0) tagAnchorIncoming[KnownAttributes.TagKey] = mergedTags;
        if (mergedAnchors is { Length: > 0 }) tagAnchorIncoming[KnownAttributes.AnchorKey] = mergedAnchors;
        if (tagAnchorIncoming.Count > 0)
            mergedAttributes = KnownAttributes.Merge(mergedAttributes, tagAnchorIncoming);

        AttributeExtractor.SeedTermFrequencies(mergedAttributes, tf);

        return (
            desc,
            mergedTags.Length > 0 ? mergedTags : null,
            mergedKeywords.Length > 0 ? mergedKeywords : null,
            tf.Count > 0 ? tf : null,
            mergedAnchors,
            mergedAttributes);
    }

    /// <summary>Case-insensitive union of two optional string arrays, preserving first-seen casing and order; drops null/whitespace.</summary>
    private static string[] UnionDistinct(string[]? existing, string[]? added) =>
        (existing ?? []).Concat(added ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Merges anchor phrases (existing + new), trimming and deduplicating case-insensitively; null when empty.</summary>
    private static string[]? MergeAnchors(string[]? existing, string[]? added)
    {
        string[] merged = UnionDistinct(existing, added);
        return merged.Length > 0 ? merged : null;
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
    /// Create a bidirectional relationship between two memories. Writes a unified
    /// <c>ref</c> attribute on each side (via <see cref="UpdateMeta"/>, which routes the
    /// <c>ref:{name}</c>-shaped keyword into <c>Attributes["ref"]</c> instead of a legacy keyword).
    /// </summary>
    internal async Task<McpResponse> Link(
        [Description("Source memory name.")] string from,
        [Description("Target memory name.")] string to,
        [Description("Reason for the connection.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        // Add ref:{to} attribute to {from} (UpdateMeta routes the ref:-shaped keyword into Attributes)
        McpResponse result1 = await UpdateMeta(from, keywords: [$"ref:{to}"], cancellationToken: cancellationToken);
        // Add ref:{from} attribute to {to}
        McpResponse result2 = await UpdateMeta(to, keywords: [$"ref:{from}"], cancellationToken: cancellationToken);

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

    private static Task<McpResponse> BuildDriftList(ILocalMemoryStore store)
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
            string qualName = store.FormatQualifiedName(sa.Scope, sa.Entry.Name);

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
            return Task.FromResult(ResponseBuilder.Success(msg).WithAction("listed"));
        }

        string driftResponse = $"Code reference drift detected ({driftCount} drifted, {missingCount} missing, {okCount} ok):\n" +
            string.Join("\n", results);
        var driftWarnings = new List<string>();
        if (driftCount > 0) driftWarnings.Add($"{driftCount} code reference(s) have drifted (files changed since stored).");
        if (missingCount > 0) driftWarnings.Add($"{missingCount} code reference(s) point to missing files.");
        return Task.FromResult(
            ResponseBuilder.Success(driftResponse).WithAction("listed").WithActionNeeded(driftWarnings.ToArray()));
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
