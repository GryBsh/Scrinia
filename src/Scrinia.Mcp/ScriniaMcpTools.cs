using System.ComponentModel;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Mcp;

public sealed record BundleIndex(List<ArtifactEntry> Entries);
public sealed record BundleManifest(int Version, string Exported, List<string> Topics, int TotalEntries);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BundleIndex))]
[JsonSerializable(typeof(BundleManifest))]
public partial class BundleJsonContext : JsonSerializerContext;

[McpServerToolType]
public sealed class ScriniaMcpTools
{
    public static readonly JsonSerializerOptions BundleJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = BundleJsonContext.Default,
    };

    private static IMemoryStore CurrentStore =>
        MemoryStoreContext.Current ?? throw new InvalidOperationException(
            "No memory store configured. Call MemoryStoreContext.Current = ... before using MCP tools.");

    /// <summary>
    /// Resolves inline NMP/2 artifacts and file:// URIs without requiring a configured store.
    /// Returns null if the input requires store-based resolution (memory name, ephemeral, etc.).
    /// </summary>
    private static async Task<string?> TryResolveWithoutStore(string input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        // Inline NMP/2 artifact
        if (input.TrimStart().StartsWith("NMP/2 ", StringComparison.Ordinal))
            return input;

        // file:// URI — direct file read
        if (input.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return await File.ReadAllTextAsync(input[7..], ct);

        return null;
    }

    [McpServerTool(Name = "guide"), Description(
        "Required reading for effective memory use — call once per session. " +
        "Returns a concise playbook covering ephemeral memories, topic organization, " +
        "chunked retrieval, context compression, and cross-project sharing.")]
    public Task<string> Guide(CancellationToken cancellationToken = default) =>
        Task.FromResult("""
            # scrinia guide — patterns for effective scrinia memory use

            ## Ephemeral scrinia memories (~name)
            Use `~` prefix for in-session working state that shouldn't persist:
            - `store(["scratch data"], "~scratch")` — dies when process exits
            - Great for intermediate results, draft summaries, working context
            - Promote to persistent with `copy("~scratch", "topic:final-name")`

            ## Topic organization
            Use topic:subject naming to organize related scrinia memories:
            - `store(["content"], "api:auth-flow")` — stored in api/ topic
            - `store(["content"], "arch:decisions")` — stored in arch/ topic
            - Topics are auto-discovered — no setup needed

            ## Chunked retrieval
            For large scrinia memories, retrieve only what you need:
            1. `chunk_count("my-memory")` — see how many chunks
            2. `get_chunk("my-memory", 1)` — read just the first chunk
            3. Process chunk by chunk to stay within context limits

            ## Incremental capture with append
            Build up scrinia memories incrementally — each append adds a new independently retrievable chunk:
            - `append("New finding here", "session-notes")` — adds as a new chunk
            - Creates the scrinia memory if it doesn't exist yet
            - Great for session journals, running logs, and incremental notes
            - Each appended chunk is individually indexed for search

            ## Context compression
            When you gather large amounts of information during research:
            1. Summarize your findings into a concise document
            2. `store([summary], "topic:finding-name")` — persist for future sessions
            3. Later: `search("finding")` → `show("topic:finding-name")` to recall
            This lets you carry knowledge across sessions without re-researching.

            ## Version history
            When you overwrite an existing scrinia memory, the previous version is archived:
            - Stored in `versions/` subdirectory with timestamp suffix
            - No manual action needed — happens automatically on store/append

            ## Review conditions
            Flag scrinia memories that may become stale:
            - `store(["content"], "api:endpoints", reviewAfter="2026-06-01")` — date-based
            - `store(["content"], "auth:flow", reviewWhen="when auth system changes")` — condition-based
            - `list()` shows a summary with topics, keywords, and stats
            - `list(mode="full")` shows all entries with `[stale]` or `[review?]` markers

            ## Budget tracking
            Monitor how much context you're consuming:
            - `budget()` — shows per-scrinia-memory chars/tokens loaded via show()/get_chunk()
            - Helps decide when to use chunked retrieval vs. full show()

            ## Session-end reflection
            Call `reflect()` at the end of a session for a checklist of knowledge to persist.

            ## Context preservation (~checkpoints)
            Long conversations get compressed by your host platform. Use ephemeral scrinia checkpoints to survive:
            - Before a large task or after a milestone, store your current state:
              `store(["Task: ...\nKey findings: ...\nNext steps: ..."], "~checkpoint")`
            - After context compaction, restore your bearings:
              `list(scopes="ephemeral")` then `show("~checkpoint")`
            - Update the checkpoint as you make progress — overwrite with fresh state
            - **When to checkpoint**: before large multi-step tasks, after completing milestones,
              when the conversation is getting long, or before operations that generate lots of output

            ## Cross-project sharing
            Export topics as portable .scrinia-bundle files:
            1. `export(["api", "arch"])` — creates a .scrinia-bundle in .scrinia/exports/
            2. Copy the bundle to another project
            3. `import("path/to/bundle.scrinia-bundle")` — restores all topics
            Useful for sharing team conventions, API patterns, or onboarding knowledge.

            ## Workspace changes
            Scrinia tools (store, append, forget, import, copy) write to the `.scrinia/` directory
            in the workspace root. These file changes are a direct result of your tool calls —
            treat them as changes you made. Include `.scrinia/` changes in your commits alongside
            other project changes.

            ## When to store vs. not store
            **Store in scrinia:** stable patterns, architectural decisions, API conventions,
            solutions to recurring problems, project-specific knowledge.
            **Don't store:** session-specific state (use ~ephemeral instead).
            **Exception:** use `~checkpoint` to preserve working context across context compactions.
            """);

    [McpServerTool(Name = "encode"), Description(
        "Compress text into a chunk-addressable NMP/2 artifact (brotli). " +
        "Returns the artifact inline. " +
        "Use chunk_count() and get_chunk() to access the content chunk-by-chunk.")]
    public Task<string> Encode(
        [Description("The text to compress. " +
                     "Pass a single element for a single-chunk artifact, or multiple elements to control " +
                     "chunk boundaries — each element becomes one independently decodable chunk.")] string[] content,
        CancellationToken cancellationToken = default)
    {
        string artifact = content.Length == 1
            ? Nmp2ChunkedEncoder.Encode(content[0])
            : Nmp2ChunkedEncoder.EncodeChunks(content);
        return Task.FromResult(artifact);
    }

    [McpServerTool(Name = "chunk_count"), Description(
        "Returns the number of independently decodable chunks in a compressed artifact. " +
        "Single-chunk artifacts return 1.")]
    public async Task<int> ChunkCount(
        [Description("The artifact text, memory name, or file:// URI returned by Encode().")]
        string artifactOrName,
        CancellationToken cancellationToken = default)
    {
        var resolved = await TryResolveWithoutStore(artifactOrName, cancellationToken);
        string artifact = resolved ?? await CurrentStore.ResolveArtifactAsync(artifactOrName, cancellationToken);
        return Nmp2ChunkedEncoder.GetChunkCount(artifact);
    }

    [McpServerTool(Name = "get_chunk"), Description(
        "Decodes and returns the text of one chunk from a compressed artifact. " +
        "Chunks are 1-based. Call chunk_count() first to know the upper bound. " +
        "Process chunks sequentially to reconstruct the full document.")]
    public async Task<string> GetChunk(
        [Description("The artifact text, memory name, or file:// URI returned by Encode().")]
        string artifactOrName,
        [Description("1-based chunk index.")] int chunkIndex,
        CancellationToken cancellationToken = default)
    {
        var resolved = await TryResolveWithoutStore(artifactOrName, cancellationToken);
        string artifact = resolved ?? await CurrentStore.ResolveArtifactAsync(artifactOrName, cancellationToken);
        string chunk = Nmp2ChunkedEncoder.DecodeChunk(artifact, chunkIndex);
        SessionBudget.RecordAccess(artifactOrName, chunk.Length);
        return chunk;
    }

    [McpServerTool(Name = "show"), Description(
        "Unpack an NMP/2 artifact back to its original text content. " +
        "Accepts either the artifact text inline or a memory name. " +
        "Only NMP/2 artifacts are supported; other formats return an error string.")]
    public async Task<string> Show(
        [Description("The NMP/2 artifact text, or a memory name to resolve. " +
                     "Use the exact name shown by list() (e.g. 'session-notes', 'api:auth-flow', '~scratch').")] string artifactOrName,
        CancellationToken cancellationToken = default)
    {
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
                return $"Error: memory '{artifactOrName}' not found. Use list() or search() to find available memories.";

            try
            {
                artifact = await store.ResolveArtifactAsync(artifactOrName, cancellationToken);
            }
            catch (FileNotFoundException)
            {
                return $"Error: memory '{artifactOrName}' not found. Use list() or search() to find available memories.";
            }
        }

        if (!artifact.TrimStart().StartsWith("NMP/2 ", StringComparison.Ordinal))
            return "Error: only NMP/2 artifacts are supported by this tool.";

        byte[] bytes = new Nmp2Strategy().Decode(artifact);
        string decoded = System.Text.Encoding.UTF8.GetString(bytes);
        SessionBudget.RecordAccess(artifactOrName, decoded.Length);
        return decoded;
    }

    // ── Persistent memory tools ───────────────────────────────────────────────

    [McpServerTool(Name = "store"), Description(
        "Compress text and persist it as a named artifact in a memory scope. " +
        "Use proactively to save important findings, decisions, patterns, and solutions as you work. " +
        "Knowledge saved here persists across sessions and travels with the code. " +
        "Use topic:subject naming to organize into local topics " +
        "(e.g. 'api:auth-flow', 'arch:decisions'). " +
        "Prefix with ~ for ephemeral in-memory storage (e.g. '~scratch'). " +
        "Flag memories that may become stale with optional review conditions. " +
        "Note: this writes to .scrinia/ in the workspace — treat those file changes as your own.")]
    public async Task<string> Store(
        [Description("The text content to compress and store. " +
                     "Pass a single element for a single-chunk artifact, or multiple elements to control " +
                     "chunk boundaries — each element becomes one independently retrievable chunk.")] string[] content,
        [Description("Human-readable name for this artifact (e.g. \"session-notes\", \"my-codebase\"). " +
                     "Invalid filename characters are replaced with '_'. " +
                     "Naming: 'subject' (local store), 'topic:subject' (local topic), '~subject' (ephemeral).")] string name,
        [Description("Optional description. If empty, the first 200 characters of content are used.")] string description = "",
        [Description("Optional tags for categorization.")] string[]? tags = null,
        [Description("Optional keywords for search. Merged with auto-extracted content terms.")] string[]? keywords = null,
        [Description("Optional ISO 8601 date after which this memory should be reviewed for staleness.")] string? reviewAfter = null,
        [Description("Optional free-text condition describing when this memory should be reviewed (e.g. 'when auth system changes').")] string? reviewWhen = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;
        string joined = string.Concat(content);

        // Compute text analysis: keywords + term frequencies (single-pass)
        var (autoKeywords, tf) = TextAnalysis.AnalyzeText(joined);
        var (mergedKeywords, agentKeywordSet) = TextAnalysis.MergeKeywordsWithSource(keywords, autoKeywords);

        // Boost keywords in TF: agent keywords +5, auto-extracted +2
        foreach (string kw in mergedKeywords)
        {
            tf.TryGetValue(kw, out int count);
            tf[kw] = count + (agentKeywordSet.Contains(kw) ? 5 : 2);
        }

        ChunkEntry[]? chunkEntries = content.Length > 1
            ? ComputeChunkEntries(store, content)
            : null;

        // ── Ephemeral path (~name) ───────────────────────────────────────
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

            // Fire event sink (embeddings, etc.) — never block the response
            var sink = MemoryEventSinkContext.Current;
            try { await (sink?.OnStoredAsync($"~{key}", content, store, cancellationToken) ?? Task.CompletedTask); }
            catch (Exception ex) { Console.Error.WriteLine($"[scrinia:warn] Event sink error: {ex.GetType().Name}: {ex.Message}"); }

            return $"Remembered: ~{key} ({ephChunkCount} {(ephChunkCount == 1 ? "chunk" : "chunks")}, {FormatBytes(ephBytes)}) [ephemeral]";
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
            ChunkEntries: chunkEntries);

        store.Upsert(entry, scope);

        // Fire event sink (embeddings, etc.) — never block the response
        try { await (MemoryEventSinkContext.Current?.OnStoredAsync(qualifiedName, content, store, cancellationToken) ?? Task.CompletedTask); }
        catch (Exception ex) { Console.Error.WriteLine($"[scrinia:warn] Event sink error: {ex.GetType().Name}: {ex.Message}"); }

        return $"Remembered: {qualifiedName} ({chunkCount} {(chunkCount == 1 ? "chunk" : "chunks")}, {FormatBytes(originalBytes)}). Files in .scrinia/ were updated — these are your changes.";
    }

    [McpServerTool(Name = "list"), Description(
        "Returns a summary or full listing of persisted memories. " +
        "Call this when starting a session to orient on available project knowledge. " +
        "Default mode is 'summary' — returns topics, top keywords, and stats without flooding context. " +
        "Use mode='full' with offset/limit to page through entries.")]
    public Task<string> List(
        [Description("Optional comma-separated scope order, e.g. local,api,ephemeral. " +
                     "Topic names filter to local topics (e.g. 'api' shows api topic entries).")] string? scopes = null,
        [Description("'summary' (default) returns topics, top keywords, and stats. " +
                     "'full' returns a paginated table of all entries.")] string mode = "summary",
        [Description("Starting index for full mode (0-based). Ignored in summary mode.")] int offset = 0,
        [Description("Maximum entries to return in full mode (default 50). Ignored in summary mode.")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;
        List<ScopedArtifact> entries = store.ListScoped(scopes);
        if (entries.Count == 0)
            return Task.FromResult("No memories stored.");

        entries.Sort((a, b) => b.Entry.CreatedAt.CompareTo(a.Entry.CreatedAt));

        if (!string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(BuildSummary(entries, store));

        return Task.FromResult(BuildFullList(entries, store, offset, limit));
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

        sb.Append("Use `list(mode=\"full\")` to see all entries, or `search(\"query\")` to find specific memories.");
        return sb.ToString();
    }

    private static string BuildFullList(List<ScopedArtifact> entries, IMemoryStore store, int offset, int limit)
    {
        int total = entries.Count;
        if (offset < 0) offset = 0;
        if (limit < 1) limit = 50;
        var page = entries.Skip(offset).Take(limit).ToList();

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

            string desc = e.Description;
            desc = desc.Replace('\n', ' ').Replace('\r', ' ');
            string fullDesc = reviewPrefix + desc;
            if (fullDesc.Length > 60) fullDesc = fullDesc[..57] + "...";

            sb.AppendLine(
                $"{qualifiedName.PadRight(nameW)}  {e.ChunkCount,chunkW}  {sizeStr,bytesW}  {estTokens,tokensW}  {dateStr,-dateW}  {fullDesc}");
        }

        if (showingEnd < total)
            sb.AppendLine($"\nUse list(mode=\"full\", offset={showingEnd}) for more.");

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "search"), Description(
        "Search this first before starting research or problem-solving — " +
        "relevant knowledge may already exist from prior sessions. " +
        "Finds memories across local and topic scopes using a name/description query. " +
        "Searches both entries and topics.")]
    public async Task<string> Search(
        [Description("Search term matched against memory names and descriptions.")] string query,
        [Description("Optional comma-separated scope order, e.g. local,api,ephemeral. " +
                     "Topic names filter to local topics (e.g. 'api' shows api topic entries).")] string? scopes = null,
        [Description("Maximum results to return.")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Compute supplemental scores from plugin (e.g. embeddings) if available
        var contributor = SearchContributorContext.Current;
        IReadOnlyDictionary<string, double>? supplemental = null;
        if (contributor is not null)
        {
            var candidates = store.ListScoped(scopes);
            supplemental = await contributor.ComputeScoresAsync(query, candidates, store, cancellationToken);
        }

        IReadOnlyList<SearchResult> matches = supplemental is { Count: > 0 }
            ? store.SearchAll(query, scopes, limit, supplemental)
            : store.SearchAll(query, scopes, limit);
        if (matches.Count == 0)
            return "No matching memories found.";

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

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "copy"), Description(
        "Copies a memory artifact from one scope to another. " +
        "Use to move between topics, promote ephemeral to persistent, " +
        "or reorganize project knowledge.")]
    public Task<string> Copy(
        [Description("Memory name or file:// URI to copy.")] string nameOrUri,
        [Description("Destination as qualified name (e.g. 'api:auth-flow' or 'my-notes'). " +
                     "Use '~name' for ephemeral destination.")] string destination,
        [Description("When true, replaces destination memory if it already exists.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        bool ok = CurrentStore.CopyMemory(nameOrUri, destination, overwrite, out string msg);
        if (!ok) return Task.FromResult(msg);
        return Task.FromResult(msg);
    }

    [McpServerTool(Name = "forget"), Description(
        "Removes a stored artifact and its index entry. " +
        "Use to clean up outdated or incorrect memories. " +
        "Accepts a qualified name (e.g. 'session-notes', 'api:auth-flow', '~scratch'). " +
        "Note: this modifies .scrinia/ in the workspace — treat those file changes as your own.")]
    public async Task<string> Forget(
        [Description("The artifact name (e.g. \"session-notes\", \"api:auth\", \"~scratch\") or its file:// URI.")] string nameOrUri,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Ephemeral memory (~name)
        if (store.IsEphemeral(nameOrUri))
        {
            string key = MemoryNaming.StripEphemeralPrefix(nameOrUri);
            if (!store.ForgetEphemeral(key))
                return $"Error: no ephemeral memory found with name '~{key}'.";

            try { await (MemoryEventSinkContext.Current?.OnForgottenAsync($"~{key}", true, store, cancellationToken) ?? Task.CompletedTask); }
            catch { /* plugin errors must not block forget */ }

            return $"Forgot: ~{key}";
        }

        // Backward compat: handle file:// URIs silently
        if (nameOrUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            string filePath = nameOrUri[7..];
            string name = FileMemoryStore.NameFromUri(nameOrUri);
            bool fileDeleted = false;

            if (File.Exists(filePath))
            {
                try { File.Delete(filePath); fileDeleted = true; }
                catch (Exception ex) { return $"Error: could not delete file: {ex.Message}"; }
            }

            bool removedAny = false;
            foreach (string s in store.ResolveReadScopes())
                removedAny |= store.Remove(name, s);

            if (!removedAny && !fileDeleted)
                return $"Error: no artifact found with name or URI '{nameOrUri}'.";

            try { await (MemoryEventSinkContext.Current?.OnForgottenAsync(name, fileDeleted || removedAny, store, cancellationToken) ?? Task.CompletedTask); }
            catch { /* plugin errors must not block forget */ }

            return $"Forgot: {name}. Files in .scrinia/ were updated — these are your changes.";
        }

        var (scope, subject) = store.ParseQualifiedName(nameOrUri);
        string qualifiedName = store.FormatQualifiedName(scope, subject);

        // Delete the artifact file
        bool deleted = store.DeleteArtifact(subject, scope);

        // Remove index entry
        bool removed = store.Remove(subject, scope);
        if (!removed && !deleted)
            return $"Error: no artifact found with name '{nameOrUri}'.";

        try { await (MemoryEventSinkContext.Current?.OnForgottenAsync(qualifiedName, deleted || removed, store, cancellationToken) ?? Task.CompletedTask); }
        catch { /* plugin errors must not block forget */ }

        return $"Forgot: {qualifiedName}. Files in .scrinia/ were updated — these are your changes.";
    }

    // ── Export/Import tools ───────────────────────────────────────────────────

    [McpServerTool(Name = "export"), Description(
        "Export one or more local topics into a portable .scrinia-bundle file. " +
        "Use to share project knowledge across workspaces or with teammates. " +
        "The bundle contains all entries from the specified topics.")]
    public Task<string> Export(
        [Description("Topic names to export (e.g. [\"api\", \"arch\"]).")] string[] topics,
        [Description("Output filename (saved to .scrinia/exports/). Defaults to auto-generated name.")] string? filename = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;
        if (topics is null || topics.Length == 0)
            return Task.FromResult("Error: at least one topic name is required.");

        string exportsDir = Path.Combine(store.GetStoreDirForScope("local"), "..", "exports");
        exportsDir = Path.GetFullPath(exportsDir);
        Directory.CreateDirectory(exportsDir);

        string bundleName = string.IsNullOrWhiteSpace(filename)
            ? $"export-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}"
            : filename;
        if (!bundleName.EndsWith(".scrinia-bundle", StringComparison.OrdinalIgnoreCase))
            bundleName += ".scrinia-bundle";

        string bundlePath = Path.Combine(exportsDir, bundleName);

        int totalEntries = 0;
        var exportedTopics = new List<string>();

        using (var stream = new FileStream(bundlePath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (string topic in topics)
            {
                string topicScope = $"local-topic:{store.SanitizeName(topic.Trim())}";
                var artifacts = store.ListTopicArtifacts(topicScope);
                var entries = store.LoadIndex(topicScope);

                if (entries.Count == 0)
                    continue;

                string sanitizedTopic = store.SanitizeName(topic.Trim());
                exportedTopics.Add(sanitizedTopic);

                // Write index.json for this topic
                string indexJson = JsonSerializer.Serialize(new BundleIndex(entries), BundleJsonOptions);
                var indexEntry = zip.CreateEntry($"topics/{sanitizedTopic}/index.json");
                using (var writer = new StreamWriter(indexEntry.Open()))
                    writer.Write(indexJson);

                // Write artifact files
                foreach (var (name, filePath) in artifacts)
                {
                    string artifactContent = File.ReadAllText(filePath);
                    string entryName = $"topics/{sanitizedTopic}/{store.SanitizeName(name)}.nmp2";
                    var zipEntry = zip.CreateEntry(entryName);
                    using var writer = new StreamWriter(zipEntry.Open());
                    writer.Write(artifactContent);
                    totalEntries++;
                }
            }

            // Write manifest
            var manifest = new BundleManifest(1, DateTimeOffset.UtcNow.ToString("o"), exportedTopics, totalEntries);
            string manifestJson = JsonSerializer.Serialize(manifest, BundleJsonOptions);
            var manifestEntry = zip.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifestEntry.Open()))
                writer.Write(manifestJson);
        }

        if (exportedTopics.Count == 0)
        {
            // Clean up empty bundle
            try { File.Delete(bundlePath); } catch { }
            return Task.FromResult("Error: no entries found in the specified topics.");
        }

        long fileSize = new FileInfo(bundlePath).Length;
        return Task.FromResult(
            $"Exported {exportedTopics.Count} topic(s) ({totalEntries} entries, {FormatBytes(fileSize)}) to {bundlePath}");
    }

    [McpServerTool(Name = "import"), Description(
        "Import topics from a .scrinia-bundle file into the local workspace. " +
        "Use to bring in shared knowledge from other projects or teammates. " +
        "Optionally filter which topics to import.")]
    public Task<string> Import(
        [Description("Path to the .scrinia-bundle file (relative to workspace or absolute).")] string bundlePath,
        [Description("Optional topic names to import. If empty, imports all topics in the bundle.")] string[]? topics = null,
        [Description("When true, replaces existing entries if they conflict.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Resolve path relative to workspace root if not absolute
        string resolvedPath = Path.IsPathRooted(bundlePath)
            ? bundlePath
            : Path.Combine(Path.GetDirectoryName(store.GetStoreDirForScope("local"))!, "..", bundlePath);
        resolvedPath = Path.GetFullPath(resolvedPath);

        if (!File.Exists(resolvedPath))
            return Task.FromResult($"Error: bundle file not found: {resolvedPath}");

        int importedTopics = 0;
        int importedEntries = 0;
        var importedTopicNames = new List<string>();

        using (var stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            // Read manifest to discover topics
            var manifestEntry = zip.GetEntry("manifest.json");
            if (manifestEntry is null)
                return Task.FromResult("Error: invalid bundle — manifest.json not found.");

            string manifestJson;
            using (var reader = new StreamReader(manifestEntry.Open()))
                manifestJson = reader.ReadToEnd();

            using var manifestDoc = JsonDocument.Parse(manifestJson);
            var root = manifestDoc.RootElement;

            if (!root.TryGetProperty("topics", out var topicsElement))
                return Task.FromResult("Error: invalid bundle — no topics in manifest.");

            var availableTopics = new List<string>();
            foreach (var t in topicsElement.EnumerateArray())
            {
                string? topicName = t.GetString();
                if (topicName is not null)
                    availableTopics.Add(topicName);
            }

            // Filter topics if specified
            var topicsToImport = topics is { Length: > 0 }
                ? availableTopics.Where(t => topics.Any(f => f.Trim().Equals(t, StringComparison.OrdinalIgnoreCase))).ToList()
                : availableTopics;

            foreach (string topic in topicsToImport)
            {
                string topicScope = $"local-topic:{store.SanitizeName(topic)}";

                // Read index from bundle
                var indexZipEntry = zip.GetEntry($"topics/{topic}/index.json");
                if (indexZipEntry is null)
                    continue;

                string indexJson;
                using (var reader = new StreamReader(indexZipEntry.Open()))
                    indexJson = reader.ReadToEnd();

                using var indexDoc = JsonDocument.Parse(indexJson);
                var indexRoot = indexDoc.RootElement;

                if (!indexRoot.TryGetProperty("entries", out var entriesElement))
                    continue;

                var entries = new List<ArtifactEntry>();
                var artifactContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entryEl in entriesElement.EnumerateArray())
                {
                    string? name = entryEl.GetProperty("name").GetString();
                    if (name is null) continue;

                    long originalBytes = entryEl.TryGetProperty("originalBytes", out var ob) ? ob.GetInt64() : 0;
                    int chunkCount = entryEl.TryGetProperty("chunkCount", out var cc) ? cc.GetInt32() : 1;
                    string description = entryEl.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                    string? contentPreview = entryEl.TryGetProperty("contentPreview", out var cp) ? cp.GetString() : null;

                    string[]? tags = null;
                    if (entryEl.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                        tags = tagsEl.EnumerateArray().Select(t => t.GetString() ?? "").Where(t => t.Length > 0).ToArray();

                    DateTimeOffset createdAt = entryEl.TryGetProperty("createdAt", out var ca)
                        ? DateTimeOffset.Parse(ca.GetString()!)
                        : DateTimeOffset.UtcNow;

                    // Parse chunk entries if present
                    ChunkEntry[]? chunkEntries = null;
                    if (entryEl.TryGetProperty("chunkEntries", out var ceEl) && ceEl.ValueKind == JsonValueKind.Array)
                    {
                        var ceList = new List<ChunkEntry>();
                        foreach (var ce in ceEl.EnumerateArray())
                        {
                            int ci = ce.TryGetProperty("chunkIndex", out var ciEl) ? ciEl.GetInt32() : 0;
                            string? cePrev = ce.TryGetProperty("contentPreview", out var cpEl) ? cpEl.GetString() : null;
                            string[]? ceKw = null;
                            if (ce.TryGetProperty("keywords", out var kwEl) && kwEl.ValueKind == JsonValueKind.Array)
                                ceKw = kwEl.EnumerateArray().Select(k => k.GetString() ?? "").Where(k => k.Length > 0).ToArray();
                            Dictionary<string, int>? ceTf = null;
                            if (ce.TryGetProperty("termFrequencies", out var tfEl) && tfEl.ValueKind == JsonValueKind.Object)
                            {
                                ceTf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                foreach (var prop in tfEl.EnumerateObject())
                                    if (prop.Value.TryGetInt32(out int v)) ceTf[prop.Name] = v;
                            }
                            ceList.Add(new ChunkEntry(ci, cePrev, ceKw, ceTf));
                        }
                        if (ceList.Count > 0) chunkEntries = ceList.ToArray();
                    }

                    entries.Add(new ArtifactEntry(
                        Name: name,
                        Uri: "",
                        OriginalBytes: originalBytes,
                        ChunkCount: chunkCount,
                        CreatedAt: createdAt,
                        Description: description,
                        Tags: tags,
                        ContentPreview: contentPreview,
                        ChunkEntries: chunkEntries));

                    // Read artifact content from zip
                    string artifactEntryName = $"topics/{topic}/{store.SanitizeName(name)}.nmp2";
                    var artifactZipEntry = zip.GetEntry(artifactEntryName);
                    if (artifactZipEntry is not null)
                    {
                        using var reader = new StreamReader(artifactZipEntry.Open());
                        artifactContents[name] = reader.ReadToEnd();
                    }
                }

                if (entries.Count > 0)
                {
                    store.ImportTopicEntries(topicScope, entries, artifactContents, overwrite);
                    importedTopics++;
                    importedEntries += entries.Count;
                    importedTopicNames.Add(topic);
                }
            }
        }

        if (importedTopics == 0)
            return Task.FromResult("No topics were imported (empty bundle or all filtered out).");

        return Task.FromResult(
            $"Imported {importedTopics} topic(s) ({importedEntries} entries): {string.Join(", ", importedTopicNames)}");
    }

    // ── Append/Reflect/Budget tools ─────────────────────────────────────────

    [McpServerTool(Name = "append"), Description(
        "Append content as a new independently retrievable chunk to an existing memory, " +
        "or create it if it does not exist. " +
        "Useful for incremental capture — build up session journals entry by entry " +
        "without recomposing the full document each time. " +
        "Note: this writes to .scrinia/ in the workspace — treat those file changes as your own.")]
    public async Task<string> Append(
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
        byte[] fullBytes = new Nmp2Strategy().Decode(newArtifact);
        string fullText = System.Text.Encoding.UTF8.GetString(fullBytes);
        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(newArtifact);
        long originalBytes = fullBytes.LongLength;

        // Compute text analysis from full decoded content (single-pass)
        var (autoKeywords, tf) = TextAnalysis.AnalyzeText(fullText);
        var mergedKeywords = TextAnalysis.MergeKeywords(null, autoKeywords);
        foreach (string kw in mergedKeywords)
        {
            tf.TryGetValue(kw, out int count);
            tf[kw] = count + 2;
        }

        string contentPreview = store.GenerateContentPreview(fullText);

        // Build chunk entry for the newly appended content (single-pass)
        var (newKw, newTf) = TextAnalysis.AnalyzeText(content);
        foreach (string k in newKw) { newTf.TryGetValue(k, out int c); newTf[k] = c + 2; }
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

        // Fire event sink (embeddings, etc.) — never block the response
        try { await (MemoryEventSinkContext.Current?.OnAppendedAsync(qualifiedName, content, store, cancellationToken) ?? Task.CompletedTask); }
        catch { /* plugin errors must not block append */ }

        return $"Appended chunk {chunkCount} to {qualifiedName} ({chunkCount} {(chunkCount == 1 ? "chunk" : "chunks")}, {FormatBytes(originalBytes)}). Files in .scrinia/ were updated — these are your changes.";
    }

    [McpServerTool(Name = "reflect"), Description(
        "Returns a session-end reflection prompt to help decide what knowledge to persist. " +
        "Call this at the end of a work session.")]
    public Task<string> Reflect(CancellationToken cancellationToken = default) =>
        Task.FromResult("""
            # Scrinia Session Reflection Checklist

            Before ending this session, consider persisting knowledge to scrinia from each category:

            ## Decisions Made
            - [ ] Were any architectural or design decisions made? Store rationale.
            - [ ] Were any trade-offs evaluated? Record what was chosen and why.

            ## Patterns Discovered
            - [ ] Did you find any codebase patterns or conventions? Document them.
            - [ ] Did you discover any API usage patterns? Save examples.

            ## Problems Solved
            - [ ] Did you debug a tricky issue? Save the root cause and fix.
            - [ ] Did you find a workaround? Document it before you forget.

            ## API/Library Knowledge
            - [ ] Did you learn API behavior not in docs? Capture it.
            - [ ] Did you find library gotchas? Record them.

            ## Context for Future Sessions
            - [ ] Is there in-progress work that needs context? Save state.
            - [ ] Are there next steps someone should know? Document them.

            ## Stale Knowledge Cleanup
            - [ ] Are any stored scrinia memories now outdated? Update or forget them.
            - [ ] Should any ephemeral (~) scrinia memories be promoted to persistent?

            Use `store()` to persist, `append()` to add to existing, `forget()` to clean up.
            Use `budget()` to see how much context you consumed this session.
            """);

    [McpServerTool(Name = "ingest"), Description(
        "Instructs you to perform a thorough memory ingestion — read all available sources, " +
        "review existing memories, and create or update memories as needed. " +
        "Call this when starting a new project or doing a full knowledge capture pass.")]
    public Task<string> Ingest(CancellationToken cancellationToken = default) =>
        Task.FromResult("""
            # Scrinia Memory Ingestion — Full Knowledge Capture

            Follow these 5 phases to perform a thorough scrinia memory ingestion. Do not skip phases or take shortcuts.

            ## Phase 1 — Inventory existing scrinia memories
            1. Call `list()` for a summary of topics, keywords, and stats.
            2. Call `list(mode="full")` to see all entry names, then `show("name")` to read content.
               - For multi-chunk scrinia memories, use `chunk_count()` then `get_chunk()` for each chunk.
            3. Note which scrinia memories exist, what they cover, and whether any look stale or incomplete.

            ## Phase 2 — Read all available sources
            Read ALL information sources you have access to. Be thorough — partial ingestion leads to gaps.

            **Code and project files:**
            - README, AGENTS.md, CLAUDE.md, CONTRIBUTING.md, and similar guide files
            - Key source files, configuration, and infrastructure (CI, Docker, etc.)
            - Package manifests, dependency files, build scripts

            **Conversation and context:**
            - Everything discussed in the current conversation
            - Any files the user has shared or referenced
            - Error messages, debugging sessions, decisions made

            **Environment:**
            - Git log (recent commits, branch structure)
            - Directory structure and file organization
            - Runtime configuration and environment details

            ## Phase 3 — Analyze and plan
            Compare what you read (Phase 2) against what's already stored (Phase 1):
            - **Missing**: Important knowledge with no scrinia memory coverage
            - **Outdated**: Scrinia memories that contradict current sources
            - **Incomplete**: Scrinia memories that need additional detail
            - **Redundant**: Duplicate or overlapping scrinia memories that should be consolidated
            - **Misorganized**: Scrinia memories in wrong topics or with poor naming

            Plan your updates before executing them.

            ## Phase 4 — Store, update, and organize
            Execute your plan using these tools:
            - `store(content, "topic:subject")` — create new scrinia memories or overwrite outdated ones
            - `append(content, "name")` — add to existing scrinia memories incrementally
            - `forget("name")` — remove obsolete or redundant scrinia memories
            - `copy("old-name", "new-name")` — reorganize into better topics

            **Best practices for this phase:**
            - Use topic:subject naming consistently (e.g., `arch:decisions`, `api:endpoints`)
            - Add keywords for search discoverability: `store(content, name, keywords=["term1", "term2"])`
            - Set review conditions on volatile knowledge: `store(content, name, reviewWhen="when X changes")`
            - Keep each scrinia memory focused — one concept per scrinia memory, split large topics into multiple entries
            - Use chunked storage for large content: `store(["section1", "section2", ...], name)`

            ## Phase 5 — Verify and report
            1. Call `list()` to confirm the final state of all scrinia memories (summary view).
            2. Summarize what you did:
               - Scrinia memories created (with names and brief descriptions)
               - Scrinia memories updated (what changed)
               - Scrinia memories deleted (why)
               - Any gaps you identified but couldn't fill (missing information)
            3. Report the summary to the user.
            """);

    [McpServerTool(Name = "ka"), Description(
        "Knowledge analysis — inventory all persistent memories, read each one completely, " +
        "perform gap analysis, and report findings to the user. Returns the inventory plus " +
        "a playbook for thorough analysis. Use this before kt() or on its own for auditing.")]
    public Task<string> Ka(
        [Description("Optional comma-separated scope filter, e.g. 'local,api,research'. " +
                     "Defaults to all persistent scopes. Ephemeral memories are always excluded.")] string? scopes = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // List all persistent entries, excluding ephemeral
        var allEntries = store.ListScoped(scopes)
            .Where(e => e.Scope != "ephemeral")
            .ToList();

        if (allEntries.Count == 0)
            return Task.FromResult("No persistent memories found.");

        // Group by scope label, sorted alphabetically
        var grouped = allEntries
            .GroupBy(e => MemoryNaming.FormatScopeLabel(e.Scope))
            .OrderBy(g => g.Key)
            .Select(g => (
                Label: g.Key,
                Entries: g.OrderBy(e => e.Entry.Name).ToList()))
            .ToList();

        long totalBytes = allEntries.Sum(e => e.Entry.OriginalBytes);
        int topicCount = grouped.Count(g => g.Label != "local");

        var sb = new System.Text.StringBuilder();

        // ── Inventory ────────────────────────────────────────────────────
        sb.AppendLine("# Knowledge Analysis — Memory Inventory");
        sb.AppendLine();
        sb.AppendLine($"**{allEntries.Count} {(allEntries.Count == 1 ? "memory" : "memories")}** across " +
                       $"**{topicCount} {(topicCount == 1 ? "topic" : "topics")}** ({FormatBytes(totalBytes)} total)");
        sb.AppendLine();

        foreach (var (label, entries) in grouped)
        {
            string heading = label == "local" ? "Local" : $"Topic: {label}";
            sb.AppendLine($"### {heading}");
            foreach (var item in entries)
            {
                var e = item.Entry;
                string qualName = store.FormatQualifiedName(item.Scope, e.Name);
                string size = FormatBytes(e.OriginalBytes);
                string desc = e.Description.Length > 80
                    ? e.Description[..80] + "..."
                    : e.Description;
                string tags = e.Tags is { Length: > 0 } ? $" [tags: {string.Join(", ", e.Tags)}]" : "";
                string review = "";
                if (e.ReviewAfter.HasValue || !string.IsNullOrEmpty(e.ReviewWhen))
                {
                    var parts = new List<string>();
                    if (e.ReviewAfter.HasValue) parts.Add($"after {e.ReviewAfter.Value:yyyy-MM-dd}");
                    if (!string.IsNullOrEmpty(e.ReviewWhen)) parts.Add(e.ReviewWhen);
                    review = $" **[REVIEW: {string.Join("; ", parts)}]**";
                }
                sb.AppendLine($"- **{qualName}** ({size}, {e.ChunkCount} chunk{(e.ChunkCount == 1 ? "" : "s")}, created {e.CreatedAt:yyyy-MM-dd}) — {desc}{tags}{review}");
            }
            sb.AppendLine();
        }

        // ── Analysis Playbook ────────────────────────────────────────────
        sb.AppendLine("""
            ## Playbook

            Perform a thorough knowledge analysis and **report the findings to the user**. Do not write KT documents yet — this is analysis only.

            ### Step 1 — Read every scrinia memory completely
            - Call `show("name")` for each scrinia memory listed above. Do NOT skip or skim any.
            - For multi-chunk scrinia memories: call `chunk_count()` then `get_chunk()` for every chunk.
            - Read them in the order listed (grouped by topic, alphabetical within each group).

            ### Step 2 — Analyze each scrinia memory
            As you read, evaluate each scrinia memory for:
            - **Accuracy**: does the content match what you know about the codebase/project?
            - **Completeness**: is important information missing from this scrinia memory?
            - **Staleness**: has the content drifted from reality? Check review markers especially.
            - **Cross-references**: what other scrinia memories does this one relate to or depend on?
            - **Contradictions**: does this conflict with other scrinia memories or with the codebase?

            ### Step 3 — Identify gaps
            After reading ALL scrinia memories, identify knowledge areas that SHOULD exist but DON'T:
            - Important project features or components with no scrinia memory coverage
            - Decisions, patterns, or conventions that are undocumented
            - Onboarding knowledge a new contributor would need
            - Operational knowledge (deployment, debugging, monitoring) that's missing
            - Relationships between components that aren't captured anywhere

            ### Step 4 — Report to the user
            Present a structured analysis report covering:

            **Coverage summary**: What domains/topics are well-covered vs. sparse?

            **Staleness audit**: List scrinia memories with review markers, suspected outdated content, or confirmed drift. Be specific about what's wrong.

            **Gap analysis**: List missing knowledge areas, ordered by importance. For each gap, note:
            - What should be documented
            - Why it matters
            - Which topic it belongs in

            **Contradiction check**: Any conflicting information between scrinia memories?

            **Recommended actions**: Concrete, prioritized list of:
            - Scrinia memories to update (with what needs changing)
            - Scrinia memories to create (with suggested names and content scope)
            - Scrinia memories to delete or consolidate
            - Scrinia memories to reorganize (wrong topic, poor naming)

            ### Quality checklist
            Before reporting, verify:
            - [ ] Every scrinia memory from the inventory was fully read (no skipping)
            - [ ] Multi-chunk scrinia memories were completely read (all chunks)
            - [ ] Gap analysis considers the project holistically, not just what's stored
            - [ ] Recommendations are specific and actionable (not vague suggestions)
            - [ ] Staleness concerns cite specific evidence
            """);

        return Task.FromResult(sb.ToString().TrimEnd());
    }

    [McpServerTool(Name = "kt"), Description(
        "Knowledge transfer — produce focused, per-topic KT documents from persistent memories. " +
        "Runs knowledge analysis first (via ka()), then creates multiple smaller documents " +
        "grouped by topic. Better quality than a single monolithic document.")]
    public Task<string> Kt(
        [Description("Optional comma-separated scope filter, e.g. 'local,api,research'. " +
                     "Defaults to all persistent scopes. Ephemeral memories are always excluded.")] string? scopes = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // List all persistent entries, excluding ephemeral
        var allEntries = store.ListScoped(scopes)
            .Where(e => e.Scope != "ephemeral")
            .ToList();

        if (allEntries.Count == 0)
            return Task.FromResult("No persistent memories found.");

        // Group by scope label
        var grouped = allEntries
            .GroupBy(e => MemoryNaming.FormatScopeLabel(e.Scope))
            .OrderBy(g => g.Key)
            .Select(g => (
                Label: g.Key,
                Entries: g.OrderBy(e => e.Entry.Name).ToList()))
            .ToList();

        int topicCount = grouped.Count(g => g.Label != "local");

        var sb = new System.Text.StringBuilder();

        sb.AppendLine("# Knowledge Transfer — Multi-Document Playbook");
        sb.AppendLine();
        sb.AppendLine($"**{allEntries.Count} {(allEntries.Count == 1 ? "memory" : "memories")}** across " +
                       $"**{topicCount} {(topicCount == 1 ? "topic" : "topics")}** to transfer.");
        sb.AppendLine();
        sb.AppendLine("## Documents to produce");
        sb.AppendLine();

        foreach (var (label, entries) in grouped)
        {
            string docName = label == "local" ? "Local Memories" : $"Topic: {label}";
            sb.AppendLine($"- **{docName}** ({entries.Count} {(entries.Count == 1 ? "memory" : "memories")})");
            foreach (var item in entries)
            {
                var e = item.Entry;
                string qualName = store.FormatQualifiedName(item.Scope, e.Name);
                sb.AppendLine($"  - {qualName}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("""
            ## Playbook

            ### Phase 1 — Analysis
            Call `ka()` to perform a full knowledge analysis. Read every scrinia memory and review the analysis report before writing anything.

            ### Phase 2 — Write one KT document per topic/group
            For each group listed above, produce a **separate, focused markdown document**. Each document should be self-contained and useful on its own.

            **Per-document structure:**

            1. **Title and scope**: Which topic/group this document covers
            2. **Summary**: 2-3 paragraph overview of what this knowledge area covers and why it matters
            3. **Per-memory sections**: For each scrinia memory in the group:
               - Heading with the qualified name
               - Full content — preserve faithfully, reformat for readability
               - Your annotations: cross-references, staleness notes, gaps
            4. **Group-level notes**: Connections between scrinia memories in this group, patterns, and any missing coverage specific to this topic

            **Writing guidelines:**
            - Each document should make sense independently — a reader shouldn't need other KT documents to understand this one
            - Preserve the actual knowledge faithfully — don't summarize away important details
            - Add cross-references to other KT documents where relevant (e.g., "see also: api topic KT")
            - Flag any issues found during the ka() analysis that are relevant to this group

            ### Phase 3 — Write a transfer index
            After all per-topic documents are written, produce a short **KT Index** document containing:
            - List of all KT documents produced (with brief descriptions)
            - Cross-cutting themes that span multiple topics
            - The gap analysis and recommended actions from the ka() report
            - Any contradictions or staleness concerns found

            ### Phase 4 — Deliver
            - Deliver each document separately (file, inline, or however the platform supports)
            - The index document should be delivered last as the entry point
            - Confirm delivery with the user

            ## Quality checklist
            Before delivering, verify:
            - [ ] ka() was called and its analysis was incorporated
            - [ ] Every scrinia memory from the inventory appears in exactly one KT document
            - [ ] Multi-chunk scrinia memories were fully read (all chunks, not just the first)
            - [ ] Each document is self-contained and readable independently
            - [ ] Cross-references between documents are noted
            - [ ] The index document summarizes all KT documents and includes gap analysis
            """);

        return Task.FromResult(sb.ToString().TrimEnd());
    }

    [McpServerTool(Name = "budget"), Description(
        "Reports estimated token consumption for this session. " +
        "Shows what you have loaded via show() and get_chunk(), " +
        "with per-memory breakdown and total.")]
    public Task<string> Budget(CancellationToken cancellationToken = default)
    {
        var breakdown = SessionBudget.Breakdown;
        if (breakdown.Count == 0)
            return Task.FromResult("No memories loaded this session.");

        const int nameW = 20;
        const int charsW = 10;
        const int tokensW = 10;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{"name".PadRight(nameW)}  {"chars",charsW}  {"~tokens",tokensW}");
        sb.AppendLine(new string('-', nameW + charsW + tokensW + 6));

        foreach (var kvp in breakdown.OrderByDescending(k => k.Value.Chars))
        {
            string displayName = kvp.Key.Length > nameW ? kvp.Key[..nameW] : kvp.Key;
            sb.AppendLine($"{displayName.PadRight(nameW)}  {kvp.Value.Chars,charsW}  {kvp.Value.EstTokens,tokensW}");
        }

        sb.AppendLine(new string('-', nameW + charsW + tokensW + 6));
        sb.AppendLine($"{"TOTAL".PadRight(nameW)}  {SessionBudget.TotalCharsLoaded,charsW}  {SessionBudget.EstimatedTokensLoaded,tokensW}");

        return Task.FromResult(sb.ToString().TrimEnd());
    }

    private static ChunkEntry[] ComputeChunkEntries(IMemoryStore store, string[] chunks)
    {
        var entries = new ChunkEntry[chunks.Length];
        for (int i = 0; i < chunks.Length; i++)
        {
            var (kw, tf) = TextAnalysis.AnalyzeText(chunks[i]);
            foreach (string k in kw) { tf.TryGetValue(k, out int c); tf[k] = c + 2; }
            string preview = store.GenerateContentPreview(chunks[i]);
            entries[i] = new ChunkEntry(
                ChunkIndex: i + 1,
                ContentPreview: string.IsNullOrEmpty(preview) ? null : preview,
                Keywords: kw.Length > 0 ? kw : null,
                TermFrequencies: tf.Count > 0 ? tf : null);
        }
        return entries;
    }

    public static string FormatBytes(long bytes) =>
        bytes switch
        {
            < 1_024 => $"{bytes} B",
            < 1_048_576 => $"{bytes / 1_024.0:F1} KB",
            < 1_073_741_824 => $"{bytes / 1_048_576.0:F1} MB",
            _ => $"{bytes / 1_073_741_824.0:F1} GB",
        };
}
