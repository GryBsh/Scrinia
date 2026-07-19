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
    /// <summary>Unpack an NMP/2 artifact back to its original text content.</summary>
    internal async Task<string> Show(
        [Description("The NMP/2 artifact text, or a memory name to resolve. " +
                     "Use the exact name shown by list (e.g. 'session-notes', 'api:auth-flow', '~scratch').")] string artifactOrName,
        [Description("Optional 1-based chunk index to retrieve a specific chunk.")] int? chunk = null,
        string actionLabel = "shown",
        CancellationToken cancellationToken = default)
    {
        // ── Agent config: read .md file first, NMP/2 fallback ────────────
        if (artifactOrName.StartsWith("agent:", StringComparison.OrdinalIgnoreCase))
        {
            var agentStore = MemoryStoreContext.Current;
            if (agentStore is not null)
            {
                string agentSubject = artifactOrName["agent:".Length..].Trim();
                string agentBaseDir = GetScriniaBaseDir(agentStore);
                string agentFilePath = Path.Combine(agentBaseDir, "agent", $"{agentSubject}.md");
                if (File.Exists(agentFilePath))
                {
                    string mdContent = await File.ReadAllTextAsync(agentFilePath, cancellationToken);
                    SessionBudget.RecordAccess(artifactOrName, mdContent.Length);
                    return ResponseBuilder.Success(mdContent).WithAction(actionLabel).ToYaml();
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
                    "search({ query: '...' }) to find by keyword").ToYaml();

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
                    "search({ query: '...' }) to find by keyword").ToYaml();
            }
        }

        if (!artifact.TrimStart().StartsWith("NMP/2 ", StringComparison.Ordinal))
            return ResponseBuilder.Error(
                "Only NMP/2 artifacts are supported by this tool.",
                ErrorCodes.InvalidParameter,
                "Provide a memory name or an artifact whose header begins with 'NMP/2 '.").ToYaml();

        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);

        if (chunk is not null)
        {
            string chunkContent = Nmp2ChunkedEncoder.DecodeChunk(artifact, chunk.Value);
            SessionBudget.RecordAccess(artifactOrName, chunkContent.Length);
            return ResponseBuilder.Success($"Chunk {chunk}/{chunkCount}\n\n{chunkContent}").WithAction(actionLabel).ToYaml();
        }

        byte[] bytes = Nmp2Strategy.Instance.Decode(artifact);
        string decoded = System.Text.Encoding.UTF8.GetString(bytes);
        SessionBudget.RecordAccess(artifactOrName, decoded.Length);

        if (chunkCount > 1)
            return ResponseBuilder.Success($"({chunkCount} chunks)\n\n{decoded}").WithAction(actionLabel).ToYaml();

        return ResponseBuilder.Success(decoded).WithAction(actionLabel).ToYaml();
    }

    // ── Persistent memory tools ───────────────────────────────────────────────

    /// <summary>Compress text and persist it as a named artifact in a memory scope.</summary>
    internal async Task<string> Store(
        [Description("The text content to compress and store. " +
                     "Pass a single element for a single-chunk artifact, or multiple elements to control " +
                     "chunk boundaries — each element becomes one independently retrievable chunk.")] string[] content,
        [Description("Human-readable name for this artifact (e.g. \"session-notes\", \"my-codebase\"). " +
                     "Invalid filename characters are replaced with '_'. " +
                     "Naming: '/path/to/memory' (v2 path), 'topic:subject' (v1 compat), '/temp/subject' (ephemeral).")] string name,
        [Description("Optional description. If empty, the first 200 characters of content are used.")] string description = "",
        [Description("Optional tags for categorization.")] string[]? tags = null,
        [Description("Optional keywords for search. Merged with auto-extracted content terms. " +
                     "Prefer TYPED keywords using one of these types: 'person:<name>', 'place:<name>', " +
                     "'org:<name>', 'thing:<name>', 'topic:<theme>'. Typed entries, and any other " +
                     "key:value keyword, become structured fields — an exact-match retrieval signal and " +
                     "entity-graph input, so a query mentioning one entity can surface this memory via a " +
                     "related entity even with no text overlap. Plain keywords remain search keywords. " +
                     "Use the same canonical name for an entity across memories; the type lets a person and a " +
                     "place that share a name stay distinct.")] string[]? keywords = null,
        [Description("Optional retrieval anchors — phrases, questions, or category-level paraphrases that bridge likely user queries to this entry's content. Use vocabulary you expect users to query with, not vocabulary already in the content. Indexed for both field-score lookup and BM25 via term-frequency seeding.")]
        string[]? anchors = null,
        [Description("Optional ISO 8601 date after which this memory should be reviewed for staleness.")] string? reviewAfter = null,
        [Description("Optional free-text condition describing when this memory should be reviewed (e.g. 'when auth system changes').")] string? reviewWhen = null,
        [Description("Optional file paths this memory depends on. Hashes are recorded to detect drift.")] string[]? codeRefs = null,
        IReadOnlyDictionary<string, string[]>? attributes = null,
        string actionLabel = "stored",
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;
        string joined = string.Concat(content);

        // Compute text analysis: keywords + term frequencies (single-pass)
        var (autoKeywords, tf) = TextAnalysis.AnalyzeText(joined);

        // Unified fields: key:value-shaped agent keywords route to Fields; only plain
        // keywords participate in the keyword merge. Prefixed-keyword writes are retired.
        var (routedFields, plainKeywords) = FieldExtractor.RouteKeywords(keywords);
        var (mergedKeywords, agentKeywordSet) = TextAnalysis.MergeKeywordsWithSource(plainKeywords, autoKeywords);

        // References land in Fields (file/ref), not as prefixed keywords.
        string rawContent = string.Join("\n", content);
        var refFields = FieldExtractor.ExtractRefFields(rawContent);

        // Boost keywords in TF: agent keywords +5, auto-extracted +2. Typed keywords
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
        // keywords get; the field-score branch (+15) carries the agent-curated boost.
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

        // Assemble unified fields: routed keywords ∪ content extraction ∪ references,
        // then fold in explicit agent attributes (they win scalar collisions), then seed
        // key/value tokens into TF.
        var fields = KnownFields.Merge(KnownFields.Merge(routedFields, FieldExtractor.Extract(joined)), refFields);
        fields = KnownFields.Merge(fields, NormalizeAttributes(attributes));
        FieldExtractor.SeedTermFrequencies(fields, tf);

        ChunkEntry[]? chunkEntries = content.Length > 1
            ? TextAnalysis.ComputeChunkEntries(store, content)
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
                : store.GenerateContentPreview(joined);

            // Check if updating existing ephemeral entry
            var existingEph = store.GetEphemeral(key);
            DateTimeOffset ephCreatedAt = existingEph?.CreatedAt ?? DateTimeOffset.UtcNow;
            DateTimeOffset? ephUpdatedAt = existingEph is not null ? DateTimeOffset.UtcNow : null;

            // Dual-write: fold tag/anchor into Fields at write time, mirroring how
            // AttributeMigration.Forward populates the persistent path's Fields (Task 1).
            // EphemeralEntry carries only Tags/RetrievalAnchors as legacy attribute-fields
            // (no ReviewAfter/ReviewWhen/FileHashes), so only tag/anchor route here.
            var ephIncoming = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (tags is { Length: > 0 }) ephIncoming[KnownFields.TagKey] = tags;
            if (cleanedAnchors is { Length: > 0 }) ephIncoming[KnownFields.AnchorKey] = cleanedAnchors;
            var ephFields = ephIncoming.Count > 0 ? KnownFields.Merge(fields, ephIncoming) : fields;

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
                Attributes: ephFields);

            store.RememberEphemeral(key, ephEntry);

            // Fire event sink (embeddings, etc.) — fire-and-forget so embedding latency
            // never blocks the response. Failure logged to stderr; the artifact is already on disk.
            // CancellationToken.None: the sink runs detached on a background Task —
            // propagating the per-request CT would abort it the moment the user-facing
            // response returns, leaving importance unscored or vectors half-written.
            FireEventSinkAsync(sink => sink.OnStoredAsync($"~{key}", content, store, CancellationToken.None));

            return ResponseBuilder.Success($"Remembered: ~{key} ({ephChunkCount} {(ephChunkCount == 1 ? "chunk" : "chunks")}, {FormatBytes(ephBytes)}) [ephemeral]")
                .WithAction(actionLabel).ToYaml();
        }

        // ── Agent config path (agent:* → .scrinia/agent/{name}.md) ──────
        if (name.StartsWith("agent:", StringComparison.OrdinalIgnoreCase))
        {
            string agentSubject = name["agent:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(agentSubject))
                return ResponseBuilder.Error(
                    "Agent config name required (e.g. 'agent:profile').",
                    ErrorCodes.InvalidParameter,
                    "Use a path like 'agent:profile' for the agent config.").ToYaml();

            string baseDir = GetScriniaBaseDir(store);
            string agentDir = Path.Combine(baseDir, "agent");
            string filePath = Path.Combine(agentDir, $"{agentSubject}.md");
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
                .WithFileChanges().WithAction(actionLabel).ToYaml();
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

        await store.WriteArtifactAsync(subject, scope, artifact, cancellationToken);

        string uri = store.ArtifactUri(subject, scope);

        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);
        long originalBytes = System.Text.Encoding.UTF8.GetByteCount(joined);
        // Merge 3 Task 2b: an explicit curated description becomes the displayed
        // ContentPreview (option A) — it wins over the auto content snippet.
        string contentPreview = !string.IsNullOrWhiteSpace(description)
            ? description
            : store.GenerateContentPreview(joined);
        string qualifiedName = store.FormatQualifiedName(scope, subject);

        // New model: the `review` attribute carries review dates and/or free-text conditions.
        // Feed date-parseable values into the existing reviewAfter parse (first wins); join the
        // rest into reviewWhen. Only when the legacy params weren't explicitly supplied.
        if (reviewAfter is null && reviewWhen is null
            && fields is not null && fields.TryGetValue(KnownFields.ReviewKey, out var reviewVals))
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

        // Compute code reference hashes — explicit codeRefs + auto-detected file field values
        string storeDir = store.GetStoreDirForScope("local");
        string scriniaDir = Path.GetDirectoryName(storeDir) ?? storeDir;
        string workspaceRoot = Path.GetDirectoryName(scriniaDir) ?? scriniaDir;

        var allRefPaths = (codeRefs ?? [])
            .Concat(fields is not null && fields.TryGetValue("file", out var fileRefs)
                ? fileRefs : [])
            .Distinct(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, string>? codeRefDict = null;
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

        // Dual-write: fold tag/anchor/review/file into Fields at write time — the attributes-native
        // write path (byte-identical to AttributeMigration.Forward's fold, Task 1). Inserted here,
        // immediately before construction, so it captures the auto-derived reviewWhen (above) and
        // the completed codeRefDict (above) — inserting any earlier would fold stale/empty values.
        var carrier = AttributeMigration.BuildCarrierAttributes(
            tags, codeRefDict?.Keys, parsedReviewAfter,
            string.IsNullOrWhiteSpace(reviewWhen) ? null : reviewWhen, cleanedAnchors);
        if (carrier.Count > 0)
            fields = KnownFields.Merge(fields, carrier);

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
            Attributes: fields);
        entry = AttributeMigration.Forward(entry);

        store.Upsert(entry, scope);

        // Fire event sink (embeddings, etc.) — fire-and-forget so embedding latency
        // never blocks the response. Failure logged to stderr; the artifact is already on disk.
        // CancellationToken.None — see the ephemeral path above for rationale.
        // Sink gets the round-trip-safe name (not the display-formatted `qualifiedName`): the
        // sink re-parses this to decide which scope to embed under, and FormatQualifiedName's
        // display form strips the "memory/"/"entity/" namespace prefix that would reparse to
        // the wrong scope for any v1-namespaced topic.
        string sinkQualifiedName = store.FormatRoundTripQualifiedName(scope, subject);
        FireEventSinkAsync(sink => sink.OnStoredAsync(sinkQualifiedName, content, store, CancellationToken.None));

        return ResponseBuilder.Success($"Remembered: {qualifiedName} ({chunkCount} {(chunkCount == 1 ? "chunk" : "chunks")}, {FormatBytes(originalBytes)}).")
            .WithFileChanges().WithAction(actionLabel).ToYaml();
    }

    /// <summary>Update metadata on an existing memory without re-encoding its content.</summary>
    internal Task<string> UpdateMeta(
        [Description("Memory name (e.g. 'api:auth-flow', 'session-notes').")] string name,
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
                "remember({ path: '...', content: [...] }) — overwrites an existing ephemeral entry").ToYaml());

        var (scope, subject) = store.ParseQualifiedName(name);

        var entries = store.LoadIndex(scope);
        var entry = entries.FirstOrDefault(e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            string qualName = store.FormatQualifiedName(scope, subject);
            return Task.FromResult(ResponseBuilder.Error(
                $"Memory '{qualName}' not found.",
                ErrorCodes.NotFound,
                "list() to see available memories",
                "remember({ path: '...', content: [...] }) to create it").ToYaml());
        }

        // Track what changed for the response message
        var changes = new List<string>();

        // Route key:value-shaped additions into Fields; only plain ones merge into Keywords.
        var (routedFields, plainAdds) = FieldExtractor.RouteKeywords(keywords);

        string[]? mergedKeywords = entry.Keywords;
        int addedCount = 0;
        if (plainAdds is { Length: > 0 })
        {
            var existing = entry.Keywords ?? [];
            mergedKeywords = existing.Union(plainAdds, StringComparer.OrdinalIgnoreCase).ToArray();
            addedCount = mergedKeywords.Length - existing.Length;
            if (addedCount > 0) changes.Add($"{addedCount} keyword(s) added");
        }

        Dictionary<string, string[]>? mergedFields = entry.Attributes;
        if (routedFields is not null)
        {
            mergedFields = KnownFields.Merge(entry.Attributes, routedFields);
            changes.Add($"{routedFields.Count} field(s) merged");
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

        if (changes.Count == 0)
            return Task.FromResult(ResponseBuilder.Warning("No changes specified. Provide at least one of: keywords, description, reviewAfter, reviewWhen.").ToYaml());

        // Attributes-native direct write (dual-write with the legacy ReviewAfter/ReviewWhen
        // members below + the Forward call at the end of this method). ADD/REPLACE ONLY — no
        // Remove(ReviewKey) — matches AttributeMigration.Forward's incoming-only fold: when
        // neither reviewAfter nor reviewWhen is present, Forward's `incoming` carries no "review"
        // key, so KnownFields.Merge PRESERVES any existing Attributes["review"] rather than
        // clearing it. KnownFields.Merge always returns a NEW dictionary, so this never mutates
        // entry.Attributes (== mergedFields when routedFields is null) in place.
        var reviewParts = new List<string>(2);
        if (parsedReviewAfter is { } ra2) reviewParts.Add(ra2.ToString("o", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(updatedReviewWhen)) reviewParts.Add(updatedReviewWhen);
        if (reviewParts.Count > 0)
            mergedFields = KnownFields.Merge(mergedFields,
                new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [KnownFields.ReviewKey] = reviewParts.ToArray() });

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

        if (routedFields is not null)
        {
            var tfForFields = updatedTf is not null
                ? new Dictionary<string, int>(updatedTf) : new Dictionary<string, int>();
            // Agent-curated fields seed at +5, matching agent keywords (not the +3 auto rate).
            foreach (var kv in routedFields)
                foreach (string v in kv.Value)
                    foreach (string token in TextAnalysis.Tokenize(v))
                    {
                        tfForFields.TryGetValue(token, out int c);
                        tfForFields[token] = c + 5;
                    }
            updatedTf = tfForFields;
        }

        var updated = entry with
        {
            Keywords = mergedKeywords,
            TermFrequencies = updatedTf,
            Attributes = mergedFields,
            ContentPreview = updatedContentPreview,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        updated = AttributeMigration.Forward(updated);

        store.Upsert(updated, scope);

        string qualifiedName = store.FormatQualifiedName(scope, subject);
        return Task.FromResult(
            ResponseBuilder.Success($"Updated metadata for '{qualifiedName}': {string.Join(", ", changes)}.")
                .WithFileChanges().WithAction("updated").ToYaml());
    }

    /// <summary>Returns a summary or full listing of persisted memories.</summary>
    internal Task<string> List(
        [Description("Optional comma-separated scope order, e.g. local,api,ephemeral. " +
                     "Topic names filter to local topics (e.g. 'api' shows api topic entries).")] string? scopes = null,
        [Description("'summary' (default), 'full' for paginated table, 'drift' for code reference drift check.")] string mode = "summary",
        [Description("Starting index for full mode (0-based). Ignored in summary mode.")] int offset = 0,
        [Description("Maximum entries to return in full mode (default 50). Ignored in summary mode.")] int limit = 50,
        [Description("Optional comma-separated topic names to exclude from results. " +
                     "Use 'plan,task,project,learn' to hide planning namespaces from knowledge listings.")] string? excludeTopics = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        if (string.Equals(mode, "drift", StringComparison.OrdinalIgnoreCase))
            return BuildDriftList(store);

        List<ScopedArtifact> entries = store.ListScoped(scopes, excludeTopics);
        if (entries.Count == 0)
            return Task.FromResult(ResponseBuilder.Success("No memories stored.").WithAction("listed").ToYaml());

        entries.Sort((a, b) => b.Entry.CreatedAt.CompareTo(a.Entry.CreatedAt));

        if (!string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ResponseBuilder.Success(BuildSummary(entries, store)).WithAction("listed").ToYaml());

        return Task.FromResult(ResponseBuilder.Success(BuildFullList(entries, store, offset, limit)).WithAction("listed").ToYaml());
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

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Memory Summary");
        sb.AppendLine($"**{entries.Count} memories** — {FormatBytes(totalBytes)} (~{totalTokens:N0} tokens)");
        if (topicCount > 0 || ephemeralCount > 0 || staleCount > 0 || reviewCount > 0)
        {
            var parts = new List<string>();
            if (topicCount > 0) parts.Add($"{topicCount} topic{(topicCount == 1 ? "" : "s")}");
            if (ephemeralCount > 0) parts.Add($"{ephemeralCount} ephemeral");
            if (staleCount > 0) parts.Add($"{staleCount} stale");
            if (reviewCount > 0) parts.Add($"{reviewCount} need review");
            sb.AppendLine(string.Join(" · ", parts));
        }
        sb.AppendLine();

        // Topics with entry counts and total size
        sb.AppendLine("### Scopes");
        foreach (var group in grouped)
        {
            string label = group.Key == "local" ? "local" : $"topic:{group.Key}";
            long groupBytes = group.Sum(e => e.Entry.OriginalBytes);
            sb.AppendLine($"- **{label}** — {group.Count()} {(group.Count() == 1 ? "memory" : "memories")}, {FormatBytes(groupBytes)}");
        }
        if (ephemeralCount > 0)
            sb.AppendLine($"- **ephemeral** — {ephemeralCount} {(ephemeralCount == 1 ? "memory" : "memories")}");
        sb.AppendLine();

        // Top keywords — aggregate from Keywords and Tags across all entries
        var keywordCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in entries)
        {
            if (item.Entry.Keywords is { Length: > 0 })
                foreach (var kw in item.Entry.Keywords)
                    keywordCounts[kw] = keywordCounts.GetValueOrDefault(kw) + 1;
            foreach (var tag in AttributeReaders.DisplayTags(item.Entry.Attributes))
                keywordCounts[tag] = keywordCounts.GetValueOrDefault(tag) + 1;
        }
        if (keywordCounts.Count > 0)
        {
            var topKeywords = keywordCounts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .Select(kv => kv.Key);
            sb.AppendLine($"### Top keywords");
            sb.AppendLine(string.Join(", ", topKeywords));
            sb.AppendLine();
        }

        // Top attributes — aggregate attribute values across all entries (new model's facets).
        var attrCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in entries)
        {
            if (item.Entry.Attributes is { Count: > 0 })
                foreach (var kv in item.Entry.Attributes)
                    foreach (var v in kv.Value)
                        attrCounts[$"{kv.Key}:{v}"] = attrCounts.GetValueOrDefault($"{kv.Key}:{v}") + 1;
        }
        if (attrCounts.Count > 0)
        {
            var topAttrs = attrCounts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .Select(kv => kv.Key);
            sb.AppendLine("### Top attributes");
            sb.AppendLine(string.Join(", ", topAttrs));
            sb.AppendLine();
        }

        sb.Append("Use `list({ mode: 'full' })` to see all entries, or `search({ query: '...' })` to find specific memories.");
        return sb.ToString();
    }

    internal static string BuildFullList(List<ScopedArtifact> entries, IMemoryStore store, int offset, int limit)
    {
        int total = entries.Count;
        if (offset < 0) offset = 0;
        if (limit < 1) limit = 50;
        var page = entries.Skip(offset).Take(limit).ToList();

        // Derive workspace root for drift checking (only used if any entry has FileHashes)
        string? workspaceRoot = null;
        bool anyCodeRefs = page.Any(p => p.Entry.FileHashes is { Count: > 0 });
        if (anyCodeRefs)
        {
            string sd = store.GetStoreDirForScope("local");
            string sd2 = Path.GetDirectoryName(sd) ?? sd;
            workspaceRoot = Path.GetDirectoryName(sd2) ?? sd2;
        }

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

        var sb = new System.Text.StringBuilder();

        // Pagination header
        int showing = offset + 1;
        int showingEnd = offset + page.Count;
        sb.AppendLine($"Showing {showing}-{showingEnd} of {total} memories.");
        sb.AppendLine();

        sb.AppendLine(
            $"{"name".PadRight(nameW)}  {"chunks",chunkW}  {"bytes",bytesW}  {"~tokens",tokensW}  {"created",dateW}  description");
        sb.AppendLine(new string('-', nameW + chunkW + bytesW + tokensW + dateW + 18));

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

            sb.AppendLine(
                $"{qualifiedName.PadRight(nameW)}  {e.ChunkCount,chunkW}  {sizeStr,bytesW}  {estTokens,tokensW}  {dateStr,-dateW}  {fullDesc}");
        }

        if (showingEnd < total)
            sb.AppendLine($"\nUse list(mode=\"full\", offset={showingEnd}) for more.");

        return sb.ToString().TrimEnd();
    }

    /// <summary>Search memories across local and topic scopes using a query.</summary>
    internal async Task<string> Search(
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

        // Augment the query with caller-supplied context for scoring. The bare query is preserved
        // for any user-facing echo; the effective query feeds BM25, field scoring, and embeddings
        // alike so a single signal carries through all three.
        string effectiveQuery = string.IsNullOrWhiteSpace(context) ? query : $"{query} {context}";

        // Compute supplemental (e.g. embeddings) and entity-graph scores from plugins if available.
        // Both run over the same excludeTopics-filtered candidates so excluded topics don't
        // influence scoring. The entity-graph signal is a separate fusion source, not summed
        // into supplemental — see EntityGraphScoreContributor.
        var contributor = SearchContributorContext.Current;
        var entityContributor = EntityGraphScoreContext.Current;
        IReadOnlyDictionary<string, double>? supplemental = null;
        IReadOnlyDictionary<string, double>? entityScores = null;
        if (contributor is not null || entityContributor is not null)
        {
            var candidates = store.ListScoped(scopes, excludeTopics);
            if (contributor is not null)
                supplemental = await contributor.ComputeScoresAsync(effectiveQuery, candidates, store, cancellationToken);
            if (entityContributor is not null)
                entityScores = await entityContributor.ComputeScoresAsync(effectiveQuery, candidates, store, cancellationToken);
        }

        // excludeTopics must be applied BEFORE the top-K limit cut in both branches — passing it
        // straight into SearchAll (rather than post-filtering the already-limited result set)
        // ensures excluded-topic hits never consume result slots.
        IReadOnlyList<SearchResult> matches = supplemental is { Count: > 0 }
            ? store.SearchAll(effectiveQuery, scopes, limit, excludeTopics, supplemental, entityScores)
            : store.SearchAll(effectiveQuery, scopes, limit, excludeTopics, entityScores);

        // Post-filter by path prefix and attribute filter. Both are AND filters — the candidate
        // must satisfy every active constraint. Filters apply uniformly to entry, chunk, and
        // topic results so callers don't need to know which result type to expect.
        matches = ApplyPostFilters(store, matches, pathPrefix, attributeFilter);

        if (matches.Count == 0)
            return ResponseBuilder.Success("No matching memories found.").WithAction("searched").ToYaml();

        string body = FormatRichSearchResults(store, matches);
        return ResponseBuilder.Success(body).WithAction("searched").ToYaml();
    }

    private static IReadOnlyList<SearchResult> ApplyPostFilters(
        IMemoryStore store,
        IReadOnlyList<SearchResult> matches,
        string? pathPrefix,
        IReadOnlyDictionary<string, string[]>? attributeFilter)
    {
        if (string.IsNullOrWhiteSpace(pathPrefix) && (attributeFilter is null || attributeFilter.Count == 0))
            return matches;

        var kept = new List<SearchResult>(matches.Count);
        foreach (var match in matches)
        {
            if (!MatchesPathPrefix(store, match, pathPrefix)) continue;
            if (!MatchesAttributes(match, attributeFilter)) continue;
            kept.Add(match);
        }
        return kept;
    }

    private static bool MatchesPathPrefix(IMemoryStore store, SearchResult match, string? pathPrefix)
    {
        if (string.IsNullOrWhiteSpace(pathPrefix)) return true;
        string label = match switch
        {
            EntryResult er => er.Item.Scope == "ephemeral"
                ? $"~{er.Item.Entry.Name}"
                : store.FormatQualifiedName(er.Item.Scope, er.Item.Entry.Name),
            ChunkEntryResult cr => cr.ParentItem.Scope == "ephemeral"
                ? $"~{cr.ParentItem.Entry.Name}"
                : store.FormatQualifiedName(cr.ParentItem.Scope, cr.ParentItem.Entry.Name),
            TopicResult tr => MemoryNaming.FormatScopeLabel(tr.Scope),
            _ => string.Empty,
        };
        return label.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesAttributes(SearchResult match, IReadOnlyDictionary<string, string[]>? filter)
    {
        if (filter is null || filter.Count == 0) return true;
        Dictionary<string, string[]>? fields = match switch
        {
            EntryResult er => er.Item.Entry.Attributes,
            ChunkEntryResult cr => cr.ParentItem.Entry.Attributes,
            _ => null,   // TopicResult carries no attributes — excluded when a filter is active
        };
        if (fields is null || fields.Count == 0) return false;
        foreach (var want in filter)
        {
            if (!fields.TryGetValue(want.Key.Trim().ToLowerInvariant(), out var have) || have is null || have.Length == 0)
                return false;
            if (want.Value is { Length: > 0 })
            {
                bool any = false;
                foreach (string wv in want.Value)
                {
                    foreach (string hv in have)
                        if (string.Equals(hv, wv, StringComparison.OrdinalIgnoreCase)) { any = true; break; }
                    if (any) break;
                }
                if (!any) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Renders one block per match with the persisted entry fields surfaced. The agent
    /// gets enough decision-making context per hit — description, tags, retrieval
    /// anchors — to either answer directly or pick which hits warrant a recall,
    /// without a second round trip per candidate.
    /// </summary>
    /// <remarks>
    /// Block layout (inside the YAML envelope's <c>content: |</c> body):
    /// <code>
    /// [1] entry  /findings/auth-refactor  score=47  ~380 tokens
    ///     tags: security, refactor
    ///     description: Multi-line description verbatim.
    ///     anchors:
    ///       - what is the auth refactor?
    ///       - how does session rotation work?
    /// </code>
    /// Description is sanity-capped at <see cref="MaxDescriptionChars"/> chars to bound
    /// response size; anchors is capped at <see cref="MaxAnchorsPerResult"/>. Tags omitted
    /// when empty; anchors section omitted when null/empty.
    /// </remarks>
    private static string FormatRichSearchResults(IMemoryStore store, IReadOnlyList<SearchResult> matches)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("results: ").Append(matches.Count).Append('\n');

        for (int i = 0; i < matches.Count; i++)
        {
            sb.Append('\n');
            AppendResultBlock(sb, store, matches[i], i + 1);
        }

        return sb.ToString().TrimEnd();
    }

    private const int MaxDescriptionChars = 1500;
    private const int MaxAnchorsPerResult = 7;

    internal static void AppendResultBlock(System.Text.StringBuilder sb, IMemoryStore store, SearchResult match, int index)
    {
        switch (match)
        {
            case EntryResult er:
                {
                    string label = er.Item.Scope == "ephemeral"
                        ? $"~{er.Item.Entry.Name}"
                        : store.FormatQualifiedName(er.Item.Scope, er.Item.Entry.Name);
                    int estTokens = (int)(er.Item.Entry.OriginalBytes / 4);
                    sb.Append('[').Append(index).Append("] entry  ").Append(label)
                      .Append("  score=").Append(er.Score.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))
                      .Append("  ~").Append(estTokens).Append(" tokens\n");
                    AppendDatesLine(sb, er.Item.Entry.CreatedAt, er.Item.Entry.UpdatedAt);
                    AppendAttributesLine(sb, er.Item.Entry.Attributes);
                    AppendDescription(sb, AttributeReaders.PreviewText(er.Item.Entry.ContentPreview));
                    AppendAnchors(sb, AttributeReaders.DisplayAnchors(er.Item.Entry.Attributes));
                    break;
                }
            case ChunkEntryResult cr:
                {
                    string parentLabel = cr.ParentItem.Scope == "ephemeral"
                        ? $"~{cr.ParentItem.Entry.Name}"
                        : store.FormatQualifiedName(cr.ParentItem.Scope, cr.ParentItem.Entry.Name);
                    int estTokens = cr.TotalChunks > 0
                        ? (int)(cr.ParentItem.Entry.OriginalBytes / cr.TotalChunks / 4)
                        : 0;
                    sb.Append('[').Append(index).Append("] chunk  ").Append(parentLabel)
                      .Append(" [chunk ").Append(cr.Chunk.ChunkIndex).Append('/').Append(cr.TotalChunks).Append(']')
                      .Append("  score=").Append(cr.Score.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))
                      .Append("  ~").Append(estTokens).Append(" tokens\n");
                    AppendDatesLine(sb, cr.ParentItem.Entry.CreatedAt, cr.ParentItem.Entry.UpdatedAt);
                    AppendAttributesLine(sb, cr.ParentItem.Entry.Attributes);
                    AppendDescription(sb, cr.Chunk.ContentPreview
                        ?? AttributeReaders.PreviewText(cr.ParentItem.Entry.ContentPreview));
                    AppendAnchors(sb, AttributeReaders.DisplayAnchors(cr.ParentItem.Entry.Attributes));
                    break;
                }
            case TopicResult tr:
                {
                    string label = MemoryNaming.FormatScopeLabel(tr.Scope);
                    sb.Append('[').Append(index).Append("] topic  ").Append(label)
                      .Append("  score=").Append(tr.Score.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))
                      .Append("  (").Append(tr.EntryCount).Append(" entries)\n");
                    AppendTagsLine(sb, tr.Tags);
                    AppendDescription(sb, tr.Description);
                    break;
                }
        }
    }

    private static void AppendTagsLine(System.Text.StringBuilder sb, string[]? tags)
    {
        if (tags is null || tags.Length == 0) return;
        sb.Append("    tags: ");
        for (int i = 0; i < tags.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(tags[i]);
        }
        sb.Append('\n');
    }

    /// <summary>Render record dates as display-only pseudo-attributes from the entry's structural
    /// CreatedAt/UpdatedAt — never stored in Attributes, never agent-writable. Date-only, ISO.</summary>
    private static void AppendDatesLine(System.Text.StringBuilder sb, DateTimeOffset createdAt, DateTimeOffset? updatedAt)
    {
        sb.Append("    created: ").Append(createdAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        if (updatedAt is { } u)
            sb.Append("    updated: ").Append(u.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
    }

    private static void AppendAttributesLine(System.Text.StringBuilder sb, Dictionary<string, string[]>? fields)
    {
        if (fields is null || fields.Count == 0) return;
        sb.Append("    attributes:\n");
        foreach (var kv in fields)
        {
            sb.Append("      ").Append(kv.Key).Append(": ");
            sb.Append(string.Join(", ", kv.Value)).Append('\n');
        }
    }

    private static void AppendDescription(System.Text.StringBuilder sb, string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return;

        string text = description;
        bool truncated = false;
        if (text.Length > MaxDescriptionChars)
        {
            text = text[..MaxDescriptionChars];
            truncated = true;
        }

        // Inline form when the description fits on one line — easier to scan. Multi-line
        // descriptions fall back to an indented block whose continuation lines stay aligned
        // under the block header so visual grouping survives the outer YAML's content: |
        // re-indentation.
        if (!text.Contains('\n') && !text.Contains('\r'))
        {
            sb.Append("    description: ").Append(text);
            if (truncated) sb.Append(" (truncated)");
            sb.Append('\n');
            return;
        }

        sb.Append("    description: |\n");
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            sb.Append("      ").Append(line).Append('\n');
        }
        if (truncated) sb.Append("      (truncated)\n");
    }

    internal static void AppendAnchors(System.Text.StringBuilder sb, IReadOnlyList<string> anchors)
    {
        if (anchors.Count == 0) return;
        sb.Append("    anchors:\n");
        int emit = Math.Min(anchors.Count, MaxAnchorsPerResult);
        for (int i = 0; i < emit; i++)
        {
            sb.Append("      - ").Append(anchors[i].Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
        }
        if (anchors.Count > emit)
        {
            sb.Append("      - (+").Append(anchors.Count - emit).Append(" more)\n");
        }
    }

    /// <summary>Copies a memory artifact from one scope to another.</summary>
    internal Task<string> Copy(
        [Description("Memory name or file:// URI to copy.")] string nameOrUri,
        [Description("Destination as qualified name (e.g. 'api:auth-flow' or 'my-notes'). " +
                     "Use '~name' for ephemeral destination.")] string destination,
        [Description("When true, replaces destination memory if it already exists.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        bool ok = CurrentStore.CopyMemory(nameOrUri, destination, overwrite, out string msg);
        if (!ok) return Task.FromResult(ResponseBuilder.Error(msg, ErrorCodes.Internal).ToYaml());
        return Task.FromResult(ResponseBuilder.Success(msg).WithAction("copied").ToYaml());
    }

    /// <summary>Removes a stored artifact and its index entry.</summary>
    internal async Task<string> Forget(
        [Description("The artifact name (e.g. \"session-notes\", \"api:auth\", \"/temp/scratch\") or its file:// URI.")] string nameOrUri,
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
                    "list() to see ephemeral memories currently held in process").ToYaml();

            try { await (MemoryEventSinkContext.Current?.OnForgottenAsync($"~{key}", true, store, cancellationToken) ?? Task.CompletedTask); }
            catch { /* plugin errors must not block forget */ }

            return ResponseBuilder.Success($"Forgot: ~{key}").WithAction("forgotten").ToYaml();
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
                    "list() to see available memories").ToYaml();

            var sink = MemoryEventSinkContext.Current;
            if (sink is not null)
            {
                foreach (string s in removedScopes)
                {
                    // Notify per scope the memory lived in, round-trip-safe, so vectors are
                    // removed from EVERY scope's index — not just `local` (see the matching
                    // comment on the non-file:// path below).
                    try { await sink.OnForgottenAsync(store.FormatRoundTripQualifiedName(s, name), true, store, cancellationToken); }
                    catch { /* plugin errors must not block forget */ }
                }
            }

            return ResponseBuilder.Success($"Forgot: {name}.").WithFileChanges().WithAction("forgotten").ToYaml();
        }

        var (scope, subject) = store.ParseQualifiedName(nameOrUri);
        string qualifiedName = store.FormatQualifiedName(scope, subject);
        // The sink re-parses this to find which scope's vectors to remove — must be the
        // round-trip-safe form, not the display form (see the matching comment in Remember).
        string sinkQualifiedName = store.FormatRoundTripQualifiedName(scope, subject);

        // Delete the artifact file
        bool deleted = store.DeleteArtifact(subject, scope);

        // Remove index entry
        bool removed = store.Remove(subject, scope);
        if (!removed && !deleted)
            return ResponseBuilder.Error(
                $"No artifact found with name '{nameOrUri}'.",
                ErrorCodes.NotFound,
                "list() to see available memories").ToYaml();

        try { await (MemoryEventSinkContext.Current?.OnForgottenAsync(sinkQualifiedName, deleted || removed, store, cancellationToken) ?? Task.CompletedTask); }
        catch { /* plugin errors must not block forget */ }

        return ResponseBuilder.Success($"Forgot: {qualifiedName}.").WithFileChanges().WithAction("forgotten").ToYaml();
    }

    // ── Append/Reflect/Budget tools ─────────────────────────────────────────

    /// <summary>
    /// Append content as a new chunk to an existing memory, or create it if it does not exist.
    /// Preserves the memory's curated retrieval metadata (anchors, tags, keywords, description,
    /// codeRefs) and merges any caller-supplied <paramref name="tags"/> / <paramref name="keywords"/> /
    /// <paramref name="anchors"/> into it — appended content can introduce new concepts users will
    /// query for, and the metadata `remember` set must not be silently discarded on append.
    /// </summary>
    internal async Task<string> Append(
        [Description("The text content to append.")] string content,
        [Description("Memory name to append to (e.g. 'session-notes', 'api:auth-flow', '~scratch').")] string name,
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
            return await this.Store([content], name,
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

        // Unified fields: key:value-shaped agent keywords route to Fields, same as Store.
        // Only the plain remainder participates in the entry-level keyword merge below.
        var (routedNewFields, plainNewKeywords) = FieldExtractor.RouteKeywords(keywords);

        // Ref/file references are no longer folded into chunk-level keywords — they land in
        // Fields via the entry-level merge in BuildAppendMetadata.
        var newChunkEntry = new ChunkEntry(
            ChunkIndex: chunkCount,
            ContentPreview: store.GenerateContentPreview(content),
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
                : (existingEph?.ContentPreview ?? store.GenerateContentPreview(fullText));

            // meta.Description is unused post-Merge-3 (the legacy Description member it fed is
            // removed); ContentPreview (computed above) is the surviving display carrier.
            var meta = BuildAppendMetadata(
                fullText, description, tags, plainNewKeywords, anchors,
                existingEph?.ContentPreview, AttributeReaders.DisplayTags(existingEph?.Attributes).ToArray(),
                existingEph?.Keywords, AttributeReaders.DisplayAnchors(existingEph?.Attributes).ToArray(),
                content, routedNewFields, existingEph?.Attributes, attributes);

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
                Attributes: meta.Fields);

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
                : (existingEntry?.ContentPreview ?? store.GenerateContentPreview(fullText));

            // meta.Description is unused post-Merge-3 (the legacy Description member it fed is
            // removed); ContentPreview (computed above) is the surviving display carrier.
            var meta = BuildAppendMetadata(
                fullText, description, tags, plainNewKeywords, anchors,
                existingEntry?.ContentPreview, AttributeReaders.DisplayTags(existingEntry?.Attributes).ToArray(),
                existingEntry?.Keywords, AttributeReaders.DisplayAnchors(existingEntry?.Attributes).ToArray(),
                content, routedNewFields, existingEntry?.Attributes, attributes);

            ChunkEntry[]? existingChunks = existingEntry?.ChunkEntries;
            ChunkEntry[] updatedChunks = existingChunks is not null
                ? [.. existingChunks, newChunkEntry]
                : [newChunkEntry];

            // Archive previous version
            if (existingEntry is not null)
                store.ArchiveVersion(subject, scope);

            await store.WriteArtifactAsync(subject, scope, newArtifact, cancellationToken);

            string uri = store.ArtifactUri(subject, scope);
            qualifiedName = store.FormatQualifiedName(scope, subject);
            sinkQualifiedName = store.FormatRoundTripQualifiedName(scope, subject);

            // Dual-write gap: BuildAppendMetadata already folds tag/anchor into meta.Fields, but
            // review/file aren't available there — they come from existingEntry, which is only a
            // local at this construction site. Fold them here so Attributes carries the full
            // legacy carrier set even though tags/anchors are already in meta.Fields (passed null
            // below to avoid double-folding them).
            AttributeReaders.TryReadReview(existingEntry?.Attributes, out var existingReviewDate, out var existingReviewWhen);
            var carrier = AttributeMigration.BuildCarrierAttributes(
                tags: null, existingEntry?.FileHashes?.Keys, existingReviewDate,
                existingReviewWhen, anchors: null);
            var appendAttrs = carrier.Count > 0 ? KnownFields.Merge(meta.Fields, carrier) : meta.Fields;

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
            .WithFileChanges().WithAction("appended").ToYaml();
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
    private (string Description, string[]? Tags, string[]? Keywords, Dictionary<string, int>? Tf, string[]? Anchors, Dictionary<string, string[]>? Fields)
        BuildAppendMetadata(
            string fullText,
            string? newDescription, string[]? newTags, string[]? newKeywords, string[]? newAnchors,
            string? existingDescription, string[]? existingTags, string[]? existingKeywords, string[]? existingAnchors,
            string appendedContent, Dictionary<string, string[]>? routedNewFields, Dictionary<string, string[]>? existingFields,
            IReadOnlyDictionary<string, string[]>? attributes)
    {
        var (autoKeywords, tf) = TextAnalysis.AnalyzeText(fullText);

        // Existing curated keywords + caller additions are treated as "agent" terms so they
        // survive and keep the +5 TF boost; auto-extracted terms from the full text fill the
        // rest at +2. (Previously-auto keywords folded into the curated set get the agent
        // boost too — a negligible, intentional over-weight in exchange for never losing a
        // curated term.) Mirrors Store's keyword/TF contract.
        // Ref/file references are no longer concatenated in here as prefixed keywords —
        // they land in Fields via the merge below. Dropping the concat here is intentional:
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

        // Unified fields: incoming = routed new keywords ∪ content extraction ∪ references
        // from the appended chunk; merged per KnownFields (entity/ref union, scalar
        // newer-wins). Seed the merged set at +3 — TF is rebuilt from scratch per append,
        // so carried-forward fields must re-seed (same contract as merged anchors).
        var incoming = KnownFields.Merge(
            KnownFields.Merge(routedNewFields, FieldExtractor.Extract(appendedContent)),
            FieldExtractor.ExtractRefFields(appendedContent));
        incoming = KnownFields.Merge(incoming, NormalizeAttributes(attributes));   // explicit agent attributes
        var mergedFields = KnownFields.Merge(existingFields, incoming);

        // Dual-write: fold the fully-merged tag/anchor set into Fields at write time —
        // same mechanism as Store, mirroring AttributeMigration.Forward for the persistent
        // path (Task 1). Uses the already-merged mergedTags/mergedAnchors (existing ∪ new)
        // so Fields carries the same values that land in Tags/RetrievalAnchors below.
        var tagAnchorIncoming = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (mergedTags.Length > 0) tagAnchorIncoming[KnownFields.TagKey] = mergedTags;
        if (mergedAnchors is { Length: > 0 }) tagAnchorIncoming[KnownFields.AnchorKey] = mergedAnchors;
        if (tagAnchorIncoming.Count > 0)
            mergedFields = KnownFields.Merge(mergedFields, tagAnchorIncoming);

        FieldExtractor.SeedTermFrequencies(mergedFields, tf);

        return (
            desc,
            mergedTags.Length > 0 ? mergedTags : null,
            mergedKeywords.Length > 0 ? mergedKeywords : null,
            tf.Count > 0 ? tf : null,
            mergedAnchors,
            mergedFields);
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
    internal Task<string> References(
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
                 (MatchesFieldRef(sa.Entry.Attributes, "file", target) ||
                  MatchesFieldRef(sa.Entry.Attributes, "ref", target))))
            .Select(sa => store.FormatQualifiedName(sa.Scope, sa.Entry.Name))
            .Distinct()
            .ToList();

        if (matches.Count == 0)
            return Task.FromResult(ResponseBuilder.Success($"No memories reference '{target}'.").WithAction("searched").ToYaml());

        string result = $"Found {matches.Count} memory(s) referencing '{target}':\n" +
            string.Join("\n", matches.Select(m => $"- {m}"));
        return Task.FromResult(ResponseBuilder.Success(result).WithAction("searched").ToYaml());
    }

    /// <summary>Reference-field match: exact value or path-suffix ("/{target}") — the fields-side
    /// twin of the legacy file:/ref: keyword predicate in References.</summary>
    private static bool MatchesFieldRef(Dictionary<string, string[]> fields, string key, string target)
    {
        foreach (var kv in fields)
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
    /// <c>ref</c> field on each side (via <see cref="UpdateMeta"/>, which routes the
    /// <c>ref:{name}</c>-shaped keyword into <c>Fields["ref"]</c> instead of a legacy keyword).
    /// </summary>
    internal async Task<string> Link(
        [Description("Source memory name.")] string from,
        [Description("Target memory name.")] string to,
        [Description("Reason for the connection.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        // Add ref:{to} field to {from} (UpdateMeta routes the ref:-shaped keyword into Fields)
        string result1 = await UpdateMeta(from, keywords: [$"ref:{to}"], cancellationToken: cancellationToken);
        // Add ref:{from} field to {to}
        string result2 = await UpdateMeta(to, keywords: [$"ref:{from}"], cancellationToken: cancellationToken);

        if (result1.Contains("status: error") || result2.Contains("status: error"))
            return ResponseBuilder.Error(
                $"Partial link failure:\n  {from}: {result1}\n  {to}: {result2}",
                ErrorCodes.Internal,
                $"Verify both '{from}' and '{to}' exist via recall.").ToYaml();

        string linkMsg = $"Linked '{from}' <-> '{to}'.";
        if (!string.IsNullOrWhiteSpace(reason))
            linkMsg += $" Reason: {reason}";
        return ResponseBuilder.Success(linkMsg).WithAction("linked").ToYaml();
    }

    private static Task<string> BuildDriftList(IMemoryStore store)
    {
        string storeDir = store.GetStoreDirForScope("local");
        string scriniaDir = Path.GetDirectoryName(storeDir) ?? storeDir;
        string workspaceRoot = Path.GetDirectoryName(scriniaDir) ?? scriniaDir;

        var allEntries = store.ListScoped(null);
        var results = new List<string>();
        int driftCount = 0, missingCount = 0, okCount = 0;

        foreach (var sa in allEntries)
        {
            if (sa.Entry.FileHashes is null or { Count: 0 }) continue;

            string qualName = store.FormatQualifiedName(
                sa.Scope switch {
                    "local" => "local",
                    var s when s.StartsWith("local-topic:") => s["local-topic:".Length..],
                    _ => sa.Scope
                }, sa.Entry.Name);

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
                : "No memories have code references. Use codeRefs parameter on remember to track file dependencies.";
            return Task.FromResult(ResponseBuilder.Success(msg).WithAction("listed").ToYaml());
        }

        string driftResponse = $"Code reference drift detected ({driftCount} drifted, {missingCount} missing, {okCount} ok):\n" +
            string.Join("\n", results);
        var driftWarnings = new List<string>();
        if (driftCount > 0) driftWarnings.Add($"{driftCount} code reference(s) have drifted (files changed since stored).");
        if (missingCount > 0) driftWarnings.Add($"{missingCount} code reference(s) point to missing files.");
        return Task.FromResult(
            ResponseBuilder.Success(driftResponse).WithAction("listed").WithActionNeeded(driftWarnings.ToArray()).ToYaml());
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

    /// <summary>Lowercases attribute keys and trims/drops empty values, matching the Fields
    /// convention (FieldExtractor lowercases keys). Null when nothing survives.</summary>
    private static Dictionary<string, string[]>? NormalizeAttributes(IReadOnlyDictionary<string, string[]>? attributes)
    {
        if (attributes is not { Count: > 0 }) return null;
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in attributes)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null) continue;
            var vals = kv.Value.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToArray();
            if (vals.Length == 0) continue;
            result[kv.Key.Trim().ToLowerInvariant()] = vals;
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Detach an event-sink call from the response path so embedding latency (which can be
    /// 100–400 ms for remote providers) never blocks the MCP write response.
    /// The sink is resolved at fire-time so a concurrent context switch won't bind us to a stale sink.
    /// Errors are logged to stderr; the on-disk artifact is already durable by the time this runs,
    /// so a missed embedding only means the entry won't show in semantic search until the next write.
    /// </summary>
    private static void FireEventSinkAsync(Func<IMemoryEventSink, Task> action)
    {
        var sink = MemoryEventSinkContext.Current;
        if (sink is null) return;
        _ = Task.Run(async () =>
        {
            try { await action(sink); }
            catch (Exception ex) { Console.Error.WriteLine($"[scrinia:warn] Event sink error: {ex.GetType().Name}: {ex.Message}"); }
        });
    }
}
