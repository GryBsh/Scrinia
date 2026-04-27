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
        "Required reading — call at session start, then commit content to your project's agent file. " +
        "Covers memory patterns, skills, and session recall.")]
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
        "'compact' (merge chunks in a memory), " +
        "'link' (add codeRefs to a memory), " +
        "'restore' (resume agent context — agent profile, patterns, session log, available skills), " +
        "'reconcile' (scan for merge conflicts or resolve a specific conflict).")]
    public async Task<string> Memory(
        [Description("Action to perform: 'remember' (or 'store'), 'recall' (or 'show'), 'forget', 'search', 'list', 'append', 'compact', 'link', 'restore', 'reconcile'.")] string action,
        [Description("Memory path (e.g., '/skill/qa', '/patterns/retry', or 'topic:subject' for backward compat).")] string? path = null,
        [Description("Content array — each element becomes one chunk (store).")] string[]? content = null,
        [Description("Text content to append as new chunk (append).")] string? appendContent = null,
        [Description("Search query string (search).")] string? query = null,
        [Description("Target name for link.")] string? destination = null,
        [Description("Optional description (store).")] string? description = null,
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
        // Map aliases to their response action names
        string responseAction = act switch
        {
            "remember" => "remembered",
            "recall" => "recalled",
            _ => null! // null means use the handler's default
        };

        // ── Skill path routing ───────────────────────────────────────────────
        // When `path` starts with "/skill/", route to SkillLoad/SkillCreate.
        {
            var skillResult = TryRouteToSkill(act, path, content, cancellationToken);
            if (skillResult is not null)
                return await skillResult;
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

            default:
                return ResponseBuilder.Error($"Unknown action '{action}'. Valid actions: remember (store), recall (show), forget, search, list, append, compact, link, restore, reconcile.").ToYaml();
        }
    }

    // ── Skill path routing ────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to route a memory() call to <see cref="SkillLoad"/> or
    /// <see cref="SkillCreate"/> when the <paramref name="path"/> parameter
    /// starts with "/skill/". Returns null when the call should fall through
    /// to standard memory behavior.
    /// </summary>
    private static Task<string>? TryRouteToSkill(
        string act, string? path, string[]? content,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (!path.StartsWith("/skill/", StringComparison.OrdinalIgnoreCase) &&
            !path.Equals("/skill", StringComparison.OrdinalIgnoreCase))
            return null;

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
                return SkillLoad(skillName, reconcile: false, cancellationToken);

            case "remember":
            case "store":
                if (content is null || content.Length == 0)
                    return Task.FromResult(ResponseBuilder.Error(
                        "memory('remember', { path: '/skill/...' }) requires 'content' with at least one element (the skill instructions).").ToYaml());
                if (string.IsNullOrWhiteSpace(skillName))
                    return Task.FromResult(ResponseBuilder.Error(
                        "memory('remember', { path: '/skill/{name}' }) requires a skill name in the path.").ToYaml());
                return SkillCreate(
                    name: skillName,
                    scaffold: "custom",
                    instructions: string.Join("\n\n", content),
                    tools: null,
                    cancellationToken);

            case "list":
                return SkillLoad(name: null, reconcile: false, cancellationToken);

            default:
                return null;
        }
    }

}
