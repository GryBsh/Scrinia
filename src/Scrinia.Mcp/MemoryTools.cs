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

[McpServerToolType]
public sealed class ScriniaMcpTools
{

    private static readonly ConcurrentDictionary<string, ConflictEntry> _activeConflicts = new();
    private sealed record ConflictEntry(string FilePath, string Type, string? OursContent, string? TheirsContent);

    private static readonly Regex CountPattern = new(
        @"\b\d+\s+(tests?|tools?|skills?|endpoints?|routes?|models?|projects?|memories|concerns?|phases?|goals?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static IMemoryStore CurrentStore =>
        MemoryStoreContext.Current ?? throw new InvalidOperationException(
            "No memory store configured. Call MemoryStoreContext.Current = ... before using MCP tools.");

    /// <summary>
    /// Resolves inline NMP/2 artifacts without requiring a configured store.
    /// Returns null if the input requires store-based resolution (memory name, file://, ephemeral, etc.).
    /// file:// URIs are deliberately NOT handled here — they require store-based resolution
    /// for workspace sandbox validation (see FileMemoryStore.ResolveArtifactAsync).
    /// </summary>
    private static Task<string?> TryResolveWithoutStore(string input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Task.FromResult<string?>(null);

        // Inline NMP/2 artifact
        if (input.TrimStart().StartsWith("NMP/2 ", StringComparison.Ordinal))
            return Task.FromResult<string?>(input);

        return Task.FromResult<string?>(null);
    }

    [McpServerTool(Name = "guide"), Description(
        "Required reading — call at session start, then commit content to your project's agent file. " +
        "If a project exists (.scrinia/ directory), check plan('status') for active goals before starting new work. " +
        "Covers memory patterns, the goal-driven planning workflow, and when to plan vs. just do.")]
    public Task<string> Guide(CancellationToken cancellationToken = default) =>
        Task.FromResult("""
            [INSTRUCTION]
            # scrinia guide — cognitive toolset for LLM agents

            Scrinia gives you three capabilities — use them fluidly, not sequentially:
            - **Research** — search memories and skills, investigate the codebase, form hypotheses
            - **Plan & Do** — set goals, decompose into tasks, execute via spawned agents, verify
            - **Learn** — update beliefs, evolve skills, store what you figured out

            ## First session setup
            1. Commit this guide to your project's agent file (AGENTS.md, CLAUDE.md, or equivalent)
            2. If `.scrinia/` exists, call `plan('status')` to check for active goals
            3. If a project exists but no active goal, ask the user what to work on → `goal('add')`
            4. If no project exists, call `plan('init')` when the user's request warrants planning

            ## Before you start any work
            Always call memory('search') before answering questions or performing work.
            Check prior sessions, existing knowledge, and agent behavioral norms.
            This is not optional — context from prior work prevents redundant investigation
            and ensures consistency with established patterns and decisions.

            Proactively create memories as you work — don't wait to be asked.
            When you learn something, discover a pattern, or figure out a non-obvious solution,
            store it immediately. The goal is a knowledge base that makes the next session smarter.

            ## When to plan vs. just do
            - **Every goal** goes through the full workflow: goal('add') → task('next') → spawn → task('complete')
            - **Questions and quick lookups** don't need a goal — just answer using memory('search') first
            - **When in doubt**: set a goal — the auditor will right-size the scope

            ## Memory habits
            - **When you learn something** → `memory('store', { name: "topic:subject", content: [...] })`
            - **When you fix a bug** → store the root cause and fix pattern
            - **When the user corrects you** → store the correction as a pattern
            - **When you discover a convention** → store it for future sessions
            - **When starting work** → `memory('search')` first to check prior sessions
            - **Maintain a session log** → `memory('store')` or `memory('append')` to `sessions:YYYY-MM-DD`
            One concept per chunk — each chunk is independently searchable with its own keywords.
            Use multiple elements in `memory('store')` to control semantic boundaries, not just size.

            **Scrinia is the single source of truth.** Do not use platform-specific memory systems
            (Claude auto-memory, Cursor notes, etc.) for project knowledge. All persistence through scrinia.

            ## Skill habits
            - Skills are methodology (how to work), memories are knowledge (what you know)
            - `skill('load')` lists available skills; `skill('load', { name: "..." })` activates one
            - `skill('create')` to capture effective approaches as reusable methodology
            - Built-in skills: planner, auditor, debugger, chaos-engineer, onboarder,
              sos-handler, evolutionary, cartographer, march-reporter, merge-safety, qa, self-reflector
            - **Precedence**: project memory overrides built-in. Built-in for universal features,
              `skill('create')` for project-specific evolution.

            ## Ephemeral memories
            `~` prefix for in-session working state: `memory('store', { name: "~scratch", content: ["data"] })`.
            Dies on process exit. Promote via `memory('copy', { name: "~scratch", destination: "topic:name" })`.

            ## Topics and naming
            `topic:subject` organizes memories: `api:auth-flow`, `patterns:retry`, `bugs:sqlite`.
            Auto-discovered — no setup needed. Names are auto-prefixed with goal ID when active
            (e.g. `task:g36-4a2-01-1-01`). `memory('search', { query: "task:g36" })` scopes to one goal.

            ## Reserved planning topics
            | Topic | Purpose |
            |-------|---------|
            | `project:*` | project context, requirements, state |
            | `plan:*` | planning state |
            | `task:*` | decomposed tasks (goal-prefixed) |
            | `learn:*` | retrospectives, beliefs (per-phase files) |
            | `agent:*` | behavioral norms |
            | `research:*` | investigation findings (goal-prefixed) |
            | `concern:*` | tracked risks |
            | `skill:*` | reusable specialist prompts |
            | `backlog:*` | deferred work and future ideas |

            Use `excludeTopics="plan,task,project,learn,backlog"` to hide planning data.

            ## Chunks as semantic boundaries
            Use `memory('show', { name: "..." })` for full content, `memory('show', { name: "...", chunk: 2 })` for a specific chunk.
            Each chunk in a multi-element `memory('store')` call is independently retrievable and searchable.
            Design chunks around concepts, not size.

            ## Versioning, review, and context
            - Overwriting a memory auto-archives the previous version
            - `memory('store', { ..., reviewAfter: "2026-06-01" })` or `reviewWhen: "when auth changes"` for staleness
            - `memory('store', { name: "~checkpoint", content: ["state..."] })` before large tasks to survive context compression

            ## When to store vs. not
            Store: anything you'd want to know in a fresh session tomorrow.
            Don't store: transient working state (use `~ephemeral`), things derivable from code/git.
            Rule of thumb: if you had to figure it out, store it.

            ## Workspace changes
            Scrinia tools write to `.scrinia/` — treat those changes as yours. Include in commits.

            ## Project planning — the goal-driven cycle

            **1. Pre-plan** → scan codebase for concerns → `concern('add')`
            **2. Set a goal** → `goal('add')` — auto-creates researcher → auditor → planner seed tasks
            **3. Execute** — the orchestrator's only loop is `task('next')` → spawn → `task('complete')`:
              - `goal('add')` auto-creates researcher (wave 0) → auditor (wave 1) → planner (wave 2)
              - Researcher explores codebase and stores findings via `memory('store')`
              - Auditor reads research, calls `requirement('add')` + `concern('add')` with full context
              - Planner reads research + requirements, calls `plan('tasks')` directly
              - `plan('tasks')` auto-injects gate tasks: QA + self-reflector every phase, evolutionary/cartographer/march on last phase
              - The primary agent orchestrates — it does not execute tasks directly
              - Use `run_in_background: true` for execution agents
            **4. Complete** → `goal('complete')` — auto-appends session log + checkpoint

            ## Recovery
            - `memory('restore')` — full context restoration (project state, agent profile, session log, task nudge)
            - `plan('status')` — quick progress check
            - `memory('search', { query: "agent:" })` — load behavioral norms

            ## Multi-user merge safety
            Sorted metadata, per-phase retros, per-file sidecars prevent most conflicts.
            After merging: `memory('reconcile')` → resolve each conflict → verify clean.
            For team setup (merge driver, hooks): see docs/multi-user-setup.md

            ## Test count tracking
            Use `codeRefs` on testing memories pointing to `.csproj` files.
            `memory('list', { mode: "drift" })` flags when test projects change → evolutionary updates counts.
            [/INSTRUCTION]
            """);

    /// <summary>Unified memory tool — dispatches to action-specific handlers.</summary>
    [McpServerTool(Name = "memory"), Description(
        "Unified memory operations. Actions: " +
        "'store' (persist content with keywords/review/codeRefs), " +
        "'append' (add chunk to existing memory), " +
        "'show' (read/decode memory content, optional chunk), " +
        "'search' (find memories by query), " +
        "'list' (browse memories — summary/full/drift modes), " +
        "'forget' (delete memory), " +
        "'copy' (copy between scopes), " +
        "'compact' (merge chunks in a memory), " +
        "'update' (update metadata without re-encoding), " +
        "'link' (add codeRefs to a memory), " +
        "'references' (find what references a memory), " +
        "'restore' (resume full agent context — project state, agent profile, session log, task nudge), " +
        "'reconcile' (scan for merge conflicts or resolve a specific conflict).")]
    public async Task<string> Memory(
        [Description("Action to perform: 'store', 'append', 'show', 'search', 'list', 'forget', 'copy', 'compact', 'update', 'link', 'references', 'restore', 'reconcile'.")] string action,
        [Description("Memory name (store/append/show/forget/compact/update/link/references).")] string? name = null,
        [Description("Content array — each element becomes one chunk (store).")] string[]? content = null,
        [Description("Text content to append as new chunk (append).")] string? appendContent = null,
        [Description("Search query string (search).")] string? query = null,
        [Description("Destination name for copy, or 'to' name for link.")] string? destination = null,
        [Description("Optional description (store, update).")] string? description = null,
        [Description("Optional tags (store).")] string[]? tags = null,
        [Description("Optional keywords for search — merged with auto-extracted (store, update).")] string[]? keywords = null,
        [Description("ISO 8601 review date (store, update).")] string? reviewAfter = null,
        [Description("Free-text review condition (store, update).")] string? reviewWhen = null,
        [Description("File paths this memory depends on (store).")] string[]? codeRefs = null,
        [Description("Chunk index to retrieve, 1-based (show).")] int? chunk = null,
        [Description("Comma-separated scope order (list, search).")] string? scopes = null,
        [Description("List mode: 'summary', 'full', or 'drift' (list).")] string? mode = null,
        [Description("Starting index for full mode (list).")] int offset = 0,
        [Description("Max results (list default 50, search default 20).")] int limit = 50,
        [Description("Comma-separated topics to exclude (list, search).")] string? excludeTopics = null,
        [Description("Overwrite existing on copy (copy).")] bool overwrite = false,
        [Description("Keep only N recent chunks, 0 = merge all (compact).")] int keepRecent = 0,
        [Description("Reason for linking (link).")] string? reason = null,
        [Description("Conflict ID to resolve (reconcile).")] string? conflictId = null,
        [Description("Resolution: 'ours', 'theirs', or 'merged' (reconcile).")] string? choice = null,
        [Description("Content for 'merged' resolution (reconcile).")] string? mergedContent = null,
        CancellationToken cancellationToken = default)
    {
        string act = action.Trim().ToLowerInvariant();
        switch (act)
        {
            case "store":
                const int MaxNameLength = 256;
                const int MaxContentBytesPerElement = 5 * 1024 * 1024; // 5 MB
                if (content is null || content.Length == 0) return "Error: memory('store') requires 'content' parameter.";
                if (string.IsNullOrWhiteSpace(name)) return "Error: memory('store') requires 'name' parameter.";
                if (name.Length > MaxNameLength)
                    return $"Error: name exceeds {MaxNameLength} characters.";
                foreach (var element in content)
                {
                    if (element != null && System.Text.Encoding.UTF8.GetByteCount(element) > MaxContentBytesPerElement)
                        return $"Error: content element exceeds {MaxContentBytesPerElement / (1024 * 1024)} MB limit.";
                }
                return await Store(content, name, description ?? "", tags, keywords, reviewAfter, reviewWhen, codeRefs, cancellationToken);

            case "append":
                if (string.IsNullOrWhiteSpace(appendContent)) return "Error: memory('append') requires 'appendContent' parameter.";
                if (string.IsNullOrWhiteSpace(name)) return "Error: memory('append') requires 'name' parameter.";
                if (appendContent != null && System.Text.Encoding.UTF8.GetByteCount(appendContent) > 5 * 1024 * 1024)
                    return "Error: append content exceeds 5 MB limit.";
                return await Append(appendContent, name, cancellationToken);

            case "show":
                if (string.IsNullOrWhiteSpace(name)) return "Error: memory('show') requires 'name' parameter.";
                return await Show(name, chunk, cancellationToken);

            case "search":
                if (string.IsNullOrWhiteSpace(query)) return "Error: memory('search') requires 'query' parameter.";
                return await Search(query, scopes, limit, excludeTopics, cancellationToken);

            case "list":
                return await List(scopes, mode ?? "summary", offset, limit, excludeTopics, cancellationToken);

            case "forget":
                if (string.IsNullOrWhiteSpace(name)) return "Error: memory('forget') requires 'name' parameter.";
                return await Forget(name, cancellationToken);

            case "copy":
                if (string.IsNullOrWhiteSpace(name)) return "Error: memory('copy') requires 'name' parameter.";
                if (string.IsNullOrWhiteSpace(destination)) return "Error: memory('copy') requires 'destination' parameter.";
                return await Copy(name, destination, overwrite, cancellationToken);

            case "compact":
                if (string.IsNullOrWhiteSpace(name)) return "Error: memory('compact') requires 'name' parameter.";
                return await Compact(name, keepRecent, cancellationToken);

            case "update":
                if (string.IsNullOrWhiteSpace(name)) return "Error: memory('update') requires 'name' parameter.";
                return await UpdateMeta(name, keywords, description, reviewAfter, reviewWhen, cancellationToken);

            case "link":
                if (string.IsNullOrWhiteSpace(name)) return "Error: memory('link') requires 'name' parameter.";
                if (string.IsNullOrWhiteSpace(destination)) return "Error: memory('link') requires 'destination' parameter.";
                return await Link(name, destination, reason, cancellationToken);

            case "references":
                if (string.IsNullOrWhiteSpace(name)) return "Error: memory('references') requires 'name' parameter.";
                return await References(name, cancellationToken);

            case "restore":
                return await Restore(cancellationToken);

            case "reconcile":
                return await Reconcile(conflictId, choice, mergedContent, cancellationToken);

            default:
                return $"Error: unknown action '{action}'. Valid actions: store, append, show, search, list, forget, copy, compact, update, link, references, restore, reconcile.";
        }
    }

    /// <summary>Unpack an NMP/2 artifact back to its original text content.</summary>
    internal async Task<string> Show(
        [Description("The NMP/2 artifact text, or a memory name to resolve. " +
                     "Use the exact name shown by list() (e.g. 'session-notes', 'api:auth-flow', '~scratch').")] string artifactOrName,
        [Description("Optional 1-based chunk index to retrieve a specific chunk.")] int? chunk = null,
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

        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);

        if (chunk is not null)
        {
            string chunkContent = Nmp2ChunkedEncoder.DecodeChunk(artifact, chunk.Value);
            SessionBudget.RecordAccess(artifactOrName, chunkContent.Length);
            return $"Chunk {chunk}/{chunkCount}\n\n{chunkContent}";
        }

        byte[] bytes = Nmp2Strategy.Instance.Decode(artifact);
        string decoded = System.Text.Encoding.UTF8.GetString(bytes);
        SessionBudget.RecordAccess(artifactOrName, decoded.Length);

        if (chunkCount > 1)
            return $"({chunkCount} chunks)\n\n{decoded}";

        return decoded;
    }

