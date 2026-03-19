using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;

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
/// the plan:*, task:*, project:*, and learn:* topic conventions.
/// </summary>
[McpServerToolType]
public sealed class ScriniaProjectTools
{
    private const int MaxResponseChars = 8 * 1024;

    private static IMemoryStore CurrentStore =>
        MemoryStoreContext.Current ?? throw new InvalidOperationException(
            "No memory store configured. Call MemoryStoreContext.Current = ... before using planning tools.");

    // ── MCP Tools ────────────────────────────────────────────────────────────

    /// <summary>Initialize a project by storing goals, context, and constraints.</summary>
    [McpServerTool(Name = "project_init"), Description(
        "Initialize a project by storing goals, context, and constraints. " +
        "The agent should compose goals, constraints, and scope as free-text in the context parameter. " +
        "Returns the workspace-derived project ID. " +
        "Note: this writes to .scrinia/ in the workspace — treat those file changes as your own.")]
    public async Task<string> ProjectInit(
        [Description("Free-text describing the project goals, context, constraints, and scope.")] string context,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;
        string projectId = DeriveProjectId(store);
        string projectName = ToProjectName(projectId);

        await WritePlanningMemoryAsync(store, "project:context", context, archiveExisting: true, cancellationToken);
        await WriteStateAsync(store, projectName, projectId,
            phase: "Not started",
            progressPct: "0",
            lastAction: "Project initialized",
            blockers: "none",
            nextStep: "run plan_requirements to define project requirements",
            cancellationToken);

        return $"Initialized project '{projectId}'. Stored: project:context, project:state. " +
               $"Files in .scrinia/ were updated — these are your changes.";
    }

    /// <summary>Store project requirements with category grouping and REQ-IDs.</summary>
    [McpServerTool(Name = "plan_requirements"), Description(
        "Store project requirements with category grouping and REQ-IDs. " +
        "The agent formats categories (e.g. Foundation, API, UI) and REQ-IDs with v1/v2 scope labels in the requirements text. " +
        "Note: this writes to .scrinia/ in the workspace — treat those file changes as your own.")]
    public async Task<string> PlanRequirements(
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
            nextStep: "run plan_roadmap to create phased roadmap",
            cancellationToken);

