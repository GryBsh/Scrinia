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
    /// <summary>
    /// Copilot CLI hard-truncates MCP tool responses at 10 KB (fixed constant in Iw()).
    /// VS Code Copilot Chat truncates at ~50% of prompt token budget (dynamic).
    /// We cap at 8 KB to stay safely under the CLI limit with 2 KB headroom.
    /// </summary>
    private const int MaxResponseChars = 8 * 1024;

    private static string Truncate(string text) =>
        text.Length <= MaxResponseChars ? text : text[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

    private static IMemoryStore CurrentStore =>
        MemoryStoreContext.Current ?? throw new InvalidOperationException(
            "No memory store configured. Call MemoryStoreContext.Current = ... before using planning tools.");

    // ── MCP Tools ────────────────────────────────────────────────────────────

    /// <summary>Initialize a project by storing goals, context, and constraints.</summary>
    [McpServerTool(Name = "project_init"), Description(
        "One-time project initialization. Call when you first encounter a workspace without an existing project, " +
        "or when the user's request warrants structured planning. Detects whether the workspace has " +
        "an existing codebase and returns tailored next steps. " +
        "Note: this writes to .scrinia/ in the workspace — treat those file changes as your own.")]
    public async Task<string> ProjectInit(
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
            ? "scan the existing codebase for concerns (concern_add) and capture patterns (store), then set a goal with goal_update"
            : "set a goal with goal_update, then plan requirements";

        await WriteStateAsync(store, projectName, projectId,
            phase: "Initialized",
            progressPct: "0",
            lastAction: "Project initialized",
            blockers: "none",
            nextStep: nextStep,
            cancellationToken);

        string response = $"Initialized project '{projectId}'. Stored: project:context, project:state. " +
               $"Files in .scrinia/ were updated — these are your changes.";

        if (hasExistingCode)
            response += "\n\nExisting codebase detected. Recommended next steps:\n" +
                "1. Scan the codebase for concerns, risks, and tech debt → concern_add\n" +
                "2. Capture key architecture patterns and conventions → store(content, \"topic:subject\")\n" +
                "3. Set a goal for what you want to achieve → goal_update(action:'add')\n" +
                "4. Then proceed to research → requirements → roadmap → execution";
        else
            response += "\n\nEmpty workspace. Set a goal with goal_update(action:'add'), " +
                "then define requirements and a roadmap.";

        return response;
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
            nextStep: "review requirements with the user before creating roadmap",
            cancellationToken);

        return "Stored: project:requirements. Files in .scrinia/ were updated — these are your changes.\n\n" +
               "Before creating a roadmap, review these requirements with the user:\n" +
               "- Are all requirements captured? Anything missing?\n" +
               "- Are the REQ-IDs scoped correctly (too broad? too narrow?)?\n" +
               "- Are priorities clear — what's essential vs. nice-to-have?\n" +
               "Once confirmed, run plan_roadmap to create a phased roadmap.";
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
            nextStep: "review the roadmap with the user before starting execution",
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

        return $"Stored: plan:roadmap.{extraNote}{patternNote} " +
               "Files in .scrinia/ were updated — these are your changes.\n\n" +
               "Before starting execution, review this roadmap with the user:\n" +
               "- Are the phases in the right order? Any dependencies between them?\n" +
               "- Are the success criteria measurable and specific?\n" +
               "- Is the scope per phase reasonable (not too large, not too small)?\n" +
               "Once confirmed, run research_start for phase 01 to investigate before decomposing into tasks.";
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
            "Depends on: {comma-separated task IDs, or 'none'}\n" +
            "Action: {what to do}\n" +
            "Acceptance criteria:\n" +
            "- criterion 1\n" +
            "- criterion 2\n" +
            "Waves are computed automatically from the dependency graph — no need to specify them.")] string tasks,
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
                // If dep is a raw ID (e.g., "01"), resolve to full name "phaseId-wave-id".
                // If dep is already a full name (e.g., "01-1-01"), pass through as-is.
                if (computedWaves.TryGetValue(dep, out int depWave))
                    keywords.Add($"depends_on:{phaseId}-{depWave}-{dep}");
                else
                    keywords.Add($"depends_on:{dep}"); // already full name or external ref
            }

            // Task naming: task:{phaseId}-{wave}-{id}
            string taskName = $"task:{phaseId}-{wave}-{task.Id}";

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
        string response =
            $"Created {parsedTasks.Count} task(s) for phase {phaseId} in {waveCount} wave(s).\n" +
            $"Tasks stored:\n{taskList}\n" +
            $"Files in .scrinia/ were updated — these are your changes.\n" +
            $"Next: run task_next to get the first pending tasks.{parallelHint}\n" +
            $"Spawn agents for all task execution — the primary agent orchestrates, it does not execute tasks directly." +
            executionPolicyHint +
            patternNote;

        response = Truncate(response);

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
            capabilityHints += "\nHint: use store(content, \"topic:subject\") with keywords to persist domain knowledge across sessions.";

        // Check if agent behavioral norms exist
        try
        {
            var (agentScope, _) = store.ParseQualifiedName("agent:placeholder");
            var agentEntries = store.LoadIndex(agentScope);
            if (agentEntries.Count > 0)
                capabilityHints += "\nAgent behavioral norms found — search('agent:') to load project-level norms.";
        }
        catch { /* agent scope not created — skip silently */ }

        response += capabilityHints;

        response = Truncate(response);

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
        string blockers = ExtractStateField(stateText, "Blockers:") ?? "none";
        string next = ExtractStateField(stateText, "Next:") ?? "(not set)";
        string lastAction = ExtractStateField(stateText, "Last action:") ?? "(not set)";

        // Compute progress from roadmap + task data (not from stale stored value)
        string? roadmapText = null;
        string roadmapNote = "";
        try
        {
            roadmapText = await ReadMemoryAsync(store, "plan:roadmap", cancellationToken);
            int phaseCount = CountPhases(roadmapText);
            if (phaseCount > 0)
                roadmapNote = $"\nRoadmap: {phaseCount} phase(s) defined";
        }
        catch (FileNotFoundException) { /* roadmap not yet created — skip silently */ }

        string? statusGoalId = await GetActiveGoalIdAsync(store, cancellationToken);
        string progress = CalculateProgress(store, roadmapText, statusGoalId) + "%";

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
            idleNote = "\nNo active goal. Ask the user what to work on next → goal_update(action:'add')";
        else if (!hasActiveGoal && progress == "0%")
            idleNote = "\nNo active goal. Set one with goal_update(action:'add') to start planning.";

        string response =
            $"Project: {projectName}\n" +
            $"Phase: {phase}\n" +
            $"Progress: {progress}\n" +
            $"Last action: {lastAction}\n" +
            $"Blockers: {blockers}\n" +
            $"Next: {next}" +
            roadmapNote + concernNote + goalNote + idleNote;

        response = Truncate(response);

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

        // Compute progress live from roadmap + task data
        string? tnRoadmap = null;
        try { tnRoadmap = await ReadMemoryAsync(store, "plan:roadmap", cancellationToken); }
        catch (FileNotFoundException) { }

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: CalculateProgress(store, tnRoadmap, activeGoalId),
            lastAction: $"task_next called for phase {phaseId} wave {currentWave}",
            blockers: "none",
            nextStep: unblockedEntries.Count > 1
                ? $"spawn {unblockedEntries.Count} parallel agents for wave {currentWave} tasks, call task_complete for each"
                : $"execute wave {currentWave} task, then call task_complete",
            cancellationToken);

        string response = sb.ToString();
        response = Truncate(response);

        return response;
    }

    /// <summary>Verify a phase achieved its goal using success criteria from the roadmap.</summary>
    [McpServerTool(Name = "plan_verify"), Description(
        "Record verification results for a phase. Call WITHOUT evidence to see the criteria checklist. " +
        "Call WITH evidence after you have verified the work yourself (run tests, reviewed changes, confirmed behavior). " +
        "The agent is responsible for actual verification — this tool records the results.")]
    public async Task<string> PlanVerify(
        [Description("Two-digit phase number (e.g. '01').")] string phaseId,
        [Description("Your verification evidence — one line per criterion in order, " +
                     "each starting with PASS: or FAIL: followed by what you observed. " +
                     "Omit to see the criteria checklist without recording results.")] string? evidence = null,
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

        // Load task summary for context (scoped to active goal)
        string? verifyGoalId = await GetActiveGoalIdAsync(store, cancellationToken);
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var allEntries = store.LoadIndex(taskScope);
        var phaseEntries = allEntries
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
            sb.AppendLine($"plan_verify(\"{phaseId}\", evidence: \"PASS: criterion 1 — your evidence\\nPASS: criterion 2 — your evidence\")");
            sb.AppendLine($"```");
            sb.AppendLine();

            for (int i = 0; i < criteria.Count; i++)
                sb.AppendLine($"{i + 1}. [ ] {criteria[i]}");

            string checklistResponse = sb.ToString();
            checklistResponse = Truncate(checklistResponse);

            return checklistResponse;
        }

        // ── Recording mode (evidence provided) ──
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
        string progressPct2 = CalculateProgress(store, roadmapText, verifyGoalId);

        // Build next step guidance based on verification result
        string verifyNextStep;
        if (passCount < criteria.Count)
        {
            verifyNextStep = "run plan_gaps to create gap closure tasks";
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
                ? $"resolve addressed concerns (concern_resolve), then run plan_retrospective for phase {phaseId}"
                : $"run plan_retrospective for phase {phaseId} to record lessons learned";
        }

        await WriteStateAsync(store, projectName2, projectId2,
            phase: currentPhase2,
            progressPct: progressPct2,
            lastAction: $"plan_verify for phase {phaseId}: {status}",
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

        response = Truncate(response);

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

        // Check for existing knowledge to build on (internal research before external)
        var researchHints = new List<string>();

        // Existing skills
        try
        {
            var (skillScope, _) = store.ParseQualifiedName("skill:placeholder");
            var skillEntries = store.LoadIndex(skillScope);
            if (skillEntries.Count > 0)
            {
                var names = skillEntries.Select(e => $"skill:{e.Name}").Take(5);
                researchHints.Add($"Skills available: {string.Join(", ", names)} — use skill_load for specialist prompts");
            }
        }
        catch { /* skill scope not yet created — skip silently */ }

        // Prior beliefs from retrospectives
        try
        {
            var (learnScope, _) = store.ParseQualifiedName("learn:placeholder");
            var learnEntries = store.LoadIndex(learnScope);
            var beliefEntries = learnEntries.Where(e => HasKeyword(e, "type:beliefs")).ToList();
            if (beliefEntries.Count > 0)
            {
                var names = beliefEntries.Select(e => $"learn:{e.Name}").Take(3);
                researchHints.Add($"Prior beliefs: {string.Join(", ", names)} — search or show to build on what you already understand");
            }
        }
        catch { /* learn scope not yet created — skip silently */ }

        // Prior research on related topics
        try
        {
            var (resScope, _) = store.ParseQualifiedName("research:placeholder");
            var resEntries = store.LoadIndex(resScope);
            var priorResearch = resEntries.Where(e => HasKeyword(e, "status:complete")).ToList();
            if (priorResearch.Count > 0)
            {
                var names = priorResearch.Select(e => $"research:{e.Name}").Take(3);
                researchHints.Add($"Prior research: {string.Join(", ", names)} — check for relevant findings and hypotheses");
            }
        }
        catch { /* research scope not yet created — skip silently */ }

        string hintsText = researchHints.Count > 0
            ? "\n\nBuild on existing knowledge before investigating externally:\n" +
              string.Join("\n", researchHints.Select(h => $"- {h}"))
            : "";

        string response = $"Research investigation started. Stored as {memoryName} with status:active. " +
                          $"Files in .scrinia/ were updated — these are your changes. " +
                          $"Call research_complete when you have findings.{hintsText}";

        response = Truncate(response);

        return response;
    }

    /// <summary>Complete a research investigation with findings and a hypothesis.</summary>
    [McpServerTool(Name = "research_complete"), Description(
        "Complete a research investigation with findings and a hypothesis. " +
        "The hypothesis states what approach you believe will work and why, based on your findings. " +
        "This hypothesis will be surfaced during plan_verify so you can evaluate whether it held. " +
        "Call after research_start, before plan_tasks.")]
    public async Task<string> ResearchComplete(
        [Description("Two-digit phase number (e.g. '06').")] string phaseId,
        [Description("Research topic slug — must match the topic used in research_start.")] string topic,
        [Description("Findings from the research investigation.")] string findings,
        [Description("Your hypothesis: what approach will work and why? What would invalidate it?")] string? hypothesis = null,
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

        string hypothesisSection = !string.IsNullOrWhiteSpace(hypothesis)
            ? $"\n\n## Hypothesis\n{hypothesis}"
            : "";

        string updatedContent =
            existingContent.TrimEnd() + "\n\n" +
            $"## Findings\n{findings}" +
            hypothesisSection;

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
            nextStep: $"share research findings with the user before decomposing into tasks",
            cancellationToken);

        string response = $"Research complete. {memoryName} updated with findings and status:complete. " +
                          $"Files in .scrinia/ were updated — these are your changes.\n\n" +
                          $"Before decomposing into tasks, share your findings with the user:\n" +
                          $"- Summarize what you found and any concerns discovered\n" +
                          $"- Flag anything surprising or that changes the approach\n" +
                          $"- Ask if the user has additional context you may have missed\n" +
                          $"Use `skill_load(\"planner\")` before plan_tasks to produce agent-executable task specs with file scoping and SOS criteria.\n" +
                          $"Once confirmed, call plan_tasks to decompose using these findings. " +
                          $"If this research revealed a recurring specialist need, consider skill_create to save a reusable prompt.";

        response = Truncate(response);

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

    /// <summary>
    /// Extracts two-digit phase IDs from roadmap headings (e.g. "## Phase 1" → "01").
    /// </summary>
    private static List<string> ExtractPhaseIds(string roadmap)
    {
        var ids = new List<string>();
        foreach (string line in roadmap.Split('\n'))
        {
            string trimmed = line.Trim();
            var match = Regex.Match(trimmed, @"^#{0,4}\s*Phase\s+0*(\d+)", RegexOptions.IgnoreCase);
            if (match.Success)
                ids.Add(int.Parse(match.Groups[1].Value).ToString("D2"));
        }
        return ids;
    }

    /// <summary>
    /// Calculates overall progress percentage from roadmap phases and task completion data.
    /// Each phase contributes equally. Within a phase, progress = completed / total tasks.
    /// A phase with no tasks counts as 0% (not yet decomposed).
    /// </summary>
    private static string CalculateProgress(IMemoryStore store, string? roadmapText, string? goalId = null)
    {
        if (string.IsNullOrWhiteSpace(roadmapText))
            return "0";

        var phaseIds = ExtractPhaseIds(roadmapText);
        if (phaseIds.Count == 0)
            return "0";

        // Load task index once (filter by goal if available)
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var allTaskEntries = store.LoadIndex(taskScope);

        double totalProgress = 0;
        foreach (string phaseId in phaseIds)
        {
            var phaseEntries = allTaskEntries
                .Where(e => HasKeyword(e, $"phase:{phaseId}"))
                .Where(e => goalId is null || HasKeyword(e, $"goal:{goalId}"))
                .ToList();

            if (phaseEntries.Count == 0)
                continue; // phase not yet decomposed into tasks

            int complete = phaseEntries.Count(e => HasKeyword(e, "status:complete"));
            totalProgress += (double)complete / phaseEntries.Count;
        }

        int pct = (int)Math.Round(totalProgress / phaseIds.Count * 100);
        return pct.ToString();
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

        // Update project state with computed progress
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? $"Phase {phaseId}";

        // Compute progress from roadmap + task data (scoped to active goal)
        string? tcGoalId = await GetActiveGoalIdAsync(store, cancellationToken);
        string? roadmapText = null;
        try { roadmapText = await ReadMemoryAsync(store, "plan:roadmap", cancellationToken); }
        catch (FileNotFoundException) { /* no roadmap yet */ }
        string progressPct = CalculateProgress(store, roadmapText, tcGoalId);

        // Check if this was the last pending task in the phase (scoped to goal)
        var updatedEntries = store.LoadIndex(scope);
        var goalScopedEntries = updatedEntries
            .Where(e => HasKeyword(e, $"phase:{phaseId}"))
            .Where(e => tcGoalId is null || HasKeyword(e, $"goal:{tcGoalId}"))
            .ToList();
        bool phaseComplete = !goalScopedEntries.Any(e => HasKeyword(e, "status:pending"));

        string nextStep;
        if (phaseComplete)
            nextStep = $"all phase {phaseId} tasks complete — verify the work (run tests, review changes), then run plan_verify to record results";
        else
        {
            var pendingCheck = goalScopedEntries
                .Where(e => HasKeyword(e, "status:pending"))
                .ToList();
            int thisWaveCheck = ParseWave(existing);
            int sameWaveCheck = pendingCheck.Count(e => ParseWave(e) == thisWaveCheck);
            nextStep = sameWaveCheck > 0
                ? $"keep {sameWaveCheck} remaining wave {thisWaveCheck} parallel agents running"
                : "run task_next to get the next wave's tasks";
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
                $"then call plan_verify(\"{phaseId}\") to record your verification results.";
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
                response = $"Task '{taskName}' marked complete. {sameWaveRemaining} tasks remaining in wave {thisWave} — keep parallel agents running. Call task_complete for each as they finish.";
            else if (sameWaveRemaining == 1)
                response = $"Task '{taskName}' marked complete. 1 task remaining in wave {thisWave}.";
            else
                response = $"Task '{taskName}' marked complete. Wave {thisWave} done. Run task_next to get wave {thisWave + 1} tasks ({totalRemaining} pending).";
        }

        response = Truncate(response);

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

    /// <summary>Extracts the active goal ID (e.g., "G-14") from project:context goals section.</summary>
    private static async Task<string?> GetActiveGoalIdAsync(IMemoryStore store, CancellationToken ct)
    {
        try
        {
            string contextText = await ReadMemoryAsync(store, "project:context", ct);
            var (goals, _, _) = ParseGoalsSection(contextText);
            var activeLine = goals.FirstOrDefault(g => g.Contains("[active]", StringComparison.OrdinalIgnoreCase));
            if (activeLine is null) return null;

            // Extract G-N from "[G-14] [active] ..."
            var match = System.Text.RegularExpressions.Regex.Match(activeLine, @"\[G-(\d+)\]");
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
        response = Truncate(response);

        return Task.FromResult(response);
    }

    [McpServerTool(Name = "plan_retrospective"), Description(
        "Store a structured phase retrospective in learn:execution-outcomes. " +
        "Call after a phase completes to record what worked, what failed, lessons learned, " +
        "and what you now understand differently about the domain. " +
        "Updated beliefs are automatically stored as topical memories.")]
    public async Task<string> PlanRetrospective(
        [Description("Two-digit phase number (e.g. '01').")] string phaseId,
        [Description("Free-text describing what worked well during this phase.")] string whatWorked,
        [Description("Free-text describing what failed or was problematic.")] string whatFailed,
        [Description("Free-text describing lessons learned for future phases.")] string lessons,
        [Description("What do you now understand differently about this domain? " +
                     "New patterns discovered, assumptions proven wrong, conventions clarified. " +
                     "These get auto-stored as topical memories.")] string? beliefsUpdated = null,
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
            $"## Provenance\nAuthored by agent via plan_retrospective. Keyword: provenance:agent";

        await AppendToExecutionLogAsync(store, "learn:execution-outcomes",
            retroContent, keywords: ["provenance:agent"], cancellationToken);

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
        string? retroGoalId = await GetActiveGoalIdAsync(store, cancellationToken);
        string retroNextStep = "";
        try
        {
            string? rmText = null;
            try { rmText = await ReadMemoryAsync(store, "plan:roadmap", cancellationToken); }
            catch (FileNotFoundException) { }

            if (rmText is not null)
            {
                var phaseIds = ExtractPhaseIds(rmText);
                var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
                var allTasks = store.LoadIndex(taskScope);

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
                        "1. Distill valuable learnings into topical memories (store) so future goals start smarter\n" +
                        "2. Update existing skills or create new ones (skill_create) with lessons from this goal" +
                        skillNudge + "\n" +
                        "3. Then run goal_update(action:'complete')";
                else if (nextPhase is not null)
                    retroNextStep = $"\nNext: run research_start for phase {nextPhase} to investigate before decomposing into tasks." +
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

        string? roadmapForProgress = null;
        try { roadmapForProgress = await ReadMemoryAsync(store, "plan:roadmap", cancellationToken); }
        catch (FileNotFoundException) { }

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: CalculateProgress(store, roadmapForProgress, retroGoalId),
            lastAction: $"Retrospective for phase {phaseId}",
            blockers: "none",
            nextStep: retroNextStep.TrimStart('\n'),
            cancellationToken);

        string response = $"Phase {phaseId} retrospective stored in learn:execution-outcomes. " +
            "Searchable via standard search. Use get_chunk() to retrieve individual phase retrospectives." +
            retroNextStep;

        response = Truncate(response);

        return response;
    }

    [McpServerTool(Name = "plan_profile"), Description(
        "Store or update project-level agent behavioral norms. " +
        "Norms persist across sessions in agent:profile. " +
        "Accepts key-value text (e.g. 'response_style: terse\\nreview_depth: detailed'). " +
        "Each call fully overwrites the previous profile.")]
    public async Task<string> PlanProfile(
        [Description("Key-value norms text, one per line (e.g. 'response_style: terse').")] string profile,
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

                // Update project:state with next step toward planning
                string addStateText;
                try { addStateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
                catch (FileNotFoundException) { addStateText = ""; }

                string addProjectName = ExtractStateField(addStateText, "Project:") ?? "Unknown Project";
                string addProjectId = ExtractStateField(addStateText, "ID:") ?? DeriveProjectId(store);
                string addPhase = ExtractStateField(addStateText, "Phase:") ?? "Not started";
                string addProgress = ExtractStateField(addStateText, "Progress:")?.TrimEnd('%') ?? "0";

                await WriteStateAsync(store, addProjectName, addProjectId,
                    phase: addPhase, progressPct: addProgress,
                    lastAction: $"Goal added: G-{nextId}",
                    blockers: "none",
                    nextStep: "clarify the goal with the user before planning",
                    cancellationToken);

                return $"Goal added as G-{nextId}: {description}.\n" +
                       $"project:context updated. Files in .scrinia/ were updated — these are your changes.\n\n" +
                       $"Before planning, confirm the goal with the user:\n" +
                       $"- **Scope**: What's included? What's explicitly out of scope?\n" +
                       $"- **Success criteria**: How will we know the goal is achieved?\n" +
                       $"- **Constraints**: Timeline, tech stack, dependencies, or other limits?\n" +
                       $"- **Priority**: What matters most if trade-offs are needed?\n" +
                       $"Once the goal is clear, run plan_requirements to define requirements.";
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

                // Failsafe: actually verify criteria and check for retrospectives
                var warnings = new List<string>();
                try
                {
                    string? rmText = null;
                    try { rmText = await ReadMemoryAsync(store, "plan:roadmap", cancellationToken); }
                    catch (FileNotFoundException) { }

                    if (rmText is not null)
                    {
                        var phaseIds = ExtractPhaseIds(rmText);
                        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
                        var allTasks = store.LoadIndex(taskScope);
                        string? completeGoalId = await GetActiveGoalIdAsync(store, cancellationToken);

                        // Check each phase for: incomplete tasks, missing verification, missing retrospective
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
                                warnings.Add($"phase {pid} has no plan_verify record");

                            // Check for FAIL in verification results
                            if (hasVerify && logText!.Contains($"VERIFY phase {pid}: PARTIAL", StringComparison.OrdinalIgnoreCase))
                                warnings.Add($"phase {pid} verification had failures — check plan_verify results");
                            if (hasVerify && logText!.Contains($"VERIFY phase {pid}: ALL_FAIL", StringComparison.OrdinalIgnoreCase))
                                warnings.Add($"phase {pid} verification failed — all criteria unmet");

                            // Missing retrospective
                            bool hasRetro = retroText?.Contains($"Phase {pid} Retrospective", StringComparison.OrdinalIgnoreCase) == true;
                            if (!hasRetro)
                                warnings.Add($"phase {pid} has no plan_retrospective");
                        }
                    }
                }
                catch { /* workflow check is best-effort — never block goal completion */ }

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

                string response = $"Goal '{searchId}' marked complete. Outcome recorded. " +
                       $"project:context updated. Files in .scrinia/ were updated — these are your changes.";

                if (warnings.Count > 0)
                    response += "\n\nWorkflow steps you may have skipped:\n" +
                        string.Join("\n", warnings.Select(w => $"- {w}")) +
                        "\nConsider running plan_verify and plan_retrospective before moving on.";

                response += "\n\nPost-goal learning:\n" +
                    "- Distill valuable findings into topical memories (store) for future goals\n" +
                    "- Update or create skills (skill_create) with lessons learned\n" +
                    "Planning artifacts (task:*, plan:*, research:*) can be cleaned up — " +
                    "the learnings live in your memories and skills now.";

                return response;
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

                string response = Truncate(sb.ToString());

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
        "4. Store durable knowledge via store for reuse across sessions.\n\n" +
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
    [McpServerTool(Name = "skill_create"), Description(
        "Create a reusable specialist skill and store as skill:* memory. " +
        "Skills are methodology — how to approach a type of work, not what you know. " +
        "Keep skills lean: put facts in memories (store), put approach in skills. " +
        "A skill should say 'search for X' not list X inline. " +
        "Built-in scaffolds: researcher, reviewer, domain-expert, or custom.")]
    public async Task<string> SkillCreate(
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

        response = Truncate(response);

        return response;
    }

    /// <summary>List or load stored specialist skills.</summary>
    [McpServerTool(Name = "skill_load"), Description(
        "List or load stored specialist skills. " +
        "Call with no skillName to list available skills. " +
        "Call with a skillName to load the full prompt for activation. " +
        "Skills created by skill_create.")]
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
            foreach (string name in BuiltInSkills.Keys)
            {
                string tag = projectNames.Contains(name) ? "built-in, custom override" : "built-in";
                sb.AppendLine($"- skill:{name} [{tag}]");
            }

            // Project-only skills (not built-in)
            foreach (var entry in entries)
            {
                if (BuiltInSkills.ContainsKey(entry.Name))
                    continue; // already listed above

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
            listResponse = Truncate(listResponse);

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
            // Fall back to built-in skills
            if (BuiltInSkills.TryGetValue(skillName, out string? builtIn))
                return Truncate(builtIn);
            return $"Error: skill '{skillName}' not found. Use skill_load (no name) to list available skills.";
        }

        content = Truncate(content);

        return content;
    }

    private static readonly Dictionary<string, string> BuiltInSkills = new(StringComparer.OrdinalIgnoreCase)
    {
        ["march-reporter"] = """
            ## Role: Goal March Reporter
            You produce human-readable goal summary documents that report the march toward project
            objectives. These documents serve as audit trails for stakeholders and future agents.

            ## When to use
            After completing a goal (step 8), offer to produce a march report. Always produce one
            at milestone boundaries. Small goals can skip; significant goals need the paper trail.
            The agent should ask: "Want me to produce a march report for this goal?"

            ## Methodology
            1. `search("findings-registry")` — load the findings registry for sequential IDs
            2. `goal_update(action:"list")` — get all goals with outcomes for the reporting period
            3. `concern(statusFilter:"all")` — get all concerns (active + resolved)
            4. `search("applied-fixes")` — load fix summaries
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
            Pull from `audit:findings-registry`. Include ALL findings for this goal —
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
            1. `search("findings-registry")` — load existing findings to get next IDs and avoid duplicates
            2. `search("applied-fixes")` — know what's already been fixed
            3. `search("audit-false-positives")` — avoid known false positives
            4. Understand the project: `search("architecture")`, `search("patterns")`

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
            Assign sequential IDs from the findings registry. Never reuse numbers.
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
            - Register each finding with `concern_add`
            - Update `audit:findings-registry` with new entries
            - Present findings table to user with ID, severity, status, resolution
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
            - `search("bugs:")` — has this been investigated before?

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
            - `store(["Root cause: ...\nFix: ...\nPattern: ..."], "bugs:{area}-{slug}")`
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
            Use the findings registry pattern (SEC/QAL IDs) for tracking.
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
            - `store([overview], "arch:overview")` — high-level architecture
            - `store([patterns], "arch:patterns")` — conventions and patterns
            - `store([pitfalls], "arch:pitfalls")` — things that will trip you up
            - `store([testing], "testing:infrastructure")` — how to run and write tests

            The goal: a future agent starting a fresh session can `search("architecture")`
            and have enough context to start working without re-exploring the codebase.

            ## Key principle
            Write for the agent that comes after you. They have zero context. The walkthrough
            should answer every question they'd ask in their first 10 minutes.
            """,

        ["planner"] = """
            ## Role: Wave Execution Planner
            You decompose validated work into parallel execution waves with explicit agent specifications.
            You don't do the work — you plan how agents will do it.

            ## When to use
            After research produces a change manifest (files, functions, transformations) and tasks are
            defined, the planner produces an execution plan that maximizes parallelism.

            ## Methodology

            ### 1. Analyze the task set
            For each task, identify:
            - **Files touched**: which files will be created/modified
            - **Dependencies**: which tasks must complete before this one starts
            - **Agent type**: Explore (research), general-purpose (code changes), or specialist (loaded skill)
            - **Isolation needed**: does this task modify files that other tasks also modify?

            ### 2. Detect file conflicts
            Build a file → task mapping. If two tasks touch the same file:
            - They CANNOT run in parallel (unless using worktree isolation)
            - Group them into the same agent, OR sequence them in different waves
            - Worktree isolation allows parallel execution but requires merge afterward

            ### 3. Produce the execution plan
            For each wave, specify:
            ```
            Wave N:
            - Agent 1 [type: general-purpose, isolation: worktree]
              Files: src/Server/Program.cs
              Task: {exact change description with file:line, transformation}
            - Agent 2 [type: general-purpose]
              Files: src/Core/FileMemoryStore.cs
              Task: {exact change description}
            - Agent 3 [type: Explore]
              Task: {research question}
            Merge: build + test after wave completes
            ```

            ### 4. Handle SOS signals
            If an agent returns an SOS (needs specialist, needs skill, needs decomposition):
            - Assess the SOS request
            - If skill needed: create it via `skill_create`, spawn specialist in next wave
            - If decomposition needed: split the task, add sub-tasks to current or next wave
            - If specialist needed: `skill_load` the relevant skill, spawn with its methodology
            - Update the execution plan and continue

            ### 5. Convergence
            After all waves complete:
            - Build the full project
            - Run all tests
            - Verify each task's acceptance criteria
            - Report: which tasks completed, which SOS'd, what was replanned

            ## Key rules
            - **Different files = parallel agents.** Always. Not a judgment call.
            - **Same file = same agent or sequential waves.** Worktree if urgent.
            - **Research = Explore agent.** Code changes = general-purpose agent.
            - **Every agent gets the exact change spec.** No agent should need to explore.
            - **Build + test between waves.** Never start wave N+1 on a broken build.
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
            - Check available skills: `skill_load()` — is there already a specialist?
            - If yes: spawn a new agent with `skill_load("{specialist}")` as its prompt
            - If no: assess whether to create a new skill or handle inline
            - Feed the SOS context to the specialist as its starting point

            ### Type: needs-skill
            The agent identified a recurring pattern that should be a reusable skill.
            - Review the agent's context: what methodology would help?
            - `skill_create` the new skill with the methodology
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
    };

    private sealed record ParsedTask(string Id, string[] DependsOn, string Content);

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

            // Build content: Action + Acceptance criteria (everything except Depends on line)
            var contentLines = section.Split('\n')
                .Where(line => !Regex.IsMatch(line.Trim(), @"^Depends\s+on:", RegexOptions.IgnoreCase))
                .ToList();

            // Trim leading/trailing blank lines from content
            while (contentLines.Count > 0 && string.IsNullOrWhiteSpace(contentLines[0]))
                contentLines.RemoveAt(0);
            while (contentLines.Count > 0 && string.IsNullOrWhiteSpace(contentLines[^1]))
                contentLines.RemoveAt(contentLines.Count - 1);

            string content = string.Join('\n', contentLines).Trim();
            if (string.IsNullOrWhiteSpace(content))
                content = "(no action specified)";

            result.Add(new ParsedTask(taskId, dependsOn, content));
        }

        return result;
    }
}
