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

// ── Planning DTOs ────────────────────────────────────────────────────────────

/// <summary>Represents a project tracked in scrinia planning memory (project:* topic).</summary>
public sealed record ProjectRecord(
    string Id,
    string Name,
    string? Description,
    string[]? Goals,
    string[]? Constraints);

/// <summary>Represents a plan (phase) tracked in scrinia planning memory (plan:* topic).</summary>
public sealed record PlanRecord(
    string Id,
    string Phase,
    string? Goal,
    string? Status,
    string[]? TaskIds);

/// <summary>Represents a task tracked in scrinia planning memory (task:* topic).</summary>
public sealed record TaskRecord(
    string Id,
    string Phase,
    string Name,
    string? Description,
    string? Status,
    string[]? DependsOn,
    string[]? AcceptanceCriteria);

/// <summary>Represents a concern/risk tracked across project phases (concern:* topic).</summary>
public sealed record ConcernRecord(
    string Id,
    string Phase,
    string Description,
    string Severity,
    string? Status,
    string? Resolution,
    string? ResolvedAt);

/// <summary>Represents a reusable agent skill/prompt template (skill:* topic).</summary>
public sealed record SkillRecord(
    string Id,
    string Name,
    string? Description,
    string? SystemPrompt,
    string[]? Tools,
    string[]? Capabilities);

/// <summary>Represents a research investigation and its findings (research:* topic).</summary>
public sealed record ResearchRecord(
    string Id,
    string Topic,
    string? Question,
    string? Status,
    string? Findings,
    string[]? Sources);

/// <summary>Represents a project goal that can evolve over time (project:goals topic).</summary>
public sealed record GoalRecord(
    string Id,
    string Description,
    string? Status,
    string? Outcome,
    string? CompletedAt);

// ── Source-gen JSON context (trimming-safe) ──────────────────────────────────

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(ProjectRecord))]
[JsonSerializable(typeof(PlanRecord))]
[JsonSerializable(typeof(TaskRecord))]
[JsonSerializable(typeof(ConcernRecord))]
[JsonSerializable(typeof(SkillRecord))]
[JsonSerializable(typeof(ResearchRecord))]
[JsonSerializable(typeof(GoalRecord))]
[JsonSerializable(typeof(ProjectRecord[]))]
[JsonSerializable(typeof(PlanRecord[]))]
[JsonSerializable(typeof(TaskRecord[]))]
[JsonSerializable(typeof(ConcernRecord[]))]
[JsonSerializable(typeof(SkillRecord[]))]
[JsonSerializable(typeof(ResearchRecord[]))]
[JsonSerializable(typeof(GoalRecord[]))]
[JsonSerializable(typeof(WorkflowDefinition))]
[JsonSerializable(typeof(WorkflowActivity))]
[JsonSerializable(typeof(WorkflowActivity[]))]
[JsonSerializable(typeof(GateValidation))]
[JsonSerializable(typeof(GateValidation[]))]
[JsonSerializable(typeof(SkillFileMeta))]
[JsonSerializable(typeof(WorkflowFileMeta))]
[JsonSerializable(typeof(AgentFileMeta))]
[JsonSerializable(typeof(Dictionary<string,string>))]
public partial class PlanningJsonContext : JsonSerializerContext;

// ── Planning MCP tool class ──────────────────────────────────────────────────

/// <summary>
/// MCP tools for project planning — stores and retrieves planning memories using
/// the plan:*, task:*, project:*, learn:*, and backlog:* topic conventions.
/// </summary>
[McpServerToolType]
public sealed class ScriniaProjectTools
{
    /// <summary>
    /// Copilot CLI hard-truncates MCP tool responses at 10 KB (fixed constant in Iw()).
    /// VS Code Copilot Chat truncates at ~50% of prompt token budget (dynamic).
    /// We cap at 8 KB to stay safely under the CLI limit with 2 KB headroom.
    /// </summary>
    private const int MaxResponseChars = 8 * 1024;

