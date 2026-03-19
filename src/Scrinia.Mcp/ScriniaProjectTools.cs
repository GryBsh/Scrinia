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

// ── Source-gen JSON context (trimming-safe) ──────────────────────────────────

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProjectRecord))]
[JsonSerializable(typeof(PlanRecord))]
[JsonSerializable(typeof(TaskRecord))]
[JsonSerializable(typeof(ProjectRecord[]))]
[JsonSerializable(typeof(PlanRecord[]))]
[JsonSerializable(typeof(TaskRecord[]))]
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

        return $"Stored: plan:roadmap. Files in .scrinia/ were updated — these are your changes.{extraNote}";
    }

    /// <summary>Decompose a phase into task memories with keyword-based metadata.</summary>
    [McpServerTool(Name = "plan_tasks"), Description(
        "Decompose a phase into task memories with keyword-based metadata for status, wave, phase, and dependencies. " +
        "Research the domain before calling this tool — study existing code, APIs, and patterns so tasks accurately " +
        "reflect the work needed. Each task is stored as task:{phaseId}-{wave}-{id} with keywords: " +
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

        string taskList = string.Join("\n", createdNames.Select(n => $"  - {n}"));
        string response =
            $"Created {parsedTasks.Count} task(s) for phase {phaseId} in {waveCount} wave(s).\n" +
            $"Tasks stored:\n{taskList}\n" +
            $"Files in .scrinia/ were updated — these are your changes.\n" +
            $"Next: run task_next to get the first pending task.";

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

        string response =
            $"Project: {projectName}\n" +
            $"Phase: {phase}\n" +
            $"Progress: {progress}\n" +
            $"Last action: {lastAction}\n" +
            $"Blockers: {blockers}\n" +
            $"Next: {next}" +
            roadmapNote;

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
    private static async Task AppendToExecutionLogAsync(
        IMemoryStore store, string logName, string outcomeText, CancellationToken ct)
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
