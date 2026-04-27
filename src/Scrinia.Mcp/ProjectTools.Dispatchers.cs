using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using YamlDotNet.Serialization;

namespace Scrinia.Mcp;

public sealed partial class ScriniaProjectTools
{
    // ── Entity Dispatcher ─────────────────────────────────────────────────

    /// <summary>Unified entity operations dispatcher — routes to existing internal methods.
    /// No longer exposed as an MCP tool; called internally via memory() path routing.</summary>
    public static async Task<string> EntityDispatch(
        [Description("Action: 'create', 'update', 'transition', 'show', 'list', 'search'.")] string action,
        [Description("Entity type: 'goal', 'concern', 'requirement', 'project', 'workflow', 'file'.")] string type,
        [Description("Entity description (create goal/concern/project).")] string? description = null,
        [Description("Entity ID for operations on existing entities.")] string? id = null,
        [Description("Target state for transitions (e.g., 'complete', 'resolved', 'fulfilled').")] string? to = null,
        [Description("Concern severity: 'high', 'medium', 'low' (create concern).")] string? severity = null,
        [Description("Phase scope for concerns.")] string? phase = null,
        [Description("Requirements text with REQ-IDs (create requirement).")] string? requirements = null,
        [Description("Outcome note (complete goal).")] string? outcome = null,
        [Description("Resolution description (resolve concern).")] string? resolution = null,
        [Description("Verification method: 'debugger', 'qa', 'manual' (resolve concern).")] string? verifiedBy = null,
        [Description("Status filter for list (e.g., 'active', 'resolved').")] string? filter = null,
        [Description("Search query string.")] string? query = null,
        [Description("Evidence for requirement fulfillment.")] string? evidence = null,
        [Description("Project context description (create project).")] string? context = null,
        [Description("Workflow definition JSON (create/update workflow).")] string? definition = null,
        [Description("Workflow name for goal creation (default: 'default').")] string? workflowRef = null,
        CancellationToken cancellationToken = default)
    {
        // ── Validate type ────────────────────────────────────────────────────
        string typeLower = type.Trim().ToLowerInvariant();
        string? scriniaBaseDir = null;
        try { scriniaBaseDir = GetScriniaBaseDir(CurrentStore); }
        catch { /* ignore — no base dir available */ }
        var mergedTypes = EntityTypeRegistry.GetMergedTypes(scriniaBaseDir);
        if (!mergedTypes.ContainsKey(typeLower))
        {
            string validTypes = string.Join(", ", mergedTypes.Keys.Order());
            return ResponseBuilder.Error($"Unknown entity type '{type}'. Valid types: {validTypes}.").ToYaml();
        }

        // ── Validate action ──────────────────────────────────────────────────
        string act = action.Trim().ToLowerInvariant();
        string[] validActions = ["create", "update", "transition", "show", "list", "search"];
        if (!validActions.Contains(act))
            return ResponseBuilder.Error($"Unknown action '{action}'. Valid actions: create, update, transition, show, list, search.").ToYaml();

        // ── Route ────────────────────────────────────────────────────────────
        switch (act)
        {
            // ── CREATE ───────────────────────────────────────────────────────
            case "create":
                switch (typeLower)
                {
                    case "goal":
                        if (string.IsNullOrWhiteSpace(description))
                            return ResponseBuilder.Error("memory('remember', { path: '/goal/...' }) requires 'description' parameter.").ToYaml();
                        return await GoalUpdate("add", description, goalId: null, outcome: null, workflowRef: workflowRef, cancellationToken);

                    case "concern":
                        if (string.IsNullOrWhiteSpace(description))
                            return ResponseBuilder.Error("memory('remember', { path: '/concern/...' }) requires 'description' parameter.").ToYaml();
                        if (string.IsNullOrWhiteSpace(severity))
                            return ResponseBuilder.Error("memory('remember', { path: '/concern/...' }) requires 'severity' parameter.").ToYaml();
                        if (string.IsNullOrWhiteSpace(phase))
                            return ResponseBuilder.Error("memory('remember', { path: '/concern/...' }) requires 'phase' parameter.").ToYaml();
                        return await ConcernAdd(description, severity, phase, id, cancellationToken);

                    case "requirement":
                        if (string.IsNullOrWhiteSpace(requirements))
                            return ResponseBuilder.Error("memory('remember', { path: '/requirement/...' }) requires 'requirements' parameter.").ToYaml();
                        return await PlanRequirements(requirements, cancellationToken);

                    case "project":
                        string? projectDesc = description ?? context;
                        if (string.IsNullOrWhiteSpace(projectDesc))
                            return ResponseBuilder.Error("memory('remember', { path: '/project/...' }) requires 'description' or 'context' parameter.").ToYaml();
                        return await ProjectInit(projectDesc, cancellationToken);

                    case "workflow":
                        return await CreateOrUpdateWorkflow("create", definition, cancellationToken);

                    case "file":
                        return ResponseBuilder.Error("File entities are computed views — use codeRefs on memory('remember') to track files.").ToYaml();

                    default:
                        return await UserEntityCreate(typeLower, description, id, mergedTypes, cancellationToken);
                }

            // ── UPDATE ───────────────────────────────────────────────────────
            case "update":
                switch (typeLower)
                {
                    case "goal":
                        if (string.IsNullOrWhiteSpace(id))
                            return ResponseBuilder.Error("memory('remember', { path: '/goal/{id}' }) requires 'id' parameter.").ToYaml();
                        if (string.IsNullOrWhiteSpace(description))
                            return ResponseBuilder.Error("memory('remember', { path: '/goal/{id}' }) requires 'description' parameter.").ToYaml();
                        return await GoalUpdate("edit", description, goalId: id, outcome: null, cancellationToken: cancellationToken);

                    case "workflow":
                        return await CreateOrUpdateWorkflow("update", definition, cancellationToken);

                    case "file":
                        return ResponseBuilder.Error("File entities are computed views — use codeRefs on memory('remember') to track files.").ToYaml();

                    default:
                        return ResponseBuilder.Error($"Update is not supported for type '{type}'. Supported types: goal, workflow.").ToYaml();
                }

            // ── TRANSITION ───────────────────────────────────────────────────
            case "transition":
            {
                if (typeLower == "file")
                    return ResponseBuilder.Error("File entities are computed views — use codeRefs on memory('remember') to track files.").ToYaml();

                if (string.IsNullOrWhiteSpace(to))
                    return ResponseBuilder.Error($"memory('transition', {{ path: '/{type}/...' }}) requires 'to' parameter.").ToYaml();

                // Validate transition against registry (built-in or user-defined)
                var typeDef = mergedTypes.GetValueOrDefault(typeLower);
                if (typeDef is null)
                    return ResponseBuilder.Error($"Unknown entity type '{type}'.").ToYaml();
                string toLower = to.Trim().ToLowerInvariant();
                var transition = typeDef.Transitions.FirstOrDefault(t =>
                    t.ToState.Equals(toLower, StringComparison.OrdinalIgnoreCase));
                if (transition is null)
                {
                    string validStates = string.Join(", ", typeDef.Transitions.Select(t => t.ToState).Distinct());
                    return ResponseBuilder.Error($"memory('transition', {{ path: '/{type}/...', to: '{to}' }}) — invalid target state. Valid transitions: {validStates}.").ToYaml();
                }

                switch (typeLower)
                {
                    case "goal":
                        if (!toLower.Equals("complete", StringComparison.OrdinalIgnoreCase))
                            return ResponseBuilder.Error("memory('transition', { path: '/goal/...' }) only supports to: 'complete'.").ToYaml();
                        if (string.IsNullOrWhiteSpace(id))
                            return ResponseBuilder.Error("memory('transition', { path: '/goal/{id}', to: 'complete' }) requires 'id' parameter.").ToYaml();
                        return await GoalUpdate("complete", description: null, goalId: id, outcome, cancellationToken: cancellationToken);

                    case "concern":
                        if (!toLower.Equals("resolved", StringComparison.OrdinalIgnoreCase))
                            return ResponseBuilder.Error("memory('transition', { path: '/concern/...' }) only supports to: 'resolved'.").ToYaml();
                    {
                        var missing = new List<string>();
                        if (string.IsNullOrWhiteSpace(id)) missing.Add("id");
                        if (string.IsNullOrWhiteSpace(resolution)) missing.Add("resolution");
                        if (string.IsNullOrWhiteSpace(verifiedBy)) missing.Add("verifiedBy");
                        if (missing.Count > 0)
                            return ResponseBuilder.Error($"memory('transition', {{ path: '/concern/...', to: 'resolved' }}) requires '{string.Join("', '", missing)}' parameters.").ToYaml();
                        // ConcernResolve expects a qualified name (concern:xxx) or raw name
                        string concernName = id!.StartsWith("concern:", StringComparison.OrdinalIgnoreCase) ? id : $"concern:{id}";
                        return await ConcernResolve(concernName, resolution!, verifiedBy!, cancellationToken);
                    }

                    case "requirement":
                        if (!toLower.Equals("fulfilled", StringComparison.OrdinalIgnoreCase))
                            return ResponseBuilder.Error("memory('transition', { path: '/requirement/...' }) only supports to: 'fulfilled'.").ToYaml();
                    {
                        var missing = new List<string>();
                        if (string.IsNullOrWhiteSpace(id)) missing.Add("id");
                        if (string.IsNullOrWhiteSpace(evidence)) missing.Add("evidence");
                        if (missing.Count > 0)
                            return ResponseBuilder.Error($"memory('transition', {{ path: '/requirement/...', to: 'fulfilled' }}) requires '{string.Join("', '", missing)}' parameters.").ToYaml();
                        return await RequirementResolve(id!, evidence!, cancellationToken);
                    }

                    default:
                        return await UserEntityTransition(typeLower, id, toLower, transition, description, cancellationToken);
                }
            }

            // ── SHOW ─────────────────────────────────────────────────────────
            case "show":
                switch (typeLower)
                {
                    case "project":
                        return await PlanStatus(cancellationToken);

                    case "goal":
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            // Show specific goal — extract from GoalList output
                            string listOutput = await GoalUpdate("list", description: null, goalId: null, outcome: null, cancellationToken: cancellationToken);
                            // Find the line matching the requested goal ID
                            string searchId = id.Trim();
                            var lines = listOutput.Split('\n');
                            foreach (string line in lines)
                            {
                                if (line.Contains($"[{searchId}]", StringComparison.OrdinalIgnoreCase) ||
                                    line.Contains($"[{searchId.ToUpperInvariant()}]", StringComparison.OrdinalIgnoreCase))
                                {
                                    return ResponseBuilder.Success(line.Trim()).WithPath($"/goal/{searchId}").WithAction("shown").ToYaml();
                                }
                                // Short-form match (G-N matches G-N-xxx)
                                if (ShortGoalIdPattern.IsMatch(searchId) &&
                                    System.Text.RegularExpressions.Regex.IsMatch(line,
                                        $@"\[{Regex.Escape(searchId)}(-[a-fA-F0-9]+)?\]",
                                        RegexOptions.IgnoreCase))
                                {
                                    return ResponseBuilder.Success(line.Trim()).WithPath($"/goal/{searchId}").WithAction("shown").ToYaml();
                                }
                            }
                            return ResponseBuilder.Error($"Goal '{id}' not found. Use memory('list', {{ path: '/goal/' }}) to see all goals.").ToYaml();
                        }
                        // No ID — show all goals
                        return await GoalUpdate("list", description: null, goalId: null, outcome: null, cancellationToken: cancellationToken);

                    case "concern":
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            // Show specific concern content
                            try
                            {
                                var store = CurrentStore;
                                string concernName = id.StartsWith("concern:", StringComparison.OrdinalIgnoreCase) ? id : $"concern:{id}";
                                string content = await ReadMemoryAsync(store, concernName, cancellationToken);
                                return ResponseBuilder.Success(Truncate(content)).WithPath($"/concern/{id}").WithAction("shown").ToYaml();
                            }
                            catch (FileNotFoundException)
                            {
                                return ResponseBuilder.Error($"Concern '{id}' not found.").ToYaml();
                            }
                        }
                        // No ID — list all active concerns
                        return await ConcernList(phaseFilter: null, statusFilter: null, cancellationToken);

                    case "requirement":
                        return await RequirementList(cancellationToken);

                    case "workflow":
                    {
                        string workflowName = !string.IsNullOrWhiteSpace(id) ? id.Trim() : "default";
                        var store = CurrentStore;
                        var (wf, wfWarn) = await ResolveWorkflowAsync(store, workflowName, cancellationToken);
                        string json = JsonSerializer.Serialize(wf, PlanningJsonContext.Default.WorkflowDefinition);
                        string source = wf == WorkflowDefinition.DefaultGoalWorkflow ? " (built-in)" : " (override)";
                        var wfResponse = ResponseBuilder.Success($"Workflow: {wf.Name}{source}\n\n{json}")
                            .WithPath($"/workflow/{wf.Name}")
                            .WithAction("shown");
                        if (wfWarn is not null)
                            wfResponse = wfResponse.WithActionNeeded(wfWarn);
                        return wfResponse.ToYaml();
                    }

                    case "file":
                        return FileShow(id);

                    default:
                        return await UserEntityShow(typeLower, id, cancellationToken);
                }