    // ── Compiled regex patterns (single source of truth) ─────────────────────
    private const string GoalIdCore = @"\d+(?:-[a-fA-F0-9]+)?";
    private static readonly Regex GoalIdPattern = new($@"G-({GoalIdCore})", RegexOptions.Compiled);
    private static readonly Regex BracketedGoalIdPattern = new($@"\[G-({GoalIdCore})\]", RegexOptions.Compiled);
    private static readonly Regex BracketedGoalIdFullPattern = new($@"\[(G-{GoalIdCore})\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GoalIdNumericPattern = new($@"\[G-(\d+)(?:-[a-fA-F0-9]+)?\]", RegexOptions.Compiled);
    private static readonly Regex GoalIdStructuredPattern = new($@"^\[G-{GoalIdCore}\]", RegexOptions.Compiled);
    private static readonly Regex ShortGoalIdPattern = new(@"^G-\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    internal static readonly Regex PhaseNumberPattern = new(@"Phase\s+0*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GoalsSectionPattern = new(@"^#{0,4}\s*Goals\s*:?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GoalsSectionAltPattern = new(@"^Goals\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OriginalGoalsPattern = new(@"^[Oo]riginal goals?\s*:\s*\d+", RegexOptions.Compiled);
    private static readonly Regex DependsOnPattern = new(@"^Depends\s+on:\s*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex FilesFieldPattern = new(@"^Files:\s*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex CompletedTimestampPattern = new(@"Completed:\s*(\S+)", RegexOptions.Compiled);
    private static readonly Regex SectionHeadingPattern = new(@"^#{1,4}\s+\S", RegexOptions.Compiled);
    private static readonly Regex ReqIdPattern = new(@"\b([A-Z]+-\d+)\b", RegexOptions.Compiled);
    private static readonly Regex TaskHeaderPattern = new(@"^##\s+Task\s+(\w+)", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex DigitPattern = new(@"\d+", RegexOptions.Compiled);
    private static readonly Regex GoalStatusPrefixPattern = new($@"^\[G-{GoalIdCore}\]\s*\[[\w]+\]\s*", RegexOptions.Compiled);

    internal static string Truncate(string text) =>
        text.Length <= MaxResponseChars ? text : text[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

    private static IMemoryStore CurrentStore =>
        MemoryStoreContext.Current ?? throw new InvalidOperationException(
            "No memory store configured. Call MemoryStoreContext.Current = ... before using planning tools.");

    // ── MCP Tools ────────────────────────────────────────────────────────────

    // ── Dispatchers ──────────────────────────────────────────────────────────

    /// <summary>Dispatcher for task('plan'). Init and status moved to memory() dispatcher.
    /// No longer exposed as an MCP tool; called internally via task('plan').</summary>
    public async Task<string> PlanDispatch(
        [Description("Action: 'tasks'.")] string action,
        [Description("Phase ID for task decomposition (tasks).")] string? phaseId = null,
        [Description("Free-text task definitions (tasks).")] string? tasks = null,
        CancellationToken cancellationToken = default)
    {
        string act = action.Trim().ToLowerInvariant();
        switch (act)
        {
            case "tasks":
                if (string.IsNullOrWhiteSpace(phaseId))
                    return ResponseBuilder.Error("task('plan') requires 'phaseId' parameter.").ToYaml();
                if (string.IsNullOrWhiteSpace(tasks))
                    return ResponseBuilder.Error("task('plan') requires 'tasks' parameter.").ToYaml();
                return await PlanTasks(phaseId, tasks, cancellationToken);
            default:
                return ResponseBuilder.Error($"Unknown action '{action}'. Use memory('remember', {{ path: '/project/...' }}) for init, memory('recall', {{ path: '/project/status' }}) for status. task('plan') is the only plan action.").ToYaml();
        }
    }

    /// <summary>Internal dispatcher for requirement operations — delegates to PlanRequirements/RequirementResolve/RequirementList. Exposed via memory() dispatcher.</summary>
    public async Task<string> RequirementDispatch(
        string action,
        string? requirements = null,
        string? id = null,
        string? evidence = null,
        CancellationToken cancellationToken = default)
    {
        string act = action.Trim().ToLowerInvariant();
        switch (act)
        {
            case "add":
                if (string.IsNullOrWhiteSpace(requirements))
                    return ResponseBuilder.Error("requirement('add') requires 'requirements' parameter.").ToYaml();
                return await PlanRequirements(requirements, cancellationToken);
            case "resolve":
                if (string.IsNullOrWhiteSpace(id))
                    return ResponseBuilder.Error("requirement('resolve') requires 'id' parameter (e.g., 'REQ-01').").ToYaml();
                if (string.IsNullOrWhiteSpace(evidence))
                    return ResponseBuilder.Error("requirement('resolve') requires 'evidence' parameter.").ToYaml();
                return await RequirementResolve(id, evidence, cancellationToken);
            case "list":
                return await RequirementList(cancellationToken);
            default:
                return ResponseBuilder.Error($"Unknown action '{action}'. Valid actions: 'add', 'resolve', 'list'.").ToYaml();
        }
    }

    /// <summary>Dispatcher for skill load and create operations.
    /// No longer exposed as an MCP tool; called internally via memory() path routing.</summary>
    public async Task<string> SkillDispatch(
        [Description("Action: 'load' or 'create'.")] string action,
        [Description("Skill name (load — optional for listing, create — required).")] string? name = null,
        [Description("Scaffold type: researcher/reviewer/domain-expert/custom (create).")] string? scaffold = null,
        [Description("Additional instructions (create).")] string? instructions = null,
        [Description("Comma-separated tool names (create, custom scaffold).")] string? tools = null,
        [Description("Show both built-in and override for reconciliation (load).")] bool reconcile = false,
        CancellationToken cancellationToken = default)
    {
        string act = action.Trim().ToLowerInvariant();
        switch (act)
        {
            case "load":
                return await SkillLoad(name, reconcile, cancellationToken);
            case "create":
                if (string.IsNullOrWhiteSpace(name))
                    return ResponseBuilder.Error("memory('remember', { path: '/skill/...' }) requires 'name' in the path.").ToYaml();
                if (string.IsNullOrWhiteSpace(scaffold))
                    return ResponseBuilder.Error("memory('remember', { path: '/skill/...' }) requires 'scaffold' parameter.").ToYaml();
                return await SkillCreate(name, scaffold, instructions, tools, cancellationToken);
            default:
                return ResponseBuilder.Error($"Unknown action '{action}'. Valid actions: 'load', 'create'.").ToYaml();
        }
    }

    // ── Entity Dispatcher ─────────────────────────────────────────────────

    /// <summary>Unified entity operations dispatcher — routes to existing internal methods.
    /// No longer exposed as an MCP tool; called internally via memory() path routing.</summary>
    public async Task<string> EntityDispatch(
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
                                    return ResponseBuilder.Success(line.Trim()).WithPath($"goal:{searchId}").WithAction("shown").ToYaml();
                                }
                                // Short-form match (G-N matches G-N-xxx)
                                if (ShortGoalIdPattern.IsMatch(searchId) &&
                                    System.Text.RegularExpressions.Regex.IsMatch(line,
                                        $@"\[{Regex.Escape(searchId)}(-[a-fA-F0-9]+)?\]",
                                        RegexOptions.IgnoreCase))
                                {
                                    return ResponseBuilder.Success(line.Trim()).WithPath($"goal:{searchId}").WithAction("shown").ToYaml();
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
                                return ResponseBuilder.Success(Truncate(content)).WithPath($"concern:{id}").WithAction("shown").ToYaml();
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
                            .WithPath($"workflow:{wf.Name}")
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
    private async Task<string> UserEntityCreate(
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
            .WithPath($"{typeLower}:{entityId}")
            .WithAction("created")
            .ToYaml();
    }

    /// <summary>
    /// Transitions a user-defined entity to a new state, validating against the
    /// transition definition's required parameters.
    /// </summary>
    private async Task<string> UserEntityTransition(
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
            .WithPath($"{typeLower}:{id}")
            .WithAction("transitioned")
            .ToYaml();
    }

    /// <summary>
    /// Shows a user-defined entity instance or lists all instances if no ID given.
    /// </summary>
    private async Task<string> UserEntityShow(
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
                    .WithPath($"{typeLower}:{id}")
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
            sb.AppendLine($"- {typeLower}:{entry.Name} [{statusKw}]");

            if (sb.Length > MaxResponseChars - 200)
            {
                sb.AppendLine("[... truncated to 8KB limit]");
                break;
            }
        }

        return ResponseBuilder.Success(sb.ToString().TrimEnd()).WithAction("listed").ToYaml();
    }

    // ── Internals ────────────────────────────────────────────────────────────

    /// <summary>Initialize a project by storing goals, context, and constraints.</summary>
    internal async Task<string> ProjectInit(
        [Description("Free-text describing the project goals, context, constraints, and scope.")] string context,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;
        string projectId = DeriveProjectId(store);
        string projectName = ToProjectName(projectId);

        await WritePlanningMemoryAsync(store, "project:context", context, archiveExisting: true, cancellationToken);

        // Detect existing codebase — anything beyond an empty git repo is "existing"
        string storeDir = store.GetStoreDirForScope("local");
        string scriniaDir = Path.GetDirectoryName(storeDir) ?? storeDir;
        string workspaceDir = Path.GetDirectoryName(scriniaDir) ?? scriniaDir;
        bool hasExistingCode = Directory.EnumerateFileSystemEntries(workspaceDir)
            .Any(e => !Path.GetFileName(e).StartsWith('.'));

        string nextStep = hasExistingCode
            ? "scan the existing codebase for concerns (memory('remember', { path: '/goal/G-X/concern/...' })) and capture patterns (remember), then set a goal with memory('remember', { path: '/goal/...' })"
            : "set a goal with memory('remember', { path: '/goal/...' }), then plan requirements";

        await WriteStateAsync(store, projectName, projectId,
            phase: "Initialized",
            progressPct: "0",
            lastAction: "Project initialized",
            blockers: "none",
            nextStep: nextStep,
            cancellationToken);

        // Auto-create onboarder seed task when existing code detected
        if (hasExistingCode)
        {
            try
            {
                string onboarderContent =
                    "## Onboarder Task\n" +
                    "Action: Load the onboarder skill via memory('recall', { path: '/skill/onboarder' }). " +
                    "Explore the existing codebase structure, conventions, and patterns. " +
                    "Store findings as project knowledge via memory('remember').\n" +
                    "Acceptance criteria:\n" +
                    "- Codebase structure documented\n" +
                    "- Key patterns and conventions stored";
                await WritePlanningMemoryAsync(store, "task:init-0-onboarder", onboarderContent,
                    archiveExisting: false,
                    keywords: ["status:pending", "wave:0", "phase:init", "tag:onboarder"],
                    cancellationToken);
            }
            catch { /* best-effort */ }
        }

        // Scaffold merge infrastructure
        ScaffoldMergeInfrastructure(scriniaDir);

        // Create meta-entity directories
        string initBaseDir = GetScriniaBaseDir(store);
        Directory.CreateDirectory(Path.Combine(initBaseDir, "workflows"));
        Directory.CreateDirectory(Path.Combine(initBaseDir, "skills"));
        Directory.CreateDirectory(Path.Combine(initBaseDir, "agent"));

        string responseContent = $"Initialized project '{projectId}'. Stored: project:context, project:state. " +
               $"Created workflows/, skills/, agent/ directories. " +
               $"Files in .scrinia/ were updated — these are your changes.\n" +
               "Merge infrastructure created in .scrinia/hooks/. " +
               "Configure the merge driver: git config merge.scrinia-meta.driver " +
               "'.scrinia/hooks/scrinia-merge-meta.sh %O %A %B'";

        if (hasExistingCode)
            responseContent += "\n\nExisting codebase detected. Onboarder task created.";

        string instruction = hasExistingCode
            ? "call task('next') to start the onboarder. After onboarding completes, suggest memory('remember', { path: '/goal/...' }) to the user to set a goal."
            : "ask the user what to work on, then call memory('remember', { path: '/goal/...' }) to set a goal.";

        return ResponseBuilder.Success(responseContent)
            .WithPath($"project:{projectId}")
            .WithAction("created")
            .WithInstruction(instruction)
            .ToYaml();
    }

    /// <summary>Store project requirements with category grouping and REQ-IDs.</summary>
    internal async Task<string> PlanRequirements(
        [Description("Free-text requirements organized by category with REQ-IDs and v1/v2 scope labels (e.g. '## v1 Requirements\\n### Auth\\n- AUTH-01: Login via email').")] string requirements,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Verify project_init was called first
        try
        {
            await ReadMemoryAsync(store, "project:context", cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return ResponseBuilder.Error("No project initialized. Run project_init first.").ToYaml();
        }

        await WritePlanningMemoryAsync(store, "project:requirements", requirements, archiveExisting: true, cancellationToken);

        // Update state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string phase = ExtractStateField(stateText, "Phase:") ?? "Not started";

        await WriteStateAsync(store, projectName, projectId,
            phase: phase,
            progressPct: "10",
            lastAction: "Requirements defined",
            blockers: "none",
            nextStep: "review requirements with the user before starting execution",
            cancellationToken);

        return ResponseBuilder.Success("Stored: project:requirements. Files in .scrinia/ were updated — these are your changes.")
            .WithPath("project:requirements")
            .WithAction("created")
            .WithInstruction("review these requirements with the user:\n- Are all requirements captured? Anything missing?\n- Are the REQ-IDs scoped correctly (too broad? too narrow?)?\n- Are priorities clear — what's essential vs. nice-to-have?\nOnce confirmed, call memory('remember', { path: '/goal/...' }) to start execution.")
            .ToYaml();
    }


    /// <summary>Resolve a requirement by marking it fulfilled in project:requirements.</summary>
    internal async Task<string> RequirementResolve(
        string id,
        string evidence,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var store = CurrentStore;
            string reqText = await ReadMemoryAsync(store, "project:requirements", cancellationToken);
            string marker = $"[RESOLVED: {evidence}]";
            string updated = reqText.Replace(id, $"{id} {marker}");
            await WritePlanningMemoryAsync(store, "project:requirements", updated,
                archiveExisting: true, cancellationToken);
            return ResponseBuilder.Success($"Requirement '{id}' resolved: {evidence}. project:requirements updated.")
                .WithPath($"requirement:{id}")
                .WithAction("resolved")
                .ToYaml();
        }
        catch (FileNotFoundException)
        {
            return ResponseBuilder.Error("No requirements found. Call memory('remember', { path: '/requirement/...' }) first.").ToYaml();
        }
    }

    /// <summary>List all requirements from project:requirements.</summary>
    internal async Task<string> RequirementList(CancellationToken cancellationToken = default)
    {
        try
        {
            var store = CurrentStore;
            string reqText = await ReadMemoryAsync(store, "project:requirements", cancellationToken);
            return ResponseBuilder.Success(reqText).WithAction("listed").ToYaml();
        }
        catch (FileNotFoundException)
        {
            return ResponseBuilder.Success("No requirements found. Call memory('remember', { path: '/requirement/...' }) to add requirements.").WithAction("listed").ToYaml();
        }
    }

    /// <summary>Decompose a phase into task memories with keyword-based metadata.</summary>
    internal async Task<string> PlanTasks(
        [Description("Two-digit phase number (e.g. '01').")] string phaseId,
        [Description(
            "Free-text task definitions. Each task section uses this format:\n" +
            "## Task {id}\n" +
            "Depends on: {comma-separated task IDs, or 'none'}\n" +
            "Action: {what to do}\n" +
            "Acceptance criteria:\n" +
            "- criterion 1\n" +
            "- criterion 2\n" +
            "Waves are computed automatically from the dependency graph — no need to specify them.")] string tasks,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(phaseId))
            return ResponseBuilder.Error("phaseId is required.").ToYaml();

        var store = CurrentStore;

        // Parse task sections from free-text input
        var parsedTasks = ParseTaskSections(tasks);
        if (parsedTasks.Count == 0)
            return ResponseBuilder.Error("No tasks found. Provide tasks using '## Task {id}' section headers.").ToYaml();

        // Auto-inject gate tasks
        var allUserTaskIds = parsedTasks.Select(t => t.Id).ToArray();

        // Resolve active goal for task scoping (moved up so we can read its workflow keyword)
        string? activeGoalId = await GetActiveGoalIdAsync(store, cancellationToken);

        // Inject gate tasks from workflow definition (override-aware)
        string goalWorkflowName = ResolveGoalWorkflowName(store, activeGoalId);
        var (workflow, workflowWarning) = await ResolveWorkflowAsync(store, goalWorkflowName, cancellationToken);
        foreach (var gate in workflow.PostPlanActivities.Where(a => !string.Equals(a.Type, "spawner", StringComparison.OrdinalIgnoreCase)))
        {
            var deps = new List<string>();
            foreach (var dep in gate.DependsOn)
            {
                if (dep == "*")
                    deps.AddRange(allUserTaskIds);  // wildcard = all user tasks
                else
                    deps.Add(dep);  // other gate IDs (e.g., "qa-gate")
            }
            parsedTasks.Add(new ParsedTask(
                Id: gate.Id,
                DependsOn: deps.ToArray(),
                Content: gate.Prompt));
        }

        // Compute waves from the dependency graph (topological ordering)
        // Tasks with no dependencies → wave 1. Tasks depending on wave N tasks → wave N+1.
        var taskById = parsedTasks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var computedWaves = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Also build a lookup including phaseId-prefixed IDs (e.g. "01-01" maps to task "01")
        foreach (var t in parsedTasks)
            computedWaves[t.Id] = 0; // sentinel

        bool changed = true;
        int iterations = 0;
        while (changed && iterations < 100) // safety limit
        {
            changed = false;
            iterations++;
            foreach (var task in parsedTasks)
            {
                int maxDepWave = 0;
                foreach (string dep in task.DependsOn)
                {
                    // Dependency might be "01-1-01" (full) or "01" (short ID)
                    // Try exact match first, then suffix match
                    string depId = dep;
                    if (!computedWaves.ContainsKey(depId))
                    {
                        // Try extracting just the last segment (task ID portion)
                        string[] parts = dep.Split('-');
                        depId = parts[^1];
                    }

                    if (computedWaves.TryGetValue(depId, out int depWave))
                        maxDepWave = Math.Max(maxDepWave, depWave);
                    else
                        maxDepWave = Math.Max(maxDepWave, 1); // unknown dep → assume wave 1
                }

                int newWave = maxDepWave + 1;
                if (computedWaves[task.Id] != newWave)
                {
                    computedWaves[task.Id] = newWave;
                    changed = true;
                }
            }
        }

        int waveCount = computedWaves.Values.DefaultIfEmpty(1).Max();
        var createdNames = new List<string>();

        // Compute goal prefix once before the loop
        string goalPrefix = "";
        if (activeGoalId is not null)
        {
            var m = GoalIdPattern.Match(activeGoalId);
            if (m.Success) goalPrefix = $"g{m.Groups[1].Value}-";
        }

        foreach (var task in parsedTasks)
        {
            int wave = computedWaves[task.Id];

            // Build keywords: status:pending, wave:N, phase:XX, goal:GN, depends_on:* entries
            var keywords = new List<string>
            {
                "status:pending",
                $"wave:{wave}",
                $"phase:{phaseId}"
            };
            if (activeGoalId is not null)
                keywords.Add($"goal:{activeGoalId}");
            foreach (string dep in task.DependsOn)
            {
                // Store full task name as dependency for reliable resolution.
                // If dep is a raw ID (e.g., "01"), resolve to full name "{goalPrefix}phaseId-wave-id".
                // If dep is already a full name (e.g., "g1-01-1-01"), pass through as-is.
                if (computedWaves.TryGetValue(dep, out int depWave))
                    keywords.Add($"depends_on:{goalPrefix}{phaseId}-{depWave}-{dep}");
                else
                    keywords.Add($"depends_on:{dep}"); // already full name or external ref
            }
            if (task.Files is { Length: > 0 })
            {
                foreach (string file in task.Files)
                    keywords.Add($"files:{file.Trim()}");
            }
            // Add tag: keyword from workflow activity definition, or infer from -gate suffix
            var matchingActivity = workflow.Activities.FirstOrDefault(a => string.Equals(a.Id, task.Id, StringComparison.OrdinalIgnoreCase));
            if (matchingActivity is not null)
                keywords.Add($"tag:{matchingActivity.Tag}");
            else if (task.Id.EndsWith("-gate", StringComparison.OrdinalIgnoreCase))
                keywords.Add($"tag:{task.Id.Replace("-gate", "", StringComparison.OrdinalIgnoreCase)}");

            // Extract REQ-IDs from task content for requirement traceability
            foreach (Match reqMatch in ReqIdPattern.Matches(task.Content))
            {
                string reqKw = $"req:{reqMatch.Groups[1].Value}";
                if (!keywords.Contains(reqKw, StringComparer.OrdinalIgnoreCase))
                    keywords.Add(reqKw);
            }

            // Task naming: task:{goalPrefix}{phaseId}-{wave}-{id}
            string taskName = $"task:{goalPrefix}{phaseId}-{wave}-{task.Id}";

            await WritePlanningMemoryAsync(store, taskName, task.Content,
                archiveExisting: false, keywords: [.. keywords], cancellationToken);

            createdNames.Add(taskName);
        }

        // File-conflict detection: flag same-wave tasks that modify the same file
        var fileConflicts = new List<string>();
        var tasksByWave = parsedTasks
            .Where(t => t.Files is { Length: > 0 })
            .GroupBy(t => computedWaves[t.Id]);

        foreach (var waveGroup in tasksByWave)
        {
            var fileToTasks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var task in waveGroup)
            {
                foreach (var file in task.Files!)
                {
                    var trimmed = file.Trim();
                    if (!fileToTasks.ContainsKey(trimmed))
                        fileToTasks[trimmed] = new();
                    fileToTasks[trimmed].Add(task.Id);
                }
            }
            foreach (var (file, conflictTasks) in fileToTasks.Where(kv => kv.Value.Count > 1))
            {
                fileConflicts.Add($"Wave {waveGroup.Key}: {file} modified by tasks {string.Join(", ", conflictTasks)}. Use worktree isolation or re-sequence.");
            }
        }

        // Update project:state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? "Not started";

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: "30",
            lastAction: $"Tasks created for phase {phaseId} ({parsedTasks.Count} tasks, {waveCount} wave(s))",
            blockers: "none",
            nextStep: $"run task('next') to get first task for phase {phaseId}",
            cancellationToken);

        // Optionally surface learn:patterns as a hint for the task planner
        string patternNote = "";
        try
        {
            string patterns = await ReadMemoryAsync(store, "learn:patterns", cancellationToken);
            string hint = patterns.Length > 300 ? patterns[..300] + "..." : patterns;
            patternNote = $"\nPatterns from prior phases: {hint}";
        }
        catch { /* no learn:patterns yet — skip silently */ }

        int firstWaveCount = computedWaves.Values.Count(w => w == 1);
        string parallelHint = firstWaveCount > 1
            ? $" First wave has {firstWaveCount} independent tasks — spawn parallel agents, one per task."
            : "";

        // Check if agent:execution-policy exists — file first, NMP/2 fallback
        bool hasPolicy = false;
        string epBaseDir = GetScriniaBaseDir(store);
        string policyPath = Path.Combine(epBaseDir, "agent", "execution-policy.md");
        hasPolicy = File.Exists(policyPath);
        if (!hasPolicy)
        {
            // Fall back to NMP/2 check (legacy)
            try
            {
                var (epScope, _) = store.ParseQualifiedName("agent:execution-policy");
                var epEntries = store.LoadIndex(epScope);
                hasPolicy = epEntries.Any(e => e.Name == "execution-policy");
            }
            catch { }
        }

        string taskList = string.Join("\n", createdNames.Select(n => $"  - {n}"));
        string responseContent =
            $"Created {parsedTasks.Count} task(s) for phase {phaseId} in {waveCount} wave(s).\n" +
            $"Tasks stored:\n{taskList}\n" +
            $"Files in .scrinia/ were updated — these are your changes.";
        if (!string.IsNullOrEmpty(patternNote))
            responseContent += patternNote;

        string instruction = $"call task('next') to get the first pending tasks.{parallelHint} Spawn agents for all task execution — the primary agent orchestrates, it does not execute tasks directly.";

        var infoItems = new List<string>();
        if (hasPolicy)
            infoItems.Add("Agent execution policy available — show('agent:execution-policy') for spawn requirements.");

        var warningItems = new List<string>();
        if (workflowWarning is not null)
            warningItems.Add(workflowWarning);
        if (fileConflicts.Count > 0)
            warningItems.AddRange(fileConflicts);

        var ptResponse = ResponseBuilder.Success(responseContent)
            .WithAction("created")
            .WithInstruction(instruction);
        if (warningItems.Count > 0)
            ptResponse = ptResponse.WithActionNeeded([.. warningItems]);
        if (infoItems.Count > 0)
            ptResponse = ptResponse.WithInfo([.. infoItems]);

        return ptResponse.ToYaml();
    }

    /// <summary>Resume full agent context after context loss or session start. Delegates to ScriniaMcpTools.Restore.</summary>
    internal Task<string> ContextResume(CancellationToken cancellationToken = default)
        => new ScriniaMcpTools().Restore(cancellationToken);

    /// <summary>Query current project status.</summary>
    internal async Task<string> PlanStatus(CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        string stateText;
        try
        {
            stateText = await ReadMemoryAsync(store, "project:state", cancellationToken);
        }
        catch (FileNotFoundException)
        {
            string? rebuilt = await RebuildStateFromMemoriesAsync(store, cancellationToken);
            if (rebuilt is null)
                return ResponseBuilder.Error("No project found. Run project_init first.").ToYaml();
            stateText = rebuilt;
        }

        // Build compact status report from state fields
        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown";
        string phase = ExtractStateField(stateText, "Phase:") ?? "Unknown";
        string blockers = ExtractStateField(stateText, "Blockers:") ?? "none";
        string next = ExtractStateField(stateText, "Next:") ?? "(not set)";
        string lastAction = ExtractStateField(stateText, "Last action:") ?? "(not set)";

        // Compute progress from task data (scoped to active goal)
        string? statusGoalId = await GetActiveGoalIdAsync(store, cancellationToken);
        string progress = CalculateProgress(store, statusGoalId) + "%";

        // Optionally enrich with active concern count (keyword-only scan, no artifact decoding)
        string concernNote = "";
        try
        {
            var (cs, _) = store.ParseQualifiedName("concern:placeholder");
            var entries = store.LoadIndex(cs);
            int activeCount = entries.Count(e => HasKeyword(e, "status:active"));
            if (activeCount > 0)
            {
                int highCount = entries.Count(e =>
                    HasKeyword(e, "status:active") &&
                    HasKeyword(e, "severity:high"));
                concernNote = highCount > 0
                    ? $"\nConcerns: {activeCount} active ({highCount} high-severity)"
                    : $"\nConcerns: {activeCount} active";
            }
        }
        catch { /* concern scope not yet created — skip silently */ }

        // Optionally enrich with goal delta (GOAL-03): original count vs current
        string goalNote = "";
        try
        {
            string contextText = await ReadMemoryAsync(store, "project:context", cancellationToken);
            var (goals, originalCount, _) = ParseGoalsSection(contextText);
            int totalGoals = goals.Count;
            if (totalGoals > 0)
            {
                int addedCount = originalCount >= 0 ? totalGoals - originalCount : 0;
                goalNote = addedCount > 0
                    ? $"\nGoals: {totalGoals} ({originalCount} original + {addedCount} added)"
                    : $"\nGoals: {totalGoals}";
            }
        }
        catch { /* no project:context or no goals section — skip silently */ }

        // Check if all goals are complete (no active goals)
        bool hasActiveGoal = false;
        try
        {
            string contextText2 = await ReadMemoryAsync(store, "project:context", cancellationToken);
            var (goals2, _, _) = ParseGoalsSection(contextText2);
            hasActiveGoal = goals2.Any(g => g.Contains("[active]", StringComparison.OrdinalIgnoreCase));
        }
        catch { /* no project:context or no goals section — skip silently */ }

        string? idleInstruction = null;
        if (!hasActiveGoal && progress == "100%")
            idleInstruction = "no active goal — ask the user what to work on next, then call memory('remember', { path: '/goal/...' }).";
        else if (!hasActiveGoal && progress == "0%")
            idleInstruction = "no active goal — ask the user what to work on, then call memory('remember', { path: '/goal/...' }) to start planning.";

        string statusContent =
            $"Project: {projectName}\n" +
            $"Phase: {phase}\n" +
            $"Progress: {progress}\n" +
            $"Last action: {lastAction}\n" +
            $"Blockers: {blockers}\n" +
            $"Next: {next}" +
            concernNote + goalNote;

        // Staleness & drift alerts — use cache if available, fall back to live scan
        int psStale, psReview, psDrift, psMissing;
        string psCacheNote = "";
        if (MaintenanceCache.TryReadCache(store, out var psCached) && psCached is not null)
        {
            psStale = psCached.StaleCount;
            psReview = psCached.ReviewCount;
            psDrift = psCached.DriftCount;
            psMissing = psCached.MissingCount;
            int cacheAge = (int)(DateTimeOffset.UtcNow - psCached.ComputedAt).TotalMinutes;
            psCacheNote = $" (cached {cacheAge} min ago)";
        }
        else
        {
            (psStale, psReview) = ScanStaleness(store);
            (psDrift, psMissing) = ScanDrift(store);
        }

        var psWarnings = new List<string>();
        if (psStale > 0) psWarnings.Add($"{psStale} memory(s) have passed their review date — verify content is still accurate.{psCacheNote}");
        if (psDrift > 0) psWarnings.Add($"{psDrift} code reference(s) have drifted (files changed since stored) — re-link or update.{psCacheNote}");
        if (psMissing > 0) psWarnings.Add($"{psMissing} code reference(s) point to missing files — unlink or correct.{psCacheNote}");

        var psInfoItems = new List<string>();
        if (psReview > 0) psInfoItems.Add($"{psReview} memory(s) have review conditions set.{psCacheNote}");

        var psResponse = ResponseBuilder.Success(statusContent)
            .WithPath("project:state")
            .WithAction("shown");
        if (idleInstruction is not null)
            psResponse = psResponse.WithInstruction(idleInstruction);
        if (psWarnings.Count > 0)
            psResponse = psResponse.WithActionNeeded([.. psWarnings]);
        if (psInfoItems.Count > 0)
            psResponse = psResponse.WithInfo([.. psInfoItems]);

        return psResponse.ToYaml();
    }

    /// <summary>Thin dispatcher for task operations — delegates to TaskNext/TaskComplete.</summary>
    [McpServerTool(Name = "task"), Description(
        "Task operations. Actions: 'next' (get next pending task), 'complete' (mark task done), 'plan' (decompose phase into tasks).")]
    public async Task<string> TaskDispatch(
        [Description("Action: 'next', 'complete', or 'plan'.")] string action,
        [Description("Phase ID (next — optional, auto-detects if omitted; plan — required).")] string? phaseId = null,
        [Description("Task name to complete (complete).")] string? taskName = null,
        [Description("Outcome description (complete).")] string? outcome = null,
        [Description("Free-text task definitions (plan).")] string? tasks = null,
        CancellationToken cancellationToken = default)
    {
        string act = action.Trim().ToLowerInvariant();
        switch (act)
        {
            case "next":
                if (string.IsNullOrWhiteSpace(phaseId))
                {
                    // Auto-detect phase from active goal's pending tasks
                    try
                    {
                        var store = CurrentStore;
                        string? activeGoalId = await GetActiveGoalIdAsync(store, cancellationToken);
                        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
                        var allEntries = store.LoadIndex(taskScope);
                        var pendingEntries = allEntries
                            .Where(e => HasKeyword(e, "status:pending"))
                            .Where(e => activeGoalId is null || HasKeyword(e, $"goal:{activeGoalId}"));

                        // Find first phase with pending tasks
                        foreach (var entry in pendingEntries)
                        {
                            var phaseKw = entry.Keywords?.FirstOrDefault(k =>
                                k.StartsWith("phase:", StringComparison.OrdinalIgnoreCase));
                            if (phaseKw is not null)
                            {
                                phaseId = phaseKw["phase:".Length..];
                                break;
                            }
                        }

                        if (string.IsNullOrWhiteSpace(phaseId))
                            return ResponseBuilder.Success("No pending tasks found for the active goal.").WithAction("listed").ToYaml();
                    }
                    catch { return ResponseBuilder.Error("Could not auto-detect phase. Provide phaseId parameter.").ToYaml(); }
                }
                return await TaskNext(phaseId, cancellationToken);

            case "complete":
                if (string.IsNullOrWhiteSpace(taskName))
                    return ResponseBuilder.Error("task('complete') requires 'taskName' parameter.").ToYaml();
                if (string.IsNullOrWhiteSpace(outcome))
                    return ResponseBuilder.Error("task('complete') requires 'outcome' parameter.").ToYaml();
                return await TaskComplete(taskName, outcome, cancellationToken);

            case "plan":
                if (string.IsNullOrWhiteSpace(phaseId))
                    return ResponseBuilder.Error("task('plan') requires 'phaseId' parameter.").ToYaml();
                if (string.IsNullOrWhiteSpace(tasks))
                    return ResponseBuilder.Error("task('plan') requires 'tasks' parameter.").ToYaml();
                return await PlanTasks(phaseId, tasks, cancellationToken);

            default:
                return ResponseBuilder.Error($"Unknown action '{action}'. Valid actions: 'next', 'complete', 'plan'.").ToYaml();
        }
    }

    /// <summary>Returns all unblocked tasks in the current wave for a phase.</summary>
    internal async Task<string> TaskNext(
        [Description("Two-digit phase number (e.g. '01').")] string phaseId,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Get task scope via ParseQualifiedName — "local-topic:task" scope
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");

        // Keyword-only scan — no ResolveArtifactAsync during filtering
        var allEntries = store.LoadIndex(taskScope);

        // Resolve active goal to scope tasks
        string? activeGoalId = await GetActiveGoalIdAsync(store, cancellationToken);

        // Filter to tasks for this phase, scoped to active goal if one exists
        var phaseEntries = allEntries
            .Where(e => HasKeyword(e, $"phase:{phaseId}"))
            .Where(e => activeGoalId is null || HasKeyword(e, $"goal:{activeGoalId}"))
            .ToList();

        if (phaseEntries.Count == 0)
            return ResponseBuilder.Success($"No pending tasks for phase {phaseId}.").WithAction("listed").ToYaml();

        // Find pending entries
        var pendingEntries = phaseEntries
            .Where(e => HasKeyword(e, "status:pending"))
            .ToList();

        if (pendingEntries.Count == 0)
            return ResponseBuilder.Success($"No pending tasks for phase {phaseId}.").WithAction("listed").ToYaml();

        // Find the lowest wave among pending entries
        int currentWave = pendingEntries.Min(e => ParseWave(e));

        // Filter pending to current wave only
        var currentWaveEntries = pendingEntries
            .Where(e => ParseWave(e) == currentWave)
            .ToList();

        // Build set of completed task names for dependency checking
        var completedNames = allEntries
            .Where(e => HasKeyword(e, "status:complete"))
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Filter to unblocked: all depends_on names must be in completedNames
        var unblockedEntries = currentWaveEntries
            .Where(e => GetDependencies(e).All(dep => completedNames.Contains(dep)))
            .ToList();

        if (unblockedEntries.Count == 0)
            return ResponseBuilder.Success($"No unblocked tasks for phase {phaseId} in wave {currentWave}. Some tasks may be waiting on dependencies.").WithAction("listed").ToYaml();

        // Build response: read artifact content only for unblocked tasks
        string tnInstruction = unblockedEntries.Count > 1
            ? $"these {unblockedEntries.Count} tasks are independent — spawn a parallel agent for each task."
            : "spawn an agent for this task — keep the primary agent available for SOS and user interaction.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Phase {phaseId} — Wave {currentWave} — {unblockedEntries.Count} unblocked task(s):");
        sb.AppendLine();

        foreach (var entry in unblockedEntries)
        {
            string qualifiedName = $"task:{entry.Name}";
            string content;
            try
            {
                content = await ReadMemoryAsync(store, qualifiedName, cancellationToken);
            }
            catch (FileNotFoundException)
            {
                content = "(content not found)";
            }

            sb.AppendLine($"## {qualifiedName}");
            sb.AppendLine(content);
            sb.AppendLine();

            // Truncate early if getting close to limit
            if (sb.Length > MaxResponseChars - 200)
            {
                sb.AppendLine("[... truncated to 8KB limit]");
                break;
            }
        }

        // Update project state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? $"Phase {phaseId}";

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: CalculateProgress(store, activeGoalId),
            lastAction: $"task('next') called for phase {phaseId} wave {currentWave}",
            blockers: "none",
            nextStep: unblockedEntries.Count > 1
                ? $"spawn {unblockedEntries.Count} parallel agents for wave {currentWave} tasks, then call task('complete') for each"
                : $"spawn agent for wave {currentWave} task, then call task('complete')",
            cancellationToken);

        return ResponseBuilder.Success(sb.ToString().TrimEnd())
            .WithAction("listed")
            .WithInstruction(tnInstruction)
            .ToYaml();
    }

    /// <summary>Verify a phase achieved its goal using acceptance criteria from requirements.</summary>
    internal async Task<string> PlanVerify(
        string phaseId,
        string? evidence = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Discover REQ-IDs referenced by tasks in this phase
        string? verifyGoalId0 = await GetActiveGoalIdAsync(store, cancellationToken);
        var (taskScope0, _) = store.ParseQualifiedName("task:placeholder");
        var allEntries0 = store.LoadIndex(taskScope0);
        var phaseReqIds = allEntries0
            .Where(e => HasKeyword(e, $"phase:{phaseId}"))
            .Where(e => verifyGoalId0 is null || HasKeyword(e, $"goal:{verifyGoalId0}"))
            .SelectMany(e => e.Keywords ?? [])
            .Where(k => k.StartsWith("req:", StringComparison.OrdinalIgnoreCase))
            .Select(k => k["req:".Length..])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Read requirements and extract matching entries as criteria
        string requirementsText;
        try { requirementsText = await ReadMemoryAsync(store, "project:requirements", cancellationToken); }
        catch (FileNotFoundException)
        {
            return ResponseBuilder.Error("No requirements found. Call memory('remember', { path: '/requirement/...' }) first.").ToYaml();
        }

        var criteria = ExtractRequirementCriteria(requirementsText, phaseReqIds);
        if (criteria.Count == 0)
            return ResponseBuilder.Success($"No requirements found for phase {phaseId}. Ensure tasks reference REQ-IDs in their content.").WithAction("listed").ToYaml();

        // Load task summary for context (scoped to active goal)
        string? verifyGoalId = verifyGoalId0;
        var phaseEntries = allEntries0
            .Where(e => HasKeyword(e, $"phase:{phaseId}"))
            .Where(e => verifyGoalId is null || HasKeyword(e, $"goal:{verifyGoalId}"))
            .ToList();
        int totalTasks = phaseEntries.Count;
        int completeTasks = phaseEntries.Count(e => HasKeyword(e, "status:complete"));

        // Try to surface the hypothesis from research for this phase
        string? hypothesisText = null;
        try
        {
            var (resScope, _) = store.ParseQualifiedName("research:placeholder");
            var resEntries = store.LoadIndex(resScope);
            foreach (var re in resEntries.Where(e => HasKeyword(e, $"phase:{phaseId}")))
            {
                string resContent = await ReadMemoryAsync(store, $"research:{re.Name}", cancellationToken);
                int hypIdx = resContent.IndexOf("## Hypothesis", StringComparison.OrdinalIgnoreCase);
                if (hypIdx >= 0)
                {
                    string hypSection = resContent[(hypIdx + "## Hypothesis".Length)..];
                    int nextSection = hypSection.IndexOf("\n## ", StringComparison.Ordinal);
                    hypothesisText = (nextSection >= 0 ? hypSection[..nextSection] : hypSection).Trim();
                    break;
                }
            }
        }
        catch { /* no research or no hypothesis — skip */ }

        // ── Checklist mode (no evidence provided) ──
        if (string.IsNullOrWhiteSpace(evidence))
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"## Phase {phaseId} — Verification Checklist");
            sb.AppendLine();
            sb.AppendLine($"Tasks: {completeTasks}/{totalTasks} complete");
            sb.AppendLine();

            if (hypothesisText is not null)
            {
                sb.AppendLine($"**Hypothesis from research:** {hypothesisText}");
                sb.AppendLine("Does the evidence support this hypothesis, or did reality diverge?");
                sb.AppendLine();
            }

            sb.AppendLine("Verify each criterion yourself (run tests, review code, confirm behavior).");
            sb.AppendLine("The QA gate task (auto-injected) will handle structured verification for this phase.");
            sb.AppendLine();

            for (int i = 0; i < criteria.Count; i++)
                sb.AppendLine($"{i + 1}. [ ] {criteria[i]}");

            return ResponseBuilder.Success(sb.ToString().TrimEnd()).WithAction("checklist").ToYaml();
        }

