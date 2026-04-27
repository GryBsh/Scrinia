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
    // -- Dynamic goal management (GOAL-01, GOAL-02, GOAL-04) ---------------------


    /// <summary>Manage project goals dynamically.</summary>
    internal static async Task<string> GoalUpdate(
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
                                    $"- /backlog/{x.Entry.Name}: {x.Entry.Description ?? "(no description)"}")) +
                                "\n\n";
                        }
                    }
                }
                catch { /* backlog topic may not exist */ }

                var seedNames = string.Join(", ", workflow.SeedActivities.OrderBy(a => a.Wave ?? 0).Select(a => a.Id));
                string goalContent = $"Goal added as {newGoalId}: {description}.\n" +
                       $"/project/context updated.\n\n" +
                       backlogSection +
                       $"Seed tasks created ({seedNames}).";
                return ResponseBuilder.Success(goalContent)
                    .WithFileChanges()
                    .WithPath($"/goal/{newGoalId}")
                    .WithAction("created")
                    .WithInstruction($"Confirm this goal with the user. Once confirmed, call task('next', {{ path: '/goal/{newGoalId}' }}) to begin.")
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
                       $"/project/context updated.\n\n" +
                       "Post-goal learning:\n" +
                       "- Run QA if not already done: memory('recall', { path: '/skill/qa' }) for structured verification\n" +
                       "- Produce a march report: memory('recall', { path: '/skill/march-reporter' }) -> write to docs/reports/ and update sessions:YYYY-MM-DD memory\n" +
                       "- Distill valuable findings into topical memories (remember) for future goals\n" +
                       "- Update or create skills (memory('remember', { path: '/skill/...' })) with lessons learned\n" +
                       "Planning artifacts (task:*, plan:*, research:*) can be cleaned up — the learnings live in your memories and skills now.";

                var gcResponse = ResponseBuilder.Success(gcContent)
                    .WithFileChanges()
                    .WithPath($"/goal/{searchId}")
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
                string editSuffix = outcomeSep >= 0 ? afterStatus[outcomeSep..] : "";

                // Rebuild goal line with new description
                string prefix = trimmed[..descStart];
                goals[matchIndex] = $"- {prefix}{description}{editSuffix}";

                string goalsSection = BuildGoalsSection(goals, originalCount >= 0 ? originalCount : goals.Count);
                string updatedContext = contextWithoutGoals.TrimEnd() + "\n\n" + goalsSection;
                await WritePlanningMemoryAsync(store, "project:context", updatedContext,
                    archiveExisting: true, cancellationToken);

                return ResponseBuilder.Success($"Goal '{searchId}' updated.\nOld: {oldDesc.Trim()}\nNew: {description}\n/project/context updated.")
                    .WithFileChanges()
                    .WithPath($"/goal/{searchId}")
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
}
