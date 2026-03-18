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
}