            // ── LIST ─────────────────────────────────────────────────────────
            case "list":
                switch (typeLower)
                {
                    case "goal":
                        return await GoalUpdate("list", description: null, goalId: null, outcome: null, cancellationToken: cancellationToken);

                    case "concern":
                        return await ConcernList(phaseFilter: null, statusFilter: filter, cancellationToken);

                    case "requirement":
                        return await RequirementList(cancellationToken);

                    case "workflow":
                    {
                        var store = CurrentStore;
                        var overrides = new List<string>();
                        try
                        {
                            var (wfScope, _) = store.ParseQualifiedName("workflow:placeholder");
                            var entries = store.LoadIndex(wfScope);
                            foreach (var entry in entries)
                                overrides.Add(entry.Name);
                        }
                        catch { /* workflow topic not yet created — no overrides */ }

                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine("Available workflows:");
                        sb.AppendLine($"  - default (built-in: {WorkflowDefinition.DefaultGoalWorkflow.Name})");
                        foreach (var name in overrides)
                            sb.AppendLine($"  - {name} (override)");
                        return ResponseBuilder.Success(sb.ToString().TrimEnd()).WithAction("listed").ToYaml();
                    }

                    case "file":
                        return FileList(query);

                    default:
                        return UserEntityList(typeLower, filter);
                }