        // ── Recording mode (evidence provided) ──

        // Concern gate — reject if open high/medium concerns for this phase
        try
        {
            var (concernScope, _) = store.ParseQualifiedName("concern:placeholder");
            var concernEntries = store.LoadIndex(concernScope);
            var openConcerns = concernEntries
                .Where(e => e.Keywords is not null &&
                    e.Keywords.Any(k => k.Equals($"phase:{phaseId}", StringComparison.OrdinalIgnoreCase) ||
                                        k.Equals("phase:all", StringComparison.OrdinalIgnoreCase)) &&
                    e.Keywords.Any(k => k.Equals("status:active", StringComparison.OrdinalIgnoreCase)) &&
                    e.Keywords.Any(k => k.StartsWith("severity:high", StringComparison.OrdinalIgnoreCase) ||
                                        k.StartsWith("severity:medium", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (openConcerns.Count > 0)
            {
                var names = string.Join(", ", openConcerns.Select(e => e.Name));
                return ResponseBuilder.Error($"{openConcerns.Count} open high/medium concern(s) for phase {phaseId}: {names}.")
                    .WithInstruction("Resolve them (memory('transition', { path: '/concern/...', to: 'resolved' }) with verifiedBy) before verification.")
                    .ToYaml();
            }
        }
        catch { /* best-effort — don't block if concern scope doesn't exist */ }

        var sb2 = new System.Text.StringBuilder();
        sb2.AppendLine($"## Phase Verification: {phaseId}");

        // Parse evidence lines — match PASS:/FAIL: prefixes to criteria in order
        var evidenceLines = evidence.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("PASS:", StringComparison.OrdinalIgnoreCase)
                     || l.StartsWith("FAIL:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        int passCount = 0;
        var criterionResults = new List<(bool pass, string criterion, string evidenceText)>();

        for (int i = 0; i < criteria.Count; i++)
        {
            if (i < evidenceLines.Count)
            {
                string line = evidenceLines[i];
                bool pass = line.StartsWith("PASS:", StringComparison.OrdinalIgnoreCase);
                string evidenceText = line[(line.IndexOf(':') + 1)..].Trim();
                criterionResults.Add((pass, criteria[i], evidenceText));
                if (pass) passCount++;
            }
            else
            {
                criterionResults.Add((false, criteria[i], "No evidence provided"));
            }
        }

        // Overall status
        string status = passCount == criteria.Count
            ? "ALL_PASS"
            : passCount == 0
                ? "ALL_FAIL"
                : $"PARTIAL ({passCount}/{criteria.Count} passed)";

        sb2.AppendLine($"Status: {status}");
        sb2.AppendLine();

        foreach (var (pass, criterion, evidenceText) in criterionResults)
        {
            sb2.AppendLine($"{(pass ? "PASS" : "FAIL")}: {criterion}");
            sb2.AppendLine($"  Evidence: {evidenceText}");
            sb2.AppendLine();
        }

        // Append verification record to execution log for auditability
        string verifyLog = $"[{DateTimeOffset.UtcNow:o}] VERIFY phase {phaseId}: {status}\n" +
            string.Join("\n", criterionResults.Select(r => $"  {(r.pass ? "PASS" : "FAIL")}: {r.criterion} — {r.evidenceText}"));
        await AppendToExecutionLogAsync(store, $"task:{phaseId}-execution-log", verifyLog, cancellationToken);

        // Update project:state with verification results
        string stateText2;
        try { stateText2 = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText2 = ""; }

        string projectName2 = ExtractStateField(stateText2, "Project:") ?? "Unknown Project";
        string projectId2 = ExtractStateField(stateText2, "ID:") ?? DeriveProjectId(store);
        string currentPhase2 = ExtractStateField(stateText2, "Phase:") ?? $"Phase {phaseId}";
        string progressPct2 = CalculateProgress(store, verifyGoalId);

        // Build next step guidance based on verification result
        string verifyNextStep;
        if (passCount < criteria.Count)
        {
            verifyNextStep = "call task('plan') with gap closure tasks";
        }
        else
        {
            // Check for active concerns scoped to this phase
            bool hasActiveConcerns = false;
            try
            {
                var (cs, _) = store.ParseQualifiedName("concern:placeholder");
                var concernEntries = store.LoadIndex(cs);
                hasActiveConcerns = concernEntries.Any(e =>
                    HasKeyword(e, "status:active") &&
                    (HasKeyword(e, $"phase:{phaseId}") || HasKeyword(e, "phase:all")));
            }
            catch { /* concern scope not yet created — skip silently */ }

            verifyNextStep = hasActiveConcerns
                ? $"resolve addressed concerns (memory('transition', {{ path: '/concern/...', to: 'resolved' }})), then the self-reflector gate task will handle retrospective for phase {phaseId}"
                : $"self-reflector gate task will handle retrospective for phase {phaseId} — proceed to task('next')";
        }

        await WriteStateAsync(store, projectName2, projectId2,
            phase: currentPhase2,
            progressPct: progressPct2,
            lastAction: $"QA verification for phase {phaseId}: {status}",
            blockers: passCount < criteria.Count ? $"{criteria.Count - passCount} criteria failed" : "none",
            nextStep: verifyNextStep,
            cancellationToken);

        var verifyResponse = (passCount == criteria.Count
                ? ResponseBuilder.Success(sb2.ToString().TrimEnd())
                : ResponseBuilder.Warning(sb2.ToString().TrimEnd()))
            .WithAction("verified");
        if (passCount == criteria.Count)
            verifyResponse = verifyResponse.WithInstruction(verifyNextStep);

        return verifyResponse.ToYaml();
    }

    /// <summary>Create gap closure tasks for failed verification criteria and re-open the phase.</summary>
    internal async Task<string> PlanGaps(
        string phaseId,
        string failedCriteria,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Verify project exists
        try { await ReadMemoryAsync(store, "project:context", cancellationToken); }
        catch (FileNotFoundException)
        {
            return ResponseBuilder.Error("No project found. Run project_init first.").ToYaml();
        }

        // Parse failed criteria — split on newlines, trim, filter empty
        var criteria = failedCriteria
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => c.Length > 0)
            .ToList();

        if (criteria.Count == 0)
            return ResponseBuilder.Error("No failed criteria provided. Pass newline-separated criterion texts.").ToYaml();

        // Create a gap task for each failed criterion (scoped to active goal)
        string? gapGoalId = await GetActiveGoalIdAsync(store, cancellationToken);
        var createdNames = new List<string>();
        for (int i = 0; i < criteria.Count; i++)
        {
            string criterion = criteria[i];
            string gapTaskName = $"task:{phaseId}-gap-{(i + 1):D2}";
            var gapKeywordsList = new List<string> { "status:pending", "wave:1", $"phase:{phaseId}", "gap_closure:true" };
            if (gapGoalId is not null) gapKeywordsList.Add($"goal:{gapGoalId}");
            string[] gapKeywords = gapKeywordsList.ToArray();
            string content =
                $"Gap closure task for failed criterion:\n{criterion}\n\n" +
                $"Action: Address the failed criterion and ensure it passes on re-verification.";

            await WritePlanningMemoryAsync(store, gapTaskName, content,
                archiveExisting: false, keywords: gapKeywords, cancellationToken);

            createdNames.Add(gapTaskName);
        }

        // Re-open phase in project:state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string progressPct = CalculateProgress(store, gapGoalId);

        await WriteStateAsync(store, projectName, projectId,
            phase: $"Phase {phaseId} (re-opened for gap closure)",
            progressPct: progressPct,
            lastAction: $"Gap closure: {criteria.Count} task(s) created for phase {phaseId}",
            blockers: "none",
            nextStep: "call task('next') to work on gap tasks",
            cancellationToken);

        string taskList = string.Join("\n", createdNames.Select(n => $"  - {n}"));
        return ResponseBuilder.Success($"Created {criteria.Count} gap closure task(s) for phase {phaseId}. Phase re-opened.\nGap tasks created:\n{taskList}")
            .WithAction("created")
            .WithInstruction("call task('next') to begin.")
            .ToYaml();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to reconstruct project state from available memories when project:state is missing.
    /// Returns rebuilt state text prefixed with "[State rebuilt from memories]", or null if no
    /// project memories exist at all.
    /// </summary>
    internal static async Task<string?> RebuildStateFromMemoriesAsync(
        IMemoryStore store, CancellationToken ct)
    {
        // Step 1: Try project:context for project name
        string? projectName = null;
        try
        {
            string contextText = await ReadMemoryAsync(store, "project:context", ct);
            // Extract first meaningful line or first 100 chars as project name indicator
            string firstLine = contextText.Split('\n')[0].Trim();
            projectName = firstLine.Length > 100 ? firstLine[..100] : firstLine;
        }
        catch (FileNotFoundException) { /* no context */ }

        if (projectName is null)
            return null; // No project memories at all

        // Step 2: Check for requirements and compute progress from tasks
        string phase = "Not started";
        string? rebuildGoalId = await GetActiveGoalIdAsync(store, ct);
        string progressPct = CalculateProgress(store, rebuildGoalId);
        try
        {
            await ReadMemoryAsync(store, "project:requirements", ct);
            phase = "Requirements defined";
        }
        catch (FileNotFoundException) { /* no requirements yet */ }

        // Step 3: Derive project ID
        string projectId = DeriveProjectId(store);
        string projectDisplayName = ToProjectName(projectId);

        // Step 4: Write the rebuilt state for future calls (avoids repeated rebuilds)
        string rebuiltNote = "[State rebuilt from memories]\n";
        string nextStep = phase == "Requirements defined"
            ? "set a goal with memory('remember', { path: '/goal/...' }) to start planning"
            : "run memory('remember', { path: '/requirement/...' }) to define project requirements";

        await WriteStateAsync(store, projectDisplayName, projectId,
            phase: phase,
            progressPct: progressPct,
            lastAction: "State rebuilt from memories",
            blockers: "none",
            nextStep: nextStep,
            ct);

        string timestamp = DateTimeOffset.UtcNow.ToString("o");
        return rebuiltNote +
               $"Project: {projectDisplayName}\n" +
               $"ID: {projectId}\n" +
               $"Phase: {phase}\n" +
               $"Progress: {progressPct}%\n" +
               $"Last action: State rebuilt from memories ({timestamp})\n" +
               $"Blockers: none\n" +
               $"Next: {nextStep}";
    }

    /// <summary>
    /// Derives a project ID from the workspace directory name.
    /// Walks up two levels from the store dir to reach workspace root.
    /// </summary>
    private static string DeriveProjectId(IMemoryStore store)
    {
        string storeDir = store.GetStoreDirForScope("local");
        // storeDir is typically {workspaceRoot}/.scrinia/store
        string scriniaDir = Path.GetDirectoryName(storeDir) ?? storeDir;
        string workspaceDir = Path.GetDirectoryName(scriniaDir) ?? scriniaDir;
        string dirName = Path.GetFileName(workspaceDir);
        return store.SanitizeName(dirName);
    }

    /// <summary>Converts a sanitized project ID to a display name.</summary>
    private static string ToProjectName(string projectId) =>
        projectId.Replace('-', ' ').Replace('_', ' ');

    /// <summary>
    /// Reads and decodes a named memory artifact.
    /// Throws FileNotFoundException if the memory does not exist.
    /// </summary>
    internal static async Task<string> ReadMemoryAsync(
        IMemoryStore store, string qualifiedName, CancellationToken ct)
    {
        string artifact = await store.ResolveArtifactAsync(qualifiedName, ct);
        byte[] decoded = Nmp2Strategy.Instance.Decode(artifact);
        return System.Text.Encoding.UTF8.GetString(decoded);
    }

    /// <summary>
    /// Resolves a workflow by name with override precedence:
    /// 1. Disk file (.scrinia/workflows/{name}.json)
    /// 2. NMP/2 memory (workflow:{name}) — legacy fallback
    /// 3. Built-in default (QuickFixWorkflow or DefaultGoalWorkflow)
    /// Corrupted overrides fall back with a warning.
    /// </summary>
    private static async Task<(WorkflowDefinition Workflow, string? Warning)> ResolveWorkflowAsync(
        IMemoryStore store, string workflowName, CancellationToken ct)
    {
        // 1a. Disk file — YAML
        string baseDir = GetScriniaBaseDir(store);
        foreach (var ext in new[] { ".yaml", ".yml" })
        {
            string yamlPath = Path.Combine(baseDir, "workflows", $"{workflowName}{ext}");
            if (File.Exists(yamlPath))
            {
                try
                {
                    string yamlContent = await File.ReadAllTextAsync(yamlPath, ct);
                    // AOT pipeline: YAML → object → JSON string → source-gen deserialize
                    var yamlDeserializer = new DeserializerBuilder().Build();
                    var yamlObj = yamlDeserializer.Deserialize<object>(yamlContent);
                    var jsonSerializer = new SerializerBuilder()
                        .JsonCompatible()
                        .Build();
                    string jsonString = jsonSerializer.Serialize(yamlObj);
                    var parsed = JsonSerializer.Deserialize(jsonString,
                        PlanningJsonContext.Default.WorkflowDefinition);
                    if (parsed is not null) return (parsed, null);
                }
                catch (Exception ex)
                {
                    return (WorkflowDefinition.DefaultGoalWorkflow,
                        $"\u26a0 ACTION NEEDED: YAML workflow '{workflowName}{ext}' could not be parsed: {ex.Message}");
                }
            }
        }

        // 1b. Disk file — JSON
        try
        {
            string filePath = Path.Combine(baseDir, "workflows", $"{workflowName}.json");
            if (File.Exists(filePath))
            {
                string json = await File.ReadAllTextAsync(filePath, ct);
                var parsed = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.WorkflowDefinition);
                if (parsed is not null) return (parsed, null);
            }
        }
        catch (JsonException)
        {
            var fallback = workflowName.Equals("quick-fix", StringComparison.OrdinalIgnoreCase)
                ? WorkflowDefinition.QuickFixWorkflow
                : WorkflowDefinition.DefaultGoalWorkflow;
            return (fallback,
                "\u26a0 ACTION NEEDED: workflow file could not be parsed \u2014 using built-in default.");
        }
        catch { /* file I/O error — fall through to NMP/2 */ }

        // 2. NMP/2 fallback (legacy)
        try
        {
            string content = await ReadMemoryAsync(store, $"workflow:{workflowName}", ct);
            var parsed = JsonSerializer.Deserialize(content, PlanningJsonContext.Default.WorkflowDefinition);
            if (parsed is not null) return (parsed, null);
        }
        catch (FileNotFoundException) { /* no override stored — fall through to built-ins */ }
        catch
        {
            var fallback = workflowName.Equals("quick-fix", StringComparison.OrdinalIgnoreCase)
                ? WorkflowDefinition.QuickFixWorkflow
                : WorkflowDefinition.DefaultGoalWorkflow;
            return (fallback,
                "\u26a0 ACTION NEEDED: stored workflow override could not be parsed \u2014 using built-in default.");
        }

        // 3. Built-in default
        return workflowName.Equals("quick-fix", StringComparison.OrdinalIgnoreCase)
            ? (WorkflowDefinition.QuickFixWorkflow, null)
            : (WorkflowDefinition.DefaultGoalWorkflow, null);
    }

    /// <summary>
    /// Creates or updates a workflow definition from JSON, with full validation.
    /// </summary>
    private async Task<string> CreateOrUpdateWorkflow(
        string action, string? definition, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(definition))
            return ResponseBuilder.Error($"Workflow '{action}' requires 'definition' parameter with workflow JSON.").ToYaml();

        WorkflowDefinition parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(definition, PlanningJsonContext.Default.WorkflowDefinition)!;
            if (parsed is null)
                return ResponseBuilder.Error("Workflow definition deserialized to null.").ToYaml();
        }
        catch (JsonException)
        {
            // JSON parse failed — try YAML pipeline: YAML → object → JSON string → source-gen deserialize
            try
            {
                var yamlDeserializer = new DeserializerBuilder().Build();
                var yamlObj = yamlDeserializer.Deserialize<object>(definition);
                var jsonSerializer = new SerializerBuilder()
                    .JsonCompatible()
                    .Build();
                string jsonFromYaml = jsonSerializer.Serialize(yamlObj);
                parsed = JsonSerializer.Deserialize(jsonFromYaml, PlanningJsonContext.Default.WorkflowDefinition)!;
                if (parsed is null)
                    return ResponseBuilder.Error("Workflow definition deserialized to null (from YAML).").ToYaml();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.Error($"Failed to parse workflow definition as JSON or YAML — {ex.Message}").ToYaml();
            }
        }

        var errors = WorkflowDefinition.Validate(parsed);
        if (errors.Count > 0)
            return ResponseBuilder.Error($"Workflow validation failed:\n- {string.Join("\n- ", errors)}").ToYaml();

        // Compute basedOn hash from the built-in default workflow
        string defaultJson = JsonSerializer.Serialize(
            WorkflowDefinition.DefaultGoalWorkflow, PlanningJsonContext.Default.WorkflowDefinition);
        string basedOnHash = Convert.ToHexStringLower(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(defaultJson)));

        var store = CurrentStore;

        // Write to disk (.scrinia/workflows/{name}.json)
        string baseDir = GetScriniaBaseDir(store);
        string workflowsDir = Path.Combine(baseDir, "workflows");
        string filePath = Path.Combine(workflowsDir, $"{parsed.Name}.json");
        Directory.CreateDirectory(workflowsDir);

        // Archive previous version if file exists
        ArchiveFileVersion(filePath, Path.Combine(workflowsDir, "versions"));

        // Serialize and write workflow JSON
        string content = JsonSerializer.Serialize(parsed, PlanningJsonContext.Default.WorkflowDefinition);
        await File.WriteAllTextAsync(filePath, content, cancellationToken);

        // Write sidecar metadata
        string now = DateTimeOffset.UtcNow.ToString("o");
        var existingMeta = ReadSidecarMeta(filePath, PlanningJsonContext.Default.WorkflowFileMeta);
        var meta = new WorkflowFileMeta(
            BasedOn: basedOnHash,
            CreatedAt: existingMeta?.CreatedAt ?? now,
            UpdatedAt: now);
        WriteSidecarMeta(filePath, meta, PlanningJsonContext.Default.WorkflowFileMeta);

        // Check for legacy NMP/2 entry (MF-C01 migration note)
        string migrationNote = "";
        try
        {
            await ReadMemoryAsync(store, $"workflow:{parsed.Name}", cancellationToken);
            migrationNote = " Note: a legacy NMP/2 entry for workflow:{parsed.Name} still exists — it will be used as fallback but the disk file takes precedence.";
        }
        catch { /* no legacy entry — nothing to note */ }

