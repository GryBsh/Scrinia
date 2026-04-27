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
public sealed partial class ScriniaMcpTools
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
        "If a project exists (.scrinia/ directory), check memory('recall', { path: '/project/status' }) for active goals before starting new work. " +
        "Covers memory patterns, the goal-driven planning workflow, and when to plan vs. just do.")]
    public Task<string> Guide(CancellationToken cancellationToken = default) =>
        Task.FromResult(ResponseBuilder.Success(
            EmbeddedPrompts.LoadGuide()
            ?? throw new InvalidOperationException("Built-in guide not found in embedded resources"))
            .WithAction("guide").ToYaml());

    /// <summary>Unified memory tool — dispatches to action-specific handlers.</summary>
    [McpServerTool(Name = "memory"), Description(
        "Unified memory operations. Actions: " +
        "'remember' / 'store' (persist content with keywords/review/codeRefs), " +
        "'recall' / 'show' (read/decode memory content, optional chunk), " +
        "'forget' (delete memory), " +
        "'search' (find memories by query), " +
        "'list' (browse memories — summary/full/drift modes), " +
        "'append' (add chunk to existing memory), " +
        "'transition' (change entity state), " +
        "'compact' (merge chunks in a memory), " +
        "'link' (add codeRefs to a memory), " +
        "'restore' (resume full agent context — project state, agent profile, session log, task nudge), " +
        "'reconcile' (scan for merge conflicts or resolve a specific conflict).")]
    public async Task<string> Memory(
        [Description("Action to perform: 'remember' (or 'store'), 'recall' (or 'show'), 'forget', 'search', 'list', 'append', 'transition', 'compact', 'link', 'restore', 'reconcile'.")] string action,
        [Description("Memory path (e.g., '/goal/G-5/research/frontend' or 'topic:subject' for backward compat).")] string? path = null,
        [Description("Content array — each element becomes one chunk (store).")] string[]? content = null,
        [Description("Text content to append as new chunk (append).")] string? appendContent = null,
        [Description("Search query string (search).")] string? query = null,
        [Description("Target name for link.")] string? destination = null,
        [Description("Optional description (store, update, create goal/concern/project).")] string? description = null,
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
        [Description("Entity ID for operations on existing entities.")] string? id = null,
        [Description("Target state for transitions.")] string? to = null,
        [Description("Concern severity: 'high', 'medium', 'low'.")] string? severity = null,
        [Description("Phase scope for concerns.")] string? phase = null,
        [Description("Requirements text with REQ-IDs.")] string? requirements = null,
        [Description("Outcome note (complete goal).")] string? outcome = null,
        [Description("Resolution description (resolve concern).")] string? resolution = null,
        [Description("Verification method: 'debugger', 'qa', 'manual'.")] string? verifiedBy = null,
        [Description("Evidence for requirement fulfillment.")] string? evidence = null,
        [Description("Workflow name for goal creation.")] string? workflowRef = null,
        [Description("Workflow definition JSON/YAML.")] string? definition = null,
        [Description("Project context description.")] string? context = null,
        [Description("Status filter for list.")] string? filter = null,
        CancellationToken cancellationToken = default)
    {
        string act = action.Trim().ToLowerInvariant();
        // Map aliases to their response action names
        string responseAction = act switch
        {
            "remember" => "remembered",
            "recall" => "recalled",
            _ => null! // null means use the handler's default
        };

        // ── Skill path routing ───────────────────────────────────────────────
        // When `path` starts with "/skill/", route to SkillLoad/SkillCreate on
        // ScriniaProjectTools. Handled before entity routing so "/skill/" doesn't
        // fall through to entity dispatch (skill is not an entity type).
        {
            var skillResult = TryRouteToSkill(act, path, content, cancellationToken);
            if (skillResult is not null)
                return await skillResult;
        }

        // ── Entity path routing ──────────────────────────────────────────────
        // When `path` starts with "/" and the first segment is a known entity type,
        // route to EntityDispatch on ScriniaProjectTools — provided the action is
        // one that has an entity equivalent AND required entity params are present.
        // If required params are missing, fall through to standard memory storage.
        {
            var entityResult = TryRouteToEntity(
                act, 
                path, description, 
                id, to, 
                severity,
                phase, requirements, outcome, resolution, 
                verifiedBy, evidence,
                context, definition, 
                workflowRef, 
                query, filter, 
                cancellationToken
            );
            if (entityResult is not null)
                return await entityResult;
        }

        switch (act)
        {
            case "remember":
            case "store":
                const int MaxNameLength = 256;
                const int MaxContentBytesPerElement = 5 * 1024 * 1024; // 5 MB
                if (content is null || content.Length == 0) return ResponseBuilder.Error("memory('remember') requires 'content' parameter.").ToYaml();
                if (string.IsNullOrWhiteSpace(path)) return ResponseBuilder.Error("memory('remember') requires 'path' parameter.").ToYaml();
                if (path.Length > MaxNameLength)
                    return ResponseBuilder.Error($"path exceeds {MaxNameLength} characters.").ToYaml();
                foreach (var element in content)
                {
                    if (element != null && System.Text.Encoding.UTF8.GetByteCount(element) > MaxContentBytesPerElement)
                        return ResponseBuilder.Error($"content element exceeds {MaxContentBytesPerElement / (1024 * 1024)} MB limit.").ToYaml();
                }
                {
                    var result = await Store(content, path, description ?? "", tags, keywords, reviewAfter, reviewWhen, codeRefs, cancellationToken);
                    return responseAction is not null ? result.Replace("action: stored", $"action: {responseAction}") : result;
                }

            case "append":
                if (string.IsNullOrWhiteSpace(appendContent)) return ResponseBuilder.Error("memory('append') requires 'appendContent' parameter.").ToYaml();
                if (string.IsNullOrWhiteSpace(path)) return ResponseBuilder.Error("memory('append') requires 'path' parameter.").ToYaml();
                if (appendContent != null && System.Text.Encoding.UTF8.GetByteCount(appendContent) > 5 * 1024 * 1024)
                    return ResponseBuilder.Error("append content exceeds 5 MB limit.").ToYaml();
                return await Append(appendContent!, path!, cancellationToken);

            case "recall":
            case "show":
                if (string.IsNullOrWhiteSpace(path)) return ResponseBuilder.Error("memory('recall') requires 'path' parameter.").ToYaml();
                {
                    var result = await Show(path, chunk, cancellationToken);
                    return responseAction is not null ? result.Replace("action: shown", $"action: {responseAction}") : result;
                }

            case "search":
                if (string.IsNullOrWhiteSpace(query)) return ResponseBuilder.Error("memory('search') requires 'query' parameter.").ToYaml();
                return await Search(query, scopes, limit, excludeTopics, cancellationToken);

            case "list":
                return await List(scopes, mode ?? "summary", offset, limit, excludeTopics, cancellationToken);

            case "forget":
                if (string.IsNullOrWhiteSpace(path)) return ResponseBuilder.Error("memory('forget') requires 'path' parameter.").ToYaml();
                return await Forget(path, cancellationToken);

            case "compact":
                if (string.IsNullOrWhiteSpace(path)) return ResponseBuilder.Error("memory('compact') requires 'path' parameter.").ToYaml();
                return await Compact(path, keepRecent, cancellationToken);

            case "link":
                if (string.IsNullOrWhiteSpace(path)) return ResponseBuilder.Error("memory('link') requires 'path' parameter.").ToYaml();
                if (string.IsNullOrWhiteSpace(destination)) return ResponseBuilder.Error("memory('link') requires 'destination' parameter.").ToYaml();
                return await Link(path, destination, reason, cancellationToken);

            case "restore":
                return await Restore(cancellationToken);

            case "reconcile":
                return await Reconcile(conflictId, choice, mergedContent, cancellationToken);

            case "transition":
                // Entity routing above handles /entity-type/id paths.
                // If we reach here, the name wasn't an entity path — transition only works on entities.
                return ResponseBuilder.Error("memory('transition') requires an entity path (e.g. path: '/goal/G-5', '/concern/SEC-1').").ToYaml();

            default:
                return ResponseBuilder.Error($"Unknown action '{action}'. Valid actions: remember (store), recall (show), forget, search, list, append, transition, compact, link, restore, reconcile.").ToYaml();
        }
    }

    // ── Skill path routing ────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to route a memory() call to <see cref="ScriniaProjectTools.SkillLoad"/>
    /// or <see cref="ScriniaProjectTools.SkillCreate"/> when the <paramref name="path"/>
    /// parameter starts with "/skill/".
    /// Returns null when the call should fall through to standard memory behavior.
    /// </summary>
    private static Task<string>? TryRouteToSkill(
        string act, string? path, string[]? content,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        // Check for "/skill/" prefix (case-insensitive)
        if (!path.StartsWith("/skill/", StringComparison.OrdinalIgnoreCase) &&
            !path.Equals("/skill", StringComparison.OrdinalIgnoreCase))
            return null;

        // Extract skill name from path: "/skill/qa" → "qa", "/skill/" → null (list)
        string? skillName = null;
        if (path.Length > "/skill/".Length)
        {
            skillName = path["/skill/".Length..].Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(skillName))
                skillName = null;
        }

        switch (act)
        {
            case "recall":
            case "show":
                // Load a specific skill prompt (or list if no name)
                return ScriniaProjectTools.SkillLoad(skillName, reconcile: false, cancellationToken);

            case "remember":
            case "store":
                // Create/override a skill — use content[0] as instructions, default scaffold to "custom"
                if (content is null || content.Length == 0)
                    return Task.FromResult(ResponseBuilder.Error(
                        "memory('remember', { path: '/skill/...' }) requires 'content' with at least one element (the skill instructions).").ToYaml());
                if (string.IsNullOrWhiteSpace(skillName))
                    return Task.FromResult(ResponseBuilder.Error(
                        "memory('remember', { path: '/skill/{name}' }) requires a skill name in the path.").ToYaml());
                return ScriniaProjectTools.SkillCreate(
                    name: skillName,
                    scaffold: "custom",
                    instructions: string.Join("\n\n", content),
                    tools: null,
                    cancellationToken);

            case "list":
                // List all skills
                return ScriniaProjectTools.SkillLoad(name: null, reconcile: false, cancellationToken);

            default:
                // Other actions (append, forget, copy, etc.) don't map to skill operations
                return null;
        }
    }

    // ── Entity path routing ──────────────────────────────────────────────────

    /// <summary>
    /// Built-in entity types routable through the memory() dispatcher.
    /// Only types that EntityDispatch supports are included (not phase/task which
    /// are handled via task() tool).
    /// </summary>
    private static readonly HashSet<string> BuiltInRoutableEntityTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "goal", "concern", "requirement", "project", "workflow", "file"
    };

    /// <summary>
    /// Returns the full set of routable entity types (built-in + user-defined).
    /// User-defined types are always routable.
    /// </summary>
    private static HashSet<string> GetRoutableEntityTypes(string? scriniaBaseDir)
    {
        var merged = EntityTypeRegistry.GetMergedTypes(scriniaBaseDir);
        var routable = new HashSet<string>(BuiltInRoutableEntityTypes, StringComparer.OrdinalIgnoreCase);
        // Add all user-defined types (they're not phase/task, so always routable)
        foreach (var key in merged.Keys)
        {
            if (!EntityTypeRegistry.Types.ContainsKey(key))
                routable.Add(key);
        }
        return routable;
    }

    /// <summary>
    /// Attempts to route a memory() call to <see cref="ScriniaProjectTools.EntityDispatch"/>
    /// when the <paramref name="path"/> parameter is a v2 entity path (starts with "/").
    /// Returns null when the call should fall through to standard memory behavior:
    /// - <paramref name="path"/> doesn't start with "/"
    /// - First path segment isn't a routable entity type
    /// - Action is remember/store but required entity params are missing (plain storage)
    /// </summary>
    private static Task<string>? TryRouteToEntity(
        string act, string? path,
        string? description, string? id, string? to, string? severity,
        string? phase, string? requirements, string? outcome,
        string? resolution, string? verifiedBy, string? evidence,
        string? context, string? definition, string? workflowRef,
        string? query, string? filter,
        CancellationToken cancellationToken)
    {
        // Resolve scrinia base dir for user-defined entity type discovery
        string? scriniaBaseDir = null;
        var storeRef = MemoryStoreContext.Current;
        if (storeRef is not null)
        {
            try { scriniaBaseDir = ScriniaProjectTools.GetScriniaBaseDir(storeRef); }
            catch { /* ignore — no base dir available */ }
        }

        // Only handle paths (starting with "/")
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
        {
            // For search, check the query parameter for entity path prefixes
            if (act == "search" && !string.IsNullOrWhiteSpace(query) && query.StartsWith('/'))
                return TryRouteSearchToEntity(query, scriniaBaseDir, cancellationToken);

            // For list with no path, check if query has entity type
            return null;
        }

        // Parse the path using merged entity types (built-in + user-defined)
        ParsedPath parsed;
        try
        {
            var entityTypes = new HashSet<string>(
                EntityTypeRegistry.GetMergedTypes(scriniaBaseDir).Keys,
                StringComparer.OrdinalIgnoreCase);
            parsed = PathParser.Parse(path, entityTypes);
        }
        catch (ArgumentException)
        {
            // Invalid path syntax — fall through to standard memory (may be a colon-separated name)
            return null;
        }

        // Check first segment is a routable entity type
        if (parsed.Segments.Count == 0) return null;
        string firstSegment = parsed.Segments[0].Value.ToLowerInvariant();
        var routableTypes = GetRoutableEntityTypes(scriniaBaseDir);
        if (!routableTypes.Contains(firstSegment)) return null;

        string entityType = firstSegment;

        // Extract entity ID from path — either from EntityPairs or the second segment
        string? entityId = id; // Explicit id param takes priority
        if (string.IsNullOrWhiteSpace(entityId) && parsed.EntityPairs.Count > 0)
            entityId = parsed.EntityPairs[0].Id;

        // ── Route based on memory action → entity action ────────────────────

        switch (act)
        {
            case "remember":
            case "store":
                // Only route to entity lifecycle when required params are present.
                // Otherwise fall through to standard memory storage.
                return HasRequiredCreateParams(entityType, description, severity, phase, requirements, context, definition)
                    ? ScriniaProjectTools.EntityDispatch(
                        action: "create",
                        type: entityType,
                        description: description,
                        id: id,
                        to: to,
                        severity: severity,
                        phase: phase,
                        requirements: requirements,
                        outcome: outcome,
                        resolution: resolution,
                        verifiedBy: verifiedBy,
                        filter: filter,
                        query: query,
                        evidence: evidence,
                        context: context,
                        definition: definition,
                        workflowRef: workflowRef,
                        cancellationToken: cancellationToken
                    )
                    : null; // Fall through — plain memory storage

            case "recall":
            case "show":
                return ScriniaProjectTools.EntityDispatch(
                    action: "show",
                    type: entityType,
                    description: description,
                    id: id,
                    to: to,
                    severity: severity,
                    phase: phase,
                    requirements: requirements,
                    outcome: outcome,
                    resolution: resolution,
                    verifiedBy: verifiedBy,
                    filter: filter,
                    query: query,
                    evidence: evidence,
                    context: context,
                    definition: definition,
                    workflowRef: workflowRef,
                    cancellationToken: cancellationToken
                );

            case "list":
                return ScriniaProjectTools.EntityDispatch(
                    action: "list",
                    type: entityType,
                    description: description,
                    id: id,
                    to: to,
                    severity: severity,
                    phase: phase,
                    requirements: requirements,
                    outcome: outcome,
                    resolution: resolution,
                    verifiedBy: verifiedBy,
                    filter: filter,
                    query: query,
                    evidence: evidence,
                    context: context,
                    definition: definition,
                    workflowRef: workflowRef,
                    cancellationToken: cancellationToken
                );

            case "search":
                return ScriniaProjectTools.EntityDispatch(
                    action: "search",
                    type: entityType,
                    description: description,
                    id: id,
                    to: to,
                    severity: severity,
                    phase: phase,
                    requirements: requirements,
                    outcome: outcome,
                    resolution: resolution,
                    verifiedBy: verifiedBy,
                    filter: filter,
                    query: query,
                    evidence: evidence,
                    context: context,
                    definition: definition,
                    workflowRef: workflowRef,
                    cancellationToken: cancellationToken
                );

            case "transition":
                // Transition always routes to entity (no plain-memory equivalent)
                return ScriniaProjectTools.EntityDispatch(
                    action: "transition",
                    type: entityType,
                    description: description,
                    id: id,
                    to: to,
                    severity: severity,
                    phase: phase,
                    requirements: requirements,
                    outcome: outcome,
                    resolution: resolution,
                    verifiedBy: verifiedBy,
                    filter: filter,
                    query: query,
                    evidence: evidence,
                    context: context,
                    definition: definition,
                    workflowRef: workflowRef,
                    cancellationToken: cancellationToken
                );

            default:
                // Other actions (append, forget, copy, compact, update, link, etc.)
                // don't have entity equivalents — fall through to standard memory.
                return null;
        }
    }

    /// <summary>
    /// Checks whether required entity creation parameters are present for the given entity type.
    /// When false, the memory() call should fall through to plain storage.
    /// For user-defined types, always returns true (description is the only param,
    /// and EntityDispatch will validate further).
    /// </summary>
    private static bool HasRequiredCreateParams(
        string entityType, string? description, string? severity, string? phase,
        string? requirements, string? context, string? definition) =>
        entityType switch
        {
            "goal" => !string.IsNullOrWhiteSpace(description),
            "concern" => !string.IsNullOrWhiteSpace(description) && !string.IsNullOrWhiteSpace(severity),
            "requirement" => !string.IsNullOrWhiteSpace(requirements),
            "project" => !string.IsNullOrWhiteSpace(description) || !string.IsNullOrWhiteSpace(context),
            "workflow" => !string.IsNullOrWhiteSpace(definition),
            "file" or "phase" or "task" => false, // never route creates
            _ => !string.IsNullOrWhiteSpace(description) // user-defined types: route when description present
        };



    /// <summary>
    /// Routes a search with an entity-path query to entity search.
    /// </summary>
    private static Task<string>? TryRouteSearchToEntity(string query, string? scriniaBaseDir, CancellationToken cancellationToken)
    {
        // Parse the query to see if it starts with an entity type
        ParsedPath parsed;
        try
        {
            var entityTypes = new HashSet<string>(
                EntityTypeRegistry.GetMergedTypes(scriniaBaseDir).Keys, StringComparer.OrdinalIgnoreCase);
            parsed = PathParser.Parse(query, entityTypes);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (parsed.Segments.Count == 0) return null;
        string firstSegment = parsed.Segments[0].Value.ToLowerInvariant();
        var routableTypes = GetRoutableEntityTypes(scriniaBaseDir);
        if (!routableTypes.Contains(firstSegment)) return null;

        return ScriniaProjectTools.EntityDispatch(
            action: "search",
            type: firstSegment,
            query: query,
            cancellationToken: cancellationToken);
    }
}
