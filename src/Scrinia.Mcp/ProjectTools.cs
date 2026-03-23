using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

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
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
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

    /// <summary>Dispatcher for plan('tasks'), plan('status'), and project_init.</summary>
    [McpServerTool(Name = "plan"), Description(
        "Project planning operations. Actions: 'tasks' (decompose phase into tasks), " +
        "'status' (project progress and state), 'init' (initialize new project).")]
    public async Task<string> PlanDispatch(
        [Description("Action: 'tasks', 'status', 'init'.")] string action,
        [Description("Phase ID for task decomposition (tasks).")] string? phaseId = null,
        [Description("Free-text task definitions (tasks).")] string? tasks = null,
        [Description("Project context description (init).")] string? context = null,
        CancellationToken cancellationToken = default)
    {
        string act = action.Trim().ToLowerInvariant();
        switch (act)
        {
            case "tasks":
                if (string.IsNullOrWhiteSpace(phaseId))
                    return "Error: plan('tasks') requires 'phaseId' parameter.";
                if (string.IsNullOrWhiteSpace(tasks))
                    return "Error: plan('tasks') requires 'tasks' parameter.";
                return await PlanTasks(phaseId, tasks, cancellationToken);
            case "status":
                return await PlanStatus(cancellationToken);
            case "init":
                if (string.IsNullOrWhiteSpace(context))
                    return "Error: plan('init') requires 'context' parameter.";
                return await ProjectInit(context, cancellationToken);
            default:
                return $"Error: unknown action '{action}'. Valid actions: 'tasks', 'status', 'init'.";
        }
    }

    /// <summary>Dispatcher for requirement('add').</summary>
    [McpServerTool(Name = "requirement"), Description(
        "Manage project requirements. Actions: 'add' (store requirements with REQ-IDs), " +
        "'resolve' (mark a requirement fulfilled), 'list' (show all requirements).")]
    public async Task<string> RequirementDispatch(
        [Description("Action: 'add', 'resolve', or 'list'.")] string action,
        [Description("Free-text requirements with REQ-IDs (add).")] string? requirements = null,
        [Description("Requirement ID to resolve (resolve).")] string? id = null,
        [Description("Evidence of requirement fulfillment (resolve).")] string? evidence = null,
        CancellationToken cancellationToken = default)
    {
        string act = action.Trim().ToLowerInvariant();
        switch (act)
        {
            case "add":
                if (string.IsNullOrWhiteSpace(requirements))
                    return "Error: requirement('add') requires 'requirements' parameter.";
                return await PlanRequirements(requirements, cancellationToken);
            case "resolve":
                if (string.IsNullOrWhiteSpace(id))
                    return "Error: requirement('resolve') requires 'id' parameter (e.g., 'REQ-01').";
                if (string.IsNullOrWhiteSpace(evidence))
                    return "Error: requirement('resolve') requires 'evidence' parameter.";
                try
                {
                    var store = CurrentStore;
                    string reqText = await ReadMemoryAsync(store, "project:requirements", cancellationToken);
                    string marker = $"[RESOLVED: {evidence}]";
                    string updated = reqText.Replace(id, $"{id} {marker}");
                    await WritePlanningMemoryAsync(store, "project:requirements", updated,
                        archiveExisting: true, cancellationToken);
                    return $"Requirement '{id}' resolved: {evidence}. project:requirements updated.";
                }
                catch (FileNotFoundException)
                {
                    return "Error: no requirements found. Call requirement('add') first.";
                }
            case "list":
                try
                {
                    var store = CurrentStore;
                    string reqText = await ReadMemoryAsync(store, "project:requirements", cancellationToken);
                    return reqText;
                }
                catch (FileNotFoundException)
                {
                    return "No requirements found. Call requirement('add') to add requirements.";
                }
            default:
                return $"Error: unknown action '{action}'. Valid actions: 'add', 'resolve', 'list'.";
        }
    }

    /// <summary>Dispatcher for skill('load') and skill('create').</summary>
    [McpServerTool(Name = "skill"), Description(
        "Manage specialist skills. Actions: 'load' (list or load a skill), " +
        "'create' (create a reusable skill from scaffold).")]
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
                    return "Error: skill('create') requires 'name' parameter.";
                if (string.IsNullOrWhiteSpace(scaffold))
                    return "Error: skill('create') requires 'scaffold' parameter.";
                return await SkillCreate(name, scaffold, instructions, tools, cancellationToken);
            default:
                return $"Error: unknown action '{action}'. Valid actions: 'load', 'create'.";
        }
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
            ? "scan the existing codebase for concerns (concern('add')) and capture patterns (store), then set a goal with goal('add')"
            : "set a goal with goal('add'), then plan requirements";

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
                    "Action: Load the onboarder skill via skill('load', { name: \"onboarder\" }). " +
                    "Explore the existing codebase structure, conventions, and patterns. " +
                    "Store findings as project knowledge via memory('store').\n" +
                    "Acceptance criteria:\n" +
                    "- Codebase structure documented\n" +
                    "- Key patterns and conventions stored";
                await WritePlanningMemoryAsync(store, "task:init-0-onboarder", onboarderContent,
                    archiveExisting: false,
                    keywords: ["status:pending", "wave:0", "phase:init", "gate:onboarder"],
                    cancellationToken);
            }
            catch { /* best-effort */ }
        }

        // Scaffold merge infrastructure
        ScaffoldMergeInfrastructure(scriniaDir);

        string response = $"Initialized project '{projectId}'. Stored: project:context, project:state. " +
               $"Files in .scrinia/ were updated — these are your changes.";

        response += "\nMerge infrastructure created in .scrinia/hooks/. " +
            "Configure the merge driver: git config merge.scrinia-meta.driver " +
            "'.scrinia/hooks/scrinia-merge-meta.sh %O %A %B'";

        if (hasExistingCode)
            response += "\n\nExisting codebase detected. Onboarder task created.\n" +
                "Recommended next steps:\n" +
                "1. Run task('next') to start the onboarder\n" +
                "2. Set a goal for what you want to achieve → goal('add')\n" +
                "3. Then proceed to research → requirements → execution";
        else
            response += "\n\nEmpty workspace. Set a goal with goal('add'), " +
                "then define requirements.";

        return response;
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
            return "Error: no project initialized. Run project_init first.";
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

        return "Stored: project:requirements. Files in .scrinia/ were updated — these are your changes.\n\n" +
               "Review these requirements with the user:\n" +
               "- Are all requirements captured? Anything missing?\n" +
               "- Are the REQ-IDs scoped correctly (too broad? too narrow?)?\n" +
               "- Are priorities clear — what's essential vs. nice-to-have?\n" +
               "Once confirmed, set a goal with goal('add') to start execution.";
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
            return "Error: phaseId is required.";

        var store = CurrentStore;

        // Parse task sections from free-text input
        var parsedTasks = ParseTaskSections(tasks);
        if (parsedTasks.Count == 0)
            return "Error: no tasks found. Provide tasks using '## Task {id}' section headers.";

        // Auto-inject gate tasks
        var allUserTaskIds = parsedTasks.Select(t => t.Id).ToArray();

        // QA gate — always injected, depends on all user tasks
        parsedTasks.Add(new ParsedTask(
            Id: "qa-gate",
            DependsOn: allUserTaskIds,
            Content: "## QA Gate\nAction: Spawn a QA agent via skill('load', { name: \"qa\" }). " +
                "The QA agent runs the full test suite, verifies the build, checks acceptance criteria, " +
                "and writes qa:latest memory with structured results.\n" +
                "Acceptance criteria:\n- qa:latest memory exists with current test pass/fail counts\n" +
                "- Build passes with 0 errors\n- All phase acceptance criteria verified by QA agent"));

        // Self-reflector gate — always injected, after QA
        parsedTasks.Add(new ParsedTask(
            Id: "self-reflector-gate",
            DependsOn: ["qa-gate"],
            Content: "## Self-Reflector Gate\nAction: Spawn a self-reflector agent via skill('load', { name: \"self-reflector\" }). " +
                "Read execution logs and QA findings. Compare plan vs reality. " +
                "Store retrospective and belief updates.\n" +
                "Acceptance criteria:\n- Retrospective stored as learn:retro-*\n- Beliefs updated if applicable"));

        // Evolutionary, cartographer, and march gates — always injected (after QA + self-reflector)
        parsedTasks.Add(new ParsedTask(
            Id: "evolutionary-gate",
            DependsOn: ["qa-gate", "self-reflector-gate"],
            Content: "## Evolutionary Gate\nAction: Spawn an evolutionary agent via skill('load', { name: \"evolutionary\" }). " +
                "Run a knowledge base scan to update stale memories, detect skill drift, and surface emergent patterns.\n" +
                "Acceptance criteria:\n- Evolutionary scan completed\n- Stale memories updated\n" +
                "- Session stored as sessions:evolutionary-gNN"));

        parsedTasks.Add(new ParsedTask(
            Id: "cartographer-gate",
            DependsOn: ["qa-gate", "self-reflector-gate"],
            Content: "## Cartographer Gate\nAction: Spawn a cartographer agent via skill('load', {{ name: \"cartographer\" }}). " +
                "Map knowledge connections, link orphans, identify gaps.\n" +
                "Acceptance criteria:\n- Cartography scan completed\n- New links created for orphaned memories\n" +
                "- Report stored as cartography:YYYY-MM-DD"));

        parsedTasks.Add(new ParsedTask(
            Id: "march-gate",
            DependsOn: ["qa-gate", "self-reflector-gate"],
            Content: "## March Report Gate\nAction: Spawn a march reporter agent via skill('load', { name: \"march-reporter\" }). " +
                "Produce a goal summary document in docs/reports/.\n" +
                "Acceptance criteria:\n- March report written to docs/reports/\n- Session log updated"));

        // Resolve active goal for task scoping
        string? activeGoalId = await GetActiveGoalIdAsync(store, cancellationToken);

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
            if (task.Id.EndsWith("-gate", StringComparison.OrdinalIgnoreCase))
                keywords.Add($"gate:{task.Id.Replace("-gate", "", StringComparison.OrdinalIgnoreCase)}");

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

        // Check if agent:execution-policy exists
        string executionPolicyHint = "";
        try
        {
            var (epScope, _) = store.ParseQualifiedName("agent:execution-policy");
            var epEntries = store.LoadIndex(epScope);
            if (epEntries.Any(e => e.Name == "execution-policy"))
                executionPolicyHint = "\nAgent execution policy available — show('agent:execution-policy') for spawn requirements.";
        }
        catch { /* agent scope not created — skip silently */ }

        string taskList = string.Join("\n", createdNames.Select(n => $"  - {n}"));
        string conflictWarning = fileConflicts.Count > 0
            ? "\n\nFile conflicts detected:\n" + string.Join("\n", fileConflicts.Select(c => $"  - {c}"))
            : "";
        string response =
            $"Created {parsedTasks.Count} task(s) for phase {phaseId} in {waveCount} wave(s).\n" +
            $"Tasks stored:\n{taskList}\n" +
            $"Files in .scrinia/ were updated — these are your changes.\n" +
            $"Next: run task('next') to get the first pending tasks.{parallelHint}\n" +
            $"Spawn agents for all task execution — the primary agent orchestrates, it does not execute tasks directly." +
            executionPolicyHint +
            conflictWarning +
            patternNote;

        response = Truncate(response);

        return response;
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
                return "Error: no project found. Run project_init first.";
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

        string idleNote = "";
        if (!hasActiveGoal && progress == "100%")
            idleNote = "\nNo active goal. Ask the user what to work on next → goal('add')";
        else if (!hasActiveGoal && progress == "0%")
            idleNote = "\nNo active goal. Set one with goal('add') to start planning.";

        string response =
            $"Project: {projectName}\n" +
            $"Phase: {phase}\n" +
            $"Progress: {progress}\n" +
            $"Last action: {lastAction}\n" +
            $"Blockers: {blockers}\n" +
            $"Next: {next}" +
            concernNote + goalNote + idleNote;

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

        if (psStale > 0) response += $"\n⚠ {psStale} memory(s) have passed their review date.{psCacheNote}";
        if (psReview > 0) response += $"\nℹ {psReview} memory(s) have review conditions set.{psCacheNote}";
        if (psDrift > 0) response += $"\n⚠ {psDrift} code reference(s) have drifted (files changed since stored).{psCacheNote}";
        if (psMissing > 0) response += $"\n⚠ {psMissing} code reference(s) point to missing files.{psCacheNote}";

        response = Truncate(response);

        return response;
    }

    /// <summary>Thin dispatcher for task operations — delegates to TaskNext/TaskComplete.</summary>
    [McpServerTool(Name = "task"), Description(
        "Task operations. Actions: 'next' (get next pending task), 'complete' (mark task done).")]
    public async Task<string> TaskDispatch(
        [Description("Action: 'next' or 'complete'.")] string action,
        [Description("Phase ID (next — optional, auto-detects if omitted).")] string? phaseId = null,
        [Description("Task name to complete (complete).")] string? taskName = null,
        [Description("Outcome description (complete).")] string? outcome = null,
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
                            return "No pending tasks found for the active goal.";
                    }
                    catch { return "Error: could not auto-detect phase. Provide phaseId parameter."; }
                }
                return await TaskNext(phaseId, cancellationToken);

            case "complete":
                if (string.IsNullOrWhiteSpace(taskName))
                    return "Error: task('complete') requires 'taskName' parameter.";
                if (string.IsNullOrWhiteSpace(outcome))
                    return "Error: task('complete') requires 'outcome' parameter.";
                return await TaskComplete(taskName, outcome, cancellationToken);

            default:
                return $"Error: unknown action '{action}'. Valid actions: 'next', 'complete'.";
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
            return $"No pending tasks for phase {phaseId}.";

        // Find pending entries
        var pendingEntries = phaseEntries
            .Where(e => HasKeyword(e, "status:pending"))
            .ToList();

        if (pendingEntries.Count == 0)
            return $"No pending tasks for phase {phaseId}.";

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
            return $"No unblocked tasks for phase {phaseId} in wave {currentWave}. Some tasks may be waiting on dependencies.";

        // Build response: read artifact content only for unblocked tasks
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Phase {phaseId} — Wave {currentWave} — {unblockedEntries.Count} unblocked task(s):");
        if (unblockedEntries.Count > 1)
            sb.AppendLine($"These {unblockedEntries.Count} tasks are independent — spawn a parallel agent for each task.");
        else
            sb.AppendLine("Spawn an agent for this task — keep the primary agent available for SOS and user interaction.");
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
                ? $"spawn {unblockedEntries.Count} parallel agents for wave {currentWave} tasks, call task('complete') for each"
                : $"execute wave {currentWave} task, then call task('complete')",
            cancellationToken);

        string response = sb.ToString();
        response = Truncate(response);

        return response;
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
            return "Error: no requirements found. Call requirement('add') first.";
        }

        var criteria = ExtractRequirementCriteria(requirementsText, phaseReqIds);
        if (criteria.Count == 0)
            return $"No requirements found for phase {phaseId}. Ensure tasks reference REQ-IDs in their content.";

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

            sb.AppendLine("Verify each criterion yourself (run tests, review code, confirm behavior), then call:");
            sb.AppendLine($"```");
            sb.AppendLine($"plan('verify', {{ phaseId: \"{phaseId}\", evidence: \"PASS: criterion 1 — your evidence\\nPASS: criterion 2 — your evidence\")");
            sb.AppendLine($"```");
            sb.AppendLine();

            for (int i = 0; i < criteria.Count; i++)
                sb.AppendLine($"{i + 1}. [ ] {criteria[i]}");

            string checklistResponse = sb.ToString();
            checklistResponse = Truncate(checklistResponse);

            return checklistResponse;
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
                return $"Error: {openConcerns.Count} open high/medium concern(s) for phase {phaseId}: {names}. " +
                    "Resolve them (concern('resolve') with verifiedBy) before verification.";
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
            verifyNextStep = "run plan('gaps') to create gap closure tasks";
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
                ? $"resolve addressed concerns (concern('resolve')), then run plan('retrospective') for phase {phaseId}"
                : $"run plan('retrospective') for phase {phaseId} to record lessons learned";
        }

        await WriteStateAsync(store, projectName2, projectId2,
            phase: currentPhase2,
            progressPct: progressPct2,
            lastAction: $"plan('verify') for phase {phaseId}: {status}",
            blockers: passCount < criteria.Count ? $"{criteria.Count - passCount} criteria failed" : "none",
            nextStep: verifyNextStep,
            cancellationToken);

        string response = sb2.ToString();

        // Append next step guidance to response
        if (passCount == criteria.Count)
            response += $"\nNext: {verifyNextStep}";

        response = Truncate(response);

        return response;
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
            return "Error: no project found. Run project_init first.";
        }

        // Parse failed criteria — split on newlines, trim, filter empty
        var criteria = failedCriteria
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => c.Length > 0)
            .ToList();

        if (criteria.Count == 0)
            return $"Error: no failed criteria provided. Pass newline-separated criterion texts.";

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
            nextStep: "run task('next') to work on gap tasks",
            cancellationToken);

        string taskList = string.Join("\n", createdNames.Select(n => $"  - {n}"));
        string response =
            $"Created {criteria.Count} gap closure task(s) for phase {phaseId}. Phase re-opened. Run task('next') to begin.\n" +
            $"Gap tasks created:\n{taskList}";

        response = Truncate(response);

        return response;
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
            ? "set a goal with goal('add') to start planning"
            : "run requirement('add') to define project requirements";

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
            return $"Error: task '{taskName}' not found.";

        // Gate task validation — block completion if required artifact missing
        if (existing.Keywords is not null)
        {
            string? activeGoal = await GetActiveGoalIdAsync(store, cancellationToken);

            foreach (var kw in existing.Keywords.Where(k => k.StartsWith("gate:", StringComparison.OrdinalIgnoreCase)))
            {
                string gateType = kw["gate:".Length..];
                string? validationError = null;

                switch (gateType.ToLowerInvariant())
                {
                    case "qa":
                        try { await ReadMemoryAsync(store, "qa:latest", cancellationToken); }
                        catch (FileNotFoundException) { validationError = "qa:latest memory not found. The QA agent must write qa:latest before this gate can complete."; }
                        break;

                    case "self-reflector":
                        try
                        {
                            // Check for learn:retro-* for this goal
                            var (learnScope, _) = store.ParseQualifiedName("learn:placeholder");
                            var learnEntries = store.LoadIndex(learnScope);
                            string goalShort = "";
                            if (activeGoal is not null)
                            {
                                var gm = GoalIdPattern.Match(activeGoal);
                                if (gm.Success) goalShort = $"g{gm.Groups[1].Value}";
                            }
                            bool hasRetro = learnEntries.Any(e => e.Name.StartsWith($"retro-{goalShort}", StringComparison.OrdinalIgnoreCase));
                            if (!hasRetro)
                                validationError = $"No learn:retro-{goalShort}-* memory found. The self-reflector agent must store a retrospective before this gate can complete.";
                        }
                        catch { }
                        break;

                    case "auditor":
                        try
                        {
                            try { await ReadMemoryAsync(store, "project:requirements", cancellationToken); }
                            catch (FileNotFoundException) { validationError = "No requirements found. The auditor must call requirement('add') before this gate can complete."; }
                        }
                        catch { }
                        break;

                    case "researcher":
                        try
                        {
                            var (resScope, _) = store.ParseQualifiedName("research:placeholder");
                            var resEntries = store.LoadIndex(resScope);
                            string resGoalShort = "";
                            if (activeGoal is not null)
                            {
                                var rgm = GoalIdPattern.Match(activeGoal);
                                if (rgm.Success) resGoalShort = $"g{rgm.Groups[1].Value}";
                            }
                            bool hasResearch = resEntries.Any(e =>
                                string.IsNullOrEmpty(resGoalShort) ||
                                e.Name.StartsWith(resGoalShort, StringComparison.OrdinalIgnoreCase));
                            if (!hasResearch)
                                validationError = "No research:* memories found. The researcher must store findings before this gate can complete.";
                        }
                        catch { }
                        break;

                    case "planner":
                        try
                        {
                            var (ptScope, _) = store.ParseQualifiedName("task:placeholder");
                            var ptEntries = store.LoadIndex(ptScope);
                            // Check for non-seed tasks (tasks that don't have gate: keyword — i.e., execution tasks)
                            string ptGoalShort = "";
                            if (activeGoal is not null)
                            {
                                var pgm = GoalIdPattern.Match(activeGoal);
                                if (pgm.Success) ptGoalShort = $"g{pgm.Groups[1].Value}";
                            }
                            bool hasExecutionTasks = ptEntries.Any(e =>
                                (string.IsNullOrEmpty(ptGoalShort) || HasKeyword(e, $"goal:{activeGoal}")) &&
                                e.Keywords is not null && !e.Keywords.Any(k => k.StartsWith("gate:", StringComparison.OrdinalIgnoreCase)));
                            if (!hasExecutionTasks)
                                validationError = "No execution tasks found. The planner must call plan('tasks') before this gate can complete.";
                        }
                        catch { }
                        break;

                    case "evolutionary":
                        try
                        {
                            var (evoScope, _) = store.ParseQualifiedName("sessions:placeholder");
                            var evoEntries = store.LoadIndex(evoScope);
                            bool hasEvoSession = evoEntries.Any(e =>
                                e.Name.StartsWith("evolutionary-", StringComparison.OrdinalIgnoreCase));
                            if (!hasEvoSession)
                                validationError = "No sessions:evolutionary-* memory found. The evolutionary agent must complete its scan.";
                        }
                        catch { }
                        break;

                    case "cartographer":
                        try
                        {
                            var (cartoScope, _) = store.ParseQualifiedName("cartography:placeholder");
                            var cartoEntries = store.LoadIndex(cartoScope);
                            if (cartoEntries.Count == 0)
                                validationError = "No cartography:* memory found. The cartographer agent must complete its scan.";
                        }
                        catch { }
                        break;

                    case "march":
                        try
                        {
                            string gwStoreDir = store.GetStoreDirForScope("local");
                            string gwScriniaDir = Path.GetDirectoryName(gwStoreDir) ?? gwStoreDir;
                            string workspaceRoot = Path.GetDirectoryName(gwScriniaDir) ?? gwScriniaDir;
                            string reportsDir = Path.Combine(workspaceRoot, "docs", "reports");
                            if (!Directory.Exists(reportsDir) || !Directory.EnumerateFiles(reportsDir, "*.md").Any())
                                validationError = "No march report found in docs/reports/. The march reporter must produce a report.";
                        }
                        catch { }
                        break;
                }

                if (validationError is not null)
                    return $"Error: gate '{gateType}' validation failed — {validationError}";
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
            nextStep = $"all phase {phaseId} tasks complete — verify the work (run tests, review changes), then run plan('verify') to record results";
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

        string response;
        if (phaseComplete)
        {
            response = $"Task '{taskName}' marked complete. All phase {phaseId} tasks done.\n" +
                $"Next: verify the work yourself (run tests, review changes, confirm behavior), " +
                $"then call plan('verify', {{ phaseId: \"{phaseId}\" }}) to record your verification results.";
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
                response = $"Task '{taskName}' marked complete. {sameWaveRemaining} tasks remaining in wave {thisWave} — keep parallel agents running. Call task('complete') for each as they finish.";
            else if (sameWaveRemaining == 1)
                response = $"Task '{taskName}' marked complete. 1 task remaining in wave {thisWave}.";
            else
                response = $"Task '{taskName}' marked complete. Wave {thisWave} done. Run task('next') to get wave {thisWave + 1} tasks ({totalRemaining} pending).";
        }

        if (!string.IsNullOrWhiteSpace(acceptanceCriteria))
            response += $"\nAcceptance criteria for this task:\n{acceptanceCriteria}";

        response = Truncate(response);

        return response;
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
                    return $"\u26a0 {newSince} memories created/modified since last cartographer run. Run skill('load', {{ name: \"cartographer\" }}) to index connections.\n";
            }
            else if (totalMemories >= 10)
            {
                return $"\u26a0 {totalMemories} memories exist with no cartographer run. Run skill('load', {{ name: \"cartographer\" }}) to index connections.\n";
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

    /// <summary>Thin dispatcher for concern operations — delegates to ConcernAdd/ConcernResolve/ConcernList.</summary>
    [McpServerTool(Name = "concern"), Description(
        "Track project concerns. Actions: 'add' (new concern), 'resolve' (mark resolved), " +
        "'list' (show concerns by status/phase).")]
    public async Task<string> ConcernDispatch(
        [Description("Action: 'add', 'resolve', 'list'. Defaults to 'list'.")] string action = "list",
        [Description("Concern description (add).")] string? description = null,
        [Description("Severity: high/medium/low (add).")] string? severity = null,
        [Description("Phase scope (add).")] string? phaseScope = null,
        [Description("Concern ID (add — optional, resolve — as concernName).")] string? id = null,
        [Description("Concern name to resolve (resolve).")] string? concernName = null,
        [Description("Resolution notes (resolve).")] string? resolution = null,
        [Description("Who verified: debugger/qa/manual (resolve).")] string? verifiedBy = null,
        [Description("Phase filter (list).")] string? phaseFilter = null,
        CancellationToken cancellationToken = default)
    {
        string act = action.Trim().ToLowerInvariant();
        switch (act)
        {
            case "add":
                if (string.IsNullOrWhiteSpace(description))
                    return "Error: concern('add') requires 'description' parameter.";
                if (string.IsNullOrWhiteSpace(severity))
                    return "Error: concern('add') requires 'severity' parameter.";
                if (string.IsNullOrWhiteSpace(phaseScope))
                    return "Error: concern('add') requires 'phaseScope' parameter.";
                return await ConcernAdd(description, severity, phaseScope, id, cancellationToken);

            case "resolve":
                if (string.IsNullOrWhiteSpace(concernName))
                    return "Error: concern('resolve') requires 'concernName' parameter.";
                if (string.IsNullOrWhiteSpace(resolution))
                    return "Error: concern('resolve') requires 'resolution' parameter.";
                if (string.IsNullOrWhiteSpace(verifiedBy))
                    return "Error: concern('resolve') requires 'verifiedBy' parameter.";
                return await ConcernResolve(concernName, resolution, verifiedBy, cancellationToken);

            case "list":
                return await ConcernList(phaseFilter, statusFilter: null, cancellationToken);

            default:
                return $"Error: unknown action '{action}'. Valid actions: 'add', 'resolve', 'list'.";
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
            return "Error: no project initialized. Run project_init first.";
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
            nextStep: "run concern to list active concerns, or concern('resolve') when addressed",
            cancellationToken);

        return $"Stored as {qualifiedName}. Files in .scrinia/ were updated — these are your changes." + patternSuggestion;
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
            return $"Error: verifiedBy must be 'debugger', 'qa', or 'manual'. Got: '{verifiedBy}'.";

        // Parse name to get scope and subject
        var (scope, subject) = store.ParseQualifiedName(concernName);

        // Load index and find existing entry
        var allEntries = store.LoadIndex(scope);
        var existing = allEntries.FirstOrDefault(e =>
            string.Equals(e.Name, subject, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
            return $"Error: concern '{concernName}' not found.";

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

        return $"Concern '{concernName}' resolved. Files in .scrinia/ were updated — these are your changes.";
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
            return Task.FromResult($"No active concerns.");
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
            return Task.FromResult($"No active concerns{phaseNote}.");
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

        string response = sb.ToString();
        response = Truncate(response);

        return Task.FromResult(response);
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
            $"## Provenance\nAuthored by agent via plan('retrospective'). Keyword: provenance:agent";

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
                        skillNudge = $"\nExisting skills to consider updating: {string.Join(", ", names)}";
                    }
                }
                catch { /* skill scope not yet created — skip silently */ }

                if (allPhasesDone)
                    retroNextStep = "\nAll phases complete. Before completing the goal:\n" +
                        "0. Run QA: skill('load', { name: \"qa\" }) → verify tests pass, build clean, criteria met\n" +
                        "1. Produce a march report: skill('load', { name: \"march-reporter\" }) → docs/reports/ + sessions:YYYY-MM-DD memory\n" +
                        "2. Distill valuable learnings into topical memories (store) so future goals start smarter\n" +
                        "3. Update existing skills or create new ones (skill('create')) with lessons from this goal" +
                        skillNudge + "\n" +
                        "4. Then run goal('complete')";
                else if (nextPhase is not null)
                    retroNextStep = $"\nNext: investigate phase {nextPhase} — explore the codebase, store research findings, then plan tasks." +
                        skillNudge +
                        "\nTip: if this conversation is getting long, checkpoint your state: store([\"current context...\"], \"~checkpoint\")";
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

        string response = $"Phase {phaseId} retrospective stored in {retroMemoryName}. " +
            "Searchable via standard search.\n" +
            "Update your session log: append to or store sessions:YYYY-MM-DD with this phase's outcome." +
            retroNextStep;

        // Check memory growth for cartographer nudge (CART-02)
        string retroCartWarning = CheckCartographerNeeded(store);
        if (!string.IsNullOrEmpty(retroCartWarning))
            response += "\n\n" + retroCartWarning.TrimEnd();

        response = Truncate(response);

        return response;
    }

    /// <summary>Alias for AgentProfile — used by plan('profile') dispatcher.</summary>
    internal Task<string> PlanProfile(string profile, CancellationToken cancellationToken = default)
        => AgentProfile(profile, cancellationToken);

    /// <summary>Store or update project-level agent behavioral norms.</summary>
    internal async Task<string> AgentProfile(
        string profile,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        await WritePlanningMemoryAsync(store, "agent:profile", profile,
            archiveExisting: false, keywords: ["provenance:agent"], cancellationToken);

        string response = "Agent profile stored in agent:profile. " +
            "Norms persist across sessions and are searchable via standard search.";

        response = Truncate(response);

        return response;
    }

    // knowledge_add removed — knowledge is just topical memories via store().
    // e.g., store(content, "dotnet:asynclocal-pattern", keywords=["source:agent"])

    // -- Dynamic goal management (GOAL-01, GOAL-02, GOAL-04) ---------------------

    /// <summary>Thin dispatcher for goal management — delegates to GoalUpdate.</summary>
    [McpServerTool(Name = "goal"), Description(
        "Manage project goals. Actions: 'add' (new goal), 'edit' (update description), " +
        "'complete' (mark done with outcome), 'list' (show all goals with status).")]
    public Task<string> Goal(
        [Description("Action: 'add', 'edit', 'complete', 'list'.")] string action,
        [Description("Goal description (add, edit).")] string? description = null,
        [Description("Goal ID (edit, complete).")] string? goalId = null,
        [Description("Outcome note (complete).")] string? outcome = null,
        CancellationToken cancellationToken = default)
    {
        return GoalUpdate(action, description, goalId, outcome, cancellationToken);
    }

    /// <summary>Manage project goals dynamically.</summary>
    internal async Task<string> GoalUpdate(
        [Description("Action to perform: 'add', 'edit', 'complete', or 'list'.")] string action,
        [Description("Goal description (required for 'add' action).")] string? description = null,
        [Description("Goal ID to complete (e.g. 'G-1'); required for 'complete' action.")] string? goalId = null,
        [Description("Outcome note; required for 'complete' action.")] string? outcome = null,
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
            return "Error: no project initialized. Run project_init first.";
        }

        string actionLower = action.Trim().ToLowerInvariant();

        switch (actionLower)
        {
            case "add":
            {
                if (string.IsNullOrWhiteSpace(description))
                    return "Error: 'add' action requires a description.";

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

                // Auto-create researcher seed task (wave 0 — research first for full context)
                try
                {
                    string researcherTaskName = $"task:{goalPrefix}00-0-researcher";
                    var researcherKeywords = new List<string> { "status:pending", "wave:0", "phase:00", "gate:researcher" };
                    researcherKeywords.Add($"goal:{newGoalId}");
                    string researcherContent =
                        "## Researcher Task\n" +
                        "Action: Investigate the goal scope by exploring the codebase and existing memories. " +
                        "Understand what exists, what needs to change, and what risks are present. " +
                        "Store findings via memory('store', { name: \"research:...\", content: [...] }).\n" +
                        "Acceptance criteria:\n" +
                        "- Research findings stored as research:* memories\n" +
                        "- Scope and implementation approach documented";
                    await WritePlanningMemoryAsync(store, researcherTaskName, researcherContent,
                        archiveExisting: false, keywords: [.. researcherKeywords], cancellationToken);
                }
                catch { /* best-effort */ }

                // Auto-create auditor seed task (wave 1, depends on researcher)
                try
                {
                    string auditorTaskName = $"task:{goalPrefix}00-1-auditor";
                    var auditorKeywords = new List<string> { "status:pending", "wave:1", "phase:00", "gate:auditor",
                        $"depends_on:{goalPrefix}00-0-researcher" };
                    auditorKeywords.Add($"goal:{newGoalId}");
                    string auditorContent =
                        "## Auditor Task\n" +
                        "Action: Load the auditor skill via skill('load', { name: \"auditor\" }). " +
                        "Read the research findings to understand full scope and context. " +
                        "Call requirement('add') for each requirement discovered. " +
                        "Call concern('add') for each risk or issue found.\n" +
                        "Acceptance criteria:\n" +
                        "- Requirements captured via requirement('add')\n" +
                        "- Concerns raised via concern('add')";
                    await WritePlanningMemoryAsync(store, auditorTaskName, auditorContent,
                        archiveExisting: false, keywords: [.. auditorKeywords], cancellationToken);
                }
                catch { /* best-effort */ }

                // Auto-create planner seed task (wave 2, depends on auditor)
                try
                {
                    string plannerTaskName = $"task:{goalPrefix}00-2-planner";
                    var plannerKeywords = new List<string> { "status:pending", "wave:2", "phase:00", "gate:planner",
                        $"depends_on:{goalPrefix}00-1-auditor" };
                    plannerKeywords.Add($"goal:{newGoalId}");
                    string plannerContent =
                        "## Planner Task\n" +
                        "Action: Load the planner skill via skill('load', {{ name: \"planner\" }}). " +
                        "Read research findings and requirements. " +
                        "Produce task definitions and call plan('tasks') directly.\n" +
                        "Acceptance criteria:\n" +
                        "- plan('tasks') called with task definitions\n" +
                        "- Tasks created with proper dependencies and acceptance criteria";
                    await WritePlanningMemoryAsync(store, plannerTaskName, plannerContent,
                        archiveExisting: false, keywords: [.. plannerKeywords], cancellationToken);
                }
                catch { /* best-effort */ }

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

                return $"Goal added as {newGoalId}: {description}.\n" +
                       $"project:context updated. Files in .scrinia/ were updated — these are your changes.\n\n" +
                       backlogSection +
                       $"Researcher task created. Auditor and planner seed tasks queued. Run task('next') to continue.";
            }

            case "complete":
            {
                if (string.IsNullOrWhiteSpace(goalId))
                    return "Error: 'complete' action requires a goalId (e.g. 'G-1').";

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
                    return $"Error: goal '{goalId}' not found. Use goal('list') to see all goal IDs.";

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
                                warnings.Add($"phase {pid} has no plan('verify') record");

                            // Check for FAIL in verification results
                            if (hasVerify && logText!.Contains($"VERIFY phase {pid}: PARTIAL", StringComparison.OrdinalIgnoreCase))
                                warnings.Add($"phase {pid} verification had failures — check plan('verify') results");
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
                                warnings.Add($"phase {pid} has no plan('retrospective')");
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
                        return $"Error: {openHighMed.Count} open high/medium concern(s) must be resolved before completing the goal: {names}. " +
                            "Use concern('resolve', { concernName, resolution, verifiedBy }) for each.";
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

                string response = $"Goal '{searchId}' marked complete. Outcome recorded. " +
                       $"project:context updated. Files in .scrinia/ were updated \u2014 these are your changes.";

                if (warnings.Count > 0)
                    response += "\n\nWorkflow steps you may have skipped:\n" +
                        string.Join("\n", warnings.Select(w => $"- {w}")) +
                        "\nConsider running plan('verify') and plan('retrospective') before moving on.";

                response += "\n\nPost-goal learning:\n" +
                    "- Run QA if not already done: skill('load', { name: \"qa\" }) for structured verification\n" +
                    "- Produce a march report: skill('load', { name: \"march-reporter\" }) \u2192 write to docs/reports/ and update sessions:YYYY-MM-DD memory\n" +
                    "- Distill valuable findings into topical memories (store) for future goals\n" +
                    "- Update or create skills (skill('create')) with lessons learned\n" +
                    "Planning artifacts (task:*, plan:*, research:*) can be cleaned up \u2014 " +
                    "the learnings live in your memories and skills now.";

                return response;
            }

            case "edit":
            {
                if (string.IsNullOrWhiteSpace(goalId))
                    return "Error: 'edit' action requires a goalId.";
                if (string.IsNullOrWhiteSpace(description))
                    return "Error: 'edit' action requires a description.";

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
                    return $"Error: goal '{goalId}' not found. Use goal('list') to see all goals.";

                string oldLine = goals[matchIndex];
                string trimmed = oldLine.TrimStart('-', '*', ' ');

                // Find where description starts (after [G-N-xxx] [status])
                var statusMatch = Regex.Match(trimmed, @"\]\s*\[(active|complete)\]\s*");
                if (!statusMatch.Success)
                    return $"Error: could not parse goal line format for '{goalId}'.";

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

                return $"Goal '{searchId}' updated.\nOld: {oldDesc.Trim()}\nNew: {description}\nproject:context updated. Files in .scrinia/ were updated — these are your changes.";
            }

            case "list":
            {
                var (goals, originalCount, _) = ParseGoalsSection(contextText);

                if (goals.Count == 0)
                    return "No structured goals found in project:context. Use goal('add') to add goals.";

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

                string response = Truncate(sb.ToString());

                return response;
            }

            default:
                return $"Error: unknown action '{action}'. Valid actions: 'add', 'edit', 'complete', 'list'.";
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
            nextStep: "run goal('list') to see all goals with status",
            ct);
    }

    // -- Built-in specialist scaffolds (AGENT-04) --------------------------------

    private const string ResearcherScaffold =
        "## Role: Researcher Specialist\n" +
        "You investigate technical questions and gather findings for the current project.\n\n" +
        "## Tools Available (if Scrinia MCP is active)\n" +
        "- memory('search'): Query stored knowledge and memories for related context.\n" +
        "- memory('show'): Retrieve full artifact content for a named memory.\n" +
        "- memory('store'): Persist research findings as research:* memories.\n\n" +
        "## Instructions\n" +
        "1. Use memory('search') to find existing knowledge before researching from scratch.\n" +
        "2. Explore the codebase to understand scope, patterns, and risks.\n" +
        "3. Store findings via memory('store', { name: \"research:...\", content: [...] }).\n" +
        "4. Document questions answered, sources consulted, and key conclusions.\n\n" +
        "## Fallback Instructions (if Scrinia MCP is not available)\n" +
        "Organize findings in markdown. Use file read/write operations to persist results.\n" +
        "Document questions answered, sources consulted, and key conclusions.\n";

    private const string ReviewerScaffold =
        "## Role: Reviewer Specialist\n" +
        "You review code, architecture, or plans and provide structured feedback with actionable concerns.\n\n" +
        "## Tools Available (if Scrinia MCP is active)\n" +
        "- search: Query memories for existing decisions, patterns, or prior art.\n" +
        "- show: Load full artifact content for review context.\n" +
        "- concern('add'): Track issues found during review with severity and phase scope.\n" +
        "- concern('resolve'): Mark concerns resolved when addressed.\n\n" +
        "## Instructions\n" +
        "1. Use search() to load relevant context before reviewing.\n" +
        "2. For each issue found, call concern('add') with severity (high/medium/low) and phase.\n" +
        "3. Provide specific, actionable feedback — not just identification.\n" +
        "4. Summarize findings with a list of concerns added and recommendations.\n\n" +
        "## Fallback Instructions (if Scrinia MCP is not available)\n" +
        "Write a structured review document. List issues with severity labels.\n" +
        "Use markdown headings: Critical Issues, Medium Issues, Minor Issues, Recommendations.\n";

    private const string DomainExpertScaffold =
        "## Role: Domain Expert Specialist\n" +
        "You apply deep domain knowledge to answer questions and document expert-level insights.\n\n" +
        "## Tools Available (if Scrinia MCP is active)\n" +
        "- search: Find existing knowledge entries before adding new ones.\n" +
        "- store: Store expert insights in the body of knowledge (bok:*).\n" +
        "- show: Retrieve full artifact content for context on prior decisions.\n\n" +
        "## Instructions\n" +
        "1. Use search(scopes='bok') to check for existing domain knowledge first.\n" +
        "2. Provide expert-level analysis grounded in established domain patterns.\n" +
        "3. Store durable insights via store(domain, slug, knowledge, ...).\n" +
        "4. Flag uncertainty explicitly — indicate confidence level in your responses.\n\n" +
        "## Fallback Instructions (if Scrinia MCP is not available)\n" +
        "Document expert insights in a structured markdown file.\n" +
        "Include sections: Domain Context, Key Patterns, Caveats, References.\n";

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
            return "Error: no project initialized. Run project_init first.";
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

        // Store via WritePlanningMemoryAsync with skill:{name} qualified name
        string qualifiedName = $"skill:{name}";
        var skillKeywords = new List<string> { $"role:{role}", $"capabilities:{capabilityList}" };

        // If this skill overrides a built-in, record the built-in's hash so we can detect staleness
        if (BuiltInSkills.TryGetValue(name, out string? builtInText))
        {
            var builtInHash = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(builtInText)));
            skillKeywords.Add($"basedOn:{builtInHash}");
        }

        await WritePlanningMemoryAsync(store, qualifiedName, promptContent,
            archiveExisting: true,
            keywords: [.. skillKeywords],
            cancellationToken);

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
            nextStep: "use skill('load') to retrieve stored skills",
            cancellationToken);

        string response = $"Stored as {qualifiedName}. Files in .scrinia/ were updated -- these are your changes.";

        response = Truncate(response);

        return response;
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
            // List mode: synchronous index-only scan, no artifact decode
            var (scope, _) = store.ParseQualifiedName("skill:placeholder");
            IReadOnlyList<ArtifactEntry> entries;
            try { entries = store.LoadIndex(scope); }
            catch { entries = []; }

            // Merge built-in + project skills
            var projectNames = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            int totalCount = BuiltInSkills.Count + entries.Count(e => !BuiltInSkills.ContainsKey(e.Name));

            if (totalCount == 0)
                return Task.FromResult("No skills available.");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Available skills ({totalCount}):");
            sb.AppendLine();

            // Built-in skills (always available, project version overrides if exists)
            foreach (string skillKey in BuiltInSkills.Keys)
            {
                if (projectNames.Contains(skillKey))
                {
                    // Check if the override is stale (basedOn hash mismatch)
                    var overrideEntry = entries.FirstOrDefault(e => e.Name.Equals(skillKey, StringComparison.OrdinalIgnoreCase));
                    string tag = "override";
                    if (overrideEntry?.Keywords is not null)
                    {
                        var basedOnKw = overrideEntry.Keywords.FirstOrDefault(k => k.StartsWith("basedOn:", StringComparison.Ordinal));
                        if (basedOnKw is not null)
                        {
                            string storedHash = basedOnKw["basedOn:".Length..];
                            string currentHash = Convert.ToHexStringLower(
                                System.Security.Cryptography.SHA256.HashData(
                                    System.Text.Encoding.UTF8.GetBytes(BuiltInSkills[skillKey])));
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

            // Project-only skills (not built-in)
            foreach (var entry in entries)
            {
                if (BuiltInSkills.ContainsKey(entry.Name))
                    continue; // already listed above

                string roleKw = entry.Keywords?
                    .FirstOrDefault(k => k.StartsWith("role:", StringComparison.OrdinalIgnoreCase))
                    ?? "role:unknown";

                sb.AppendLine($"- skill:{entry.Name} [custom] [{roleKw}]");

                if (sb.Length > MaxResponseChars - 200)
                {
                    sb.AppendLine("[... truncated to 8KB limit]");
                    break;
                }
            }

            string listResponse = sb.ToString();
            listResponse = Truncate(listResponse);

            return Task.FromResult(listResponse);
        }

        // Load mode: async artifact read
        return LoadSkillAsync(store, name, reconcile, cancellationToken);
    }

    private static async Task<string> LoadSkillAsync(
        IMemoryStore store, string skillName, bool reconcile, CancellationToken ct)
    {
        string qualifiedName = $"skill:{skillName}";
        string? overrideContent = null;
        try
        {
            overrideContent = await ReadMemoryAsync(store, qualifiedName, ct);
        }
        catch (FileNotFoundException)
        {
            // No project override exists
        }

        // Reconcile mode: show both built-in and override side by side
        if (reconcile && overrideContent is not null && BuiltInSkills.TryGetValue(skillName, out string? reconBuiltIn))
        {
            return Truncate(
                $"## Current Built-in\n{reconBuiltIn}\n\n" +
                $"## Your Project Override\n{overrideContent}\n\n" +
                "## Instructions\n" +
                "Merge your project-specific additions with the updated built-in base,\n" +
                "then call skill('create') to save the reconciled version.\n");
        }

        if (overrideContent is null)
        {
            // Fall back to built-in skills
            if (BuiltInSkills.TryGetValue(skillName, out string? builtIn))
                return "[Loaded from built-in]\n" + Truncate(builtIn);
            return $"Error: skill '{skillName}' not found. Use skill('load') (no name) to list available skills.";
        }

        string content = Truncate(overrideContent);
        content = "[Loaded from project override]\n" + content;

        // Check for stale base — warn if the built-in has changed since this override was created
        var (scope, subject) = store.ParseQualifiedName(qualifiedName);
        var entries = store.LoadIndex(scope);
        var entry = entries.FirstOrDefault(e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
        if (entry?.Keywords is not null && BuiltInSkills.TryGetValue(skillName, out string? currentBuiltIn))
        {
            var basedOnKw = entry.Keywords.FirstOrDefault(k => k.StartsWith("basedOn:", StringComparison.Ordinal));
            if (basedOnKw is not null)
            {
                string storedHash = basedOnKw["basedOn:".Length..];
                string currentHash = Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(currentBuiltIn)));
                if (!storedHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
                {
                    content = $"[WARNING: built-in skill has changed since this override was created. Review with skill('load', {{ name: \"{skillName}\", reconcile: true }})]\n" + content;
                }
            }
        }

        return content;
    }

    private static readonly Dictionary<string, string> BuiltInSkills = new(StringComparer.OrdinalIgnoreCase)
    {
        ["march-reporter"] = """
            ## Role: Goal March Reporter
            > **Spawn protocol**: When spawning an agent for this skill, call `skill('load', { name: "march-reporter" })` and pass its output as the agent prompt. Do not paraphrase or summarize the skill text.
            You produce human-readable goal summary documents that report the march toward project
            objectives. These documents serve as audit trails for stakeholders and future agents.

            ## When to use
            After completing a goal (step 8), offer to produce a march report. Always produce one
            at milestone boundaries. Small goals can skip; significant goals need the paper trail.
            The agent should ask: "Want me to produce a march report for this goal?"

            ## Methodology
            1. `concern('list')` — query active concerns to see current state and determine next IDs
            2. `goal('list')` — get all goals with outcomes for the reporting period
            3. `concern('list', { statusFilter: "all" })` — get all concerns (active + resolved)
            4. `memory('search', { query: "applied-fixes" })` — load fix summaries
            5. Review git log for the period to capture file-level changes

            ## Document structure
            Write to `docs/reports/{date}-{goal-slug}.md`:

            ### 1. Summary
            - Goal description, dates, outcome (1-3 sentences)

            ### 2. Changes
            - Features added, config surface changes, new endpoints/permissions
            - Files touched (summary, not exhaustive)

            ### 3. Findings
            Table with columns: ID, Description, Severity, Status, Resolution
            Query `concern('list')` for all findings. Include ALL findings for this goal —
            fixed, dismissed, and accepted. Dismissed findings need rationale.

            ### 4. Test Impact
            - Before/after test counts
            - New tests added and what they cover

            ### 5. Security Posture
            - What was hardened
            - Accepted risks with justification
            - Remaining known issues (if any)

            ### 6. Configuration Changes
            - New settings with defaults and purpose
            - Breaking changes (if any)
            - Migration notes for existing deployments

            ## Key principles
            - Reference finding IDs (SEC-001, QAL-001, DOC-001) — never describe findings without IDs
            - Be specific about what was dismissed and why — a future auditor should understand the rationale
            - Include the "so what" — not just what changed, but why it matters
            - The document is for humans who weren't in the conversation — write for someone with
              project context but no session context
            """,

        ["auditor"] = """
            ## Role: Code & Architecture Auditor
            You systematically review code, architecture, and documentation for quality, security,
            and correctness. You produce structured findings with sequential IDs for tracking.

            ## Methodology

            ### Before scanning
            1. `concern('list')` — query active concerns to see current findings state and determine next IDs
            2. `memory('search', { query: "applied-fixes" })` — know what's already been fixed
            3. `memory('search', { query: "audit-false-positives" })` — avoid known false positives
            4. Understand the project: `memory('search', { query: "architecture" })`, `memory('search', { query: "patterns" })`

            ### Scanning — three streams
            Run these in parallel when possible:

            **Security**: input validation at all boundaries, auth/authz consistency, injection risks
            (path traversal, SQL, XSS), data exposure (logs, errors, stack traces), crypto concerns,
            concurrency (races, deadlocks), resource exhaustion, dependency vulnerabilities.

            **Code quality**: duplication, missing IDisposable, dead code, error handling (swallowed
            exceptions, inconsistent patterns), resource leaks, thread safety, API consistency,
            performance concerns in hot paths.

            **Documentation**: counts match reality (run `dotnet test`, count attributes), stale references
            to removed features, examples match current API signatures, new features documented.

            ### Finding IDs
            Use sequenced IDs registered via concern('add'). Count existing concern:SEC-*, concern:QAL-*, concern:DOC-* entries via concern('list') to determine the next available ID. Never reuse numbers.
            - Security: SEC-NNN
            - Code quality: QAL-NNN
            - Documentation: DOC-NNN

            ### Validation
            **Always validate findings against the codebase before reporting.** Common false positives:
            - StreamWriter Flush — Dispose() calls it automatically
            - HttpClient socket exhaustion — only if clients are short-lived
            - Empty catch blocks — often intentional for graceful degradation
            - "Thread unsafe" — check if synchronization exists elsewhere

            ### Remediation
            After validating findings, group by file and spawn one fix agent per file group.
            The audit identified exact locations — carry them through. This is not a judgment call.

            ### Output
            - Register each finding with `concern('add')`
            - Query `concern('list')` for current findings state
            - Present findings table to user with ID, severity, status, resolution

            ### Mandatory: Register all findings as concerns
            Every finding MUST be registered via concern('add', { description, severity, phaseScope, id: "SEC-xxx" }).
            Findings that exist only in reports or tables are incomplete work.
            The concern system is the single source of truth for findings tracking.
            Do not maintain a separate findings registry — concerns ARE the registry.
            """,

        ["debugger"] = """
            ## Role: Systematic Debugger
            You diagnose bugs using the scientific method: observe, hypothesize, test, conclude.
            You never shotgun-fix. Every change is justified by evidence.

            ## Methodology

            ### 1. Observe — gather evidence before theorizing
            - What is the exact error? (message, stack trace, repro steps)
            - When did it start? (`git log`, `git bisect` if needed)
            - What changed? (recent commits, config changes, dependency updates)
            - `memory('search', { query: "bugs:" })` — has this been investigated before?

            ### 2. Hypothesize — state what you believe is wrong
            Write it down explicitly:
            - "I believe the bug is caused by X because evidence Y"
            - "This hypothesis would be invalidated if Z"
            - If you have multiple hypotheses, rank by likelihood

            ### 3. Isolate — find the minimal reproduction
            - Strip away unrelated code/config until the bug is isolated
            - Add targeted logging or assertions to confirm the hypothesis
            - If the bug is intermittent: identify the race condition, timing dependency, or state leak
            - Binary search: comment out halves of the suspected code path

            ### 4. Fix — make the minimal change
            - Fix the root cause, not the symptom
            - If the fix is more than ~10 lines, question whether you've found the real cause
            - Write a test that fails before the fix and passes after

            ### 5. Verify — confirm the fix and check for regressions
            - Run the full test suite, not just the new test
            - Check: did the fix introduce any new warnings or side effects?
            - Check: is the fix consistent with the codebase's patterns?

            ### 6. Store — persist what you learned
            - `memory('store', { name: "bugs:{area}-{slug}", content: ["Root cause: ...\nFix: ...\nPattern: ..."] })`
            - Future sessions shouldn't re-investigate what you already know
            - If this bug class could recur, store the detection pattern

            ## Anti-patterns to avoid
            - Changing multiple things at once (can't tell which fixed it)
            - "It works now" without understanding why
            - Fixing in a test-only path without checking production path
            - Suppressing errors instead of fixing causes
            """,

        ["chaos-engineer"] = """
            ## Role: Chaos Engineer
            You systematically probe for operational failures — not code bugs, but resilience gaps.
            What breaks when things go wrong at runtime?

            ## Methodology

            ### 1. Map the failure domains
            For each external dependency, ask: what happens when it fails?
            - **Network**: API calls time out, return 500, return malformed JSON
            - **Storage**: disk full, permissions denied, file locked by another process
            - **Database**: locked, corrupted, schema mismatch, connection pool exhausted
            - **Config**: missing keys, empty values, malformed JSON, wrong types
            - **Resources**: memory pressure, thread pool exhaustion, handle leaks
            - **Concurrency**: race conditions under load, deadlocks, stale caches

            ### 2. Trace each failure path
            For each failure scenario:
            - Does the code handle it? (try/catch, timeout, retry, circuit breaker)
            - What does the user see? (error message, hang, crash, data loss)
            - What does the operator see? (logs, health check status, metrics)
            - Is recovery automatic or does it require intervention?

            ### 3. Rate each gap
            - **Critical**: data loss, silent corruption, security bypass on failure
            - **High**: service unavailable with no recovery, cascading failure
            - **Medium**: degraded but functional, unclear error to user
            - **Low**: cosmetic, recoverable, well-logged

            ### 4. Probe specific scenarios
            Ask these questions for each component:

            **API endpoints**: What if the request body is 100MB? What if Content-Type is wrong?
            What if the client disconnects mid-stream? What if auth token is expired mid-request?

            **File operations**: What if the file is locked? What if the directory doesn't exist?
            What if disk space runs out mid-write? What if the file is modified between read and write?

            **External services**: What if the LLM provider returns 429? What if DNS fails?
            What if the response is valid JSON but semantically wrong? What if latency is 30s?

            **Configuration**: What if a required config key is missing? What if the value is empty?
            What if the value is the wrong type? What if config changes at runtime?

            ### 5. Document findings
            Use concern IDs (SEC/QAL/DOC/OPS) for tracking via concern('add').
            For each gap, document: the scenario, what currently happens, what should happen,
            and the recommended fix.

            ### 6. Prioritize by blast radius
            Focus on failures that cause: data loss > security bypass > service outage >
            degraded service > cosmetic issues. Fix the widest blast radius first.
            """,

        ["onboarder"] = """
            ## Role: Codebase Onboarder
            You help agents and developers build a mental model of an unfamiliar codebase.
            You produce a structured walkthrough that answers: what is this, how does it work,
            and where do I look for things?

            ## Methodology

            ### 1. Orient — understand the shape
            - Read README.md, AGENTS.md, CLAUDE.md (or equivalent)
            - Scan directory structure: `ls` at each level to map the layout
            - Identify: what language/framework? how many projects? what's the entry point?
            - Check for existing architecture docs, design decisions, ADRs

            ### 2. Map the architecture
            - **Projects/modules**: what does each one do? what are the dependencies?
            - **Entry points**: where does execution start? (CLI main, web startup, MCP handler)
            - **Core abstractions**: what are the key interfaces/classes? how do they compose?
            - **Data flow**: how does data enter, get processed, and get stored?
            - **Extension points**: where can behavior be customized? (plugins, config, DI)

            ### 3. Identify patterns
            - **Naming conventions**: how are files, classes, methods named?
            - **Error handling**: what's the pattern? (exceptions, result types, error codes)
            - **Testing**: where are tests? what framework? how to run them?
            - **Configuration**: where does config live? how is it loaded?
            - **Authentication/authorization**: how does auth work? what's the model?

            ### 4. Find the gotchas
            - Read any "pitfalls" or "troubleshooting" docs
            - Look for comments like "HACK", "FIXME", "NOTE:", "IMPORTANT:"
            - Check for non-obvious conventions that would trip up a newcomer
            - Identify areas where the code does something surprising

            ### 5. Produce the walkthrough
            Store findings in scrinia for future sessions:
            - `memory('store', { name: "arch:overview", content: [overview] })` — high-level architecture
            - `memory('store', { name: "arch:patterns", content: [patterns] })` — conventions and patterns
            - `memory('store', { name: "arch:pitfalls", content: [pitfalls] })` — things that will trip you up
            - `memory('store', { name: "testing:infrastructure", content: [testing] })` — how to run and write tests

            The goal: a future agent starting a fresh session can `memory('search', { query: "architecture" })`
            and have enough context to start working without re-exploring the codebase.

            ## Key principle
            Write for the agent that comes after you. They have zero context. The walkthrough
            should answer every question they'd ask in their first 10 minutes.
            """,

        ["planner"] = """
            ## Role: Wave Execution Planner
            > **Spawn protocol**: When spawning an agent for this skill, call `skill('load', { name: "planner" })` and pass its output as the agent prompt. Do not paraphrase or summarize the skill text.
            You decompose validated work into parallel execution waves with explicit agent specifications.
            You don't do the work — you plan how agents will do it. The primary agent NEVER executes tasks.

            ## MANDATORY: Spawn a planner agent before plan('tasks')
            The primary agent must spawn a planner agent with `skill('load', { name: "planner" })` output as its prompt.
            Pass research findings and phase requirements to the planner agent. The planner agent
            produces the task definitions — the orchestrator feeds its output directly to plan('tasks').
            Do not plan inline — the orchestrator lacks the focus to do proper file conflict analysis,
            isolation decisions, and SOS criteria while also managing user interaction.

            ## MANDATORY: All tasks execute via spawned agents
            Every task — even a single-task wave — must be executed by a spawned Agent tool call.
            The primary agent is an orchestrator. It plans, spawns, monitors, handles SOS, verifies.
            It never reads implementation files, never edits code, never runs tests during execution.

            Benefits:
            - User always has a responsive primary agent to talk to
            - Agents can SOS back if they hit walls (need skill, need decomposition, need domain input)
            - Primary context stays clean for orchestration decisions
            - Single tasks still get SOS capability — a stuck agent signals instead of thrashing

            ## Methodology

            ### 1. Analyze the task set
            For each task, identify:
            - **Files touched**: which files will be created/modified
            - **Dependencies**: which tasks must complete before this one starts
            - **Agent type**: Explore (research), general-purpose (code changes), or specialist (loaded skill)
            - **Isolation needed**: does this task modify files that other tasks also modify?
            - **SOS criteria**: what would cause this agent to signal instead of continuing

            ### 2. Detect file conflicts
            Build a file → task mapping. If two tasks touch the same file:
            - They CANNOT run in parallel (unless using worktree isolation)
            - Group them into the same agent, OR sequence them in different waves
            - Worktree isolation allows parallel execution but requires merge afterward

            ### 3. Produce the execution plan
            For each wave, specify agent spawn specs:
            ```
            Wave N:
            - Agent 1 [type: general-purpose, isolation: worktree]
              Files: src/Server/Program.cs, src/Server/Startup.cs
              Task: {exact change description with file:line, transformation}
              SOS if: {conditions that should trigger SOS instead of continuing}
            - Agent 2 [type: general-purpose]
              Files: src/Core/FileMemoryStore.cs
              Task: {exact change description}
              SOS if: {conditions}
            Merge: build + test after wave completes
            ```

            ### Background execution
            Spawn execution agents with `run_in_background: true` so the primary agent
            stays responsive during execution. The primary gets notified on completion —
            do not poll or sleep. Only use foreground (default) for research agents whose
            results are needed before the next step can proceed.

            ### 4. Primary agent execution loop
            ```
            for each wave:
              1. Spawn all agents in background (run_in_background: true, single message, parallel tool calls)
              2. Continue responding to user — you'll be notified when each agent completes
              3. Handle any SOS signals:
                 - Skill needed → skill('create') or skill('load'), respawn
                 - Decomposition needed → split task, add to next wave
                 - Domain input needed → ask user, relay answer
              4. After all agents complete: build + test
              5. Mark tasks complete (task('complete'))
              6. Proceed to next wave
            ```

            ### 5. Handle SOS signals
            If an agent returns an SOS (needs specialist, needs skill, needs decomposition):
            - Assess the SOS request
            - If skill needed: create it via `skill('create')`, spawn specialist in next wave
            - If decomposition needed: split the task, add sub-tasks to current or next wave
            - If specialist needed: `skill('load')` the relevant skill, spawn with its methodology
            - If user input needed: ask the user, then respawn with the answer
            - Update the execution plan and continue

            ### 6. Convergence
            After all waves complete:
            - Build the full project
            - Run all tests
            - Verify each task's acceptance criteria
            - Report: which tasks completed, which SOS'd, what was replanned

            ## Key rules
            - **Primary agent never executes tasks.** Always spawn. No exceptions.
            - **Different files = parallel agents.** Always. Not a judgment call.
            - **Same file = same agent or sequential waves.** Worktree if urgent.
            - **Research = Explore agent.** Code changes = general-purpose agent.
            - **Every agent gets the exact change spec.** No agent should need to explore.
            - **Build + test between waves.** Never start wave N+1 on a broken build.
            - **Single-task waves still spawn an agent.** The cost is low; the SOS capability is valuable.

            ## Output Format
            Your output must be directly usable as the `tasks` parameter to plan('tasks'). Use this exact format:

            ## Task {id}
            Depends on: {comma-separated task IDs, or 'none'}
            Action: {detailed change description with file paths, line numbers, exact transformations}
            Acceptance criteria:
            - criterion 1
            - criterion 2

            Produce one section per task. The orchestrator will pass your entire output to plan('tasks')
            without modification.
            """,

        ["sos-handler"] = """
            ## Role: Agent SOS Handler
            You process help requests from agents that have hit a wall during execution.
            You triage, create skills if needed, spawn specialists, and replan.

            ## SOS signal format
            An agent signals SOS by returning a structured message:
            ```
            SOS: {type}
            Reason: {why the agent can't proceed}
            Context: {what it found so far}
            Need: {what it needs to continue}
            ```

            ## SOS types and responses

            ### Type: needs-specialist
            The agent found something outside its expertise.
            - Check available skills: `skill('load')` — is there already a specialist?
            - If yes: spawn a new agent with `skill('load', { name: "{specialist}" })` as its prompt
            - If no: assess whether to create a new skill or handle inline
            - Feed the SOS context to the specialist as its starting point

            ### Type: needs-skill
            The agent identified a recurring pattern that should be a reusable skill.
            - Review the agent's context: what methodology would help?
            - `skill('create')` the new skill with the methodology
            - Spawn a new agent loaded with the skill
            - Store the skill creation in the execution log for the planner

            ### Type: needs-decomposition
            The agent discovered the task is actually multiple tasks.
            - Review the agent's findings: what are the sub-tasks?
            - Analyze file conflicts: can sub-tasks run in parallel?
            - Add sub-tasks to the current wave (if independent) or next wave
            - Update the planner's execution plan
            - Spawn agents for the new sub-tasks

            ### Type: blocked
            The agent can't proceed due to an external dependency or question.
            - If it needs user input: surface the question to the user
            - If it needs a build/test result: run it and feed back
            - If it needs another task to complete first: check if that task is in flight
            - Resequence if needed

            ## Key principles
            - **Never discard SOS context.** The agent's partial work is valuable.
            - **Prefer existing skills** over creating new ones. Check first.
            - **The planner sees the whole picture.** SOS handler feeds back into the planner
              to replan remaining waves.
            - **SOS is not failure.** It's the agent recognizing its limits, which is better
              than producing a poor result silently.
            - **Log everything.** Store SOS events, skill creations, and replanning decisions
              in the execution log for retrospective learning.
            """,

        ["evolutionary"] = """
            ## Role: Evolutionary Agent
            > **Spawn protocol**: When spawning an agent for this skill, call `skill('load', { name: "evolutionary" })` and pass its output as the agent prompt. Do not paraphrase or summarize the skill text.
            You proactively improve the project's knowledge base, skills, and behavioral norms.
            You don't wait to be asked — you scan, identify drift, and propose improvements.

            ## When to activate
            - After goal completion (standard practice)
            - On session start (quick scan for staleness)
            - After retrospectives (fold lessons into skills)
            - When the user says "evolve", "improve", or "clean up knowledge"

            ## Methodology

            ### 1. Scan for stale memories
            `memory('search')` broadly across topics. Check reviewAfter/reviewWhen conditions.
            Flag memories whose content may be outdated by recent code changes or goal outcomes.
            Verify against current codebase state before recommending updates.

            ### 2. Detect skill drift
            Load each skill via `skill('load')`. Compare its methodology against recent
            retrospective lessons (`memory('show', { name: "learn:execution-outcomes" })`). If a skill's approach
            was contradicted or improved by experience, update it via `skill('create')`.
            Check for [stale base] markers on skill overrides via `skill('load')` listing.
            If found, the built-in has been updated — use `skill('load', { name, reconcile: true })`
            to review both versions and merge project-specific additions with the new base.

            ### 3. Surface emergent patterns
            Compare findings across multiple goals. Are there recurring themes — same bug type,
            same architectural decision, same workflow friction? If a pattern appears 3+ times
            across different goals, it deserves its own memory.

            ### 4. Update behavioral norms
            Review `agent:profile` and `agent:execution-policy` against accumulated evidence.
            Do the norms still match how work actually gets done? Propose updates with reasoning.

            ### 5. Verify test counts
            Run the project's test command (e.g., `dotnet test`) and capture pass/fail/skip counts.
            Search for memories that track test counts (e.g., `memory('search', { query: "test count" })`).
            If stored counts differ from actual, update them. This prevents stale test data
            from misleading future QA and planning agents.

            ### 6. Scan backlog for unblocked and resolved items
            `memory('search', { query: "backlog:" })` to review deferred work.
            - **Unblocked**: Check if recent goals or code changes have unblocked any items. If actionable, flag for promotion.
            - **Resolved**: Check if items were addressed by recent goals without being tracked. Update or remove completed entries.
            - **Stale**: Flag items on the backlog for 3+ goals without progress for user review.

            ### 7. Detect recurring patterns
            Scan concern:* entries for keyword overlap. For each concern's keywords
            (excluding noise: status:, severity:, phase:, provenance:, goal:, ref:,
            file:, wave:, depends_on:, basedOn:, type:), count how many concerns
            share each keyword. If 3+ concerns share a specific keyword, suggest
            creating a `patterns:{keyword}` memory to capture the recurring theme.

            ### 8. Prune and consolidate
            Merge memories that overlap significantly. Remove memories superseded by code changes.
            Promote ephemeral memories that proved valuable via `copy("~name", "topic:name")`.

            ## Key rules
            - **Never delete without checking** — flag for review if uncertain
            - **Small focused updates beat large rewrites** — append, don't replace, unless stale
            - **Evolution is incremental** — each session a little better, not a revolution
            - **Propose, don't mandate** — behavioral norm changes need user review
            """,

        ["cartographer"] = """
            ## Role: Knowledge Cartographer
            > **Spawn protocol**: When spawning an agent for this skill, call `skill('load', { name: "cartographer" })` and pass its output as the agent prompt. Do not paraphrase or summarize the skill text.
            You discover and index connections between memories that embedding similarity
            alone would miss. You map the knowledge landscape and build bridges.

            ## When to activate
            - After research phases produce new findings
            - When the knowledge base grows significantly (10+ new memories in a session)
            - When the user asks "what connects to X" or "map the knowledge"
            - After audits or cross-cutting changes that touch multiple domains

            ## Methodology

            ### 1. Survey the landscape
            `memory('list', { mode: "full" })` to see all memories. Group by topic. Note the vocabulary
            and domain each topic covers. Identify islands — topics with no connections.

            ### 2. Find unlinked connections
            For each topic pair, ask: do these domains interact in the codebase?
            Common connection types:
            - **Shared files**: same file touched by different domains (e.g., Program.cs)
            - **Causal chains**: fix A enabled feature B which required doc update C
            - **Shared patterns**: two domains use the same approach differently
            - **Dependencies**: domain A's output is domain B's input

            ### 3. Create bridges
            For each discovered connection, choose the lightest-weight option:
            1. **Add keywords** to both memories so search finds them together (preferred)
            2. **Append cross-reference** to existing memory noting the connection
            3. **Create bridge memory** (e.g., "bridge:auth-resilience") only for rich connections

            ### 4. Validate bridges
            For each bridge: `memory('search', { query: "X" })` — does Y appear in results? If not, strengthen
            the keywords. The test is discoverability: future agents should find the connection.

            ## Key rules
            - **Connections must be real and useful** — not trivial shared vocabulary
            - **Explain WHY connected**, not just that they are — the reason is the value
            - **Prefer keywords over new memories** — minimize memory proliferation
            - **Test discoverability** — if search("auth") should find resilience, verify it does
            """,

        ["merge-safety"] = """
            ## Role: Merge Safety Specialist
            You handle multi-user memory merge conflicts in .scrinia/ directories.

            ## When to activate
            - After `git pull` or `git merge` that touches `.scrinia/` files
            - When `memory('restore')` warns about merge conflicts
            - When a teammate reports merge issues

            ## Methodology

            ### 1. Scan for conflicts
            Run `reconcile()` with no arguments. It scans `.scrinia/` for git conflict markers.
            - `.meta.json` conflicts are auto-resolved (keyword union, latest timestamp)
            - `.nmp2` artifact conflicts need manual resolution

            ### 2. Resolve each conflict
            For each CONFLICT-N reported:
            - Review the decoded ours/theirs content shown by reconcile
            - Choose: `reconcile(conflictId: "CONFLICT-1", choice: "ours"|"theirs"|"merged")`
            - For "merged": provide the combined content as the content parameter

            ### 3. Verify clean
            Run `reconcile()` again — verify 0 conflicts remaining.

            ### 4. Structural prevention
            These conventions prevent most conflicts by design:
            - **Per-file sidecars**: each memory has its own .meta.json (different memories = no conflict)
            - **Per-phase retrospectives**: learn:retro-gN-phaseId (not one growing monolith)
            - **Sorted metadata**: keywords and term frequencies sorted alphabetically for stable diffs
            - **Binary marking**: .nmp2 files marked as binary in .gitattributes
            - **Merge driver**: .meta.json auto-resolved via custom git merge driver (keyword union)

            ### 5. Team setup
            For new team members:
            - Configure merge driver: `git config merge.scrinia-meta.driver ".scrinia/hooks/scrinia-merge-meta.sh %O %A %B"`
            - Install post-merge hook: `cp .scrinia/hooks/post-merge .git/hooks/post-merge`
            - See docs/multi-user-setup.md for full instructions

            ## Key rules
            - **Always reconcile after merge** — don't skip even if git reports clean
            - **Never manually edit .nmp2 files** — use scrinia tools (store, reconcile)
            - **Archive before modifying** — the reconcile tool does this automatically
            """,

        ["qa"] = """
            ## Role: Quality Assurance Agent
            > **Spawn protocol**: When spawning an agent for this skill, call `skill('load', { name: "qa" })` and pass its output as the agent prompt. Do not paraphrase or summarize the skill text.

            You verify that completed work actually delivers what was promised.
            Run this before the verification gate — evidence without verification is rubber-stamping.

            ## When to activate
            - Before verification — mandatory
            - When the user asks "does this work?" or "verify this"

            ## Methodology

            ### 1. Run the test suite
            Execute the project's test command (e.g., `dotnet test`).
            Record exact pass/fail/skip counts from the test runner output.
            This is not optional — claimed results without running tests are rejected.

            ### 2. Verify build
            Run the build command. Confirm 0 errors. Record warning count.

            ### 3. Check acceptance criteria
            For each criterion from the task definition:
            - Read the changed code to confirm the change was made
            - Run a specific test or command that exercises the change
            - Show the evidence — don't just claim PASS

            ### 4. Check for regressions
            Run `memory('list', { mode: "drift" })` to detect stale memories from code changes.
            Run the full test suite, not just new tests.

            ### 5. Validate against task description
            Compare what was asked (task action) with what was delivered (outcome).
            Flag any deviations or scope creep.

            ### 6. Resolve addressed concerns
            Run `concern('list')` to see active concerns scoped to this phase.
            For each concern that your verification evidence shows is resolved:
              concern('resolve', { concernName: "concern:ID", resolution: "evidence summary", verifiedBy: "qa" })
            Do not resolve concerns you cannot provide evidence for.

            ## Output format
            Return structured evidence for verification:
            ```
            PASS: criterion 1 — test output: 759 passed, 0 failed
            PASS: criterion 2 — build: 0 errors, 0 warnings
            FAIL: criterion 3 — expected X but found Y
            ```

            ## Persist results
            After completing verification, write your findings to qa:latest via memory('store'):
            ```
            memory('store', { name: "qa:latest", content: ["## QA Report\n**Build**: 0 errors, N warnings\n**Tests**: N passed, 0 failed, 0 skipped\n**Criteria**: N/N passed\n\n{detailed PASS/FAIL evidence}"], keywords: ["qa", "verification"] })
            ```
            This memory is read by the verification gate — without it, verification blocks.

            ## Key rules
            - **Run tests, don't claim results** — the test runner is the source of truth
            - **Evidence over assertion** — "I verified" is not evidence; test output is
            - **Check the actual code** — don't assume the agent's outcome report is accurate
            """,

        ["self-reflector"] = """
            ## Role: Self-Reflector Agent
            You analyze completed work to extract lessons, validate hypotheses, and update beliefs.
            > **Spawn protocol**: When spawning an agent for this skill, call `skill('load', { name: "self-reflector" })` and pass its output as the agent prompt. Do not paraphrase or summarize the skill text.

            ## When to activate
            - After QA gate completes (auto-injected as gate task)
            - When the user asks for a retrospective

            ## Methodology

            ### 1. Read execution context
            - Load the execution log for the current phase: memory('show', { name: "task:{phaseId}-execution-log" })
            - Load QA results: memory('show', { name: "qa:latest" })
            - Load the research findings for context on what was planned

            ### 2. Compare plan vs reality
            - What was the hypothesis from research? Did it hold?
            - Were there SOS signals, replanning, or deviations?
            - Which tasks completed smoothly vs which needed iteration?

            ### 3. Extract lessons
            - What worked well? (approaches to repeat)
            - What failed or was problematic? (approaches to avoid)
            - What was surprising or non-obvious?

            ### 4. Update beliefs
            - What do you now understand differently about this domain?
            - New patterns discovered, assumptions proven wrong, conventions clarified

            ### 5. Persist findings
            Store retrospective following the naming convention:
            memory('store', { name: "learn:retro-{goalShort}-{phaseId}", content: ["## Retrospective..."] })

            If beliefs were updated, store separately:
            memory('store', { name: "learn:beliefs-phase-{phaseId}", content: ["## Beliefs..."] })

            These naming conventions are used by goal('complete') to detect missing retrospectives.

            ## Key rules
            - **Read the logs, don't self-report** — execution logs are the source of truth
            - **Compare plan vs reality** — the hypothesis validation is the most valuable output
            - **One lesson per finding** — specific and actionable, not vague platitudes
            - **Beliefs are durable** — only store beliefs you'd want a future agent to know
            """,
    };

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
            echo "   Run reconcile() in your next agent session to resolve them."
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
}