        return "Stored: project:requirements. Files in .scrinia/ were updated — these are your changes.";
    }

    /// <summary>Store a phased roadmap that maps requirements to phases.</summary>
    [McpServerTool(Name = "plan_roadmap"), Description(
        "Store a phased roadmap that maps requirements to phases. " +
        "Validates that every REQ-ID from project:requirements appears in exactly one phase. " +
        "Note: this writes to .scrinia/ in the workspace — treat those file changes as your own.")]
    public async Task<string> PlanRoadmap(
        [Description("Free-text phased roadmap. Each phase should reference the REQ-IDs it covers.")] string roadmap,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Read requirements to extract REQ-IDs
        string requirementsText;
        try { requirementsText = await ReadMemoryAsync(store, "project:requirements", cancellationToken); }
        catch (FileNotFoundException)
        {
            return "Error: no requirements found. Run plan_requirements first.";
        }

        // Extract REQ-IDs from requirements and roadmap
        var reqPattern = new Regex(@"\b([A-Z]+-\d+)\b");
        var reqIds = reqPattern.Matches(requirementsText)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var roadmapIdList = reqPattern.Matches(roadmap)
            .Select(m => m.Groups[1].Value)
            .ToList();

        // Detect duplicate REQ-IDs across phases (same ID in multiple phases)
        var duplicates = roadmapIdList
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .OrderBy(id => id)
            .ToList();

        if (duplicates.Count > 0)
        {
            return $"Error: REQ-IDs appear in more than one phase: {string.Join(", ", duplicates)}. " +
                   "Every requirement must appear in exactly one phase.";
        }

        var roadmapIds = roadmapIdList.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Validate: every REQ-ID from requirements must appear in roadmap
        var missingIds = reqIds.Where(id => !roadmapIds.Contains(id)).OrderBy(id => id).ToList();
        if (missingIds.Count > 0)
        {
            return $"Error: roadmap is missing required REQ-IDs: {string.Join(", ", missingIds)}. " +
                   $"Every requirement must appear in exactly one phase.";
        }

        // Extra IDs in roadmap not in requirements: allowed — agent may define new ones
        var extraIds = roadmapIds.Where(id => !reqIds.Contains(id)).OrderBy(id => id).ToList();
        string extraNote = extraIds.Count > 0
            ? $" Note: {extraIds.Count} REQ-ID(s) in the roadmap are not in project:requirements: {string.Join(", ", extraIds)}."
            : "";

        await WritePlanningMemoryAsync(store, "plan:roadmap", roadmap, archiveExisting: true, cancellationToken);

        // Count phases (lines starting with "### Phase" or "Phase \d")
        int phaseCount = CountPhases(roadmap);

        // Update state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);

        await WriteStateAsync(store, projectName, projectId,
            phase: phaseCount > 0 ? $"Roadmap created ({phaseCount} phases)" : "Roadmap created",
            progressPct: "20",
            lastAction: "Roadmap created",
            blockers: "none",
            nextStep: "run plan_tasks for phase 1",
            cancellationToken);

        // Optionally surface learn:patterns as a hint for the roadmap author
        string patternNote = "";
        try
        {
            string patterns = await ReadMemoryAsync(store, "learn:patterns", cancellationToken);
            string hint = patterns.Length > 300 ? patterns[..300] + "..." : patterns;
            patternNote = $" Patterns from prior phases: {hint}";
        }
        catch { /* no learn:patterns yet — skip silently */ }

        return $"Stored: plan:roadmap. Files in .scrinia/ were updated — these are your changes.{extraNote}{patternNote}";
    }

    /// <summary>Decompose a phase into task memories with keyword-based metadata.</summary>
    [McpServerTool(Name = "plan_tasks"), Description(
        "Decompose a phase into task memories with keyword-based metadata for status, wave, phase, and dependencies. " +
        "Call research_complete to store research:{phaseId}-{topic} findings before decomposing tasks. " +
        "Each task is stored as task:{phaseId}-{wave}-{id} with keywords: " +
        "status:pending, wave:N, phase:XX, and depends_on:{subject} for each dependency. " +
        "Requires plan:roadmap to exist (run plan_roadmap first). " +
        "Note: this writes to .scrinia/ in the workspace — treat those file changes as your own.")]
    public async Task<string> PlanTasks(
        [Description("Two-digit phase number (e.g. '01').")] string phaseId,
        [Description(
            "Free-text task definitions. Each task section uses this format:\n" +
            "## Task {id}\n" +
            "Wave: {N}\n" +
            "Depends on: {comma-separated subject names, or 'none'}\n" +
            "Action: {what to do}\n" +
            "Acceptance criteria:\n" +
            "- criterion 1\n" +
            "- criterion 2")] string tasks,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Prerequisite check: plan:roadmap must exist
        try
        {
            await ReadMemoryAsync(store, "plan:roadmap", cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return "Error: no roadmap found. Run plan_roadmap first.";
        }

        // Parse task sections from free-text input
        var parsedTasks = ParseTaskSections(tasks);
        if (parsedTasks.Count == 0)
            return "Error: no tasks found. Provide tasks using '## Task {id}' section headers.";

        int waveCount = parsedTasks.Select(t => t.Wave).Distinct().Count();
        var createdNames = new List<string>();

        foreach (var task in parsedTasks)
        {
            // Build keywords: status:pending, wave:N, phase:XX, depends_on:* entries
            var keywords = new List<string>
            {
                "status:pending",
                $"wave:{task.Wave}",
                $"phase:{phaseId}"
            };
            foreach (string dep in task.DependsOn)
                keywords.Add($"depends_on:{dep}");

            // Task naming: task:{phaseId}-{wave}-{id}
            string taskName = $"task:{phaseId}-{task.Wave}-{task.Id}";

            await WritePlanningMemoryAsync(store, taskName, task.Content,
                archiveExisting: false, keywords: [.. keywords], cancellationToken);

            createdNames.Add(taskName);
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
            nextStep: $"run task_next to get first task for phase {phaseId}",
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

        string taskList = string.Join("\n", createdNames.Select(n => $"  - {n}"));
        string response =
            $"Created {parsedTasks.Count} task(s) for phase {phaseId} in {waveCount} wave(s).\n" +
            $"Tasks stored:\n{taskList}\n" +
            $"Files in .scrinia/ were updated — these are your changes.\n" +
            $"Next: run task_next to get the first pending task." +
            patternNote;

        if (response.Length > MaxResponseChars)
            response = response[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

        return response;
    }

    /// <summary>Resume project context after context loss.</summary>
    [McpServerTool(Name = "plan_resume"), Description(
        "Resume project context after context loss. Returns a structured summary of current project " +
        "state including project name, current phase, progress, last action, blockers, and a concrete " +
        "next-step suggestion. If project state is missing or corrupted, attempts to rebuild from " +
        "existing project memories. " +
        "Note: reads from .scrinia/ in the workspace.")]
    public async Task<string> PlanResume(CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        string response;
        try
        {
            response = await ReadMemoryAsync(store, "project:state", cancellationToken);
        }
        catch (FileNotFoundException)
        {
            string? rebuilt = await RebuildStateFromMemoriesAsync(store, cancellationToken);
            if (rebuilt is null)
                return "Error: no project found. Run project_init first.";
            response = rebuilt;
        }

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
            capabilityHints += "\nHint: concern tracking is available — use concern_add to track risks and issues across phases.";

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
            capabilityHints += "\nHint: body of knowledge is available — use knowledge_add to store domain expertise that persists across sessions.";

        response += capabilityHints;

        if (response.Length > MaxResponseChars)
            response = response[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

        return response;
    }

    /// <summary>Query current project status.</summary>
    [McpServerTool(Name = "plan_status"), Description(
        "Query current project status. Returns current phase, progress percentage, and any blockers. " +
        "Works with partial project state (e.g., only project:context exists with no roadmap yet). " +
        "Note: reads from .scrinia/ in the workspace.")]
    public async Task<string> PlanStatus(CancellationToken cancellationToken = default)
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
        string progress = ExtractStateField(stateText, "Progress:") ?? "0%";
        string blockers = ExtractStateField(stateText, "Blockers:") ?? "none";
        string next = ExtractStateField(stateText, "Next:") ?? "(not set)";
        string lastAction = ExtractStateField(stateText, "Last action:") ?? "(not set)";

        // Optionally enrich with roadmap summary
        string roadmapNote = "";
        try
        {
            string roadmap = await ReadMemoryAsync(store, "plan:roadmap", cancellationToken);
            int phaseCount = CountPhases(roadmap);
            if (phaseCount > 0)
                roadmapNote = $"\nRoadmap: {phaseCount} phase(s) defined";
        }
        catch (FileNotFoundException) { /* roadmap not yet created — skip silently */ }

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

        string response =
            $"Project: {projectName}\n" +
            $"Phase: {phase}\n" +
            $"Progress: {progress}\n" +
            $"Last action: {lastAction}\n" +
            $"Blockers: {blockers}\n" +
            $"Next: {next}" +
            roadmapNote + concernNote + goalNote;

        if (response.Length > MaxResponseChars)
            response = response[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

        return response;
    }

    /// <summary>Returns all unblocked tasks in the current wave for a phase.</summary>
    [McpServerTool(Name = "task_next"), Description(
        "Returns all unblocked tasks in the current wave for a phase. " +
        "The agent decides which to execute and in what order. Call task_complete when done.")]
    public async Task<string> TaskNext(
        [Description("Two-digit phase number (e.g. '01').")] string phaseId,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Get task scope via ParseQualifiedName — "local-topic:task" scope
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");

        // Keyword-only scan — no ResolveArtifactAsync during filtering
        var allEntries = store.LoadIndex(taskScope);

        // Filter to tasks for this phase
        var phaseEntries = allEntries
            .Where(e => HasKeyword(e, $"phase:{phaseId}"))
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

        // Build a HashSet of completed task names for dependency checking
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
            progressPct: ExtractStateField(stateText, "Progress:")?.TrimEnd('%') ?? "30",
            lastAction: $"task_next called for phase {phaseId} wave {currentWave}",
            blockers: "none",
            nextStep: $"execute tasks for phase {phaseId} wave {currentWave}, then call task_complete for each",
            cancellationToken);

        string response = sb.ToString();
        if (response.Length > MaxResponseChars)
            response = response[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

        return response;
    }

    /// <summary>Verify a phase achieved its goal using success criteria from the roadmap.</summary>
    [McpServerTool(Name = "plan_verify"), Description(
        "Verify a phase achieved its goal using success criteria from the roadmap. " +
        "Returns structured pass/fail per criterion with evidence. " +
        "Can be called before execution to check plan coverage (PLAN-03). " +
        "Note: reads from .scrinia/ in the workspace.")]
    public async Task<string> PlanVerify(
        [Description("Two-digit phase number (e.g. '01').")] string phaseId,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Read plan:roadmap — required
        string roadmapText;
        try { roadmapText = await ReadMemoryAsync(store, "plan:roadmap", cancellationToken); }
        catch (FileNotFoundException)
        {
            return "Error: no roadmap found. Run plan_roadmap first.";
        }

        // Extract success criteria scoped to target phase
        var criteria = ExtractPhaseCriteria(roadmapText, phaseId);
        if (criteria.Count == 0)
            return $"No success criteria found for phase {phaseId}.";

        // Load task index for this phase (keyword-only scan)
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var allEntries = store.LoadIndex(taskScope);
        var phaseEntries = allEntries.Where(e => HasKeyword(e, $"phase:{phaseId}")).ToList();
        int totalTasks = phaseEntries.Count;
        int completeTasks = phaseEntries.Count(e => HasKeyword(e, "status:complete"));

        // Try read execution log
        string? logText = null;
        try { logText = await ReadMemoryAsync(store, $"task:{phaseId}-execution-log", cancellationToken); }
        catch (FileNotFoundException) { /* no log yet — acceptable */ }

        // Check each criterion
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Phase Verification: {phaseId}");

        int passCount = 0;
        var criterionResults = new List<(bool pass, string criterion, string evidence)>();

        foreach (string criterion in criteria)
        {
            bool pass;
            string evidence;

            string lower = criterion.ToLowerInvariant();

            if (lower.Contains("task") || lower.Contains("complete") || lower.Contains("all"))
            {
                // Task completion criterion
                if (totalTasks == 0)
                {
                    pass = false;
                    evidence = "No tasks found for this phase";
                }
                else if (completeTasks == totalTasks)
                {
                    pass = true;
                    evidence = $"All {totalTasks} tasks complete";
                }
                else
                {
                    pass = false;
                    evidence = $"{totalTasks - completeTasks} of {totalTasks} tasks incomplete";
                }
            }
            else if (lower.Contains("execution log") || lower.Contains("log") || lower.Contains("completion entr"))
            {
                // Execution log criterion
                if (logText is not null)
                {
                    pass = true;
                    evidence = $"Execution log exists ({logText.Length} chars, {completeTasks} completion record(s))";
                }
                else
                {
                    pass = false;
                    evidence = $"No execution log found (task:{phaseId}-execution-log missing)";
                }
            }
            else
            {
                // Generic criterion — check execution log content for keywords
                if (logText is not null)
                {
                    // Extract significant words from criterion (4+ chars)
                    var words = Regex.Matches(criterion, @"\b\w{4,}\b")
                        .Select(m => m.Value.ToLowerInvariant())
                        .Where(w => !new[] { "must", "that", "with", "from", "this", "have", "been", "were" }.Contains(w))
                        .Take(3)
                        .ToList();

                    bool found = words.Count == 0 || words.Any(w =>
                        logText.Contains(w, StringComparison.OrdinalIgnoreCase));

                    if (found)
                    {
                        pass = true;
                        string snippet = logText[..Math.Min(80, logText.Length)].Replace('\n', ' ');
                        evidence = $"Execution log contains: {snippet}...";
                    }
                    else
                    {
                        pass = false;
                        evidence = "Criterion not evidenced in execution log";
                    }
                }
                else
                {
                    pass = false;
                    evidence = $"No execution log found — cannot verify criterion";
                }
            }

            criterionResults.Add((pass, criterion, evidence));
            if (pass) passCount++;
        }

        // Overall status
        string status = passCount == criteria.Count
            ? "ALL_PASS"
            : passCount == 0
                ? "ALL_FAIL"
                : $"PARTIAL ({passCount}/{criteria.Count} passed)";

        sb.AppendLine($"Status: {status}");
        sb.AppendLine();

        foreach (var (pass, criterion, evidence) in criterionResults)
        {
            sb.AppendLine($"{(pass ? "PASS" : "FAIL")}: {criterion}");
            sb.AppendLine($"  Evidence: {evidence}");
            sb.AppendLine();
        }

        // Update project:state with verification results
        string stateText2;
        try { stateText2 = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText2 = ""; }

        string projectName2 = ExtractStateField(stateText2, "Project:") ?? "Unknown Project";
        string projectId2 = ExtractStateField(stateText2, "ID:") ?? DeriveProjectId(store);
        string currentPhase2 = ExtractStateField(stateText2, "Phase:") ?? $"Phase {phaseId}";
        string progressPct2 = ExtractStateField(stateText2, "Progress:")?.TrimEnd('%') ?? "50";

        await WriteStateAsync(store, projectName2, projectId2,
            phase: currentPhase2,
            progressPct: progressPct2,
            lastAction: $"plan_verify for phase {phaseId}: {status}",
            blockers: passCount < criteria.Count ? $"{criteria.Count - passCount} criteria failed" : "none",
            nextStep: passCount < criteria.Count
                ? "run plan_gaps to create gap closure tasks"
                : "phase verification complete",
            cancellationToken);

        string response = sb.ToString();
        if (response.Length > MaxResponseChars)
            response = response[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

        return response;
    }

    /// <summary>Create gap closure tasks for failed verification criteria and re-open the phase.</summary>
    [McpServerTool(Name = "plan_gaps"), Description(
        "Create gap closure tasks for failed verification criteria. " +
        "Re-opens the phase for another execution cycle. " +
        "Call after plan_verify identifies failures. " +
        "Note: this writes to .scrinia/ in the workspace — treat those file changes as your own.")]
    public async Task<string> PlanGaps(
        [Description("Two-digit phase number (e.g. '01').")] string phaseId,
        [Description("Newline-separated list of failed criterion texts (from plan_verify output).")] string failedCriteria,
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

        // Create a gap task for each failed criterion
        var createdNames = new List<string>();
        for (int i = 0; i < criteria.Count; i++)
        {
            string criterion = criteria[i];
            string gapTaskName = $"task:{phaseId}-gap-{(i + 1):D2}";
            string[] gapKeywords = ["status:pending", "wave:1", $"phase:{phaseId}", "gap_closure:true"];
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
        string progressPct = ExtractStateField(stateText, "Progress:")?.TrimEnd('%') ?? "50";

        await WriteStateAsync(store, projectName, projectId,
            phase: $"Phase {phaseId} (re-opened for gap closure)",
            progressPct: progressPct,
            lastAction: $"Gap closure: {criteria.Count} task(s) created for phase {phaseId}",
            blockers: "none",
            nextStep: "run task_next to work on gap tasks",
            cancellationToken);

        string taskList = string.Join("\n", createdNames.Select(n => $"  - {n}"));
        string response =
            $"Created {criteria.Count} gap closure task(s) for phase {phaseId}. Phase re-opened. Run task_next to begin.\n" +
            $"Gap tasks created:\n{taskList}";

        if (response.Length > MaxResponseChars)
            response = response[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

        return response;
    }

    /// <summary>Open a research investigation and store questions under research:{phaseId}-{topic}.</summary>
    [McpServerTool(Name = "research_start"), Description(
        "Open a research investigation before plan_tasks. " +
        "Stores research:{phaseId}-{topic} with status:active. " +
        "Call research_complete when findings are ready, then call plan_tasks.")]
    public async Task<string> ResearchStart(
        [Description("Two-digit phase number (e.g. '06').")] string phaseId,
        [Description("Research topic slug — used as the memory name suffix (e.g. 'auth', 'storage').")] string topic,
        [Description("Questions to investigate during this research session.")] string questions,
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

        string memoryName = $"research:{phaseId}-{topic}";
        string content =
            $"## Research Investigation\n" +
            $"Phase: {phaseId}\n" +
            $"Topic: {topic}\n\n" +
            $"## Questions\n{questions}";

        await WritePlanningMemoryAsync(store, memoryName, content,
            archiveExisting: true,
            keywords: ["status:active", $"phase:{phaseId}"],
            cancellationToken);

        // Update project:state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? $"Phase {phaseId}";
        string progressPct = ExtractStateField(stateText, "Progress:")?.TrimEnd('%') ?? "0";

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: progressPct,
            lastAction: $"Research started: {memoryName}",
            blockers: "none",
            nextStep: $"investigate questions, then call research_complete(\"{phaseId}\", \"{topic}\", findings)",
            cancellationToken);

        string response = $"Research investigation started. Stored as {memoryName} with status:active. " +
                          $"Files in .scrinia/ were updated — these are your changes. " +
                          $"Call research_complete when you have findings.";

        if (response.Length > MaxResponseChars)
            response = response[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

        return response;
    }

    /// <summary>Complete a research investigation with findings.</summary>
    [McpServerTool(Name = "research_complete"), Description(
        "Complete a research investigation with findings. " +
        "Call after research_start, before plan_tasks. " +
        "Overwrites research:{phaseId}-{topic} memory with status:complete.")]
    public async Task<string> ResearchComplete(
        [Description("Two-digit phase number (e.g. '06').")] string phaseId,
        [Description("Research topic slug — must match the topic used in research_start.")] string topic,
        [Description("Findings from the research investigation.")] string findings,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Verify research:{phaseId}-{topic} exists with status:active
        string memoryName = $"research:{phaseId}-{topic}";
        var (researchScope, researchSubject) = store.ParseQualifiedName(memoryName);
        var allEntries = store.LoadIndex(researchScope);
        var existing = allEntries.FirstOrDefault(e => e.Name == researchSubject);

        if (existing is null || !HasKeyword(existing, "status:active"))
        {
            return $"Error: no active research found for '{memoryName}'. Call research_start first.";
        }

        // Build updated content with findings
        string existingContent;
        try { existingContent = await ReadMemoryAsync(store, memoryName, cancellationToken); }
        catch (FileNotFoundException) { existingContent = ""; }

        string updatedContent =
            existingContent.TrimEnd() + "\n\n" +
            $"## Findings\n{findings}";

        await WritePlanningMemoryAsync(store, memoryName, updatedContent,
            archiveExisting: true,
            keywords: ["status:complete", $"phase:{phaseId}"],
            cancellationToken);

        // Update project:state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? $"Phase {phaseId}";
        string progressPct = ExtractStateField(stateText, "Progress:")?.TrimEnd('%') ?? "0";

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: progressPct,
            lastAction: $"Research complete: {memoryName}",
            blockers: "none",
            nextStep: $"call plan_tasks(\"{phaseId}\", ...) to decompose tasks using research findings",
            cancellationToken);

        string response = $"Research complete. {memoryName} updated with findings and status:complete. " +
                          $"Files in .scrinia/ were updated — these are your changes. " +
                          $"Call plan_tasks to decompose tasks using these findings.";

        if (response.Length > MaxResponseChars)
            response = response[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

        return response;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to reconstruct project state from available memories when project:state is missing.
    /// Returns rebuilt state text prefixed with "[State rebuilt from memories]", or null if no
    /// project memories exist at all.
    /// </summary>
    private static async Task<string?> RebuildStateFromMemoriesAsync(
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

        // Step 2: Try plan:roadmap for phase info
        string phase = "Not started";
        string progressPct = "0";
        try
        {
            string roadmap = await ReadMemoryAsync(store, "plan:roadmap", ct);
            int phaseCount = CountPhases(roadmap);
            phase = phaseCount > 0 ? $"Roadmap created ({phaseCount} phases)" : "Roadmap created";
            progressPct = "20";
        }
        catch (FileNotFoundException) { /* no roadmap yet */ }

        // Step 3: Count plan:* memories for progress estimate
        try
        {
            var planMemories = store.ListScoped("plan");
            if (planMemories.Count > 1) // more than just roadmap
                progressPct = "30";
        }
        catch { /* listing failed — skip */ }

        // Step 4: Derive project ID
        string projectId = DeriveProjectId(store);
        string projectDisplayName = ToProjectName(projectId);

        // Step 5: Write the rebuilt state for future calls (avoids repeated rebuilds)
        string rebuiltNote = "[State rebuilt from memories]\n";
        string nextStep = phase.Contains("Roadmap")
            ? "run plan_tasks for phase 1"
            : "run plan_requirements to define project requirements";

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
    private static async Task<string> ReadMemoryAsync(
        IMemoryStore store, string qualifiedName, CancellationToken ct)
    {
        string artifact = await store.ResolveArtifactAsync(qualifiedName, ct);
        byte[] decoded = new Nmp2Strategy().Decode(artifact);
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
    private static async Task WriteStateAsync(
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
    private static string? ExtractStateField(string stateText, string fieldPrefix)
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
    /// Counts phases in a roadmap text using common heading patterns.
    /// </summary>
    private static int CountPhases(string roadmap)
    {
        int count = 0;
        foreach (string line in roadmap.Split('\n'))
        {
            string trimmed = line.Trim();
            if (Regex.IsMatch(trimmed, @"^#{1,4}\s+Phase\s+\d", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(trimmed, @"^Phase\s+\d", RegexOptions.IgnoreCase))
                count++;
        }
        return count;
    }

    /// <summary>Mark a task complete with outcome metadata. Appends to execution log.</summary>
    [McpServerTool(Name = "task_complete"), Description(
        "Mark a task complete with outcome metadata. Appends to execution log. " +
        "Call task_next to get the next task.")]
    public async Task<string> TaskComplete(
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

        // Update project state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? $"Phase {phaseId}";
        string progressPct = ExtractStateField(stateText, "Progress:")?.TrimEnd('%') ?? "30";

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: progressPct,
            lastAction: $"Completed {taskName}",
            blockers: "none",
            nextStep: "run task_next to get the next pending task",
            cancellationToken);

        string response = $"Task '{taskName}' marked complete. Execution log updated. Run task_next for next task.";

        if (response.Length > MaxResponseChars)
            response = response[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

        return response;
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
    private static bool HasKeyword(ArtifactEntry e, string keyword) =>
        e.Keywords?.Contains(keyword, StringComparer.OrdinalIgnoreCase) == true;

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
    /// Extracts success criteria from the roadmap text scoped to the specified phase section.
    /// Handles numbered lists (1. ...), bulleted lists (- ..., * ...).
    /// Stops extracting when a new Phase section heading is reached.
    /// </summary>
    private static List<string> ExtractPhaseCriteria(string roadmapText, string phaseId)
    {
        var criteria = new List<string>();
        bool inTargetPhase = false;
        bool inCriteriaSection = false;

        foreach (string rawLine in roadmapText.Split('\n'))
        {
            string line = rawLine.TrimEnd();
            string trimmed = line.Trim();

            // Detect Phase heading — matches "### Phase 1:" or "Phase 1" or "Phase 01"
            bool isPhaseHeading = Regex.IsMatch(trimmed,
                @"^#{1,4}\s+Phase\s+\d", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(trimmed, @"^Phase\s+\d", RegexOptions.IgnoreCase);

            if (isPhaseHeading)
            {
                // Check if this heading matches our target phase
                // Extract the number from the heading
                var phaseNumMatch = Regex.Match(trimmed, @"Phase\s+0*(\d+)", RegexOptions.IgnoreCase);
                if (phaseNumMatch.Success)
                {
                    int headingNum = int.Parse(phaseNumMatch.Groups[1].Value);
                    int targetNum = int.TryParse(phaseId.TrimStart('0').Length > 0
                        ? phaseId.TrimStart('0') : "0", out int n) ? n : 0;
                    inTargetPhase = headingNum == targetNum;
                    inCriteriaSection = false; // reset on new phase
                }
                continue;
            }

            if (!inTargetPhase) continue;

            // Detect success criteria sub-heading
            bool isCriteriaHeading = Regex.IsMatch(trimmed,
                @"^#{1,4}\s+(success\s+criteria|criteria|acceptance)", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(trimmed,
                @"^\*{0,2}Success\s+Criteria", RegexOptions.IgnoreCase);

            if (isCriteriaHeading)
            {
                inCriteriaSection = true;
                continue;
            }

            // A non-criteria heading while in target phase ends the criteria section
            if (inCriteriaSection && Regex.IsMatch(trimmed, @"^#{1,4}\s+", RegexOptions.None))
            {
                inCriteriaSection = false;
                continue;
            }

            if (!inCriteriaSection) continue;

            // Collect bulleted or numbered list items
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                criteria.Add(trimmed[2..].Trim());
            }
            else if (Regex.IsMatch(trimmed, @"^\d+\.\s+"))
            {
                // Numbered: "1. criterion text"
                string criterionText = Regex.Replace(trimmed, @"^\d+\.\s+", "").Trim();
                if (criterionText.Length > 0)
                    criteria.Add(criterionText);
            }
        }

        return criteria;
    }

    // ── Concern tracking tools (CONC-01, CONC-02, CONC-03) ───────────────────

    /// <summary>Track a risk or concern with severity and phase scope.</summary>
    [McpServerTool(Name = "concern_add"), Description(
        "Track a risk or issue with severity and phase scope. " +
        "Call concern_resolve when addressed. Query active concerns with concern tool.")]
    public async Task<string> ConcernAdd(
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
        await WritePlanningMemoryAsync(store, qualifiedName, content,
            archiveExisting: false,
            keywords: ["status:active", $"severity:{severity}", $"phase:{phaseScope}"],
            cancellationToken);

        // Update project state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? "Not started";
        string progressPct = ExtractStateField(stateText, "Progress:")?.TrimEnd('%') ?? "0";

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: progressPct,
            lastAction: $"Concern added: {qualifiedName} (severity:{severity})",
            blockers: "none",
            nextStep: "run concern to list active concerns, or concern_resolve when addressed",
            cancellationToken);

        return $"Stored as {qualifiedName}. Files in .scrinia/ were updated — these are your changes.";
    }

    /// <summary>Resolve a tracked concern with resolution notes.</summary>
    [McpServerTool(Name = "concern_resolve"), Description(
        "Resolve a tracked concern with resolution notes. " +
        "Call after concern_add when the issue is addressed.")]
    public async Task<string> ConcernResolve(
        [Description("Concern name (e.g. 'concern:auth-risk' or 'concern:20260319-143022').")] string concernName,
        [Description("Resolution notes.")] string resolution,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

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
        string[] resolvedKeywords = ["status:resolved", severityKw, phaseKw];
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
        string progressPct = ExtractStateField(stateText, "Progress:")?.TrimEnd('%') ?? "0";

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
    [McpServerTool(Name = "concern"), Description(
        "List tracked concerns by status and phase. Returns index-only summary (no artifact decoding). " +
        "Use concern_add to add concerns, concern_resolve to resolve them. Called by plan_status automatically.")]
    public Task<string> Concern(
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
        if (response.Length > MaxResponseChars)
            response = response[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

        return Task.FromResult(response);
    }

    [McpServerTool(Name = "plan_retrospective"), Description(
        "Store a structured phase retrospective in learn:execution-outcomes. " +
        "Call after a phase completes to record what worked, what failed, and lessons learned. " +
        "Outcomes accumulate across phases as independently retrievable chunks.")]
    public async Task<string> PlanRetrospective(
        [Description("Two-digit phase number (e.g. '01').")] string phaseId,
        [Description("Free-text describing what worked well during this phase.")] string whatWorked,
        [Description("Free-text describing what failed or was problematic.")] string whatFailed,
        [Description("Free-text describing lessons learned for future phases.")] string lessons,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        string timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        string retroContent =
            $"## Phase {phaseId} Retrospective\n" +
            $"**Date:** {timestamp}\n\n" +
            $"## What Worked\n{whatWorked}\n\n" +
            $"## What Failed\n{whatFailed}\n\n" +
            $"## Lessons\n{lessons}\n\n" +
            $"## Provenance\nAuthored by agent via plan_retrospective. Keyword: provenance:agent";

        await AppendToExecutionLogAsync(store, "learn:execution-outcomes",
            retroContent, keywords: ["provenance:agent"], cancellationToken);

        string response = $"Phase {phaseId} retrospective stored in learn:execution-outcomes. " +
            "Searchable via standard search. Use get_chunk() to retrieve individual phase retrospectives.";

        if (response.Length > MaxResponseChars)
            response = response[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

        return response;
    }

    [McpServerTool(Name = "plan_profile"), Description(
        "Store or update user preferences for agent behavior. " +
        "Preferences persist across sessions in user:profile. " +
        "Accepts key-value text (e.g. 'autonomy_level: high\\nreview_depth: detailed'). " +
        "Each call fully overwrites the previous profile.")]
    public async Task<string> PlanProfile(
        [Description("Key-value preferences text, one per line (e.g. 'autonomy_level: high').")] string profile,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        await WritePlanningMemoryAsync(store, "user:profile", profile,
            archiveExisting: false, keywords: ["provenance:agent"], cancellationToken);

        string response = "User profile stored in user:profile. " +
            "Preferences persist across sessions and are searchable via standard search.";

        if (response.Length > MaxResponseChars)
            response = response[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

        return response;
    }

    // -- Knowledge building tools (KNOW-01, KNOW-02, KNOW-03, KNOW-04) ----------

    /// <summary>Store domain knowledge in the body of knowledge (bok:*).</summary>
    [McpServerTool(Name = "knowledge_add"), Description(
        "Store domain knowledge in the body of knowledge (bok:*). " +
        "Warns if an existing entry covers the same topic (conflict detection). " +
        "Search existing knowledge with search(scopes='bok') before adding. " +
        "Stored entries feed into future search results automatically.")]
    public async Task<string> KnowledgeAdd(
        [Description("Knowledge domain (e.g. 'dotnet', 'auth', 'deployment').")] string domain,
        [Description("Topic slug within the domain (e.g. 'mcp-tools', 'jwt-pattern').")] string slug,
        [Description("Knowledge content to store.")] string knowledge,
        [Description("How knowledge was obtained: agent, research, manual, or inferred.")] string sourceType,
        [Description("Confidence level: high, medium, or low.")] string confidence,
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

        // Build qualified name
        string qualifiedName = $"bok:{domain}-{slug}";

        // Conflict detection BEFORE write: BM25 search scoped to bok using full subject name
        // Uses "{domain}-{slug}" as query so an exact name match (score=100) indicates a conflict.
        // Threshold > 60.0 catches exact/prefix matches while ignoring weak partial matches.
        string conflictWarning = "";
        string conflictQuery = $"{domain}-{slug}";
        var conflictResults = store.SearchAll(conflictQuery, scopes: "bok", limit: 3);
        foreach (var result in conflictResults)
        {
            if (result is Scrinia.Core.Search.EntryResult er && er.Score > 60.0)
            {
                conflictWarning =
                    $" Warning: existing bok entry '{er.Item.Entry.Name}' may cover the same topic." +
                    " Review with search(scopes='bok') or show() before storing.";
                break;
            }
        }

        // Build content string with provenance header
        string timestamp = DateTimeOffset.UtcNow.ToString("o");
        string content =
            $"## Knowledge Entry\n" +
            $"Domain: {domain}\n" +
            $"Slug: {slug}\n" +
            $"Source: {sourceType}\n" +
            $"Confidence: {confidence}\n" +
            $"Added: {timestamp}\n\n" +
            $"## Content\n{knowledge}";

        // Write with provenance keywords and archiveExisting: true
        await WritePlanningMemoryAsync(store, qualifiedName, content,
            archiveExisting: true,
            keywords: [$"source_type:{sourceType}", $"confidence:{confidence}", $"domain:{domain}"],
            cancellationToken);

        // Update project:state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? "Not started";
        string progressPct = ExtractStateField(stateText, "Progress:")?.TrimEnd('%') ?? "0";

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: progressPct,
            lastAction: $"Knowledge added: {qualifiedName}",
            blockers: "none",
            nextStep: "continue planning or search bok:* to retrieve stored knowledge",
            cancellationToken);

        string response =
            $"Stored as {qualifiedName}. Files in .scrinia/ were updated -- these are your changes.{conflictWarning}";

        if (response.Length > MaxResponseChars)
            response = response[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

        return response;
    }

    // -- Dynamic goal management (GOAL-01, GOAL-02, GOAL-04) ---------------------

    /// <summary>Manage project goals dynamically.</summary>
    [McpServerTool(Name = "goal_update"), Description(
        "Manage project goals dynamically. Actions: 'add' (new goal), 'complete' (mark done with outcome), 'list' (show all goals with status). " +
        "Goals modify project:context in-place — no re-initialization needed. " +
        "Original goal count is preserved for scope drift detection by plan_status.")]
    public async Task<string> GoalUpdate(
        [Description("Action to perform: 'add', 'complete', or 'list'.")] string action,
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

                // Assign sequential ID across all goals (raw init goals + structured goals)
                int nextId = goals.Count + 1;
                string newGoalLine = $"- [G-{nextId}] [active] {description}";
                goals.Add(newGoalLine);

                // Rebuild goals section
                string goalsSection = BuildGoalsSection(goals, lockedOriginalCount);
                string updatedContext = contextWithoutGoals.TrimEnd() + "\n\n" + goalsSection;

                await WritePlanningMemoryAsync(store, "project:context", updatedContext,
                    archiveExisting: true, cancellationToken);

                // Update project:state
                await UpdateStateAfterGoalMutationAsync(store, $"Goal added: G-{nextId}", cancellationToken);

                return $"Goal added as G-{nextId}: {description}. " +
                       $"project:context updated. Files in .scrinia/ were updated — these are your changes.";
            }

            case "complete":
            {
                if (string.IsNullOrWhiteSpace(goalId))
                    return "Error: 'complete' action requires a goalId (e.g. 'G-1').";

                var (goals, originalCount, contextWithoutGoals) = ParseGoalsSection(contextText);

                // Find goal line matching goalId (case-insensitive)
                string searchId = goalId.Trim();
                int matchIndex = goals.FindIndex(g =>
                    g.Contains($"[{searchId}]", StringComparison.OrdinalIgnoreCase) ||
                    g.Contains($"[{searchId.ToUpperInvariant()}]", StringComparison.OrdinalIgnoreCase));

                if (matchIndex < 0)
                    return $"Error: goal '{goalId}' not found. Use goal_update(action:'list') to see all goal IDs.";

                // Extract description from the matched line
                string existingLine = goals[matchIndex];
                string goalDesc = ExtractGoalDescription(existingLine);
                string outcomeText = outcome ?? "(no outcome recorded)";
                string timestamp = DateTimeOffset.UtcNow.ToString("o");

                goals[matchIndex] =
                    $"- [{searchId.ToUpperInvariant()}] [complete] {goalDesc} | Outcome: {outcomeText} | Completed: {timestamp}";

                string goalsSection = BuildGoalsSection(goals, originalCount >= 0 ? originalCount : goals.Count);
                string updatedContext = contextWithoutGoals.TrimEnd() + "\n\n" + goalsSection;

                await WritePlanningMemoryAsync(store, "project:context", updatedContext,
                    archiveExisting: true, cancellationToken);

                await UpdateStateAfterGoalMutationAsync(store, $"Goal completed: {searchId}", cancellationToken);

                return $"Goal '{searchId}' marked complete. Outcome recorded. " +
                       $"project:context updated. Files in .scrinia/ were updated — these are your changes.";
            }

            case "list":
            {
                var (goals, originalCount, _) = ParseGoalsSection(contextText);

                if (goals.Count == 0)
                    return "No structured goals found in project:context. Use goal_update(action:'add') to add goals.";

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
                    if (!System.Text.RegularExpressions.Regex.IsMatch(trimmedGoal, @"^\[G-\d+\]"))
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

                string response = sb.ToString();
                if (response.Length > MaxResponseChars)
                    response = response[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

                return response;
            }

            default:
                return $"Error: unknown action '{action}'. Valid actions: 'add', 'complete', 'list'.";
        }
    }

    /// <summary>
    /// Parses the goals section from project:context text.
    /// Returns: (goalLines, originalCount, contextWithoutGoals).
    /// originalCount is -1 if the "Original goals:" marker is not present.
    /// goalLines contains all goal lines (raw or structured) found in the goals section.
    /// contextWithoutGoals is the context text with the goals section stripped.
    /// </summary>
    private static (List<string> Goals, int OriginalCount, string ContextWithoutGoals)
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
                if (System.Text.RegularExpressions.Regex.IsMatch(trimmed,
                        @"^#{0,4}\s*Goals\s*:?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
                    System.Text.RegularExpressions.Regex.IsMatch(trimmed,
                        @"^Goals\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    goalsSectionStart = i;
                }
            }
            else
            {
                // Inside goals section — look for "Original goals: N" marker
                if (System.Text.RegularExpressions.Regex.IsMatch(trimmed,
                        @"^[Oo]riginal goals?\s*:\s*\d+"))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(trimmed, @"\d+");
                    if (m.Success) originalCount = int.Parse(m.Value);
                    continue;
                }

                // Detect end of goals section: blank line followed by new non-goal content,
                // OR a new section header (## or ###)
                if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^#{1,4}\s+\S"))
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

    /// <summary>Extracts the description text from a goal line, stripping ID and status brackets.</summary>
    private static string ExtractGoalDescription(string goalLine)
    {
        string stripped = goalLine.TrimStart('-', '*', ' ');
        // Remove leading [G-N] and [status] brackets if present
        stripped = System.Text.RegularExpressions.Regex.Replace(
            stripped, @"^\[G-\d+\]\s*\[[\w]+\]\s*", "").Trim();
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
        string progressPct = ExtractStateField(stateText, "Progress:")?.TrimEnd('%') ?? "0";

        await WriteStateAsync(store, projectName, projectId,
            phase: phase,
            progressPct: progressPct,
            lastAction: lastAction,
            blockers: "none",
            nextStep: "run goal_update(action:'list') to see all goals with status",
            ct);
    }

    // -- Built-in specialist scaffolds (AGENT-04) --------------------------------

    private const string ResearcherScaffold =
        "## Role: Researcher Specialist\n" +
        "You investigate technical questions and gather findings for the current project.\n\n" +
        "## Tools Available (if Scrinia MCP is active)\n" +
        "- research_start: Open a research investigation with questions to investigate.\n" +
        "- research_complete: Store findings once investigation is done.\n" +
        "- search: Query stored knowledge and memories for related context.\n" +
        "- show: Retrieve full artifact content for a named memory.\n\n" +
        "## Instructions\n" +
        "1. Call research_start with your phase and topic to register the investigation.\n" +
        "2. Use search() to find existing knowledge before researching from scratch.\n" +
        "3. Investigate thoroughly, then call research_complete with your findings.\n" +
        "4. Store durable knowledge via knowledge_add for reuse across sessions.\n\n" +
        "## Fallback Instructions (if Scrinia MCP is not available)\n" +
        "Organize findings in markdown. Use file read/write operations to persist results.\n" +
        "Document questions answered, sources consulted, and key conclusions.\n";

    private const string ReviewerScaffold =
        "## Role: Reviewer Specialist\n" +
        "You review code, architecture, or plans and provide structured feedback with actionable concerns.\n\n" +
        "## Tools Available (if Scrinia MCP is active)\n" +
        "- search: Query memories for existing decisions, patterns, or prior art.\n" +
        "- show: Load full artifact content for review context.\n" +
        "- concern_add: Track issues found during review with severity and phase scope.\n" +
        "- concern_resolve: Mark concerns resolved when addressed.\n\n" +
        "## Instructions\n" +
        "1. Use search() to load relevant context before reviewing.\n" +
        "2. For each issue found, call concern_add with severity (high/medium/low) and phase.\n" +
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
        "- knowledge_add: Store expert insights in the body of knowledge (bok:*).\n" +
        "- show: Retrieve full artifact content for context on prior decisions.\n\n" +
        "## Instructions\n" +
        "1. Use search(scopes='bok') to check for existing domain knowledge first.\n" +
        "2. Provide expert-level analysis grounded in established domain patterns.\n" +
        "3. Store durable insights via knowledge_add(domain, slug, knowledge, ...).\n" +
        "4. Flag uncertainty explicitly — indicate confidence level in your responses.\n\n" +
        "## Fallback Instructions (if Scrinia MCP is not available)\n" +
        "Document expert insights in a structured markdown file.\n" +
        "Include sections: Domain Context, Key Patterns, Caveats, References.\n";

    // -- Subagent creation tools (AGENT-01, AGENT-02, AGENT-03, AGENT-04) -------

    /// <summary>Generate a specialist subagent prompt and store as skill:* memory.</summary>
    [McpServerTool(Name = "spawn_agent"), Description(
        "Generate a specialist subagent prompt and store as skill:* memory. " +
        "Built-in scaffolds: researcher, reviewer, domain-expert. " +
        "Use skill_load to retrieve stored skills. " +
        "Includes capability-conditional fallbacks for non-MCP environments.")]
    public async Task<string> SpawnAgent(
        [Description("Skill name slug (e.g. 'api-reviewer', 'auth-researcher').")] string skillName,
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
                    $"{instructionsSection}\n\n" +
                    toolSection +
                    $"## Instructions\n" +
                    $"{instructionsSection}\n\n" +
                    $"## Fallback Instructions (if Scrinia MCP is not available)\n" +
                    $"Organize findings in markdown. Use standard file operations to persist results.\n";
                break;
        }

        // Build capability list for keywords
        string capabilityList = string.IsNullOrWhiteSpace(tools) ? scaffoldLower : tools;

        // Store via WritePlanningMemoryAsync with skill:{skillName} qualified name
        string qualifiedName = $"skill:{skillName}";
        await WritePlanningMemoryAsync(store, qualifiedName, promptContent,
            archiveExisting: true,
            keywords: [$"role:{role}", $"capabilities:{capabilityList}"],
            cancellationToken);

        // Update project:state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? "Not started";
        string progressPct = ExtractStateField(stateText, "Progress:")?.TrimEnd('%') ?? "0";

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: progressPct,
            lastAction: $"Skill created: {qualifiedName} (role:{role})",
            blockers: "none",
            nextStep: "use skill_load to retrieve stored skills",
            cancellationToken);

        string response = $"Stored as {qualifiedName}. Files in .scrinia/ were updated -- these are your changes.";

        if (response.Length > MaxResponseChars)
            response = response[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

        return response;
    }

    /// <summary>List or load stored specialist skills.</summary>
    [McpServerTool(Name = "skill_load"), Description(
        "List or load stored specialist skills. " +
        "Call with no skillName to list available skills. " +
        "Call with a skillName to load the full prompt for activation. " +
        "Skills created by spawn_agent.")]
    public Task<string> SkillLoad(
        [Description("Skill name to load (e.g. 'api-reviewer'). Omit to list all skills.")] string? skillName = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        if (string.IsNullOrWhiteSpace(skillName))
        {
            // List mode: synchronous index-only scan, no artifact decode
            var (scope, _) = store.ParseQualifiedName("skill:placeholder");
            IReadOnlyList<ArtifactEntry> entries;
            try { entries = store.LoadIndex(scope); }
            catch { return Task.FromResult("No skills stored yet."); }

            if (entries.Count == 0)
                return Task.FromResult("No skills stored yet.");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Available skills ({entries.Count}):");
            sb.AppendLine();

            foreach (var entry in entries)
            {
                string roleKw = entry.Keywords?
                    .FirstOrDefault(k => k.StartsWith("role:", StringComparison.OrdinalIgnoreCase))
                    ?? "role:unknown";

                sb.AppendLine($"- skill:{entry.Name} [{roleKw}]");

                if (sb.Length > MaxResponseChars - 200)
                {
                    sb.AppendLine("[... truncated to 8KB limit]");
                    break;
                }
            }

            string listResponse = sb.ToString();
            if (listResponse.Length > MaxResponseChars)
                listResponse = listResponse[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

            return Task.FromResult(listResponse);
        }

        // Load mode: async artifact read
        return LoadSkillAsync(store, skillName, cancellationToken);
    }

    private static async Task<string> LoadSkillAsync(
        IMemoryStore store, string skillName, CancellationToken ct)
    {
        string qualifiedName = $"skill:{skillName}";
        string content;
        try
        {
            content = await ReadMemoryAsync(store, qualifiedName, ct);
        }
        catch (FileNotFoundException)
        {
            return $"Error: skill '{skillName}' not found. Use skill_load (no name) to list available skills.";
        }

        if (content.Length > MaxResponseChars)
            content = content[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

        return content;
    }

    private sealed record ParsedTask(string Id, int Wave, string[] DependsOn, string Content);

    /// <summary>
    /// Parses free-text task input into structured task records.
    /// Each task section starts with "## Task {id}" and contains Wave, Depends on, Action, and Acceptance criteria fields.
    /// </summary>
    private static List<ParsedTask> ParseTaskSections(string tasks)
    {
        var result = new List<ParsedTask>();
        // Split by task section headers: ## Task XX or ## Task XX (anything)
        var taskHeaderPattern = new Regex(@"^##\s+Task\s+(\w+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var headerMatches = taskHeaderPattern.Matches(tasks);

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

            // Parse Wave
            int wave = 1;
            var waveMatch = Regex.Match(section, @"^Wave:\s*(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (waveMatch.Success && int.TryParse(waveMatch.Groups[1].Value, out int parsedWave))
                wave = parsedWave;

            // Parse Depends on
            string[] dependsOn = [];
            var depsMatch = Regex.Match(section, @"^Depends\s+on:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
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

            // Build content: Action + Acceptance criteria (everything except Wave/Depends on lines)
            var contentLines = section.Split('\n')
                .Where(line =>
                {
                    string t = line.Trim();
                    return !Regex.IsMatch(t, @"^Wave:\s*\d+", RegexOptions.IgnoreCase) &&
                           !Regex.IsMatch(t, @"^Depends\s+on:", RegexOptions.IgnoreCase);
                })
                .ToList();

            // Trim leading/trailing blank lines from content
            while (contentLines.Count > 0 && string.IsNullOrWhiteSpace(contentLines[0]))
                contentLines.RemoveAt(0);
            while (contentLines.Count > 0 && string.IsNullOrWhiteSpace(contentLines[^1]))
                contentLines.RemoveAt(contentLines.Count - 1);

            string content = string.Join('\n', contentLines).Trim();
            if (string.IsNullOrWhiteSpace(content))
                content = "(no action specified)";

            result.Add(new ParsedTask(taskId, wave, dependsOn, content));
        }

        return result;
    }
}
