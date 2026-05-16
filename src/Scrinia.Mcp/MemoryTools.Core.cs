using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Mcp;

public sealed partial class ScriniaMcpTools
{
    /// <summary>Unpack an NMP/2 artifact back to its original text content.</summary>
    internal async Task<string> Show(
        [Description("The NMP/2 artifact text, or a memory name to resolve. " +
                     "Use the exact name shown by memory('list') (e.g. 'session-notes', 'api:auth-flow', '~scratch').")] string artifactOrName,
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
                    "memory('list') to see available memories",
                    "memory('search', { query: '...' }) to find by keyword").ToYaml();

            try
            {
                artifact = await store.ResolveArtifactAsync(artifactOrName, cancellationToken);
            }
            catch (FileNotFoundException)
            {
                return ResponseBuilder.Error(
                    $"Memory '{artifactOrName}' not found.",
                    ErrorCodes.NotFound,
                    "memory('list') to see available memories",
                    "memory('search', { query: '...' }) to find by keyword").ToYaml();
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
        [Description("Optional keywords for search. Merged with auto-extracted content terms.")] string[]? keywords = null,
        [Description("Optional ISO 8601 date after which this memory should be reviewed for staleness.")] string? reviewAfter = null,
        [Description("Optional free-text condition describing when this memory should be reviewed (e.g. 'when auth system changes').")] string? reviewWhen = null,
        [Description("Optional file paths this memory depends on. Hashes are recorded to detect drift.")] string[]? codeRefs = null,
        string actionLabel = "stored",
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;
        string joined = string.Concat(content);

        // Compute text analysis: keywords + term frequencies (single-pass)
        var (autoKeywords, tf) = TextAnalysis.AnalyzeText(joined);
        var (mergedKeywords, agentKeywordSet) = TextAnalysis.MergeKeywordsWithSource(keywords, autoKeywords);

        // Extract file and memory references as prefixed keywords
        string rawContent = string.Join("\n", content);
        mergedKeywords = mergedKeywords.Concat(ExtractRefKeywords(rawContent)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // Boost keywords in TF: agent keywords +5, auto-extracted +2
        foreach (string kw in mergedKeywords)
        {
            tf.TryGetValue(kw, out int count);
            tf[kw] = count + (agentKeywordSet.Contains(kw) ? 5 : 2);
        }

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
            string ephPreview = store.GenerateContentPreview(joined);
            string ephDesc = string.IsNullOrWhiteSpace(description)
                ? joined[..Math.Min(200, joined.Length)]
                : description;

            // Check if updating existing ephemeral entry
            var existingEph = store.GetEphemeral(key);
            DateTimeOffset ephCreatedAt = existingEph?.CreatedAt ?? DateTimeOffset.UtcNow;
            DateTimeOffset? ephUpdatedAt = existingEph is not null ? DateTimeOffset.UtcNow : null;

            var ephEntry = new EphemeralEntry(
                Name: key,
                Artifact: ephArtifact,
                OriginalBytes: ephBytes,
                ChunkCount: ephChunkCount,
                CreatedAt: ephCreatedAt,
                Description: ephDesc,
                Tags: tags,
                ContentPreview: ephPreview,
                Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
                TermFrequencies: tf.Count > 0 ? tf : null,
                UpdatedAt: ephUpdatedAt,
                ChunkEntries: chunkEntries);

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
        string desc = string.IsNullOrWhiteSpace(description)
            ? joined[..Math.Min(200, joined.Length)]
            : description;

        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);
        long originalBytes = System.Text.Encoding.UTF8.GetByteCount(joined);
        string contentPreview = store.GenerateContentPreview(joined);
        string qualifiedName = store.FormatQualifiedName(scope, subject);

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

        // Compute code reference hashes — explicit codeRefs + auto-detected file: keywords
        string storeDir = store.GetStoreDirForScope("local");
        string scriniaDir = Path.GetDirectoryName(storeDir) ?? storeDir;
        string workspaceRoot = Path.GetDirectoryName(scriniaDir) ?? scriniaDir;

        var allRefPaths = (codeRefs ?? [])
            .Concat(mergedKeywords
                .Where(k => k.StartsWith("file:", StringComparison.Ordinal))
                .Select(k => k["file:".Length..]))
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

        var entry = new ArtifactEntry(
            Name: subject,
            Uri: uri,
            OriginalBytes: originalBytes,
            ChunkCount: chunkCount,
            CreatedAt: createdAt,
            Description: desc,
            Tags: tags,
            ContentPreview: contentPreview,
            Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
            TermFrequencies: tf.Count > 0 ? tf : null,
            UpdatedAt: updatedAt,
            ReviewAfter: parsedReviewAfter,
            ReviewWhen: string.IsNullOrWhiteSpace(reviewWhen) ? null : reviewWhen,
            ChunkEntries: chunkEntries,
            CodeRefs: codeRefDict);

        store.Upsert(entry, scope);

        // Fire event sink (embeddings, etc.) — fire-and-forget so embedding latency
        // never blocks the response. Failure logged to stderr; the artifact is already on disk.
        // CancellationToken.None — see the ephemeral path above for rationale.
        FireEventSinkAsync(sink => sink.OnStoredAsync(qualifiedName, content, store, CancellationToken.None));

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
                "Update does not support ephemeral memories. Use memory('remember') instead.",
                ErrorCodes.InvalidParameter,
                "memory('remember', { path: '...', content: [...] }) — overwrites an existing ephemeral entry").ToYaml());

        var (scope, subject) = store.ParseQualifiedName(name);

        var entries = store.LoadIndex(scope);
        var entry = entries.FirstOrDefault(e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            string qualName = store.FormatQualifiedName(scope, subject);
            return Task.FromResult(ResponseBuilder.Error(
                $"Memory '{qualName}' not found.",
                ErrorCodes.NotFound,
                "memory('list') to see available memories",
                "memory('remember', { path: '...', content: [...] }) to create it").ToYaml());
        }

        // Track what changed for the response message
        var changes = new List<string>();

        // Merge keywords (additive)
        string[]? mergedKeywords = entry.Keywords;
        int addedCount = 0;
        if (keywords is { Length: > 0 })
        {
            var existing = entry.Keywords ?? [];
            mergedKeywords = existing.Union(keywords, StringComparer.OrdinalIgnoreCase).ToArray();
            addedCount = mergedKeywords.Length - existing.Length;
            if (addedCount > 0)
                changes.Add($"{addedCount} keyword(s) added");
        }

        // Replace description if provided
        string updatedDescription = entry.Description;
        if (!string.IsNullOrWhiteSpace(description))
        {
            updatedDescription = description;
            changes.Add("description updated");
        }

        // Parse reviewAfter if provided
        DateTimeOffset? parsedReviewAfter = entry.ReviewAfter;
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
        string? updatedReviewWhen = entry.ReviewWhen;
        if (reviewWhen is not null)
        {
            updatedReviewWhen = string.IsNullOrWhiteSpace(reviewWhen) ? null : reviewWhen;
            changes.Add(updatedReviewWhen is not null ? "reviewWhen updated" : "reviewWhen cleared");
        }

        if (changes.Count == 0)
            return Task.FromResult(ResponseBuilder.Warning("No changes specified. Provide at least one of: keywords, description, reviewAfter, reviewWhen.").ToYaml());

        // Build the updated entry via term frequencies merge
        var updatedTf = entry.TermFrequencies;
        if (keywords is { Length: > 0 } && addedCount > 0)
        {
            var tf = entry.TermFrequencies is not null
                ? new Dictionary<string, int>(entry.TermFrequencies)
                : new Dictionary<string, int>();
            var existing = entry.Keywords ?? [];
            foreach (string kw in keywords)
            {
                if (!existing.Contains(kw, StringComparer.OrdinalIgnoreCase))
                {
                    tf.TryGetValue(kw, out int count);
                    tf[kw] = count + 5;
                }
            }
            updatedTf = tf;
        }

        var updated = entry with
        {
            Keywords = mergedKeywords,
            TermFrequencies = updatedTf,
            Description = updatedDescription,
            ReviewAfter = parsedReviewAfter,
            ReviewWhen = updatedReviewWhen,
            UpdatedAt = DateTimeOffset.UtcNow
        };

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

    private static string BuildSummary(List<ScopedArtifact> entries, IMemoryStore store)
    {
        long totalBytes = entries.Sum(e => e.Entry.OriginalBytes);
        int totalTokens = (int)(totalBytes / 4);
        int staleCount = entries.Count(e => e.Entry.ReviewAfter.HasValue && e.Entry.ReviewAfter.Value <= DateTimeOffset.UtcNow);
        int reviewCount = entries.Count(e => !string.IsNullOrEmpty(e.Entry.ReviewWhen)
            && !(e.Entry.ReviewAfter.HasValue && e.Entry.ReviewAfter.Value <= DateTimeOffset.UtcNow));
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
            if (item.Entry.Tags is { Length: > 0 })
                foreach (var tag in item.Entry.Tags)
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

        sb.Append("Use `memory('list', { mode: 'full' })` to see all entries, or `memory('search', { query: '...' })` to find specific memories.");
        return sb.ToString();
    }

    private static string BuildFullList(List<ScopedArtifact> entries, IMemoryStore store, int offset, int limit)
    {
        int total = entries.Count;
        if (offset < 0) offset = 0;
        if (limit < 1) limit = 50;
        var page = entries.Skip(offset).Take(limit).ToList();

        // Derive workspace root for drift checking (only used if any entry has CodeRefs)
        string? workspaceRoot = null;
        bool anyCodeRefs = page.Any(p => p.Entry.CodeRefs is { Count: > 0 });
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
            string reviewPrefix = "";
            if (e.ReviewAfter.HasValue && e.ReviewAfter.Value <= DateTimeOffset.UtcNow)
                reviewPrefix = "[stale] ";
            else if (!string.IsNullOrEmpty(e.ReviewWhen))
                reviewPrefix = "[review?] ";

            // Drift marker — only check entries that have CodeRefs
            string driftPrefix = "";
            if (workspaceRoot is not null && e.CodeRefs is { Count: > 0 })
            {
                bool hasDrift = false;
                foreach (var (path, storedHash) in e.CodeRefs)
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

            string desc = e.Description;
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
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Augment the query with caller-supplied context for scoring. The bare query is preserved
        // for any user-facing echo; the effective query feeds BM25, field scoring, and embeddings
        // alike so a single signal carries through all three.
        string effectiveQuery = string.IsNullOrWhiteSpace(context) ? query : $"{query} {context}";

        // Compute supplemental scores from plugin (e.g. embeddings) if available
        // Use excludeTopics-filtered candidates so excluded topics don't influence embeddings scoring
        var contributor = SearchContributorContext.Current;
        IReadOnlyDictionary<string, double>? supplemental = null;
        if (contributor is not null)
        {
            var candidates = store.ListScoped(scopes, excludeTopics);
            supplemental = await contributor.ComputeScoresAsync(effectiveQuery, candidates, store, cancellationToken);
        }

        IReadOnlyList<SearchResult> matches = supplemental is { Count: > 0 }
            ? store.SearchAll(effectiveQuery, scopes, limit, supplemental)
                .Where(r => !IMemoryStore.ShouldExcludeScope(IMemoryStore.GetResultScope(r), excludeTopics))
                .ToList()
            : store.SearchAll(effectiveQuery, scopes, limit, excludeTopics);
        if (matches.Count == 0)
            return ResponseBuilder.Success("No matching memories found.").WithAction("searched").ToYaml();

        // Build qualified names first to compute dynamic column width (never truncate names)
        const int typeW = 6;
        const int scoreW = 6;
        const int tokensW = 8;
        var rows = new List<(string Type, string Name, double Score, string TokensStr, string Desc)>(matches.Count);
        int nameW = 4; // min width = "name".Length
        foreach (var match in matches)
        {
            if (match is ChunkEntryResult cr)
            {
                string qualifiedName = cr.ParentItem.Scope == "ephemeral"
                    ? $"~{cr.ParentItem.Entry.Name}"
                    : store.FormatQualifiedName(cr.ParentItem.Scope, cr.ParentItem.Entry.Name);
                string chunkLabel = $"{qualifiedName} [chunk {cr.Chunk.ChunkIndex}/{cr.TotalChunks}]";
                string desc = cr.Chunk.ContentPreview ?? cr.ParentItem.Entry.Description;
                desc = desc.Replace('\n', ' ').Replace('\r', ' ');
                if (desc.Length > 60) desc = desc[..57] + "...";
                int estTokens = (int)(cr.ParentItem.Entry.OriginalBytes / cr.TotalChunks / 4);
                rows.Add(("chunk", chunkLabel, cr.Score, estTokens.ToString(), desc));
                if (chunkLabel.Length > nameW) nameW = chunkLabel.Length;
            }
            else if (match is EntryResult er)
            {
                string qualifiedName = er.Item.Scope == "ephemeral"
                    ? $"~{er.Item.Entry.Name}"
                    : store.FormatQualifiedName(er.Item.Scope, er.Item.Entry.Name);
                string desc = er.Item.Entry.Description.Replace('\n', ' ').Replace('\r', ' ');
                if (desc.Length > 60) desc = desc[..57] + "...";
                int estTokens = (int)(er.Item.Entry.OriginalBytes / 4);
                rows.Add(("entry", qualifiedName, er.Score, estTokens.ToString(), desc));
                if (qualifiedName.Length > nameW) nameW = qualifiedName.Length;
            }
            else if (match is TopicResult tr)
            {
                string trLabel = MemoryNaming.FormatScopeLabel(tr.Scope);
                string desc = tr.Description.Replace('\n', ' ').Replace('\r', ' ');
                if (desc.Length > 60) desc = desc[..57] + "...";
                rows.Add(("topic", trLabel, tr.Score, "", desc));
                if (trLabel.Length > nameW) nameW = trLabel.Length;
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{"type",-typeW}  {"name".PadRight(nameW)}  {"score",scoreW}  {"~tokens",tokensW}  description");
        sb.AppendLine(new string('-', typeW + nameW + scoreW + tokensW + 17));

        foreach (var (type, name, score, tokensStr, desc) in rows)
        {
            sb.AppendLine($"{type,-typeW}  {name.PadRight(nameW)}  {score,scoreW:F0}  {tokensStr,tokensW}  {desc}");
        }

        return ResponseBuilder.Success(sb.ToString().TrimEnd()).WithAction("searched").ToYaml();
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
                    "memory('list') to see ephemeral memories currently held in process").ToYaml();

            try { await (MemoryEventSinkContext.Current?.OnForgottenAsync($"~{key}", true, store, cancellationToken) ?? Task.CompletedTask); }
            catch { /* plugin errors must not block forget */ }

            return ResponseBuilder.Success($"Forgot: ~{key}").WithAction("forgotten").ToYaml();
        }

        // Backward compat: resolve file:// URIs to their memory name, then delete by name
        if (nameOrUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            string name = FileMemoryStore.NameFromUri(nameOrUri);

            bool removedAny = false;
            foreach (string s in store.ResolveReadScopes())
            {
                store.DeleteArtifact(name, s);
                removedAny |= store.Remove(name, s);
            }

            if (!removedAny)
                return ResponseBuilder.Error(
                    $"No artifact found with name or URI '{nameOrUri}'.",
                    ErrorCodes.NotFound,
                    "memory('list') to see available memories").ToYaml();

            try { await (MemoryEventSinkContext.Current?.OnForgottenAsync(name, removedAny, store, cancellationToken) ?? Task.CompletedTask); }
            catch { /* plugin errors must not block forget */ }

            return ResponseBuilder.Success($"Forgot: {name}.").WithFileChanges().WithAction("forgotten").ToYaml();
        }

        var (scope, subject) = store.ParseQualifiedName(nameOrUri);
        string qualifiedName = store.FormatQualifiedName(scope, subject);

        // Delete the artifact file
        bool deleted = store.DeleteArtifact(subject, scope);

        // Remove index entry
        bool removed = store.Remove(subject, scope);
        if (!removed && !deleted)
            return ResponseBuilder.Error(
                $"No artifact found with name '{nameOrUri}'.",
                ErrorCodes.NotFound,
                "memory('list') to see available memories").ToYaml();

        try { await (MemoryEventSinkContext.Current?.OnForgottenAsync(qualifiedName, deleted || removed, store, cancellationToken) ?? Task.CompletedTask); }
        catch { /* plugin errors must not block forget */ }

        return ResponseBuilder.Success($"Forgot: {qualifiedName}.").WithFileChanges().WithAction("forgotten").ToYaml();
    }

    // ── Append/Reflect/Budget tools ─────────────────────────────────────────

    /// <summary>Append content as a new chunk to an existing memory, or create it if it does not exist.</summary>
    internal async Task<string> Append(
        [Description("The text content to append.")] string content,
        [Description("Memory name to append to (e.g. 'session-notes', 'api:auth-flow', '~scratch').")] string name,
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
            // Non-existent → create as single-chunk (same as Store)
            return await this.Store([content], name, cancellationToken: cancellationToken);
        }

        // Append as new chunk
        string newArtifact = Nmp2ChunkedEncoder.AppendChunk(existingArtifact, content);

        // Decode full result for metadata
        byte[] fullBytes = Nmp2Strategy.Instance.Decode(newArtifact);
        string fullText = System.Text.Encoding.UTF8.GetString(fullBytes);
        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(newArtifact);
        long originalBytes = fullBytes.LongLength;

        // Compute text analysis from full decoded content (single-pass)
        var (autoKeywords, tf) = TextAnalysis.AnalyzeText(fullText);
        var mergedKeywords = TextAnalysis.MergeKeywords(null, autoKeywords);

        // Extract file and memory references as prefixed keywords
        mergedKeywords = mergedKeywords.Concat(ExtractRefKeywords(fullText)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        foreach (string kw in mergedKeywords)
        {
            tf.TryGetValue(kw, out int count);
            tf[kw] = count + 2;
        }

        string contentPreview = store.GenerateContentPreview(fullText);

        // Build chunk entry for the newly appended content (single-pass)
        var (newKw, newTf) = TextAnalysis.AnalyzeText(content);
        foreach (string k in newKw) { newTf.TryGetValue(k, out int c); newTf[k] = c + 2; }

        // Add ref keywords from the new chunk to its chunk-level keywords
        newKw = newKw.Concat(ExtractRefKeywords(content)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var newChunkEntry = new ChunkEntry(
            ChunkIndex: chunkCount,
            ContentPreview: store.GenerateContentPreview(content),
            Keywords: newKw.Length > 0 ? newKw : null,
            TermFrequencies: newTf.Count > 0 ? newTf : null);

        string qualifiedName;

        if (store.IsEphemeral(name))
        {
            string key = MemoryNaming.StripEphemeralPrefix(name);
            var existingEph = store.GetEphemeral(key);
            DateTimeOffset createdAt = existingEph?.CreatedAt ?? DateTimeOffset.UtcNow;

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
                Description: fullText[..Math.Min(200, fullText.Length)],
                Tags: null,
                ContentPreview: contentPreview,
                Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
                TermFrequencies: tf.Count > 0 ? tf : null,
                UpdatedAt: DateTimeOffset.UtcNow,
                ChunkEntries: updatedChunks);

            store.RememberEphemeral(key, ephEntry);
            qualifiedName = $"~{key}";
        }
        else
        {
            var (scope, subject) = store.ParseQualifiedName(name);

            // Check existing entry for versioning + timestamps
            var existingEntries = store.LoadIndex(scope);
            var existingEntry = existingEntries.FirstOrDefault(e => e.Name == subject);
            DateTimeOffset createdAt = existingEntry?.CreatedAt ?? DateTimeOffset.UtcNow;

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

            var entry = new ArtifactEntry(
                Name: subject,
                Uri: uri,
                OriginalBytes: originalBytes,
                ChunkCount: chunkCount,
                CreatedAt: createdAt,
                Description: fullText[..Math.Min(200, fullText.Length)],
                Tags: null,
                ContentPreview: contentPreview,
                Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
                TermFrequencies: tf.Count > 0 ? tf : null,
                UpdatedAt: DateTimeOffset.UtcNow,
                ReviewAfter: existingEntry?.ReviewAfter,
                ReviewWhen: existingEntry?.ReviewWhen,
                ChunkEntries: updatedChunks);

            store.Upsert(entry, scope);
        }

        // Fire event sink (embeddings, etc.) — fire-and-forget so embedding latency
        // never blocks the response. Failure logged to stderr; the artifact is already on disk.
        // CancellationToken.None — see the ephemeral path above for rationale.
        FireEventSinkAsync(sink => sink.OnAppendedAsync(qualifiedName, content, store, CancellationToken.None));

        return ResponseBuilder.Success($"Appended chunk {chunkCount} to {qualifiedName} ({chunkCount} {(chunkCount == 1 ? "chunk" : "chunks")}, {FormatBytes(originalBytes)}).")
            .WithFileChanges().WithAction("appended").ToYaml();
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
            .Where(sa => sa.Entry.Keywords is not null &&
                sa.Entry.Keywords.Any(k =>
                    k.Equals(fileKey, StringComparison.OrdinalIgnoreCase) ||
                    k.Equals(refKey, StringComparison.OrdinalIgnoreCase) ||
                    k.EndsWith($"/{target}", StringComparison.OrdinalIgnoreCase)))
            .Select(sa => store.FormatQualifiedName(sa.Scope, sa.Entry.Name))
            .Distinct()
            .ToList();

        if (matches.Count == 0)
            return Task.FromResult(ResponseBuilder.Success($"No memories reference '{target}'.").WithAction("searched").ToYaml());

        string result = $"Found {matches.Count} memory(s) referencing '{target}':\n" +
            string.Join("\n", matches.Select(m => $"- {m}"));
        return Task.FromResult(ResponseBuilder.Success(result).WithAction("searched").ToYaml());
    }

    /// <summary>Create a bidirectional relationship between two memories.</summary>
    internal async Task<string> Link(
        [Description("Source memory name.")] string from,
        [Description("Target memory name.")] string to,
        [Description("Reason for the connection.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        // Add ref:{to} keyword to {from}
        string result1 = await UpdateMeta(from, keywords: [$"ref:{to}"], cancellationToken: cancellationToken);
        // Add ref:{from} keyword to {to}
        string result2 = await UpdateMeta(to, keywords: [$"ref:{from}"], cancellationToken: cancellationToken);

        if (result1.Contains("status: error") || result2.Contains("status: error"))
            return ResponseBuilder.Error(
                $"Partial link failure:\n  {from}: {result1}\n  {to}: {result2}",
                ErrorCodes.Internal,
                $"Verify both '{from}' and '{to}' exist via memory('recall').").ToYaml();

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
            if (sa.Entry.CodeRefs is null or { Count: 0 }) continue;

            string qualName = store.FormatQualifiedName(
                sa.Scope switch {
                    "local" => "local",
                    var s when s.StartsWith("local-topic:") => s["local-topic:".Length..],
                    _ => sa.Scope
                }, sa.Entry.Name);

            foreach (var (path, storedHash) in sa.Entry.CodeRefs)
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
                : "No memories have code references. Use codeRefs parameter on memory('remember') to track file dependencies.";
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
        return fullPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }

    private static string? ComputeFileHash(string fullPath)
    {
        try { return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fullPath))); }
        catch { return null; }
    }

    private static IEnumerable<string> ExtractRefKeywords(string text)
    {
        var fileRefs = ReferenceExtractor.ExtractFileRefs(text);
        var memoryRefs = ReferenceExtractor.ExtractMemoryRefs(text);
        return fileRefs.Select(f => $"file:{f}").Concat(memoryRefs.Select(m => $"ref:{m}"));
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
