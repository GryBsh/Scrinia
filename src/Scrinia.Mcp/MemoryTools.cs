using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Scrinia.Core;

namespace Scrinia.Mcp;

[McpServerToolType]
public sealed partial class ScriniaMcpTools
{

    private static readonly Regex CountPattern = new(
        @"\b\d+\s+(tests?|tools?|skills?|endpoints?|routes?|models?|memories)\b",
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
        "Required reading — call at session start. Returns the agent guide AND auto-restores " +
        "workspace context (available skills, recent sessions, patterns) so you have everything " +
        "needed for the session in one tool call. Then commit content to your project's agent file.")]
    public async Task<string> Guide(CancellationToken cancellationToken = default)
    {
        string guideContent = EmbeddedPrompts.LoadGuide()
            ?? throw new InvalidOperationException("Built-in guide not found in embedded resources");

        // No store configured (e.g. running outside a workspace): just return the static guide.
        if (MemoryStoreContext.Current is null)
            return ResponseBuilder.Success(guideContent).WithAction("guide").ToYaml();

        // Fold the workspace-restore context into the guide response so the agent gets warnings,
        // available skills, and followUp pointers without a separate memory_restore call —
        // closes the most common utilization failure mode (read guide, forget to restore).
        var ctx = await BuildRestoreContextAsync(cancellationToken);
        var combinedInfo = ctx.Info.Concat(ctx.ContentSections).ToArray();
        return ResponseBuilder.Success(guideContent)
            .WithAction("guide")
            .WithActionNeeded(ctx.Warnings.ToArray())
            .WithInfo(combinedInfo)
            .WithInstruction(ctx.Instruction)
            .WithFollowUp(ctx.FollowUpNames.ToArray())
            .ToYaml();
    }

    // ── Per-verb memory tools ────────────────────────────────────────────────
    //
    // Each tool below has its own JSONSchema — the validator sees exactly the
    // fields the operation accepts, so the agent's autonomous-mode "reach for
    // the more salient name" failure mode (e.g. passing `content` to an append
    // that needed a different shape) is now caught at schema-validation time,
    // not by the per-handler error path. Shared validation constants:

    private const int MaxPathLength = 256;
    private const int MaxContentBytesPerElement = 5 * 1024 * 1024; // 5 MB

    [McpServerTool(Name = "memory_remember"), Description(
        "Persist a memory at a path. content is an array — each element becomes one independently " +
        "retrievable chunk. attributes is a map of typed facets that link and filter memories " +
        "(e.g. person/org/topic/file/review); keywords are the distinctive terms and query phrasings " +
        "a searcher would use. Example: memory_remember({ path: '/findings/SEC-001', " +
        "content: ['observation', 'recommendation'], attributes: { org: ['acme'], topic: ['auth'] }, " +
        "keywords: ['oauth flow', 'token refresh'] }).")]
    public async Task<string> MemoryRemember(
        [Description("Memory path (e.g. '/findings/SEC-001', '/patterns/retry', '/temp/scratch', or 'topic:subject' for v1 compat).")] string path,
        [Description("Content array — each element becomes one independently retrievable chunk.")] string[] content,
        [Description("Optional typed attributes — a map of key to values. Recommended keys: person, place, " +
                     "org, thing, topic (entities that link related memories); file, ref (dependencies, hashed " +
                     "for drift detection); review (an ISO 8601 date and/or a free-text condition). Any key is " +
                     "allowed. Example: { person: ['maria'], org: ['acme'], review: ['2026-08-01'] }.")]
        Dictionary<string, string[]>? attributes = null,
        [Description("Optional keywords — the distinctive terms AND the phrasings a searcher is likely to use. Merged with auto-extracted terms and seeded into search.")] string[]? keywords = null,
        CancellationToken cancellationToken = default)
    {
        // Skill-path routing: '/skill/{name}' → SkillCreate.
        var skillRoute = TryRouteSkillRemember(path, content, cancellationToken);
        if (skillRoute is not null) return await skillRoute;

        if (content is null || content.Length == 0)
            return ResponseBuilder.Error(
                "memory_remember requires 'content' (a non-empty string array).",
                ErrorCodes.InvalidParameter,
                "memory_remember({ path: '...', content: ['chunk 1', 'chunk 2'] })").ToYaml();
        if (string.IsNullOrWhiteSpace(path))
            return ResponseBuilder.Error(
                "memory_remember requires 'path'.",
                ErrorCodes.InvalidParameter,
                "memory_remember({ path: '/findings/example', content: [...] })").ToYaml();
        if (path.Length > MaxPathLength)
            return ResponseBuilder.Error(
                $"path exceeds {MaxPathLength} characters.",
                ErrorCodes.InvalidParameter,
                $"Shorten the path to under {MaxPathLength} characters.").ToYaml();
        foreach (var element in content)
        {
            if (element != null && System.Text.Encoding.UTF8.GetByteCount(element) > MaxContentBytesPerElement)
                return ResponseBuilder.Error(
                    $"content element exceeds {MaxContentBytesPerElement / (1024 * 1024)} MB limit.",
                    ErrorCodes.InvalidParameter,
                    "Split the content into multiple smaller elements in the content array.").ToYaml();
        }

        string result = await Store(content, path,
            keywords: keywords, attributes: attributes,
            actionLabel: "remembered", cancellationToken: cancellationToken);
        string? typoHint = ReservedPrefixGuard.HintFor(path);
        if (typoHint is not null)
            result = McpResponseExtensions.InjectInfoHint(result, typoHint);
        return result;
    }

    [McpServerTool(Name = "memory_recall"), Description(
        "Read a memory by path. Returns the full content unless a chunk index is provided. " +
        "Example: memory_recall({ path: '/findings/SEC-001' }), or memory_recall({ path: '/skill/qa' }) to load a skill.")]
    public async Task<string> MemoryRecall(
        [Description("Memory path (e.g. '/findings/SEC-001', '/skill/qa', or 'topic:subject' for v1 compat). '/skill/' alone lists available skills.")] string path,
        [Description("Optional 1-based chunk index to retrieve a single chunk.")] int? chunk = null,
        [Description("Skills only: when path is '/skill/{name}', return both the built-in and the project override side-by-side for merging. Ignored for non-skill paths.")] bool withBuiltin = false,
        CancellationToken cancellationToken = default)
    {
        // Skill-path routing: '/skill/...' → SkillLoad. Handles both '/skill/{name}' (load)
        // and '/skill' or '/skill/' (list view).
        var skillRoute = TryRouteSkillRecall(path, withBuiltin, cancellationToken);
        if (skillRoute is not null) return await skillRoute;

        if (string.IsNullOrWhiteSpace(path))
            return ResponseBuilder.Error(
                "memory_recall requires 'path'.",
                ErrorCodes.InvalidParameter,
                "memory_recall({ path: '...' })",
                "memory_list() to discover available paths").ToYaml();

        return await Show(path, chunk, actionLabel: "recalled", cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "memory_search"), Description(
        "Find memories by keyword or phrase. Returns ranked matches with description, tags, and " +
        "retrieval anchors per hit so you can decide what to recall without a follow-up call. " +
        "Example: memory_search({ query: 'oauth flow', pathPrefix: '/findings/' }).")]
    public async Task<string> MemorySearch(
        [Description("Search query string.")] string query,
        [Description("Optional context terms appended to the query for scoring only — bridges vocabulary mismatches when you know synonyms or related concepts the user didn't type.")] string? context = null,
        [Description("Optional comma-separated scope order, e.g. 'local,api,ephemeral'.")] string? scopes = null,
        [Description("Maximum results to return (default 20). Rich-result format is verbose, so prefer 10 for broad exploratory queries.")] int limit = 20,
        [Description("Optional comma-separated topic names to exclude from results.")] string? excludeTopics = null,
        [Description("Optional path prefix to scope results, e.g. '/findings/' or '/skill/'. Matches the qualified memory name.")] string? pathPrefix = null,
        [Description("Optional comma-separated tags. Returns only memories whose tag set contains ALL listed tags (AND, not OR).")] string? tags = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ResponseBuilder.Error(
                "memory_search requires 'query'.",
                ErrorCodes.InvalidParameter,
                "memory_search({ query: 'keywords or phrase' })").ToYaml();

        return await Search(query, scopes, limit, excludeTopics, context, pathPrefix, tags, cancellationToken);
    }

    [McpServerTool(Name = "memory_list"), Description(
        "List stored memories. mode 'summary' (default) shows scopes + top keywords; 'full' paginates the table; 'drift' surfaces memories whose codeRefs files have changed.")]
    public Task<string> MemoryList(
        [Description("Optional comma-separated scope order, e.g. 'local,api,ephemeral'. Topic names filter to local topics.")] string? scopes = null,
        [Description("'summary' (default), 'full' for a paginated table, 'drift' for a code-reference drift check.")] string mode = "summary",
        [Description("Starting index for full mode (0-based).")] int offset = 0,
        [Description("Maximum entries to return in full mode (default 50).")] int limit = 50,
        [Description("Optional comma-separated topic names to exclude (e.g. 'plan,task,project,learn' to hide planning namespaces).")] string? excludeTopics = null,
        CancellationToken cancellationToken = default)
    {
        return List(scopes, mode ?? "summary", offset, limit, excludeTopics, cancellationToken);
    }

    [McpServerTool(Name = "memory_append"), Description(
        "Append a new chunk to an existing memory (or create it if missing). Preserves the memory's " +
        "existing retrieval metadata and merges in any new attributes (multi-valued keys union) and " +
        "keywords (deduplicated). Example: memory_append({ path: '/sessions/2026-05-11', " +
        "content: 'session note', attributes: { topic: ['standup'] } }).")]
    public async Task<string> MemoryAppend(
        [Description("Memory path to append to.")] string path,
        [Description("Text content for the new chunk.")] string content,
        [Description("Optional attributes merged into the memory's existing attributes (multi-valued keys union, scalar keys newer-wins). Same key vocabulary as memory_remember.")]
        Dictionary<string, string[]>? attributes = null,
        [Description("Optional keywords merged with the memory's existing + auto-extracted terms (deduplicated).")] string[]? keywords = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            return ResponseBuilder.Error(
                "memory_append requires 'content'.",
                ErrorCodes.InvalidParameter,
                "memory_append({ path: '...', content: 'new chunk text' })").ToYaml();
        if (string.IsNullOrWhiteSpace(path))
            return ResponseBuilder.Error(
                "memory_append requires 'path'.",
                ErrorCodes.InvalidParameter,
                "memory_append({ path: '/sessions/2026-05-11', content: '...' })").ToYaml();
        if (System.Text.Encoding.UTF8.GetByteCount(content) > MaxContentBytesPerElement)
            return ResponseBuilder.Error(
                $"content exceeds {MaxContentBytesPerElement / (1024 * 1024)} MB limit.",
                ErrorCodes.InvalidParameter,
                "Split the append into multiple smaller memory_append calls.").ToYaml();

        string result = await Append(content, path, keywords: keywords, attributes: attributes, cancellationToken: cancellationToken);
        string? typoHint = ReservedPrefixGuard.HintFor(path);
        if (typoHint is not null)
            result = McpResponseExtensions.InjectInfoHint(result, typoHint);
        return result;
    }

    [McpServerTool(Name = "memory_forget"), Description(
        "Delete a memory by path. " +
        "Example: memory_forget({ path: '/temp/scratch' }).")]
    public async Task<string> MemoryForget(
        [Description("Memory path (e.g. '/findings/SEC-001', '~scratch') or a file:// URI.")] string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ResponseBuilder.Error(
                "memory_forget requires 'path'.",
                ErrorCodes.InvalidParameter,
                "memory_forget({ path: '...' })").ToYaml();

        return await Forget(path, cancellationToken);
    }

    [McpServerTool(Name = "memory_compact"), Description(
        "Merge a multi-chunk memory. Archives the original version. " +
        "Example: memory_compact({ path: '/sessions/2026-05-11', keepRecent: 3 }).")]
    public Task<string> MemoryCompact(
        [Description("Memory path to compact.")] string path,
        [Description("Keep only the N most recent chunks. 0 (default) merges all into one.")] int keepRecent = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(ResponseBuilder.Error(
                "memory_compact requires 'path'.",
                ErrorCodes.InvalidParameter,
                "memory_compact({ path: '...', keepRecent: 3 })").ToYaml());

        return Compact(path, keepRecent, cancellationToken);
    }

    [McpServerTool(Name = "memory_link"), Description(
        "Create a bidirectional link between two memories. " +
        "Example: memory_link({ path: '/findings/SEC-001', destination: '/patterns/auth' }).")]
    public Task<string> MemoryLink(
        [Description("Source memory path.")] string path,
        [Description("Target memory path.")] string destination,
        [Description("Optional reason for the link.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(ResponseBuilder.Error(
                "memory_link requires 'path'.",
                ErrorCodes.InvalidParameter,
                "memory_link({ path: '...', destination: '...' })").ToYaml());
        if (string.IsNullOrWhiteSpace(destination))
            return Task.FromResult(ResponseBuilder.Error(
                "memory_link requires 'destination'.",
                ErrorCodes.InvalidParameter,
                "memory_link({ path: '...', destination: '...' })").ToYaml());

        return Link(path, destination, reason, cancellationToken);
    }

    [McpServerTool(Name = "memory_restore"), Description(
        "Resume workspace context — agent profile, patterns, today's session log, available skills, " +
        "unresolved merge conflicts. Usually unnecessary because guide() folds this in at session start; " +
        "call this mid-session to refresh after a long gap.")]
    public Task<string> MemoryRestore(CancellationToken cancellationToken = default)
    {
        return Restore(cancellationToken);
    }

    [McpServerTool(Name = "memory_reconcile"), Description(
        "Scan .scrinia/ for git merge conflicts, or resolve a specific one. " +
        "Without args: scan and report. With conflictId + choice: resolve. " +
        "Example: memory_reconcile({ conflictId: 'local/skills/qa.nmp2', choice: 'ours' }).")]
    public Task<string> MemoryReconcile(
        [Description("Workspace-relative path under .scrinia/ to the conflicted file. Omit to scan.")] string? conflictId = null,
        [Description("Resolution: 'ours', 'theirs', or 'merged'. Required when conflictId is provided.")] string? choice = null,
        [Description("Content for 'merged' resolution.")] string? mergedContent = null,
        CancellationToken cancellationToken = default)
    {
        return Reconcile(conflictId, choice, mergedContent, cancellationToken);
    }

    // ── Skill-path routing ────────────────────────────────────────────────────
    //
    // Skills are conceptually memory at a reserved path — we route /skill/{name}
    // requests to SkillLoad / SkillCreate inside memory_recall and memory_remember
    // rather than exposing a separate skill_* tool surface. The routing key is
    // `path`, so a dedicated tool wouldn't reduce ambiguity — it would just
    // create a second surface the agent has to choose between.

    /// <summary>
    /// Routes a memory_recall call to <see cref="SkillLoad"/> when the path is under
    /// /skill/. Handles both '/skill/{name}' (load one) and '/skill' or '/skill/'
    /// (list available). Returns null when the call should fall through to normal recall.
    /// </summary>
    private static Task<string>? TryRouteSkillRecall(string? path, bool withBuiltin, CancellationToken ct)
    {
        if (!TryParseSkillPath(path, out string? skillName))
            return null;
        return SkillLoad(skillName, withBuiltin, ct);
    }

    /// <summary>
    /// Routes a memory_remember call to <see cref="SkillCreate"/> when the path is under
    /// /skill/. Returns null when the call should fall through to normal remember.
    /// </summary>
    private static Task<string>? TryRouteSkillRemember(string? path, string[]? content, CancellationToken ct)
    {
        if (!TryParseSkillPath(path, out string? skillName))
            return null;

        if (content is null || content.Length == 0)
            return Task.FromResult(ResponseBuilder.Error(
                "memory_remember on a /skill/... path requires 'content' with at least one element (the skill instructions).",
                ErrorCodes.InvalidParameter,
                "memory_remember({ path: '/skill/my-helper', content: ['instructions here'] })").ToYaml());

        if (string.IsNullOrWhiteSpace(skillName))
            return Task.FromResult(ResponseBuilder.Error(
                "memory_remember on a /skill/... path requires a skill name in the path.",
                ErrorCodes.InvalidParameter,
                "memory_remember({ path: '/skill/your-name-here', content: [...] })").ToYaml());

        return SkillCreate(
            name: skillName,
            scaffold: "custom",
            instructions: string.Join("\n\n", content),
            tools: null,
            ct);
    }

    /// <summary>
    /// True when <paramref name="path"/> is a /skill/... path (case-insensitive).
    /// Sets <paramref name="skillName"/> to the parsed name, or null when the path
    /// is just '/skill' or '/skill/' (the list view).
    /// </summary>
    private static bool TryParseSkillPath(string? path, out string? skillName)
    {
        skillName = null;
        if (string.IsNullOrWhiteSpace(path)) return false;

        if (!path.StartsWith("/skill/", StringComparison.OrdinalIgnoreCase) &&
            !path.Equals("/skill", StringComparison.OrdinalIgnoreCase))
            return false;

        if (path.Length > "/skill/".Length)
        {
            skillName = path["/skill/".Length..].Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(skillName))
                skillName = null;
        }
        return true;
    }
}