    // ── Persistent memory tools ───────────────────────────────────────────────

    /// <summary>Compress text and persist it as a named artifact in a memory scope.</summary>
    internal async Task<string> Store(
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
        [Description("Optional file paths this memory depends on. Hashes are recorded to detect drift.")] string[]? codeRefs = null,
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

        // Fire event sink (embeddings, etc.) — never block the response
        try { await (MemoryEventSinkContext.Current?.OnStoredAsync(qualifiedName, content, store, cancellationToken) ?? Task.CompletedTask); }
        catch (Exception ex) { Console.Error.WriteLine($"[scrinia:warn] Event sink error: {ex.GetType().Name}: {ex.Message}"); }

        return $"Remembered: {qualifiedName} ({chunkCount} {(chunkCount == 1 ? "chunk" : "chunks")}, {FormatBytes(originalBytes)}). Files in .scrinia/ were updated — these are your changes.";
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
            return Task.FromResult("Error: update_meta does not support ephemeral memories. Use store() instead.");

        var (scope, subject) = store.ParseQualifiedName(name);

        var entries = store.LoadIndex(scope);
        var entry = entries.FirstOrDefault(e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            string qualName = store.FormatQualifiedName(scope, subject);
            return Task.FromResult($"Error: memory '{qualName}' not found.");
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
            return Task.FromResult("No changes specified. Provide at least one of: keywords, description, reviewAfter, reviewWhen.");

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
            $"Updated metadata for '{qualifiedName}': {string.Join(", ", changes)}. " +
            "Files in .scrinia/ were updated — these are your changes.");
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
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Compute supplemental scores from plugin (e.g. embeddings) if available
        // Use excludeTopics-filtered candidates so excluded topics don't influence embeddings scoring
        var contributor = SearchContributorContext.Current;
        IReadOnlyDictionary<string, double>? supplemental = null;
        if (contributor is not null)
        {
            var candidates = store.ListScoped(scopes, excludeTopics);
            supplemental = await contributor.ComputeScoresAsync(query, candidates, store, cancellationToken);
        }

        IReadOnlyList<SearchResult> matches = supplemental is { Count: > 0 }
            ? store.SearchAll(query, scopes, limit, supplemental)
                .Where(r => !IMemoryStore.ShouldExcludeScope(IMemoryStore.GetResultScope(r), excludeTopics))
                .ToList()
            : store.SearchAll(query, scopes, limit, excludeTopics);
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

    /// <summary>Copies a memory artifact from one scope to another.</summary>
    internal Task<string> Copy(
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

    /// <summary>Removes a stored artifact and its index entry.</summary>
    internal async Task<string> Forget(
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
                return $"Error: no artifact found with name or URI '{nameOrUri}'.";

            try { await (MemoryEventSinkContext.Current?.OnForgottenAsync(name, removedAny, store, cancellationToken) ?? Task.CompletedTask); }
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

    /// <summary>Export one or more local topics into a portable .scrinia-bundle file.</summary>
    internal Task<string> Export(
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

        // Sanitize filename: strip control characters and path separators
        bundleName = new string(bundleName.Where(c => !char.IsControl(c) && c != '/' && c != '\\').ToArray());
        bundleName = Path.GetFileName(bundleName);

        string bundlePath = Path.Combine(exportsDir, bundleName);

        List<string> exportedTopics;
        int totalEntries;

        using (var stream = new FileStream(bundlePath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            (exportedTopics, totalEntries) = Scrinia.Core.Bundles.BundleFormatService.ExportTopicsToZip(zip, store, topics);

            if (exportedTopics.Count == 0)
            {
                try { File.Delete(bundlePath); } catch { }
                return Task.FromResult("Error: no entries found in the specified topics.");
            }
        }

        long fileSize = new FileInfo(bundlePath).Length;
        return Task.FromResult(
            $"Exported {exportedTopics.Count} topic(s) ({totalEntries} entries, {FormatBytes(fileSize)}) to {bundlePath}");
    }

    /// <summary>Import topics from a .scrinia-bundle file into the local workspace.</summary>
    internal Task<string> Import(
        [Description("Path to the .scrinia-bundle file (relative to workspace or absolute).")] string bundlePath,
        [Description("Optional topic names to import. If empty, imports all topics in the bundle.")] string[]? topics = null,
        [Description("When true, replaces existing entries if they conflict.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Resolve path relative to workspace root if not absolute
        string storeDir = store.GetStoreDirForScope("local");
        string workspaceRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(storeDir)!, ".."));

        string resolvedPath = Path.IsPathRooted(bundlePath)
            ? bundlePath
            : Path.Combine(workspaceRoot, bundlePath);
        resolvedPath = Path.GetFullPath(resolvedPath);

        // SEC-041: prevent path traversal outside workspace
        if (!resolvedPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult("Error: bundle path must be within the workspace.");

        if (!File.Exists(resolvedPath))
            return Task.FromResult($"Error: bundle file not found: {resolvedPath}");

        try
        {
            using var stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            var (topicCount, entryCount, names) =
                Scrinia.Core.Bundles.BundleFormatService.ImportTopicsFromZip(zip, store, topics, overwrite);

            if (topicCount == 0)
                return Task.FromResult("No topics were imported (empty bundle or all filtered out).");

            return Task.FromResult(
                $"Imported {topicCount} topic(s) ({entryCount} entries): {string.Join(", ", names)}");
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult($"Error: {ex.Message}");
        }
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

        // Fire event sink (embeddings, etc.) — never block the response
        try { await (MemoryEventSinkContext.Current?.OnAppendedAsync(qualifiedName, content, store, cancellationToken) ?? Task.CompletedTask); }
        catch { /* plugin errors must not block append */ }

        return $"Appended chunk {chunkCount} to {qualifiedName} ({chunkCount} {(chunkCount == 1 ? "chunk" : "chunks")}, {FormatBytes(originalBytes)}). Files in .scrinia/ were updated — these are your changes.";
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
            return Task.FromResult($"No memories reference '{target}'.");

        string result = $"Found {matches.Count} memory(s) referencing '{target}':\n" +
            string.Join("\n", matches.Select(m => $"- {m}"));
        return Task.FromResult(result);
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

        if (result1.StartsWith("Error") || result2.StartsWith("Error"))
            return $"Partial link failure:\n  {from}: {result1}\n  {to}: {result2}";

        string response = $"Linked '{from}' \u2194 '{to}'.";
        if (!string.IsNullOrWhiteSpace(reason))
            response += $" Reason: {reason}";
        return response;
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
            return Task.FromResult(okCount > 0
                ? $"All {okCount} code references are current. No drift detected."
                : "No memories have code references. Use codeRefs parameter on store() to track file dependencies.");

        string response = $"Code reference drift detected ({driftCount} drifted, {missingCount} missing, {okCount} ok):\n" +
            string.Join("\n", results);
        return Task.FromResult(response);
    }

    /// <summary>Bundle operations — export and import memory topics.</summary>
    [McpServerTool(Name = "bundle"), Description(
        "Export or import memory topic bundles. Actions: " +
        "'export' (topic → .scrinia-bundle file), " +
        "'import' (.scrinia-bundle file → topic).")]
    public async Task<string> Bundle(
        [Description("Action: 'export' or 'import'.")] string action,
        [Description("Topic names to export, or topic filter for import.")] string[]? topics = null,
        [Description("Bundle file path (required for import, optional filename for export).")] string? bundlePath = null,
        [Description("Overwrite existing on import (default false).")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        string act = action.Trim().ToLowerInvariant();
        switch (act)
        {
            case "export":
                if (topics is null || topics.Length == 0)
                    return "Error: bundle('export') requires 'topics' parameter.";
                return await Export(topics, bundlePath, cancellationToken);

            case "import":
                if (string.IsNullOrWhiteSpace(bundlePath))
                    return "Error: bundle('import') requires 'bundlePath' parameter.";
                return await Import(bundlePath, topics, overwrite, cancellationToken);

            default:
                return $"Error: unknown action '{action}'. Valid actions: 'export', 'import'.";
        }
    }

    /// <summary>Scan for merge conflicts or resolve a specific conflict.</summary>
    internal Task<string> Reconcile(
        [Description("Conflict ID to resolve (from a prior reconcile scan). Omit to scan for conflicts.")] string? conflictId = null,
        [Description("Resolution: 'ours', 'theirs', or 'merged'. Required when conflictId is provided.")] string? choice = null,
        [Description("Content for 'merged' resolution.")] string? content = null,
        CancellationToken cancellationToken = default)
    {
        // ── Resolve mode: conflictId provided ─────────────────────────────
        if (conflictId is not null)
        {
            if (string.IsNullOrWhiteSpace(choice))
                return Task.FromResult("Error: 'choice' is required when resolving a conflict. Use 'ours', 'theirs', or 'merged'.");

            if (!_activeConflicts.TryGetValue(conflictId, out var conflictEntry))
                return Task.FromResult($"Error: conflict '{conflictId}' not found. Run reconcile() first to scan for conflicts.");

            string? resolvedContent;
            switch (choice.ToLowerInvariant())
            {
                case "ours":
                    resolvedContent = conflictEntry.OursContent;
                    if (resolvedContent is null)
                        return Task.FromResult($"Error: no 'ours' content available for {conflictId}. Use 'merged' with explicit content instead.");
                    break;
                case "theirs":
                    resolvedContent = conflictEntry.TheirsContent;
                    if (resolvedContent is null)
                        return Task.FromResult($"Error: no 'theirs' content available for {conflictId}. Use 'merged' with explicit content instead.");
                    break;
                case "merged":
                    if (string.IsNullOrEmpty(content))
                        return Task.FromResult("Error: 'merged' choice requires the content parameter.");
                    if (conflictEntry.Type.Contains("meta", StringComparison.OrdinalIgnoreCase))
                    {
                        try { System.Text.Json.Nodes.JsonNode.Parse(content!); }
                        catch { return Task.FromResult("Error: merged content is not valid JSON for .meta.json conflict."); }
                    }
                    resolvedContent = content;
                    break;
                default:
                    return Task.FromResult($"Error: invalid choice '{choice}'. Use 'ours', 'theirs', or 'merged'.");
            }

            try
            {
                if (conflictEntry.Type == "nmp2")
                {
                    string artifact = Nmp2ChunkedEncoder.Encode(resolvedContent);
                    File.WriteAllText(conflictEntry.FilePath, artifact);
                }
                else
                {
                    File.WriteAllText(conflictEntry.FilePath, resolvedContent);
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult($"Error writing resolved content to {conflictEntry.FilePath}: {ex.Message}");
            }

            _activeConflicts.TryRemove(conflictId, out _);
            return Task.FromResult($"Resolved {conflictId} ({conflictEntry.Type}) with '{choice}'. {_activeConflicts.Count} conflict(s) remaining.");
        }

        // ── Scan mode: no conflictId ──────────────────────────────────────
        _activeConflicts.Clear();

        var store = CurrentStore;
        string storeDir = store.GetStoreDirForScope("local");
        string scriniaDir = Path.GetDirectoryName(storeDir)!; // .scrinia/ directory

        var autoResolved = new List<string>();
        var needsManual = new List<string>();
        int nextConflictId = 0;

        // Scan all files in .scrinia/ recursively
        foreach (var filePath in Directory.EnumerateFiles(scriniaDir, "*", SearchOption.AllDirectories))
        {
            string fileContent;
            try { fileContent = File.ReadAllText(filePath); }
            catch { continue; }

            // Check for git conflict markers
            if (!fileContent.Contains("<<<<<<<")) continue;

            string relativePath = Path.GetRelativePath(scriniaDir, filePath);

            if (filePath.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
            {
                // Try auto-resolve .meta.json
                if (TryAutoResolveMetaJson(filePath, fileContent))
                {
                    autoResolved.Add(relativePath);
                }
                else
                {
                    string id = $"CONFLICT-{++nextConflictId}";
                    _activeConflicts[id] = new ConflictEntry(filePath, "meta.json", null, null);
                    needsManual.Add($"{id}: {relativePath} (.meta.json — auto-resolve failed)");
                }
            }
            else if (filePath.EndsWith(".nmp2", StringComparison.OrdinalIgnoreCase))
            {
                // Extract ours and theirs raw content
                int oursStart = fileContent.IndexOf('\n', fileContent.IndexOf("<<<<<<<")) + 1;
                int separator = fileContent.IndexOf("=======");
                int theirsEnd = fileContent.IndexOf(">>>>>>>");

                if (separator < 0 || theirsEnd < 0) { needsManual.Add($"{relativePath} (.nmp2 — malformed conflict markers)"); continue; }

                int theirsStart = fileContent.IndexOf('\n', separator) + 1;

                string oursRaw = fileContent[oursStart..separator].TrimEnd();
                string theirsRaw = fileContent[theirsStart..theirsEnd].TrimEnd();

                // Try to decode NMP/2 content from each side
                string? oursDecoded = null, theirsDecoded = null;
                try { oursDecoded = System.Text.Encoding.UTF8.GetString(new Scrinia.Core.Encoding.Nmp2Strategy().Decode(oursRaw)); } catch { oursDecoded = oursRaw; }
                try { theirsDecoded = System.Text.Encoding.UTF8.GetString(new Scrinia.Core.Encoding.Nmp2Strategy().Decode(theirsRaw)); } catch { theirsDecoded = theirsRaw; }

                string id = $"CONFLICT-{++nextConflictId}";
                _activeConflicts[id] = new ConflictEntry(filePath, "nmp2", oursDecoded, theirsDecoded);

                // Check for additional conflict regions after the first
                string multiNote = "";
                int afterFirstConflict = theirsEnd + ">>>>>>>".Length;
                if (afterFirstConflict < fileContent.Length && fileContent.IndexOf("<<<<<<<", afterFirstConflict, StringComparison.Ordinal) >= 0)
                    multiNote = " (file has additional conflict regions — resolve manually)";

                needsManual.Add($"{id}: {relativePath} (.nmp2 artifact){multiNote}\n    OURS:\n    {Indent(oursDecoded)}\n    THEIRS:\n    {Indent(theirsDecoded)}");
            }
            else
            {
                string id = $"CONFLICT-{++nextConflictId}";
                _activeConflicts[id] = new ConflictEntry(filePath, "unknown", null, null);
                needsManual.Add($"{id}: {relativePath} (unknown file type)");
            }
        }

        if (autoResolved.Count == 0 && needsManual.Count == 0)
            return Task.FromResult("No merge conflicts found in .scrinia/.");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Merge conflict scan: {autoResolved.Count} auto-resolved, {needsManual.Count} need manual resolution.");

        if (autoResolved.Count > 0)
        {
            sb.AppendLine("\nAuto-resolved:");
            foreach (var f in autoResolved) sb.AppendLine($"  \u2713 {f}");
        }
        if (needsManual.Count > 0)
        {
            sb.AppendLine("\nNeeds manual resolution:");
            foreach (var f in needsManual) sb.AppendLine($"  \u2717 {f}");
        }

        sb.Append($"\n{_activeConflicts.Count} conflict(s) remaining.");

        return Task.FromResult(sb.ToString());
    }

    private static string Indent(string? text, string prefix = "      ")
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        // Show first 500 chars to keep output manageable
        var truncated = text.Length > 500 ? text[..500] + "..." : text;
        return truncated.Replace("\n", "\n" + prefix);
    }

    private static bool TryAutoResolveMetaJson(string filePath, string conflictedContent)
    {
        try
        {
            // Extract "ours" and "theirs" sections
            // Format: <<<<<<< HEAD\n{ours}\n=======\n{theirs}\n>>>>>>> {branch}
            int oursStart = conflictedContent.IndexOf("<<<<<<<");
            int separator = conflictedContent.IndexOf("=======");
            int theirsEnd = conflictedContent.IndexOf(">>>>>>>");

            if (oursStart < 0 || separator < 0 || theirsEnd < 0) return false;

            // Extract the JSON from each side
            string beforeConflict = conflictedContent[..oursStart];
            string oursSection = conflictedContent[(conflictedContent.IndexOf('\n', oursStart) + 1)..separator];
            string theirsSection = conflictedContent[(conflictedContent.IndexOf('\n', separator) + 1)..theirsEnd];
            string afterConflict = conflictedContent[(conflictedContent.IndexOf('\n', theirsEnd) + 1)..];

            // Try to reconstruct valid JSON from each side
            string oursJson = beforeConflict + oursSection + afterConflict;
            string theirsJson = beforeConflict + theirsSection + afterConflict;

            // Parse both sides as mutable JSON
            var oursNode = JsonNode.Parse(oursJson);
            var theirsNode = JsonNode.Parse(theirsJson);
            if (oursNode is null || theirsNode is null) return false;

            // Pick base: latest updatedAt wins, fall back to theirs
            var baseNode = theirsNode; // default to incoming
            var otherNode = oursNode;

            var oursUpdated = oursNode["updatedAt"]?.GetValue<string>();
            var theirsUpdated = theirsNode["updatedAt"]?.GetValue<string>();
            if (oursUpdated is not null && theirsUpdated is not null)
            {
                if (DateTimeOffset.TryParse(oursUpdated, out var odt) &&
                    DateTimeOffset.TryParse(theirsUpdated, out var tdt) && odt > tdt)
                {
                    baseNode = oursNode;
                    otherNode = theirsNode;
                }
            }

            // Union keywords
            var keywordSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (baseNode["keywords"] is JsonArray baseKw)
                foreach (var k in baseKw) if (k?.GetValue<string>() is string s) keywordSet.Add(s);
            if (otherNode["keywords"] is JsonArray otherKw)
                foreach (var k in otherKw) if (k?.GetValue<string>() is string s) keywordSet.Add(s);

            var sortedKw = new JsonArray();
            foreach (var k in keywordSet.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                sortedKw.Add(k);
            baseNode["keywords"] = sortedKw;

            // Union termFrequencies (max value for shared keys)
            if (baseNode["termFrequencies"] is JsonObject baseTf &&
                otherNode["termFrequencies"] is JsonObject otherTf)
            {
                foreach (var kvp in otherTf)
                {
                    if (baseTf.ContainsKey(kvp.Key))
                    {
                        int baseVal = baseTf[kvp.Key]?.GetValue<int>() ?? 0;
                        int otherVal = kvp.Value?.GetValue<int>() ?? 0;
                        baseTf[kvp.Key] = Math.Max(baseVal, otherVal);
                    }
                    else
                    {
                        baseTf[kvp.Key] = kvp.Value?.GetValue<int>() ?? 0;
                    }
                }
            }

            // Write resolved JSON
            var writeOptions = new JsonSerializerOptions { WriteIndented = true };
            string resolvedJson = baseNode.ToJsonString(writeOptions);
            File.WriteAllText(filePath, resolvedJson);

            return true;
        }
        catch
        {
            return false; // If anything fails, report as needing manual resolution
        }
    }

    // ── Maintenance tools ──────────────────────────────────────────────────

    /// <summary>Compact a multi-chunk memory by merging chunks. Archives the original version.</summary>
    internal async Task<string> Compact(
        [Description("Memory name to compact.")] string name,
        [Description("Keep only the N most recent chunks. 0 = merge all into one (default).")] int keepRecent = 0,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;
        var (scope, subject) = store.ParseQualifiedName(name);

        string artifact = await store.ReadArtifactAsync(subject, scope, cancellationToken);
        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);

        if (chunkCount <= 1)
            return "Already a single chunk, nothing to compact.";

        if (keepRecent > 0 && keepRecent >= chunkCount)
            return $"Nothing to compact — keepRecent ({keepRecent}) >= chunk count ({chunkCount}).";

        // Archive the original before modifying
        store.ArchiveVersion(subject, scope);

        string compacted;
        int newChunkCount;

        if (keepRecent <= 0)
        {
            // Merge all chunks into one: decode entire artifact, re-encode as single chunk
            byte[] allBytes = Nmp2Strategy.Instance.Decode(artifact);
            string fullText = System.Text.Encoding.UTF8.GetString(allBytes);
            compacted = Nmp2ChunkedEncoder.Encode(fullText);
            newChunkCount = 1;
        }
        else if (keepRecent == 1)
        {
            // Keep only the last chunk as a single-chunk artifact
            string lastChunk = Nmp2ChunkedEncoder.DecodeChunk(artifact, chunkCount);
            compacted = Nmp2ChunkedEncoder.Encode(lastChunk);
            newChunkCount = 1;
        }
        else
        {
            // Keep the N most recent chunks
            int startChunk = chunkCount - keepRecent + 1;
            var keptChunks = new string[keepRecent];
            for (int i = 0; i < keepRecent; i++)
                keptChunks[i] = Nmp2ChunkedEncoder.DecodeChunk(artifact, startChunk + i);

            compacted = Nmp2ChunkedEncoder.EncodeChunks(keptChunks);
            newChunkCount = keepRecent;
        }

        await store.WriteArtifactAsync(subject, scope, compacted, cancellationToken);

        // Preserve keywords from the existing entry metadata
        var entries = store.LoadIndex(scope);
        var existingEntry = entries.FirstOrDefault(e => e.Name == subject);

        if (existingEntry is not null)
        {
            long newBytes = System.Text.Encoding.UTF8.GetByteCount(
                System.Text.Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(compacted)));
            var updatedEntry = existingEntry with
            {
                ChunkCount = newChunkCount,
                OriginalBytes = newBytes,
                UpdatedAt = DateTimeOffset.UtcNow,
                ChunkEntries = null  // chunk-level entries no longer valid after compaction
            };
            store.Upsert(updatedEntry, scope);
        }

        string qualifiedName = store.FormatQualifiedName(scope, subject);
        int dropped = chunkCount - newChunkCount;
        return $"Compacted {qualifiedName}: {chunkCount} → {newChunkCount} chunk{(newChunkCount == 1 ? "" : "s")} ({dropped} dropped). Original archived. Files in .scrinia/ were updated — these are your changes.";
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

    /// <summary>Resume full agent context after context loss or session start.</summary>
    internal async Task<string> Restore(CancellationToken cancellationToken)
    {
        var store = CurrentStore;

        // Check for unresolved merge conflicts in .scrinia/
        string conflictWarning = "";
        try
        {
            string resumeStoreDir = store.GetStoreDirForScope("local");
            string resumeScriniaDir = Path.GetDirectoryName(resumeStoreDir)!;
            if (Directory.Exists(resumeScriniaDir))
            {
                bool hasConflicts = Directory.EnumerateFiles(resumeScriniaDir, "*", SearchOption.AllDirectories)
                    .Any(f =>
                    {
                        try
                        {
                            // Quick check: read first 10KB of each file for conflict markers
                            using var reader = new StreamReader(f);
                            var buf = new char[10240];
                            int read = reader.Read(buf, 0, buf.Length);
                            return new string(buf, 0, read).Contains("<<<<<<<");
                        }
                        catch { return false; }
                    });
                if (hasConflicts)
                    conflictWarning = "WARNING: .scrinia/ has unresolved merge conflicts. Run reconcile() before continuing.\n\n";
            }
        }
        catch { /* best-effort check */ }

        // Read checkpoint:latest for recovery context
        string? checkpointContent = null;
        try
        {
            checkpointContent = await ScriniaProjectTools.ReadMemoryAsync(store, "checkpoint:latest", cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // No checkpoint exists — normal for first-time projects
        }

        string response;
        try
        {
            response = await ScriniaProjectTools.ReadMemoryAsync(store, "project:state", cancellationToken);
        }
        catch (FileNotFoundException)
        {
            string? rebuilt = await ScriniaProjectTools.RebuildStateFromMemoriesAsync(store, cancellationToken);
            if (rebuilt is null)
                return "Error: no project found. Run plan('init') first.";
            response = rebuilt;
        }

        // Replace stale progress with computed value
        try
        {
            string? restoreGoalId = await ScriniaProjectTools.GetActiveGoalIdAsync(store, cancellationToken);
            string computedProgress = ScriniaProjectTools.CalculateProgress(store, restoreGoalId);
            response = Regex.Replace(response, @"(?m)^Progress:\s*\d+%?$", $"Progress: {computedProgress}%");
        }
        catch { /* best-effort — if progress computation fails, show raw state */ }

        // Active goal description
        string activeGoalDesc = "";
        try
        {
            string ctxText = await ScriniaProjectTools.ReadMemoryAsync(store, "project:context", cancellationToken);
            var (goals, _, _) = ScriniaProjectTools.ParseGoalsSection(ctxText);
            var activeLine = goals.FirstOrDefault(g => g.Contains("[active]", StringComparison.OrdinalIgnoreCase));
            if (activeLine is not null)
            {
                // Extract description: everything after "[active] " and before " | Outcome:"
                var statusMatch = Regex.Match(
                    activeLine.TrimStart('-', '*', ' '),
                    @"\]\s*\[active\]\s*",
                    RegexOptions.IgnoreCase);
                if (statusMatch.Success)
                {
                    string desc = activeLine.TrimStart('-', '*', ' ')[(statusMatch.Index + statusMatch.Length)..];
                    activeGoalDesc = $"\nActive goal: {desc.Trim()}";
                }
            }
        }
        catch { /* no project:context or no active goal — skip */ }

        response += activeGoalDesc;

        // Optionally enrich with active concern count (keyword-only scan, no artifact decoding)
        string concernNote = "";
        try
        {
            var (cs, _) = store.ParseQualifiedName("concern:placeholder");
            var entries = store.LoadIndex(cs);
            int activeCount = entries.Count(e => ScriniaProjectTools.HasKeyword(e, "status:active"));
            if (activeCount > 0)
            {
                int highCount = entries.Count(e =>
                    ScriniaProjectTools.HasKeyword(e, "status:active") &&
                    ScriniaProjectTools.HasKeyword(e, "severity:high"));
                concernNote = highCount > 0
                    ? $"\nConcerns: {activeCount} active ({highCount} high-severity)"
                    : $"\nConcerns: {activeCount} active";
            }
        }
        catch { /* concern scope not yet created — skip silently */ }

        response += concernNote;

        // Optionally surface unused capability hints (ADOPT-03)
        string capabilityHints = "";

        // Check if concern tracking has been used (scope exists with entries)
        bool concernsUsed = false;
        try
        {
            var (cs2, _) = store.ParseQualifiedName("concern:placeholder");
            var cEntries = store.LoadIndex(cs2);
            concernsUsed = cEntries.Count > 0;
        }
        catch { /* scope not created — concerns not used */ }

        if (!concernsUsed)
            capabilityHints += "\nHint: concern tracking is available — use concern('add') to track risks and issues across phases.";

        // Check if knowledge (bok) has been used
        bool knowledgeUsed = false;
        try
        {
            var (bs, _) = store.ParseQualifiedName("bok:placeholder");
            var bEntries = store.LoadIndex(bs);
            knowledgeUsed = bEntries.Count > 0;
        }
        catch { /* scope not created — knowledge not used */ }

        if (!knowledgeUsed)
            capabilityHints += "\nHint: use store(content, \"topic:subject\") with keywords to persist domain knowledge across sessions.";

        response += capabilityHints;

        // Inline all agent:* memories
        try
        {
            var (agentScope, _) = store.ParseQualifiedName("agent:placeholder");
            var agentEntries = store.LoadIndex(agentScope);
            if (agentEntries.Count > 0)
            {
                response += "\n\n## Agent norms";
                foreach (var entry in agentEntries)
                {
                    try
                    {
                        string content = await ScriniaProjectTools.ReadMemoryAsync(store, $"agent:{entry.Name}", cancellationToken);
                        response += $"\n### {entry.Name}\n{content}";
                    }
                    catch { /* entry unreadable — skip */ }
                }
            }
        }
        catch { /* agent scope not yet created — skip silently */ }

        // Inline all patterns:* memories
        try
        {
            var (patternsScope, _) = store.ParseQualifiedName("patterns:placeholder");
            var patternsEntries = store.LoadIndex(patternsScope);
            if (patternsEntries.Count > 0)
            {
                response += "\n\n## Learned patterns";
                foreach (var entry in patternsEntries)
                {
                    try
                    {
                        string content = await ScriniaProjectTools.ReadMemoryAsync(store, $"patterns:{entry.Name}", cancellationToken);
                        response += $"\n### {entry.Name}\n{content}";
                    }
                    catch { /* entry unreadable — skip */ }
                }
            }
        }
        catch { /* patterns scope not yet created — skip silently */ }

        if (checkpointContent is not null)
            response += "\n\n## Last checkpoint\n" + checkpointContent;

        // Today's session log
        try
        {
            string today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            string sessionContent = await ScriniaProjectTools.ReadMemoryAsync(store, $"sessions:{today}", cancellationToken);
            response += "\n\n## Session log\n" + sessionContent;
        }
        catch (FileNotFoundException) { /* no session log for today — skip */ }

        // Staleness & drift alerts — use cache if available, fall back to live scan
        int rsStale, rsReview, rsDrift, rsMissing;
        string rsCacheNote = "";
        if (MaintenanceCache.TryReadCache(store, out var rsCached) && rsCached is not null)
        {
            rsStale = rsCached.StaleCount;
            rsReview = rsCached.ReviewCount;
            rsDrift = rsCached.DriftCount;
            rsMissing = rsCached.MissingCount;
            int cacheAge = (int)(DateTimeOffset.UtcNow - rsCached.ComputedAt).TotalMinutes;
            rsCacheNote = $" (cached {cacheAge} min ago)";
        }
        else
        {
            (rsStale, rsReview) = ScriniaProjectTools.ScanStaleness(store);
            (rsDrift, rsMissing) = ScriniaProjectTools.ScanDrift(store);
        }

        if (rsStale > 0) response += $"\n⚠ {rsStale} memory(s) have passed their review date.{rsCacheNote}";
        if (rsReview > 0) response += $"\nℹ {rsReview} memory(s) have review conditions set.{rsCacheNote}";
        if (rsDrift > 0) response += $"\n⚠ {rsDrift} code reference(s) have drifted (files changed since stored).{rsCacheNote}";
        if (rsMissing > 0) response += $"\n⚠ {rsMissing} code reference(s) point to missing files.{rsCacheNote}";

        // Task nudge — rational lensing: nudge agent into the task loop
        try
        {
            // Extract phase number from state
            string phaseId = "";
            var phaseMatch = ScriniaProjectTools.PhaseNumberPattern.Match(response);
            if (phaseMatch.Success)
                phaseId = int.Parse(phaseMatch.Groups[1].Value).ToString("D2");

            if (!string.IsNullOrEmpty(phaseId))
            {
                string? nudgeGoalId = await ScriniaProjectTools.GetActiveGoalIdAsync(store, cancellationToken);
                var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
                var taskEntries = store.LoadIndex(taskScope);
                var pendingTasks = taskEntries
                    .Where(e => ScriniaProjectTools.HasKeyword(e, $"phase:{phaseId}"))
                    .Where(e => nudgeGoalId is null || ScriniaProjectTools.HasKeyword(e, $"goal:{nudgeGoalId}"))
                    .Where(e => ScriniaProjectTools.HasKeyword(e, "status:pending"))
                    .ToList();

                if (pendingTasks.Count > 0)
                    response += $"\n\nRun task('next', {{ phaseId: \"{phaseId}\" }}) to continue.";
            }
        }
        catch { /* best-effort — skip nudge silently */ }

        response = conflictWarning + response;
        response = ScriniaProjectTools.Truncate(response);

        return response;
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