            // ── SEARCH ───────────────────────────────────────────────────────
            case "search":
            {
                if (typeLower == "file")
                    return FileList(query);

                if (string.IsNullOrWhiteSpace(query))
                    return ResponseBuilder.Error("Search requires 'query' parameter.").ToYaml();

                // Search entity-classified scopes only: goal lives in project:context,
                // concerns in concern:*, requirements in project:requirements.
                // Use the memory search with scopes restricted to entity topics.
                var memoryTools = new ScriniaMcpTools();
                return await memoryTools.Search(query, scopes: null, limit: 20,
                    excludeTopics: "task,plan,learn,sessions,skill,backlog,research,checkpoint,testing,server,qa,quality,security,cartography,bugs,chaos,benchmark,landscape,feedback",
                    cancellationToken);
            }

            default:
                return ResponseBuilder.Error($"Unknown action '{action}'. Valid actions: create, update, transition, show, list, search.").ToYaml();
        }
    }

    // ── User-defined entity helpers ─────────────────────────────────────────

    /// <summary>
    /// Creates an instance of a user-defined entity type by storing a memory
    /// at the entity topic path with status/state keywords.
    /// </summary>
    private static async Task<string> UserEntityCreate(
        string typeLower, string? description, string? id,
        IReadOnlyDictionary<string, EntityTypeDefinition> mergedTypes,
        CancellationToken cancellationToken)
    {
        if (!mergedTypes.TryGetValue(typeLower, out var typeDef))
            return ResponseBuilder.Error($"Unknown entity type '{typeLower}'.").ToYaml();

        if (string.IsNullOrWhiteSpace(description))
            return ResponseBuilder.Error($"memory('remember', {{ path: '/{typeLower}/...' }}) requires 'description' parameter.").ToYaml();

        var store = CurrentStore;

        // Generate ID if not provided
        string entityId = id ?? DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        string defaultState = typeDef.DefaultState ?? typeDef.ValidStates.FirstOrDefault() ?? "created";

        // Build content
        string content =
            $"## {typeLower}: {entityId}\n" +
            $"**Description:** {description}\n" +
            $"**Status:** {defaultState}\n" +
            $"**Created:** {DateTimeOffset.UtcNow:o}\n";

        string qualifiedName = $"entity_{typeLower}:{entityId}";

        // Extract keywords from description
        var (autoKeywords, _) = Scrinia.Core.Search.TextAnalysis.AnalyzeText(description);
        string[] explicitKeywords = [$"status:{defaultState}", $"type:{typeLower}"];
        string[] mergedKeywords = Scrinia.Core.Search.TextAnalysis.MergeKeywords(explicitKeywords, autoKeywords);

        await WritePlanningMemoryAsync(store, qualifiedName, content,
            archiveExisting: false,
            keywords: mergedKeywords,
            cancellationToken);

        return ResponseBuilder.Success($"Created {typeLower} '{entityId}' [status:{defaultState}].")
            .WithPath($"/{typeLower}/{entityId}")
            .WithAction("created")
            .ToYaml();
    }

    /// <summary>
    /// Transitions a user-defined entity to a new state, validating against the
    /// transition definition's required parameters.
    /// </summary>
    private static async Task<string> UserEntityTransition(
        string typeLower, string? id, string toLower,
        TransitionDefinition transition, string? description,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            return ResponseBuilder.Error($"memory('transition', {{ path: '/{typeLower}/...', to: '{toLower}' }}) requires 'id' parameter.").ToYaml();

        var store = CurrentStore;
        string qualifiedName = $"entity_{typeLower}:{id}";

        // Read existing content
        string existingContent;
        try
        {
            existingContent = await ReadMemoryAsync(store, qualifiedName, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return ResponseBuilder.Error($"{typeLower} '{id}' not found.").ToYaml();
        }

        // Validate from-state if transition is not wildcard
        if (transition.FromState != "*")
        {
            // Extract current status from keywords
            var (scope, _) = store.ParseQualifiedName(qualifiedName);
            var entries = store.LoadIndex(scope);
            var entry = entries.FirstOrDefault(e => e.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                string? currentStatus = entry.Keywords?
                    .FirstOrDefault(k => k.StartsWith("status:", StringComparison.OrdinalIgnoreCase))
                    ?["status:".Length..];
                if (currentStatus is not null &&
                    !currentStatus.Equals(transition.FromState, StringComparison.OrdinalIgnoreCase))
                {
                    return ResponseBuilder.Error(
                        $"Cannot transition {typeLower} '{id}' from '{currentStatus}' to '{toLower}'. " +
                        $"Transition requires from-state '{transition.FromState}'.").ToYaml();
                }
            }
        }

        // Update content with new status
        string updatedContent = existingContent + $"\n**Transitioned to:** {toLower}\n**At:** {DateTimeOffset.UtcNow:o}\n";
        if (!string.IsNullOrWhiteSpace(description))
            updatedContent += $"**Note:** {description}\n";

        // Update keywords: replace status keyword
        var (entryScope, _) = store.ParseQualifiedName(qualifiedName);
        var existingEntries = store.LoadIndex(entryScope);
        var existingEntry = existingEntries.FirstOrDefault(e => e.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
        var updatedKeywords = (existingEntry?.Keywords ?? [])
            .Where(k => !k.StartsWith("status:", StringComparison.OrdinalIgnoreCase))
            .Append($"status:{toLower}")
            .ToArray();

        await WritePlanningMemoryAsync(store, qualifiedName, updatedContent,
            archiveExisting: true,
            keywords: updatedKeywords,
            cancellationToken);

        return ResponseBuilder.Success($"Transitioned {typeLower} '{id}' to '{toLower}'.")
            .WithPath($"/{typeLower}/{id}")
            .WithAction("transitioned")
            .ToYaml();
    }

    /// <summary>
    /// Shows a user-defined entity instance or lists all instances if no ID given.
    /// </summary>
    private static async Task<string> UserEntityShow(
        string typeLower, string? id, CancellationToken cancellationToken)
    {
        var store = CurrentStore;

        if (!string.IsNullOrWhiteSpace(id))
        {
            // Show specific entity
            string qualifiedName = $"entity_{typeLower}:{id}";
            try
            {
                string content = await ReadMemoryAsync(store, qualifiedName, cancellationToken);
                return ResponseBuilder.Success(Truncate(content))
                    .WithPath($"/{typeLower}/{id}")
                    .WithAction("shown")
                    .ToYaml();
            }
            catch (FileNotFoundException)
            {
                return ResponseBuilder.Error($"{typeLower} '{id}' not found.").ToYaml();
            }
        }

        // No ID — list all entities of this type
        return UserEntityList(typeLower, filter: null);
    }

    /// <summary>
    /// Lists all instances of a user-defined entity type, optionally filtered by status.
    /// </summary>
    private static string UserEntityList(string typeLower, string? filter)
    {
        var store = CurrentStore;

        IReadOnlyList<ArtifactEntry> allEntries;
        try
        {
            var (scope, _) = store.ParseQualifiedName($"entity_{typeLower}:placeholder");
            allEntries = store.LoadIndex(scope);
        }
        catch
        {
            return ResponseBuilder.Success($"No {typeLower} entities found.").WithAction("listed").ToYaml();
        }

        var filtered = allEntries.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(filter))
            filtered = filtered.Where(e => HasKeyword(e, $"status:{filter}"));

        var entries = filtered.ToList();
        if (entries.Count == 0)
        {
            string filterNote = filter is not null ? $" with status '{filter}'" : "";
            return ResponseBuilder.Success($"No {typeLower} entities{filterNote}.").WithAction("listed").ToYaml();
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{typeLower} entities ({entries.Count}):");
        sb.AppendLine();

        foreach (var entry in entries)
        {
            string statusKw = entry.Keywords?
                .FirstOrDefault(k => k.StartsWith("status:", StringComparison.OrdinalIgnoreCase))
                ?? "status:unknown";
            sb.AppendLine($"- /{typeLower}/{entry.Name} [{statusKw}]");

            if (sb.Length > MaxResponseChars - 200)
            {
                sb.AppendLine("[... truncated to 8KB limit]");
                break;
            }
        }

        return ResponseBuilder.Success(sb.ToString().TrimEnd()).WithAction("listed").ToYaml();
    }
}
