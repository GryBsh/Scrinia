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
    // ── Internals ────────────────────────────────────────────────────────────

    /// <summary>Initialize a project by storing goals, context, and constraints.</summary>
    internal static async Task<string> ProjectInit(
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

        // Create meta-entity directories and seed built-in agent files
        string initBaseDir = GetScriniaBaseDir(store);
        Directory.CreateDirectory(Path.Combine(initBaseDir, "workflows"));
        Directory.CreateDirectory(Path.Combine(initBaseDir, "skills"));
        string agentSeedDir = Path.Combine(initBaseDir, "agent");
        Directory.CreateDirectory(agentSeedDir);
        SeedBuiltInAgentFiles(agentSeedDir);

        string responseContent = $"Initialized project '{projectId}'. Stored: project:context, project:state. " +
               $"Created workflows/, skills/, agent/ directories.\n" +
               "Merge infrastructure created in .scrinia/hooks/. " +
               "Configure the merge driver: git config merge.scrinia-meta.driver " +
               "'.scrinia/hooks/scrinia-merge-meta.sh %O %A %B'";

        if (hasExistingCode)
            responseContent += "\n\nExisting codebase detected. Onboarder task created.";

        string instruction = hasExistingCode
            ? "call task('next', { path: '/goal/G-X' }) to start the onboarder. After onboarding completes, suggest memory('remember', { path: '/goal/...' }) to the user to set a goal."
            : "ask the user what to work on, then call memory('remember', { path: '/goal/...' }) to set a goal.";

        return ResponseBuilder.Success(responseContent)
            .WithFileChanges()
            .WithPath($"/project/{projectId}")
            .WithAction("created")
            .WithInstruction(instruction)
            .ToYaml();
    }

    /// <summary>Store project requirements with category grouping and REQ-IDs.</summary>
    internal static async Task<string> PlanRequirements(
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

        return ResponseBuilder.Success("Stored: project:requirements.")
            .WithFileChanges()
            .WithPath("/project/requirements")
            .WithAction("created")
            .WithInstruction("review these requirements with the user:\n- Are all requirements captured? Anything missing?\n- Are the REQ-IDs scoped correctly (too broad? too narrow?)?\n- Are priorities clear — what's essential vs. nice-to-have?\nOnce confirmed, call memory('remember', { path: '/goal/...' }) to start execution.")
            .ToYaml();
    }

    /// <summary>Resolve a requirement by marking it fulfilled in project:requirements.</summary>
    internal static async Task<string> RequirementResolve(
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
                .WithPath($"/requirement/{id}")
                .WithAction("resolved")
                .ToYaml();
        }
        catch (FileNotFoundException)
        {
            return ResponseBuilder.Error("No requirements found. Call memory('remember', { path: '/requirement/...' }) first.").ToYaml();
        }
    }

    /// <summary>List all requirements from project:requirements.</summary>
    internal static async Task<string> RequirementList(CancellationToken cancellationToken = default)
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

    /// <summary>Resume full agent context after context loss or session start. Delegates to ScriniaMcpTools.Restore.</summary>
    internal static Task<string> ContextResume(CancellationToken cancellationToken = default)
        => new ScriniaMcpTools().Restore(cancellationToken);

    /// <summary>Query current project status.</summary>
    internal static async Task<string> PlanStatus(CancellationToken cancellationToken = default)
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
            .WithPath("/project/state")
            .WithAction("shown");
        if (idleInstruction is not null)
            psResponse = psResponse.WithInstruction(idleInstruction);
        if (psWarnings.Count > 0)
            psResponse = psResponse.WithActionNeeded([.. psWarnings]);
        if (psInfoItems.Count > 0)
            psResponse = psResponse.WithInfo([.. psInfoItems]);

        return psResponse.ToYaml();
    }

    /// <summary>Store a structured phase retrospective in learn:retro-gN-phaseId.</summary>
    internal static async Task<string> PlanRetrospective(
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
                        skillNudge = $"\n\u2139 Existing skills to consider updating: {string.Join(", ", names)}";
                    }
                }
                catch { /* skill scope not yet created — skip silently */ }

                if (allPhasesDone)
                    retroNextStep = "\nAll phases complete. \u2192 INSTRUCTION: complete the following before calling memory('transition', { path: '/goal/G-X', to: 'complete' }):\n" +
                        "0. Spawn QA agent: memory('recall', { path: '/skill/qa' }) \u2192 verify tests pass, build clean, criteria met\n" +
                        "1. Spawn march reporter: memory('recall', { path: '/skill/march-reporter' }) \u2192 docs/reports/ + sessions:YYYY-MM-DD memory\n" +
                        "2. Distill valuable learnings into topical memories (remember) so future goals start smarter\n" +
                        "3. Update existing skills or create new ones (memory('remember', { path: '/skill/...' })) with lessons from this goal" +
                        skillNudge + "\n" +
                        "4. Then call memory('transition', { path: '/goal/G-X', to: 'complete' })";
                else if (nextPhase is not null)
                    retroNextStep = $"\n\u2192 INSTRUCTION: investigate phase {nextPhase} — explore the codebase, store research findings, then plan tasks." +
                        skillNudge +
                        "\n\u2139 if this conversation is getting long, checkpoint your state: memory('remember', { path: '/checkpoint/latest', content: [\"current context...\"] })";
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
    internal static async Task<string> AgentProfile(
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
            .WithPath("/agent/profile")
            .WithAction("created")
            .ToYaml();
    }

    /// <summary>Seeds built-in agent markdown files to the agent directory if they don't already exist on disk.</summary>
    internal static void SeedBuiltInAgentFiles(string agentDir)
    {
        foreach (var (name, content) in EmbeddedPrompts.LoadAllAgentFiles())
        {
            string filePath = Path.Combine(agentDir, $"{name}.md");
            if (!File.Exists(filePath))
                File.WriteAllText(filePath, content);
        }
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
    private static string FileShow(string? id)
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

        return ResponseBuilder.Success(sb.ToString().TrimEnd()).WithPath($"/file/{normalizedId}").WithAction("shown").ToYaml();
    }

    /// <summary>List all files tracked via codeRefs across all memories (file entity view).</summary>
    private static string FileList(string? query)
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