        int seedCount = parsed.SeedActivities?.Length ?? 0;
        int gateCount = parsed.PostPlanActivities?.Length ?? 0;
        string wfAction = action == "create" ? "created" : "updated";
        var wfResult = ResponseBuilder.Success($"Workflow '{parsed.Name}' {wfAction} — {seedCount} seed(s), {gateCount} gate(s). Stored at .scrinia/workflows/{parsed.Name}.json. Files in .scrinia/ were updated — these are your changes.")
            .WithPath($"workflow:{parsed.Name}")
            .WithAction(wfAction);
        if (!string.IsNullOrEmpty(migrationNote))
            wfResult = wfResult.WithInfo(migrationNote.TrimStart());
        return wfResult.ToYaml();
    }

    /// <summary>
    /// Encodes and writes a planning memory, updating the index.
    /// If archiveExisting is true and an entry already exists, archives it first.
    /// </summary>
    private static async Task WritePlanningMemoryAsync(
        IMemoryStore store,
        string qualifiedName,
        string content,
        bool archiveExisting,
        CancellationToken ct)
        => await WritePlanningMemoryAsync(store, qualifiedName, content, archiveExisting, keywords: null, ct);

    /// <summary>
    /// Encodes and writes a planning memory with optional keyword metadata, updating the index.
    /// Keywords are stored in the index entry for fast keyword-only scans without artifact decoding.
    /// If archiveExisting is true and an entry already exists, archives it first.
    /// </summary>
    private static async Task WritePlanningMemoryAsync(
        IMemoryStore store,
        string qualifiedName,
        string content,
        bool archiveExisting,
        string[]? keywords,
        CancellationToken ct)
    {
        var (scope, subject) = store.ParseQualifiedName(qualifiedName);

        // Check for existing entry
        var existingEntries = store.LoadIndex(scope);
        var existing = existingEntries.FirstOrDefault(e => e.Name == subject);

        if (existing is not null && archiveExisting)
            store.ArchiveVersion(subject, scope);

        string artifact = Nmp2ChunkedEncoder.Encode(content);
        await store.WriteArtifactAsync(subject, scope, artifact, ct);

        string uri = store.ArtifactUri(subject, scope);
        long originalBytes = System.Text.Encoding.UTF8.GetByteCount(content);
        string desc = content[..Math.Min(200, content.Length)];
        DateTimeOffset? updatedAt = existing is not null ? DateTimeOffset.UtcNow : null;

        var entry = new ArtifactEntry(
            Name: subject,
            Uri: uri,
            OriginalBytes: originalBytes,
            ChunkCount: 1,
            CreatedAt: existing?.CreatedAt ?? DateTimeOffset.UtcNow,
            Description: desc,
            Keywords: keywords,
            UpdatedAt: updatedAt);

        store.Upsert(entry, scope);
    }

    /// <summary>
    /// Writes a structured project:state memory with current planning status.
    /// Uses archiveExisting: false to avoid version bloat on frequent state updates.
    /// </summary>
    internal static async Task WriteStateAsync(
        IMemoryStore store,
        string projectName,
        string projectId,
        string phase,
        string progressPct,
        string lastAction,
        string blockers,
        string nextStep,
        CancellationToken ct)
    {
        string timestamp = DateTimeOffset.UtcNow.ToString("o");
        string stateText =
            $"Project: {projectName}\n" +
            $"ID: {projectId}\n" +
            $"Phase: {phase}\n" +
            $"Progress: {progressPct}%\n" +
            $"Last action: {lastAction} ({timestamp})\n" +
            $"Blockers: {blockers}\n" +
            $"Next: {nextStep}";

        await WritePlanningMemoryAsync(store, "project:state", stateText,
            archiveExisting: false, ct);
    }

    /// <summary>
    /// Extracts a field value from state text (e.g. "Project: MyProj" → "MyProj").
    /// Returns null if not found.
    /// </summary>
    internal static string? ExtractStateField(string stateText, string fieldPrefix)
    {
        if (string.IsNullOrWhiteSpace(stateText)) return null;
        foreach (string line in stateText.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(fieldPrefix, StringComparison.OrdinalIgnoreCase))
                return trimmed[fieldPrefix.Length..].Trim();
        }
        return null;
    }

    /// <summary>
    /// Discovers distinct phase IDs from task keywords (e.g. "phase:01" → "01").
    /// Optionally scoped to a specific goal.
    /// </summary>
    private static List<string> DiscoverPhaseIds(IReadOnlyList<ArtifactEntry> taskEntries, string? goalId = null)
    {
        return taskEntries
            .Where(e => goalId is null || HasKeyword(e, $"goal:{goalId}"))
            .SelectMany(e => e.Keywords ?? [])
            .Where(k => k.StartsWith("phase:", StringComparison.OrdinalIgnoreCase))
            .Select(k => k["phase:".Length..])
            .Where(p => p != "init") // exclude init phase
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Calculates overall progress percentage from task completion data.
    /// Each phase contributes equally. Within a phase, progress = completed / total tasks.
    /// </summary>
    /// <remarks>
    /// Called at 10+ carry-forward sites on every state write. Must stay synchronous and
    /// lightweight (keyword scan over in-memory index only — no artifact decoding, no I/O).
    /// Tightly coupled to the <c>task:</c> topic and <c>phase:XX</c> / <c>status:complete</c>
    /// / <c>goal:ID</c> keyword conventions. Not a general-purpose API.
    /// </remarks>
    internal static string CalculateProgress(IMemoryStore store, string? goalId = null)
    {
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var allTaskEntries = store.LoadIndex(taskScope);

        var phaseIds = DiscoverPhaseIds(allTaskEntries, goalId);
        if (phaseIds.Count == 0)
            return "0";

        double totalProgress = 0;
        foreach (string phaseId in phaseIds)
        {
            var phaseEntries = allTaskEntries
                .Where(e => HasKeyword(e, $"phase:{phaseId}"))
                .Where(e => goalId is null || HasKeyword(e, $"goal:{goalId}"))
                .ToList();

            if (phaseEntries.Count == 0)
                continue;

            int complete = phaseEntries.Count(e => HasKeyword(e, "status:complete"));
            totalProgress += (double)complete / phaseEntries.Count;
        }

        int pct = (int)Math.Round(totalProgress / phaseIds.Count * 100);
        return pct.ToString();
    }

    /// <summary>Mark a task complete with outcome metadata. Appends to execution log.</summary>
    internal async Task<string> TaskComplete(
        [Description("Qualified task name (e.g. 'task:01-1-01').")] string taskName,
        [Description("Free-text describing what was done, any deviations or outcomes.")] string outcome,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Parse task name to get scope and subject
        var (scope, subject) = store.ParseQualifiedName(taskName);

        // Load index and find the existing task entry
        var allEntries = store.LoadIndex(scope);
        var existing = allEntries.FirstOrDefault(e => e.Name == subject);

        if (existing is null)
            return ResponseBuilder.Error($"Task '{taskName}' not found.").ToYaml();

        // Gate task validation — data-driven dispatch from WorkflowDefinition (override-aware)
        if (existing.Keywords is not null)
        {
            string? activeGoal = await GetActiveGoalIdAsync(store, cancellationToken);
            string goalWorkflowName = ResolveGoalWorkflowName(store, activeGoal);
            var (workflow, _) = await ResolveWorkflowAsync(store, goalWorkflowName, cancellationToken);

            // Compute goalShort once for template substitution
            string goalShort = "";
            if (activeGoal is not null)
            {
                var gm = GoalIdPattern.Match(activeGoal);
                if (gm.Success) goalShort = $"g{gm.Groups[1].Value}";
            }

            foreach (var kw in existing.Keywords.Where(k => k.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) || k.StartsWith("gate:", StringComparison.OrdinalIgnoreCase)))
            {
                string gateType = kw.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)
                    ? kw["tag:".Length..]
                    : kw["gate:".Length..];

                // Find matching activity in workflow definition
                var activity = workflow.SeedActivities
                    .Concat(workflow.PostPlanActivities)
                    .FirstOrDefault(a => string.Equals(a.Tag, gateType, StringComparison.OrdinalIgnoreCase));

                // Unknown gate type → pass through (WF-09 backward compat)
                if (activity?.Validation is null)
                    continue;

                var validation = activity.Validation;
                string target = SubstituteGoalShort(validation.Target, goalShort);
                string errorMessage = SubstituteGoalShort(validation.ErrorTemplate, goalShort);
                string? instructionMessage = validation.InstructionTemplate is not null
                    ? SubstituteGoalShort(validation.InstructionTemplate, goalShort)
                    : null;
                string? validationError = null;

                try
                {
                    switch (validation.CheckType)
                    {
                        case "memory-exists":
                            try { await ReadMemoryAsync(store, target, cancellationToken); }
                            catch (FileNotFoundException) { validationError = errorMessage; }
                            break;

                        case "index-prefix":
                        {
                            int colonIdx = target.IndexOf(':');
                            string topic = colonIdx >= 0 ? target[..colonIdx] : target;
                            string prefix = colonIdx >= 0 ? target[(colonIdx + 1)..] : "";
                            var (indexScope, _) = store.ParseQualifiedName($"{topic}:placeholder");
                            var entries = store.LoadIndex(indexScope);
                            bool found = entries.Any(e =>
                                string.IsNullOrEmpty(prefix) ||
                                e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                            if (!found)
                                validationError = errorMessage;
                            break;
                        }

                        case "index-no-gate":
                        {
                            int ngColonIdx = target.IndexOf(':');
                            string ngTopic = ngColonIdx >= 0 ? target[..ngColonIdx] : target;
                            var (ngScope, _) = store.ParseQualifiedName($"{ngTopic}:placeholder");
                            var ngEntries = store.LoadIndex(ngScope);
                            bool hasExecutionTasks = ngEntries.Any(e =>
                                (string.IsNullOrEmpty(goalShort) || HasKeyword(e, $"goal:{activeGoal}")) &&
                                e.Keywords is not null && !e.Keywords.Any(k => k.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) || k.StartsWith("gate:", StringComparison.OrdinalIgnoreCase)));
                            if (!hasExecutionTasks)
                                validationError = errorMessage;
                            break;
                        }

                        case "filesystem-glob":
                        {
                            string fsStoreDir = store.GetStoreDirForScope("local");
                            string fsScriniaDir = Path.GetDirectoryName(fsStoreDir) ?? fsStoreDir;
                            string workspaceRoot = Path.GetDirectoryName(fsScriniaDir) ?? fsScriniaDir;
                            // target is e.g. "docs/reports/*.md" — split into directory and file pattern
                            string targetNormalized = target.Replace('/', Path.DirectorySeparatorChar);
                            string globDir = Path.Combine(workspaceRoot, Path.GetDirectoryName(targetNormalized) ?? "");
                            string globPattern = Path.GetFileName(targetNormalized);
                            if (!Directory.Exists(globDir) || !Directory.EnumerateFiles(globDir, globPattern).Any())
                                validationError = errorMessage;
                            break;
                        }
                    }
                }
                catch { /* best-effort validation — swallow exceptions like the original */ }

                if (validationError is not null)
                {
                    var gateResponse = ResponseBuilder.Error($"gate '{gateType}' validation failed — {validationError}");
                    if (instructionMessage is not null)
                        gateResponse = gateResponse.WithInstruction(instructionMessage);
                    return gateResponse.ToYaml();
                }

                // RequiredOutputs validation (in addition to gate validation)
                if (activity?.RequiredOutputs is { Length: > 0 } requiredOutputs)
                {
                    foreach (var req in requiredOutputs)
                    {
                        string reqTarget = SubstituteGoalShort(req.Target, goalShort);
                        string? reqError = null;

                        try
                        {
                            switch (req.CheckType?.ToLowerInvariant())
                            {
                                case "memory-exists":
                                    try { await ReadMemoryAsync(store, reqTarget, cancellationToken); }
                                    catch (FileNotFoundException) { reqError = SubstituteGoalShort(req.ErrorTemplate, goalShort); }
                                    break;

                                case "index-prefix":
                                {
                                    int roColonIdx = reqTarget.IndexOf(':');
                                    string roTopic = roColonIdx >= 0 ? reqTarget[..roColonIdx] : reqTarget;
                                    string roPrefix = roColonIdx >= 0 ? reqTarget[(roColonIdx + 1)..] : "";
                                    var (roScope, _) = store.ParseQualifiedName($"{roTopic}:placeholder");
                                    var roEntries = store.LoadIndex(roScope);
                                    bool roFound = roEntries.Any(e =>
                                        string.IsNullOrEmpty(roPrefix) ||
                                        e.Name.StartsWith(roPrefix, StringComparison.OrdinalIgnoreCase));
                                    if (!roFound)
                                        reqError = SubstituteGoalShort(req.ErrorTemplate, goalShort);
                                    break;
                                }

                                case "index-no-gate":
                                {
                                    int roNgColonIdx = reqTarget.IndexOf(':');
                                    string roNgTopic = roNgColonIdx >= 0 ? reqTarget[..roNgColonIdx] : reqTarget;
                                    var (roNgScope, _) = store.ParseQualifiedName($"{roNgTopic}:placeholder");
                                    var roNgEntries = store.LoadIndex(roNgScope);
                                    bool roHasExecTasks = roNgEntries.Any(e =>
                                        (string.IsNullOrEmpty(goalShort) || HasKeyword(e, $"goal:{activeGoal}")) &&
                                        e.Keywords is not null && !e.Keywords.Any(k => k.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) || k.StartsWith("gate:", StringComparison.OrdinalIgnoreCase)));
                                    if (!roHasExecTasks)
                                        reqError = SubstituteGoalShort(req.ErrorTemplate, goalShort);
                                    break;
                                }

                                case "filesystem-glob":
                                {
                                    string roFsStoreDir = store.GetStoreDirForScope("local");
                                    string roFsScriniaDir = Path.GetDirectoryName(roFsStoreDir) ?? roFsStoreDir;
                                    string roWorkspaceRoot = Path.GetDirectoryName(roFsScriniaDir) ?? roFsScriniaDir;
                                    string roTargetNormalized = reqTarget.Replace('/', Path.DirectorySeparatorChar);
                                    string roGlobDir = Path.Combine(roWorkspaceRoot, Path.GetDirectoryName(roTargetNormalized) ?? "");
                                    string roGlobPattern = Path.GetFileName(roTargetNormalized);
                                    if (!Directory.Exists(roGlobDir) || !Directory.EnumerateFiles(roGlobDir, roGlobPattern).Any())
                                        reqError = SubstituteGoalShort(req.ErrorTemplate, goalShort);
                                    break;
                                }
                            }
                        }
                        catch { /* best-effort validation — swallow exceptions */ }

                        if (reqError is not null)
                        {
                            string reqInstruction = req.InstructionTemplate is not null
                                ? SubstituteGoalShort(req.InstructionTemplate, goalShort)
                                : "Produce the required output before completing this task.";
                            return ResponseBuilder.Error($"Required output check failed: {reqError}")
                                .WithInstruction(reqInstruction)
                                .ToYaml();
                        }
                    }
                }
            }
        }

        // Read task content to surface acceptance criteria
        string acceptanceCriteria = "";
        try
        {
            string taskContent = await ReadMemoryAsync(store, taskName, cancellationToken);
            int acIdx = taskContent.IndexOf("Acceptance criteria:", StringComparison.OrdinalIgnoreCase);
            if (acIdx >= 0)
                acceptanceCriteria = taskContent[(acIdx + "Acceptance criteria:".Length)..].Trim();
        }
        catch { /* best-effort */ }

        // Replace status keyword: remove status:* and add status:complete
        var newKeywords = (existing.Keywords ?? [])
            .Where(k => !k.StartsWith("status:", StringComparison.OrdinalIgnoreCase))
            .Append("status:complete")
            .ToArray();

        // Update entry via record with-expression — DO NOT call ArchiveVersion
        var updated = existing with
        {
            Keywords = newKeywords,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        store.Upsert(updated, scope);

        // Append to execution log: task:{phaseId}-execution-log
        string phaseId = existing.Keywords?
            .FirstOrDefault(k => k.StartsWith("phase:", StringComparison.OrdinalIgnoreCase))
            ?["phase:".Length..] ?? "unknown";

        string logName = $"task:{phaseId}-execution-log";
        string outcomeEntry = $"[{DateTimeOffset.UtcNow:o}] COMPLETE: {taskName}\n{outcome}";

        await AppendToExecutionLogAsync(store, logName, outcomeEntry, cancellationToken);

        // Auto-compact execution log if too large (COMPACT-01)
        string compactionNotice = "";
        try
        {
            var (logScope, logSubject) = store.ParseQualifiedName(logName);
            var logEntries = store.LoadIndex(logScope);
            var logEntry = logEntries.FirstOrDefault(e => e.Name == logSubject);
            if (logEntry is not null && logEntry.ChunkCount > 50)
            {
                const int keepRecent = 20;

                string artifact = await store.ReadArtifactAsync(logSubject, logScope, cancellationToken);
                int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);

                if (chunkCount > 50)
                {
                    // Archive the original before modifying
                    store.ArchiveVersion(logSubject, logScope);

                    // Keep last 20 chunks
                    int startChunk = chunkCount - keepRecent + 1;
                    var keptChunks = new string[keepRecent];
                    for (int i = 0; i < keepRecent; i++)
                        keptChunks[i] = Nmp2ChunkedEncoder.DecodeChunk(artifact, startChunk + i);

                    string compacted = Nmp2ChunkedEncoder.EncodeChunks(keptChunks);
                    await store.WriteArtifactAsync(logSubject, logScope, compacted, cancellationToken);

                    // Update index entry
                    long newBytes = System.Text.Encoding.UTF8.GetByteCount(
                        System.Text.Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(compacted)));
                    store.Upsert(logEntry with
                    {
                        ChunkCount = keepRecent,
                        OriginalBytes = newBytes,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        ChunkEntries = null
                    }, logScope);

                    // COMPACT-02: notice for response
                    compactionNotice = $"\nExecution log auto-compacted: {chunkCount} → {keepRecent} chunks.";
                }
            }
        }
        catch { /* best-effort compaction */ }

        // Update project state with computed progress
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? $"Phase {phaseId}";

        // Compute progress from task data (scoped to active goal)
        string? tcGoalId = await GetActiveGoalIdAsync(store, cancellationToken);
        string progressPct = CalculateProgress(store, tcGoalId);

        // Check if this was the last pending task in the phase (scoped to goal)
        var updatedEntries = store.LoadIndex(scope);
        var goalScopedEntries = updatedEntries
            .Where(e => HasKeyword(e, $"phase:{phaseId}"))
            .Where(e => tcGoalId is null || HasKeyword(e, $"goal:{tcGoalId}"))
            .ToList();
        bool phaseComplete = !goalScopedEntries.Any(e => HasKeyword(e, "status:pending"));

        string nextStep;
        if (phaseComplete)
            nextStep = $"all phase {phaseId} tasks complete — the QA gate task will handle verification, run task('next') to continue";
        else
        {
            var pendingCheck = goalScopedEntries
                .Where(e => HasKeyword(e, "status:pending"))
                .ToList();
            int thisWaveCheck = ParseWave(existing);
            int sameWaveCheck = pendingCheck.Count(e => ParseWave(e) == thisWaveCheck);
            nextStep = sameWaveCheck > 0
                ? $"keep {sameWaveCheck} remaining wave {thisWaveCheck} parallel agents running"
                : "run task('next') to get the next wave's tasks";
        }

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: progressPct,
            lastAction: $"Completed {taskName}",
            blockers: "none",
            nextStep: nextStep,
            cancellationToken);

        string tcContent;
        string? tcInstruction;
        var tcInfoItems = new List<string>();

        if (phaseComplete)
        {
            tcContent = $"Task '{taskName}' marked complete. All phase {phaseId} tasks done.";
            tcInstruction = "verify the work (run tests, review changes, confirm behavior), then call task('next') — the QA gate task will handle structured verification.";
        }
        else
        {
            // Check remaining pending tasks — distinguish same-wave (parallel) from next-wave
            var pendingInPhase = goalScopedEntries
                .Where(e => HasKeyword(e, "status:pending"))
                .ToList();

            // Find the current task's wave to determine if remaining tasks are in the same wave
            int thisWave = ParseWave(existing);
            int sameWaveRemaining = pendingInPhase.Count(e => ParseWave(e) == thisWave);
            int totalRemaining = pendingInPhase.Count;

            if (sameWaveRemaining > 1)
            {
                tcContent = $"Task '{taskName}' marked complete. {sameWaveRemaining} tasks remaining in wave {thisWave}.";
                tcInstruction = "keep parallel agents running — call task('complete') for each as they finish.";
            }
            else if (sameWaveRemaining == 1)
            {
                tcContent = $"Task '{taskName}' marked complete. 1 task remaining in wave {thisWave}.";
                tcInstruction = null;
            }
            else
            {
                tcContent = $"Task '{taskName}' marked complete. Wave {thisWave} done.";
                tcInstruction = $"call task('next') to get wave {thisWave + 1} tasks ({totalRemaining} pending).";
            }
        }

        if (!string.IsNullOrWhiteSpace(acceptanceCriteria))
            tcContent += $"\nAcceptance criteria for this task:\n{acceptanceCriteria}";

        // COMPACT-02: add compaction notice if triggered
        if (!string.IsNullOrEmpty(compactionNotice))
            tcInfoItems.Add(compactionNotice.Trim());

        var tcResponse = ResponseBuilder.Success(tcContent)
            .WithPath($"task:{subject}")
            .WithAction("completed");
        if (tcInstruction is not null)
            tcResponse = tcResponse.WithInstruction(tcInstruction);
        if (tcInfoItems.Count > 0)
            tcResponse = tcResponse.WithInfo([.. tcInfoItems]);

        return tcResponse.ToYaml();
    }

    /// <summary>
    /// Checks whether the cartographer should be run based on memory growth
    /// since the last cartography entry. Returns a warning string or empty.
    /// </summary>
    private static string CheckCartographerNeeded(IMemoryStore store)
    {
        try
        {
            var allEntries = store.ListScoped(null);
            int totalMemories = allEntries.Count;

            // Check last cartography entry
            var (cartScope2, _) = store.ParseQualifiedName("cartography:placeholder");
            var cartEntries2 = store.LoadIndex(cartScope2);
            var lastCart = cartEntries2.OrderByDescending(e => e.UpdatedAt ?? e.CreatedAt).FirstOrDefault();

            if (lastCart is not null)
            {
                var lastCartDate = lastCart.UpdatedAt ?? lastCart.CreatedAt;
                // Count memories created/modified after last cartography
                int newSince = allEntries.Count(e => (e.Entry.UpdatedAt ?? e.Entry.CreatedAt) > lastCartDate);
                if (newSince >= 10)
                    return $"⚠ ACTION NEEDED: {newSince} memories created/modified since last cartographer run — spawn a cartographer to index connections.\n";
            }
            else if (totalMemories >= 10)
            {
                return $"⚠ ACTION NEEDED: {totalMemories} memories exist with no cartographer run — spawn a cartographer to index connections.\n";
            }
        }
        catch { /* best-effort */ }

        return "";
    }

    /// <summary>
    /// Returns the number of memories created/modified since the last cartography entry.
    /// If no cartography exists, returns total memory count. Returns 0 on error.
    /// </summary>
    private static int CountMemoriesSinceLastCartography(IMemoryStore store)
    {
        try
        {
            var allEntries = store.ListScoped(null);
            int totalMemories = allEntries.Count;

            var (cartScope2, _) = store.ParseQualifiedName("cartography:placeholder");
            var cartEntries2 = store.LoadIndex(cartScope2);
            var lastCart = cartEntries2.OrderByDescending(e => e.UpdatedAt ?? e.CreatedAt).FirstOrDefault();

            if (lastCart is not null)
            {
                var lastCartDate = lastCart.UpdatedAt ?? lastCart.CreatedAt;
                return allEntries.Count(e => (e.Entry.UpdatedAt ?? e.Entry.CreatedAt) > lastCartDate);
            }
            else
            {
                return totalMemories;
            }
        }
        catch { return 0; }
    }

    /// <summary>
    /// Appends an outcome entry to the named execution log memory using AppendChunk.
    /// Creates the log if it doesn't exist.
    /// </summary>
    private static Task AppendToExecutionLogAsync(
        IMemoryStore store, string logName, string outcomeText, CancellationToken ct)
        => AppendToExecutionLogAsync(store, logName, outcomeText, keywords: null, ct);

    /// <summary>
    /// Appends an outcome entry to the named execution log memory using AppendChunk,
    /// optionally setting keywords on the index entry.
    /// Creates the log if it doesn't exist.
    /// </summary>
    private static async Task AppendToExecutionLogAsync(
        IMemoryStore store, string logName, string outcomeText, string[]? keywords, CancellationToken ct)
    {
        var (logScope, logSubject) = store.ParseQualifiedName(logName);

        // Check for existing log artifact
        string? existingArtifact = null;
        long existingBytes = 0;
        var logEntries = store.LoadIndex(logScope);
        var logEntry = logEntries.FirstOrDefault(e => e.Name == logSubject);

        if (logEntry is not null)
        {
            try
            {
                existingArtifact = await store.ReadArtifactAsync(logSubject, logScope, ct);
                existingBytes = logEntry.OriginalBytes;
            }
            catch (FileNotFoundException)
            {
                existingArtifact = null;
            }
        }

        // Build new artifact: AppendChunk if existing, Encode if fresh
        string newArtifact;
        int newByteCount = System.Text.Encoding.UTF8.GetByteCount(outcomeText);
        long totalBytes;

        if (existingArtifact is not null)
        {
            newArtifact = Nmp2ChunkedEncoder.AppendChunk(existingArtifact, outcomeText);
            totalBytes = existingBytes + newByteCount;
        }
        else
        {
            newArtifact = Nmp2ChunkedEncoder.Encode(outcomeText);
            totalBytes = newByteCount;
        }

        await store.WriteArtifactAsync(logSubject, logScope, newArtifact, ct);

        string uri = store.ArtifactUri(logSubject, logScope);
        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(newArtifact);
        string desc = outcomeText[..Math.Min(200, outcomeText.Length)];
        DateTimeOffset? updatedAt = existingArtifact is not null ? DateTimeOffset.UtcNow : null;

        var newLogEntry = new ArtifactEntry(
            Name: logSubject,
            Uri: uri,
            OriginalBytes: totalBytes,
            ChunkCount: chunkCount,
            CreatedAt: logEntry?.CreatedAt ?? DateTimeOffset.UtcNow,
            Description: desc,
            Keywords: keywords,
            UpdatedAt: updatedAt);

        store.Upsert(newLogEntry, logScope);
    }

    // ── Keyword helpers (EXEC-01) ─────────────────────────────────────────────

    /// <summary>Substitutes {goalShort} in a template and collapses any resulting double-dashes
    /// so that e.g. "retro-{goalShort}-" becomes "retro-" (not "retro--") when goalShort is empty.</summary>
    private static string SubstituteGoalShort(string template, string goalShort)
    {
        string result = template.Replace("{goalShort}", goalShort);
        // Collapse double-dashes that result from empty goalShort substitution
        if (goalShort.Length == 0)
            result = result.Replace("--", "-");
        return result;
    }

    /// <summary>Returns true if the entry has the given keyword (case-insensitive).</summary>
    internal static bool HasKeyword(ArtifactEntry e, string keyword) =>
        e.Keywords?.Contains(keyword, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>Extracts the active goal ID (e.g., "G-14" or "G-29-a3f") from project:context goals section.</summary>
    internal static async Task<string?> GetActiveGoalIdAsync(IMemoryStore store, CancellationToken ct)
    {
        try
        {
            string contextText = await ReadMemoryAsync(store, "project:context", ct);
            var (goals, _, _) = ParseGoalsSection(contextText);
            var activeLine = goals.FirstOrDefault(g => g.Contains("[active]", StringComparison.OrdinalIgnoreCase));
            if (activeLine is null) return null;

            // Extract full goal ID from "[G-14] ..." or "[G-29-a3f] ..."
            var match = BracketedGoalIdPattern.Match(activeLine);
            return match.Success ? $"G-{match.Groups[1].Value}" : null;
        }
        catch { return null; }
    }

    /// <summary>Resolves workflow name for the active goal by reading the "workflow:*" keyword from its seed tasks. Falls back to "default".</summary>
    internal static string ResolveGoalWorkflowName(IMemoryStore store, string? goalId)
    {
        if (goalId is null) return "default";
        try
        {
            var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
            var taskEntries = store.LoadIndex(taskScope);
            // Find any task belonging to this goal that has a workflow keyword
            var goalTask = taskEntries.FirstOrDefault(e =>
                HasKeyword(e, $"goal:{goalId}") &&
                e.Keywords?.Any(k => k.StartsWith("workflow:", StringComparison.OrdinalIgnoreCase)) == true);
            if (goalTask?.Keywords is not null)
            {
                string? wfKeyword = goalTask.Keywords.FirstOrDefault(k =>
                    k.StartsWith("workflow:", StringComparison.OrdinalIgnoreCase));
                if (wfKeyword is not null)
                    return wfKeyword["workflow:".Length..];
            }
        }
        catch { /* best-effort */ }
        return "default";
    }

    /// <summary>Extracts wave number from "wave:N" keyword; returns 0 if not found.</summary>
    private static int ParseWave(ArtifactEntry e)
    {
        string? waveKw = e.Keywords?.FirstOrDefault(k =>
            k.StartsWith("wave:", StringComparison.OrdinalIgnoreCase));
        return waveKw is not null && int.TryParse(waveKw[5..], out int w) ? w : 0;
    }

    /// <summary>Returns all subject names from "depends_on:*" keywords.</summary>
    private static IEnumerable<string> GetDependencies(ArtifactEntry e) =>
        e.Keywords?
            .Where(k => k.StartsWith("depends_on:", StringComparison.OrdinalIgnoreCase))
            .Select(k => k["depends_on:".Length..])
        ?? Enumerable.Empty<string>();

    /// <summary>
    /// Extracts requirement entries from requirements text, filtered to the given REQ-IDs.
    /// Each matching "REQ-NNN: description" line becomes one criterion.
    /// </summary>
    private static List<string> ExtractRequirementCriteria(string requirementsText, HashSet<string> reqIds)
    {
        var criteria = new List<string>();
        foreach (string rawLine in requirementsText.Split('\n'))
        {
            string trimmed = rawLine.Trim();
            var match = ReqIdPattern.Match(trimmed);
            if (match.Success && reqIds.Contains(match.Groups[1].Value))
            {
                // Use the full REQ line as the criterion (e.g. "REQ-001: description")
                criteria.Add(trimmed);
            }
        }
        return criteria;
    }

    // ── Concern tracking tools (CONC-01, CONC-02, CONC-03) ───────────────────

    /// <summary>Internal dispatcher for concern operations — delegates to ConcernAdd/ConcernResolve/ConcernList. Exposed via memory() dispatcher.</summary>
    public async Task<string> ConcernDispatch(
        string action = "list",
        string? description = null,
        string? severity = null,
        string? phaseScope = null,
        string? id = null,
        string? concernName = null,
        string? resolution = null,
        string? verifiedBy = null,
        string? phaseFilter = null,
        CancellationToken cancellationToken = default)
    {
        string act = action.Trim().ToLowerInvariant();
        switch (act)
        {
            case "add":
                if (string.IsNullOrWhiteSpace(description))
                    return ResponseBuilder.Error("concern('add') requires 'description' parameter.").ToYaml();
                if (string.IsNullOrWhiteSpace(severity))
                    return ResponseBuilder.Error("concern('add') requires 'severity' parameter.").ToYaml();
                if (string.IsNullOrWhiteSpace(phaseScope))
                    return ResponseBuilder.Error("concern('add') requires 'phaseScope' parameter.").ToYaml();
                return await ConcernAdd(description, severity, phaseScope, id, cancellationToken);

            case "resolve":
                if (string.IsNullOrWhiteSpace(concernName))
                    return ResponseBuilder.Error("concern('resolve') requires 'concernName' parameter.").ToYaml();
                if (string.IsNullOrWhiteSpace(resolution))
                    return ResponseBuilder.Error("concern('resolve') requires 'resolution' parameter.").ToYaml();
                if (string.IsNullOrWhiteSpace(verifiedBy))
                    return ResponseBuilder.Error("concern('resolve') requires 'verifiedBy' parameter.").ToYaml();
                return await ConcernResolve(concernName, resolution, verifiedBy, cancellationToken);

            case "list":
                return await ConcernList(phaseFilter, statusFilter: null, cancellationToken);

            default:
                return ResponseBuilder.Error($"Unknown action '{action}'. Valid actions: 'add', 'resolve', 'list'.").ToYaml();
        }
    }

    /// <summary>Track a risk or concern with severity and phase scope.</summary>
    internal async Task<string> ConcernAdd(
        [Description("Concern description.")] string description,
        [Description("Severity: high, medium, or low.")] string severity,
        [Description("Phase scope, e.g. '06' or 'all'.")] string phaseScope,
        [Description("Optional readable ID; auto-generated if omitted (e.g. 'auth-risk').")] string? id = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Prerequisite check: project:context must exist
        try
        {
            await ReadMemoryAsync(store, "project:context", cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return ResponseBuilder.Error("No project initialized. Run project_init first.").ToYaml();
        }

        // Generate ID if not provided
        string concernId = id ?? DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");

        // Build content
        string content =
            $"## Concern: {concernId}\n" +
            $"**Description:** {description}\n" +
            $"**Severity:** {severity}\n" +
            $"**Phase:** {phaseScope}\n" +
            $"**Added:** {DateTimeOffset.UtcNow:o}\n";

        string qualifiedName = $"concern:{concernId}";

        // Extract keywords from description and merge with explicit keywords
        var (autoKeywords, _) = TextAnalysis.AnalyzeText(description);
        string[] explicitKeywords = ["status:active", $"severity:{severity}", $"phase:{phaseScope}"];
        string[] mergedKeywords = TextAnalysis.MergeKeywords(explicitKeywords, autoKeywords);

        await WritePlanningMemoryAsync(store, qualifiedName, content,
            archiveExisting: false,
            keywords: mergedKeywords,
            cancellationToken);

        // Detect concern keyword patterns
        string patternSuggestion = "";
        try
        {
            var (concernScope, _) = store.ParseQualifiedName("concern:placeholder");
            var allConcerns = store.LoadIndex(concernScope);

            // Noise prefixes to exclude from pattern matching
            var noisePrefixes = new[] { "status:", "severity:", "phase:", "provenance:",
                "goal:", "ref:", "file:", "wave:", "depends_on:", "basedOn:", "type:" };

            // Count keyword frequency across active concerns
            var keywordCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in allConcerns)
            {
                if (entry.Keywords is null) continue;
                if (!entry.Keywords.Any(k => k.Equals("status:active", StringComparison.OrdinalIgnoreCase)))
                    continue; // only count active concerns

                foreach (var kw in entry.Keywords)
                {
                    if (kw.Equals("orphan", StringComparison.OrdinalIgnoreCase)) continue;
                    if (noisePrefixes.Any(p => kw.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

                    keywordCounts.TryGetValue(kw, out int count);
                    keywordCounts[kw] = count + 1;
                }
            }

            // Find keywords shared by 3+ concerns
            var patterns = keywordCounts
                .Where(kv => kv.Value >= 3)
                .OrderByDescending(kv => kv.Value)
                .Take(3)
                .ToList();

            if (patterns.Count > 0)
            {
                patternSuggestion = "\n" + string.Join("\n", patterns.Select(p =>
                    $"Pattern detected: {p.Value} concerns share keyword '{p.Key}'. Consider creating a patterns:{p.Key} memory."));
            }
        }
        catch { /* best-effort */ }

        // Update project state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? "Not started";
        string? concernGoalId = await GetActiveGoalIdAsync(store, cancellationToken);
        string progressPct = CalculateProgress(store, concernGoalId);

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: progressPct,
            lastAction: $"Concern added: {qualifiedName} (severity:{severity})",
            blockers: "none",
            nextStep: "run memory('list', { path: '/concern/' }) to list active concerns, or memory('transition', { path: '/concern/...', to: 'resolved' }) when addressed",
            cancellationToken);

        var caResponse = ResponseBuilder.Success($"Stored as {qualifiedName}. Files in .scrinia/ were updated — these are your changes.")
            .WithPath(qualifiedName)
            .WithAction("created");
        if (!string.IsNullOrEmpty(patternSuggestion))
            caResponse = caResponse.WithInfo(patternSuggestion.Trim());
        return caResponse.ToYaml();
    }

    /// <summary>Resolve a tracked concern with resolution notes.</summary>
    internal async Task<string> ConcernResolve(
        [Description("Concern name (e.g. 'concern:auth-risk' or 'concern:20260319-143022').")] string concernName,
        [Description("Resolution notes.")] string resolution,
        [Description("Who verified the resolution: 'debugger', 'qa', or 'manual'.")] string verifiedBy,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        var validVerifiers = new[] { "debugger", "qa", "manual" };
        if (!validVerifiers.Contains(verifiedBy, StringComparer.OrdinalIgnoreCase))
            return ResponseBuilder.Error($"verifiedBy must be 'debugger', 'qa', or 'manual'. Got: '{verifiedBy}'.").ToYaml();

        // Parse name to get scope and subject
        var (scope, subject) = store.ParseQualifiedName(concernName);

        // Load index and find existing entry
        var allEntries = store.LoadIndex(scope);
        var existing = allEntries.FirstOrDefault(e =>
            string.Equals(e.Name, subject, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
            return ResponseBuilder.Error($"Concern '{concernName}' not found.").ToYaml();

        // Extract existing severity and phase keywords (to preserve them)
        string severityKw = existing.Keywords?
            .FirstOrDefault(k => k.StartsWith("severity:", StringComparison.OrdinalIgnoreCase))
            ?? "severity:unknown";
        string phaseKw = existing.Keywords?
            .FirstOrDefault(k => k.StartsWith("phase:", StringComparison.OrdinalIgnoreCase))
            ?? "phase:unknown";

        // Read existing content
        string existingContent;
        try
        {
            existingContent = await ReadMemoryAsync(store, concernName, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            existingContent = $"(original content not found for {concernName})";
        }

        // Build updated content with resolution appended
        string timestamp = DateTimeOffset.UtcNow.ToString("o");
        string updatedContent =
            existingContent.TrimEnd() +
            $"\n\n## Resolution\n{resolution}\n**Resolved at:** {timestamp}\n";

        // Write updated content with resolved status (no archiving)
        string[] resolvedKeywords = ["status:resolved", severityKw, phaseKw, $"verified_by:{verifiedBy.ToLowerInvariant()}"];
        await WritePlanningMemoryAsync(store, concernName, updatedContent,
            archiveExisting: false,
            keywords: resolvedKeywords,
            cancellationToken);

        // Update project state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? "Not started";
        string? resolveGoalId = await GetActiveGoalIdAsync(store, cancellationToken);
        string progressPct = CalculateProgress(store, resolveGoalId);

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: progressPct,
            lastAction: $"Concern resolved: {concernName}",
            blockers: "none",
            nextStep: "run concern to check remaining active concerns",
            cancellationToken);

        return ResponseBuilder.Success($"Concern '{concernName}' resolved. Files in .scrinia/ were updated — these are your changes.")
            .WithPath(concernName)
            .WithAction("resolved")
            .ToYaml();
    }

    /// <summary>List tracked concerns by status and phase (index-only, no artifact decoding).</summary>
    internal Task<string> ConcernList(
        [Description("Filter by phase (e.g. '06'); omit for all phases.")] string? phaseFilter = null,
        [Description("Filter by status; defaults to 'active'.")] string? statusFilter = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        string effectiveStatus = statusFilter ?? "active";

        // Load index via keyword-only scan
        IReadOnlyList<ArtifactEntry> allEntries;
        try
        {
            var (scope, _) = store.ParseQualifiedName("concern:placeholder");
            allEntries = store.LoadIndex(scope);
        }
        catch
        {
            return Task.FromResult(ResponseBuilder.Success("No active concerns.").WithAction("listed").ToYaml());
        }

        // Filter by status
        var filtered = allEntries
            .Where(e => HasKeyword(e, $"status:{effectiveStatus}"))
            .ToList();

        // Filter by phase if provided
        if (!string.IsNullOrWhiteSpace(phaseFilter))
        {
            filtered = filtered
                .Where(e => HasKeyword(e, $"phase:{phaseFilter}"))
                .ToList();
        }

        if (filtered.Count == 0)
        {
            string phaseNote = phaseFilter is not null ? $" (phase:{phaseFilter})" : "";
            return Task.FromResult(ResponseBuilder.Success($"No active concerns{phaseNote}.").WithAction("listed").ToYaml());
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Active concerns ({filtered.Count}):");
        sb.AppendLine();

        foreach (var entry in filtered)
        {
            string sevKw = entry.Keywords?
                .FirstOrDefault(k => k.StartsWith("severity:", StringComparison.OrdinalIgnoreCase))
                ?? "severity:unknown";
            string phaseKw = entry.Keywords?
                .FirstOrDefault(k => k.StartsWith("phase:", StringComparison.OrdinalIgnoreCase))
                ?? "phase:unknown";

            sb.AppendLine($"- concern:{entry.Name} [{sevKw}] [{phaseKw}]");

            if (sb.Length > MaxResponseChars - 200)
            {
                sb.AppendLine("[... truncated to 8KB limit]");
                break;
            }
        }

        return Task.FromResult(ResponseBuilder.Success(sb.ToString().TrimEnd()).WithAction("listed").ToYaml());
    }

    /// <summary>Store a structured phase retrospective in learn:retro-gN-phaseId.</summary>
    internal async Task<string> PlanRetrospective(
        string phaseId,
        string whatWorked,
        string whatFailed,
        string lessons,
        string? beliefsUpdated = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        string timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        string beliefsSection = !string.IsNullOrWhiteSpace(beliefsUpdated)
            ? $"\n\n## Beliefs Updated\n{beliefsUpdated}"
            : "";

        string retroContent =
            $"## Phase {phaseId} Retrospective\n" +
            $"**Date:** {timestamp}\n\n" +
            $"## What Worked\n{whatWorked}\n\n" +
            $"## What Failed\n{whatFailed}\n\n" +
            $"## Lessons\n{lessons}" +
            beliefsSection + "\n\n" +
            $"## Provenance\nAuthored by agent via self-reflector gate. Keyword: provenance:agent";

        // Fetch goal ID early so per-phase file name includes it
        string? retroGoalId = await GetActiveGoalIdAsync(store, cancellationToken);

        string goalNum = "0";
        if (retroGoalId is not null)
        {
            var gm = GoalIdPattern.Match(retroGoalId);
            if (gm.Success) goalNum = gm.Groups[1].Value;
        }
        string retroMemoryName = $"learn:retro-g{goalNum}-{phaseId}";

        await WritePlanningMemoryAsync(store, retroMemoryName, retroContent,
            archiveExisting: true,
            keywords: ["provenance:agent", $"phase:{phaseId}", $"goal:{retroGoalId ?? "none"}"],
            cancellationToken);

        // Auto-store beliefs as topical memories (Change 4: structural distillation)
        if (!string.IsNullOrWhiteSpace(beliefsUpdated))
        {
            string beliefMemory =
                $"## Beliefs from Phase {phaseId} ({timestamp})\n\n{beliefsUpdated}";

            await WritePlanningMemoryAsync(store, $"learn:beliefs-phase-{phaseId}", beliefMemory,
                archiveExisting: true,
                keywords: ["provenance:agent", $"phase:{phaseId}", "type:beliefs"],
                cancellationToken);
        }

        // Determine next step: more phases to execute, or goal completion?
        string retroNextStep = "";
        try
        {
            var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
            var allTasks = store.LoadIndex(taskScope);

            // Discover phases from task keywords
            var phaseIds = DiscoverPhaseIds(allTasks, retroGoalId);
            if (phaseIds.Count > 0)
            {

                // Find next phase that has pending tasks or no tasks yet
                string? nextPhase = null;
                bool allPhasesDone = true;
                foreach (string pid in phaseIds)
                {
                    var phaseTasks = allTasks
                        .Where(e => HasKeyword(e, $"phase:{pid}"))
                        .Where(e => retroGoalId is null || HasKeyword(e, $"goal:{retroGoalId}"))
                        .ToList();
                    if (phaseTasks.Count == 0)
                    {
                        nextPhase ??= pid;
                        allPhasesDone = false;
                    }
                    else if (phaseTasks.Any(e => HasKeyword(e, "status:pending")))
                    {
                        nextPhase ??= pid;
                        allPhasesDone = false;
                    }
                }

                // Check for existing skills that should be updated with lessons
                string skillNudge = "";
                try
                {
                    var (skillScope, _) = store.ParseQualifiedName("skill:placeholder");
                    var skillEntries = store.LoadIndex(skillScope);
                    if (skillEntries.Count > 0)
                    {
                        var names = skillEntries.Select(e => $"skill:{e.Name}").Take(5);
                        skillNudge = $"\nℹ Existing skills to consider updating: {string.Join(", ", names)}";
                    }
                }
                catch { /* skill scope not yet created — skip silently */ }

                if (allPhasesDone)
                    retroNextStep = "\nAll phases complete. → INSTRUCTION: complete the following before calling memory('transition', { path: '/goal/G-X', to: 'complete' }):\n" +
                        "0. Spawn QA agent: memory('recall', { path: '/skill/qa' }) → verify tests pass, build clean, criteria met\n" +
                        "1. Spawn march reporter: memory('recall', { path: '/skill/march-reporter' }) → docs/reports/ + sessions:YYYY-MM-DD memory\n" +
                        "2. Distill valuable learnings into topical memories (remember) so future goals start smarter\n" +
                        "3. Update existing skills or create new ones (memory('remember', { path: '/skill/...' })) with lessons from this goal" +
                        skillNudge + "\n" +
                        "4. Then call memory('transition', { path: '/goal/G-X', to: 'complete' })";
                else if (nextPhase is not null)
                    retroNextStep = $"\n→ INSTRUCTION: investigate phase {nextPhase} — explore the codebase, store research findings, then plan tasks." +
                        skillNudge +
                        "\nℹ if this conversation is getting long, checkpoint your state: store([\"current context...\"], \"~checkpoint\")";
            }
        }
        catch { /* best-effort guidance */ }

        // Update project:state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? $"Phase {phaseId}";

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: CalculateProgress(store, retroGoalId),
            lastAction: $"Retrospective for phase {phaseId}",
            blockers: "none",
            nextStep: retroNextStep.TrimStart('\n'),
            cancellationToken);

        string retroContent2 = $"Phase {phaseId} retrospective stored in {retroMemoryName}. " +
            "Searchable via standard search.\n" +
            "Update your session log: append to or store sessions:YYYY-MM-DD with this phase's outcome.";

        var retroWarnings = new List<string>();
        // Check memory growth for cartographer nudge (CART-02)
        string retroCartWarning = CheckCartographerNeeded(store);
        if (!string.IsNullOrEmpty(retroCartWarning))
            retroWarnings.Add(retroCartWarning.Replace("\u26a0 ACTION NEEDED: ", "").TrimEnd());

        var retroResponse = ResponseBuilder.Success(retroContent2)
            .WithPath(retroMemoryName)
            .WithAction("created");
        if (!string.IsNullOrEmpty(retroNextStep))
            retroResponse = retroResponse.WithInstruction(retroNextStep.TrimStart('\n'));
        if (retroWarnings.Count > 0)
            retroResponse = retroResponse.WithActionNeeded([.. retroWarnings]);

        return retroResponse.ToYaml();
    }

    /// <summary>Alias for AgentProfile — used by profile dispatcher.</summary>
    internal Task<string> PlanProfile(string profile, CancellationToken cancellationToken = default)
        => AgentProfile(profile, cancellationToken);

    /// <summary>Store or update project-level agent behavioral norms.</summary>
    internal async Task<string> AgentProfile(
        string profile,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Write to .scrinia/agent/profile.md (plain markdown)
        string baseDir = GetScriniaBaseDir(store);
        string agentDir = Path.Combine(baseDir, "agent");
        string filePath = Path.Combine(agentDir, "profile.md");
        Directory.CreateDirectory(agentDir);

        // Archive previous version if file exists
        ArchiveFileVersion(filePath, Path.Combine(agentDir, "versions"));

        await File.WriteAllTextAsync(filePath, profile, cancellationToken);

        // Write sidecar metadata
        string now = DateTimeOffset.UtcNow.ToString("o");
        var existingMeta = ReadSidecarMeta(filePath, PlanningJsonContext.Default.AgentFileMeta);
        var meta = new AgentFileMeta(
            CreatedAt: existingMeta?.CreatedAt ?? now,
            UpdatedAt: now);
        WriteSidecarMeta(filePath, meta, PlanningJsonContext.Default.AgentFileMeta);

        return ResponseBuilder.Success("Agent profile stored in .scrinia/agent/profile.md. Norms persist across sessions and are searchable via standard search.")
            .WithPath("agent:profile")
            .WithAction("created")
            .ToYaml();
    }

    // knowledge_add removed — knowledge is just memories via remember().
    // e.g., memory('remember', { path: '/dotnet/asynclocal-pattern', content: [...], keywords: ["source:agent"] })

    // -- Dynamic goal management (GOAL-01, GOAL-02, GOAL-04) ---------------------

    /// <summary>Internal dispatcher for goal management — delegates to GoalUpdate. Exposed via memory() dispatcher.</summary>
    public Task<string> Goal(
        string action,
        string? description = null,
        string? goalId = null,
        string? outcome = null,
        string? workflowRef = null,
        CancellationToken cancellationToken = default)
    {
        return GoalUpdate(action, description, goalId, outcome, workflowRef, cancellationToken);
    }

    /// <summary>Manage project goals dynamically.</summary>
    internal async Task<string> GoalUpdate(
        [Description("Action to perform: 'add', 'edit', 'complete', or 'list'.")] string action,
        [Description("Goal description (required for 'add' action).")] string? description = null,
        [Description("Goal ID to complete (e.g. 'G-1'); required for 'complete' action.")] string? goalId = null,
        [Description("Outcome note; required for 'complete' action.")] string? outcome = null,
        string? workflowRef = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Prerequisite check: project:context must exist
        string contextText;
        try
        {
            contextText = await ReadMemoryAsync(store, "project:context", cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return ResponseBuilder.Error("No project initialized. Run project_init first.").ToYaml();
        }

        string actionLower = action.Trim().ToLowerInvariant();

        switch (actionLower)
        {
            case "add":
            {
                if (string.IsNullOrWhiteSpace(description))
                    return ResponseBuilder.Error("'add' action requires a description.").ToYaml();

                var (goals, originalCount, contextWithoutGoals) = ParseGoalsSection(contextText);

                // First mutation: lock original count if not yet set
                int lockedOriginalCount = originalCount >= 0 ? originalCount : goals.Count;

                // Assign sequential ID: scan for highest existing G-N to avoid reuse after cleanup.
                // Also use total goal count as a floor — init goals lack [G-N] markers.
                int maxId = 0;
                foreach (var goal in goals)
                {
                    var idMatch = GoalIdNumericPattern.Match(goal);
                    if (idMatch.Success && int.TryParse(idMatch.Groups[1].Value, out int id) && id > maxId)
                        maxId = id;
                }
                int nextId = Math.Max(maxId, goals.Count) + 1;
                string suffix = Guid.NewGuid().ToString("N")[..3];
                string newGoalId = $"G-{nextId}-{suffix}";
                string newGoalLine = $"- [{newGoalId}] [active] {description}";
                goals.Add(newGoalLine);

                // Rebuild goals section
                string goalsSection = BuildGoalsSection(goals, lockedOriginalCount);
                string updatedContext = contextWithoutGoals.TrimEnd() + "\n\n" + goalsSection;

                await WritePlanningMemoryAsync(store, "project:context", updatedContext,
                    archiveExisting: true, cancellationToken);

                // Update project:state with next step toward planning
                string addStateText;
                try { addStateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
                catch (FileNotFoundException) { addStateText = ""; }

                string addProjectName = ExtractStateField(addStateText, "Project:") ?? "Unknown Project";
                string addProjectId = ExtractStateField(addStateText, "ID:") ?? DeriveProjectId(store);
                string addPhase = ExtractStateField(addStateText, "Phase:") ?? "Not started";
                string addProgress = CalculateProgress(store, null);

                await WriteStateAsync(store, addProjectName, addProjectId,
                    phase: addPhase, progressPct: addProgress,
                    lastAction: $"Goal added: {newGoalId}",
                    blockers: "none",
                    nextStep: "clarify the goal with the user before planning",
                    cancellationToken);

                // Compute goal prefix for seed task naming
                string goalPrefix = "";
                var gpMatch = GoalIdPattern.Match(newGoalId);
                if (gpMatch.Success) goalPrefix = $"g{gpMatch.Groups[1].Value}-";

                // Auto-create seed tasks from workflow definition (override-aware)
                string resolvedWorkflowName = workflowRef ?? "default";
                var (workflow, _) = await ResolveWorkflowAsync(store, resolvedWorkflowName, cancellationToken);
                foreach (var activity in workflow.SeedActivities.OrderBy(a => a.Wave ?? 0))
                {
                    try
                    {
                        string taskName = $"task:{goalPrefix}00-{activity.Wave}-{activity.Id}";
                        var keywords = new List<string>
                        {
                            "status:pending",
                            $"wave:{activity.Wave}",
                            "phase:00",
                            $"tag:{activity.Tag}",
                            $"goal:{newGoalId}",
                            $"workflow:{resolvedWorkflowName}"
                        };
                        // Map activity DependsOn IDs to full task names
                        foreach (var dep in activity.DependsOn)
                        {
                            var depActivity = workflow.SeedActivities.FirstOrDefault(a => a.Id == dep);
                            if (depActivity is not null)
                                keywords.Add($"depends_on:{goalPrefix}00-{depActivity.Wave}-{depActivity.Id}");
                        }
                        // Store the task
                        await WritePlanningMemoryAsync(store, taskName, activity.Prompt,
                            archiveExisting: false, keywords: [.. keywords], cancellationToken);
                    }
                    catch { /* best-effort */ }
                }

                // Search backlog for related items
                string backlogSection = "";
                try
                {
                    var (backlogScope, _) = store.ParseQualifiedName("backlog:placeholder");
                    var backlogEntries = store.LoadIndex(backlogScope);
                    if (backlogEntries.Count > 0)
                    {
                        // Extract words from goal description for matching
                        var descWords = new HashSet<string>(
                            description.ToLowerInvariant()
                                .Split([' ', ',', '.', ':', ';', '-', '(', ')', '[', ']', '\n', '\r'],
                                    StringSplitOptions.RemoveEmptyEntries)
                                .Where(w => w.Length > 3),
                            StringComparer.OrdinalIgnoreCase);

                        // Score each backlog entry by keyword/description overlap
                        var scored = backlogEntries
                            .Select(e =>
                            {
                                int score = 0;
                                if (e.Keywords is not null)
                                    score += e.Keywords.Count(k => descWords.Contains(k));
                                if (!string.IsNullOrEmpty(e.Description))
                                {
                                    var entryWords = e.Description.ToLowerInvariant()
                                        .Split([' ', ',', '.', ':', ';', '-'], StringSplitOptions.RemoveEmptyEntries);
                                    score += entryWords.Count(w => w.Length > 3 && descWords.Contains(w));
                                }
                                return (Entry: e, Score: score);
                            })
                            .Where(x => x.Score > 0)
                            .OrderByDescending(x => x.Score)
                            .Take(3)
                            .ToList();

                        if (scored.Count > 0)
                        {
                            backlogSection = "Related backlog items:\n" +
                                string.Join("\n", scored.Select(x =>
                                    $"- backlog:{x.Entry.Name}: {x.Entry.Description ?? "(no description)"}")) +
                                "\n\n";
                        }
                    }
                }
                catch { /* backlog topic may not exist */ }

                var seedNames = string.Join(", ", workflow.SeedActivities.OrderBy(a => a.Wave ?? 0).Select(a => a.Id));
                string goalContent = $"Goal added as {newGoalId}: {description}.\n" +
                       $"project:context updated. Files in .scrinia/ were updated — these are your changes.\n\n" +
                       backlogSection +
                       $"Seed tasks created ({seedNames}).";
                return ResponseBuilder.Success(goalContent)
                    .WithPath($"goal:{newGoalId}")
                    .WithAction("created")
                    .WithInstruction("call task('next') to continue.")
                    .ToYaml();
            }

            case "complete":
            {
                if (string.IsNullOrWhiteSpace(goalId))
                    return ResponseBuilder.Error("'complete' action requires a goalId (e.g. 'G-1').").ToYaml();

                var (goals, originalCount, contextWithoutGoals) = ParseGoalsSection(contextText);

                // Find goal line matching goalId (case-insensitive).
                // Supports both exact match (e.g. "G-4-a3f") and short form (e.g. "G-4"
                // matches "G-4-a3f"). Short form matches by regex: [G-4(-[a-fA-F0-9]+)?]
                string searchId = goalId.Trim();
                int matchIndex = goals.FindIndex(g =>
                    g.Contains($"[{searchId}]", StringComparison.OrdinalIgnoreCase) ||
                    g.Contains($"[{searchId.ToUpperInvariant()}]", StringComparison.OrdinalIgnoreCase));

                // If exact match failed and searchId is short form (G-N), try regex match
                if (matchIndex < 0 && ShortGoalIdPattern.IsMatch(searchId))
                {
                    var shortPattern = new Regex(
                        $@"\[{Regex.Escape(searchId)}(-[a-fA-F0-9]+)?\]",
                        RegexOptions.IgnoreCase);
                    matchIndex = goals.FindIndex(g => shortPattern.IsMatch(g));

                    // If found, update searchId to the full ID from the matched line
                    if (matchIndex >= 0)
                    {
                        var fullIdMatch = BracketedGoalIdFullPattern.Match(goals[matchIndex]);
                        if (fullIdMatch.Success)
                            searchId = fullIdMatch.Groups[1].Value;
                    }
                }

                if (matchIndex < 0)
                    return ResponseBuilder.Error($"Goal '{goalId}' not found. Use memory('list', {{ path: '/goal/' }}) to see all goal IDs.").ToYaml();

                // Failsafe: actually verify criteria and check for retrospectives
                var warnings = new List<string>();
                try
                {
                    var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
                    var allTasks = store.LoadIndex(taskScope);
                    string? completeGoalId = await GetActiveGoalIdAsync(store, cancellationToken);

                    // Discover phases from task keywords
                    var phaseIds = DiscoverPhaseIds(allTasks, completeGoalId);
                    if (phaseIds.Count > 0)
                    {

                        // Check each phase for: incomplete tasks, missing verification, missing retrospective
                        // Load learn topic index for per-phase retro detection (G-29 format: learn:retro-gN-phaseId)
                        var (learnScope, _) = store.ParseQualifiedName("learn:placeholder");
                        var learnEntries = store.LoadIndex(learnScope);

                        // Extract goal number for retro file matching (e.g. "34-6d3" from "G-34-6d3")
                        string? completeGoalNum = null;
                        if (completeGoalId is not null)
                        {
                            var gm = GoalIdPattern.Match(completeGoalId);
                            if (gm.Success) completeGoalNum = gm.Groups[1].Value;
                        }

                        // Backward-compat fallback: try legacy learn:execution-outcomes
                        string? retroText = null;
                        try { retroText = await ReadMemoryAsync(store, "learn:execution-outcomes", cancellationToken); }
                        catch (FileNotFoundException) { }

                        foreach (string pid in phaseIds)
                        {
                            var phaseTasks = allTasks
                                .Where(e => HasKeyword(e, $"phase:{pid}"))
                                .Where(e => completeGoalId is null || HasKeyword(e, $"goal:{completeGoalId}"))
                                .ToList();
                            if (phaseTasks.Count == 0) continue;

                            // Incomplete tasks
                            int pending = phaseTasks.Count(e => HasKeyword(e, "status:pending"));
                            if (pending > 0)
                                warnings.Add($"phase {pid} has {pending} incomplete task(s)");

                            // Missing verification — check execution log for VERIFY record
                            string? logText = null;
                            try { logText = await ReadMemoryAsync(store, $"task:{pid}-execution-log", cancellationToken); }
                            catch (FileNotFoundException) { }

                            bool hasVerify = logText?.Contains($"VERIFY phase {pid}:", StringComparison.OrdinalIgnoreCase) == true;
                            if (!hasVerify)
                                warnings.Add($"phase {pid} has no QA verification record");

                            // Check for FAIL in verification results
                            if (hasVerify && logText!.Contains($"VERIFY phase {pid}: PARTIAL", StringComparison.OrdinalIgnoreCase))
                                warnings.Add($"phase {pid} verification had failures — check QA gate results");
                            if (hasVerify && logText!.Contains($"VERIFY phase {pid}: ALL_FAIL", StringComparison.OrdinalIgnoreCase))
                                warnings.Add($"phase {pid} verification failed — all criteria unmet");

                            // Missing retrospective — check per-phase retro files (G-29 format)
                            bool hasRetro = learnEntries.Any(e =>
                                e.Name.Contains("retro-", StringComparison.OrdinalIgnoreCase) &&
                                e.Name.Contains($"-{pid}", StringComparison.OrdinalIgnoreCase) &&
                                (completeGoalNum is null || e.Name.Contains($"g{completeGoalNum}", StringComparison.OrdinalIgnoreCase)));

                            // Backward-compat fallback: check legacy learn:execution-outcomes
                            if (!hasRetro)
                                hasRetro = retroText?.Contains($"Phase {pid} Retrospective", StringComparison.OrdinalIgnoreCase) == true;

                            if (!hasRetro)
                                warnings.Add($"phase {pid} has no retrospective (self-reflector gate)");
                        }
                    }
                }
                catch { /* workflow check is best-effort — never block goal completion */ }

                // Concern gate — block completion if open high/medium concerns
                try
                {
                    var (concernScope, _) = store.ParseQualifiedName("concern:placeholder");
                    var allConcerns = store.LoadIndex(concernScope);
                    var openHighMed = allConcerns
                        .Where(e => e.Keywords is not null &&
                            e.Keywords.Any(k => k.Equals("status:active", StringComparison.OrdinalIgnoreCase)) &&
                            e.Keywords.Any(k => k.StartsWith("severity:high", StringComparison.OrdinalIgnoreCase) ||
                                                k.StartsWith("severity:medium", StringComparison.OrdinalIgnoreCase)))
                        .ToList();

                    if (openHighMed.Count > 0)
                    {
                        var names = string.Join(", ", openHighMed.Select(e => e.Name).Take(5));
                        return ResponseBuilder.Error($"{openHighMed.Count} open high/medium concern(s) must be resolved before completing the goal: {names}.")
                            .WithInstruction("call memory('transition', { path: '/concern/...', to: 'resolved' }) with concernName, resolution, verifiedBy for each before retrying memory('transition', { path: '/goal/G-X', to: 'complete' }).")
                            .ToYaml();
                    }
                }
                catch { /* best-effort */ }

                // Extract description from the matched line
                string existingLine = goals[matchIndex];
                string goalDesc = ExtractGoalDescription(existingLine);
                string outcomeText = outcome ?? "(no outcome recorded)";

                // --- Mark goal complete ---
                string timestamp = DateTimeOffset.UtcNow.ToString("o");

                goals[matchIndex] =
                    $"- [{searchId.ToUpperInvariant()}] [complete] {goalDesc} | Outcome: {outcomeText} | Completed: {timestamp}";

                string goalsSection = BuildGoalsSection(goals, originalCount >= 0 ? originalCount : goals.Count);
                string updatedContext = contextWithoutGoals.TrimEnd() + "\n\n" + goalsSection;

                await WritePlanningMemoryAsync(store, "project:context", updatedContext,
                    archiveExisting: true, cancellationToken);

                await UpdateStateAfterGoalMutationAsync(store, $"Goal completed: {searchId}", cancellationToken);

                // Auto-append to session log (AUTO-01)
                try
                {
                    string today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
                    string sessionEntry = $"- [{searchId.ToUpperInvariant()}] {goalDesc} | Outcome: {outcomeText}";
                    await AppendToExecutionLogAsync(store, $"sessions:{today}", sessionEntry,
                        keywords: ["session", "goal-complete"], cancellationToken);
                }
                catch { /* best-effort — don't block goal completion */ }

                // Auto-create checkpoint:latest for context_resume recovery
                try
                {
                    string cpStateText;
                    try { cpStateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
                    catch (FileNotFoundException) { cpStateText = ""; }

                    string cpProjectName = ExtractStateField(cpStateText, "Project:") ?? "Unknown Project";
                    string cpProjectId = ExtractStateField(cpStateText, "ID:") ?? DeriveProjectId(store);
                    string cpPhase = ExtractStateField(cpStateText, "Phase:") ?? "Not started";
                    string? cpGoalId = await GetActiveGoalIdAsync(store, cancellationToken);
                    string cpProgress = CalculateProgress(store, cpGoalId);

                    int cpActiveConcerns = 0;
                    try
                    {
                        var (cpConcernScope, _) = store.ParseQualifiedName("concern:placeholder");
                        var cpAllConcerns = store.LoadIndex(cpConcernScope);
                        cpActiveConcerns = cpAllConcerns
                            .Count(e => e.Keywords is not null &&
                                e.Keywords.Any(k => k.Equals("status:active", StringComparison.OrdinalIgnoreCase)));
                    }
                    catch { }

                    string checkpointContent = BuildCheckpointContent(
                        cpProjectName, cpProjectId, searchId, goalDesc, outcomeText,
                        goals, originalCount, cpPhase, cpProgress,
                        cpActiveConcerns, warnings);
                    await WritePlanningMemoryAsync(store, "checkpoint:latest", checkpointContent,
                        archiveExisting: true,
                        keywords: ["checkpoint", "recovery", $"goal:{searchId}"],
                        cancellationToken);
                }
                catch { /* best-effort — don't block goal completion */ }

                string gcContent = $"Goal '{searchId}' marked complete. Outcome recorded. " +
                       $"project:context updated. Files in .scrinia/ were updated — these are your changes.\n\n" +
                       "Post-goal learning:\n" +
                       "- Run QA if not already done: memory('recall', { path: '/skill/qa' }) for structured verification\n" +
                       "- Produce a march report: memory('recall', { path: '/skill/march-reporter' }) -> write to docs/reports/ and update sessions:YYYY-MM-DD memory\n" +
                       "- Distill valuable findings into topical memories (remember) for future goals\n" +
                       "- Update or create skills (memory('remember', { path: '/skill/...' })) with lessons learned\n" +
                       "Planning artifacts (task:*, plan:*, research:*) can be cleaned up — the learnings live in your memories and skills now.";

                var gcResponse = ResponseBuilder.Success(gcContent)
                    .WithPath($"goal:{searchId}")
                    .WithAction("completed");
                if (warnings.Count > 0)
                    gcResponse = gcResponse.WithActionNeeded([.. warnings.Select(w => w).Append("Consider running QA and self-reflector gate tasks before moving on.")]);

                return gcResponse.ToYaml();
            }

            case "edit":
            {
                if (string.IsNullOrWhiteSpace(goalId))
                    return ResponseBuilder.Error("'edit' action requires a goalId.").ToYaml();
                if (string.IsNullOrWhiteSpace(description))
                    return ResponseBuilder.Error("'edit' action requires a description.").ToYaml();

                var (goals, originalCount, contextWithoutGoals) = ParseGoalsSection(contextText);

                string searchId = goalId.Trim();
                int matchIndex = goals.FindIndex(g =>
                    g.Contains($"[{searchId}]", StringComparison.OrdinalIgnoreCase) ||
                    g.Contains($"[{searchId.ToUpperInvariant()}]", StringComparison.OrdinalIgnoreCase));

                if (matchIndex < 0 && ShortGoalIdPattern.IsMatch(searchId))
                {
                    var shortPattern = new Regex(
                        $@"\[{Regex.Escape(searchId)}(-[a-fA-F0-9]+)?\]",
                        RegexOptions.IgnoreCase);
                    matchIndex = goals.FindIndex(g => shortPattern.IsMatch(g));
                    if (matchIndex >= 0)
                    {
                        var fullIdMatch = BracketedGoalIdFullPattern.Match(goals[matchIndex]);
                        if (fullIdMatch.Success)
                            searchId = fullIdMatch.Groups[1].Value;
                    }
                }

                if (matchIndex < 0)
                    return ResponseBuilder.Error($"Goal '{goalId}' not found. Use memory('list', {{ path: '/goal/' }}) to see all goals.").ToYaml();

                string oldLine = goals[matchIndex];
                string trimmed = oldLine.TrimStart('-', '*', ' ');

                // Find where description starts (after [G-N-xxx] [status])
                var statusMatch = Regex.Match(trimmed, @"\]\s*\[(active|complete)\]\s*");
                if (!statusMatch.Success)
                    return ResponseBuilder.Error($"Could not parse goal line format for '{goalId}'.").ToYaml();

                int descStart = statusMatch.Index + statusMatch.Length;
                string afterStatus = trimmed[descStart..];

                // Split off any " | Outcome:" suffix (present on completed goals)
                int outcomeSep = afterStatus.IndexOf(" | Outcome:", StringComparison.Ordinal);
                string oldDesc = outcomeSep >= 0 ? afterStatus[..outcomeSep] : afterStatus;
                string suffix = outcomeSep >= 0 ? afterStatus[outcomeSep..] : "";

                // Rebuild goal line with new description
                string prefix = trimmed[..descStart];
                goals[matchIndex] = $"- {prefix}{description}{suffix}";

                string goalsSection = BuildGoalsSection(goals, originalCount >= 0 ? originalCount : goals.Count);
                string updatedContext = contextWithoutGoals.TrimEnd() + "\n\n" + goalsSection;
                await WritePlanningMemoryAsync(store, "project:context", updatedContext,
                    archiveExisting: true, cancellationToken);

                return ResponseBuilder.Success($"Goal '{searchId}' updated.\nOld: {oldDesc.Trim()}\nNew: {description}\nproject:context updated. Files in .scrinia/ were updated — these are your changes.")
                    .WithPath($"goal:{searchId}")
                    .WithAction("updated")
                    .ToYaml();
            }

            case "list":
            {
                var (goals, originalCount, _) = ParseGoalsSection(contextText);

                if (goals.Count == 0)
                    return ResponseBuilder.Success("No structured goals found in project:context. Use memory('remember', { path: '/goal/...' }) to add goals.").WithAction("listed").ToYaml();

                int locked = originalCount >= 0 ? originalCount : goals.Count;
                int current = goals.Count;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Project Goals | Original: {locked} | Current total: {current}");
                sb.AppendLine();

                int lineNum = 0;
                foreach (string goal in goals)
                {
                    lineNum++;
                    string trimmedGoal = goal.TrimStart('-', '*', ' ');
                    // If the goal doesn't have a structured [G-N] ID, annotate as active
                    if (!GoalIdStructuredPattern.IsMatch(trimmedGoal))
                    {
                        sb.AppendLine($"[active] {trimmedGoal}");
                    }
                    else
                    {
                        sb.AppendLine(trimmedGoal);
                    }

                    if (sb.Length > MaxResponseChars - 200)
                    {
                        sb.AppendLine("[... truncated to 8KB limit]");
                        break;
                    }
                }

                return ResponseBuilder.Success(sb.ToString().TrimEnd()).WithAction("listed").ToYaml();
            }

            default:
                return ResponseBuilder.Error($"Unknown action '{action}'. Valid actions: 'add', 'edit', 'complete', 'list'.").ToYaml();
        }
    }

    /// <summary>
    /// Parses the goals section from project:context text.
    /// Returns: (goalLines, originalCount, contextWithoutGoals).
    /// originalCount is -1 if the "Original goals:" marker is not present.
    /// goalLines contains all goal lines (raw or structured) found in the goals section.
    /// contextWithoutGoals is the context text with the goals section stripped.
    /// </summary>
    internal static (List<string> Goals, int OriginalCount, string ContextWithoutGoals)
        ParseGoalsSection(string contextText)
    {
        var goals = new List<string>();
        int originalCount = -1;

        // Find goals section start: "## Goals" or "Goals:" header (case-insensitive)
        var lines = contextText.Split('\n');
        int goalsSectionStart = -1;
        int goalsSectionEnd = lines.Length;

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (goalsSectionStart < 0)
            {
                // Detect goals section header
                if (GoalsSectionPattern.IsMatch(trimmed) ||
                    GoalsSectionAltPattern.IsMatch(trimmed))
                {
                    goalsSectionStart = i;
                }
            }
            else
            {
                // Inside goals section — look for "Original goals: N" marker
                if (OriginalGoalsPattern.IsMatch(trimmed))
                {
                    var m = DigitPattern.Match(trimmed);
                    if (m.Success) originalCount = int.Parse(m.Value);
                    continue;
                }

                // Detect end of goals section: blank line followed by new non-goal content,
                // OR a new section header (## or ###)
                if (SectionHeadingPattern.IsMatch(trimmed))
                {
                    goalsSectionEnd = i;
                    break;
                }

                // Collect goal lines: lines starting with "- "
                if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                {
                    goals.Add(trimmed);
                }
            }
        }

        // Build contextWithoutGoals: remove the goals section lines
        string contextWithoutGoals;
        if (goalsSectionStart < 0)
        {
            contextWithoutGoals = contextText; // no goals section found
        }
        else
        {
            var beforeGoals = lines[..goalsSectionStart];
            var afterGoals = lines[goalsSectionEnd..];
            contextWithoutGoals = string.Join('\n', beforeGoals.Concat(afterGoals)).TrimEnd();
        }

        return (goals, originalCount, contextWithoutGoals);
    }

    /// <summary>Builds a formatted goals section string from goal lines and original count.</summary>
    private static string BuildGoalsSection(List<string> goals, int originalCount)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Goals");
        sb.AppendLine($"Original goals: {originalCount}");
        foreach (string goal in goals)
            sb.AppendLine(goal);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Builds a structured checkpoint snapshot for context_resume recovery.</summary>
    private static string BuildCheckpointContent(
        string projectName, string projectId, string goalId, string goalDescription, string outcome,
        List<string> goals, int originalCount, string currentPhase, string progressPct,
        int activeConcernCount, List<string> warnings)
    {
        int completedCount = goals.Count(g => g.Contains("[complete]", StringComparison.OrdinalIgnoreCase));
        int totalCount = goals.Count;
        string warningText = warnings.Count > 0
            ? string.Join("; ", warnings)
            : "none";

        return $"""
            ## Checkpoint — {projectName}
            **Project ID**: {projectId}
            **Completed**: {goalId} — {goalDescription}
            **Outcome**: {outcome}
            **Progress**: {progressPct}% | Phase: {currentPhase}
            **Goals**: {completedCount}/{totalCount} complete (originally {originalCount})
            **Active concerns**: {activeConcernCount}
            **Warnings**: {warningText}
            **Timestamp**: {DateTimeOffset.UtcNow:o}
            **Next steps**: Review post-goal guidance, check for new goals, run evolutionary if overdue
            """.ReplaceLineEndings("\n");
    }

    /// <summary>Extracts the description text from a goal line, stripping ID and status brackets.</summary>
    private static string ExtractGoalDescription(string goalLine)
    {
        string stripped = goalLine.TrimStart('-', '*', ' ');
        // Remove leading [G-N] and [status] brackets if present
        stripped = GoalStatusPrefixPattern.Replace(stripped, "").Trim();
        // Also strip trailing " | Outcome: ..." sections from previously completed lines
        int pipeIdx = stripped.IndexOf(" | Outcome:", StringComparison.OrdinalIgnoreCase);
        if (pipeIdx >= 0) stripped = stripped[..pipeIdx];
        return stripped.Trim();
    }

    /// <summary>Updates project:state after a goal mutation, preserving existing state fields.</summary>
    private static async Task UpdateStateAfterGoalMutationAsync(
        IMemoryStore store, string lastAction, CancellationToken ct)
    {
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", ct); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string phase = ExtractStateField(stateText, "Phase:") ?? "Not started";
        string? mutGoalId = await GetActiveGoalIdAsync(store, ct);
        string progressPct = CalculateProgress(store, mutGoalId);

        await WriteStateAsync(store, projectName, projectId,
            phase: phase,
            progressPct: progressPct,
            lastAction: lastAction,
            blockers: "none",
            nextStep: "run memory('list', { path: '/goal/' }) to see all goals with status",
            ct);
    }

    // -- Built-in specialist scaffolds (AGENT-04) --------------------------------
    // Loaded from embedded resources: prompts/scaffolds/{name}.md

    private static readonly Lazy<string> _researcherScaffold = new(() =>
        EmbeddedPrompts.LoadScaffold("researcher")
        ?? throw new InvalidOperationException("Built-in researcher scaffold not found"));

    private static readonly Lazy<string> _reviewerScaffold = new(() =>
        EmbeddedPrompts.LoadScaffold("reviewer")
        ?? throw new InvalidOperationException("Built-in reviewer scaffold not found"));

    private static readonly Lazy<string> _domainExpertScaffold = new(() =>
        EmbeddedPrompts.LoadScaffold("domain-expert")
        ?? throw new InvalidOperationException("Built-in domain-expert scaffold not found"));

    private static string ResearcherScaffold => _researcherScaffold.Value;
    private static string ReviewerScaffold => _reviewerScaffold.Value;
    private static string DomainExpertScaffold => _domainExpertScaffold.Value;

    // -- Subagent creation tools (AGENT-01, AGENT-02, AGENT-03, AGENT-04) -------

    /// <summary>Create a reusable specialist skill prompt and store as skill:* memory.</summary>
    internal async Task<string> SkillCreate(
        [Description("Skill name slug (e.g. 'api-reviewer', 'auth-researcher').")] string name,
        [Description("Built-in scaffold: researcher, reviewer, domain-expert, or custom.")] string scaffold,
        [Description("Additional context or instructions to embed in the prompt.")] string? instructions = null,
        [Description("Comma-separated tool names the agent should use (for custom scaffold).")] string? tools = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Prerequisite check: project:context must exist
        try
        {
            await ReadMemoryAsync(store, "project:context", cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return ResponseBuilder.Error("No project initialized. Run project_init first.").ToYaml();
        }

        // Select prompt template based on scaffold (case-insensitive)
        string promptContent;
        string role;

        string scaffoldLower = scaffold.Trim().ToLowerInvariant();
        switch (scaffoldLower)
        {
            case "researcher":
                promptContent = ResearcherScaffold;
                role = "researcher";
                if (!string.IsNullOrWhiteSpace(instructions))
                    promptContent += $"\n## Additional Instructions\n{instructions}\n";
                break;

            case "reviewer":
                promptContent = ReviewerScaffold;
                role = "reviewer";
                if (!string.IsNullOrWhiteSpace(instructions))
                    promptContent += $"\n## Additional Instructions\n{instructions}\n";
                break;

            case "domain-expert":
                promptContent = DomainExpertScaffold;
                role = "domain-expert";
                if (!string.IsNullOrWhiteSpace(instructions))
                    promptContent += $"\n## Additional Instructions\n{instructions}\n";
                break;

            default:
                // Custom scaffold: build from instructions/tools parameters
                role = "custom";
                string toolSection = "";
                if (!string.IsNullOrWhiteSpace(tools))
                {
                    var toolList = tools
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(t => $"- {t}: use as needed");
                    toolSection =
                        "## Tools Available (if Scrinia MCP is active)\n" +
                        string.Join("\n", toolList) + "\n\n";
                }

                string instructionsSection = string.IsNullOrWhiteSpace(instructions)
                    ? "(no custom instructions provided)"
                    : instructions;

                promptContent =
                    $"## Role: Custom Specialist\n" +
                    toolSection +
                    $"## Instructions\n" +
                    $"{instructionsSection}\n\n" +
                    $"## Fallback Instructions (if Scrinia MCP is not available)\n" +
                    $"Organize findings in markdown. Use standard file operations to persist results.\n";
                break;
        }

        // Build capability list for keywords
        string capabilityList = string.IsNullOrWhiteSpace(tools) ? scaffoldLower : tools;

        // Compute basedOn hash if this skill overrides a built-in
        string? basedOnHash = null;
        if (BuiltInSkills.TryGetValue(name, out string? builtInText))
        {
            basedOnHash = Convert.ToHexStringLower(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builtInText)));
        }

        // Write to disk (.scrinia/skills/{name}.md)
        string baseDir = GetScriniaBaseDir(store);
        string skillsDir = Path.Combine(baseDir, "skills");
        string filePath = Path.Combine(skillsDir, $"{name}.md");
        Directory.CreateDirectory(skillsDir);

        // Archive previous version if file exists
        ArchiveFileVersion(filePath, Path.Combine(skillsDir, "versions"));

        // Write skill content as plain markdown
        await File.WriteAllTextAsync(filePath, promptContent, cancellationToken);

        // Write sidecar metadata
        string now = DateTimeOffset.UtcNow.ToString("o");
        var existingMeta = ReadSidecarMeta(filePath, PlanningJsonContext.Default.SkillFileMeta);
        string[]? capabilities = string.IsNullOrWhiteSpace(tools) ? null
            : tools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var meta = new SkillFileMeta(
            BasedOn: basedOnHash,
            Role: role,
            Capabilities: capabilities,
            Scaffold: scaffoldLower,
            CreatedAt: existingMeta?.CreatedAt ?? now,
            UpdatedAt: now);
        WriteSidecarMeta(filePath, meta, PlanningJsonContext.Default.SkillFileMeta);

        // MF-C01: check for legacy NMP/2 entry, log migration note if found
        string qualifiedName = $"skill:{name}";
        string migrationNote = "";
        try
        {
            await ReadMemoryAsync(store, qualifiedName, cancellationToken);
            migrationNote = $" Note: a legacy NMP/2 entry for {qualifiedName} still exists — it will be used as fallback but the disk file takes precedence.";
        }
        catch { /* no legacy entry — nothing to note */ }

        // Update project:state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? "Not started";
        string? skillGoalId = await GetActiveGoalIdAsync(store, cancellationToken);
        string progressPct = CalculateProgress(store, skillGoalId);

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: progressPct,
            lastAction: $"Skill created: {qualifiedName} (role:{role})",
            blockers: "none",
            nextStep: "use memory('recall', { path: '/skill/' }) to retrieve stored skills",
            cancellationToken);

        var scResponse = ResponseBuilder.Success($"Stored as .scrinia/skills/{name}.md. Files in .scrinia/ were updated -- these are your changes.")
            .WithPath(qualifiedName)
            .WithAction("created");
        if (!string.IsNullOrEmpty(migrationNote))
            scResponse = scResponse.WithInfo(migrationNote.TrimStart());
        return scResponse.ToYaml();
    }

    /// <summary>List or load stored specialist skills.</summary>
    internal Task<string> SkillLoad(
        [Description("Skill name to load (e.g. 'api-reviewer'). Omit to list all skills.")] string? name = null,
        [Description("Set to true to show both built-in and override for reconciliation.")] bool reconcile = false,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        if (string.IsNullOrWhiteSpace(name))
        {
            // List mode: scan disk files, NMP/2 index, and built-in dictionary

            // 1. Disk files (.scrinia/skills/*.md)
            string baseDir = GetScriniaBaseDir(store);
            string skillsDir = Path.Combine(baseDir, "skills");
            var diskNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var diskMetas = new Dictionary<string, SkillFileMeta?>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(skillsDir))
            {
                foreach (string mdFile in Directory.GetFiles(skillsDir, "*.md"))
                {
                    string diskName = Path.GetFileNameWithoutExtension(mdFile);
                    diskNames.Add(diskName);
                    diskMetas[diskName] = ReadSidecarMeta(mdFile, PlanningJsonContext.Default.SkillFileMeta);
                }
            }

            // 2. NMP/2 index entries (legacy)
            var (scope, _) = store.ParseQualifiedName("skill:placeholder");
            IReadOnlyList<ArtifactEntry> entries;
            try { entries = store.LoadIndex(scope); }
            catch { entries = []; }
            var nmpNames = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);

            // 3. Merge: collect all unique names across all three sources
            var allNames = new HashSet<string>(BuiltInSkills.Keys, StringComparer.OrdinalIgnoreCase);
            allNames.UnionWith(diskNames);
            allNames.UnionWith(nmpNames);

            if (allNames.Count == 0)
                return Task.FromResult(ResponseBuilder.Success("No skills available.").WithAction("listed").ToYaml());

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Available skills ({allNames.Count}):");
            sb.AppendLine();

            // Built-in skills first (show source label based on override presence)
            foreach (string skillKey in BuiltInSkills.Keys)
            {
                if (diskNames.Contains(skillKey))
                {
                    // Disk file overrides built-in — check staleness via sidecar
                    string tag = "file";
                    if (diskMetas.TryGetValue(skillKey, out var meta) && meta?.BasedOn is not null)
                    {
                        string currentHash = Convert.ToHexStringLower(
                            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(BuiltInSkills[skillKey])));
                        if (!meta.BasedOn.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
                            tag = "stale base";
                    }
                    sb.AppendLine($"- skill:{skillKey} [{tag}]");
                }
                else if (nmpNames.Contains(skillKey))
                {
                    // NMP/2 override (legacy) — check staleness via keywords
                    var overrideEntry = entries.FirstOrDefault(e => e.Name.Equals(skillKey, StringComparison.OrdinalIgnoreCase));
                    string tag = "override";
                    if (overrideEntry?.Keywords is not null)
                    {
                        var basedOnKw = overrideEntry.Keywords.FirstOrDefault(k => k.StartsWith("basedOn:", StringComparison.Ordinal));
                        if (basedOnKw is not null)
                        {
                            string storedHash = basedOnKw["basedOn:".Length..];
                            string currentHash = Convert.ToHexStringLower(
                                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(BuiltInSkills[skillKey])));
                            if (!storedHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
                                tag = "stale base";
                        }
                    }
                    sb.AppendLine($"- skill:{skillKey} [{tag}]");
                }
                else
                {
                    sb.AppendLine($"- skill:{skillKey} [built-in]");
                }
            }

            // Non-built-in skills: disk files first, then NMP/2-only
            foreach (string diskName in diskNames)
            {
                if (BuiltInSkills.ContainsKey(diskName))
                    continue; // already listed above

                string roleTag = diskMetas.TryGetValue(diskName, out var fileMeta) && fileMeta?.Role is not null
                    ? $"role:{fileMeta.Role}"
                    : "role:unknown";
                sb.AppendLine($"- skill:{diskName} [file] [{roleTag}]");

                if (sb.Length > MaxResponseChars - 200)
                {
                    sb.AppendLine("[... truncated to 8KB limit]");
                    break;
                }
            }

            foreach (var entry in entries)
            {
                if (BuiltInSkills.ContainsKey(entry.Name) || diskNames.Contains(entry.Name))
                    continue; // already listed above (built-in or disk takes precedence)

                string roleKw = entry.Keywords?
                    .FirstOrDefault(k => k.StartsWith("role:", StringComparison.OrdinalIgnoreCase))
                    ?? "role:unknown";

                sb.AppendLine($"- skill:{entry.Name} [override] [{roleKw}]");

                if (sb.Length > MaxResponseChars - 200)
                {
                    sb.AppendLine("[... truncated to 8KB limit]");
                    break;
                }
            }

            return Task.FromResult(ResponseBuilder.Success(sb.ToString().TrimEnd()).WithAction("listed").ToYaml());
        }

        // Load mode: async artifact read
        return LoadSkillAsync(store, name, reconcile, cancellationToken);
    }

    private static async Task<string> LoadSkillAsync(
        IMemoryStore store, string skillName, bool reconcile, CancellationToken ct)
    {
        // 1. Disk file (.scrinia/skills/{name}.md)
        string baseDir = GetScriniaBaseDir(store);
        string filePath = Path.Combine(baseDir, "skills", $"{skillName}.md");
        string? diskContent = null;
        if (File.Exists(filePath))
        {
            diskContent = await File.ReadAllTextAsync(filePath, ct);
        }

        // 2. NMP/2 fallback (legacy)
        string? nmpContent = null;
        if (diskContent is null)
        {
            try
            {
                nmpContent = await ReadMemoryAsync(store, $"skill:{skillName}", ct);
            }
            catch (FileNotFoundException)
            {
                // No NMP/2 override exists
            }
        }

        // Determine the override content (disk > NMP/2) and its source label
        string? overrideContent = diskContent ?? nmpContent;
        string sourceLabel = diskContent is not null ? "file" : "project override";

        // Reconcile mode: show both built-in and override side by side
        if (reconcile && overrideContent is not null && BuiltInSkills.TryGetValue(skillName, out string? reconBuiltIn))
        {
            string reconContent = $"## Current Built-in\n{reconBuiltIn}\n\n" +
                $"## Your Project Override ({sourceLabel})\n{overrideContent}";
            return ResponseBuilder.Success(reconContent)
                .WithPath($"skill:{skillName}")
                .WithAction("loaded")
                .WithInstruction("Merge your project-specific additions with the updated built-in base, then call memory('remember', { path: '/skill/...' }) to save the reconciled version.")
                .ToYaml();
        }

        if (overrideContent is null)
        {
            // Fall back to built-in skills
            if (BuiltInSkills.TryGetValue(skillName, out string? builtIn))
                return ResponseBuilder.Success(builtIn).WithPath($"skill:{skillName}").WithAction("loaded").WithInfo("Loaded from built-in").ToYaml();
            return ResponseBuilder.Error($"Skill '{skillName}' not found. Use memory('recall', {{ path: '/skill/' }}) to list available skills.").ToYaml();
        }

        var slWarnings = new List<string>();

        // Check for stale base — warn if the built-in has changed since this override was created
        if (BuiltInSkills.TryGetValue(skillName, out string? currentBuiltIn))
        {
            string? storedHash = null;

            // Read basedOn hash from sidecar metadata (disk file)
            if (diskContent is not null)
            {
                var meta = ReadSidecarMeta(filePath, PlanningJsonContext.Default.SkillFileMeta);
                storedHash = meta?.BasedOn;
            }
            else if (nmpContent is not null)
            {
                // Fall back to NMP/2 keyword-based basedOn
                var (scope, subject) = store.ParseQualifiedName($"skill:{skillName}");
                var entries = store.LoadIndex(scope);
                var entry = entries.FirstOrDefault(e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
                if (entry?.Keywords is not null)
                {
                    var basedOnKw = entry.Keywords.FirstOrDefault(k => k.StartsWith("basedOn:", StringComparison.Ordinal));
                    if (basedOnKw is not null)
                        storedHash = basedOnKw["basedOn:".Length..];
                }
            }

            if (storedHash is not null)
            {
                string currentHash = Convert.ToHexStringLower(
                    SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(currentBuiltIn)));
                if (!storedHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
                {
                    slWarnings.Add($"built-in skill has changed since this override was created. Review with memory('recall', {{ path: '/skill/{skillName}', reconcile: true }})");
                }
            }
        }

        var slResponse = ResponseBuilder.Success(overrideContent)
            .WithPath($"skill:{skillName}")
            .WithAction("loaded")
            .WithInfo($"Loaded from {sourceLabel}");
        if (slWarnings.Count > 0)
            slResponse = slResponse.WithActionNeeded([.. slWarnings]);
        return slResponse.ToYaml();
    }

    private static readonly Lazy<IReadOnlyDictionary<string, string>> _builtInSkills =
        new(() => EmbeddedPrompts.LoadAllSkills());

    private static IReadOnlyDictionary<string, string> BuiltInSkills => _builtInSkills.Value;

    private sealed record ParsedTask(string Id, string[] DependsOn, string Content, string[]? Files = null);

    /// <summary>
    /// Parses free-text task input into structured task records.
    /// Each task section starts with "## Task {id}" and contains Wave, Depends on, Action, and Acceptance criteria fields.
    /// </summary>
    private static List<ParsedTask> ParseTaskSections(string tasks)
    {
        var result = new List<ParsedTask>();
        // Split by task section headers: ## Task XX or ## Task XX (anything)
        var headerMatches = TaskHeaderPattern.Matches(tasks);

        for (int i = 0; i < headerMatches.Count; i++)
        {
            Match header = headerMatches[i];
            string taskId = header.Groups[1].Value.TrimStart('0');
            if (taskId.Length == 0) taskId = "0";
            // Pad to 2 digits
            if (int.TryParse(taskId, out int taskIdNum))
                taskId = taskIdNum.ToString("D2");

            // Extract the section content between this header and the next
            int sectionStart = header.Index + header.Length;
            int sectionEnd = i + 1 < headerMatches.Count
                ? headerMatches[i + 1].Index
                : tasks.Length;
            string section = tasks[sectionStart..sectionEnd];

            // Parse Depends on
            string[] dependsOn = [];
            var depsMatch = DependsOnPattern.Match(section);
            if (depsMatch.Success)
            {
                string depsValue = depsMatch.Groups[1].Value.Trim();
                if (!string.Equals(depsValue, "none", StringComparison.OrdinalIgnoreCase))
                {
                    dependsOn = depsValue
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToArray();
                }
            }

            // Parse Files
            string[]? files = null;
            var filesMatch = FilesFieldPattern.Match(section);
            if (filesMatch.Success)
            {
                files = filesMatch.Groups[1].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToArray();
            }

            // Build content: Action + Acceptance criteria (everything except Depends on / Files lines)
            var contentLines = section.Split('\n')
                .Where(line => !DependsOnPattern.IsMatch(line.Trim()))
                .Where(line => !FilesFieldPattern.IsMatch(line.Trim()))
                .ToList();

            // Trim leading/trailing blank lines from content
            while (contentLines.Count > 0 && string.IsNullOrWhiteSpace(contentLines[0]))
                contentLines.RemoveAt(0);
            while (contentLines.Count > 0 && string.IsNullOrWhiteSpace(contentLines[^1]))
                contentLines.RemoveAt(contentLines.Count - 1);

            string content = string.Join('\n', contentLines).Trim();
            if (string.IsNullOrWhiteSpace(content))
                content = "(no action specified)";

            result.Add(new ParsedTask(taskId, dependsOn, content, files));
        }

        return result;
    }

    // ── Merge infrastructure scaffolding ─────────────────────────────────────

    private static void ScaffoldMergeInfrastructure(string scriniaDir)
    {
        // .gitattributes
        string gitattributesPath = Path.Combine(scriniaDir, ".gitattributes");
        if (!File.Exists(gitattributesPath))
        {
            File.WriteAllText(gitattributesPath,
                "# Scrinia memory merge configuration\n" +
                "*.nmp2 binary\n" +
                "*.meta.json merge=scrinia-meta\n");
        }

        // hooks directory
        string hooksDir = Path.Combine(scriniaDir, "hooks");
        Directory.CreateDirectory(hooksDir);

        // Merge driver - bash
        string bashDriver = Path.Combine(hooksDir, "scrinia-merge-meta.sh");
        if (!File.Exists(bashDriver))
        {
            File.WriteAllText(bashDriver, GetBashMergeDriverContent());
        }

        // Merge driver - PowerShell
        string psDriver = Path.Combine(hooksDir, "scrinia-merge-meta.ps1");
        if (!File.Exists(psDriver))
        {
            File.WriteAllText(psDriver, GetPowerShellMergeDriverContent());
        }

        // Post-merge hook
        string postMerge = Path.Combine(hooksDir, "post-merge");
        if (!File.Exists(postMerge))
        {
            File.WriteAllText(postMerge, GetPostMergeHookContent());
        }
    }

    private static string GetBashMergeDriverContent() =>
        """
        #!/usr/bin/env bash
        # scrinia .meta.json merge driver
        # Unions keywords, takes latest updatedAt, max termFrequencies
        # Usage: git config merge.scrinia-meta.driver ".scrinia/hooks/scrinia-merge-meta.sh %O %A %B"

        set -euo pipefail

        ANCESTOR="$1"  # %O — common ancestor
        OURS="$2"      # %A — our version (result written here)
        THEIRS="$3"    # %B — their version

        # Requires jq for JSON processing
        if ! command -v jq &>/dev/null; then
            echo "scrinia merge driver: jq not found, falling back to git merge" >&2
            exit 1
        fi

        # Union keywords from both sides (sorted, unique)
        OURS_KW=$(jq -r '.keywords // [] | .[]' "$OURS" 2>/dev/null | sort -fu)
        THEIRS_KW=$(jq -r '.keywords // [] | .[]' "$THEIRS" 2>/dev/null | sort -fu)
        MERGED_KW=$(echo -e "${OURS_KW}\n${THEIRS_KW}" | sort -fu | grep -v '^$')

        # Pick base: latest updatedAt wins
        OURS_TS=$(jq -r '.updatedAt // .createdAt // ""' "$OURS" 2>/dev/null)
        THEIRS_TS=$(jq -r '.updatedAt // .createdAt // ""' "$THEIRS" 2>/dev/null)

        if [[ "$THEIRS_TS" > "$OURS_TS" ]]; then
            BASE="$THEIRS"
        else
            BASE="$OURS"
        fi

        # Build merged keywords as JSON array
        KW_JSON=$(echo "$MERGED_KW" | jq -R -s 'split("\n") | map(select(length > 0))')

        # Merge termFrequencies: take max value for each key
        TF_MERGED=$(jq -s '
          .[0].termFrequencies // {} | to_entries | map({key: .key, value: .value}) as $a |
          .[1].termFrequencies // {} | to_entries | map({key: .key, value: .value}) as $b |
          ($a + $b) | group_by(.key) | map({key: .[0].key, value: ([.[].value] | max)}) |
          from_entries
        ' "$OURS" "$THEIRS" 2>/dev/null || echo '{}')

        # Write result to OURS path (git expects result there)
        jq --argjson kw "$KW_JSON" --argjson tf "$TF_MERGED" \
          '.keywords = $kw | .termFrequencies = $tf' "$BASE" > "${OURS}.tmp" && mv "${OURS}.tmp" "$OURS"

        exit 0
        """;

    private static string GetPowerShellMergeDriverContent() =>
        """
        #!/usr/bin/env pwsh
        # scrinia .meta.json merge driver (PowerShell)
        # Usage: git config merge.scrinia-meta.driver "pwsh .scrinia/hooks/scrinia-merge-meta.ps1 %O %A %B"

        param(
            [string]$Ancestor,  # %O
            [string]$Ours,      # %A — result written here
            [string]$Theirs     # %B
        )

        try {
            $oursJson = Get-Content $Ours -Raw | ConvertFrom-Json
            $theirsJson = Get-Content $Theirs -Raw | ConvertFrom-Json

            # Pick base: latest updatedAt
            $oursTs = if ($oursJson.updatedAt) { [DateTimeOffset]::Parse($oursJson.updatedAt) } else { [DateTimeOffset]::MinValue }
            $theirsTs = if ($theirsJson.updatedAt) { [DateTimeOffset]::Parse($theirsJson.updatedAt) } else { [DateTimeOffset]::MinValue }

            $base = if ($theirsTs -gt $oursTs) { $theirsJson } else { $oursJson }
            $other = if ($theirsTs -gt $oursTs) { $oursJson } else { $theirsJson }

            # Union keywords (sorted, case-insensitive unique)
            $allKw = @()
            if ($oursJson.keywords) { $allKw += $oursJson.keywords }
            if ($theirsJson.keywords) { $allKw += $theirsJson.keywords }
            $base.keywords = $allKw | Sort-Object -Unique

            # Merge termFrequencies (max for shared keys)
            if ($other.termFrequencies) {
                $baseTf = @{}
                if ($base.termFrequencies) {
                    $base.termFrequencies.PSObject.Properties | ForEach-Object { $baseTf[$_.Name] = $_.Value }
                }
                $other.termFrequencies.PSObject.Properties | ForEach-Object {
                    if ($baseTf.ContainsKey($_.Name)) {
                        $baseTf[$_.Name] = [Math]::Max($baseTf[$_.Name], $_.Value)
                    } else {
                        $baseTf[$_.Name] = $_.Value
                    }
                }
                $base.termFrequencies = [PSCustomObject]$baseTf
            }

            # Write result
            $base | ConvertTo-Json -Depth 10 | Set-Content $Ours -Encoding UTF8
            exit 0
        }
        catch {
            Write-Error "scrinia merge driver failed: $_"
            exit 1
        }
        """;

    private static string GetPostMergeHookContent() =>
        """
        #!/usr/bin/env bash
        # scrinia post-merge hook
        # Scans .scrinia/ for unresolved merge conflicts after git merge/pull.
        #
        # Installation:
        #   cp .scrinia/hooks/post-merge .git/hooks/post-merge
        #   chmod +x .git/hooks/post-merge
        #
        # Or with symlink (updates automatically):
        #   ln -s ../../.scrinia/hooks/post-merge .git/hooks/post-merge

        # Check for conflict markers in .scrinia/ files
        if grep -r -l "<<<<<<< " .scrinia/ 2>/dev/null | head -1 > /dev/null 2>&1; then
            CONFLICTED=$(grep -r -l "<<<<<<< " .scrinia/ 2>/dev/null | wc -l)
            echo ""
            echo "⚠  scrinia: $CONFLICTED file(s) in .scrinia/ have unresolved merge conflicts."
            echo "   Run memory('reconcile') in your next agent session to resolve them."
            echo "   Files with conflicts:"
            grep -r -l "<<<<<<< " .scrinia/ 2>/dev/null | sed 's/^/     /'
            echo ""
        fi
        """;

    // ── Staleness / Drift scanning helpers ───────────────────────────────────

    /// <summary>
    /// Scans all memory entries for staleness indicators.
    /// Returns count of date-stale entries (ReviewAfter in the past) and
    /// conditional-review entries (ReviewWhen set but not already date-stale).
    /// Index-only scan — does not decode artifacts.
    /// </summary>
    internal static (int StaleCount, int ReviewCount) ScanStaleness(IMemoryStore store)
    {
        var allEntries = store.ListScoped(null);
        int staleCount = 0;
        int reviewCount = 0;

        foreach (var sa in allEntries)
        {
            bool isDateStale = sa.Entry.ReviewAfter.HasValue
                && sa.Entry.ReviewAfter.Value <= DateTimeOffset.UtcNow;

            if (isDateStale)
                staleCount++;
            else if (!string.IsNullOrEmpty(sa.Entry.ReviewWhen))
                reviewCount++;
        }

        return (staleCount, reviewCount);
    }

    /// <summary>
    /// Scans all memory entries with CodeRefs for drift (hash mismatch) or missing files.
    /// Index-only scan — does not decode artifacts.
    /// </summary>
    internal static (int DriftCount, int MissingCount) ScanDrift(IMemoryStore store)
    {
        string storeDir = store.GetStoreDirForScope("local");
        string scriniaDir = Path.GetDirectoryName(storeDir) ?? storeDir;
        string workspaceRoot = Path.GetDirectoryName(scriniaDir) ?? scriniaDir;

        var allEntries = store.ListScoped(null);
        int driftCount = 0, missingCount = 0;

        foreach (var sa in allEntries)
        {
            if (sa.Entry.CodeRefs is null or { Count: 0 }) continue;

            foreach (var (path, storedHash) in sa.Entry.CodeRefs)
            {
                var fullPath = ResolveDriftPath(workspaceRoot, path);
                if (fullPath is null || !File.Exists(fullPath))
                {
                    missingCount++;
                }
                else
                {
                    var currentHash = ComputeDriftHash(fullPath);
                    if (currentHash is null || !currentHash.Equals(storedHash, StringComparison.OrdinalIgnoreCase))
                        driftCount++;
                }
            }
        }

        return (driftCount, missingCount);
    }

    /// <summary>Resolves a relative path against the workspace root, ensuring it stays within bounds.</summary>
    private static string? ResolveDriftPath(string workspaceRoot, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath.Trim()));
        return fullPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }

    /// <summary>Computes SHA-256 hex hash of a file for drift detection.</summary>
    private static string? ComputeDriftHash(string fullPath)
    {
        try { return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fullPath))); }
        catch { return null; }
    }

    // ── File entity computed view ────────────────────────────────────────────

    /// <summary>Normalizes a file path for comparison: backslash → forward-slash, strip leading ./</summary>
    internal static string NormalizeFilePath(string path) =>
        path.Replace('\\', '/').TrimStart('.', '/');

    /// <summary>Computes drift status for a single code-ref entry.</summary>
    private static string ComputeRefStatus(string workspaceRoot, string refPath, string storedHash)
    {
        var fullPath = ResolveDriftPath(workspaceRoot, refPath);
        if (fullPath is null || !File.Exists(fullPath))
            return "MISSING";
        var currentHash = ComputeDriftHash(fullPath);
        if (currentHash is null || !currentHash.Equals(storedHash, StringComparison.OrdinalIgnoreCase))
            return "DRIFT";
        return "OK";
    }

    /// <summary>Formats a qualified name from a ScopedArtifact (e.g. "arch:example-notes").</summary>
    private static string FormatQualName(IMemoryStore store, ScopedArtifact sa) =>
        store.FormatQualifiedName(sa.Scope, sa.Entry.Name);

    /// <summary>Show all memories referencing a file path (file entity view).</summary>
    private string FileShow(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return ResponseBuilder.Error("File show requires 'id' parameter with a file path.").ToYaml();

        var store = CurrentStore;
        string storeDir = store.GetStoreDirForScope("local");
        string scriniaDir = Path.GetDirectoryName(storeDir) ?? storeDir;
        string workspaceRoot = Path.GetDirectoryName(scriniaDir) ?? scriniaDir;

        string normalizedId = NormalizeFilePath(id);
        var allEntries = store.ListScoped(null);
        var matches = new List<(string QualName, string Description, string Status)>();
        string? overallStatus = null;

        foreach (var sa in allEntries)
        {
            if (sa.Entry.CodeRefs is null or { Count: 0 }) continue;

            foreach (var (path, storedHash) in sa.Entry.CodeRefs)
            {
                string normalizedPath = NormalizeFilePath(path);
                if (!normalizedPath.Contains(normalizedId, StringComparison.OrdinalIgnoreCase))
                    continue;

                string status = ComputeRefStatus(workspaceRoot, path, storedHash);
                string qualName = FormatQualName(store, sa);
                matches.Add((qualName, sa.Entry.Description, status));

                // Track worst status for the header
                if (overallStatus is null || status == "MISSING" ||
                    (status == "DRIFT" && overallStatus == "OK"))
                    overallStatus = status;
            }
        }

        if (matches.Count == 0)
            return ResponseBuilder.Success($"No memories reference file '{id}'.").WithAction("shown").ToYaml();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"file: {normalizedId}");
        sb.AppendLine($"Status: {overallStatus}");
        sb.AppendLine();
        sb.AppendLine("Referenced by:");
        foreach (var (qualName, desc, status) in matches)
        {
            string descPart = !string.IsNullOrWhiteSpace(desc) ? $" — \"{Truncate(desc, 80)}\"" : "";
            sb.AppendLine($"  {qualName}{descPart} [{status}]");
        }

        return ResponseBuilder.Success(sb.ToString().TrimEnd()).WithPath($"file:{normalizedId}").WithAction("shown").ToYaml();
    }

    /// <summary>List all files tracked via codeRefs across all memories (file entity view).</summary>
    private string FileList(string? query)
    {
        var store = CurrentStore;
        string storeDir = store.GetStoreDirForScope("local");
        string scriniaDir = Path.GetDirectoryName(storeDir) ?? storeDir;
        string workspaceRoot = Path.GetDirectoryName(scriniaDir) ?? scriniaDir;

        string? normalizedQuery = !string.IsNullOrWhiteSpace(query)
            ? NormalizeFilePath(query) : null;

        var allEntries = store.ListScoped(null);

        // Build inverted index: file path → list of (qualifiedName, status)
        var index = new Dictionary<string, List<(string QualName, string Status)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var sa in allEntries)
        {
            if (sa.Entry.CodeRefs is null or { Count: 0 }) continue;

            string qualName = FormatQualName(store, sa);

            foreach (var (path, storedHash) in sa.Entry.CodeRefs)
            {
                string normalizedPath = NormalizeFilePath(path);

                if (normalizedQuery is not null &&
                    !normalizedPath.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!index.TryGetValue(normalizedPath, out var list))
                {
                    list = [];
                    index[normalizedPath] = list;
                }

                string status = ComputeRefStatus(workspaceRoot, path, storedHash);
                list.Add((qualName, status));
            }
        }

        if (index.Count == 0)
        {
            string emptyMsg = normalizedQuery is not null
                ? $"No file references match '{query}'."
                : "No memories have code references. Use codeRefs parameter on memory('remember') to track file dependencies.";
            return ResponseBuilder.Success(emptyMsg).WithAction("listed").ToYaml();
        }

        int totalRefs = index.Values.Sum(v => v.Count);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"File references ({index.Count} file{(index.Count == 1 ? "" : "s")}, {totalRefs} memor{(totalRefs == 1 ? "y" : "ies")}):");
        sb.AppendLine();

        foreach (var (filePath, refs) in index.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            int okCount = refs.Count(r => r.Status == "OK");
            int driftCount = refs.Count(r => r.Status == "DRIFT");
            int missingCount = refs.Count(r => r.Status == "MISSING");

            var statusParts = new List<string>();
            if (okCount > 0) statusParts.Add($"{okCount} OK");
            if (driftCount > 0) statusParts.Add($"{driftCount} DRIFT");
            if (missingCount > 0) statusParts.Add($"{missingCount} MISSING");

            sb.AppendLine($"  {filePath} — {refs.Count} ref{(refs.Count == 1 ? "" : "s")} [{string.Join(", ", statusParts)}]");

            if (sb.Length > MaxResponseChars - 200) // leave room for truncation notice
            {
                sb.AppendLine("  [... truncated]");
                break;
            }
        }

        return ResponseBuilder.Success(sb.ToString().TrimEnd()).WithAction("listed").ToYaml();
    }

    /// <summary>Truncates a description string to the given max length.</summary>
    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 3)] + "...";
    }

    // ── File-based meta-entity I/O helpers ──────────────────────────────────

    /// <summary>
    /// Derives the <c>.scrinia/</c> root directory from the memory store by walking
    /// up from the local scope directory until a directory named ".scrinia" is found.
    /// </summary>
    internal static string GetScriniaBaseDir(IMemoryStore store)
    {
        string storeDir = store.GetStoreDirForScope("local");
        // storeDir is like /path/.scrinia/topics/local/ or /path/.scrinia/topics/
        // Navigate up to find .scrinia/
        var dir = new DirectoryInfo(storeDir);
        while (dir is not null && dir.Name != ".scrinia")
            dir = dir.Parent;
        return dir?.FullName ?? Path.GetDirectoryName(storeDir) ?? storeDir;
    }

    /// <summary>
    /// Archives an existing file into <paramref name="versionsDir"/> with a UTC
    /// timestamp suffix before it gets overwritten. No-op if the file does not exist.
    /// </summary>
    internal static void ArchiveFileVersion(string filePath, string versionsDir)
    {
        if (!File.Exists(filePath)) return;
        Directory.CreateDirectory(versionsDir);
        string name = Path.GetFileNameWithoutExtension(filePath);
        string ext = Path.GetExtension(filePath);
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        File.Copy(filePath, Path.Combine(versionsDir, $"{name}_{timestamp}{ext}"));
    }

    /// <summary>
    /// Reads a JSON sidecar metadata file (<c>.meta.json</c>) next to the given file path.
    /// Returns <c>null</c> if the sidecar does not exist.
    /// </summary>
    internal static T? ReadSidecarMeta<T>(string filePath, JsonTypeInfo<T> typeInfo) where T : class
    {
        string metaPath = Path.ChangeExtension(filePath, ".meta.json");
        if (!File.Exists(metaPath)) return null;
        try
        {
            string json = File.ReadAllText(metaPath);
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException)
        {
            // Corrupted sidecar — treat as absent rather than crashing
            return null;
        }
    }

    /// <summary>
    /// Writes a JSON sidecar metadata file (<c>.meta.json</c>) next to the given file path.
    /// </summary>
    internal static void WriteSidecarMeta<T>(string filePath, T meta, JsonTypeInfo<T> typeInfo)
    {
        string metaPath = Path.ChangeExtension(filePath, ".meta.json");
        string json = JsonSerializer.Serialize(meta, typeInfo);
        File.WriteAllText(metaPath, json);
    }
}
